"""Thumbnail generation for long-form videos.

One editor-chain call turns the research context, the video title, and the
kept segments into three distinct thumbnail concepts (different art
directions, not variations of one idea). Each concept is rendered by an
Azure-hosted image deployment, seeded with reference frames pulled from the
stitched video so the thumbnails show the actual people and scenes. One
variant is picked at random as the default; the publish step lets a human
browse all of them or import their own.
"""

import base64
import json
import os
import random
import tempfile
import time
import uuid
from pathlib import Path
from typing import Any
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen

from .config import required_env
from .defaults import DEFAULT_LLM_REASONING_EFFORT, DEFAULT_THUMBNAIL_MODEL
from .agents import run_agent_text
from .llm import _json_from_text
from .providers import (
    OPENROUTER_BASE_URL,
    OPENROUTER_VERTEX_PROVIDER,
    Provider,
    editor_providers,
    run_with_fallback,
)
from .render import extract_thumbnail

THUMBNAIL_COUNT = 3
MAX_REFERENCE_FRAMES = 4
REFERENCE_FRAME_WIDTH = 1280
# Back-to-back image calls can trip the provider's rate limit; one paced
# retry per variant rides it out.
IMAGE_RETRY_SECONDS = 20

THUMBNAIL_CONCEPTS_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {
        "concepts": {
            "type": "array",
            "description": "Exactly three thumbnail concepts, each a distinct "
            "art direction.",
            "items": {
                "type": "object",
                "properties": {
                    "direction": {
                        "type": "string",
                        "description": "Short name for this concept's art direction.",
                    },
                    "image_prompt": {
                        "type": "string",
                        "description": "Complete prompt for the image model: "
                        "composition, subject, mood, colors, and how to use the "
                        "reference frames.",
                    },
                    "overlay_text": {
                        "type": "string",
                        "description": "Text drawn on the thumbnail, a few words "
                        "at most. Empty string for none.",
                    },
                },
                "required": ["direction", "image_prompt", "overlay_text"],
                "additionalProperties": False,
            },
        },
    },
    "required": ["concepts"],
    "additionalProperties": False,
}

CONCEPTS_SYSTEM_PROMPT = """\
You are the thumbnail designer for a video creator. You receive the finished
video's title, the segments it contains, and research about the creator and
what performs in their niche.

Design exactly three thumbnail concepts in three DISTINCT art directions —
three genuinely different ideas a designer would pitch side by side, not
small variations of one idea. Lean on the research's thumbnail_patterns for
what works in this niche, and on the segments for the moments worth showing.
The image model will receive real frames from the video as references; write
each image_prompt to build on those frames (the actual people, setting, and
scene), name which visual elements to keep, and describe composition, mood,
and color. Thumbnails are 16:9 and must read clearly at small sizes: bold
subjects, high contrast, minimal clutter. overlay_text is the on-image text —
keep it to a few punchy words, or empty when the concept works without text.

Return ONLY one JSON object matching the schema."""


def generate_thumbnails(
    *,
    longform_path: Path,
    entries: list[dict],
    title: str | None,
    research_context: dict | None,
    output_dir: Path,
    reasoning_effort: str = DEFAULT_LLM_REASONING_EFFORT,
    guidance: str | None = None,
    start_index: int = 1,
) -> dict | None:
    """Generate up to THUMBNAIL_COUNT thumbnail variants for a stitched
    long-form video. Returns {"variants": [{index, direction, overlay_text,
    local_path}], "selected_index": int} or None when nothing rendered.
    guidance folds a human's direction into the concept call; start_index
    continues an existing variant numbering."""
    concepts = _thumbnail_concepts(
        entries=entries,
        title=title,
        research_context=research_context,
        reasoning_effort=reasoning_effort,
        guidance=guidance,
    )
    reference_images = _reference_images(longform_path, entries)

    output_dir.mkdir(parents=True, exist_ok=True)
    variants: list[dict] = []
    for number, concept in enumerate(concepts, start=start_index):
        prompt = _image_prompt(concept, title)
        try:
            try:
                image_bytes, extension = _generate_image(
                    prompt=prompt, reference_images=reference_images
                )
            except Exception:
                time.sleep(IMAGE_RETRY_SECONDS)
                image_bytes, extension = _generate_image(
                    prompt=prompt, reference_images=reference_images
                )
            output_path = output_dir / f"thumbnail_{number}{extension}"
            output_path.write_bytes(image_bytes)
        except Exception as exc:
            print(f"Thumbnail {number} generation failed (non-fatal): {exc}")
            continue
        variants.append(
            {
                "index": number,
                "direction": concept["direction"],
                "overlay_text": concept["overlay_text"],
                "local_path": str(output_path),
            }
        )

    if not variants:
        return None
    selected = random.choice(range(len(variants)))
    print(
        f"Generated {len(variants)} thumbnail variant(s); "
        f"defaulting to #{variants[selected]['index']} ({variants[selected]['direction']})"
    )
    return {"variants": variants, "selected_index": selected}


def _thumbnail_concepts(
    *,
    entries: list[dict],
    title: str | None,
    research_context: dict | None,
    reasoning_effort: str,
    guidance: str | None = None,
) -> list[dict]:
    prompt = _concepts_prompt(
        entries=entries, title=title, research_context=research_context, guidance=guidance
    )
    providers = editor_providers(
        title="highlighter thumbnails", openrouter_reasoning_effort=reasoning_effort
    )
    decision, _provider = run_with_fallback(
        providers, lambda provider: _request_concepts(provider, prompt)
    )
    concepts = [
        concept
        for concept in decision.get("concepts", [])
        if isinstance(concept, dict) and concept.get("image_prompt")
    ]
    if not concepts:
        raise RuntimeError("thumbnail concept call returned no usable concepts")
    return concepts[:THUMBNAIL_COUNT]


def _concepts_prompt(
    *,
    entries: list[dict],
    title: str | None,
    research_context: dict | None,
    guidance: str | None = None,
) -> str:
    lines = ["Video brief:"]
    if title:
        lines.append(f"- Title: {title}")
    if guidance:
        lines.append(f"- Human guidance: {guidance}")
    lines.append("- Segments, in order:")
    for entry in entries:
        summary = entry.get("description") or ""
        lines.append(f"  - {entry.get('title') or 'Untitled'}: {summary}".rstrip(": "))
    if research_context:
        lines.append("")
        lines.append("Research context:")
        lines.append(json.dumps(research_context, indent=2))
    return "\n".join(lines)


def _request_concepts(provider: Provider, prompt: str) -> dict[str, Any]:
    content = run_agent_text(
        provider=provider,
        instructions=CONCEPTS_SYSTEM_PROMPT,
        prompt=prompt,
        timeout=180.0,
        extra_options={
            "response_format": {
                "type": "json_schema",
                "json_schema": {
                    "name": "thumbnail_concepts",
                    "schema": THUMBNAIL_CONCEPTS_SCHEMA,
                },
            }
        },
    )
    return _json_from_text(content)


def _image_prompt(concept: dict, title: str | None) -> str:
    parts = [concept["image_prompt"]]
    overlay = (concept.get("overlay_text") or "").strip()
    if overlay:
        parts.append(f'Render the text "{overlay}" boldly on the image, large and legible.')
    else:
        parts.append("Do not render any text on the image.")
    if title:
        parts.append(f'The video is titled "{title}".')
    parts.append("16:9 video thumbnail; must read clearly at small sizes.")
    return " ".join(parts)


def _reference_images(longform_path: Path, entries: list[dict]) -> list[str]:
    """Sample frames at kept-segment midpoints (in the stitched video's own
    timeline) and return them as data URIs for the image model."""
    images: list[str] = []
    with tempfile.TemporaryDirectory(prefix="thumbnails-") as tmp:
        for index, at_seconds in enumerate(_reference_offsets(entries)):
            frame_path = Path(tmp) / f"frame_{index}.jpg"
            try:
                extract_thumbnail(
                    clip_path=longform_path,
                    output_path=frame_path,
                    at_seconds=at_seconds,
                    max_width=REFERENCE_FRAME_WIDTH,
                )
            except Exception as exc:
                print(f"Thumbnail reference frame at {at_seconds:.1f}s failed: {exc}")
                continue
            encoded = base64.b64encode(frame_path.read_bytes()).decode("ascii")
            images.append(f"data:image/jpeg;base64,{encoded}")
    return images


def _reference_offsets(entries: list[dict]) -> list[float]:
    """Midpoints of up to MAX_REFERENCE_FRAMES kept segments, expressed in the
    stitched output's timeline (segments are concatenated in order)."""
    midpoints: list[float] = []
    elapsed = 0.0
    for entry in entries:
        duration = entry["end_seconds"] - entry["start_seconds"]
        midpoints.append(elapsed + duration / 2)
        elapsed += duration
    if len(midpoints) <= MAX_REFERENCE_FRAMES:
        return midpoints
    step = (len(midpoints) - 1) / (MAX_REFERENCE_FRAMES - 1)
    return [midpoints[round(i * step)] for i in range(MAX_REFERENCE_FRAMES)]


def thumbnail_model_label() -> str:
    """The model that renders thumbnails on this configuration, for run
    metadata."""
    azure = _azure_image_config()
    return azure["deployment"] if azure is not None else DEFAULT_THUMBNAIL_MODEL


def thumbnails_configured() -> bool:
    """Whether any image model is reachable on this configuration."""
    return _azure_image_config() is not None or bool(os.environ.get("OPENROUTER_API_KEY"))


def _generate_image(*, prompt: str, reference_images: list[str]) -> tuple[bytes, str]:
    """Render one thumbnail image. Returns (image bytes, file extension).

    Model resolution, kept here in one place: the Azure-hosted image
    deployment (gpt-image-2) takes the call whenever one is configured —
    AZURE_IMAGE_DEPLOYMENT or AZURE_OPENAI_IMAGE_DEPLOYMENT, with
    AZURE_IMAGE_ENDPOINT/AZURE_IMAGE_KEY overriding the shared Azure OpenAI
    endpoint and key — using images/edits when reference frames exist and
    images/generations when none survived extraction. When no deployment is
    configured, or the Azure call fails, the render falls through to Nano
    Banana 2 (google/gemini-3.1-flash-image) pinned to Google Vertex through
    OpenRouter's chat API, which returns images as data URIs. Callers only
    ever see this one function."""
    azure = _azure_image_config()
    if azure is not None:
        try:
            return _azure_image(prompt=prompt, reference_images=reference_images, **azure)
        except Exception as exc:
            print(
                f"Azure OpenAI ({azure['deployment']}) image call failed; "
                f"falling back to {DEFAULT_THUMBNAIL_MODEL}: {exc}"
            )
    return _nano_banana_image(prompt=prompt, reference_images=reference_images)


def _azure_image_config() -> dict | None:
    endpoint = os.environ.get("AZURE_IMAGE_ENDPOINT") or os.environ.get("AZURE_OPENAI_ENDPOINT")
    key = (
        os.environ.get("AZURE_IMAGE_KEY")
        or os.environ.get("AZURE_IMAGE_API_KEY")
        or os.environ.get("AZURE_OPENAI_API_KEY")
    )
    deployment = os.environ.get("AZURE_IMAGE_DEPLOYMENT") or os.environ.get(
        "AZURE_OPENAI_IMAGE_DEPLOYMENT"
    )
    if not endpoint or not key or not deployment:
        return None
    return {"endpoint": endpoint.rstrip("/"), "key": key, "deployment": deployment}


def _azure_image(
    *, prompt: str, reference_images: list[str], endpoint: str, key: str, deployment: str
) -> tuple[bytes, str]:
    if reference_images:
        fields = [("model", deployment), ("prompt", prompt), ("size", "1536x1024")]
        files = [
            ("image[]", f"frame_{index}.jpg", base64.b64decode(image.partition(",")[2]))
            for index, image in enumerate(reference_images)
        ]
        body, content_type = _multipart_body(fields, files=files)
        request = Request(
            f"{endpoint}/openai/v1/images/edits",
            data=body,
            headers={"api-key": key, "Content-Type": content_type},
            method="POST",
        )
    else:
        request = Request(
            f"{endpoint}/openai/v1/images/generations",
            data=json.dumps(
                {"model": deployment, "prompt": prompt, "size": "1536x1024"}
            ).encode("utf-8"),
            headers={"api-key": key, "Content-Type": "application/json"},
            method="POST",
        )
    try:
        with urlopen(request, timeout=300) as response:
            payload = json.loads(response.read().decode("utf-8"))
    except HTTPError as exc:
        message = exc.read().decode("utf-8", errors="replace")
        raise RuntimeError(f"Image request failed: {message}") from exc
    except URLError as exc:
        raise RuntimeError(f"Image request failed: {exc.reason}") from exc

    data = payload.get("data") or []
    encoded = data[0].get("b64_json") if data else None
    if not encoded:
        raise RuntimeError("image response contained no image data")
    return base64.b64decode(encoded), ".png"


def _multipart_body(
    fields: list[tuple[str, str]], *, files: list[tuple[str, str, bytes]]
) -> tuple[bytes, str]:
    boundary = uuid.uuid4().hex
    parts = [
        (
            f"--{boundary}\r\n"
            f'Content-Disposition: form-data; name="{name}"\r\n\r\n'
            f"{value}\r\n"
        ).encode("utf-8")
        for name, value in fields
    ]
    for name, filename, content in files:
        parts.append(
            (
                f"--{boundary}\r\n"
                f'Content-Disposition: form-data; name="{name}"; filename="{filename}"\r\n'
                "Content-Type: image/jpeg\r\n\r\n"
            ).encode("utf-8")
            + content
            + b"\r\n"
        )
    return (
        b"".join(parts) + f"--{boundary}--\r\n".encode("utf-8"),
        f"multipart/form-data; boundary={boundary}",
    )


def _nano_banana_image(*, prompt: str, reference_images: list[str]) -> tuple[bytes, str]:
    content: list[dict[str, Any]] = [{"type": "text", "text": prompt}]
    content.extend(
        {"type": "image_url", "image_url": {"url": image}} for image in reference_images
    )
    body: dict[str, Any] = {
        "model": DEFAULT_THUMBNAIL_MODEL,
        "messages": [{"role": "user", "content": content}],
        "modalities": ["image", "text"],
        "provider": {"order": [OPENROUTER_VERTEX_PROVIDER], "allow_fallbacks": False},
    }
    request = Request(
        f"{OPENROUTER_BASE_URL}/chat/completions",
        data=json.dumps(body).encode("utf-8"),
        headers={
            "Authorization": f"Bearer {required_env('OPENROUTER_API_KEY')}",
            "Content-Type": "application/json",
        },
        method="POST",
    )
    try:
        with urlopen(request, timeout=300) as response:
            payload = json.loads(response.read().decode("utf-8"))
    except HTTPError as exc:
        message = exc.read().decode("utf-8", errors="replace")
        raise RuntimeError(f"Image request failed: {message}") from exc
    except URLError as exc:
        raise RuntimeError(f"Image request failed: {exc.reason}") from exc

    choices = payload.get("choices") or []
    images = (choices[0].get("message") or {}).get("images") if choices else None
    data_uri = (images or [{}])[0].get("image_url", {}).get("url") or ""
    prefix, _, encoded = data_uri.partition(",")
    if not encoded:
        raise RuntimeError("image response contained no image data")
    extension = ".png" if "image/png" in prefix else ".jpg"
    return base64.b64decode(encoded), extension


def main() -> None:
    """Regenerate long-form thumbnails for a finished project: fresh concepts
    (optionally steered by a human prompt) render as new variants appended to
    the newest version's set."""
    import argparse

    from .config import load_env
    from .defaults import DEFAULT_OUTPUT_ROOT, DEFAULT_SUPABASE_CLIPS_BUCKET
    from .records import ProjectRecords
    from .supabase_client import SupabaseClient

    load_env()
    parser = argparse.ArgumentParser(description=main.__doc__)
    parser.add_argument("project_id")
    parser.add_argument("--prompt", default=None, help="Human guidance for the concepts.")
    parser.add_argument("--version", type=int, default=None, help="Long-form version (default: newest).")
    parser.add_argument("--select", type=int, default=None, help="Just select variant N as the thumbnail.")
    parser.add_argument("--output-root", default=None)
    args = parser.parse_args()

    output_root = Path(args.output_root or os.environ.get("OUTPUT_ROOT", DEFAULT_OUTPUT_ROOT))
    project_dir = output_root / "projects" / args.project_id
    if not (project_dir / "project.json").exists():
        raise RuntimeError(f"No local project record at {project_dir}")
    db = None if args.project_id.startswith("local-") else SupabaseClient()
    records = ProjectRecords(project_dir)

    rows = [
        json.loads(line)
        for line in (project_dir / "longform_edits.jsonl").read_text().splitlines()
        if line.strip()
    ]
    if not rows:
        raise RuntimeError("No long-form edit on record for this project")
    if args.version is not None:
        matching = [row for row in rows if (row.get("version") or 1) == args.version]
        if not matching:
            raise RuntimeError(f"No long-form version {args.version} on record")
        row = matching[-1]
    else:
        row = max(rows, key=lambda r: r.get("version") or 1)
    version = row.get("version") or 1
    render = (row.get("metadata") or {}).get("render") or {}
    thumbnails = render.get("thumbnails") or {"variants": [], "selected_index": 0}
    variants = list(thumbnails.get("variants") or [])

    if args.select is not None:
        chosen = next((v for v in variants if v.get("index") == args.select), None)
        if chosen is None or not chosen.get("url"):
            raise RuntimeError(f"No hosted thumbnail #{args.select} on record")
        thumbnails["selected_index"] = variants.index(chosen)
        render["thumbnails"] = thumbnails
        _persist_render(db, records, row, version, render, thumbnail_url=chosen["url"])
        print(f"Selected thumbnail #{args.select} for version {version}")
        return

    longform_path = project_dir / "longform" / str(render.get("filename") or "longform.mp4")
    if not longform_path.exists():
        raise RuntimeError(f"Long-form video not found at {longform_path}")

    research_file = project_dir / "research.json"
    research = json.loads(research_file.read_text()) if research_file.exists() else None
    entries = render.get("segment_windows") or []
    start_index = max((v.get("index") or 0 for v in variants), default=0) + 1

    result = generate_thumbnails(
        longform_path=longform_path,
        entries=entries,
        title=render.get("title"),
        research_context=research,
        output_dir=project_dir / "thumbnails",
        guidance=args.prompt,
        start_index=start_index,
    )
    if result is None:
        raise RuntimeError("No thumbnails were generated")

    bucket = os.environ.get("SUPABASE_CLIPS_BUCKET", DEFAULT_SUPABASE_CLIPS_BUCKET)
    for variant in result["variants"]:
        if db is None:
            continue
        local_path = Path(variant["local_path"])
        key = f"projects/{args.project_id}/thumbnails/{local_path.name}"
        try:
            variant["url"] = db.upload_storage_object(bucket=bucket, key=key, path=local_path)
            variant["storage_path"] = key
        except Exception as exc:
            print(f"Thumbnail upload failed (kept locally at {local_path}): {exc}")

    thumbnails["variants"] = variants + result["variants"]
    render["thumbnails"] = thumbnails
    _persist_render(db, records, row, version, render, thumbnail_url=None)
    print(
        f"Added {len(result['variants'])} thumbnail variant(s) to version {version}; "
        f"the set now has {len(thumbnails['variants'])}."
    )


def _persist_render(db, records, row, version, render, *, thumbnail_url):
    """Write an updated render dict back to the version row, locally and in
    the database."""
    metadata = dict(row.get("metadata") or {})
    metadata["render"] = render
    row["metadata"] = metadata
    if thumbnail_url:
        row["thumbnail_url"] = thumbnail_url
    records.update_longform_edit(version, row)
    if db is not None:
        db.update_longform_edit(
            project_id=row.get("project_id") or "",
            version=version,
            thumbnail_url=thumbnail_url,
            metadata=metadata,
        )
