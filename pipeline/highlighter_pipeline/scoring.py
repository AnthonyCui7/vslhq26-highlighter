"""Parallel clip scoring: a bounded LLM worker pool feeding an ordered emitter.

Capture (the main thread) stores transcripts and calls add_chunk(); chunk i is
dispatched to the pool only once chunk i+1's transcript exists, so the model
sees up to context_seconds of words across both of its boundaries. A single
emitter thread consumes scored chunks strictly in chunk order, stitches
candidates across chunk boundaries, and runs every downstream effect
(threshold gate, render, DB writes, segment cleanup) so clips keep appearing
live and in order while scoring runs concurrently.

Candidates whose windows overlap — including across a chunk boundary, which
both neighboring chunks can see thanks to the context margins — are merged
into one clip spanning their union. A candidate ending within context_seconds
of its chunk's end is held back one chunk so a continuation proposed by the
next chunk can be merged before anything is rendered.
"""

import queue
import threading
import time
import traceback
from concurrent.futures import Future, ThreadPoolExecutor
from typing import Any, Callable


def merge_candidates(
    candidates: list[dict[str, Any]],
    *,
    merge_gap_seconds: float,
    max_clip_seconds: float,
) -> list[dict[str, Any]]:
    """Union candidates whose windows overlap or abut within merge_gap_seconds.

    Returns a new list sorted by start time. A union that would exceed
    max_clip_seconds is left unmerged so runaway chains cannot snowball into
    one enormous clip."""
    ordered = sorted(
        candidates, key=lambda c: (c["start_seconds"], c["end_seconds"])
    )
    merged: list[dict[str, Any]] = []
    for candidate in ordered:
        if merged:
            last = merged[-1]
            touches = (
                candidate["start_seconds"]
                <= last["end_seconds"] + merge_gap_seconds
            )
            union_length = (
                max(last["end_seconds"], candidate["end_seconds"])
                - last["start_seconds"]
            )
            if touches and union_length <= max_clip_seconds:
                merged[-1] = _merge_pair(last, candidate)
                continue
            if touches:
                print(
                    "Not merging overlapping clip candidates at "
                    f"{last['start_seconds']}s-{last['end_seconds']}s and "
                    f"{candidate['start_seconds']}s-{candidate['end_seconds']}s: "
                    f"union would exceed {max_clip_seconds}s"
                )
        merged.append(candidate)
    return merged


def _merge_pair(a: dict[str, Any], b: dict[str, Any]) -> dict[str, Any]:
    """Merge two overlapping candidates: union window, best constituent wins
    the editorial fields (title/description/reason/score/chunk_index)."""
    primary = b if b["score"] > a["score"] else a
    sources: list[Any] = []
    seen_urls: set[Any] = set()
    for source in [*a.get("research_sources", []), *b.get("research_sources", [])]:
        url = source.get("url") if isinstance(source, dict) else source
        if url in seen_urls:
            continue
        seen_urls.add(url)
        sources.append(source)

    merged = {**primary}
    merged["start_seconds"] = min(a["start_seconds"], b["start_seconds"])
    merged["end_seconds"] = max(a["end_seconds"], b["end_seconds"])
    merged["score"] = max(a["score"], b["score"])
    merged["research_sources"] = sources
    merged["merged_from"] = [*_constituents(a), *_constituents(b)]
    return merged


def _constituents(decision: dict[str, Any]) -> list[dict[str, Any]]:
    if decision.get("merged_from"):
        return decision["merged_from"]
    return [
        {
            "chunk_index": decision["chunk_index"],
            "start_seconds": decision["start_seconds"],
            "end_seconds": decision["end_seconds"],
            "score": decision["score"],
            "title": decision["title"],
        }
    ]


def no_clip_record(chunk_index: int, reason: str) -> dict[str, Any]:
    """A full-shape non-worthy decision for chunks that produced no candidates."""
    return {
        "chunk_index": chunk_index,
        "is_clip_worthy": False,
        "title": None,
        "description": None,
        "start_seconds": None,
        "end_seconds": None,
        "score": 0.0,
        "reason": reason or "No clip-worthy moment in this chunk.",
        "research_sources": [],
    }


class ClipScoringCoordinator:
    """Dispatches chunk scoring to a thread pool and emits decisions in order.

    All callbacks passed in run on the single emitter thread except
    score_chunk, which runs on pool threads and must be thread-safe:

    - score_chunk(chunk, context_words_before, context_words_after,
      visible_start_seconds, visible_end_seconds) -> (candidates, assessment).
      Failures are retried once, then downgraded to an empty result — one bad
      chunk must never kill the run.
    - emit_decision(decision): every stitched worthy candidate and every
      no-clip chunk record, in chunk order.
    - on_chunk_complete(chunk_index, min_held_start_seconds): after each
      chunk's decisions were emitted; min_held_start_seconds is the earliest
      start among still-held candidates (None when nothing is held) so the
      caller can retire archive segments nothing can reference anymore.
    - on_finish(): once, after the final flush, before the emitter exits.

    add_chunk()/finish() must be called from a single thread, with contiguous
    chunk indexes starting at 0. run_on_emitter() is thread-safe.
    """

    def __init__(
        self,
        *,
        score_chunk: Callable[..., tuple[list[dict[str, Any]], str]],
        emit_decision: Callable[[dict[str, Any]], None],
        chunk_seconds: int,
        context_seconds: int,
        concurrency: int,
        merge_gap_seconds: float,
        max_clip_seconds: float,
        on_chunk_complete: Callable[[int, float | None], None] | None = None,
        on_finish: Callable[[], None] | None = None,
    ) -> None:
        if context_seconds < 0:
            raise RuntimeError("context_seconds must be 0 or greater")
        if chunk_seconds <= 2 * context_seconds:
            # The stitcher only looks one chunk back; wider context could
            # produce candidates that should merge across non-adjacent chunks.
            raise RuntimeError(
                "chunk_seconds must be more than twice context_seconds"
            )
        self._score_chunk = score_chunk
        self._emit_decision = emit_decision
        self._chunk_seconds = chunk_seconds
        self._context_seconds = context_seconds
        self._merge_gap_seconds = merge_gap_seconds
        self._max_clip_seconds = max_clip_seconds
        self._on_chunk_complete = on_chunk_complete
        self._on_finish = on_finish

        self._executor = ThreadPoolExecutor(
            max_workers=max(1, concurrency), thread_name_prefix="clip-scoring"
        )
        self._queue: queue.Queue = queue.Queue()
        self._chunks: dict[int, dict[str, Any]] = {}
        self._chunk_bounds: dict[int, tuple[int, int]] = {}
        self._futures: dict[int, Future] = {}
        self._last_added: int | None = None
        self._finished = False
        self._held: list[dict[str, Any]] = []
        self._emitter = threading.Thread(
            target=self._run_emitter, name="clip-emitter", daemon=True
        )
        self._emitter.start()

    def add_chunk(self, chunk: dict[str, Any]) -> None:
        """Register a transcribed chunk ({chunk_index, start_seconds,
        end_seconds, transcript, words}) and dispatch its predecessor, which
        now has right-hand context available."""
        index = chunk["chunk_index"]
        self._chunks[index] = chunk
        self._chunk_bounds[index] = (chunk["start_seconds"], chunk["end_seconds"])
        self._last_added = index
        if index > 0 and index - 1 in self._chunks and index - 1 not in self._futures:
            self._dispatch(index - 1, has_next=True)
            # The tail of chunk index-2 was only needed as left context for
            # the dispatch of index-1; it can go now.
            self._chunks.pop(index - 2, None)

    def run_on_emitter(self, fn: Callable[[], None]) -> None:
        """Run fn on the emitter thread, ordered with decision emission."""
        self._queue.put(("call", fn))

    def finish(self, *, cancelled: bool = False) -> None:
        """Dispatch the final chunk (unless cancelled), drain everything, and
        stop the emitter. Safe to call twice; the second call is a no-op."""
        if self._finished:
            return
        self._finished = True

        if (
            not cancelled
            and self._last_added is not None
            and self._last_added not in self._futures
        ):
            self._dispatch(self._last_added, has_next=False)

        # Waits for running scoring calls either way; cancel_futures drops
        # queued ones so a cancel is bounded by the in-flight calls only.
        self._executor.shutdown(wait=True, cancel_futures=cancelled)
        for index, future in self._futures.items():
            if future.cancelled():
                self._queue.put(("scored", index, [], "Cancelled before scoring."))
        self._queue.put(("finish",))
        self._emitter.join()

    def _dispatch(self, index: int, *, has_next: bool) -> None:
        chunk = self._chunks[index]
        previous = self._chunks.get(index - 1)
        next_chunk = self._chunks.get(index + 1) if has_next else None
        visible_start = (
            chunk["start_seconds"] - self._context_seconds
            if previous is not None
            else chunk["start_seconds"]
        )
        visible_end = (
            chunk["end_seconds"] + self._context_seconds
            if next_chunk is not None
            else chunk["end_seconds"]
        )
        context_before = (
            [
                word
                for word in previous["words"]
                if float(word.get("absolute_start", 0)) >= visible_start
            ]
            if previous is not None
            else []
        )
        context_after = (
            [
                word
                for word in next_chunk["words"]
                if float(word.get("absolute_start", 0)) < visible_end
            ]
            if next_chunk is not None
            else []
        )
        score_chunk = dict(chunk)
        audio_context = [
            {
                "path": context_chunk["audio_path"],
                "start_seconds": context_chunk["start_seconds"],
                "end_seconds": context_chunk["end_seconds"],
            }
            for context_chunk in (previous, chunk, next_chunk)
            if context_chunk is not None and context_chunk.get("audio_path")
        ]
        if audio_context:
            score_chunk["audio_context"] = audio_context

        self._futures[index] = self._executor.submit(
            self._score_and_post,
            score_chunk,
            context_before,
            context_after,
            visible_start,
            visible_end,
        )

    def _score_and_post(
        self,
        chunk: dict[str, Any],
        context_before: list[dict[str, Any]],
        context_after: list[dict[str, Any]],
        visible_start: int,
        visible_end: int,
    ) -> None:
        index = chunk["chunk_index"]
        try:
            try:
                candidates, assessment = self._score_chunk(
                    chunk, context_before, context_after, visible_start, visible_end
                )
            except Exception as exc:
                print(f"Scoring chunk {index} failed ({exc}); retrying once")
                time.sleep(2)
                candidates, assessment = self._score_chunk(
                    chunk, context_before, context_after, visible_start, visible_end
                )
        except Exception as exc:
            print(f"Scoring chunk {index} failed after retry: {exc}")
            candidates, assessment = [], f"LLM scoring failed: {exc}"
        self._queue.put(("scored", index, candidates, assessment))

    def _run_emitter(self) -> None:
        buffered: dict[int, tuple[list[dict[str, Any]], str]] = {}
        next_index = 0
        while True:
            event = self._queue.get()
            kind = event[0]
            if kind == "scored":
                buffered[event[1]] = (event[2], event[3])
            elif kind == "call":
                self._safe(event[1])
            while next_index in buffered:
                candidates, assessment = buffered.pop(next_index)
                self._process_chunk_result(next_index, candidates, assessment)
                next_index += 1
            if kind == "finish":
                if buffered:
                    print(
                        "Warning: scored chunks were left unemitted "
                        f"(missing an earlier chunk): {sorted(buffered)}"
                    )
                break
        for decision in self._held:
            self._safe(lambda d=decision: self._emit_decision(d))
        self._held = []
        if self._on_finish is not None:
            self._safe(self._on_finish)

    def _process_chunk_result(
        self,
        index: int,
        candidates: list[dict[str, Any]],
        assessment: str,
    ) -> None:
        _, chunk_end = self._chunk_bounds[index]
        if not candidates:
            self._safe(
                lambda: self._emit_decision(no_clip_record(index, assessment))
            )

        merged = merge_candidates(
            [*self._held, *candidates],
            merge_gap_seconds=self._merge_gap_seconds,
            max_clip_seconds=self._max_clip_seconds,
        )
        # Anything ending near this chunk's boundary could still merge with a
        # continuation the next chunk proposes; hold it one chunk.
        hold_after = chunk_end - self._context_seconds
        self._held = [c for c in merged if c["end_seconds"] > hold_after]
        for decision in merged:
            if decision["end_seconds"] <= hold_after:
                self._safe(lambda d=decision: self._emit_decision(d))

        if self._on_chunk_complete is not None:
            min_held_start = min(
                (c["start_seconds"] for c in self._held), default=None
            )
            self._safe(lambda: self._on_chunk_complete(index, min_held_start))

    def _safe(self, fn: Callable[[], None]) -> None:
        try:
            fn()
        except Exception:
            print("Clip emitter callback failed:")
            traceback.print_exc()
