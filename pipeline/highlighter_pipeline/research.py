"""Content research layer: one research call per run, specialized per mode.

The Azure OpenAI editor deployment takes the first attempt when configured;
otherwise (or on any failure) the call runs as a single web-grounded Claude
Sonnet 5 request through OpenRouter's web-search plugin. Either way the result
is structured, source-cited editorial context — the creator's background, the
target audience, and what performs in this niche — that feeds the editing
models as `research_context`. The prompt and schema are specialized per
pipeline mode: short form researches clip formats, hooks, and platform norms;
long form researches video structure, pacing, and retention.
"""

import json
import os
from typing import Any

from .defaults import DEFAULT_RESEARCH_MODEL
from .agents import run_agent_text
from .providers import OPENROUTER_BASE_URL, Provider, azure_editor_provider

# Fields both modes research: who the creator is, who watches, and what the
# audience already knows. The mode-specific fields below cover what "performs
# well" means for that output format.
_CORE_RESEARCH_PROPERTIES: dict[str, Any] = {
    "creator_profile": {"type": "string"},
    "content_context": {"type": "string"},
    "target_audience": {"type": "array", "items": {"type": "string"}},
    "inside_references": {"type": "array", "items": {"type": "string"}},
    "recent_context": {"type": "array", "items": {"type": "string"}},
    "thumbnail_patterns": {"type": "array", "items": {"type": "string"}},
    "avoid": {"type": "array", "items": {"type": "string"}},
}

_SHORTFORM_RESEARCH_PROPERTIES: dict[str, Any] = {
    "successful_clip_patterns": {"type": "array", "items": {"type": "string"}},
    "useful_hooks": {"type": "array", "items": {"type": "string"}},
    "platform_notes": {"type": "array", "items": {"type": "string"}},
}

_LONGFORM_RESEARCH_PROPERTIES: dict[str, Any] = {
    "structure_patterns": {"type": "array", "items": {"type": "string"}},
    "pacing_and_retention": {"type": "array", "items": {"type": "string"}},
    "title_patterns": {"type": "array", "items": {"type": "string"}},
}

_SOURCES_PROPERTY: dict[str, Any] = {
    "type": "array",
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
}


def research_schema(pipeline_mode: str) -> dict[str, Any]:
    """The research response schema for a pipeline mode ('short' or 'long')."""
    properties = {
        **_CORE_RESEARCH_PROPERTIES,
        **(
            _SHORTFORM_RESEARCH_PROPERTIES
            if pipeline_mode == "short"
            else _LONGFORM_RESEARCH_PROPERTIES
        ),
        "sources": _SOURCES_PROPERTY,
    }
    return {
        "type": "object",
        "properties": properties,
        "required": list(properties),
        "additionalProperties": False,
    }


_RESEARCH_SYSTEM_PROMPT_TEMPLATE = """You are a content researcher for an AI video editing
pipeline. Given a source video (creator/channel, platform, live or VOD), use
web search to build practical, current editorial context for the editor model
that will {editing_goal}.

Research and report:
- Who the creator is and what their content/channel is about.
- The target audience: who watches this, what they care about.
- Inside references: recurring jokes, lore, memes, nicknames, callbacks, and
  running bits this creator's audience recognizes instantly.
- Recent context: what has happened lately with this creator and in this niche
  (events, drama, releases, discourse) that a moment might reference.
- The content genre and what is currently trending or discussed in it.
{mode_points}

Rules:
- Keep every field compact and practical: the consumer is another LLM making
  editing decisions, not a human reading a report.
- Every factual outside-world claim must be backed by an entry in sources with
  a real URL you found via search.
- If you cannot find much about this specific creator, research the genre and
  platform patterns instead and say so in creator_profile.
- Return ONLY one JSON object matching the provided schema. No markdown fences,
  no commentary before or after."""

_SHORTFORM_RESEARCH_POINTS = """- Clip formats and editing patterns that perform well as short-form vertical
  video for similar content.
- Hooks that work in this niche: cold opens, first-line patterns, what stops
  the scroll.
- Platform notes: what TikTok/Reels/Shorts/X each reward or punish for this
  kind of content.
- Thumbnail patterns that work in this niche.
- What to avoid (overdone formats, sensitive topics for this audience)."""

_LONGFORM_RESEARCH_POINTS = """- Structures that hold viewers in long-form videos for this niche: how
  successful videos open, how they are sequenced or chaptered, how they close.
- Pacing and retention patterns: where similar videos lose viewers, and what
  editing rhythm keeps them watching.
- Title and thumbnail patterns that work for long uploads in this niche.
- What to avoid (overdone formats, sensitive topics for this audience)."""


def research_system_prompt(pipeline_mode: str) -> str:
    """The research system prompt for a pipeline mode ('short' or 'long')."""
    if pipeline_mode == "short":
        return _RESEARCH_SYSTEM_PROMPT_TEMPLATE.format(
            editing_goal="select short-form vertical clips from it",
            mode_points=_SHORTFORM_RESEARCH_POINTS,
        )
    return _RESEARCH_SYSTEM_PROMPT_TEMPLATE.format(
        editing_goal="cut one long-form edit from it",
        mode_points=_LONGFORM_RESEARCH_POINTS,
    )


def research_backend_label() -> str:
    provider = azure_editor_provider()
    if provider is not None:
        return f"{provider.label} (fallback: web-grounded {DEFAULT_RESEARCH_MODEL})"
    return f"web-grounded {DEFAULT_RESEARCH_MODEL}"


def research_content_context(
    *,
    source_context: dict[str, Any],
    pipeline_mode: str = "short",
    user_instructions: str | None = None,
    model: str = DEFAULT_RESEARCH_MODEL,
) -> dict[str, Any]:
    """Run the research call and return a dict matching
    research_schema(pipeline_mode). Raises on failure; callers treat research
    as best-effort and continue without it."""
    prompt = _research_prompt(
        source_context=source_context,
        pipeline_mode=pipeline_mode,
        user_instructions=user_instructions,
    )
    system_prompt = research_system_prompt(pipeline_mode)
    schema = research_schema(pipeline_mode)
    provider = azure_editor_provider()
    if provider is not None:
        try:
            return _request_azure_research(
                provider, prompt, system_prompt=system_prompt, schema=schema
            )
        except Exception as exc:
            print(
                f"{provider.label} research failed; falling back to "
                f"web-grounded {model}: {exc}"
            )
    return _web_grounded_research(prompt, model=model, system_prompt=system_prompt)


def _research_prompt(
    *,
    source_context: dict[str, Any],
    pipeline_mode: str,
    user_instructions: str | None,
) -> str:
    goal = (
        "The editor is cutting short-form vertical clips (TikTok/Reels/Shorts/X) from this source."
        if pipeline_mode == "short"
        else "The editor is cutting one long-form edit (a YouTube-ready video) from this source."
    )
    prompt_parts = [
        "Source video:",
        json.dumps(source_context, indent=2),
        "",
        goal,
    ]
    if user_instructions:
        prompt_parts += [
            "",
            "The user gave the editor these instructions (use them to focus your research):",
            user_instructions,
        ]
    prompt_parts += [
        "",
        "Return only a JSON object matching this schema:",
        json.dumps(research_schema(pipeline_mode), indent=2),
    ]
    return "\n".join(prompt_parts)


def _request_azure_research(
    provider: Provider, prompt: str, *, system_prompt: str, schema: dict[str, Any]
) -> dict[str, Any]:
    content = run_agent_text(
        provider=provider,
        instructions=system_prompt,
        prompt=prompt,
        timeout=300.0,
        extra_options={
            "response_format": {
                "type": "json_schema",
                "json_schema": {
                    "name": "content_research",
                    "schema": schema,
                },
            }
        },
    )
    context = _json_from_text(content)
    if not isinstance(context, dict):
        raise RuntimeError(f"{provider.label} research response was not a JSON object")
    context.setdefault("sources", [])
    return context


def _web_grounded_research(
    prompt: str, *, model: str, system_prompt: str
) -> dict[str, Any]:
    api_key = os.environ.get("OPENROUTER_API_KEY")
    if not api_key:
        raise RuntimeError("OPENROUTER_API_KEY is required for content research")

    provider = Provider(
        name="openrouter",
        label=f"Web-grounded research ({model})",
        base_url=OPENROUTER_BASE_URL,
        api_key=api_key,
        model=model,
        temperature=0.3,
        extra_body={"plugins": [{"id": "web", "max_results": 5}]},
        default_headers={
            "HTTP-Referer": "http://localhost",
            "X-OpenRouter-Title": "highlighter research",
        },
    )
    content = run_agent_text(
        provider=provider,
        instructions=system_prompt,
        prompt=prompt,
        timeout=300.0,
        extra_options={"max_tokens": 8000},
    )
    context = _json_from_text(content)
    if not isinstance(context, dict):
        raise RuntimeError("Research response was not a JSON object")
    context.setdefault("sources", [])
    return context


def _json_from_text(text: str) -> Any:
    stripped = text.strip()
    if stripped.startswith("```"):
        stripped = stripped.strip("`").strip()
        if stripped.startswith("json"):
            stripped = stripped[4:].strip()
    # Web-grounded responses sometimes lead with prose despite instructions;
    # fall back to the outermost JSON object.
    try:
        return json.loads(stripped)
    except json.JSONDecodeError:
        start = stripped.find("{")
        end = stripped.rfind("}")
        if start == -1 or end <= start:
            raise
        return json.loads(stripped[start : end + 1])


def main() -> None:
    """Rerun content research for a finished project and cache the refreshed
    context in the project's records."""
    import argparse

    from .config import load_env
    from .defaults import DEFAULT_OUTPUT_ROOT
    from .records import ProjectRecords
    from .supabase_client import SupabaseClient

    load_env()
    parser = argparse.ArgumentParser(description=main.__doc__)
    parser.add_argument("project_id")
    parser.add_argument("--mode", choices=["short", "long"], default="long")
    parser.add_argument("--focus", default=None, help="What the research should dig into.")
    parser.add_argument("--output-root", default=None)
    args = parser.parse_args()

    from pathlib import Path

    output_root = Path(args.output_root or os.environ.get("OUTPUT_ROOT", DEFAULT_OUTPUT_ROOT))
    project_dir = output_root / "projects" / args.project_id
    if not (project_dir / "project.json").exists():
        raise RuntimeError(f"No local project record at {project_dir}")
    records = ProjectRecords(project_dir)
    project = json.loads((project_dir / "project.json").read_text())
    metadata = project.get("metadata") or {}
    ingest = metadata.get("ingest") or {}

    instructions = "\n".join(
        part
        for part in (
            ingest.get("user_instructions"),
            f"Research focus: {args.focus}" if args.focus else None,
        )
        if part
    )
    context = research_content_context(
        source_context={
            "platform": metadata.get("platform"),
            "source_type": project.get("source_type"),
            "name": project.get("name"),
            "source_url": project.get("source_url"),
        },
        pipeline_mode=args.mode,
        user_instructions=instructions or None,
    )
    records.write_research(context, mode="short" if args.mode == "short" else None)

    key = "content_research_short" if args.mode == "short" else "content_research"
    records.update_project(metadata={**metadata, key: context})
    if not args.project_id.startswith("local-"):
        db = SupabaseClient()
        remote = db.get_project(args.project_id).get("metadata") or {}
        db.update_project_metadata(args.project_id, {**remote, key: context})
    sources = len(context.get("sources") or [])
    print(f"Refreshed {args.mode}-form research with {sources} source(s)")
