"""Burned-in captions for short-form vertical clips.

pycaps renders styled word-timed captions onto a clip. The pipeline already
has word timings from transcription, so the transcript is handed over in
Whisper's JSON shape and pycaps never runs its own speech-to-text. pycaps is
an external CLI like ffmpeg: when it isn't installed the pipeline skips
captions with a log line and ships the clean vertical only.
"""

import json
import shutil
import subprocess
import tempfile
from pathlib import Path

from .defaults import DEFAULT_CAPTION_TEMPLATE

# A silence this long starts a new caption segment.
CAPTION_SEGMENT_GAP_SECONDS = 1.5

# Caption styles shipped with the pipeline; a template name that matches a
# directory here resolves to it, anything else passes through to pycaps.
CAPTION_TEMPLATES_DIR = Path(__file__).parent / "caption_templates"


def resolve_caption_template(template: str) -> str:
    packaged = CAPTION_TEMPLATES_DIR / template
    return str(packaged.resolve()) if packaged.is_dir() else template


def captions_available() -> bool:
    return shutil.which("pycaps") is not None


def build_whisper_transcript(
    *,
    words: list[dict],
    clip_start_seconds: float,
    clip_end_seconds: float,
) -> dict | None:
    """Map word timings onto Whisper's segments[].words[] JSON shape, with
    times relative to the clip. Clips are cut at their measured source
    positions, so the word clock and the rendered video agree as-is.
    Returns None when no words fall in the clip."""
    clip_duration = max(0.0, clip_end_seconds - clip_start_seconds)
    clip_words = []
    for word in words:
        start = word.get("absolute_start", word.get("start"))
        end = word.get("absolute_end", word.get("end"))
        if start is None or end is None or end <= clip_start_seconds:
            continue
        if start >= clip_end_seconds:
            continue
        relative_start = start - clip_start_seconds
        relative_end = end - clip_start_seconds
        if relative_end <= 0.0 or relative_start >= clip_duration:
            continue
        clip_words.append(
            {
                "word": str(word.get("punctuated_word") or word.get("word") or ""),
                "start": round(max(0.0, relative_start), 3),
                "end": round(min(clip_duration, relative_end), 3),
            }
        )
    clip_words = [word for word in clip_words if word["word"]]
    if not clip_words:
        return None

    segments = []
    current: list[dict] = []
    for word in clip_words:
        if current and word["start"] - current[-1]["end"] > CAPTION_SEGMENT_GAP_SECONDS:
            segments.append(current)
            current = []
        current.append(word)
    segments.append(current)

    return {
        "segments": [
            {
                "id": index,
                "start": segment[0]["start"],
                "end": segment[-1]["end"],
                "text": " ".join(word["word"] for word in segment),
                "words": segment,
            }
            for index, segment in enumerate(segments)
        ]
    }


def caption_clip(
    *,
    vertical_path: Path,
    words: list[dict],
    clip_start_seconds: float,
    clip_end_seconds: float,
    output_path: Path,
    template: str = DEFAULT_CAPTION_TEMPLATE,
) -> Path | None:
    """Render a captioned copy of a vertical clip. Best-effort: any failure
    logs one line and returns None so the clean vertical still ships."""
    try:
        transcript = build_whisper_transcript(
            words=words,
            clip_start_seconds=clip_start_seconds,
            clip_end_seconds=clip_end_seconds,
        )
        if transcript is None:
            return None
        with tempfile.NamedTemporaryFile(
            "w", suffix=".json", prefix="captions-", delete=False
        ) as handle:
            json.dump(transcript, handle)
            transcript_path = Path(handle.name)
        try:
            _run(
                [
                    "pycaps",
                    "render",
                    "--input",
                    str(vertical_path),
                    "--output",
                    str(output_path),
                    "--template",
                    resolve_caption_template(template),
                    "--transcript",
                    str(transcript_path),
                    "--transcript-format",
                    "whisper_json",
                ]
            )
        finally:
            transcript_path.unlink(missing_ok=True)
        if not output_path.exists():
            raise RuntimeError("pycaps produced no output file")
        return output_path
    except Exception as exc:
        print(f"Caption render failed (non-fatal): {exc}")
        return None


def _run(command: list[str]) -> None:
    result = subprocess.run(command, capture_output=True, text=True)
    if result.returncode != 0:
        details = (result.stderr or result.stdout).strip()
        raise RuntimeError(details or f"Command failed with exit code {result.returncode}")
