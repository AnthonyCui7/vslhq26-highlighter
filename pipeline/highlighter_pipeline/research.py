"""Content research layer: one research call per run.

The Azure OpenAI editor deployment takes the first attempt when configured;
otherwise (or on any failure) the call runs as a single web-grounded Claude
Sonnet 5 request through OpenRouter's web-search plugin. Either way the result
is the same structured, source-cited editorial context — target audience,
content genre and current trends, successful clip formats and thumbnails, and
the creator's background — that feeds the clip-selection model as
`research_context` (llm.py threads it into the scoring prompt).
"""

import json
import os
from typing import Any

from .defaults import DEFAULT_RESEARCH_MODEL
from .providers import OPENROUTER_BASE_URL, Provider, azure_editor_provider

CONTENT_RESEARCH_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {
        "creator_profile": {"type": "string"},
        "content_context": {"type": "string"},
        "target_audience": {"type": "array", "items": {"type": "string"}},
        "inside_references": {"type": "array", "items": {"type": "string"}},
        "recent_context": {"type": "array", "items": {"type": "string"}},
        "successful_clip_patterns": {"type": "array", "items": {"type": "string"}},
        "thumbnail_patterns": {"type": "array", "items": {"type": "string"}},
        "platform_notes": {"type": "array", "items": {"type": "string"}},
        "useful_hooks": {"type": "array", "items": {"type": "string"}},
        "avoid": {"type": "array", "items": {"type": "string"}},
        "sources": {
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
        },
    },
    "required": [
        "creator_profile",
        "content_context",
        "target_audience",
        "inside_references",
        "recent_context",
        "successful_clip_patterns",
        "thumbnail_patterns",
        "platform_notes",
        "useful_hooks",
        "avoid",
        "sources",
    ],
    "additionalProperties": False,
}


RESEARCH_SYSTEM_PROMPT = """You are a content researcher for an AI video editing
pipeline. Given a source video (creator/channel, platform, live or VOD), use
web search to build practical, current editorial context for the editor model
that will select clips from it.

Research and report:
- Who the creator is and what their content/channel is about.
- The target audience: who watches this, what they care about.
- Inside references: recurring jokes, lore, memes, nicknames, callbacks, and
  running bits this creator's audience recognizes instantly.
- Recent context: what has happened lately with this creator and in this niche
  (events, drama, releases, discourse) that a moment might reference.
- The content genre and what is currently trending or discussed in it.
- Clip formats and editing patterns that perform well for similar content.
- Thumbnail and title patterns that work in this niche.
- What to avoid (overdone formats, sensitive topics for this audience).

Rules:
- Keep every field compact and practical: the consumer is another LLM making
  clip decisions, not a human reading a report.
- Every factual outside-world claim must be backed by an entry in sources with
  a real URL you found via search.
- If you cannot find much about this specific creator, research the genre and
  platform patterns instead and say so in creator_profile.
- Return ONLY one JSON object matching the provided schema. No markdown fences,
  no commentary before or after."""


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
    CONTENT_RESEARCH_SCHEMA. Raises on failure; callers treat research as
    best-effort and continue without it."""
    prompt = _research_prompt(
        source_context=source_context,
        pipeline_mode=pipeline_mode,
        user_instructions=user_instructions,
    )
    provider = azure_editor_provider()
    if provider is not None:
        try:
            return _request_azure_research(provider, prompt)
        except Exception as exc:
            print(
                f"{provider.label} research failed; falling back to "
                f"web-grounded {model}: {exc}"
            )
    return _web_grounded_research(prompt, model=model)


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
        json.dumps(CONTENT_RESEARCH_SCHEMA, indent=2),
    ]
    return "\n".join(prompt_parts)


def _request_azure_research(provider: Provider, prompt: str) -> dict[str, Any]:
    with provider.client(timeout=300.0) as client:
        response = client.chat.completions.create(
            messages=[
                {"role": "system", "content": RESEARCH_SYSTEM_PROMPT},
                {"role": "user", "content": prompt},
            ],
            response_format={
                "type": "json_schema",
                "json_schema": {
                    "name": "content_research",
                    "schema": CONTENT_RESEARCH_SCHEMA,
                },
            },
            **provider.request_kwargs(),
        )
    if not response.choices or not response.choices[0].message.content:
        raise RuntimeError(f"{provider.label} research response did not include content")
    context = _json_from_text(response.choices[0].message.content)
    if not isinstance(context, dict):
        raise RuntimeError(f"{provider.label} research response was not a JSON object")
    context.setdefault("sources", [])
    return context


def _web_grounded_research(prompt: str, *, model: str) -> dict[str, Any]:
    try:
        from openai import OpenAI
    except ImportError as exc:
        raise RuntimeError("Content research requires the 'openai' package") from exc

    api_key = os.environ.get("OPENROUTER_API_KEY")
    if not api_key:
        raise RuntimeError("OPENROUTER_API_KEY is required for content research")

    client = OpenAI(
        base_url=OPENROUTER_BASE_URL,
        api_key=api_key,
        timeout=300.0,
        default_headers={
            "HTTP-Referer": "http://localhost",
            "X-OpenRouter-Title": "highlighter research",
        },
    )
    response = client.chat.completions.create(
        model=model,
        messages=[
            {"role": "system", "content": RESEARCH_SYSTEM_PROMPT},
            {"role": "user", "content": prompt},
        ],
        temperature=0.3,
        max_tokens=8000,
        extra_body={"plugins": [{"id": "web", "max_results": 5}]},
    )

    if not response.choices:
        raise RuntimeError("Research response did not include choices")
    content = response.choices[0].message.content
    if not content:
        raise RuntimeError("Research response did not include text content")

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
