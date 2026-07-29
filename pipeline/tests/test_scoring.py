import threading
import time

import pytest

from highlighter_pipeline.scoring import (
    ClipScoringCoordinator,
    merge_candidates,
    no_clip_record,
)


def candidate(
    chunk_index,
    start,
    end,
    score=0.5,
    title=None,
    research_sources=None,
    **extra,
):
    return {
        "chunk_index": chunk_index,
        "is_clip_worthy": True,
        "title": title or f"clip {chunk_index}",
        "description": "d",
        "start_seconds": start,
        "end_seconds": end,
        "score": score,
        "reason": "r",
        "research_sources": research_sources or [],
        "model": "test-model",
        "reasoning_effort": "low",
        "raw_decision": {},
        **extra,
    }


class TestMergeCandidates:
    def test_overlapping_candidates_union(self):
        merged = merge_candidates(
            [candidate(0, 10, 30, score=0.4), candidate(1, 25, 45, score=0.7)],
            merge_gap_seconds=1.0,
            max_clip_seconds=120,
        )
        assert len(merged) == 1
        assert merged[0]["start_seconds"] == 10
        assert merged[0]["end_seconds"] == 45
        # The higher-scoring constituent wins the editorial fields.
        assert merged[0]["score"] == 0.7
        assert merged[0]["title"] == "clip 1"
        assert merged[0]["chunk_index"] == 1
        assert [c["chunk_index"] for c in merged[0]["merged_from"]] == [0, 1]

    def test_abutting_within_gap_merges(self):
        merged = merge_candidates(
            [candidate(0, 10, 30), candidate(1, 30.8, 40)],
            merge_gap_seconds=1.0,
            max_clip_seconds=120,
        )
        assert len(merged) == 1
        assert merged[0]["end_seconds"] == 40

    def test_beyond_gap_stays_separate(self):
        merged = merge_candidates(
            [candidate(0, 10, 30), candidate(1, 31.5, 40)],
            merge_gap_seconds=1.0,
            max_clip_seconds=120,
        )
        assert len(merged) == 2
        assert "merged_from" not in merged[0]

    def test_max_length_cap_prevents_merge(self):
        merged = merge_candidates(
            [candidate(0, 0, 80), candidate(1, 79, 130)],
            merge_gap_seconds=1.0,
            max_clip_seconds=120,
        )
        assert len(merged) == 2

    def test_three_way_chain_merges(self):
        merged = merge_candidates(
            [candidate(0, 0, 10), candidate(0, 9, 20), candidate(1, 19, 30)],
            merge_gap_seconds=1.0,
            max_clip_seconds=120,
        )
        assert len(merged) == 1
        assert merged[0]["start_seconds"] == 0
        assert merged[0]["end_seconds"] == 30
        assert len(merged[0]["merged_from"]) == 3

    def test_research_sources_union_deduped_by_url(self):
        a = candidate(
            0, 10, 30, research_sources=[{"title": "t", "url": "u1", "claim": "c"}]
        )
        b = candidate(
            1,
            25,
            45,
            research_sources=[
                {"title": "t2", "url": "u1", "claim": "c2"},
                {"title": "t3", "url": "u2", "claim": "c3"},
            ],
        )
        merged = merge_candidates(
            [a, b], merge_gap_seconds=1.0, max_clip_seconds=120
        )
        assert [s["url"] for s in merged[0]["research_sources"]] == ["u1", "u2"]

    def test_unsorted_input_is_sorted_first(self):
        merged = merge_candidates(
            [candidate(1, 40, 50), candidate(0, 10, 20)],
            merge_gap_seconds=1.0,
            max_clip_seconds=120,
        )
        assert [c["start_seconds"] for c in merged] == [10, 40]


def make_chunk(index, chunk_seconds=90, words_per_second=1):
    start = index * chunk_seconds
    end = (index + 1) * chunk_seconds
    words = [
        {
            "word": f"w{index}_{i}",
            "absolute_start": float(start + i),
            "absolute_end": float(start + i) + 0.5,
        }
        for i in range(0, chunk_seconds, words_per_second)
    ]
    return {
        "chunk_index": index,
        "start_seconds": start,
        "end_seconds": end,
        "transcript": f"chunk {index}",
        "words": words,
    }


class TestCoordinator:
    def test_rejects_wide_context(self):
        with pytest.raises(RuntimeError):
            ClipScoringCoordinator(
                score_chunk=lambda *a: ([], ""),
                emit_decision=lambda d: None,
                chunk_seconds=15,
                context_seconds=10,
                concurrency=2,
                merge_gap_seconds=1.0,
                max_clip_seconds=120,
            )

    def test_out_of_order_completion_emits_in_chunk_order(self):
        emitted = []
        # Chunk 0 is slow, later chunks fast: completion order is scrambled
        # but emission must stay 0, 1, 2, 3.
        delays = {0: 0.3, 1: 0.0, 2: 0.1, 3: 0.0}

        def score(chunk, before, after, vs, ve):
            index = chunk["chunk_index"]
            time.sleep(delays[index])
            return [
                candidate(index, chunk["start_seconds"] + 5, chunk["start_seconds"] + 15)
            ], "ok"

        coordinator = ClipScoringCoordinator(
            score_chunk=score,
            emit_decision=lambda d: emitted.append(d),
            chunk_seconds=90,
            context_seconds=10,
            concurrency=4,
            merge_gap_seconds=1.0,
            max_clip_seconds=120,
        )
        for i in range(4):
            coordinator.add_chunk(make_chunk(i))
        coordinator.finish()
        assert [d["chunk_index"] for d in emitted] == [0, 1, 2, 3]
        assert all(d["is_clip_worthy"] for d in emitted)

    def test_context_words_and_visible_window(self):
        seen = {}

        def score(chunk, before, after, vs, ve):
            seen[chunk["chunk_index"]] = (before, after, vs, ve)
            return [], "nothing"

        coordinator = ClipScoringCoordinator(
            score_chunk=score,
            emit_decision=lambda d: None,
            chunk_seconds=90,
            context_seconds=10,
            concurrency=2,
            merge_gap_seconds=1.0,
            max_clip_seconds=120,
        )
        for i in range(3):
            coordinator.add_chunk(make_chunk(i))
        coordinator.finish()

        before0, after0, vs0, ve0 = seen[0]
        assert (vs0, ve0) == (0, 100)  # first chunk: no left context
        assert before0 == []
        assert after0 and all(90 <= w["absolute_start"] < 100 for w in after0)

        before1, after1, vs1, ve1 = seen[1]
        assert (vs1, ve1) == (80, 190)
        assert before1 and all(80 <= w["absolute_start"] < 90 for w in before1)
        assert after1 and all(180 <= w["absolute_start"] < 190 for w in after1)

        before2, after2, vs2, ve2 = seen[2]
        assert (vs2, ve2) == (170, 270)  # last chunk: no right context
        assert after2 == []

    def test_boundary_candidates_from_adjacent_chunks_are_stitched(self):
        emitted = []

        def score(chunk, before, after, vs, ve):
            index = chunk["chunk_index"]
            if index == 0:
                # Ends right at the boundary (within the hold-back margin).
                return [candidate(0, 70, 95, score=0.4)], "ok"
            if index == 1:
                # Continuation proposed by the next chunk.
                return [candidate(1, 94, 110, score=0.9, title="the payoff")], "ok"
            return [], "nothing"

        coordinator = ClipScoringCoordinator(
            score_chunk=score,
            emit_decision=lambda d: emitted.append(d),
            chunk_seconds=90,
            context_seconds=10,
            concurrency=2,
            merge_gap_seconds=1.0,
            max_clip_seconds=120,
        )
        for i in range(3):
            coordinator.add_chunk(make_chunk(i))
        coordinator.finish()

        worthy = [d for d in emitted if d["is_clip_worthy"]]
        assert len(worthy) == 1
        assert worthy[0]["start_seconds"] == 70
        assert worthy[0]["end_seconds"] == 110
        assert worthy[0]["title"] == "the payoff"
        assert [c["chunk_index"] for c in worthy[0]["merged_from"]] == [0, 1]
        # The no-clip chunk still produced a record.
        assert any(not d["is_clip_worthy"] for d in emitted)

    def test_held_candidate_flushes_at_finish(self):
        emitted = []

        def score(chunk, before, after, vs, ve):
            if chunk["chunk_index"] == 1:
                # Last scored chunk, candidate in the hold-back margin.
                return [candidate(1, 160, 185)], "ok"
            return [], "nothing"

        coordinator = ClipScoringCoordinator(
            score_chunk=score,
            emit_decision=lambda d: emitted.append(d),
            chunk_seconds=90,
            context_seconds=10,
            concurrency=2,
            merge_gap_seconds=1.0,
            max_clip_seconds=120,
        )
        coordinator.add_chunk(make_chunk(0))
        coordinator.add_chunk(make_chunk(1))
        coordinator.finish()
        worthy = [d for d in emitted if d["is_clip_worthy"]]
        assert len(worthy) == 1
        assert worthy[0]["end_seconds"] == 185

    def test_scoring_failure_retries_then_degrades(self):
        emitted = []
        attempts = []

        def score(chunk, before, after, vs, ve):
            attempts.append(chunk["chunk_index"])
            raise RuntimeError("boom")

        coordinator = ClipScoringCoordinator(
            score_chunk=score,
            emit_decision=lambda d: emitted.append(d),
            chunk_seconds=90,
            context_seconds=10,
            concurrency=2,
            merge_gap_seconds=1.0,
            max_clip_seconds=120,
        )
        coordinator.add_chunk(make_chunk(0))
        coordinator.finish()
        assert attempts == [0, 0]  # one retry
        assert len(emitted) == 1
        assert not emitted[0]["is_clip_worthy"]
        assert "LLM scoring failed" in emitted[0]["reason"]

    def test_cancelled_finish_does_not_hang_and_skips_undispatched(self):
        emitted = []
        started = threading.Event()

        def score(chunk, before, after, vs, ve):
            started.set()
            time.sleep(0.2)
            return [], "slow"

        coordinator = ClipScoringCoordinator(
            score_chunk=score,
            emit_decision=lambda d: emitted.append(d),
            chunk_seconds=90,
            context_seconds=10,
            concurrency=1,  # chunk 1 queues behind chunk 0
            merge_gap_seconds=1.0,
            max_clip_seconds=120,
        )
        for i in range(4):
            coordinator.add_chunk(make_chunk(i))
        started.wait(timeout=5)
        deadline = time.monotonic()
        coordinator.finish(cancelled=True)
        assert time.monotonic() - deadline < 5
        # Chunk 3 was never dispatched (no next chunk arrived, cancelled), and
        # every dispatched chunk produced exactly one record.
        assert {d["chunk_index"] for d in emitted}.issubset({0, 1, 2})
        assert all(not d["is_clip_worthy"] for d in emitted)

    def test_emit_exception_does_not_stop_later_chunks(self):
        emitted = []

        def emit(decision):
            if decision["chunk_index"] == 0:
                raise RuntimeError("render exploded")
            emitted.append(decision)

        coordinator = ClipScoringCoordinator(
            score_chunk=lambda chunk, *a: (
                [candidate(chunk["chunk_index"], chunk["start_seconds"] + 5, chunk["start_seconds"] + 15)],
                "ok",
            ),
            emit_decision=emit,
            chunk_seconds=90,
            context_seconds=10,
            concurrency=2,
            merge_gap_seconds=1.0,
            max_clip_seconds=120,
        )
        for i in range(2):
            coordinator.add_chunk(make_chunk(i))
        coordinator.finish()
        assert [d["chunk_index"] for d in emitted] == [1]

    def test_chunk_complete_reports_held_state_and_finish_callback_runs(self):
        completions = []
        finished = []

        def score(chunk, before, after, vs, ve):
            if chunk["chunk_index"] == 0:
                return [candidate(0, 70, 95)], "ok"  # held (within 10s of 90)
            return [], "nothing"

        coordinator = ClipScoringCoordinator(
            score_chunk=score,
            emit_decision=lambda d: None,
            chunk_seconds=90,
            context_seconds=10,
            concurrency=2,
            merge_gap_seconds=1.0,
            max_clip_seconds=120,
            on_chunk_complete=lambda index, min_held: completions.append(
                (index, min_held)
            ),
            on_finish=lambda: finished.append(True),
        )
        coordinator.add_chunk(make_chunk(0))
        coordinator.add_chunk(make_chunk(1))
        coordinator.finish()
        assert completions[0] == (0, 70)  # candidate held with start 70
        assert completions[1] == (1, None)  # emitted while processing chunk 1
        assert finished == [True]

    def test_no_chunks_finish_is_clean(self):
        finished = []
        coordinator = ClipScoringCoordinator(
            score_chunk=lambda *a: ([], ""),
            emit_decision=lambda d: None,
            chunk_seconds=90,
            context_seconds=10,
            concurrency=2,
            merge_gap_seconds=1.0,
            max_clip_seconds=120,
            on_finish=lambda: finished.append(True),
        )
        coordinator.finish()
        assert finished == [True]

    def test_double_finish_is_noop(self):
        coordinator = ClipScoringCoordinator(
            score_chunk=lambda chunk, *a: ([], "n"),
            emit_decision=lambda d: None,
            chunk_seconds=90,
            context_seconds=10,
            concurrency=2,
            merge_gap_seconds=1.0,
            max_clip_seconds=120,
        )
        coordinator.add_chunk(make_chunk(0))
        coordinator.finish()
        coordinator.finish()


class TestNoClipRecord:
    def test_shape(self):
        record = no_clip_record(3, "nothing happened")
        assert record["chunk_index"] == 3
        assert record["is_clip_worthy"] is False
        assert record["reason"] == "nothing happened"

    def test_empty_reason_gets_default(self):
        assert no_clip_record(0, "")["reason"]
