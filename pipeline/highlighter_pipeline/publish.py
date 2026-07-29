"""Publish finished clips and long-form edits to social platforms.

Posting goes through upload-post.com (one API, per-platform OAuth handled on
their side): create a profile there, link the social accounts, and set
UPLOAD_POST_API_KEY + UPLOAD_POST_USER in the environment.

    highlighter-publish <project-id> longform --platforms youtube,x --thumbnail 2
    highlighter-publish <project-id> clip_00003_10000_20500_short.mp4 \\
        --platforms tiktok,instagram

Targets: "longform" (latest version, or --version N) or a clip mp4 filename
from the project's clips directory. Short clips post their captioned vertical
when one exists (--plain for the clean vertical); YouTube decides Short vs
regular video from the file itself (a vertical under 3 minutes becomes a
Short). X is a promo layer, not a video upload: it requires another platform
in --platforms, and once that post is live a short model-written X post goes
out with the link. --dry-run resolves everything and prints what would be
posted without calling the API.
"""

import argparse
import json
import os
import sys
import uuid
from pathlib import Path
from typing import Any
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen

from .config import load_env, required_env
from .defaults import DEFAULT_LLM_REASONING_EFFORT, DEFAULT_OUTPUT_ROOT
from .llm import _json_from_text
from .providers import Provider, editor_providers, run_with_fallback
from .records import ProjectRecords
from .supabase_client import SupabaseClient

UPLOAD_POST_BASE_URL = "https://api.upload-post.com/api"
VIDEO_PLATFORMS = ("tiktok", "instagram", "youtube")
KNOWN_PLATFORMS = VIDEO_PLATFORMS + ("x",)

X_POST_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {
        "post": {
            "type": "string",
            "description": "The complete X post text, link included.",
        },
    },
    "required": ["post"],
    "additionalProperties": False,
}

X_POST_SYSTEM_PROMPT = """\
You write the X (Twitter) post announcing a creator's new upload. You get the
video's title, where it was published, the link, and research about the
creator's voice and audience. Write ONE short post — a hook in the creator's
own register, no hashtag spam, at most a couple of sentences — and include the
link verbatim. Return ONLY one JSON object matching the schema."""


def main() -> None:
    load_env()
    args = _parse_args()

    output_root = Path(args.output_root or os.environ.get("OUTPUT_ROOT", DEFAULT_OUTPUT_ROOT))
    project_dir = output_root / "projects" / args.project_id
    if not (project_dir / "project.json").exists():
        raise RuntimeError(f"No local project record at {project_dir}")
    db = None if args.project_id.startswith("local-") else SupabaseClient()
    records = ProjectRecords(project_dir)

    platforms = _parse_platforms(args.platforms)
    video_platforms = [platform for platform in platforms if platform != "x"]

    if args.target == "longform":
        target_kind = "longform"
        row = _pick_longform_row(project_dir, version=args.version)
        render = (row.get("metadata") or {}).get("render") or {}
        media_path = _resolve_media_path(render.get("local_path"), project_dir, "longform")
        media_label = "long-form edit"
        title = args.title or _longform_title(project_dir)
        description = None
        target_meta: dict[str, Any] = {"longform_version": row.get("version")}
    else:
        target_kind = "clip"
        row = _pick_clip_row(project_dir, filename=args.target)
        render = (row.get("metadata") or {}).get("render") or {}
        raw_media, media_label = _pick_clip_media(render, plain=args.plain)
        media_path = _resolve_media_path(raw_media, project_dir, "clips")
        title = args.title or row.get("title")
        description = row.get("description")
        target_meta = {"filename": args.target}
    if not title:
        raise RuntimeError("No title on record for this target; pass --title")
    if media_path is None:
        raise RuntimeError(f"Could not locate the media file for {args.target}")

    thumbnail_url = None
    thumbnail_upload: dict[str, str] | None = None
    if args.thumbnail:
        if target_kind != "longform" or "youtube" not in platforms:
            raise RuntimeError("--thumbnail applies to longform targets published to youtube")
        thumbnail_url, thumbnail_upload = _resolve_thumbnail(
            args.thumbnail, project_dir=project_dir, project_id=args.project_id, db=db,
            dry_run=args.dry_run,
        )

    if args.dry_run:
        _print_dry_run(
            media_path=media_path,
            media_label=media_label,
            title=title,
            description=description,
            platforms=platforms,
            video_platforms=video_platforms,
            thumbnail_url=thumbnail_url,
        )
        return

    api_key = required_env("UPLOAD_POST_API_KEY")
    user = required_env("UPLOAD_POST_USER")

    fields: list[tuple[str, str]] = [("title", title), ("user", user)]
    fields.extend(("platform[]", platform) for platform in video_platforms)
    if description and "youtube" in video_platforms:
        fields.append(("description", description))
    if thumbnail_url:
        fields.append(("thumbnail_url", thumbnail_url))

    print(f"Publishing {media_label} ({Path(media_path).name}) to {', '.join(video_platforms)}")
    response = _upload_post_request(
        "upload", api_key=api_key, fields=fields, file_path=Path(media_path)
    )
    results = response.get("results") or {}

    published: dict[str, str | None] = {}
    for platform in video_platforms:
        entry = results.get(platform) or {}
        if entry.get("success"):
            url = _result_url(entry)
            published[platform] = url
            print(f"{platform}: published{f' — {url}' if url else ''}")
            metadata = {**target_meta, **(thumbnail_upload or {})}
            records.append_publication(
                {
                    "project_id": args.project_id,
                    "target": target_kind,
                    "platform": platform,
                    "url": url,
                    "title": title,
                    "metadata": metadata,
                }
            )
            if db is not None:
                db.insert_publication(
                    project_id=args.project_id,
                    target=target_kind,
                    platform=platform,
                    url=url,
                    title=title,
                    metadata=metadata,
                )
        else:
            print(f"{platform}: failed — {entry.get('error') or entry or 'no result returned'}")

    if "x" in platforms:
        link = next((url for url in published.values() if url), None)
        if link is None:
            print("x: skipped — no published post to link to")
        else:
            _publish_x_post(
                api_key=api_key,
                user=user,
                title=title,
                link=link,
                project_dir=project_dir,
                project_id=args.project_id,
                target_kind=target_kind,
                target_meta=target_meta,
                records=records,
                db=db,
                published=published,
            )

    if not published and video_platforms:
        sys.exit(1)


def _publish_x_post(
    *,
    api_key: str,
    user: str,
    title: str,
    link: str,
    project_dir: Path,
    project_id: str,
    target_kind: str,
    target_meta: dict,
    records: ProjectRecords,
    db: SupabaseClient | None,
    published: dict[str, str | None],
) -> None:
    post_text = _compose_x_post(
        title=title,
        link=link,
        research_context=_read_research(project_dir),
        platforms=[platform for platform, url in published.items() if url],
    )
    response = _upload_post_request(
        "upload_text",
        api_key=api_key,
        fields=[("title", post_text), ("user", user), ("platform[]", "x")],
    )
    entry = (response.get("results") or {}).get("x") or {}
    if not entry.get("success"):
        print(f"x: failed — {entry.get('error') or entry or 'no result returned'}")
        return
    url = _result_url(entry)
    print(f"x: published{f' — {url}' if url else ''}")
    metadata = {**target_meta, "post_text": post_text}
    records.append_publication(
        {
            "project_id": project_id,
            "target": target_kind,
            "platform": "x",
            "url": url,
            "title": title,
            "metadata": metadata,
        }
    )
    if db is not None:
        db.insert_publication(
            project_id=project_id,
            target=target_kind,
            platform="x",
            url=url,
            title=title,
            metadata=metadata,
        )


def _parse_platforms(raw: str) -> list[str]:
    platforms: list[str] = []
    for token in raw.split(","):
        platform = token.strip().lower()
        if not platform:
            continue
        if platform not in KNOWN_PLATFORMS:
            raise RuntimeError(
                f"Unknown platform '{platform}'; choose from {', '.join(KNOWN_PLATFORMS)}"
            )
        if platform not in platforms:
            platforms.append(platform)
    if not platforms:
        raise RuntimeError("No platforms given")
    if "x" in platforms and len(platforms) == 1:
        raise RuntimeError(
            "x is a promo post linking to a published video; pick at least one of "
            f"{', '.join(VIDEO_PLATFORMS)} alongside it"
        )
    return platforms


def _pick_clip_media(render: dict, *, plain: bool) -> tuple[str | None, str]:
    """The file a short clip publishes: the captioned vertical when one
    exists, unless --plain asked for the clean vertical."""
    if not plain and render.get("captioned_path"):
        return render["captioned_path"], "captioned vertical"
    if render.get("vertical_path"):
        return render["vertical_path"], "vertical"
    return render.get("local_path"), "16:9 clip"


def _pick_longform_row(project_dir: Path, *, version: int | None) -> dict:
    rows = _read_jsonl(project_dir / "longform_edits.jsonl")
    if not rows:
        raise RuntimeError("No long-form edits recorded for this project")
    if version is not None:
        for row in rows:
            if row.get("version") == version:
                return row
        raise RuntimeError(f"No long-form version {version} on record")
    return max(rows, key=lambda row: row.get("version") or 0)


def _pick_clip_row(project_dir: Path, *, filename: str) -> dict:
    for row in _read_jsonl(project_dir / "clips.jsonl"):
        render = (row.get("metadata") or {}).get("render") or {}
        if render.get("filename") == filename:
            return row
    raise RuntimeError(f"No clip named {filename} on record; pass the mp4 filename from clips/")


def _longform_title(project_dir: Path) -> str | None:
    rows = _read_jsonl(project_dir / "longform_edits.jsonl")
    for row in sorted(rows, key=lambda row: row.get("version") or 0, reverse=True):
        title = ((row.get("metadata") or {}).get("render") or {}).get("title")
        if title:
            return title
    return None


def _resolve_thumbnail(
    raw: str,
    *,
    project_dir: Path,
    project_id: str,
    db: SupabaseClient | None,
    dry_run: bool,
) -> tuple[str, dict[str, str] | None]:
    """A generated variant number (1-3) or a path to the user's own image.
    Returns (public URL, cleanup metadata for an uploaded custom file)."""
    if raw.isdigit():
        wanted = int(raw)
        for row in _read_jsonl(project_dir / "longform_edits.jsonl"):
            thumbnails = ((row.get("metadata") or {}).get("render") or {}).get("thumbnails")
            for variant in (thumbnails or {}).get("variants", []):
                if variant.get("index") == wanted and variant.get("url"):
                    return variant["url"], None
        raise RuntimeError(f"No generated thumbnail #{wanted} with a hosted URL on record")

    path = Path(raw)
    if not path.exists():
        raise RuntimeError(f"Thumbnail file not found: {raw}")
    if db is None:
        raise RuntimeError("Importing a thumbnail file needs a Supabase-backed project")
    bucket = os.environ.get("SUPABASE_CLIPS_BUCKET", "clips")
    key = f"projects/{project_id}/thumbnails/custom{path.suffix.lower() or '.jpg'}"
    if dry_run:
        return f"(would upload {path} to {bucket}/{key})", None
    url = db.upload_storage_object(bucket=bucket, key=key, path=path)
    return url, {"thumbnail_bucket": bucket, "thumbnail_storage_path": key}


def _compose_x_post(
    *,
    title: str,
    link: str,
    research_context: dict | None,
    platforms: list[str],
) -> str:
    prompt_lines = [
        f"Video title: {title}",
        f"Published on: {', '.join(platforms)}",
        f"Link to include verbatim: {link}",
    ]
    if research_context:
        prompt_lines.append("")
        prompt_lines.append("Research context:")
        prompt_lines.append(json.dumps(research_context, indent=2))
    try:
        providers = editor_providers(
            title="highlighter publish",
            openrouter_reasoning_effort=DEFAULT_LLM_REASONING_EFFORT,
        )
        decision, _provider = run_with_fallback(
            providers,
            lambda provider: _request_x_post(provider, "\n".join(prompt_lines)),
        )
        post = str(decision.get("post") or "").strip()
        if post:
            return post if link in post else f"{post}\n{link}"
    except Exception as exc:
        print(f"X post drafting failed; using the title and link: {exc}")
    return f"{title} — {link}"


def _request_x_post(provider: Provider, prompt: str) -> dict[str, Any]:
    with provider.client(timeout=120.0) as client:
        response = client.chat.completions.create(
            messages=[
                {"role": "system", "content": X_POST_SYSTEM_PROMPT},
                {"role": "user", "content": prompt},
            ],
            response_format={
                "type": "json_schema",
                "json_schema": {"name": "x_post", "schema": X_POST_SCHEMA},
            },
            **provider.request_kwargs(),
        )
    if not response.choices or not response.choices[0].message.content:
        raise RuntimeError(f"{provider.label} X post response was empty")
    return _json_from_text(response.choices[0].message.content)


def _read_research(project_dir: Path) -> dict | None:
    path = project_dir / "research.json"
    if not path.exists():
        path = project_dir / "research_short.json"
    try:
        return json.loads(path.read_text()) if path.exists() else None
    except Exception:
        return None


def _upload_post_request(
    endpoint: str,
    *,
    api_key: str,
    fields: list[tuple[str, str]],
    file_path: Path | None = None,
) -> dict[str, Any]:
    body, content_type = _multipart_body(fields, file_path=file_path)
    request = Request(
        f"{UPLOAD_POST_BASE_URL}/{endpoint}",
        data=body,
        headers={"Authorization": f"Apikey {api_key}", "Content-Type": content_type},
        method="POST",
    )
    try:
        with urlopen(request, timeout=600) as response:
            return json.loads(response.read().decode("utf-8"))
    except HTTPError as exc:
        message = exc.read().decode("utf-8", errors="replace")
        raise RuntimeError(f"upload-post request failed: {message}") from exc
    except URLError as exc:
        raise RuntimeError(f"upload-post request failed: {exc.reason}") from exc


def _multipart_body(
    fields: list[tuple[str, str]], *, file_path: Path | None = None
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
    if file_path is not None:
        parts.append(
            (
                f"--{boundary}\r\n"
                f'Content-Disposition: form-data; name="video"; filename="{file_path.name}"\r\n'
                "Content-Type: video/mp4\r\n\r\n"
            ).encode("utf-8")
            + file_path.read_bytes()
            + b"\r\n"
        )
    body = b"".join(parts) + f"--{boundary}--\r\n".encode("utf-8")
    return body, f"multipart/form-data; boundary={boundary}"


def _result_url(entry: dict) -> str | None:
    url = entry.get("url") or entry.get("post_url") or entry.get("video_url")
    return str(url) if url else None


def _read_jsonl(path: Path) -> list[dict]:
    if not path.exists():
        return []
    return [json.loads(line) for line in path.read_text().splitlines() if line.strip()]


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


def _print_dry_run(
    *,
    media_path: str,
    media_label: str,
    title: str,
    description: str | None,
    platforms: list[str],
    video_platforms: list[str],
    thumbnail_url: str | None,
) -> None:
    size_mb = Path(media_path).stat().st_size / (1024 * 1024)
    print("Dry run — nothing was posted.")
    print(f"  file: {media_path} ({media_label}, {size_mb:.1f} MB)")
    print(f"  title: {title}")
    if description:
        print(f"  description: {description}")
    print(f"  video platforms: {', '.join(video_platforms)}")
    if thumbnail_url:
        print(f"  youtube thumbnail: {thumbnail_url}")
    if "x" in platforms:
        print("  x: promo post with the first published link, drafted by the editor model")
    user = os.environ.get("UPLOAD_POST_USER")
    print(f"  upload-post profile: {user or 'UPLOAD_POST_USER not set'}")


def _parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Publish a finished clip or long-form edit to social platforms "
        "through upload-post.com.",
    )
    parser.add_argument("project_id", help="Project id under <output-root>/projects/.")
    parser.add_argument(
        "target",
        help='"longform" or a clip mp4 filename from the project\'s clips directory.',
    )
    parser.add_argument(
        "--platforms",
        required=True,
        help="Comma-separated: tiktok, instagram, youtube, x. "
        "x rides along as a promo post and needs another platform selected.",
    )
    parser.add_argument("--title", default=None, help="Override the recorded title.")
    parser.add_argument(
        "--version",
        type=int,
        default=None,
        help="Long-form version to publish (default: latest).",
    )
    parser.add_argument(
        "--thumbnail",
        default=None,
        help="YouTube thumbnail for a longform target: a generated variant number "
        "(1-3) or a path to your own image.",
    )
    parser.add_argument(
        "--plain",
        action="store_true",
        default=False,
        help="Short clips: post the clean vertical instead of the captioned one.",
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        default=False,
        help="Resolve and print what would be posted without calling the API.",
    )
    parser.add_argument(
        "--output-root",
        default=None,
        help="Directory holding project outputs (default: 'outputs', also via OUTPUT_ROOT).",
    )
    return parser.parse_args()


if __name__ == "__main__":
    main()
