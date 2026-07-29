"""Run the Highlighter pipeline against a livestream or VOD.

Short mode (default): capture -> transcribe -> detect clip-worthy moments ->
render individual short-form clips. Long mode: same pipeline with a long-form
editor prompt, then the selected segments are stitched chronologically into one
long-form video.

Everything is recorded locally under <output-root>/projects/<project-id>/ in
the same shape Supabase stores it (see records.py); Supabase writes happen too
unless --local-only is passed.
"""

import argparse
import json
import os
import signal
import time
import uuid
from pathlib import Path
from urllib.parse import parse_qs, urlparse

from .capture import assert_capture_prerequisites, capture_audio_chunks
from .config import float_env, int_env, load_env
from .cookies import prepare_ytdlp_cookies
from .defaults import (
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
    DEFAULT_OPENROUTER_MODEL,
    DEFAULT_OUTPUT_ROOT,
    DEFAULT_STREAM_URL,
    DEFAULT_STREAMLINK_QUALITY,
    DEFAULT_SUPABASE_CLIPS_BUCKET,
    DEFAULT_TARGET_LENGTH_MINUTES,
)
from .deepgram import transcribe_audio_file
from .llm import detect_clip_candidates
from .records import ProjectRecords
from .render import (
    clip_filename,
    extract_thumbnail,
    render_clip_from_segments,
)
from .research import research_content_context
from .scoring import ClipScoringCoordinator
from .stitch import stitch_clips
from .storage import storage_from_env
from .supabase_client import SupabaseClient


# How often the worker re-reads its project row while capturing, to notice a
# backend-requested cancellation (status 'stopping').
PROJECT_STATUS_POLL_SECONDS = 10


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
    llm_model = DEFAULT_OPENROUTER_MODEL
    llm_reasoning_effort = args.llm_reasoning_effort
    llm_marker_seconds = args.llm_marker_seconds
    llm_concurrency = args.llm_concurrency
    llm_context_seconds = args.llm_context_seconds
    research_enabled = llm_enabled and not args.no_research and not _truthy_env("NO_RESEARCH")
    local_only = args.local_only or _truthy_env("LOCAL_ONLY")
    clips_bucket = os.environ.get("SUPABASE_CLIPS_BUCKET", DEFAULT_SUPABASE_CLIPS_BUCKET)
    output_root = Path(args.output_root or os.environ.get("OUTPUT_ROOT", DEFAULT_OUTPUT_ROOT))
    min_clip_score = args.min_clip_score  # None -> resolved from the project row below

    if pipeline_mode == "long":
        merge_gap_seconds = DEFAULT_LONGFORM_MERGE_GAP_SECONDS
        max_clip_seconds = DEFAULT_LONGFORM_MAX_CLIP_SECONDS
    else:
        merge_gap_seconds = DEFAULT_CLIP_MERGE_GAP_SECONDS
        max_clip_seconds = DEFAULT_MAX_CLIP_SECONDS

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

        ingest_settings = {
            "pipeline": pipeline_mode,
            "user_instructions": user_instructions,
            "target_length_minutes": target_length if pipeline_mode == "long" else None,
            "chunk_seconds": chunk_seconds,
            "max_chunks": max_chunks,
            "streamlink_quality": streamlink_quality,
            "deepgram_model": deepgram_model,
            "min_clip_score": min_clip_score,
            "clip_stitching": {
                "merge_gap_seconds": merge_gap_seconds,
                "max_clip_seconds": max_clip_seconds,
            },
            "llm": {
                "enabled": llm_enabled,
                "backend": "openrouter-gemini-audio",
                "model": llm_model,
                "reasoning_effort": llm_reasoning_effort,
                "marker_seconds": llm_marker_seconds,
                "concurrency": llm_concurrency,
                "context_seconds": llm_context_seconds,
            },
            "research": {
                "enabled": research_enabled,
                "backend": "openrouter-web-search" if research_enabled else None,
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
        # Emitter-thread state (see ClipScoringCoordinator): local archive
        # segments still on disk, and rendered-decisions parked until the
        # segment(s) their window needs have been archived.
        segment_files: dict[int, Path] = {}
        parked_renders: dict[int, list[dict]] = {}
        rendered_log: list[dict] = []
        source_context = {
            "platform": platform,
            "source_type": source_type,
            "name": name,
            "source_url": source_url,
        }

        content_research_context = None
        if research_enabled:
            print("Researching content context (single web-grounded agent)")
            try:
                content_research_context = research_content_context(
                    source_context=source_context,
                    pipeline_mode=pipeline_mode,
                    user_instructions=user_instructions,
                )
                records.write_research(content_research_context)
                print(
                    "Cached content research "
                    f"with {len(content_research_context.get('sources', []))} source(s)"
                )
            except Exception as exc:
                # Research is context, not a dependency; never kill a run on it.
                print(f"Content research failed (continuing without it): {exc}")

        def _emit_decision(decision: dict) -> None:
            """Handle one stitched decision, in chunk order (emitter thread)."""
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
                )
                return

            if archive_dir is None:
                _store_clip_decision(
                    db=db,
                    records=records,
                    project_id=project_id,
                    decision=decision,
                    render_result={
                        "status": "failed",
                        "error": "Clip rendering requires source archiving (run without --no-archive).",
                    },
                )
                return
            _try_render_from_segments(decision)

        def _needed_segments(decision: dict) -> list[int]:
            """Archive segment indexes a clip window spans (end is exclusive
            when it lands exactly on a segment boundary)."""
            first = int(decision["start_seconds"] // chunk_seconds)
            last = int(
                max(decision["start_seconds"], decision["end_seconds"] - 0.001)
                // chunk_seconds
            )
            return list(range(first, last + 1))

        def _try_render_from_segments(decision: dict) -> None:
            """Render now when every needed segment is archived; park otherwise
            (emitter thread)."""
            needed = _needed_segments(decision)
            missing = [index for index in needed if index not in segment_files]
            if missing:
                parked_renders.setdefault(max(missing), []).append(decision)
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
                first_segment_start_seconds=needed[0] * chunk_seconds,
                rendered_log=rendered_log,
            )

        def _register_segment(segment_index: int, segment_path: Path) -> None:
            """Record a completed archive segment and wake parked renders
            (emitter thread)."""
            segment_files[segment_index] = segment_path
            for key in sorted(k for k in parked_renders if k <= segment_index):
                for decision in parked_renders.pop(key):
                    _try_render_from_segments(decision)

        def _retire_stale_segments(chunk_index: int, min_held_start: float | None) -> None:
            """Delete local segment copies that no held or parked decision can
            reference anymore (emitter thread)."""
            if archive_dir is None:
                return
            bound = chunk_index - 1
            if min_held_start is not None:
                bound = min(bound, int(min_held_start // chunk_seconds) - 1)
            for decisions in parked_renders.values():
                for decision in decisions:
                    bound = min(bound, int(decision["start_seconds"] // chunk_seconds) - 1)
            for index in [i for i in segment_files if i <= bound]:
                path = segment_files.pop(index)
                try:
                    path.unlink(missing_ok=True)
                except OSError as exc:
                    print(f"Could not delete local segment {path}: {exc}")

        def _finish_live_renders() -> None:
            """Fail parked decisions whose segments never arrived and drop the
            remaining local segment copies (emitter thread, once at the end)."""
            for key in sorted(parked_renders):
                for decision in parked_renders[key]:
                    _store_clip_decision(
                        db=db,
                        records=records,
                        project_id=project_id,
                        decision=decision,
                        render_result={
                            "status": "failed",
                            "error": "No archived video segment was produced for this clip.",
                        },
                    )
            parked_renders.clear()
            for index in list(segment_files):
                path = segment_files.pop(index)
                try:
                    path.unlink(missing_ok=True)
                except OSError as exc:
                    print(f"Could not delete local segment {path}: {exc}")

        scoring_backend = f"OpenRouter BYOK Gemini audio ({llm_model})"

        def _score_chunk(
            chunk: dict,
            context_before: list[dict],
            context_after: list[dict],
            visible_start: int,
            visible_end: int,
        ) -> tuple[list[dict], str]:
            """Score one chunk via the LLM (runs on coordinator pool threads)."""
            print(
                f"Scoring chunk {chunk['chunk_index']} for clip candidates "
                f"via {scoring_backend}"
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
                model=llm_model,
                reasoning_effort=llm_reasoning_effort,
                marker_seconds=llm_marker_seconds,
                pipeline_mode=pipeline_mode,
                user_instructions=user_instructions,
                target_length=target_length,
                source_context=source_context,
                research_context=content_research_context,
                audio_context=chunk.get("audio_context") or [],
            )

        coordinator = None
        if llm_enabled:
            coordinator = ClipScoringCoordinator(
                score_chunk=_score_chunk,
                emit_decision=_emit_decision,
                chunk_seconds=chunk_seconds,
                context_seconds=llm_context_seconds,
                concurrency=llm_concurrency,
                merge_gap_seconds=merge_gap_seconds,
                max_clip_seconds=max_clip_seconds,
                on_chunk_complete=_retire_stale_segments,
                on_finish=_finish_live_renders,
            )

        def _handle_video_segment(segment_path: Path, segment_index: int) -> None:
            if storage is not None:
                key = f"{archive_prefix}/{segment_path.name}"
                storage.upload_file(segment_path, key)
                archived_segments.append(key)
                print(
                    f"Archived video segment {segment_index} to s3://{storage.bucket}/{key}"
                )
            if coordinator is not None:
                # The local copy stays until nothing can reference it; the
                # emitter renders clips from these files.
                coordinator.run_on_emitter(
                    lambda: _register_segment(segment_index, segment_path)
                )
            else:
                segment_path.unlink()

        longform_result: dict = {}

        def _final_metadata(**extra: object) -> dict:
            metadata: dict = {"platform": platform, "ingest": ingest_settings, **extra}
            if content_research_context is not None:
                metadata["content_research"] = content_research_context
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

        print(
            f"Capturing {source_url} ({platform} {source_type}, via {downloader}) in {chunk_seconds}s chunks"
            f"{f' ({max_chunks} chunk max)' if max_chunks > 0 else ''}"
            f" for a {pipeline_mode}-form edit"
            f"{f'; scoring clips via OpenRouter BYOK Gemini audio ({llm_model})' if llm_enabled else '; LLM scoring disabled'}"
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
                coordinator=coordinator,
            ),
            archive_dir=archive_dir,
            on_video_segment=_handle_video_segment if archive_dir is not None else None,
            should_stop=_stop_requested,
        )
        if coordinator is not None:
            # Drains in-flight scoring, stitches, renders, and cleans up; on a
            # cancel nothing new is dispatched but finished scores still land.
            coordinator.finish(cancelled=stop_state["requested"])

        if pipeline_mode == "long" and not stop_state["requested"]:
            longform_result.update(
                _stitch_longform(
                    db=db,
                    records=records,
                    project_id=project_id,
                    project_dir=project_dir,
                    clips_bucket=clips_bucket,
                    rendered_log=rendered_log,
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
        if coordinator is not None:
            # Best-effort teardown; it must never mask the capture failure.
            try:
                coordinator.finish(cancelled=True)
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


def _stitch_longform(
    *,
    db: SupabaseClient | None,
    records: ProjectRecords,
    project_id: str,
    project_dir: Path,
    clips_bucket: str,
    rendered_log: list[dict],
) -> dict:
    """Concatenate the rendered segments chronologically into one long-form
    video, persist it like a clip, and return its metadata (or a failure/empty
    record — stitching problems must not fail the whole run)."""
    if not rendered_log:
        print("No rendered segments to stitch into a long-form video.")
        return {"status": "empty", "segments": 0}

    ordered = sorted(rendered_log, key=lambda clip: clip["start_seconds"])
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

    thumbnail_path = None
    try:
        thumbnail_path = output_path.with_suffix(".jpg")
        extract_thumbnail(
            clip_path=output_path, output_path=thumbnail_path, at_seconds=total_seconds / 2
        )
    except Exception as exc:
        thumbnail_path = None
        print(f"Long-form thumbnail extraction failed (non-fatal): {exc}")

    title = f"Long-form edit ({len(ordered)} segments, {total_seconds / 60:.0f} min)"
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
                    result["thumbnail_url"] = db.upload_storage_object(
                        bucket=clips_bucket,
                        key=f"projects/{project_id}/longform/{thumbnail_path.name}",
                        path=thumbnail_path,
                    )
                except Exception as exc:
                    print(f"Long-form thumbnail upload failed (non-fatal): {exc}")
            db.insert_clip(
                project_id=project_id,
                title=title,
                description=f"Stitched long-form edit assembled from {len(ordered)} segments.",
                start_seconds=ordered[0]["start_seconds"],
                end_seconds=ordered[-1]["end_seconds"],
                video_url=video_url,
                status="rendered",
                metadata={"source": "longform_stitch", "render": result},
            )
        except Exception as exc:
            print(f"Long-form upload failed (kept locally at {output_path}): {exc}")
            result["upload_error"] = str(exc)

    records.append_clip(
        {
            "project_id": project_id,
            "title": title,
            "description": f"Stitched long-form edit assembled from {len(ordered)} segments.",
            "start_seconds": ordered[0]["start_seconds"],
            "end_seconds": ordered[-1]["end_seconds"],
            "video_url": result.get("video_url") or result["local_path"],
            "status": "rendered",
            "metadata": {"source": "longform_stitch", "render": result},
        }
    )
    print(f"Long-form video ready: {output_path}")
    return result


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
) -> dict:
    print(f"Transcribing chunk {chunk_index} ({start_seconds}s-{end_seconds}s)")
    transcript = transcribe_audio_file(audio_path, model=deepgram_model)
    words = [_with_absolute_times(word, start_seconds) for word in transcript["words"]]
    metadata = {
        "audio_path": os.path.relpath(audio_path),
        "deepgram_request_id": transcript["metadata"].get("request_id"),
        "deepgram": transcript["metadata"],
    }

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
    coordinator: ClipScoringCoordinator | None,
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
    )
    if coordinator is None:
        return
    # Scoring happens on the coordinator's pool: this chunk is dispatched once
    # the NEXT chunk's transcript arrives, so the model sees both boundaries.
    coordinator.add_chunk(
        {
            "chunk_index": chunk_index,
            "start_seconds": start_seconds,
            "end_seconds": end_seconds,
            "transcript": stored["transcript"],
            "words": stored["words"],
            "audio_path": str(audio_path),
        }
    )


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
) -> None:
    filename = clip_filename(
        chunk_index=decision["chunk_index"],
        start_seconds=decision["start_seconds"],
        end_seconds=decision["end_seconds"],
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
                    render_result["thumbnail_url"] = db.upload_storage_object(
                        bucket=clips_bucket,
                        key=f"projects/{project_id}/clips/{thumbnail_path.name}",
                        path=thumbnail_path,
                    )
                except Exception as exc:
                    print(f"Thumbnail upload failed (non-fatal): {exc}")

        rendered_log.append(
            {
                "path": str(output_path),
                "chunk_index": decision["chunk_index"],
                "title": decision["title"],
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
        render_result=render_result,
    )


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
    render_result: dict | None = None,
) -> None:
    if render_result is not None:
        decision = {**decision, "render": render_result}

    records.append_decision(decision)

    if not decision["is_clip_worthy"]:
        print(f"No clip candidate for chunk {decision['chunk_index']}: {decision['reason'][:120]}")
        return

    clip_status = "detected"
    video_url = None
    if render_result is not None:
        clip_status = render_result.get("status", "detected")
        video_url = render_result.get("video_url") or render_result.get("local_path")

    clip_metadata = {
        "source": "llm",
        "chunk_index": decision["chunk_index"],
        "model": decision["model"],
        "reasoning_effort": decision["reasoning_effort"],
        "reason": decision["reason"],
        "research_sources": decision.get("research_sources", []),
        "raw_decision": decision["raw_decision"],
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
        choices=["short", "long"],
        default=os.environ.get("PIPELINE") or "short",
        help="short: independent short-form clips. long: long-form segment selection "
        "stitched into one video (also via PIPELINE).",
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
