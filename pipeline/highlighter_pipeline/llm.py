import base64
import json
import subprocess
import tempfile
from pathlib import Path
from typing import Any

from .defaults import (
    DEFAULT_LLM_MARKER_SECONDS,
    DEFAULT_LLM_REASONING_EFFORT,
    DEFAULT_TARGET_LENGTH_MINUTES,
)
from .providers import (
    OPENROUTER_BASE_URL,
    OPENROUTER_VERTEX_PROVIDER,
    Provider,
    audio_providers,
    run_with_fallback,
)

CLIPPER_AUDIO_BITRATE = "32k"
CLIPPER_AUDIO_SAMPLE_RATE = "16000"


SHORTFORM_SYSTEM_PROMPT = """You are the lead short-form editor for a professional clipping team.

Your job is to find only the moments that are actually worth clipping for
TikTok, Reels, Shorts, or X. You are not summarizing the source. You are making
hard editorial decisions for cold viewers with short attention spans.

Decision hierarchy:
1. Transcript evidence is the source of truth for the spoken content.
2. Audio is the source of truth for delivery: energy, pauses, laughter,
   awkwardness, yelling, surprise, timing, reactions, and the moment the hook
   actually starts.
3. Content research is only an audience-context layer: creator lore,
   current discourse, category norms, common clip formats, audience interests,
   and platform-specific patterns.
4. Research can raise or lower confidence only when the transcript/audio already has a
   real moment. It must never turn ordinary filler into a clip.

Selection standard:
You may return multiple distinct clips per chunk. Usually there are zero,
sometimes one, occasionally more. Most transcript chunks are not clip-worthy.
The bar per clip does not drop when a chunk is busy: every clip must clear the
same standard on its own. A clip-worthy moment should create a reason to keep
watching almost immediately: curiosity, tension, conflict, surprise, confusion,
a sharp joke, a strong reaction, a reversal, or a clear payoff. The moment
should be understandable to a cold viewer with minimal setup.

Reject chunks that are mainly setup, logistics, name-reading, repeated lines,
dead air, weak banter, generic gameplay commentary, or creator-only context.
Also reject moments that are merely "on topic" with the research but do not have
a hook and payoff in the transcript/audio itself. When nothing qualifies, return
an empty clips list and explain why in chunk_assessment.

Windows and timing:
The timestamp markers, transcript, and attached audio cover the visible window
[visible_start, visible_end], which includes context from neighboring chunks.
The chunk window [start, end] is your focus. A clip may extend up to the visible
bounds when the moment genuinely spills across the chunk boundary, but the core
hook and payoff must lie mainly inside the chunk window: neighboring chunks are
scored separately, and overlapping proposals are merged.

NEVER cut a speaker off mid-sentence or mid-thought. Start the clip at the
natural beginning of the sentence, sound, pause, or reaction that carries the
hook, and end it only after the thought or payoff is complete. Prefer extending
a boundary slightly over clipping a word in half.

Scene cuts: when the prompt lists scene-cut timestamps, they mark the source's
own visual transitions. Prefer starting or ending a clip on or just after a cut
when one lies near the natural spoken boundary, and never place a boundary in
the middle of a transition. A complete spoken thought still takes priority over
cut alignment.

If selecting a clip:
- Choose one sharp moment per clip, not the whole scene.
- Start as close as possible to the first audible hook.
- Include only the context needed for the viewer to understand the payoff.
- End right after the payoff, reaction, twist, or escalation.
- Do not pad the window because extra transcript is available.
- Prefer a tighter, cleaner cut over a longer explanatory cut.
- Do not default to 60-second clips. Duration is dynamic, but weak setup and
  dead air should almost always be excluded.

Use research carefully:
- Use it to recognize creator-specific references, currently relevant topics,
  and platform-native clip patterns.
- Do not invent visuals, chat messages, gameplay events, or facts not present in
  the transcript.
- Any outside-world claim in a title, description, or reason must be backed by
  a URL returned in that clip's research_sources.
- If no research source materially helped a decision, return its
  research_sources as [].

Return only JSON matching the schema. Prefer no clip over a weak clip."""


LONGFORM_SYSTEM_PROMPT = """You are the lead editor cutting one long-form video from a full-length source
(a VOD, podcast, or stream recording). The final video is assembled by
concatenating, in chronological order, the segments you and your co-editors
select across the whole source. You are seeing one chunk at a time; select the
segments from this chunk that deserve a place in the final edit.

Decision hierarchy:
1. Transcript evidence is the source of truth for the spoken content.
2. Audio is the source of truth for delivery: energy, pauses, laughter,
   awkwardness, timing, reactions, and where a passage actually begins and ends.
3. Content research is only an audience-context layer: creator lore, current
   discourse, category norms, audience interests, and platform patterns.
4. Research can raise or lower confidence only when the transcript/audio already
   has real substance. It must never turn filler into a keeper.

Selection standard:
Keep what a good human editor would keep in a tight edit of this source: complete
stories, strong arguments, genuinely informative or entertaining passages, big
reactions with their setup and payoff, and the connective moments a viewer needs
to follow along. Cut filler, dead air, logistics, repeated lines, technical
difficulties, and meandering that goes nowhere. A kept passage should assemble
into a self-contained thought — typically from ~30 seconds up to several
minutes — and a chunk may contribute zero, one, or several segments.

Edit at two scales. Beyond choosing the passages, make the small cuts a human
editor makes inside them: when a kept passage contains a cough, a sneeze, a
false start, a restart, a long dead pause, or a filler run ("um, so, yeah,
anyway"), return the passage as multiple adjacent segments that skip the junk
instead of one segment that includes it. Consecutive segments are butt-spliced
in the final video — a jump cut, which is standard in edited long-form content.
Parts of a split passage may be short; the duration guidance above applies to
the assembled passage, not to each part. Only cut what a viewer is glad to
lose: never sacrifice the natural rhythm of speech for density.

Target runtime:
The final edit is aiming for roughly {target_length} minutes in total across all
selections from the whole source. This is guidance, not a restriction: take the
time the content deserves. A dense source can justify running long; a thin one
should come out short rather than padded. Since you see one chunk at a time,
apply the target as a bar for selectivity — keep roughly the fraction of
material that would produce an edit of that scale — not as a quota to fill.{budget_calibration}

Windows and timing:
The timestamp markers, transcript, and attached audio cover the visible window
[visible_start, visible_end], which includes context from neighboring chunks.
The chunk window [start, end] is your focus. A segment may extend up to the
visible bounds when it genuinely spills across the chunk boundary; neighboring
chunks are edited separately, and selections that overlap or nearly touch
(within about a second) are merged, so a passage larger than one chunk still
comes together in the final cut. Segments you deliberately separated by a
wider gap stay separate.

NEVER cut a speaker off mid-sentence or mid-thought. Start each segment at the
natural beginning of the sentence or beat that opens it, and end only after the
thought is complete. Prefer extending a boundary slightly over clipping a word
in half.

Scene cuts: when the prompt lists scene-cut timestamps, they mark the source's
own visual transitions. Prefer starting or ending a segment on or just after a
cut when one lies near the natural spoken boundary, and never place a boundary
in the middle of a transition. A complete spoken thought still takes priority
over cut alignment.

Use research carefully:
- Use it to recognize creator-specific references and what this audience values.
- Do not invent visuals, chat messages, or facts not present in the transcript.
- Any outside-world claim in a title, description, or reason must be backed by a
  URL returned in that segment's research_sources; return [] when none was used.

Return only JSON matching the schema. In it, "clips" means the segments you are
keeping for the final edit, and "score" (0.0-1.0) means how essential the
segment is to that edit. Prefer a coherent, watchable cut over hitting a number."""


CLIP_RESPONSE_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {
        "clips": {
            "type": "array",
            "description": "Every distinct clip-worthy moment in this chunk. Usually empty; sometimes one entry; occasionally more.",
            "items": {
                "type": "object",
                "properties": {
                    "title": {
                        "type": "string",
                        "description": "Short working title for the clip.",
                    },
                    "description": {
                        "type": "string",
                        "description": "One sentence explaining what happens in the clip.",
                    },
                    "start_seconds": {
                        "type": "number",
                        "description": "Absolute source timestamp where the clip should start, at the natural beginning of the sentence or thought that carries the hook.",
                    },
                    "end_seconds": {
                        "type": "number",
                        "description": "Absolute source timestamp where the clip should end, only after the thought or payoff is complete.",
                    },
                    "score": {
                        "type": "number",
                        "description": "Clip strength from 0.0 to 1.0.",
                        "minimum": 0,
                        "maximum": 1,
                    },
                    "reason": {
                        "type": "string",
                        "description": "Brief reason this moment clears the bar.",
                    },
                    "research_sources": {
                        "type": "array",
                        "description": "Source URLs used for external content research context. Empty when no external context was used.",
                        "items": {
                            "type": "object",
                            "properties": {
                                "title": {"type": "string"},
                                "url": {"type": "string"},
                                "claim": {"type": "string"},
                            },
                            "required": ["title", "url", "claim"],
                            "additionalProperties": False,
                        },
                    },
                },
                "required": [
                    "title",
                    "description",
                    "start_seconds",
                    "end_seconds",
                    "score",
                    "reason",
                    "research_sources",
                ],
                "additionalProperties": False,
            },
        },
        "chunk_assessment": {
            "type": "string",
            "description": "One-line editorial assessment of the chunk; when clips is empty, why nothing qualified.",
        },
    },
    "required": ["clips", "chunk_assessment"],
    "additionalProperties": False,
}


def system_prompt(
    pipeline_mode: str,
    *,
    target_length: str | None = None,
    source_minutes: float | None = None,
) -> str:
    """The editor system prompt for a pipeline mode ('short' or 'long')."""
    if pipeline_mode == "long":
        target = target_length or DEFAULT_TARGET_LENGTH_MINUTES
        return LONGFORM_SYSTEM_PROMPT.format(
            target_length=target,
            budget_calibration=_budget_calibration(target, source_minutes),
        )
    return SHORTFORM_SYSTEM_PROMPT


def parse_target_minutes(target: str | None) -> tuple[float, float] | None:
    """Parse a target-runtime string ('10' or '7-15') into (low, high) minutes."""
    if not target:
        return None
    parts = [part.strip() for part in str(target).split("-")]
    try:
        numbers = [float(part) for part in parts if part]
    except ValueError:
        return None
    if len(numbers) == 1:
        return (numbers[0], numbers[0]) if numbers[0] > 0 else None
    if len(numbers) == 2 and 0 < numbers[0] <= numbers[1]:
        return (numbers[0], numbers[1])
    return None


def cap_target_minutes(target: str | None, source_minutes: float | None) -> str | None:
    """Clamp a target-runtime string to what the source actually has: an edit
    cannot run longer than its footage. Unparseable targets and unknown source
    lengths (livestreams) pass through unchanged."""
    bounds = parse_target_minutes(target)
    if not bounds or not source_minutes or source_minutes <= 0:
        return target
    low, high = bounds
    if high <= source_minutes:
        return target
    capped = round(source_minutes, 1)
    if low >= source_minutes:
        return f"{capped:g}"
    return f"{low:g}-{capped:g}"


def _budget_calibration(target: str, source_minutes: float | None) -> str:
    """Precomputed keep-rate sentence for the long-form prompt; empty when the
    source length is unknown (livestreams) or the target does not parse."""
    target = cap_target_minutes(target, source_minutes) or target
    bounds = parse_target_minutes(target)
    if not bounds or not source_minutes or source_minutes <= 0:
        return ""
    midpoint = (bounds[0] + bounds[1]) / 2
    keep_percent = max(1, min(100, round(100 * midpoint / source_minutes)))
    return (
        f"\nConcretely: this source runs roughly {source_minutes:.0f} minutes, so a"
        f" {target}-minute edit keeps roughly {keep_percent}% of it. Hold every"
        " selection to that level of selectivity."
    )


def detect_clip_candidates(
    *,
    transcript: str,
    words: list[dict[str, Any]],
    chunk_index: int,
    start_seconds: int,
    end_seconds: int,
    visible_start_seconds: int | None = None,
    visible_end_seconds: int | None = None,
    context_words_before: list[dict[str, Any]] | None = None,
    context_words_after: list[dict[str, Any]] | None = None,
    reasoning_effort: str = DEFAULT_LLM_REASONING_EFFORT,
    marker_seconds: int = DEFAULT_LLM_MARKER_SECONDS,
    pipeline_mode: str = "short",
    user_instructions: str | None = None,
    target_length: str | None = None,
    source_context: dict[str, Any] | None = None,
    research_context: dict[str, Any] | None = None,
    audio_context: list[dict[str, Any]] | None = None,
    scene_cuts: list[float] | None = None,
    source_minutes: float | None = None,
) -> tuple[list[dict[str, Any]], str]:
    """Score one chunk for clip candidates.

    Returns (candidates, chunk_assessment): a list of normalized candidate
    decision dicts (possibly empty — most chunks have no clip) plus the model's
    one-line assessment of the chunk, which callers use as the reason for a
    no-clip record when the list is empty. The transcript visible to the model
    spans [visible_start_seconds, visible_end_seconds] — the chunk window plus
    a little context from the neighboring chunks — and candidates are clamped
    to that visible window."""
    visible_start = start_seconds if visible_start_seconds is None else visible_start_seconds
    visible_end = end_seconds if visible_end_seconds is None else visible_end_seconds

    providers = audio_providers(
        title="highlighter pipeline",
        openrouter_reasoning_effort=reasoning_effort or DEFAULT_LLM_REASONING_EFFORT,
    )
    decision, provider = run_with_fallback(
        providers,
        lambda candidate_provider: _request_clip_candidates(
            provider=candidate_provider,
            transcript=transcript,
            words=words,
            chunk_index=chunk_index,
            start_seconds=start_seconds,
            end_seconds=end_seconds,
            visible_start_seconds=visible_start,
            visible_end_seconds=visible_end,
            context_words_before=context_words_before or [],
            context_words_after=context_words_after or [],
            marker_seconds=marker_seconds,
            pipeline_mode=pipeline_mode,
            user_instructions=user_instructions,
            target_length=target_length,
            research_context=research_context,
            audio_context=audio_context or [],
            scene_cuts=scene_cuts,
            source_minutes=source_minutes,
        ),
    )
    return _normalize_candidates(
        decision,
        chunk_index=chunk_index,
        chunk_start_seconds=start_seconds,
        chunk_end_seconds=end_seconds,
        visible_start_seconds=visible_start,
        visible_end_seconds=visible_end,
        model=provider.model,
        reasoning_effort=reasoning_effort or DEFAULT_LLM_REASONING_EFFORT,
    )


def _request_clip_candidates(
    *,
    provider: Provider,
    transcript: str,
    words: list[dict[str, Any]],
    chunk_index: int,
    start_seconds: int,
    end_seconds: int,
    visible_start_seconds: int,
    visible_end_seconds: int,
    context_words_before: list[dict[str, Any]],
    context_words_after: list[dict[str, Any]],
    marker_seconds: int,
    pipeline_mode: str,
    user_instructions: str | None,
    target_length: str | None,
    research_context: dict[str, Any] | None,
    audio_context: list[dict[str, Any]],
    scene_cuts: list[float] | None,
    source_minutes: float | None,
) -> dict[str, Any]:
    from openai import APIStatusError

    user_prompt = _build_user_prompt(
        transcript=transcript,
        words=words,
        chunk_index=chunk_index,
        start_seconds=start_seconds,
        end_seconds=end_seconds,
        visible_start_seconds=visible_start_seconds,
        visible_end_seconds=visible_end_seconds,
        context_words_before=context_words_before,
        context_words_after=context_words_after,
        marker_seconds=marker_seconds,
        user_instructions=user_instructions,
        research_context=research_context,
        scene_cuts=scene_cuts,
    )
    if not provider.supports_json_schema:
        user_prompt += (
            "\n\nReturn ONLY a JSON object matching this schema, with no markdown fences:\n"
            + json.dumps(CLIP_RESPONSE_SCHEMA)
        )
    user_content: list[dict[str, Any]] = [{"type": "text", "text": user_prompt}]
    audio_b64 = _audio_context_base64(
        audio_context=audio_context,
        visible_start_seconds=visible_start_seconds,
        visible_end_seconds=visible_end_seconds,
    )
    if audio_b64:
        user_content.append(
            {
                "type": "input_audio",
                "input_audio": {"data": audio_b64, "format": "mp3"},
            }
        )

    try:
        with provider.client(timeout=240.0) as client:
            response = client.chat.completions.create(
                messages=[
                    {
                        "role": "system",
                        "content": system_prompt(
                            pipeline_mode,
                            target_length=target_length,
                            source_minutes=source_minutes,
                        ),
                    },
                    {"role": "user", "content": user_content},
                ],
                **(
                    {
                        "response_format": {
                            "type": "json_schema",
                            "json_schema": {
                                "name": "clip_candidates",
                                "schema": CLIP_RESPONSE_SCHEMA,
                            },
                        }
                    }
                    if provider.supports_json_schema
                    else {}
                ),
                user=f"chunk-{chunk_index}",
                **provider.request_kwargs(),
            )
    except APIStatusError as exc:
        raise RuntimeError(
            f"{provider.label} request failed: {exc.response.text[:4000]}"
        ) from exc

    if not response.choices:
        raise RuntimeError(f"{provider.label} response did not include choices")
    content = response.choices[0].message.content
    if not content:
        raise RuntimeError(f"{provider.label} response did not include text content")
    return _json_from_text(content)


def _audio_context_base64(
    *,
    audio_context: list[dict[str, Any]],
    visible_start_seconds: int,
    visible_end_seconds: int,
) -> str | None:
    if not audio_context:
        return None
    mp3 = _encode_audio_context_mp3(
        audio_context=audio_context,
        visible_start_seconds=visible_start_seconds,
        visible_end_seconds=visible_end_seconds,
    )
    return base64.b64encode(mp3).decode("ascii")


def _encode_audio_context_mp3(
    *,
    audio_context: list[dict[str, Any]],
    visible_start_seconds: int,
    visible_end_seconds: int,
) -> bytes:
    segments = [
        {
            **segment,
            "path": Path(str(segment["path"])),
            "start_seconds": float(segment["start_seconds"]),
            "end_seconds": float(segment["end_seconds"]),
        }
        for segment in audio_context
        if segment.get("path")
    ]
    if not segments:
        raise RuntimeError("No audio segments were available for Gemini scoring")
    segments.sort(key=lambda segment: segment["start_seconds"])
    source_start = segments[0]["start_seconds"]
    offset = max(0.0, visible_start_seconds - source_start)
    duration = max(0.1, visible_end_seconds - visible_start_seconds)

    with tempfile.TemporaryDirectory(prefix="clip-audio-") as tmp:
        tmpdir = Path(tmp)
        output_path = tmpdir / "context.mp3"
        if len(segments) == 1:
            input_args = ["-i", str(segments[0]["path"])]
        else:
            concat_file = tmpdir / "inputs.txt"
            concat_file.write_text(
                "".join(f"file '{_ffmpeg_concat_path(segment['path'])}'\n" for segment in segments)
            )
            input_args = ["-f", "concat", "-safe", "0", "-i", str(concat_file)]

        command = [
            "ffmpeg",
            "-hide_banner",
            "-loglevel",
            "error",
            "-y",
            *input_args,
            "-ss",
            f"{offset:.3f}",
            "-t",
            f"{duration:.3f}",
            "-vn",
            "-ac",
            "1",
            "-ar",
            CLIPPER_AUDIO_SAMPLE_RATE,
            "-c:a",
            "libmp3lame",
            "-b:a",
            CLIPPER_AUDIO_BITRATE,
            str(output_path),
        ]
        result = subprocess.run(command, capture_output=True, text=True)
        if result.returncode != 0:
            raise RuntimeError(result.stderr.strip() or "ffmpeg failed while encoding clip audio")
        return output_path.read_bytes()


def _ffmpeg_concat_path(path: Path) -> str:
    return str(path.resolve()).replace("'", "'\\''")


def _json_from_text(text: str) -> dict[str, Any]:
    stripped = text.strip()
    if stripped.startswith("```"):
        stripped = stripped.strip("`").strip()
        if stripped.startswith("json"):
            stripped = stripped[4:].strip()
    return json.loads(stripped)


def _build_user_prompt(
    *,
    transcript: str,
    words: list[dict[str, Any]],
    chunk_index: int,
    start_seconds: int,
    end_seconds: int,
    visible_start_seconds: int,
    visible_end_seconds: int,
    context_words_before: list[dict[str, Any]],
    context_words_after: list[dict[str, Any]],
    marker_seconds: int,
    user_instructions: str | None = None,
    research_context: dict[str, Any] | None = None,
    scene_cuts: list[float] | None = None,
) -> str:
    visible_words = [*context_words_before, *words, *context_words_after]
    markers = _timestamp_markers(
        words=visible_words,
        start_seconds=visible_start_seconds,
        end_seconds=visible_end_seconds,
        marker_seconds=marker_seconds,
    )
    before_text = " ".join(
        part for part in (_word_text(word) for word in context_words_before) if part
    )
    after_text = " ".join(
        part for part in (_word_text(word) for word in context_words_after) if part
    )
    instructions_block = (
        [
            "User instructions (editorial guidance from the human in the loop;",
            "follow the spirit, but they are not rigid constraints):",
            user_instructions,
            "",
        ]
        if user_instructions
        else []
    )
    if scene_cuts is None:
        cut_lines = []
    elif scene_cuts:
        cut_lines = [
            "Scene cuts in the visible window (absolute seconds): "
            + ", ".join(f"{cut:.1f}" for cut in scene_cuts)
        ]
    else:
        cut_lines = ["Scene cuts in the visible window: none detected."]
    return "\n".join(
        [
            f"Chunk index: {chunk_index}",
            f"Absolute chunk window (focus): {start_seconds}s to {end_seconds}s",
            f"Visible window (markers and transcript below): {visible_start_seconds}s to {visible_end_seconds}s",
            "Attached audio covers the same visible window when available.",
            *cut_lines,
            "",
            *instructions_block,
            "Timestamp markers:",
            markers or "(No word-level markers available.)",
            "",
            "Content research context:",
            json.dumps(research_context or {}, indent=2),
            "",
            "Context before chunk:",
            before_text or "(No earlier context.)",
            "",
            "Chunk transcript:",
            transcript or "(No transcript text.)",
            "",
            "Context after chunk:",
            after_text or "(No later context.)",
        ]
    )


def _timestamp_markers(
    *,
    words: list[dict[str, Any]],
    start_seconds: int,
    end_seconds: int,
    marker_seconds: int,
) -> str:
    if marker_seconds <= 0:
        marker_seconds = DEFAULT_LLM_MARKER_SECONDS

    lines: list[str] = []
    current = start_seconds
    while current < end_seconds:
        next_mark = min(current + marker_seconds, end_seconds)
        marker_words = [
            _word_text(word)
            for word in words
            if current <= float(word.get("absolute_start", word.get("start", 0))) < next_mark
        ]
        snippet = " ".join(part for part in marker_words if part).strip()
        if snippet:
            lines.append(f"{current}s: {snippet[:240]}")
        current = next_mark
    return "\n".join(lines)


def _word_text(word: dict[str, Any]) -> str:
    return str(word.get("punctuated_word") or word.get("word") or "").strip()


def _normalize_candidates(
    decision: dict[str, Any],
    *,
    chunk_index: int,
    chunk_start_seconds: int,
    chunk_end_seconds: int,
    visible_start_seconds: int,
    visible_end_seconds: int,
    model: str,
    reasoning_effort: str,
) -> tuple[list[dict[str, Any]], str]:
    """Normalize a multi-clip model response into per-candidate decision dicts.

    Candidates are clamped to the visible window (chunk window plus context
    margins). Candidates with a non-positive duration after clamping are
    dropped, as are candidates that do not intersect the chunk window at all —
    a clip living entirely in a context margin belongs to the neighbor chunk."""
    raw_clips = decision.get("clips") if isinstance(decision.get("clips"), list) else []
    chunk_assessment = str(decision.get("chunk_assessment") or "")

    candidates: list[dict[str, Any]] = []
    for item in raw_clips:
        if not isinstance(item, dict):
            continue
        clip_start = _clamp_float(
            item.get("start_seconds"), visible_start_seconds, visible_end_seconds
        )
        clip_end = _clamp_float(
            item.get("end_seconds"), visible_start_seconds, visible_end_seconds
        )
        if clip_end <= clip_start:
            continue
        if clip_end <= chunk_start_seconds or clip_start >= chunk_end_seconds:
            continue

        reason = str(item.get("reason", ""))
        candidates.append(
            {
                "chunk_index": chunk_index,
                "is_clip_worthy": True,
                "title": str(item.get("title") or f"Clip candidate {chunk_index}"),
                "description": str(item.get("description") or reason),
                "start_seconds": round(clip_start, 3),
                "end_seconds": round(clip_end, 3),
                "score": _clamp_float(item.get("score", 0), 0, 1),
                "reason": reason,
                "research_sources": (
                    item.get("research_sources")
                    if isinstance(item.get("research_sources"), list)
                    else []
                ),
                "model": model,
                "reasoning_effort": reasoning_effort,
                "raw_decision": item,
            }
        )

    return candidates, chunk_assessment


def _clamp_float(value: Any, minimum: float, maximum: float) -> float:
    try:
        number = float(value)
    except (TypeError, ValueError):
        number = minimum
    return min(max(number, minimum), maximum)
