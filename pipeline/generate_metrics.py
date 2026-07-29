#!/usr/bin/env python3
from __future__ import annotations

import json
import statistics
import sys
import time
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT))


def read_jsonl(path: Path) -> list[dict[str, Any]]:
    if not path.exists():
        return []
    rows: list[dict[str, Any]] = []
    with path.open("r", encoding="utf-8", errors="ignore") as handle:
        for line in handle:
            if line.strip():
                rows.append(json.loads(line))
    return rows


def video_duration_seconds(project_dir: Path) -> float:
    chunks = read_jsonl(project_dir / "chunks.jsonl")
    if not chunks:
        return 0.0
    starts = [float(chunk.get("start_seconds") or 0) for chunk in chunks]
    ends = [float(chunk.get("end_seconds") or 0) for chunk in chunks]
    return max(ends) - min(starts)


def file_mtime_span(project_dir: Path) -> tuple[float, float] | None:
    files = [path for path in project_dir.glob("**/*") if path.is_file()]
    if not files:
        return None
    mtimes = [path.stat().st_mtime for path in files]
    return min(mtimes), max(mtimes)


def first_clip_mtime(project_dir: Path) -> float | None:
    clips = sorted((project_dir / "clips").glob("*.mp4"), key=lambda path: path.stat().st_mtime)
    return clips[0].stat().st_mtime if clips else None


def end_to_end_artifact_timing() -> dict[str, Any]:
    runs: list[dict[str, float | str]] = []
    for project_dir in sorted((ROOT / "data/projects").glob("*")):
        if not project_dir.is_dir():
            continue
        duration = video_duration_seconds(project_dir)
        span = file_mtime_span(project_dir)
        first_clip = first_clip_mtime(project_dir)
        if not span or not first_clip or duration <= 0:
            continue
        started, finished = span
        wall_seconds = max(0.0, finished - started)
        if wall_seconds <= 0:
            continue
        runs.append(
            {
                "project_id": project_dir.name,
                "video_minutes": duration / 60,
                "wall_clock_minutes": wall_seconds / 60,
                "minutes_per_video_hour": (wall_seconds / duration) * 60,
                "time_to_first_editable_clip_minutes": max(0.0, first_clip - started) / 60,
            }
        )

    if not runs:
        raise RuntimeError("No completed local runs with rendered clips and timing artifacts found.")

    selected = max(runs, key=lambda run: float(run["video_minutes"]))
    result_minutes_per_hour = float(selected["minutes_per_video_hour"])
    realtime_baseline = 60.0
    return {
        "experiment": "Observed time to process one hour of video into editable clips",
        "baseline": {
            "method": "real-time manual review lower bound",
            "minutes_per_video_hour": realtime_baseline,
        },
        "result": {
            "method": "local completed-run artifact timestamp proxy",
            "minutes_per_video_hour": round(result_minutes_per_hour, 2),
            "time_to_first_editable_clip_minutes": round(
                float(selected["time_to_first_editable_clip_minutes"]),
                2,
            ),
        },
        "delta": {
            "minutes_saved_per_video_hour": round(realtime_baseline - result_minutes_per_hour, 2),
            "speedup_vs_realtime": round(realtime_baseline / result_minutes_per_hour, 2)
            if result_minutes_per_hour
            else None,
        },
        "sample_size": {
            "timed_completed_runs": len(runs),
            "selected_run_video_minutes": round(float(selected["video_minutes"]), 2),
            "timing_source": "local artifact modification times",
        },
        "resume_worthy": False,
    }


def audio_token_reduction() -> dict[str, Any]:
    comparisons: list[dict[str, Any]] = []
    for path in sorted((ROOT / "data/audio-token-budget").glob("*/results.json")):
        rows = json.loads(path.read_text(encoding="utf-8"))
        grouped: dict[tuple[int, int], dict[str, dict[str, Any]]] = {}
        for row in rows:
            key = (int(row["clip_seconds"]), int(row["audio_window_seconds"]))
            grouped.setdefault(key, {})[str(row["preset"])] = row
        for (clip_seconds, window_seconds), presets in grouped.items():
            wav = presets.get("wav_16k_pcm")
            opus = presets.get("opus_6k")
            if wav and opus:
                comparisons.append(
                    {
                        "clip_seconds": clip_seconds,
                        "audio_window_seconds": window_seconds,
                        "wav_tokens": int(wav["estimated_tokens"]),
                        "opus_tokens": int(opus["estimated_tokens"]),
                        "source": str(path.relative_to(ROOT)),
                    }
                )

    if not comparisons:
        raise RuntimeError("No Opus-vs-WAV audio-token comparison artifacts found.")

    selected = next(
        (
            item
            for item in comparisons
            if item["clip_seconds"] == 60 and item["audio_window_seconds"] == 90
        ),
        comparisons[0],
    )
    baseline_tokens = selected["wav_tokens"]
    result_tokens = selected["opus_tokens"]
    reduction = 1 - (result_tokens / baseline_tokens)
    return {
        "experiment": "LLM audio payload token reduction for multimodal clip scoring",
        "baseline": {
            "method": "16 kHz PCM WAV audio in JSON payload",
            "estimated_tokens": baseline_tokens,
        },
        "result": {
            "method": "6 kbps Opus audio in JSON payload",
            "estimated_tokens": result_tokens,
        },
        "delta": {
            "estimated_tokens_saved": baseline_tokens - result_tokens,
            "token_reduction_percent": round(reduction * 100, 2),
        },
        "sample_size": {
            "clip_seconds": selected["clip_seconds"],
            "audio_window_seconds": selected["audio_window_seconds"],
            "source_artifact": selected["source"],
        },
        "resume_worthy": reduction >= 0.80,
    }


def benchmark_scoring_coordinator(concurrency: int) -> dict[str, Any]:
    from highlighter_pipeline.scoring import ClipScoringCoordinator

    emitted: list[dict[str, Any]] = []
    completed: list[int] = []

    def score_chunk(chunk: dict[str, Any], *_args: Any) -> tuple[list[dict[str, Any]], str]:
        index = int(chunk["chunk_index"])
        time.sleep(0.05 + (index % 5) * 0.002)
        return [
            {
                "chunk_index": index,
                "is_clip_worthy": True,
                "title": f"candidate-{index}",
                "description": "controlled scorer latency ablation",
                "start_seconds": float(chunk["start_seconds"]) + 15,
                "end_seconds": float(chunk["start_seconds"]) + 35,
                "score": 0.8,
                "reason": "deterministic candidate",
                "research_sources": [],
            }
        ], "controlled scoring response"

    coordinator = ClipScoringCoordinator(
        score_chunk=score_chunk,
        emit_decision=emitted.append,
        chunk_seconds=90,
        context_seconds=10,
        concurrency=concurrency,
        merge_gap_seconds=0.25,
        max_clip_seconds=60,
        on_chunk_complete=lambda index, _held: completed.append(index),
    )
    chunks = [
        {
            "chunk_index": index,
            "start_seconds": index * 90,
            "end_seconds": (index + 1) * 90,
            "transcript": f"chunk {index} transcript",
            "words": [
                {
                    "word": "clip",
                    "absolute_start": index * 90 + offset,
                    "absolute_end": index * 90 + offset + 0.2,
                }
                for offset in range(0, 90, 9)
            ],
        }
        for index in range(32)
    ]
    start = time.perf_counter()
    for chunk in chunks:
        coordinator.add_chunk(chunk)
    coordinator.finish()
    elapsed = time.perf_counter() - start
    ordered = [decision["chunk_index"] for decision in emitted] == sorted(
        decision["chunk_index"] for decision in emitted
    )
    return {
        "elapsed_seconds": elapsed,
        "emitted_decisions": len(emitted),
        "completed_chunks": len(completed),
        "ordered_emission": ordered,
    }


def scoring_concurrency_latency_reduction() -> dict[str, Any]:
    baseline = benchmark_scoring_coordinator(concurrency=1)
    result = benchmark_scoring_coordinator(concurrency=8)
    speedup = baseline["elapsed_seconds"] / result["elapsed_seconds"]
    reduction = 1 - (result["elapsed_seconds"] / baseline["elapsed_seconds"])
    return {
        "experiment": "Clip scoring coordinator latency ablation",
        "baseline": {
            "method": "single scoring worker through the production coordinator",
            "elapsed_seconds": round(float(baseline["elapsed_seconds"]), 3),
            "ordered_emission": baseline["ordered_emission"],
        },
        "result": {
            "method": "8 scoring workers through the same production coordinator",
            "elapsed_seconds": round(float(result["elapsed_seconds"]), 3),
            "ordered_emission": result["ordered_emission"],
        },
        "delta": {
            "latency_reduction_percent": round(reduction * 100, 2),
            "speedup": round(speedup, 2),
        },
        "sample_size": {
            "scored_chunks": baseline["completed_chunks"],
            "scope": "controlled coordinator ablation with deterministic 50 ms scorer latency; not an end-to-end media run",
        },
        "resume_worthy": False,
    }


def main() -> None:
    print(
        json.dumps(
            {
                "project": "ClipFarm worker",
                "run_at": datetime.now(timezone.utc).isoformat(),
                "experiments": [
                    audio_token_reduction(),
                    scoring_concurrency_latency_reduction(),
                    end_to_end_artifact_timing(),
                ],
            },
            indent=2,
            sort_keys=True,
        )
    )


if __name__ == "__main__":
    main()
