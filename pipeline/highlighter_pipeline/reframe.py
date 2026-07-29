"""Auto-reframe rendered short-form clips to a 9:16 vertical.

One Gemini call per clip decides where the sharp region sits: the model sees a
sampled frame from each shot (plus the clip's opening frame), the scene-cut
timings, and the clip's editorial context, and returns a small set of
horizontal crop centers — a starting framing, then a new center whenever the
speaker or action moves. Spans that cannot be cropped honestly (side-by-side
call layouts, wide action) are flagged wide and show the whole 16:9 frame
fitted to the canvas width instead. Rendering is deterministic: a full-height
square crop at those centers (or the fitted wide frame) fills the width of a
720x1280 canvas, with a blurred, darkened zoom-fill of the same frame above
and below. Framing is static between keyframes — hard cuts, no tracking.
"""

import base64
import json
import os
import subprocess
import tempfile
from pathlib import Path
from typing import Any

from .defaults import DEFAULT_OPENROUTER_MODEL
from .llm import OPENROUTER_BASE_URL, OPENROUTER_VERTEX_PROVIDER, _json_from_text
from .render import extract_thumbnail

CANVAS_WIDTH = 720
CANVAS_HEIGHT = 1280
BLUR_SIGMA = 25
# One frame per shot is plenty; more frames anchor worse, not better.
MAX_SAMPLE_FRAMES = 10
# Crop moves closer together than this read as jitter, not reframing.
MIN_KEYFRAME_SPACING_SECONDS = 1.5
MIN_CENTER_DELTA = 0.03
# Framing is a perceptual where's-the-subject call; deep reasoning only adds
# latency per clip.
REFRAME_REASONING_EFFORT = "low"


REFRAME_SYSTEM_PROMPT = """You are the framing director converting a 16:9 highlight clip into a vertical
short. The vertical canvas shows a full-height square crop of the source at
full canvas width; a blurred fill covers the rest. Your only decision is where
that square sits horizontally over time.

You get one sampled frame per shot (each labeled with its timestamp in seconds
from the start of the clip), the clip's scene-cut timings, and editorial
context about what happens in it. Return crop keyframes: a starting center_x
at 0 seconds, then a new keyframe ONLY when the subject clearly sits somewhere
else in the frame — typically because the shot changed. center_x is the
horizontal center of the square as a fraction of the source width (0 = left
edge, 0.5 = middle, 1 = right edge).

Some shots cannot be cropped honestly: side-by-side call layouts with rapid
back-and-forth dialogue, wide action involving several people at once, or
graphics spanning the full frame. For those spans set wide to true — the whole
16:9 frame is shown fitted to the canvas width instead of a crop. Wide is the
honest fallback, not the default: use it when cropping would either lose the
conversation or force constant jumping between speakers.

Rules:
- Frame what a viewer should watch: the person speaking over the person
  listening, faces over bodies, the action over the scenery.
- Framing is static between keyframes and jumps at each keyframe. Never try to
  track or slide — a few well-placed static framings beat many small moves.
- Add a keyframe only when staying put would leave the subject out of frame or
  clearly off-center. When one framing covers the whole clip, return just the
  starting keyframe.
- Keyframes belong on shot changes (the scene-cut timings) unless the subject
  clearly moves within a shot.
- Prefer one wide span over rapid crop-jumping between two speakers trading
  short lines.

Return only JSON matching the schema."""


REFRAME_RESPONSE_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {
        "keyframes": {
            "type": "array",
            "description": "Crop positions in clip order; the first starts at 0 seconds.",
            "items": {
                "type": "object",
                "properties": {
                    "start_seconds": {
                        "type": "number",
                        "description": "Clip-relative time this framing takes effect.",
                    },
                    "center_x": {
                        "type": "number",
                        "description": "Horizontal center of the square crop as a fraction of source width. Use 0.5 when wide is true.",
                        "minimum": 0,
                        "maximum": 1,
                    },
                    "wide": {
                        "type": "boolean",
                        "description": "True when this span cannot be cropped honestly: show the whole 16:9 frame fitted to the canvas width instead.",
                    },
                },
                "required": ["start_seconds", "center_x", "wide"],
                "additionalProperties": False,
            },
        },
        "notes": {
            "type": "string",
            "description": "One line on the framing choices.",
        },
    },
    "required": ["keyframes", "notes"],
    "additionalProperties": False,
}


def plan_crop_track(
    *,
    clip_path: Path,
    clip_duration_seconds: float,
    scene_cuts: list[float],
    title: str,
    description: str,
    research_context: dict[str, Any] | None = None,
    model: str = DEFAULT_OPENROUTER_MODEL,
) -> dict[str, Any]:
    """One Gemini call deciding the crop keyframes for a rendered clip.

    scene_cuts are clip-relative seconds. Returns {keyframes, notes, model}
    with keyframes validated (first at 0, sorted, de-jittered); raises on
    failure — the caller keeps the 16:9 clip."""
    try:
        from openai import OpenAI
    except ImportError as exc:
        raise RuntimeError("Auto-reframing requires the 'openai' package") from exc

    api_key = os.environ.get("OPENROUTER_API_KEY")
    if not api_key:
        raise RuntimeError("OPENROUTER_API_KEY is required for auto-reframing")

    sample_times = _sample_times(clip_duration_seconds, scene_cuts)
    cut_list = (
        ", ".join(f"{cut:.1f}" for cut in scene_cuts) if scene_cuts else "none detected"
    )
    content: list[dict[str, Any]] = [
        {
            "type": "text",
            "text": "\n".join(
                [
                    f"Clip: {title}",
                    f"What happens: {description}",
                    f"Duration: {clip_duration_seconds:.1f}s",
                    f"Scene cuts (clip-relative seconds): {cut_list}",
                    "",
                    "Content research context:",
                    json.dumps(research_context or {}, indent=2),
                    "",
                    "Sampled frames follow, one per shot.",
                ]
            ),
        }
    ]
    with tempfile.TemporaryDirectory(prefix="reframe-") as tmp:
        for index, at_seconds in enumerate(sample_times):
            frame_path = Path(tmp) / f"frame_{index}.jpg"
            extract_thumbnail(clip_path=clip_path, output_path=frame_path, at_seconds=at_seconds)
            frame_b64 = base64.b64encode(frame_path.read_bytes()).decode("ascii")
            content.append({"type": "text", "text": f"Frame at {at_seconds:.1f}s:"})
            content.append(
                {
                    "type": "image_url",
                    "image_url": {"url": f"data:image/jpeg;base64,{frame_b64}"},
                }
            )

        with OpenAI(
            base_url=OPENROUTER_BASE_URL,
            api_key=api_key,
            timeout=120.0,
            default_headers={
                "HTTP-Referer": "http://localhost",
                "X-OpenRouter-Title": "highlighter reframe",
            },
        ) as client:
            response = client.chat.completions.create(
                model=model,
                messages=[
                    {"role": "system", "content": REFRAME_SYSTEM_PROMPT},
                    {"role": "user", "content": content},
                ],
                temperature=0,
                response_format={
                    "type": "json_schema",
                    "json_schema": {"name": "crop_track", "schema": REFRAME_RESPONSE_SCHEMA},
                },
                extra_body={
                    "provider": {
                        "order": [OPENROUTER_VERTEX_PROVIDER],
                        "allow_fallbacks": False,
                    },
                    "reasoning": {"effort": REFRAME_REASONING_EFFORT},
                },
            )

    if not response.choices or not response.choices[0].message.content:
        raise RuntimeError("Reframe response did not include content")
    decision = _json_from_text(response.choices[0].message.content)
    return {
        "keyframes": _validate_keyframes(decision.get("keyframes"), clip_duration_seconds),
        "notes": str(decision.get("notes") or ""),
        "model": model,
    }


def _sample_times(duration_seconds: float, scene_cuts: list[float]) -> list[float]:
    """One frame just after the clip start and just after each cut, capped at
    MAX_SAMPLE_FRAMES by evenly thinning the cut frames (the opener stays)."""
    times = [min(0.2, max(0.0, duration_seconds / 2))]
    for cut in sorted(scene_cuts):
        at_seconds = cut + 0.3
        if 0.5 < at_seconds < duration_seconds - 0.1:
            times.append(round(at_seconds, 2))
    if len(times) > MAX_SAMPLE_FRAMES:
        rest = times[1:]
        step = len(rest) / (MAX_SAMPLE_FRAMES - 1)
        times = [times[0]] + [rest[int(i * step)] for i in range(MAX_SAMPLE_FRAMES - 1)]
    return times


def _validate_keyframes(raw: Any, duration_seconds: float) -> list[dict[str, Any]]:
    """Sorted, de-jittered keyframes with the first forced to 0 seconds.
    Falls back to a single centered framing when nothing usable comes back."""
    keyframes: list[dict[str, Any]] = []
    for item in raw if isinstance(raw, list) else []:
        if not isinstance(item, dict):
            continue
        try:
            start = float(item["start_seconds"])
            wide = bool(item.get("wide", False))
            center = 0.5 if wide else min(1.0, max(0.0, float(item["center_x"])))
        except (KeyError, TypeError, ValueError):
            continue
        if start < duration_seconds:
            keyframes.append(
                {
                    "start_seconds": max(0.0, round(start, 2)),
                    "center_x": round(center, 3),
                    "wide": wide,
                }
            )

    keyframes.sort(key=lambda k: k["start_seconds"])
    if not keyframes:
        return [{"start_seconds": 0.0, "center_x": 0.5, "wide": False}]

    keyframes[0]["start_seconds"] = 0.0
    kept = [keyframes[0]]
    for keyframe in keyframes[1:]:
        previous = kept[-1]
        if keyframe["start_seconds"] - previous["start_seconds"] < MIN_KEYFRAME_SPACING_SECONDS:
            continue
        if keyframe["wide"] == previous["wide"] and (
            keyframe["wide"]
            or abs(keyframe["center_x"] - previous["center_x"]) < MIN_CENTER_DELTA
        ):
            continue
        kept.append(keyframe)
    return kept


def _mode_enables(keyframes: list[dict[str, Any]]) -> tuple[str, str]:
    """Overlay enable expressions for the square-crop and wide framings, from
    the keyframes' spans (the last span runs to the end of the clip)."""
    crop_spans: list[str] = []
    wide_spans: list[str] = []
    for keyframe, next_keyframe in zip(keyframes, [*keyframes[1:], None]):
        end = "1e9" if next_keyframe is None else f"{next_keyframe['start_seconds']:g}"
        span = f"between(t,{keyframe['start_seconds']:g},{end})"
        (wide_spans if keyframe.get("wide") else crop_spans).append(span)
    return "+".join(crop_spans) or "0", "+".join(wide_spans) or "0"


def crop_x_expression(
    keyframes: list[dict[str, float]], *, source_width: int, source_height: int
) -> str:
    """Piecewise-constant ffmpeg crop x expression (pixels) for a full-height
    square crop. Positions are clamped so the square stays inside the frame."""
    crop_width = min(source_width, source_height)
    max_x = source_width - crop_width
    positions = [
        min(max_x, max(0, round(keyframe["center_x"] * source_width - crop_width / 2)))
        for keyframe in keyframes
    ]
    expression = str(positions[-1])
    for keyframe, position in zip(reversed(keyframes[1:]), reversed(positions[:-1])):
        expression = f"if(lt(t,{keyframe['start_seconds']:g}),{position},{expression})"
    return expression


def render_vertical(
    *,
    source_path: Path,
    output_path: Path,
    keyframes: list[dict[str, float]],
) -> None:
    """Render the blur-pad vertical: blurred zoom-fill canvas with the sharp
    framing overlaid at full width — the square crop jumping between keyframe
    positions, or the whole 16:9 frame fitted to the width on wide spans."""
    source_width, source_height = _video_dimensions(source_path)
    crop_size = min(source_width, source_height)
    x_expression = crop_x_expression(
        keyframes, source_width=source_width, source_height=source_height
    )
    crop_enable, wide_enable = _mode_enables(keyframes)
    filtergraph = (
        f"[0:v]split=3[bg][fgc][fgw];"
        f"[bg]scale={CANVAS_WIDTH}:{CANVAS_HEIGHT}:force_original_aspect_ratio=increase,"
        f"crop={CANVAS_WIDTH}:{CANVAS_HEIGHT},gblur=sigma={BLUR_SIGMA},eq=brightness=-0.06[b];"
        f"[fgc]crop=w={crop_size}:h={crop_size}:x='{x_expression}':y=0,"
        f"scale={CANVAS_WIDTH}:{CANVAS_WIDTH}[fc];"
        f"[fgw]scale={CANVAS_WIDTH}:-2[fw];"
        f"[b][fc]overlay=0:(H-h)/2:enable='{crop_enable}'[bc];"
        f"[bc][fw]overlay=0:(H-h)/2:enable='{wide_enable}'[v]"
    )
    output_path.parent.mkdir(parents=True, exist_ok=True)
    result = subprocess.run(
        [
            "ffmpeg",
            "-hide_banner",
            "-loglevel",
            "error",
            "-y",
            "-i",
            str(source_path),
            "-filter_complex",
            filtergraph,
            "-map",
            "[v]",
            "-map",
            "0:a?",
            "-c:v",
            "libx264",
            "-preset",
            "veryfast",
            "-crf",
            "30",
            "-c:a",
            "copy",
            "-movflags",
            "+faststart",
            str(output_path),
        ],
        capture_output=True,
        text=True,
    )
    if result.returncode != 0:
        raise RuntimeError(result.stderr.strip() or "ffmpeg failed while rendering the vertical clip")


def _video_dimensions(path: Path) -> tuple[int, int]:
    result = subprocess.run(
        [
            "ffprobe",
            "-v",
            "error",
            "-select_streams",
            "v:0",
            "-show_entries",
            "stream=width,height",
            "-of",
            "csv=p=0",
            str(path),
        ],
        capture_output=True,
        text=True,
    )
    try:
        width, height = result.stdout.strip().split("\n")[0].split(",")
        return int(width), int(height)
    except ValueError as exc:
        raise RuntimeError(
            result.stderr.strip() or f"Could not read video dimensions from {path}"
        ) from exc
