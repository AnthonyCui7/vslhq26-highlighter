#!/usr/bin/env python3
"""Measure audio+transcript payload size for future clipper LLM audio input.

This intentionally does not call the clipper LLM. It answers one question:
if we add audio to the clip-decision payload as base64 JSON, which durations
and audio presets stay under a target token budget?
"""

from __future__ import annotations

import argparse
import base64
import csv
import json
import math
import os
import shutil
import subprocess
import sys
import time
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from highlighter_pipeline.config import load_env  # noqa: E402
from highlighter_pipeline.deepgram import transcribe_audio_file  # noqa: E402
from highlighter_pipeline.llm import SHORTFORM_SYSTEM_PROMPT as SYSTEM_PROMPT  # noqa: E402


PRESETS = [
    {
        "name": "opus_6k",
        "mime_type": "audio/webm; codecs=opus",
        "extension": ".webm",
        "ffmpeg_args": ["-ac", "1", "-ar", "16000", "-c:a", "libopus", "-b:a", "6k"],
    },
    {
        "name": "opus_8k",
        "mime_type": "audio/webm; codecs=opus",
        "extension": ".webm",
        "ffmpeg_args": ["-ac", "1", "-ar", "16000", "-c:a", "libopus", "-b:a", "8k"],
    },
    {
        "name": "opus_12k",
        "mime_type": "audio/webm; codecs=opus",
        "extension": ".webm",
        "ffmpeg_args": ["-ac", "1", "-ar", "16000", "-c:a", "libopus", "-b:a", "12k"],
    },
    {
        "name": "opus_16k",
        "mime_type": "audio/webm; codecs=opus",
        "extension": ".webm",
        "ffmpeg_args": ["-ac", "1", "-ar", "16000", "-c:a", "libopus", "-b:a", "16k"],
    },
    {
        "name": "opus_24k",
        "mime_type": "audio/webm; codecs=opus",
        "extension": ".webm",
        "ffmpeg_args": ["-ac", "1", "-ar", "16000", "-c:a", "libopus", "-b:a", "24k"],
    },
    {
        "name": "aac_32k",
        "mime_type": "audio/mp4",
        "extension": ".m4a",
        "ffmpeg_args": ["-ac", "1", "-ar", "16000", "-c:a", "aac", "-b:a", "32k"],
    },
    {
        "name": "mp3_32k",
        "mime_type": "audio/mpeg",
        "extension": ".mp3",
        "ffmpeg_args": ["-ac", "1", "-ar", "16000", "-c:a", "libmp3lame", "-b:a", "32k"],
    },
    {
        "name": "wav_16k_pcm",
        "mime_type": "audio/wav",
        "extension": ".wav",
        "ffmpeg_args": ["-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le"],
    },
]


def main() -> None:
    os.chdir(ROOT)
    load_env()
    args = parse_args()
    require_binary("streamlink")
    require_binary("ffmpeg")

    clip_lengths = parse_int_list(args.clip_lengths)
    if not clip_lengths:
        raise RuntimeError("At least one clip length is required")

    run_dir = args.out_dir / time.strftime("audio-budget-%Y%m%d-%H%M%S")
    run_dir.mkdir(parents=True, exist_ok=True)

    visible_start = max(0, args.start_seconds - args.overlay_seconds)
    max_visible_end = args.start_seconds + max(clip_lengths) + args.overlay_seconds
    master_duration = max_visible_end - visible_start
    master_wav = run_dir / "master.wav"

    print(
        f"Extracting {master_duration:.1f}s from {args.url} at source offset "
        f"{visible_start:.1f}s"
    )
    extract_master_wav(
        url=args.url,
        quality=args.streamlink_quality,
        start_seconds=visible_start,
        duration_seconds=master_duration,
        output_path=master_wav,
    )

    transcript = {"transcript": "", "words": [], "metadata": {}}
    if not args.no_transcript:
        print("Transcribing master audio with Deepgram")
        transcript = transcribe_audio_file(master_wav)
        (run_dir / "deepgram.json").write_text(json.dumps(transcript, indent=2))

    results = []
    for clip_length in clip_lengths:
        window_duration = clip_length + (2 * args.overlay_seconds)
        window_wav = run_dir / f"window_{clip_length}s_plus_overlay.wav"
        run_ffmpeg(
            [
                "ffmpeg",
                "-hide_banner",
                "-loglevel",
                "error",
                "-y",
                "-i",
                str(master_wav),
                "-t",
                f"{window_duration:.3f}",
                "-ac",
                "1",
                "-ar",
                "16000",
                "-c:a",
                "pcm_s16le",
                str(window_wav),
            ]
        )

        window_words = words_for_window(
            transcript.get("words", []),
            visible_start_seconds=visible_start,
            window_duration_seconds=window_duration,
        )
        window_transcript = " ".join(
            str(word.get("punctuated_word") or word.get("word") or "").strip()
            for word in window_words
        ).strip()
        markers = timestamp_markers(window_words, marker_seconds=args.marker_seconds)

        for preset in PRESETS:
            audio_path = run_dir / f"clip_{clip_length}s_{preset['name']}{preset['extension']}"
            encode_audio(window_wav, audio_path, preset["ffmpeg_args"])
            audio_bytes = audio_path.read_bytes()
            audio_b64 = base64.b64encode(audio_bytes).decode("ascii")
            payload = build_payload(
                clip_length_seconds=clip_length,
                overlay_seconds=args.overlay_seconds,
                visible_start_seconds=visible_start,
                visible_end_seconds=visible_start + window_duration,
                transcript=window_transcript,
                words=window_words,
                timestamp_markers=markers,
                audio_b64=audio_b64,
                audio_mime_type=preset["mime_type"],
                audio_preset=preset["name"],
            )
            payload_json = json.dumps(payload, separators=(",", ":"))
            exact_tokens = token_count(payload_json)
            estimated_tokens = exact_tokens or estimate_text_payload_tokens(
                payload_json_chars=len(payload_json),
                base64_chars=len(audio_b64),
            )
            results.append(
                {
                    "clip_seconds": clip_length,
                    "audio_window_seconds": window_duration,
                    "overlay_each_side_seconds": args.overlay_seconds,
                    "preset": preset["name"],
                    "mime_type": preset["mime_type"],
                    "audio_bytes": len(audio_bytes),
                    "audio_kbps": round((len(audio_bytes) * 8) / window_duration / 1000, 2),
                    "base64_chars": len(audio_b64),
                    "payload_chars": len(payload_json),
                    "transcript_chars": len(window_transcript),
                    "word_count": len(window_words),
                    "token_count": exact_tokens,
                    "estimated_tokens": estimated_tokens,
                    "under_budget": estimated_tokens < args.token_budget,
                }
            )

    write_results(run_dir, results)
    print_table(results, token_budget=args.token_budget)
    print(f"\nWrote results to {run_dir}")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Extract a Twitch VOD audio window and estimate audio+transcript LLM payload size.",
    )
    parser.add_argument("url", help="Twitch VOD URL")
    parser.add_argument(
        "--start-seconds",
        type=float,
        default=300.0,
        help="Nominal clip start in the source VOD. Overlay is added around this.",
    )
    parser.add_argument(
        "--clip-lengths",
        default="60,90,120,180",
        help="Comma-separated clip lengths to test, before overlay.",
    )
    parser.add_argument(
        "--overlay-seconds",
        type=int,
        default=15,
        help="Audio/transcript context added before and after each clip length.",
    )
    parser.add_argument(
        "--marker-seconds",
        type=int,
        default=15,
        help="Timestamp marker spacing in the synthetic payload.",
    )
    parser.add_argument(
        "--token-budget",
        type=int,
        default=100_000,
        help="Budget threshold to check.",
    )
    parser.add_argument(
        "--streamlink-quality",
        default="best",
        help="Streamlink quality selector.",
    )
    parser.add_argument(
        "--out-dir",
        type=Path,
        default=Path("data") / "audio-token-budget",
        help="Directory for extracted audio and results.",
    )
    parser.add_argument(
        "--no-transcript",
        action="store_true",
        help="Skip Deepgram and measure audio-only payload overhead.",
    )
    return parser.parse_args()


def parse_int_list(value: str) -> list[int]:
    return [int(part.strip()) for part in value.split(",") if part.strip()]


def require_binary(name: str) -> None:
    if shutil.which(name) is None:
        raise RuntimeError(f"Missing required binary: {name}")


def extract_master_wav(
    *,
    url: str,
    quality: str,
    start_seconds: float,
    duration_seconds: float,
    output_path: Path,
) -> None:
    streamlink = subprocess.Popen(
        [
            "streamlink",
            "--stdout",
            "--hls-start-offset",
            f"{start_seconds:.3f}",
            url,
            quality,
        ],
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )
    try:
        ffmpeg = subprocess.run(
            [
                "ffmpeg",
                "-hide_banner",
                "-loglevel",
                "error",
                "-y",
                "-i",
                "pipe:0",
                "-t",
                f"{duration_seconds:.3f}",
                "-vn",
                "-ac",
                "1",
                "-ar",
                "16000",
                "-c:a",
                "pcm_s16le",
                str(output_path),
            ],
            stdin=streamlink.stdout,
            capture_output=True,
            text=True,
        )
        if streamlink.stdout:
            streamlink.stdout.close()
        if ffmpeg.returncode != 0:
            _, streamlink_stderr = streamlink.communicate(timeout=5)
            raise RuntimeError(
                "ffmpeg failed while extracting master audio:\n"
                f"{ffmpeg.stderr.strip()}\n{streamlink_stderr.decode('utf-8', errors='replace').strip()}"
            )
    finally:
        if streamlink.poll() is None:
            streamlink.terminate()
            try:
                streamlink.wait(timeout=5)
            except subprocess.TimeoutExpired:
                streamlink.kill()


def run_ffmpeg(command: list[str]) -> None:
    result = subprocess.run(command, capture_output=True, text=True)
    if result.returncode != 0:
        raise RuntimeError(result.stderr.strip() or result.stdout.strip() or "ffmpeg failed")


def encode_audio(source_wav: Path, output_path: Path, ffmpeg_args: list[str]) -> None:
    run_ffmpeg(
        [
            "ffmpeg",
            "-hide_banner",
            "-loglevel",
            "error",
            "-y",
            "-i",
            str(source_wav),
            *ffmpeg_args,
            str(output_path),
        ]
    )


def words_for_window(
    words: list[dict[str, Any]],
    *,
    visible_start_seconds: float,
    window_duration_seconds: float,
) -> list[dict[str, Any]]:
    output = []
    for word in words:
        start = float(word.get("start", 0))
        end = float(word.get("end", start))
        if start >= window_duration_seconds:
            continue
        item = dict(word)
        item["absolute_start"] = round(visible_start_seconds + start, 3)
        item["absolute_end"] = round(visible_start_seconds + end, 3)
        output.append(item)
    return output


def timestamp_markers(words: list[dict[str, Any]], *, marker_seconds: int) -> str:
    if not words:
        return ""
    start = int(float(words[0].get("absolute_start", 0)))
    end = int(math.ceil(float(words[-1].get("absolute_end", start))))
    lines = []
    current = start
    while current <= end:
        next_mark = current + marker_seconds
        snippet_words = [
            str(word.get("punctuated_word") or word.get("word") or "").strip()
            for word in words
            if current <= float(word.get("absolute_start", 0)) < next_mark
        ]
        snippet = " ".join(part for part in snippet_words if part).strip()
        if snippet:
            lines.append(f"{current}s: {snippet[:240]}")
        current = next_mark
    return "\n".join(lines)


def build_payload(
    *,
    clip_length_seconds: int,
    overlay_seconds: int,
    visible_start_seconds: float,
    visible_end_seconds: float,
    transcript: str,
    words: list[dict[str, Any]],
    timestamp_markers: str,
    audio_b64: str,
    audio_mime_type: str,
    audio_preset: str,
) -> dict[str, Any]:
    return {
        "task": "clip_decision_with_audio_budget_test",
        "system_prompt": SYSTEM_PROMPT,
        "clip_window": {
            "clip_length_seconds": clip_length_seconds,
            "overlay_each_side_seconds": overlay_seconds,
            "visible_start_seconds": visible_start_seconds,
            "visible_end_seconds": visible_end_seconds,
        },
        "chunk": {
            "transcript": transcript,
            "words": words,
            "timestamp_markers": timestamp_markers,
        },
        "audio": {
            "preset": audio_preset,
            "mime_type": audio_mime_type,
            "encoding": "base64",
            "data": audio_b64,
        },
    }


def token_count(text: str) -> int | None:
    try:
        import tiktoken  # type: ignore
    except Exception:
        return None

    try:
        encoder = tiktoken.get_encoding("o200k_base")
    except Exception:
        encoder = tiktoken.get_encoding("cl100k_base")
    return len(encoder.encode(text))


def estimate_text_payload_tokens(*, payload_json_chars: int, base64_chars: int) -> int:
    non_base64_chars = max(0, payload_json_chars - base64_chars)
    # Natural language JSON averages closer to chars/4. Base64 is much denser
    # for tokenizers, so keep that estimate intentionally conservative.
    return math.ceil((non_base64_chars / 4) + (base64_chars * 0.75))


def write_results(run_dir: Path, results: list[dict[str, Any]]) -> None:
    (run_dir / "results.json").write_text(json.dumps(results, indent=2))
    with (run_dir / "results.csv").open("w", newline="") as file:
        writer = csv.DictWriter(file, fieldnames=list(results[0].keys()))
        writer.writeheader()
        writer.writerows(results)


def print_table(results: list[dict[str, Any]], *, token_budget: int) -> None:
    headers = [
        "clip",
        "audio_window",
        "preset",
        "audio_kbps",
        "audio_bytes",
        "payload_chars",
        "tokens",
        f"<{token_budget}",
    ]
    rows = []
    for result in sorted(results, key=lambda row: (row["clip_seconds"], row["estimated_tokens"])):
        rows.append(
            [
                str(result["clip_seconds"]),
                str(result["audio_window_seconds"]),
                result["preset"],
                str(result["audio_kbps"]),
                str(result["audio_bytes"]),
                str(result["payload_chars"]),
                str(result["token_count"] or result["estimated_tokens"]),
                "yes" if result["under_budget"] else "no",
            ]
        )
    widths = [
        max(len(header), *(len(row[index]) for row in rows))
        for index, header in enumerate(headers)
    ]
    print()
    print(" | ".join(header.ljust(widths[index]) for index, header in enumerate(headers)))
    print("-+-".join("-" * width for width in widths))
    for row in rows:
        print(" | ".join(value.ljust(widths[index]) for index, value in enumerate(row)))


if __name__ == "__main__":
    main()
