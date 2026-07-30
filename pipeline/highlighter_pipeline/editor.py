"""Long-form pass 2: one global editor call makes the final cut.

Pass 1 (chunked scoring) proposes candidate segments and renders each one.
This runs once at the end of a long-form run with the full ordered candidate
list — timings, titles, summaries, scores — plus the budget arithmetic
(source minutes, candidate minutes, target minutes -> keep rate), the research
context, and the user's instructions. It returns the final selection: which
segments make the edit, each optionally tightened within its own window.
Assembly (trims + stitch) stays in ingest.py; any failure here falls back to
stitching every pass-1 selection, so pass 2 can only improve a run.
"""

import json
from typing import Any

from .defaults import (
    DEFAULT_LLM_REASONING_EFFORT,
    DEFAULT_TARGET_LENGTH_MINUTES,
)
from .agents import run_agent_text
from .llm import cap_target_minutes, parse_target_minutes
from .providers import Provider, editor_providers, run_with_fallback

# Above this, the weakest candidates are dropped from the prompt: a model
# wading through hundreds of mediocre candidates anchors worse than one
# reading a strong shortlist, and pass 1 should be tightened instead.
MAX_PROMPT_CANDIDATES = 150
# Selections tighter than this are editorial noise, not an edit.
MIN_SELECTION_SECONDS = 2.0


EDITOR_SYSTEM_PROMPT = """You are the supervising editor making the final cut of one long-form video.

A first-pass team went through the full source chronologically and nominated
candidate segments — each with timings, a working title, a summary, a 0.0-1.0
score for how essential it seemed, and the reason it was nominated. Every
candidate is already rendered; the final video will be exactly the segments
you keep, concatenated in strict source-chronological order. Never plan an
edit that depends on reordering — selections are always assembled in source
order regardless of the order you return them.

Your job is the actual edit:
- Keep the candidates that belong in the final video and drop the rest.
- Cut redundant beats: when two candidates cover the same story, joke, or
  point, keep the stronger one.
- Prefer a coherent, watchable arc — setup before payoff, context before
  callbacks — over a highlight reel of disconnected peaks.
- You may tighten a kept segment by moving its start later and/or its end
  earlier within that segment's own window; never extend beyond it. Tighten
  only to drop real dead weight at the edges, and never cut a spoken thought
  in half.
- The user's instructions, when present, take priority over general taste.

Budget:
The brief states the target runtime, the minutes of candidate material, and
the keep rate they imply. Treat the target as guidance from the client: land
reasonably close, and when in doubt prefer dropping a weak segment over
padding the runtime. Scores are advisory input from the first pass, not a
ranking you must obey.

Return only JSON matching the schema: one selections entry per kept segment
(the candidate's index, your possibly-tightened start_seconds and end_seconds,
and a short reason), plus edit_notes briefly explaining the shape of the edit
and the main things you dropped, plus title — the title the finished video
should publish under, shaped by the research context's title_patterns when
present."""


EDIT_RESPONSE_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {
        "selections": {
            "type": "array",
            "description": "One entry per candidate segment kept in the final edit.",
            "items": {
                "type": "object",
                "properties": {
                    "index": {
                        "type": "integer",
                        "description": "The kept candidate's index from the candidate list.",
                    },
                    "start_seconds": {
                        "type": "number",
                        "description": "Final start, at or after the candidate's own start.",
                    },
                    "end_seconds": {
                        "type": "number",
                        "description": "Final end, at or before the candidate's own end.",
                    },
                    "reason": {
                        "type": "string",
                        "description": "Brief reason this segment earns its place.",
                    },
                },
                "required": ["index", "start_seconds", "end_seconds", "reason"],
                "additionalProperties": False,
            },
        },
        "edit_notes": {
            "type": "string",
            "description": "Brief explanation of the edit's shape and the main drops.",
        },
        "title": {
            "type": "string",
            "description": "The published video's title, shaped by the research "
            "title_patterns when present.",
        },
    },
    "required": ["selections", "edit_notes", "title"],
    "additionalProperties": False,
}


def select_longform_segments(
    *,
    candidates: list[dict[str, Any]],
    source_minutes: float,
    target_length: str | None,
    research_context: dict[str, Any] | None = None,
    user_instructions: str | None = None,
    reasoning_effort: str = DEFAULT_LLM_REASONING_EFFORT,
) -> dict[str, Any]:
    """Run the global editor call over pass-1 candidates (chronologically
    sorted dicts with start_seconds/end_seconds/title/description/score/
    reason). Returns {selections, edit_notes, arithmetic, model, ...}; raises
    on failure — the caller falls back to stitching every candidate."""
    indexed_candidates, dropped_from_prompt = _cap_candidates(candidates)
    if dropped_from_prompt:
        print(
            f"Long-form editor prompt capped at {MAX_PROMPT_CANDIDATES} candidates by score; "
            f"dropped the {dropped_from_prompt} weakest of {len(candidates)}"
        )
    arithmetic = _budget_arithmetic(
        [candidate for _, candidate in indexed_candidates],
        source_minutes=source_minutes,
        target_length=target_length,
    )

    user_prompt = _build_user_prompt(
        indexed_candidates,
        arithmetic=arithmetic,
        research_context=research_context,
        user_instructions=user_instructions,
    )
    providers = editor_providers(
        title="highlighter editor",
        openrouter_reasoning_effort=reasoning_effort or DEFAULT_LLM_REASONING_EFFORT,
    )
    decision, provider = run_with_fallback(
        providers, lambda candidate_provider: _request_edit(candidate_provider, user_prompt)
    )

    return {
        "selections": _validate_selections(decision, candidates, dict(indexed_candidates)),
        "edit_notes": str(decision.get("edit_notes") or ""),
        "title": str(decision.get("title") or "").strip() or None,
        "arithmetic": arithmetic,
        "model": provider.model,
        "candidates_considered": len(indexed_candidates),
        "candidates_dropped_from_prompt": dropped_from_prompt,
    }


def _request_edit(provider: Provider, user_prompt: str) -> dict[str, Any]:
    content = run_agent_text(
        provider=provider,
        instructions=EDITOR_SYSTEM_PROMPT,
        prompt=user_prompt,
        timeout=300.0,
        extra_options={
            "response_format": {
                "type": "json_schema",
                "json_schema": {"name": "longform_edit", "schema": EDIT_RESPONSE_SCHEMA},
            }
        },
    )
    return _json_from_text(content)


def _cap_candidates(
    candidates: list[dict[str, Any]],
) -> tuple[list[tuple[int, dict[str, Any]]], int]:
    """Cap the prompt to the strongest MAX_PROMPT_CANDIDATES, keeping
    chronological order and each candidate's original index (selections index
    into the caller's full list). Returns (indexed candidates, dropped count)."""
    indexed = list(enumerate(candidates))
    if len(indexed) <= MAX_PROMPT_CANDIDATES:
        return indexed, 0
    strongest = sorted(indexed, key=lambda pair: pair[1].get("score") or 0, reverse=True)
    kept = {index for index, _ in strongest[:MAX_PROMPT_CANDIDATES]}
    return [pair for pair in indexed if pair[0] in kept], len(indexed) - MAX_PROMPT_CANDIDATES


def _budget_arithmetic(
    candidates: list[dict[str, Any]],
    *,
    source_minutes: float,
    target_length: str | None,
) -> dict[str, Any]:
    """The precomputed numbers the editor calibrates against, in minutes."""
    candidate_minutes = (
        sum(c["end_seconds"] - c["start_seconds"] for c in candidates) / 60
    )
    target = cap_target_minutes(
        target_length or DEFAULT_TARGET_LENGTH_MINUTES, source_minutes
    )
    arithmetic: dict[str, Any] = {
        "source_minutes": round(source_minutes, 1),
        "candidate_count": len(candidates),
        "candidate_minutes": round(candidate_minutes, 1),
        "target_minutes": target,
    }
    bounds = parse_target_minutes(target)
    if bounds and candidate_minutes > 0:
        midpoint = (bounds[0] + bounds[1]) / 2
        arithmetic["keep_percent"] = max(1, min(100, round(100 * midpoint / candidate_minutes)))
    return arithmetic


def _build_user_prompt(
    indexed_candidates: list[tuple[int, dict[str, Any]]],
    *,
    arithmetic: dict[str, Any],
    research_context: dict[str, Any] | None,
    user_instructions: str | None,
) -> str:
    brief = [
        "Final-cut brief:",
        f"- Source: roughly {arithmetic['source_minutes']} minutes captured.",
        f"- Candidates: {arithmetic['candidate_count']} segments totaling "
        f"{arithmetic['candidate_minutes']} minutes.",
        f"- Target runtime: {arithmetic['target_minutes']} minutes.",
    ]
    if "keep_percent" in arithmetic:
        brief.append(
            f"- Keep rate: roughly {arithmetic['keep_percent']}% of the candidate "
            "minutes should survive. Be harsh at a low rate, looser at a high one."
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

    lines = [
        f"{index}. [{c['start_seconds']:.1f}s-{c['end_seconds']:.1f}s | "
        f"{c['end_seconds'] - c['start_seconds']:.1f}s | score {c.get('score') if c.get('score') is not None else '?'}] "
        f"{c.get('title') or 'Untitled'} — {c.get('description') or '(no summary)'}"
        + (f" (nominated: {c['reason']})" if c.get("reason") else "")
        for index, c in indexed_candidates
    ]

    return "\n".join(
        [
            *brief,
            "",
            *instructions_block,
            "Content research context:",
            json.dumps(research_context or {}, indent=2),
            "",
            "Candidate segments (chronological):",
            *lines,
        ]
    )


def _validate_selections(
    decision: dict[str, Any],
    candidates: list[dict[str, Any]],
    offered: dict[int, dict[str, Any]],
) -> list[dict[str, Any]]:
    """Normalize the model's selections: drop unknown/duplicate indexes, clamp
    boundaries into each candidate's own window (tighten-only), drop windows
    shorter than MIN_SELECTION_SECONDS, and sort chronologically. Indexes refer
    to the caller's full candidate list; only offered ones are valid."""
    raw = decision.get("selections") if isinstance(decision.get("selections"), list) else []
    selections: list[dict[str, Any]] = []
    seen: set[int] = set()
    for item in raw:
        if not isinstance(item, dict):
            continue
        try:
            index = int(item.get("index"))
        except (TypeError, ValueError):
            continue
        if index in seen or index not in offered:
            continue
        seen.add(index)
        candidate = candidates[index]
        start = _clamp(
            item.get("start_seconds"), candidate["start_seconds"], candidate["end_seconds"]
        )
        end = _clamp(
            item.get("end_seconds"), candidate["start_seconds"], candidate["end_seconds"]
        )
        if end - start < MIN_SELECTION_SECONDS:
            continue
        selections.append(
            {
                "index": index,
                "start_seconds": round(start, 3),
                "end_seconds": round(end, 3),
                "reason": str(item.get("reason") or ""),
            }
        )
    return sorted(selections, key=lambda s: s["start_seconds"])


def _clamp(value: Any, minimum: float, maximum: float) -> float:
    try:
        number = float(value)
    except (TypeError, ValueError):
        number = minimum
    return min(max(number, minimum), maximum)


def _json_from_text(text: str) -> dict[str, Any]:
    stripped = text.strip()
    if stripped.startswith("```"):
        stripped = stripped.strip("`").strip()
        if stripped.startswith("json"):
            stripped = stripped[4:].strip()
    return json.loads(stripped)
