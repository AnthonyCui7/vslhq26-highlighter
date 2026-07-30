"""Re-render a finished clip in another delivery format.

The human editor picks the format: the auto-reframed vertical and the original
16:9 already exist, and this verb adds the plain center-crop square (1:1),
optionally with the same burned captions the vertical gets. Rendering is pure
ffmpeg — no model calls — and the new files ride the same storage and record
paths as the clip's other media.
"""

import argparse
import json
import os
from pathlib import Path

from .captions import caption_clip, captions_available
from .config import load_env
from .defaults import DEFAULT_OUTPUT_ROOT, DEFAULT_SUPABASE_CLIPS_BUCKET
from .records import ProjectRecords
from .render import _run
from .supabase_client import SupabaseClient

SQUARE_SIZE = 720


def render_center_crop_square(*, clip_path: Path, output_path: Path) -> None:
    """A centered full-height square crop scaled to SQUARE_SIZE."""
    output_path.parent.mkdir(parents=True, exist_ok=True)
    _run(
        [
            "ffmpeg",
            "-hide_banner",
            "-loglevel",
            "error",
            "-y",
            "-i",
            str(clip_path),
            "-vf",
            f"crop=ih:ih,scale={SQUARE_SIZE}:{SQUARE_SIZE}",
            "-c:v",
            "libx264",
            "-preset",
            "veryfast",
            "-crf",
            "23",
            "-c:a",
            "aac",
            "-b:a",
            "128k",
            "-movflags",
            "+faststart",
            str(output_path),
        ]
    )


def main() -> None:
    """Render a square center-crop copy of a finished clip (optionally with
    burned captions) and record it beside the clip's other media."""
    load_env()
    parser = argparse.ArgumentParser(description=main.__doc__)
    parser.add_argument("project_id")
    parser.add_argument("clip_filename")
    parser.add_argument("--format", choices=["square"], default="square")
    parser.add_argument("--captions", action="store_true")
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
        for line in (project_dir / "clips.jsonl").read_text().splitlines()
        if line.strip()
    ]
    row = next(
        (
            r
            for r in rows
            if ((r.get("metadata") or {}).get("render") or {}).get("filename")
            == args.clip_filename
        ),
        None,
    )
    if row is None:
        raise RuntimeError(f"No clip named {args.clip_filename} on record")
    render = (row.get("metadata") or {}).get("render") or {}

    clip_path = project_dir / "clips" / args.clip_filename
    if not clip_path.exists():
        raise RuntimeError(f"Clip file not found at {clip_path}")

    square_path = clip_path.with_name(f"{clip_path.stem}_square.mp4")
    render_center_crop_square(clip_path=clip_path, output_path=square_path)
    render["square_path"] = os.path.relpath(square_path)
    print(f"Rendered square crop to {square_path}")

    captioned_path = None
    if args.captions:
        if not captions_available():
            print("Captions unavailable (pycaps not on PATH); shipping the clean square only.")
        else:
            words = []
            for line in (project_dir / "transcript_chunks.jsonl").read_text().splitlines():
                if line.strip():
                    words.extend(json.loads(line).get("words") or [])
            captioned_path = caption_clip(
                vertical_path=square_path,
                words=words,
                clip_start_seconds=float(row["start_seconds"]),
                clip_end_seconds=float(row["end_seconds"]),
                output_path=square_path.with_name(f"{square_path.stem}_captions.mp4"),
            )
            if captioned_path is not None:
                render["square_captioned_path"] = os.path.relpath(captioned_path)
                print(f"Captioned square to {captioned_path}")

    if db is not None:
        bucket = os.environ.get("SUPABASE_CLIPS_BUCKET", DEFAULT_SUPABASE_CLIPS_BUCKET)
        fields: dict = {}
        try:
            key = f"projects/{args.project_id}/clips/{square_path.name}"
            render["square_url"] = db.upload_storage_object(
                bucket=bucket, key=key, path=square_path
            )
            render["square_storage_path"] = key
            fields["metadata"] = {**(row.get("metadata") or {}), "render": render}
            if captioned_path is not None:
                captioned_key = f"projects/{args.project_id}/clips/{captioned_path.name}"
                render["square_captioned_url"] = db.upload_storage_object(
                    bucket=bucket, key=captioned_key, path=captioned_path
                )
                render["square_captioned_storage_path"] = captioned_key
        except Exception as exc:
            print(f"Square upload failed (kept locally): {exc}")
        if fields:
            db.update_clip_media(
                project_id=args.project_id, filename=args.clip_filename, fields=fields
            )

    metadata = dict(row.get("metadata") or {})
    metadata["render"] = render
    row["metadata"] = metadata
    records.update_clip(args.clip_filename, row)
    print(f"Recorded square media for {args.clip_filename}")
