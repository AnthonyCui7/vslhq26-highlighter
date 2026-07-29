"""Natural-language revision loop for a finished long-form edit.

`highlighter-revise <project_id> "<request>"` reopens a project's local record
and puts the supervising editor (Gemini, tool-calling) in charge of producing
the next version. The agent starts with the full edit state — candidates, the
current cut, editor notes, prior revisions — and pulls everything else through
tools: the word-timestamped transcript, the actual audio, scene cuts, and the
research context. It can rerun pipeline stages (research, per-chunk pass-1
rescoring) before committing the new cut with apply_edit, whose selections are
free windows: split, tighten, extend, or add — always assembled in strict
source-chronological order.

Assembly reuses what exists: a selection matching a rendered candidate uses its
file, one inside a candidate window is trimmed from it, and anything else is
fetched from the source URL (long-form sources are VODs). Each version lands as
longform/longform_v<N>.mp4 plus a longform_edits row, so the whole history
stays queryable and a failed revision changes nothing.
"""

import argparse
import base64
import json
import os
from pathlib import Path
from typing import Any

from .config import load_env
from .defaults import (
    DEFAULT_LLM_REASONING_EFFORT,
    DEFAULT_OUTPUT_ROOT,
    DEFAULT_SUPABASE_CLIPS_BUCKET,
    DEFAULT_TARGET_LENGTH_MINUTES,
)
from .llm import (
    _encode_audio_context_mp3,
    _timestamp_markers,
    detect_clip_candidates,
)
from .providers import Provider, audio_providers
from .records import ProjectRecords
from .render import extract_thumbnail, render_clip_from_video_url, trim_clip
from .research import research_content_context
from .shots import snap_window
from .stitch import stitch_clips
from .supabase_client import SupabaseClient

MAX_AGENT_TURNS = 16
MIN_SELECTION_SECONDS = 2.0
# A selection whose edges sit within this of a rendered candidate's reuses the
# file as-is; inside the window it is trimmed from it; anything else is
# fetched from the source URL.
REUSE_TOLERANCE_SECONDS = 0.25
MAX_TRANSCRIPT_WINDOW_SECONDS = 1200
MAX_AUDIO_WINDOW_SECONDS = 600
MAX_RESCORE_CHUNKS = 6


REVISE_SYSTEM_PROMPT = """You are the supervising editor revising an existing long-form edit to satisfy
the client's revision request.

You start with the full state of the current edit: the candidate segments the
first pass nominated, the segments the current version keeps, the editor's
notes, and any earlier revisions. Tools give you everything else on demand:
the word-timestamped transcript, the actual audio, the source's scene-cut
timings, and the content research. You can also rerun parts of the pipeline:
rerun_research refreshes the research with a new focus, and rescore_chunks
re-edits specific chunks of the source with fresh guidance when you suspect
the first pass missed material the request needs.

Work like an editor, not a chatbot: gather only what the request actually
requires, then commit. When the request points at specific content ("cut the
sponsor talk"), read the transcript around the likely spots to find exact
boundaries; listen to the audio when delivery or timing matters.

The final video is your selections butt-spliced in strict source-chronological
order — never plan an edit that depends on reordering. Selections are free
windows over the source: you may split, tighten, extend, or add material.
Never cut a spoken word in half; start and end at natural speech boundaries.
A small excision inside a passage (a cough, a false start) is two adjacent
selections that skip the junk.

Finish by calling apply_edit exactly once with every selection for the NEW
version. It replaces the current edit outright, so include the segments you
are keeping unchanged, not just the ones you touched."""


TOOLS: list[dict[str, Any]] = [
    {
        "type": "function",
        "function": {
            "name": "get_transcript",
            "description": "Word-timestamped transcript for a window of the source (absolute seconds).",
            "parameters": {
                "type": "object",
                "properties": {
                    "start_seconds": {"type": "number"},
                    "end_seconds": {"type": "number"},
                },
                "required": ["start_seconds", "end_seconds"],
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "listen_audio",
            "description": "Hear the source audio for a window (absolute seconds, up to 10 minutes). The audio arrives in the next message.",
            "parameters": {
                "type": "object",
                "properties": {
                    "start_seconds": {"type": "number"},
                    "end_seconds": {"type": "number"},
                },
                "required": ["start_seconds", "end_seconds"],
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "get_scene_cuts",
            "description": "The source's detected visual scene-cut timestamps in a window (absolute seconds). Clip boundaries near a cut read cleaner.",
            "parameters": {
                "type": "object",
                "properties": {
                    "start_seconds": {"type": "number"},
                    "end_seconds": {"type": "number"},
                },
                "required": ["start_seconds", "end_seconds"],
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "get_research",
            "description": "The cached content research context for this source.",
            "parameters": {"type": "object", "properties": {}},
        },
    },
    {
        "type": "function",
        "function": {
            "name": "rerun_research",
            "description": "Rerun the web-grounded content research with a new focus; the refreshed context is cached and returned.",
            "parameters": {
                "type": "object",
                "properties": {
                    "focus": {
                        "type": "string",
                        "description": "What the research should dig into this time.",
                    }
                },
                "required": ["focus"],
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "rescore_chunks",
            "description": "Rerun first-pass segment nomination on specific chunks with fresh guidance (transcript + audio are re-read). Returns the new candidates; use their windows in apply_edit if they earn a place.",
            "parameters": {
                "type": "object",
                "properties": {
                    "chunk_indexes": {
                        "type": "array",
                        "items": {"type": "integer"},
                        "description": "Chunk indexes to re-edit (at most 6 per call).",
                    },
                    "guidance": {
                        "type": "string",
                        "description": "What the first pass should look for this time.",
                    },
                },
                "required": ["chunk_indexes", "guidance"],
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "apply_edit",
            "description": "Commit the new cut. Selections are absolute-seconds windows over the source, assembled in chronological order.",
            "parameters": {
                "type": "object",
                "properties": {
                    "selections": {
                        "type": "array",
                        "items": {
                            "type": "object",
                            "properties": {
                                "start_seconds": {"type": "number"},
                                "end_seconds": {"type": "number"},
                                "reason": {"type": "string"},
                            },
                            "required": ["start_seconds", "end_seconds", "reason"],
                        },
                    },
                    "notes": {
                        "type": "string",
                        "description": "Brief explanation of what changed and why.",
                    },
                },
                "required": ["selections", "notes"],
            },
        },
    },
]


def main() -> None:
    load_env()
    args = _parse_args()
    output_root = Path(args.output_root or os.environ.get("OUTPUT_ROOT", DEFAULT_OUTPUT_ROOT))
    project_dir = output_root / "projects" / args.project_id
    if not (project_dir / "project.json").exists():
        raise RuntimeError(f"No local project record at {project_dir}")

    state = _load_state(project_dir)
    db = None if args.project_id.startswith("local-") else SupabaseClient()
    records = ProjectRecords(project_dir)

    print(
        f"Revising project {args.project_id} "
        f"(current version {state['current_version']}): {args.request}"
    )
    edit, tools_used = _run_agent(state=state, records=records, request=args.request)
    result = _apply_revision(
        state=state,
        records=records,
        db=db,
        edit=edit,
        request=args.request,
        tools_used=tools_used,
    )
    print(
        f"Revision v{result['version']} ready: {result['local_path']} "
        f"({result['segments']} segment(s), ~{result['duration_seconds'] / 60:.1f} min)"
    )


def _load_state(project_dir: Path) -> dict[str, Any]:
    """Everything the agent and assembly need, read from the local mirror."""
    project = json.loads((project_dir / "project.json").read_text())
    chunks = _read_jsonl(project_dir / "transcript_chunks.jsonl")
    chunks.sort(key=lambda row: row["chunk_index"])

    # Combined runs tag clips with metadata.pipeline; only long-form pass-1
    # candidates feed a revision (a missing tag means an older long-form run).
    candidates = [
        row
        for row in _read_jsonl(project_dir / "clips.jsonl")
        if (row.get("metadata") or {}).get("source") == "llm"
        and row.get("status") == "rendered"
        and (row.get("metadata") or {}).get("pipeline") in (None, "long")
    ]
    candidates.sort(key=lambda row: row["start_seconds"])

    longform_edits = _read_jsonl(project_dir / "longform_edits.jsonl")
    revisions = [
        row for row in _read_jsonl(project_dir / "decisions.jsonl") if row.get("type") == "revision"
    ]

    research_file = project_dir / "research.json"
    research = json.loads(research_file.read_text()) if research_file.exists() else None

    word_spans: list[tuple[float, float]] = []
    cuts: list[float] = []
    for chunk in chunks:
        word_spans.extend(
            (float(word["absolute_start"]), float(word["absolute_end"]))
            for word in chunk.get("words") or []
        )
        cuts.extend((chunk.get("metadata") or {}).get("scene_cuts") or [])

    ingest = (project.get("metadata") or {}).get("ingest") or {}
    versions = [row.get("version") or 1 for row in longform_edits]
    current_version = max(versions) if versions else (
        1 if (project_dir / "longform" / "longform.mp4").exists() else 0
    )

    return {
        "project_dir": project_dir,
        "project": project,
        "project_id": project.get("id") or project_dir.name,
        "source_url": project.get("source_url"),
        "chunks": chunks,
        "candidates": candidates,
        "longform_edits": longform_edits,
        "revisions": revisions,
        "research": research,
        "word_spans": word_spans,
        "cuts": sorted(cuts),
        "target_length": ingest.get("target_length_minutes") or DEFAULT_TARGET_LENGTH_MINUTES,
        "source_minutes": ingest.get("source_minutes"),
        "user_instructions": ingest.get("user_instructions"),
        "source_end_seconds": max((chunk["end_seconds"] for chunk in chunks), default=0.0),
        "current_version": current_version,
    }


def _read_jsonl(path: Path) -> list[dict[str, Any]]:
    if not path.exists():
        return []
    return [json.loads(line) for line in path.read_text().splitlines() if line.strip()]


def _run_agent(
    *,
    state: dict[str, Any],
    records: ProjectRecords,
    request: str,
    reasoning_effort: str = DEFAULT_LLM_REASONING_EFFORT,
) -> tuple[dict[str, Any], list[str]]:
    """Run the tool loop until apply_edit lands, trying each provider in the
    audio chain with a fresh loop (histories do not survive a provider swap).
    Returns (edit, tools_used); raises when no provider produced a valid edit."""
    providers = audio_providers(
        title="highlighter revise",
        openrouter_reasoning_effort=reasoning_effort or DEFAULT_LLM_REASONING_EFFORT,
    )
    last_error: Exception = RuntimeError("No revision provider is configured")
    for index, provider in enumerate(providers):
        try:
            return _run_agent_attempt(
                provider=provider, state=state, records=records, request=request
            )
        except Exception as exc:
            last_error = exc
            if index + 1 < len(providers):
                print(
                    f"{provider.label} revision attempt failed; retrying with "
                    f"{providers[index + 1].label}: {exc}"
                )
    raise last_error


def _run_agent_attempt(
    *,
    provider: Provider,
    state: dict[str, Any],
    records: ProjectRecords,
    request: str,
) -> tuple[dict[str, Any], list[str]]:
    messages: list[Any] = [
        {"role": "system", "content": REVISE_SYSTEM_PROMPT},
        {"role": "user", "content": _opening_message(state, request)},
    ]
    if provider.name == "azure":
        # The gpt-audio deployments reject any request whose input contains no
        # audio at all, and the opening turns have none until listen_audio
        # runs; seed the conversation with the source's first minute so every
        # turn carries audio.
        orientation = _orientation_audio_parts(state)
        if orientation:
            messages.append({"role": "user", "content": orientation})
    tools_used: list[str] = []

    with provider.client(timeout=300.0) as client:
        for turn in range(MAX_AGENT_TURNS):
            forcing = turn >= MAX_AGENT_TURNS - 2
            response = client.chat.completions.create(
                messages=messages,
                tools=TOOLS,
                # The last turns force the commit; a named tool_choice is
                # stronger than pleading in a user message.
                tool_choice=(
                    {"type": "function", "function": {"name": "apply_edit"}}
                    if forcing
                    else "auto"
                ),
                **provider.request_kwargs(),
            )
            if not response.choices:
                raise RuntimeError("Revision agent response did not include choices")
            message = response.choices[0].message
            # Replayed verbatim: reasoning_details carries the Gemini thought
            # signatures, and the provider rejects a history without them.
            messages.append(message)

            calls = message.tool_calls or []
            if not calls:
                messages.append(
                    {
                        "role": "user",
                        "content": "Continue with a tool call; call apply_edit once the new cut is ready.",
                    }
                )
                continue

            audio_parts: list[dict[str, Any]] = []
            edit: dict[str, Any] | None = None
            for call in calls:
                name = call.function.name
                try:
                    call_args = json.loads(call.function.arguments or "{}")
                except json.JSONDecodeError as exc:
                    messages.append(
                        {
                            "role": "tool",
                            "tool_call_id": call.id,
                            "content": f"Invalid JSON arguments: {exc}",
                        }
                    )
                    continue
                tools_used.append(name)
                print(f"[agent] {name}({_summarize_args(call_args)})")

                if name == "apply_edit" and edit is None:
                    selections, problem = _validate_selections(
                        call_args.get("selections"), state["source_end_seconds"]
                    )
                    if problem:
                        result_text = problem
                    else:
                        edit = {"selections": selections, "notes": str(call_args.get("notes") or "")}
                        result_text = "Edit accepted."
                else:
                    result_text, parts = _run_tool(name, call_args, state, records)
                    audio_parts.extend(parts)
                messages.append(
                    {"role": "tool", "tool_call_id": call.id, "content": result_text}
                )

            if audio_parts:
                messages.append({"role": "user", "content": audio_parts})
            if edit is not None:
                return edit, sorted(set(tools_used))

    raise RuntimeError(
        f"The revision agent did not produce a valid edit within {MAX_AGENT_TURNS} turns"
    )


def _orientation_audio_parts(state: dict[str, Any]) -> list[dict[str, Any]]:
    """The source's opening minute as input_audio content parts; [] when the
    chunk audio is missing (the Azure attempt then fails over to the next
    provider on its first request)."""
    try:
        chunk = state["chunks"][0]
        audio_path = _resolve_media_path(
            (chunk.get("metadata") or {}).get("audio_path"), state["project_dir"], "audio"
        )
        if not audio_path:
            return []
        end = min(chunk["start_seconds"] + 60.0, chunk["end_seconds"])
        mp3 = _encode_audio_context_mp3(
            audio_context=[
                {
                    "path": audio_path,
                    "start_seconds": chunk["start_seconds"],
                    "end_seconds": chunk["end_seconds"],
                }
            ],
            visible_start_seconds=chunk["start_seconds"],
            visible_end_seconds=end,
        )
        return [
            {
                "type": "text",
                "text": (
                    f"Source audio {chunk['start_seconds']:.0f}s-{end:.0f}s for "
                    "orientation; use listen_audio for the windows you need."
                ),
            },
            {
                "type": "input_audio",
                "input_audio": {
                    "data": base64.b64encode(mp3).decode("ascii"),
                    "format": "mp3",
                },
            },
        ]
    except Exception as exc:
        print(f"Orientation audio unavailable (continuing): {exc}")
        return []


def _opening_message(state: dict[str, Any], request: str) -> str:
    current = state["longform_edits"][-1] if state["longform_edits"] else None
    current_lines = (
        [
            f"{i}. [{s['start_seconds']:.1f}s-{s['end_seconds']:.1f}s | "
            f"{s['end_seconds'] - s['start_seconds']:.1f}s] {s.get('title') or 'Untitled'}"
            for i, s in enumerate(current.get("segments") or [])
        ]
        if current
        else ["(no long-form version exists yet — build one from the candidates)"]
    )
    editor_meta = (current or {}).get("editor") or {}
    candidate_lines = [
        f"{i}. [{c['start_seconds']:.1f}s-{c['end_seconds']:.1f}s | "
        f"{c['end_seconds'] - c['start_seconds']:.1f}s | score {c.get('score')}] "
        f"{c.get('title')} — {c.get('description') or ''}"
        for i, c in enumerate(state["candidates"])
    ]
    revision_lines = [
        f"v{r.get('version')}: {r.get('request')} -> {r.get('notes')}"
        for r in state["revisions"]
    ]
    source_minutes = state["source_minutes"] or state["source_end_seconds"] / 60
    return "\n".join(
        [
            f"Revision request: {request}",
            "",
            f"Source: ~{source_minutes:.0f} minutes captured "
            f"(0s to {state['source_end_seconds']:.0f}s). "
            f"Target runtime: {state['target_length']} minutes.",
            f"Current edit (version {state['current_version']}):",
            *current_lines,
            f"Editor notes: {editor_meta.get('edit_notes') or '(none)'}",
            "",
            "Earlier revisions:",
            *(revision_lines or ["(none)"]),
            "",
            "Candidate segments from the first pass (chronological):",
            *(candidate_lines or ["(none rendered)"]),
        ]
    )


def _summarize_args(call_args: dict[str, Any]) -> str:
    parts = []
    for key, value in call_args.items():
        text = json.dumps(value)
        parts.append(f"{key}={text[:80]}{'...' if len(text) > 80 else ''}")
    return ", ".join(parts)


def _run_tool(
    name: str,
    call_args: dict[str, Any],
    state: dict[str, Any],
    records: ProjectRecords,
) -> tuple[str, list[dict[str, Any]]]:
    """Execute one read/rerun tool. Returns (tool result text, audio content
    parts to deliver in a follow-up user message). Failures come back as text
    so the agent can adjust instead of dying."""
    try:
        if name == "get_transcript":
            start, end = _window(call_args, state, MAX_TRANSCRIPT_WINDOW_SECONDS)
            words = [
                word
                for chunk in _chunks_in_window(state, start, end)
                for word in chunk.get("words") or []
                if start <= float(word["absolute_start"]) < end
            ]
            markers = _timestamp_markers(
                words=words, start_seconds=int(start), end_seconds=int(end) + 1, marker_seconds=10
            )
            return markers or "(no speech in this window)", []

        if name == "listen_audio":
            start, end = _window(call_args, state, MAX_AUDIO_WINDOW_SECONDS)
            audio_context = [
                {
                    "path": _resolve_media_path(
                        (chunk.get("metadata") or {}).get("audio_path"),
                        state["project_dir"],
                        "audio",
                    ),
                    "start_seconds": chunk["start_seconds"],
                    "end_seconds": chunk["end_seconds"],
                }
                for chunk in _chunks_in_window(state, start, end)
            ]
            mp3 = _encode_audio_context_mp3(
                audio_context=audio_context,
                visible_start_seconds=start,
                visible_end_seconds=end,
            )
            parts = [
                {"type": "text", "text": f"Audio for {start:.1f}s-{end:.1f}s:"},
                {
                    "type": "input_audio",
                    "input_audio": {
                        "data": base64.b64encode(mp3).decode("ascii"),
                        "format": "mp3",
                    },
                },
            ]
            return f"Audio for {start:.1f}s-{end:.1f}s is attached in the next message.", parts

        if name == "get_scene_cuts":
            start, end = _window(call_args, state, None)
            cuts = [cut for cut in state["cuts"] if start <= cut <= end]
            if not cuts:
                return "No scene cuts detected in this window.", []
            return "Scene cuts (absolute seconds): " + ", ".join(f"{c:.1f}" for c in cuts), []

        if name == "get_research":
            if state["research"] is None:
                return "No research context is cached for this project.", []
            return json.dumps(state["research"], indent=2), []

        if name == "rerun_research":
            focus = str(call_args.get("focus") or "")
            instructions = "\n".join(
                part for part in (state["user_instructions"], f"Research focus: {focus}") if part
            )
            project = state["project"]
            context = research_content_context(
                source_context={
                    "platform": (project.get("metadata") or {}).get("platform"),
                    "source_type": project.get("source_type"),
                    "name": project.get("name"),
                    "source_url": project.get("source_url"),
                },
                pipeline_mode="long",
                user_instructions=instructions,
            )
            state["research"] = context
            records.write_research(context)
            return json.dumps(context, indent=2), []

        if name == "rescore_chunks":
            return _rescore_chunks(call_args, state), []

        return f"Unknown tool: {name}", []
    except Exception as exc:
        return f"{name} failed: {exc}", []


def _rescore_chunks(call_args: dict[str, Any], state: dict[str, Any]) -> str:
    indexes = [int(i) for i in call_args.get("chunk_indexes") or []][:MAX_RESCORE_CHUNKS]
    guidance = str(call_args.get("guidance") or "")
    instructions = "\n".join(
        part for part in (state["user_instructions"], guidance) if part
    )
    by_index = {chunk["chunk_index"]: chunk for chunk in state["chunks"]}
    results: list[dict[str, Any]] = []
    for index in indexes:
        chunk = by_index.get(index)
        if chunk is None:
            results.append({"chunk_index": index, "error": "no such chunk"})
            continue
        audio_path = _resolve_media_path(
            (chunk.get("metadata") or {}).get("audio_path"), state["project_dir"], "audio"
        )
        candidates, assessment = detect_clip_candidates(
            transcript=chunk["transcript"],
            words=chunk["words"],
            chunk_index=index,
            start_seconds=chunk["start_seconds"],
            end_seconds=chunk["end_seconds"],
            pipeline_mode="long",
            user_instructions=instructions or None,
            target_length=state["target_length"],
            research_context=state["research"],
            audio_context=[
                {
                    "path": audio_path,
                    "start_seconds": chunk["start_seconds"],
                    "end_seconds": chunk["end_seconds"],
                }
            ]
            if audio_path
            else [],
            scene_cuts=[
                cut
                for cut in state["cuts"]
                if chunk["start_seconds"] <= cut <= chunk["end_seconds"]
            ],
            source_minutes=state["source_minutes"],
        )
        results.append(
            {
                "chunk_index": index,
                "assessment": assessment,
                "candidates": [
                    {
                        "start_seconds": c["start_seconds"],
                        "end_seconds": c["end_seconds"],
                        "title": c["title"],
                        "description": c["description"],
                        "score": c["score"],
                        "reason": c["reason"],
                    }
                    for c in candidates
                ],
            }
        )
    return json.dumps(results, indent=2)


def _window(
    call_args: dict[str, Any], state: dict[str, Any], cap_seconds: float | None
) -> tuple[float, float]:
    start = max(0.0, float(call_args.get("start_seconds") or 0))
    end = min(float(call_args.get("end_seconds") or 0), state["source_end_seconds"])
    if end <= start:
        raise RuntimeError("end_seconds must be greater than start_seconds")
    if cap_seconds is not None and end - start > cap_seconds:
        end = start + cap_seconds
    return start, end


def _chunks_in_window(state: dict[str, Any], start: float, end: float) -> list[dict[str, Any]]:
    return [
        chunk
        for chunk in state["chunks"]
        if chunk["start_seconds"] < end and chunk["end_seconds"] > start
    ]


def _resolve_media_path(raw: str | None, project_dir: Path, subdir: str) -> str | None:
    """Media paths were recorded relative to the original run's working
    directory; fall back to the project directory when that CWD moved."""
    if not raw:
        return None
    path = Path(raw)
    if path.exists():
        return str(path)
    fallback = project_dir / subdir / path.name
    return str(fallback) if fallback.exists() else None


def _validate_selections(
    raw: Any, source_end_seconds: float
) -> tuple[list[dict[str, Any]], str | None]:
    """Clamp to the source, drop degenerate windows, sort chronologically, and
    merge overlaps. Returns (selections, problem) — problem is a message for
    the agent when nothing usable was submitted."""
    selections: list[dict[str, Any]] = []
    for item in raw if isinstance(raw, list) else []:
        if not isinstance(item, dict):
            continue
        try:
            start = max(0.0, float(item["start_seconds"]))
            end = min(float(item["end_seconds"]), source_end_seconds)
        except (KeyError, TypeError, ValueError):
            continue
        if end - start < MIN_SELECTION_SECONDS:
            continue
        selections.append(
            {
                "start_seconds": round(start, 3),
                "end_seconds": round(end, 3),
                "reason": str(item.get("reason") or ""),
            }
        )
    if not selections:
        return [], (
            "No usable selections: every window was missing, outside the source, "
            f"or shorter than {MIN_SELECTION_SECONDS:g}s. Submit apply_edit again."
        )

    selections.sort(key=lambda s: (s["start_seconds"], s["end_seconds"]))
    merged = [selections[0]]
    for selection in selections[1:]:
        last = merged[-1]
        if selection["start_seconds"] < last["end_seconds"]:
            last["end_seconds"] = max(last["end_seconds"], selection["end_seconds"])
            continue
        merged.append(selection)
    return merged, None


def _apply_revision(
    *,
    state: dict[str, Any],
    records: ProjectRecords,
    db: SupabaseClient | None,
    edit: dict[str, Any],
    request: str,
    tools_used: list[str],
) -> dict[str, Any]:
    """Materialize the agent's selections into the next long-form version."""
    project_dir: Path = state["project_dir"]
    project_id: str = state["project_id"]
    version = state["current_version"] + 1
    segments_dir = project_dir / "longform" / "segments"

    entries: list[dict[str, Any]] = []
    for order, selection in enumerate(edit["selections"]):
        start, end = selection["start_seconds"], selection["end_seconds"]
        snap_info = None
        if state["cuts"]:
            start, end, snap_info = snap_window(
                start, end, cuts=state["cuts"], word_spans=state["word_spans"]
            )
        path, source = _materialize_selection(
            state=state,
            start_seconds=start,
            end_seconds=end,
            segments_dir=segments_dir,
            version=version,
            order=order,
        )
        entry = {
            "path": str(path),
            "start_seconds": start,
            "end_seconds": end,
            "title": source.get("title"),
            "reason": selection["reason"],
            "render_source": source["mode"],
        }
        if snap_info is not None:
            entry["boundary_snap"] = snap_info
        entries.append(entry)

    output_path = project_dir / "longform" / f"longform_v{version}.mp4"
    total_seconds = sum(entry["end_seconds"] - entry["start_seconds"] for entry in entries)
    print(
        f"Stitching {len(entries)} segment(s) (~{total_seconds / 60:.1f} min) into {output_path}"
    )
    stitch_clips(clip_paths=[Path(entry["path"]) for entry in entries], output_path=output_path)

    result: dict[str, Any] = {
        "status": "rendered",
        "version": version,
        "local_path": os.path.relpath(output_path),
        "filename": output_path.name,
        "segments": len(entries),
        "duration_seconds": round(total_seconds, 3),
        "segment_windows": [
            {
                "title": entry["title"],
                "start_seconds": entry["start_seconds"],
                "end_seconds": entry["end_seconds"],
                "reason": entry["reason"],
            }
            for entry in entries
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
        print(f"Revision thumbnail extraction failed (non-fatal): {exc}")

    revision_meta = {"request": request, "notes": edit["notes"], "tools_used": tools_used}
    clips_bucket = os.environ.get("SUPABASE_CLIPS_BUCKET", DEFAULT_SUPABASE_CLIPS_BUCKET)
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
                thumbnail_key = f"projects/{project_id}/longform/{thumbnail_path.name}"
                result["thumbnail_url"] = db.upload_storage_object(
                    bucket=clips_bucket, key=thumbnail_key, path=thumbnail_path
                )
                result["thumbnail_storage_path"] = thumbnail_key
            db.insert_longform_edit(
                project_id=project_id,
                version=version,
                status="rendered",
                video_url=video_url,
                thumbnail_url=result.get("thumbnail_url"),
                duration_seconds=round(total_seconds, 3),
                segments=result["segment_windows"],
                revision=revision_meta,
                metadata={"source": "revision", "render": result},
            )
        except Exception as exc:
            print(f"Revision upload failed (kept locally at {output_path}): {exc}")
            result["upload_error"] = str(exc)

    records.append_longform_edit(
        {
            "project_id": project_id,
            "version": version,
            "status": "rendered",
            "video_url": result.get("video_url") or result["local_path"],
            "thumbnail_url": result.get("thumbnail_url"),
            "duration_seconds": round(total_seconds, 3),
            "segments": result["segment_windows"],
            "editor": None,
            "revision": revision_meta,
            "metadata": {"source": "revision", "render": result},
        }
    )
    records.append_decision(
        {"type": "revision", "version": version, **revision_meta, "selections": entries}
    )

    metadata = {**(state["project"].get("metadata") or {}), "longform": result}
    records.update_project(metadata=metadata)
    if db is not None:
        try:
            remote = db.get_project(project_id).get("metadata") or {}
            db.update_project_metadata(project_id, {**remote, "longform": result})
        except Exception as exc:
            print(f"Project metadata update failed (non-fatal): {exc}")

    return result


def _materialize_selection(
    *,
    state: dict[str, Any],
    start_seconds: float,
    end_seconds: float,
    segments_dir: Path,
    version: int,
    order: int,
) -> tuple[Path, dict[str, Any]]:
    """The rendered file for one selection: an existing candidate render when
    the window matches, a trim of one when it fits inside, else a fresh fetch
    from the source URL."""
    for candidate in state["candidates"]:
        c_start, c_end = candidate["start_seconds"], candidate["end_seconds"]
        if (
            c_start - REUSE_TOLERANCE_SECONDS <= start_seconds
            and end_seconds <= c_end + REUSE_TOLERANCE_SECONDS
        ):
            raw_path = ((candidate.get("metadata") or {}).get("render") or {}).get("local_path")
            resolved = _resolve_media_path(raw_path, state["project_dir"], "clips")
            if resolved is None:
                continue
            if (
                start_seconds - c_start <= REUSE_TOLERANCE_SECONDS
                and c_end - end_seconds <= REUSE_TOLERANCE_SECONDS
            ):
                return Path(resolved), {"mode": "candidate", "title": candidate.get("title")}
            output_path = segments_dir / f"v{version}_{order:03d}.mp4"
            trim_clip(
                source_path=Path(resolved),
                output_path=output_path,
                start_offset_seconds=max(0.0, start_seconds - c_start),
                duration_seconds=end_seconds - start_seconds,
            )
            return output_path, {"mode": "trimmed", "title": candidate.get("title")}

    if not state["source_url"]:
        raise RuntimeError(
            f"Selection {start_seconds:.1f}s-{end_seconds:.1f}s is outside every rendered "
            "candidate and the project has no source URL to fetch from"
        )
    print(
        f"Fetching {start_seconds:.1f}s-{end_seconds:.1f}s from the source "
        "(outside every rendered candidate)"
    )
    output_path = segments_dir / f"v{version}_{order:03d}.mp4"
    render_clip_from_video_url(
        source_url=state["source_url"],
        output_path=output_path,
        start_seconds=start_seconds,
        end_seconds=end_seconds,
    )
    return output_path, {"mode": "fetched", "title": None}


def _parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Revise a finished long-form edit with a natural-language request.",
    )
    parser.add_argument("project_id", help="Project id under <output-root>/projects/.")
    parser.add_argument("request", help='The revision, e.g. "tighten the middle, cut the sponsor talk".')
    parser.add_argument(
        "--output-root",
        default=None,
        help="Directory holding project outputs (default: 'outputs', also via OUTPUT_ROOT).",
    )
    return parser.parse_args()


if __name__ == "__main__":
    main()
