"""Run the Highlighter pipeline against a livestream or VOD.

Short mode (default): capture -> transcribe -> detect clip-worthy moments ->
render individual short-form clips. Long mode: same pipeline with a long-form
editor prompt, then the selected segments are stitched chronologically into one
long-form video. Both mode: capture, transcription, and shot detection run
once, feeding the short and long editorial forks in parallel — one run
produces the short-form clips and the long-form edit together.

Everything is recorded locally under <output-root>/projects/<project-id>/ in
the same shape Supabase stores it (see records.py); Supabase writes happen too
unless --local-only is passed.
"""

import argparse
import json
import os
import signal
import subprocess
import threading
import time
import uuid
from collections.abc import Callable
from dataclasses import dataclass, field
from functools import partial
from pathlib import Path
from urllib.parse import parse_qs, urlparse

from .capture import assert_capture_prerequisites, capture_audio_chunks
from .config import float_env, int_env, load_env
from .captions import caption_clip, captions_available
from .cookies import prepare_ytdlp_cookies, ytdlp_cookie_args
from .editor import select_longform_segments
from .defaults import (
    DEFAULT_CAPTION_TEMPLATE,
    DEFAULT_CHUNK_SECONDS,
    DEFAULT_CLIP_MERGE_GAP_SECONDS,
    DEFAULT_DEEPGRAM_MODEL,
    DEFAULT_LLM_CONCURRENCY,
    DEFAULT_LLM_CONTEXT_SECONDS,
    DEFAULT_LLM_MARKER_SECONDS,
    DEFAULT_LLM_REASONING_EFFORT,
    DEFAULT_LONGFORM_MAX_CLIP_SECONDS,
    DEFAULT_LONGFORM_MERGE_GAP_SECONDS,
    DEFAULT_MAX_CHUNKS,
    DEFAULT_MAX_CLIP_SECONDS,
    DEFAULT_OUTPUT_ROOT,
    DEFAULT_REFRAME_FRAME_INTERVAL_SECONDS,
    DEFAULT_STREAM_URL,
    DEFAULT_STREAMLINK_QUALITY,
    DEFAULT_SUPABASE_CLIPS_BUCKET,
    DEFAULT_TARGET_LENGTH_MINUTES,
)
from .transcribe import transcribe_audio_file, transcription_backend_label
from .llm import detect_clip_candidates
from .providers import audio_providers, chain_label
from .records import ProjectRecords
from .reframe import plan_crop_track, render_vertical
from .render import (
    clip_filename,
    extract_thumbnail,
    probe_start_time,
    render_clip_from_segments,
    trim_clip,
)
from .research import research_backend_label, research_content_context
from .scoring import ClipScoringCoordinator
from .shots import shot_detector_from_env, snap_window
from .stitch import stitch_clips
from .storage import storage_from_env
from .supabase_client import SupabaseClient
from .thumbnails import generate_thumbnails, thumbnail_model_label, thumbnails_configured


# How often the worker re-reads its project row while capturing, to notice a
# backend-requested cancellation (status 'stopping').
PROJECT_STATUS_POLL_SECONDS = 10


@dataclass
class _Fork:
    """One editorial path over the shared capture: its scoring coordinator,
    research context, stitching policy, and render bookkeeping. Single modes
    run one fork; --pipeline both runs a short and a long fork in parallel."""

    mode: str  # "short" | "long"
    merge_gap_seconds: float
    max_clip_seconds: float
    reframe_enabled: bool
    filename_suffix: str  # "" in single modes, "_short"/"_long" in both mode
    research_context: dict | None = None
    coordinator: ClipScoringCoordinator | None = None
    # Emitter-thread state, touched only by this fork's own emitter.
    parked_renders: dict[int, list[dict]] = field(default_factory=dict)
    rendered_log: list[dict] = field(default_factory=list)
    # How far this fork has progressed, published under the shared segment
    # lock. -1 means it may still need segment 0; segments are only deleted
    # once they are behind every fork's bound.
    retire_bound: float = -1


def main() -> None:
    load_env()

    args = _parse_args()
    source_url = args.source_url or os.environ.get("STREAM_URL", DEFAULT_STREAM_URL)
    pipeline_mode = args.pipeline
    user_instructions = args.instructions or None
    target_length = args.target_minutes
    chunk_seconds = args.chunk_seconds
    max_chunks = args.max_chunks
    streamlink_quality = args.streamlink_quality
    deepgram_model = args.deepgram_model
    llm_enabled = not args.no_llm and not _truthy_env("NO_LLM")
    llm_reasoning_effort = args.llm_reasoning_effort
    llm_marker_seconds = args.llm_marker_seconds
    llm_concurrency = args.llm_concurrency
    llm_context_seconds = args.llm_context_seconds
    research_enabled = llm_enabled and not args.no_research and not _truthy_env("NO_RESEARCH")
    local_only = args.local_only or _truthy_env("LOCAL_ONLY")
    # Shot detection needs the archived video segments and only pays off when
    # clips are actually being selected.
    shots_requested = (
        llm_enabled and not args.no_archive and not args.no_shots and not _truthy_env("NO_SHOTS")
    )
    shot_detector = shot_detector_from_env(shots_requested)
    reframe_interval = args.reframe_interval
    clips_bucket = os.environ.get("SUPABASE_CLIPS_BUCKET", DEFAULT_SUPABASE_CLIPS_BUCKET)
    output_root = Path(args.output_root or os.environ.get("OUTPUT_ROOT", DEFAULT_OUTPUT_ROOT))
    min_clip_score = args.min_clip_score  # None -> resolved from the project row below

    # One editorial fork per output format; --pipeline both runs a short and a
    # long fork in parallel off the shared capture/transcription front end.
    fork_modes = ["short", "long"] if pipeline_mode == "both" else [pipeline_mode]
    no_reframe = args.no_reframe or _truthy_env("NO_REFRAME")
    forks = [
        _Fork(
            mode=mode,
            merge_gap_seconds=(
                DEFAULT_LONGFORM_MERGE_GAP_SECONDS
                if mode == "long"
                else DEFAULT_CLIP_MERGE_GAP_SECONDS
            ),
            max_clip_seconds=(
                DEFAULT_LONGFORM_MAX_CLIP_SECONDS
                if mode == "long"
                else DEFAULT_MAX_CLIP_SECONDS
            ),
            # Short clips ship as blur-pad verticals; long-form output stays 16:9.
            reframe_enabled=llm_enabled and mode == "short" and not no_reframe,
            filename_suffix=f"_{mode}" if len(fork_modes) > 1 else "",
        )
        for mode in fork_modes
    ]
    short_fork = next((fork for fork in forks if fork.mode == "short"), None)
    long_fork = next((fork for fork in forks if fork.mode == "long"), None)
    reframe_enabled = short_fork is not None and short_fork.reframe_enabled
    thumbnails_enabled = (
        long_fork is not None
        and llm_enabled
        and not args.no_thumbnails
        and not _truthy_env("NO_THUMBNAILS")
    )
    if thumbnails_enabled and not thumbnails_configured():
        print("Thumbnail generation needs an image model configured; continuing without it.")
        thumbnails_enabled = False
    captions_enabled = (
        reframe_enabled and not args.no_captions and not _truthy_env("NO_CAPTIONS")
    )
    if captions_enabled and not captions_available():
        print("Captions unavailable (pycaps not on PATH); shipping clean verticals only.")
        captions_enabled = False
    # In both mode the two coordinators split the scoring budget so the total
    # number of in-flight LLM calls stays at the configured level.
    per_fork_concurrency = max(1, llm_concurrency // len(forks))

    if chunk_seconds <= 0:
        raise RuntimeError("CHUNK_SECONDS must be greater than 0")
    if max_chunks < 0:
        raise RuntimeError("MAX_CHUNKS must be 0 or greater")
    if llm_marker_seconds <= 0:
        raise RuntimeError("LLM_MARKER_SECONDS must be greater than 0")
    if llm_concurrency <= 0:
        raise RuntimeError("LLM_CONCURRENCY must be greater than 0")
    if llm_context_seconds < 0:
        raise RuntimeError("LLM_CONTEXT_SECONDS must be 0 or greater")
    if chunk_seconds <= 2 * llm_context_seconds:
        # The boundary stitcher only looks one chunk back; wider context could
        # need merges across non-adjacent chunks.
        raise RuntimeError("CHUNK_SECONDS must be more than twice LLM_CONTEXT_SECONDS")
    if args.source_type not in ("auto", "livestream", "video"):
        raise RuntimeError("SOURCE_TYPE must be one of: auto, livestream, video")
    if reframe_interval is not None and reframe_interval < 0:
        raise RuntimeError("REFRAME_INTERVAL must be 0 or greater")
    if min_clip_score is not None and not 0 <= min_clip_score <= 1:
        raise RuntimeError("MIN_CLIP_SCORE must be between 0 and 1")
    if args.project_id and local_only:
        raise RuntimeError("PROJECT_ID cannot be combined with --local-only")

    # Attach mode: the backend pre-created the project row and launched this
    # worker with its id; use the row as the source of truth instead of
    # creating a new one.
    db = None
    project_row = None
    records: ProjectRecords | None = None
    if args.project_id:
        db = SupabaseClient()
        project_row = db.get_project(args.project_id)

    # Once the row has been fetched in attach mode, every pre-capture failure
    # must be written back to it (see the except below): a crash with no status
    # write would strand the row in 'created' forever.
    try:
        if project_row is not None:
            if project_row["status"] in ("stopping", "cancelled"):
                db.update_project_status_guarded(
                    args.project_id,
                    status="cancelled",
                    when_status_in=["stopping", "cancelled"],
                )
                print(f"Project {args.project_id} was cancelled before capture started; exiting.")
                return
            source_url = project_row.get("source_url") or source_url
            if min_clip_score is None:
                min_clip_score = float(project_row.get("min_clip_score") or 0)
        if min_clip_score is None:
            min_clip_score = 0.0

        platform, detected_type, name = _detect_source(source_url)
        source_type = detected_type if args.source_type == "auto" else args.source_type
        # Live Twitch broadcasts are tapped with streamlink. Everything else is
        # pulled with yt-dlp -- including YouTube livestreams, since streamlink's
        # YouTube plugin has no cookie support and trips YouTube's datacenter
        # bot wall ("Sign in to confirm you're not a bot"). source_type keeps
        # driving archiving and clip rendering regardless of the downloader.
        downloader = (
            "streamlink" if platform == "twitch" and source_type == "livestream" else "yt-dlp"
        )

        assert_capture_prerequisites(downloader)
        # Fail fast on a malformed YTDLP_COOKIES_B64 so the readable error
        # reaches the row via the pre-capture failure handling below.
        prepare_ytdlp_cookies()

        # Known length lets the long-form prompts state the keep rate as a
        # number instead of a vibe. Livestreams stay None.
        source_minutes = None
        if long_fork is not None and source_type == "video":
            source_minutes = _probe_source_minutes(source_url)
            if source_minutes is not None and max_chunks > 0:
                source_minutes = min(source_minutes, max_chunks * chunk_seconds / 60)
            if source_minutes is not None:
                print(f"Source runs ~{source_minutes:.0f} min; calibrating selectivity to it.")

        # Resolve the scoring model chain up front so a missing configuration
        # fails before capture and the run records what actually scores it.
        llm_providers = (
            audio_providers(
                title="highlighter pipeline",
                openrouter_reasoning_effort=llm_reasoning_effort,
            )
            if llm_enabled
            else None
        )
        scoring_backend = chain_label(llm_providers) if llm_providers else None

        ingest_settings = {
            "pipeline": pipeline_mode,
            "user_instructions": user_instructions,
            "target_length_minutes": target_length if long_fork is not None else None,
            "chunk_seconds": chunk_seconds,
            "max_chunks": max_chunks,
            "streamlink_quality": streamlink_quality,
            "transcription_backend": transcription_backend_label(),
            "deepgram_model": deepgram_model,
            "min_clip_score": min_clip_score,
            "source_minutes": source_minutes,
            "shots": {
                "enabled": shot_detector is not None,
                "backend": "transnetv2" if shot_detector is not None else None,
            },
            "reframe": {
                "enabled": reframe_enabled,
                "style": "blurpad-square" if reframe_enabled else None,
                "frame_interval_seconds": reframe_interval if reframe_enabled else None,
            },
            "thumbnails": {
                "enabled": thumbnails_enabled,
                "model": thumbnail_model_label() if thumbnails_enabled else None,
            },
            "captions": {
                "enabled": captions_enabled,
                "template": DEFAULT_CAPTION_TEMPLATE if captions_enabled else None,
            },
            "clip_stitching": (
                {
                    "merge_gap_seconds": forks[0].merge_gap_seconds,
                    "max_clip_seconds": forks[0].max_clip_seconds,
                }
                if len(forks) == 1
                else {
                    fork.mode: {
                        "merge_gap_seconds": fork.merge_gap_seconds,
                        "max_clip_seconds": fork.max_clip_seconds,
                    }
                    for fork in forks
                }
            ),
            "llm": {
                "enabled": llm_enabled,
                "backend": scoring_backend,
                "model": llm_providers[0].model if llm_providers else None,
                "reasoning_effort": llm_reasoning_effort,
                "marker_seconds": llm_marker_seconds,
                "concurrency": llm_concurrency,
                **(
                    {"concurrency_per_fork": per_fork_concurrency}
                    if len(forks) > 1
                    else {}
                ),
                "context_seconds": llm_context_seconds,
            },
            "research": {
                "enabled": research_enabled,
                "backend": research_backend_label() if research_enabled else None,
            },
            "clips": {
                "bucket": clips_bucket if db is not None or not local_only else None,
            },
        }

        # Optional S3 archive for livestreams (they are unrecoverable once
        # over). Local-segment archiving below works without it.
        storage = None
        if source_type == "livestream" and not args.no_archive:
            storage = storage_from_env()

        if local_only:
            project_id = f"local-{uuid.uuid4().hex[:12]}"
            print(f"Started local project {project_id} (no Supabase writes)")
        elif project_row is not None:
            project_id = project_row["id"]
            attached = db.update_project_status_guarded(
                project_id,
                status="ingesting",
                when_status_in=["created", "ingesting"],
                metadata={
                    **(project_row.get("metadata") or {}),
                    "platform": platform,
                    "ingest": ingest_settings,
                },
            )
            if attached is None:
                # The backend flipped the row to stopping/cancelled between our
                # read and this write; acknowledge and bail out before capturing.
                db.update_project_status_guarded(
                    project_id, status="cancelled", when_status_in=["stopping", "cancelled"]
                )
                print(f"Project {project_id} was cancelled before capture started; exiting.")
                return
            print(f"Attached to project {project_id}")
        else:
            db = SupabaseClient()
            project_id = db.create_project(
                name=name,
                source_type=source_type,
                source_url=source_url,
                metadata={"platform": platform, "ingest": ingest_settings},
            )
            print(f"Started project {project_id}")

        project_dir = output_root / "projects" / project_id
        audio_dir = project_dir / "audio"
        records = ProjectRecords(project_dir)
        records.update_project(
            id=project_id,
            name=name,
            source_type=source_type,
            source_url=source_url,
            status="ingesting",
            metadata={"platform": platform, "ingest": ingest_settings},
        )

        stop_state = {"requested": False, "last_poll": 0.0}

        def _stop_requested() -> bool:
            """Poll the project row (rate-limited) for a backend-requested cancel."""
            if db is None:
                return False
            if stop_state["requested"]:
                return True
            now = time.monotonic()
            if now - stop_state["last_poll"] < PROJECT_STATUS_POLL_SECONDS:
                return False
            stop_state["last_poll"] = now
            try:
                status = db.get_project_status(project_id)
            except Exception as exc:
                print(f"Project status poll failed: {exc}")
                return False
            if status in ("stopping", "cancelled"):
                print(f"Cancellation requested (project status is '{status}'); stopping capture.")
                stop_state["requested"] = True
            return stop_state["requested"]

        def _handle_termination_signal(signum: int, frame: object) -> None:
            # Best-effort final status before dying. Guarded, so a terminal status
            # the backend wrote ('cancelled') is never overwritten.
            try:
                final = "cancelled" if stop_state["requested"] else "failed"
                records.update_project(status=final, error=None if stop_state["requested"] else "worker terminated")
                if db is not None:
                    if stop_state["requested"]:
                        db.update_project_status_guarded(
                            project_id, status="cancelled", when_status_in=["stopping", "cancelled"]
                        )
                    else:
                        failed = db.update_project_status_guarded(
                            project_id,
                            status="failed",
                            when_status_in=["created", "ingesting"],
                            error="worker terminated",
                        )
                        if failed is None:
                            db.update_project_status_guarded(
                                project_id, status="cancelled", when_status_in=["stopping"]
                            )
            except Exception:
                pass
            os._exit(0 if stop_state["requested"] else 1)

        signal.signal(signal.SIGTERM, _handle_termination_signal)
        signal.signal(signal.SIGINT, _handle_termination_signal)

        # Clips are cut from locally archived source segments — remote
        # re-downloads at render time proved fragile (broken HLS seeking,
        # sources deleted mid-job) — so archiving is on unless disabled for a
        # transcription-only run. Livestream segments are additionally copied
        # to S3 when S3_BUCKET is set; local copies are retired as the emitter
        # moves past them either way.
        archive_dir = None if args.no_archive else project_dir / "source"
        archive_prefix = f"projects/{project_id}/source"
        archived_segments: list[str] = []
        # Local archive segments still on disk, shared across the forks; each
        # fork parks its own rendered-decisions (in _Fork) until the segment(s)
        # their window needs have been archived. The lock guards mutations and
        # the per-fork retire bounds — in both mode two coordinator emitter
        # threads touch these.
        segment_files: dict[int, Path] = {}
        segments_lock = threading.Lock()
        # Measured container start per archive segment: copy cuts land on
        # keyframes at or after the nominal boundary, so a segment's true
        # position drifts past index * chunk_seconds — clip windows and scene
        # cuts are placed with these instead of nominal arithmetic.
        segment_starts: dict[int, float] = {}
        # Scene cuts per archive segment (absolute seconds) and every word's
        # absolute span, so boundary snapping never lands inside speech.
        chunk_cuts: dict[int, list[float]] = {}
        word_spans: list[tuple[float, float]] = []
        # Word timings per chunk for caption renders. Written by the capture
        # thread before a chunk is scored, so every clip's covering chunks are
        # present by the time its render decision reaches an emitter.
        chunk_words: dict[int, list[dict]] = {}
        source_context = {
            "platform": platform,
            "source_type": source_type,
            "name": name,
            "source_url": source_url,
        }

        if research_enabled:
            # One specialized research call per fork: short researches clip
            # formats and hooks, long researches structure and retention.
            for fork in forks:
                print(
                    f"Researching {fork.mode}-form content context "
                    f"({research_backend_label()})"
                )
                try:
                    fork.research_context = research_content_context(
                        source_context=source_context,
                        pipeline_mode=fork.mode,
                        user_instructions=user_instructions,
                    )
                    # research.json is the long/sole fork's; a combined run's
                    # short fork lands in research_short.json.
                    records.write_research(
                        fork.research_context,
                        mode="short" if len(forks) > 1 and fork.mode == "short" else None,
                    )
                    print(
                        f"Cached {fork.mode}-form content research "
                        f"with {len(fork.research_context.get('sources', []))} source(s)"
                    )
                except Exception as exc:
                    # Research is context, not a dependency; never kill a run on it.
                    print(
                        f"Content research for the {fork.mode} fork failed "
                        f"(continuing without it): {exc}"
                    )

        def _emit_decision(fork: _Fork, decision: dict) -> None:
            """Handle one stitched decision, in chunk order (fork emitter thread)."""
            if decision["is_clip_worthy"] and decision["score"] < min_clip_score:
                # Below the project's score bar: record the decision locally but
                # do not render it or insert a clips row.
                print(
                    "Dropping clip candidate "
                    f"{decision['chunk_index']}: score {decision['score']} is below "
                    f"the minimum clip score {min_clip_score}"
                )
                decision = {
                    **decision,
                    "is_clip_worthy": False,
                    "reason": (
                        f"Score {decision['score']} is below the minimum clip score "
                        f"{min_clip_score}: {decision['reason']}"
                    ),
                }

            if not decision["is_clip_worthy"]:
                _store_clip_decision(
                    db=db,
                    records=records,
                    project_id=project_id,
                    decision=decision,
                    pipeline=fork.mode,
                )
                return

            # The model picked the moment; scene cuts pick the frame. Snap
            # before segment selection so the render uses the final window.
            nearby_cuts = _cuts_near_window(
                chunk_cuts, chunk_seconds, decision["start_seconds"], decision["end_seconds"]
            )
            if nearby_cuts:
                snapped_start, snapped_end, snap_info = snap_window(
                    decision["start_seconds"],
                    decision["end_seconds"],
                    cuts=nearby_cuts,
                    word_spans=word_spans,
                )
                if snap_info is not None:
                    print(
                        f"Snapped clip candidate {decision['chunk_index']} to scene cuts: "
                        f"{snap_info['original_start']}s-{snap_info['original_end']}s -> "
                        f"{snapped_start}s-{snapped_end}s"
                    )
                    decision = {
                        **decision,
                        "start_seconds": snapped_start,
                        "end_seconds": snapped_end,
                        "boundary_snap": snap_info,
                    }

            if archive_dir is None:
                _store_clip_decision(
                    db=db,
                    records=records,
                    project_id=project_id,
                    decision=decision,
                    pipeline=fork.mode,
                    render_result={
                        "status": "failed",
                        "error": "Clip rendering requires source archiving (run without --no-archive).",
                    },
                )
                return
            _try_render_from_segments(fork, decision)

        def _segment_start(index: int) -> float:
            with segments_lock:
                return _measured_segment_start(segment_starts, index, chunk_seconds)

        def _needed_segments(decision: dict) -> list[int]:
            first, last = _window_segment_range(
                decision["start_seconds"], decision["end_seconds"], chunk_seconds, _segment_start
            )
            return list(range(first, last + 1))

        def _try_render_from_segments(fork: _Fork, decision: dict) -> None:
            """Render now when every needed segment is archived; park otherwise
            (fork emitter thread)."""
            needed = _needed_segments(decision)
            missing = [index for index in needed if index not in segment_files]
            if missing:
                fork.parked_renders.setdefault(max(missing), []).append(decision)
                print(
                    "Parked clip candidate "
                    f"{decision['chunk_index']} until segment(s) {missing} are archived"
                )
                return
            _render_and_store_clip(
                db=db,
                records=records,
                project_id=project_id,
                project_dir=project_dir,
                clips_bucket=clips_bucket,
                decision=decision,
                segment_paths=[segment_files[index] for index in needed],
                first_segment_start_seconds=_segment_start(needed[0]),
                rendered_log=fork.rendered_log,
                pipeline=fork.mode,
                filename_suffix=fork.filename_suffix,
                reframe_enabled=fork.reframe_enabled,
                reframe_interval=reframe_interval,
                scene_cuts=_cuts_near_window(
                    chunk_cuts, chunk_seconds, decision["start_seconds"], decision["end_seconds"]
                )
                if fork.reframe_enabled
                else None,
                research_context=fork.research_context,
                captions_enabled=captions_enabled and fork.reframe_enabled,
                clip_words=(
                    _words_in_window(
                        chunk_words,
                        chunk_seconds,
                        decision["start_seconds"],
                        decision["end_seconds"],
                    )
                    if captions_enabled and fork.reframe_enabled
                    else None
                ),
            )

        def _register_segment(fork: _Fork, segment_index: int, segment_path: Path) -> None:
            """Record a completed archive segment (every fork registers the
            same path) and wake this fork's parked renders (fork emitter
            thread)."""
            with segments_lock:
                segment_files[segment_index] = segment_path
            for key in sorted(k for k in fork.parked_renders if k <= segment_index):
                for decision in fork.parked_renders.pop(key):
                    _try_render_from_segments(fork, decision)

        def _retire_stale_segments(
            fork: _Fork, chunk_index: int, min_held_start: float | None
        ) -> None:
            """Delete local segment copies that no fork can reference anymore.
            Each fork publishes its own bound; only segments behind every
            fork's bound are dropped (fork emitter threads)."""
            if archive_dir is None:
                return
            bound = chunk_index - 1
            if min_held_start is not None:
                bound = min(bound, int(min_held_start // chunk_seconds) - 1)
            for decisions in fork.parked_renders.values():
                for decision in decisions:
                    bound = min(bound, int(decision["start_seconds"] // chunk_seconds) - 1)
            with segments_lock:
                fork.retire_bound = bound
                shared_bound = min(f.retire_bound for f in forks)
                stale = [
                    segment_files.pop(index)
                    for index in [i for i in segment_files if i <= shared_bound]
                ]
            for path in stale:
                try:
                    path.unlink(missing_ok=True)
                except OSError as exc:
                    print(f"Could not delete local segment {path}: {exc}")

        def _finish_live_renders(fork: _Fork) -> None:
            """Fail parked decisions whose segments never arrived (fork emitter
            thread, once at the end); the shared segment copies are dropped on
            the main thread once every fork has drained."""
            for key in sorted(fork.parked_renders):
                for decision in fork.parked_renders[key]:
                    _store_clip_decision(
                        db=db,
                        records=records,
                        project_id=project_id,
                        decision=decision,
                        pipeline=fork.mode,
                        render_result={
                            "status": "failed",
                            "error": "No archived video segment was produced for this clip.",
                        },
                    )
            fork.parked_renders.clear()
            with segments_lock:
                # A finished fork no longer holds any segment back.
                fork.retire_bound = float("inf")

        def _drop_remaining_segments() -> None:
            """Delete leftover local segment copies (main thread, after every
            fork's coordinator has drained)."""
            for index in list(segment_files):
                path = segment_files.pop(index)
                try:
                    path.unlink(missing_ok=True)
                except OSError as exc:
                    print(f"Could not delete local segment {path}: {exc}")

        def _score_chunk(
            fork: _Fork,
            chunk: dict,
            context_before: list[dict],
            context_after: list[dict],
            visible_start: int,
            visible_end: int,
        ) -> tuple[list[dict], str]:
            """Score one chunk via the LLM (runs on coordinator pool threads)."""
            print(
                f"Scoring chunk {chunk['chunk_index']} for {fork.mode}-form "
                f"clip candidates via {scoring_backend}"
            )
            scene_cuts = None
            if shot_detector is not None:
                index = chunk["chunk_index"]
                scene_cuts = sorted(
                    cut
                    for neighbor in (index - 1, index, index + 1)
                    for cut in chunk_cuts.get(neighbor, [])
                    if visible_start <= cut <= visible_end
                )
            return detect_clip_candidates(
                transcript=chunk["transcript"],
                words=chunk["words"],
                chunk_index=chunk["chunk_index"],
                start_seconds=chunk["start_seconds"],
                end_seconds=chunk["end_seconds"],
                visible_start_seconds=visible_start,
                visible_end_seconds=visible_end,
                context_words_before=context_before,
                context_words_after=context_after,
                reasoning_effort=llm_reasoning_effort,
                marker_seconds=llm_marker_seconds,
                pipeline_mode=fork.mode,
                user_instructions=user_instructions,
                target_length=target_length,
                source_context=source_context,
                research_context=fork.research_context,
                audio_context=chunk.get("audio_context") or [],
                scene_cuts=scene_cuts,
                source_minutes=source_minutes,
            )

        if llm_enabled:
            for fork in forks:
                fork.coordinator = ClipScoringCoordinator(
                    score_chunk=partial(_score_chunk, fork),
                    emit_decision=partial(_emit_decision, fork),
                    chunk_seconds=chunk_seconds,
                    context_seconds=llm_context_seconds,
                    concurrency=per_fork_concurrency,
                    merge_gap_seconds=fork.merge_gap_seconds,
                    max_clip_seconds=fork.max_clip_seconds,
                    on_chunk_complete=partial(_retire_stale_segments, fork),
                    on_finish=partial(_finish_live_renders, fork),
                )
        coordinators = [fork.coordinator for fork in forks if fork.coordinator is not None]

        def _handle_video_segment(segment_path: Path, segment_index: int) -> None:
            probed = probe_start_time(segment_path)
            if probed is None:
                print(f"Segment {segment_index} start probe failed; using nominal timing")
            else:
                with segments_lock:
                    segment_starts[segment_index] = probed
            if shot_detector is not None:
                try:
                    cuts = shot_detector.detect_cuts(segment_path, _segment_start(segment_index))
                    chunk_cuts[segment_index] = cuts
                    print(f"Detected {len(cuts)} scene cut(s) in segment {segment_index}")
                except Exception as exc:
                    # Cuts are context, not a dependency; capture must survive.
                    print(f"Shot detection failed for segment {segment_index} (continuing): {exc}")
            if storage is not None:
                key = f"{archive_prefix}/{segment_path.name}"
                storage.upload_file(segment_path, key)
                archived_segments.append(key)
                print(
                    f"Archived video segment {segment_index} to s3://{storage.bucket}/{key}"
                )
            if coordinators:
                # The local copy stays until no fork can reference it; each
                # fork's emitter renders its clips from these files.
                for fork in forks:
                    if fork.coordinator is not None:
                        fork.coordinator.run_on_emitter(
                            partial(_register_segment, fork, segment_index, segment_path)
                        )
            else:
                segment_path.unlink()

        longform_result: dict = {}

        def _final_metadata(**extra: object) -> dict:
            metadata: dict = {"platform": platform, "ingest": ingest_settings, **extra}
            # content_research mirrors the research file naming: the long/sole
            # fork's context, with the short fork's alongside in a combined run.
            primary_research = (long_fork or forks[0]).research_context
            if primary_research is not None:
                metadata["content_research"] = primary_research
            if len(forks) > 1 and short_fork.research_context is not None:
                metadata["content_research_short"] = short_fork.research_context
            if longform_result:
                metadata["longform"] = longform_result
            if storage is not None:
                metadata["source_archive"] = {
                    "bucket": storage.bucket,
                    "region": storage.region,
                    "prefix": archive_prefix,
                    "container": "mpegts",
                    "segment_seconds": chunk_seconds,
                    "segments": len(archived_segments),
                    "segment_keys": archived_segments,
                }
            return metadata

        def _merged_project_metadata(metadata: dict) -> dict:
            """Overlay this run's metadata onto the row's current metadata jsonb so
            keys the backend wrote are not clobbered. Best-effort: falls back to the
            run's metadata alone when the row cannot be re-read."""
            try:
                existing = db.get_project(project_id).get("metadata") or {}
            except Exception as exc:
                print(f"Could not re-read project metadata before writing: {exc}")
                existing = {}
            return {**existing, **metadata}

        edit_description = (
            "short- and long-form edits" if len(forks) > 1 else f"a {pipeline_mode}-form edit"
        )
        print(
            f"Capturing {source_url} ({platform} {source_type}, via {downloader}) in {chunk_seconds}s chunks"
            f"{f' ({max_chunks} chunk max)' if max_chunks > 0 else ''}"
            f" for {edit_description}"
            f"{f'; scoring clips via {scoring_backend}' if llm_enabled else '; LLM scoring disabled'}"
            f"{f'; archiving source to s3://{storage.bucket}/{archive_prefix}' if storage else ''}."
        )
    except Exception as exc:
        # Failures after capture starts are covered by the try/except below;
        # this one only handles setup failures.
        if records is not None:
            records.update_project(status="failed", error=str(exc))
        if project_row is not None:
            failed = db.update_project_status_guarded(
                args.project_id,
                status="failed",
                when_status_in=["created", "ingesting"],
                error=str(exc),
            )
            if failed is None:
                # The row was already stopping/cancelled; finish as cancelled.
                db.update_project_status_guarded(
                    args.project_id,
                    status="cancelled",
                    when_status_in=["stopping", "cancelled"],
                )
        raise

    try:
        chunk_count = capture_audio_chunks(
            source_url=source_url,
            output_dir=audio_dir,
            chunk_seconds=chunk_seconds,
            max_chunks=max_chunks,
            streamlink_quality=streamlink_quality,
            downloader=downloader,
            source_type=source_type,
            on_chunk=lambda audio_path, chunk_index, start_seconds, end_seconds: _process_chunk(
                db=db,
                records=records,
                project_id=project_id,
                audio_path=audio_path,
                chunk_index=chunk_index,
                start_seconds=start_seconds,
                end_seconds=end_seconds,
                deepgram_model=deepgram_model,
                coordinators=coordinators,
                scene_cuts=chunk_cuts.get(chunk_index),
                word_spans=word_spans,
                chunk_words=chunk_words,
            ),
            archive_dir=archive_dir,
            on_video_segment=_handle_video_segment if archive_dir is not None else None,
            should_stop=_stop_requested,
        )
        # Drains in-flight scoring, stitches, renders, and cleans up; on a
        # cancel nothing new is dispatched but finished scores still land.
        # Each finish() blocks until that fork's emitter exits, so the shared
        # segment copies can be dropped safely once every fork has drained.
        finish_error: Exception | None = None
        for fork in forks:
            if fork.coordinator is not None:
                try:
                    fork.coordinator.finish(cancelled=stop_state["requested"])
                except Exception as exc:
                    # Keep draining the other fork before surfacing this.
                    finish_error = exc
                    print(f"The {fork.mode} fork failed while finishing: {exc}")
        _drop_remaining_segments()
        if finish_error is not None:
            raise finish_error

        if long_fork is not None and not stop_state["requested"]:
            longform_result.update(
                _edit_longform(
                    db=db,
                    records=records,
                    project_id=project_id,
                    project_dir=project_dir,
                    clips_bucket=clips_bucket,
                    rendered_log=long_fork.rendered_log,
                    chunk_count=chunk_count,
                    chunk_seconds=chunk_seconds,
                    target_length=target_length,
                    user_instructions=user_instructions,
                    research_context=long_fork.research_context,
                    reasoning_effort=llm_reasoning_effort,
                    chunk_cuts=chunk_cuts,
                    word_spans=word_spans,
                    thumbnails_enabled=thumbnails_enabled,
                )
            )

        metadata = _final_metadata(chunks=chunk_count)
        if db is not None:
            merged = _merged_project_metadata(metadata)
            if stop_state["requested"]:
                cancelled = True
            else:
                # Guarded: matches nothing when the backend flipped the row to
                # 'stopping' after our last poll, in which case we acknowledge
                # the cancel instead of writing 'ready'.
                cancelled = (
                    db.update_project_status_guarded(
                        project_id,
                        status="ready",
                        when_status_in=["ingesting"],
                        metadata=merged,
                    )
                    is None
                )
            if cancelled:
                db.update_project_status_guarded(
                    project_id,
                    status="cancelled",
                    when_status_in=["stopping", "cancelled"],
                    metadata=merged,
                )
        else:
            cancelled = stop_state["requested"]

        final_status = "cancelled" if cancelled else "ready"
        records.update_project(status=final_status, metadata=metadata)
        print(f"Project {project_id} {final_status} with {chunk_count} chunk(s).")
    except Exception as exc:
        # Best-effort teardown; it must never mask the capture failure, and
        # every fork must drain even when another fork's teardown fails.
        for fork in forks:
            if fork.coordinator is not None:
                try:
                    fork.coordinator.finish(cancelled=True)
                except Exception:
                    pass
        try:
            _drop_remaining_segments()
        except Exception:
            pass
        metadata = _final_metadata()
        records.update_project(status="failed", error=str(exc), metadata=metadata)
        if db is not None:
            merged = _merged_project_metadata(metadata)
            failed = db.update_project_status_guarded(
                project_id,
                status="failed",
                when_status_in=["created", "ingesting"],
                metadata=merged,
                error=str(exc),
            )
            if failed is None:
                # The row was already stopping/cancelled; finish as cancelled.
                db.update_project_status_guarded(
                    project_id,
                    status="cancelled",
                    when_status_in=["stopping", "cancelled"],
                    metadata=merged,
                )
        raise


def _window_segment_range(
    start_seconds: float,
    end_seconds: float,
    chunk_seconds: int,
    segment_start: Callable[[int], float],
) -> tuple[int, int]:
    """Archive segment indexes a clip window spans (end is exclusive when it
    lands exactly on a segment boundary). Measured starts sit at or after the
    nominal boundary, so a window can begin in the previous segment's tail or
    end before a nominal segment's content starts."""
    first = int(start_seconds // chunk_seconds)
    last = int(max(start_seconds, end_seconds - 0.001) // chunk_seconds)
    while first > 0 and segment_start(first) > start_seconds:
        first -= 1
    while last > first and segment_start(last) >= end_seconds:
        last -= 1
    return first, last


def _measured_segment_start(
    segment_starts: dict[int, float], index: int, chunk_seconds: int
) -> float:
    """A segment's real position on the capture clock: its archived container
    timestamp relative to segment 0's. Copy cuts wait for a keyframe, so this
    sits at or after index * chunk_seconds — which is the fallback whenever a
    probe is missing."""
    base = segment_starts.get(0)
    start = segment_starts.get(index)
    if base is None or start is None:
        return index * chunk_seconds
    return start - base


def _cuts_near_window(
    chunk_cuts: dict[int, list[float]],
    chunk_seconds: int,
    start_seconds: float,
    end_seconds: float,
) -> list[float]:
    """Every detected scene cut in the chunks a window touches, plus one chunk
    of margin on each side so boundary snapping can look just past the edges."""
    first = int(start_seconds // chunk_seconds) - 1
    last = int(end_seconds // chunk_seconds) + 1
    return sorted(
        cut for index in range(first, last + 1) for cut in chunk_cuts.get(index, [])
    )


def _probe_source_minutes(source_url: str) -> float | None:
    """Best-effort source duration in minutes via yt-dlp; None when unknown."""
    try:
        result = subprocess.run(
            [
                "yt-dlp",
                "--quiet",
                "--no-warnings",
                *ytdlp_cookie_args(),
                "--skip-download",
                "--print",
                "duration",
                source_url,
            ],
            capture_output=True,
            text=True,
            timeout=60,
        )
        if result.returncode != 0 or not result.stdout.strip():
            return None
        duration_seconds = float(result.stdout.strip().splitlines()[0])
        return duration_seconds / 60 if duration_seconds > 0 else None
    except Exception:
        return None


def _edit_longform(
    *,
    db: SupabaseClient | None,
    records: ProjectRecords,
    project_id: str,
    project_dir: Path,
    clips_bucket: str,
    rendered_log: list[dict],
    chunk_count: int,
    chunk_seconds: int,
    target_length: str | None,
    user_instructions: str | None,
    research_context: dict | None,
    reasoning_effort: str,
    chunk_cuts: dict[int, list[float]],
    word_spans: list[tuple[float, float]],
    thumbnails_enabled: bool = False,
) -> dict:
    """Pass 2: one global editor call sees every rendered pass-1 candidate and
    makes the final cut — keep/drop, plus tightening within a segment's own
    window — before stitching. Any editor failure falls back to stitching all
    pass-1 selections, so this pass can only improve a run."""
    entries = sorted(rendered_log, key=lambda clip: clip["start_seconds"])
    editor_meta: dict | None = None
    if len(entries) >= 2:
        try:
            edit = select_longform_segments(
                candidates=entries,
                source_minutes=chunk_count * chunk_seconds / 60,
                target_length=target_length,
                research_context=research_context,
                user_instructions=user_instructions,
                reasoning_effort=reasoning_effort,
            )
            if not edit["selections"]:
                raise RuntimeError("the editor kept no segments")
            final_entries, editor_meta = _apply_longform_selections(
                entries=entries,
                edit=edit,
                project_dir=project_dir,
                chunk_cuts=chunk_cuts,
                word_spans=word_spans,
                chunk_seconds=chunk_seconds,
            )
            records.append_decision(
                {"type": "longform_edit", **editor_meta, "selections": edit["selections"]}
            )
            print(
                f"Long-form editor kept {editor_meta['kept_segments']} of "
                f"{editor_meta['candidate_segments']} candidate segment(s)"
                + (
                    f" ({editor_meta['trimmed_segments']} tightened)"
                    if editor_meta["trimmed_segments"]
                    else ""
                )
            )
            entries = final_entries
        except Exception as exc:
            print(f"Long-form editor pass failed; stitching all pass-1 selections: {exc}")
            editor_meta = {"status": "failed", "error": str(exc)}
            records.append_decision({"type": "longform_edit", **editor_meta})

    return _stitch_longform(
        db=db,
        records=records,
        project_id=project_id,
        project_dir=project_dir,
        clips_bucket=clips_bucket,
        entries=entries,
        editor=editor_meta,
        research_context=research_context,
        reasoning_effort=reasoning_effort,
        thumbnails_enabled=thumbnails_enabled,
    )


def _apply_longform_selections(
    *,
    entries: list[dict],
    edit: dict,
    project_dir: Path,
    chunk_cuts: dict[int, list[float]],
    word_spans: list[tuple[float, float]],
    chunk_seconds: int,
) -> tuple[list[dict], dict]:
    """Materialize the editor's selections: keep each chosen rendered segment,
    re-cutting any whose final window was tightened (snapped to scene cuts,
    never extended). Returns (final entries, editor metadata)."""
    segments_dir = project_dir / "longform" / "segments"
    final_entries: list[dict] = []
    trimmed = 0
    for order, selection in enumerate(edit["selections"]):
        candidate = entries[selection["index"]]
        entry = {**candidate, "editor_reason": selection["reason"]}
        start, end = selection["start_seconds"], selection["end_seconds"]
        needs_trim = (
            start - candidate["start_seconds"] > 0.25
            or candidate["end_seconds"] - end > 0.25
        )
        if needs_trim:
            nearby_cuts = _cuts_near_window(chunk_cuts, chunk_seconds, start, end)
            snap_info = None
            if nearby_cuts:
                start, end, snap_info = snap_window(
                    start, end, cuts=nearby_cuts, word_spans=word_spans
                )
            start = max(start, candidate["start_seconds"])
            end = min(end, candidate["end_seconds"])
            if end - start >= 2.0:
                output_path = segments_dir / f"segment_{order:03d}.mp4"
                trim_clip(
                    source_path=Path(candidate["path"]),
                    output_path=output_path,
                    start_offset_seconds=start - candidate["start_seconds"],
                    duration_seconds=end - start,
                )
                entry.update(
                    {"path": str(output_path), "start_seconds": start, "end_seconds": end}
                )
                if snap_info is not None:
                    entry["boundary_snap"] = snap_info
                trimmed += 1

        final_entries.append(entry)

    editor_meta = {
        "status": "applied",
        "model": edit["model"],
        "edit_notes": edit["edit_notes"],
        "title": edit["title"],
        "arithmetic": edit["arithmetic"],
        "candidate_segments": len(entries),
        "kept_segments": len(final_entries),
        "dropped_segments": len(entries) - len(final_entries),
        "trimmed_segments": trimmed,
        "candidates_dropped_from_prompt": edit["candidates_dropped_from_prompt"],
    }
    return sorted(final_entries, key=lambda e: e["start_seconds"]), editor_meta


def _stitch_longform(
    *,
    db: SupabaseClient | None,
    records: ProjectRecords,
    project_id: str,
    project_dir: Path,
    clips_bucket: str,
    entries: list[dict],
    editor: dict | None = None,
    research_context: dict | None = None,
    reasoning_effort: str = DEFAULT_LLM_REASONING_EFFORT,
    thumbnails_enabled: bool = False,
) -> dict:
    """Concatenate the final segments chronologically into one long-form
    video, persist it as longform_edits version 1, and return its metadata (or
    a failure/empty record — stitching problems must not fail the whole run)."""
    if not entries:
        print("No rendered segments to stitch into a long-form video.")
        return {"status": "empty", "segments": 0}

    ordered = sorted(entries, key=lambda clip: clip["start_seconds"])
    title = (editor or {}).get("title") or ordered[0].get("title")
    output_path = project_dir / "longform" / "longform.mp4"
    total_seconds = sum(clip["end_seconds"] - clip["start_seconds"] for clip in ordered)
    print(
        f"Stitching {len(ordered)} segment(s) (~{total_seconds / 60:.1f} min) "
        f"into {output_path}"
    )

    try:
        stitch_clips(
            clip_paths=[Path(clip["path"]) for clip in ordered],
            output_path=output_path,
        )
    except Exception as exc:
        print(f"Long-form stitch failed: {exc}")
        return {"status": "failed", "error": str(exc), "segments": len(ordered)}

    result = {
        "status": "rendered",
        "version": 1,
        "title": title,
        "local_path": os.path.relpath(output_path),
        "filename": output_path.name,
        "segments": len(ordered),
        "duration_seconds": round(total_seconds, 3),
        "segment_windows": [
            {
                "chunk_index": clip["chunk_index"],
                "title": clip["title"],
                "start_seconds": clip["start_seconds"],
                "end_seconds": clip["end_seconds"],
            }
            for clip in ordered
        ],
    }
    if editor is not None:
        result["editor"] = editor

    thumbnail_path = None
    try:
        thumbnail_path = output_path.with_suffix(".jpg")
        extract_thumbnail(
            clip_path=output_path, output_path=thumbnail_path, at_seconds=total_seconds / 2
        )
    except Exception as exc:
        thumbnail_path = None
        print(f"Long-form thumbnail extraction failed (non-fatal): {exc}")

    thumbnails = None
    if thumbnails_enabled:
        try:
            thumbnails = generate_thumbnails(
                longform_path=output_path,
                entries=ordered,
                title=title,
                research_context=research_context,
                output_dir=project_dir / "thumbnails",
                reasoning_effort=reasoning_effort,
            )
        except Exception as exc:
            print(f"Thumbnail generation failed (non-fatal): {exc}")
    if thumbnails is not None:
        result["thumbnails"] = thumbnails

    if db is not None:
        try:
            storage_key = f"projects/{project_id}/longform/{output_path.name}"
            video_url = db.upload_storage_object(
                bucket=clips_bucket, key=storage_key, path=output_path
            )
            result.update(
                {"bucket": clips_bucket, "storage_path": storage_key, "video_url": video_url}
            )
            if thumbnail_path is not None:
                try:
                    thumbnail_key = f"projects/{project_id}/longform/{thumbnail_path.name}"
                    result["thumbnail_url"] = db.upload_storage_object(
                        bucket=clips_bucket, key=thumbnail_key, path=thumbnail_path
                    )
                    result["thumbnail_storage_path"] = thumbnail_key
                except Exception as exc:
                    print(f"Long-form thumbnail upload failed (non-fatal): {exc}")
            if thumbnails is not None:
                for variant in thumbnails["variants"]:
                    try:
                        variant_name = Path(variant["local_path"]).name
                        variant_key = f"projects/{project_id}/thumbnails/{variant_name}"
                        variant["url"] = db.upload_storage_object(
                            bucket=clips_bucket,
                            key=variant_key,
                            path=Path(variant["local_path"]),
                        )
                        variant["storage_path"] = variant_key
                    except Exception as exc:
                        print(f"Thumbnail variant upload failed (non-fatal): {exc}")
            db.insert_longform_edit(
                project_id=project_id,
                version=1,
                status="rendered",
                video_url=video_url,
                thumbnail_url=_display_thumbnail_url(result),
                duration_seconds=round(total_seconds, 3),
                segments=result["segment_windows"],
                editor=editor,
                metadata={"source": "longform_stitch", "render": result},
            )
        except Exception as exc:
            print(f"Long-form upload failed (kept locally at {output_path}): {exc}")
            result["upload_error"] = str(exc)

    records.append_longform_edit(
        {
            "project_id": project_id,
            "version": 1,
            "status": "rendered",
            "video_url": result.get("video_url") or result["local_path"],
            "thumbnail_url": _display_thumbnail_url(result),
            "duration_seconds": round(total_seconds, 3),
            "segments": result["segment_windows"],
            "editor": editor,
            "revision": None,
            "metadata": {"source": "longform_stitch", "render": result},
        }
    )
    print(f"Long-form video ready: {output_path}")
    return result


def _display_thumbnail_url(result: dict) -> str | None:
    """The thumbnail a longform_edits row shows: the selected generated
    variant when one was uploaded, else the midpoint frame."""
    thumbnails = result.get("thumbnails")
    if thumbnails is not None:
        selected = thumbnails["variants"][thumbnails["selected_index"]]
        if selected.get("url"):
            return selected["url"]
    return result.get("thumbnail_url")


def _detect_source(url: str) -> tuple[str, str, str]:
    """Classify a source URL. Returns (platform, source_type, project name)."""
    parsed = urlparse(url)
    host = parsed.netloc.lower().removeprefix("www.")
    path = parsed.path.rstrip("/")
    segments = [segment for segment in path.split("/") if segment]

    if host.endswith("twitch.tv"):
        if len(segments) >= 2 and segments[-2] == "videos":
            return "twitch", "video", f"twitch video {segments[-1]}"
        channel = segments[-1] if segments else "twitch"
        return "twitch", "livestream", f"{channel} livestream"

    if host.endswith("youtube.com"):
        if segments and segments[-1] == "live" and len(segments) >= 2:
            return "youtube", "livestream", f"{segments[-2].lstrip('@')} livestream"
        video_id = parse_qs(parsed.query).get("v", [None])[0]
        if video_id:
            return "youtube", "video", f"youtube video {video_id}"
        if segments:
            return "youtube", "livestream", f"{segments[-1].lstrip('@')} livestream"

    if host == "youtu.be" and segments:
        return "youtube", "video", f"youtube video {segments[-1]}"

    raise RuntimeError(f"Unsupported source URL (expected a twitch.tv or youtube URL): {url}")


def _store_chunk(
    *,
    db: SupabaseClient | None,
    records: ProjectRecords,
    project_id: str,
    audio_path: Path,
    chunk_index: int,
    start_seconds: int,
    end_seconds: int,
    deepgram_model: str,
    scene_cuts: list[float] | None = None,
) -> dict:
    print(f"Transcribing chunk {chunk_index} ({start_seconds}s-{end_seconds}s)")
    transcript = transcribe_audio_file(audio_path, model=deepgram_model)
    words = [_with_absolute_times(word, start_seconds) for word in transcript["words"]]
    metadata = {
        "audio_path": os.path.relpath(audio_path),
        "transcription_backend": transcript.get("backend"),
        "transcription": transcript["metadata"],
    }
    if scene_cuts is not None:
        metadata["scene_cuts"] = scene_cuts

    if db is not None:
        db.insert_transcript_chunk(
            project_id=project_id,
            chunk_index=chunk_index,
            start_seconds=start_seconds,
            end_seconds=end_seconds,
            transcript=transcript["transcript"],
            words=words,
            metadata=metadata,
        )
    records.append_chunk(
        {
            "project_id": project_id,
            "chunk_index": chunk_index,
            "start_seconds": start_seconds,
            "end_seconds": end_seconds,
            "transcript": transcript["transcript"],
            "words": words,
            "metadata": metadata,
        }
    )

    print(f"Stored chunk {chunk_index}: {transcript['transcript'][:120]}")
    return {"transcript": transcript["transcript"], "words": words, "metadata": transcript["metadata"]}


def _process_chunk(
    *,
    db: SupabaseClient | None,
    records: ProjectRecords,
    project_id: str,
    audio_path: Path,
    chunk_index: int,
    start_seconds: int,
    end_seconds: int,
    deepgram_model: str,
    coordinators: list[ClipScoringCoordinator],
    scene_cuts: list[float] | None = None,
    word_spans: list[tuple[float, float]] | None = None,
    chunk_words: dict[int, list[dict]] | None = None,
) -> None:
    stored = _store_chunk(
        db=db,
        records=records,
        project_id=project_id,
        audio_path=audio_path,
        chunk_index=chunk_index,
        start_seconds=start_seconds,
        end_seconds=end_seconds,
        deepgram_model=deepgram_model,
        scene_cuts=scene_cuts,
    )
    if word_spans is not None:
        word_spans.extend(
            (float(word["absolute_start"]), float(word["absolute_end"]))
            for word in stored["words"]
        )
    if chunk_words is not None:
        chunk_words[chunk_index] = stored["words"]
    # Scoring happens on each coordinator's pool: this chunk is dispatched once
    # the NEXT chunk's transcript arrives, so the model sees both boundaries.
    # The chunk dict is read-only inside the coordinators, so the forks share it.
    chunk = {
        "chunk_index": chunk_index,
        "start_seconds": start_seconds,
        "end_seconds": end_seconds,
        "transcript": stored["transcript"],
        "words": stored["words"],
        "audio_path": str(audio_path),
    }
    for coordinator in coordinators:
        coordinator.add_chunk(chunk)


def _words_in_window(
    chunk_words: dict[int, list[dict]],
    chunk_seconds: int,
    start_seconds: float,
    end_seconds: float,
) -> list[dict]:
    """Words overlapping a clip window, pulled from the chunks that cover it."""
    first = int(start_seconds // chunk_seconds)
    last = int(max(start_seconds, end_seconds - 0.001) // chunk_seconds)
    return [
        word
        for index in range(first, last + 1)
        for word in chunk_words.get(index, [])
        if word["absolute_end"] > start_seconds and word["absolute_start"] < end_seconds
    ]


def _render_and_store_clip(
    *,
    db: SupabaseClient | None,
    records: ProjectRecords,
    project_id: str,
    project_dir: Path,
    clips_bucket: str,
    decision: dict,
    segment_paths: list[Path],
    first_segment_start_seconds: float,
    rendered_log: list[dict],
    pipeline: str,
    filename_suffix: str = "",
    reframe_enabled: bool = False,
    reframe_interval: float | None = None,
    scene_cuts: list[float] | None = None,
    research_context: dict | None = None,
    captions_enabled: bool = False,
    clip_words: list[dict] | None = None,
) -> None:
    filename = clip_filename(
        chunk_index=decision["chunk_index"],
        start_seconds=decision["start_seconds"],
        end_seconds=decision["end_seconds"],
        suffix=filename_suffix,
    )
    output_path = project_dir / "clips" / filename

    try:
        render_clip_from_segments(
            segment_paths=segment_paths,
            output_path=output_path,
            first_segment_start_seconds=first_segment_start_seconds,
            start_seconds=decision["start_seconds"],
            end_seconds=decision["end_seconds"],
        )

        render_result = {
            "status": "rendered",
            "local_path": os.path.relpath(output_path),
            "filename": filename,
        }
        thumbnail_path = _extract_clip_thumbnail(output_path, decision)
        vertical_path = None
        if reframe_enabled:
            reframed = _reframe_clip(
                clip_path=output_path,
                decision=decision,
                scene_cuts=scene_cuts,
                research_context=research_context,
                frame_interval_seconds=reframe_interval,
            )
            if reframed is not None:
                vertical_path, crop_track = reframed
                render_result["vertical_path"] = os.path.relpath(vertical_path)
                decision = {**decision, "reframe": crop_track}
        captioned_path = None
        if vertical_path is not None and captions_enabled and clip_words:
            captioned_path = caption_clip(
                vertical_path=vertical_path,
                words=clip_words,
                clip_start_seconds=decision["start_seconds"],
                clip_end_seconds=decision["end_seconds"],
                output_path=vertical_path.with_name(f"{vertical_path.stem}_captions.mp4"),
            )
            if captioned_path is not None:
                render_result["captioned_path"] = os.path.relpath(captioned_path)
                print(f"Captioned clip {decision['chunk_index']} to {captioned_path.name}")
        if db is not None:
            storage_key = f"projects/{project_id}/clips/{filename}"
            video_url = db.upload_storage_object(
                bucket=clips_bucket,
                key=storage_key,
                path=output_path,
            )
            render_result.update(
                {
                    "bucket": clips_bucket,
                    "storage_path": storage_key,
                    "video_url": video_url,
                }
            )
            if thumbnail_path is not None:
                try:
                    thumbnail_key = f"projects/{project_id}/clips/{thumbnail_path.name}"
                    render_result["thumbnail_url"] = db.upload_storage_object(
                        bucket=clips_bucket, key=thumbnail_key, path=thumbnail_path
                    )
                    render_result["thumbnail_storage_path"] = thumbnail_key
                except Exception as exc:
                    print(f"Thumbnail upload failed (non-fatal): {exc}")
            if vertical_path is not None:
                try:
                    vertical_key = f"projects/{project_id}/clips/{vertical_path.name}"
                    render_result["vertical_url"] = db.upload_storage_object(
                        bucket=clips_bucket, key=vertical_key, path=vertical_path
                    )
                    render_result["vertical_storage_path"] = vertical_key
                except Exception as exc:
                    print(f"Vertical clip upload failed (non-fatal): {exc}")
            if captioned_path is not None:
                try:
                    captioned_key = f"projects/{project_id}/clips/{captioned_path.name}"
                    render_result["captioned_url"] = db.upload_storage_object(
                        bucket=clips_bucket, key=captioned_key, path=captioned_path
                    )
                    render_result["captioned_storage_path"] = captioned_key
                except Exception as exc:
                    print(f"Captioned clip upload failed (non-fatal): {exc}")

        rendered_log.append(
            {
                "path": str(output_path),
                "chunk_index": decision["chunk_index"],
                "title": decision["title"],
                "description": decision["description"],
                "score": decision["score"],
                "reason": decision["reason"],
                "start_seconds": decision["start_seconds"],
                "end_seconds": decision["end_seconds"],
            }
        )
        print(f"Rendered clip {decision['chunk_index']} to {output_path}")
    except Exception as exc:
        render_result = {"status": "failed", "error": str(exc)}
        print(f"Clip render failed for chunk {decision['chunk_index']}: {exc}")

    _store_clip_decision(
        db=db,
        records=records,
        project_id=project_id,
        decision=decision,
        pipeline=pipeline,
        render_result=render_result,
    )


def _reframe_clip(
    *,
    clip_path: Path,
    decision: dict,
    scene_cuts: list[float] | None,
    research_context: dict | None,
    frame_interval_seconds: float | None = None,
) -> tuple[Path, dict] | None:
    """Best-effort blur-pad vertical next to the rendered clip. Returns
    (vertical_path, crop_track) or None — a reframe failure keeps the 16:9."""
    try:
        duration = decision["end_seconds"] - decision["start_seconds"]
        relative_cuts = [
            round(cut - decision["start_seconds"], 2)
            for cut in (scene_cuts or [])
            if decision["start_seconds"] < cut < decision["end_seconds"]
        ]
        crop_track = plan_crop_track(
            clip_path=clip_path,
            clip_duration_seconds=duration,
            scene_cuts=relative_cuts,
            title=decision["title"],
            description=decision["description"],
            research_context=research_context,
            frame_interval_seconds=(
                frame_interval_seconds
                if frame_interval_seconds is not None
                else DEFAULT_REFRAME_FRAME_INTERVAL_SECONDS
            ),
        )
        vertical_path = clip_path.with_name(f"{clip_path.stem}_vertical.mp4")
        render_vertical(
            source_path=clip_path,
            output_path=vertical_path,
            keyframes=crop_track["keyframes"],
        )
        print(
            f"Reframed clip {decision['chunk_index']} to {vertical_path.name} "
            f"({len(crop_track['keyframes'])} framing(s))"
        )
        return vertical_path, crop_track
    except Exception as exc:
        print(
            f"Auto-reframe failed for chunk {decision['chunk_index']} "
            f"(keeping the 16:9 clip): {exc}"
        )
        return None


def _extract_clip_thumbnail(clip_path: Path, decision: dict) -> Path | None:
    """Best-effort mid-clip JPEG next to the rendered mp4; None on failure."""
    try:
        thumbnail_path = clip_path.with_suffix(".jpg")
        midpoint = max(
            0.0, (decision["end_seconds"] - decision["start_seconds"]) / 2
        )
        extract_thumbnail(
            clip_path=clip_path, output_path=thumbnail_path, at_seconds=midpoint
        )
        return thumbnail_path
    except Exception as exc:
        print(f"Thumbnail extraction failed for {clip_path.name} (non-fatal): {exc}")
        return None


def _store_clip_decision(
    *,
    db: SupabaseClient | None,
    records: ProjectRecords,
    project_id: str,
    decision: dict,
    pipeline: str,
    render_result: dict | None = None,
) -> None:
    # The fork that made the decision; in a combined run both forks' records
    # interleave, and revisions read only the long-form ones.
    decision = {**decision, "pipeline": pipeline}
    if render_result is not None:
        decision = {**decision, "render": render_result}

    records.append_decision(decision)

    if not decision["is_clip_worthy"]:
        print(f"No clip candidate for chunk {decision['chunk_index']}: {decision['reason'][:120]}")
        return

    clip_status = "detected"
    video_url = None
    vertical_url = None
    captioned_url = None
    if render_result is not None:
        clip_status = render_result.get("status", "detected")
        video_url = render_result.get("video_url") or render_result.get("local_path")
        vertical_url = render_result.get("vertical_url") or render_result.get("vertical_path")
        captioned_url = render_result.get("captioned_url") or render_result.get("captioned_path")

    clip_metadata = {
        "source": "llm",
        "pipeline": pipeline,
        "chunk_index": decision["chunk_index"],
        "model": decision["model"],
        "reasoning_effort": decision["reasoning_effort"],
        "reason": decision["reason"],
        "research_sources": decision.get("research_sources", []),
        "raw_decision": decision["raw_decision"],
        "boundary_snap": decision.get("boundary_snap"),
        "reframe": decision.get("reframe"),
        "render": render_result,
        "thumbnail_url": (render_result or {}).get("thumbnail_url"),
        "merged_from": decision.get("merged_from"),
    }
    records.append_clip(
        {
            "project_id": project_id,
            "title": decision["title"],
            "description": decision["description"],
            "start_seconds": decision["start_seconds"],
            "end_seconds": decision["end_seconds"],
            "score": decision["score"],
            "video_url": video_url,
            "vertical_url": vertical_url,
            "captioned_url": captioned_url,
            "status": clip_status,
            "metadata": clip_metadata,
        }
    )
    if db is not None:
        db.insert_clip(
            project_id=project_id,
            title=decision["title"],
            description=decision["description"],
            start_seconds=decision["start_seconds"],
            end_seconds=decision["end_seconds"],
            score=decision["score"],
            video_url=render_result.get("video_url") if render_result else None,
            vertical_url=render_result.get("vertical_url") if render_result else None,
            captioned_url=render_result.get("captioned_url") if render_result else None,
            status=clip_status,
            metadata=clip_metadata,
        )

    print(
        "Stored clip "
        f"{decision['chunk_index']}: {decision['title']} "
        f"({decision['start_seconds']}s-{decision['end_seconds']}s, score {decision['score']})"
    )


def _with_absolute_times(word: dict, chunk_start_seconds: int) -> dict:
    output = dict(word)
    output["absolute_start"] = round(float(word.get("start", 0)) + chunk_start_seconds, 3)
    output["absolute_end"] = round(float(word.get("end", 0)) + chunk_start_seconds, 3)
    return output


def _parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Capture a Twitch/YouTube stream or video, transcribe chunks, and produce clips.",
    )
    parser.add_argument(
        "source_url",
        nargs="?",
        help="Twitch or YouTube URL. Falls back to STREAM_URL, then the internal development default.",
    )
    parser.add_argument(
        "--pipeline",
        choices=["short", "long", "both"],
        default=os.environ.get("PIPELINE") or "short",
        help="short: independent short-form clips. long: long-form segment selection "
        "stitched into one video. both: one capture feeding the short and long "
        "forks in parallel (also via PIPELINE).",
    )
    parser.add_argument(
        "--instructions",
        default=os.environ.get("USER_INSTRUCTIONS") or None,
        help="Optional editorial guidance passed to the editor model "
        "(style, focus, tone; also via USER_INSTRUCTIONS).",
    )
    parser.add_argument(
        "--target-minutes",
        default=os.environ.get("TARGET_MINUTES", DEFAULT_TARGET_LENGTH_MINUTES),
        help="Long-form only: rough target runtime in minutes (e.g. '10' or '7-15'). "
        "Guidance for the editor model, not a hard limit (also via TARGET_MINUTES).",
    )
    parser.add_argument(
        "--project-id",
        default=os.environ.get("PROJECT_ID") or None,
        help="Attach to a pre-created projects row (also via PROJECT_ID) instead of creating one; "
        "its source_url and min_clip_score become the defaults.",
    )
    parser.add_argument(
        "--source-type",
        choices=["auto", "livestream", "video"],
        default=os.environ.get("SOURCE_TYPE") or "auto",
        help="Override the detected source type stored on the project (also via SOURCE_TYPE).",
    )
    parser.add_argument(
        "--min-clip-score",
        type=float,
        default=float_env("MIN_CLIP_SCORE"),
        help="Minimum clip score (0..1) to render and store; lower-scoring candidates are dropped. "
        "Overrides the project row's min_clip_score (also via MIN_CLIP_SCORE).",
    )
    parser.add_argument(
        "--local-only",
        action="store_true",
        default=False,
        help="Skip Supabase entirely; write records to the local output directory only "
        "(also via LOCAL_ONLY=1).",
    )
    parser.add_argument(
        "--output-root",
        default=None,
        help="Directory for project outputs (default: 'outputs', also via OUTPUT_ROOT).",
    )
    parser.add_argument(
        "--no-archive",
        action="store_true",
        default=False,
        help="Do not keep source video segments; transcription-only (clips cannot be rendered).",
    )
    parser.add_argument(
        "--no-research",
        action="store_true",
        default=False,
        help="Skip the content research agent (also via NO_RESEARCH=1).",
    )
    parser.add_argument(
        "--chunk-seconds",
        type=int,
        default=int_env("CHUNK_SECONDS", DEFAULT_CHUNK_SECONDS),
        help="Seconds of source audio per transcript chunk.",
    )
    parser.add_argument(
        "--max-chunks",
        type=int,
        default=int_env("MAX_CHUNKS", DEFAULT_MAX_CHUNKS),
        help="Maximum chunks to process. Use 0 to run until the stream or video ends.",
    )
    parser.add_argument(
        "--streamlink-quality",
        default=os.environ.get("STREAMLINK_QUALITY", DEFAULT_STREAMLINK_QUALITY),
        help="Streamlink quality selector.",
    )
    parser.add_argument(
        "--deepgram-model",
        default=os.environ.get("DEEPGRAM_MODEL", DEFAULT_DEEPGRAM_MODEL),
        help="Deepgram transcription model.",
    )
    parser.add_argument(
        "--no-llm",
        action="store_true",
        default=False,
        help="Skip LLM clip scoring after transcription (also via NO_LLM=1).",
    )
    parser.add_argument(
        "--no-shots",
        action="store_true",
        default=False,
        help="Skip TransNetV2 shot detection: no scene cuts in editor prompts and no "
        "cut-aligned boundary snapping (also via NO_SHOTS=1).",
    )
    parser.add_argument(
        "--no-reframe",
        action="store_true",
        default=False,
        help="Short mode: skip the auto-reframe pass that renders a blur-pad 9:16 "
        "vertical next to each clip (also via NO_REFRAME=1).",
    )
    parser.add_argument(
        "--reframe-interval",
        type=float,
        default=float_env("REFRAME_INTERVAL", DEFAULT_REFRAME_FRAME_INTERVAL_SECONDS),
        help="Seconds between the frames sampled for the auto-reframe framing call; "
        "0 samples only the opener and scene cuts (also via REFRAME_INTERVAL).",
    )
    parser.add_argument(
        "--no-captions",
        action="store_true",
        default=False,
        help="Short mode: skip the burned-caption render of each vertical clip "
        "(also via NO_CAPTIONS=1).",
    )
    parser.add_argument(
        "--no-thumbnails",
        action="store_true",
        default=False,
        help="Long mode: skip generated thumbnail concepts for the finished video "
        "(also via NO_THUMBNAILS=1).",
    )
    parser.add_argument(
        "--llm-reasoning-effort",
        default=os.environ.get("OPENROUTER_REASONING_EFFORT", DEFAULT_LLM_REASONING_EFFORT),
        help="OpenRouter reasoning effort for clip scoring.",
    )
    parser.add_argument(
        "--llm-marker-seconds",
        type=int,
        default=int_env("LLM_MARKER_SECONDS", DEFAULT_LLM_MARKER_SECONDS),
        help="Spacing between transcript timestamp marker snippets sent to the LLM.",
    )
    parser.add_argument(
        "--llm-concurrency",
        type=int,
        default=int_env("LLM_CONCURRENCY", DEFAULT_LLM_CONCURRENCY),
        help="How many chunk-scoring LLM calls may run concurrently (also via LLM_CONCURRENCY).",
    )
    parser.add_argument(
        "--llm-context-seconds",
        type=int,
        default=int_env("LLM_CONTEXT_SECONDS", DEFAULT_LLM_CONTEXT_SECONDS),
        help="Seconds of neighboring-chunk transcript shown to the LLM on each side of a "
        "chunk; clips may extend into this margin and are stitched across boundaries "
        "(also via LLM_CONTEXT_SECONDS).",
    )
    return parser.parse_args()


def _truthy_env(key: str) -> bool:
    return os.environ.get(key, "").lower() in ("1", "true", "yes")


if __name__ == "__main__":
    main()
