"""Cut a new clip from a project's archived source video in S3.

Looks up the project's source_archive pointer, downloads the segments covering
the requested window, renders an MP4, uploads it to the clips bucket, and
inserts a clips row — the full S3 -> Supabase round trip, long after the
original stream ended."""

import argparse
import re
import tempfile
from pathlib import Path

from .config import load_env
from .defaults import DEFAULT_SUPABASE_CLIPS_BUCKET
from .render import clip_filename, render_clip_from_segments
from .storage import S3Storage
from .supabase_client import SupabaseClient


def main() -> None:
    load_env()
    args = _parse_args()

    if args.end_seconds <= args.start_seconds:
        raise RuntimeError("end must be greater than start")

    db = SupabaseClient()
    project = db.get_project(args.project_id)
    archive = (project.get("metadata") or {}).get("source_archive")
    if not archive:
        raise RuntimeError(
            f"Project {args.project_id} has no source_archive; only archived livestreams can be re-clipped."
        )

    segment_seconds = int(archive["segment_seconds"])
    first_index = int(args.start_seconds // segment_seconds)
    last_index = int(max(args.start_seconds, args.end_seconds - 0.001) // segment_seconds)
    keys = _segment_keys(archive, first_index=first_index, last_index=last_index)

    storage = S3Storage(archive["bucket"], archive.get("region") or _default_region())
    title = args.title or f"Reclip {args.start_seconds:g}s-{args.end_seconds:g}s"
    filename = clip_filename(
        chunk_index=first_index,
        start_seconds=args.start_seconds,
        end_seconds=args.end_seconds,
    )

    import os

    from .defaults import DEFAULT_OUTPUT_ROOT

    output_root = Path(os.environ.get("OUTPUT_ROOT", DEFAULT_OUTPUT_ROOT))
    project_dir = output_root / "projects" / args.project_id
    output_path = project_dir / "clips" / filename
    output_path.parent.mkdir(parents=True, exist_ok=True)

    with tempfile.TemporaryDirectory(dir=output_path.parent) as tmpdir:
        segment_paths = []
        for key in keys:
            local = Path(tmpdir) / Path(key).name
            print(f"Downloading s3://{storage.bucket}/{key}")
            storage.download_file(key, local)
            segment_paths.append(local)

        print(f"Rendering {filename} from {len(segment_paths)} segment(s)")
        render_clip_from_segments(
            segment_paths=segment_paths,
            output_path=output_path,
            first_segment_start_seconds=first_index * segment_seconds,
            start_seconds=args.start_seconds,
            end_seconds=args.end_seconds,
        )

    storage_key = f"projects/{args.project_id}/clips/{filename}"
    video_url = db.upload_storage_object(
        bucket=args.clips_bucket,
        key=storage_key,
        path=output_path,
    )
    db.insert_clip(
        project_id=args.project_id,
        title=title,
        description=args.description,
        start_seconds=args.start_seconds,
        end_seconds=args.end_seconds,
        video_url=video_url,
        status="rendered",
        metadata={
            "source": "reclip",
            "render": {
                "status": "rendered",
                "bucket": args.clips_bucket,
                "storage_path": storage_key,
                "video_url": video_url,
                "filename": filename,
                "segment_keys": keys,
            },
        },
    )
    print(f"Clip stored: {video_url}")
    print(f"Local copy: {output_path}")


def _segment_keys(archive: dict, *, first_index: int, last_index: int) -> list[str]:
    known_keys = archive.get("segment_keys") or []
    by_index: dict[int, str] = {}
    for key in known_keys:
        match = re.search(r"video_(\d+)\.ts$", key)
        if match:
            by_index[int(match.group(1))] = key

    keys = []
    for index in range(first_index, last_index + 1):
        if index in by_index:
            keys.append(by_index[index])
        elif not known_keys and archive.get("prefix"):
            # Older projects recorded only a prefix; segment names are deterministic.
            keys.append(f"{archive['prefix'].rstrip('/')}/video_{index:05d}.ts")
        else:
            raise RuntimeError(
                f"Requested window needs archive segment {index}, which is not in this project's archive."
            )
    return keys


def _default_region() -> str:
    import os

    from .defaults import DEFAULT_AWS_REGION

    return os.environ.get("AWS_REGION", DEFAULT_AWS_REGION)


def _parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Render a clip from a project's archived source video in S3.",
    )
    parser.add_argument("project_id", help="Project whose archive to cut from.")
    parser.add_argument("start_seconds", type=float, help="Clip start (absolute stream seconds).")
    parser.add_argument("end_seconds", type=float, help="Clip end (absolute stream seconds).")
    parser.add_argument("--title", default=None, help="Clip title (defaults to the window).")
    parser.add_argument("--description", default=None, help="Optional clip description.")
    parser.add_argument(
        "--clips-bucket",
        default=DEFAULT_SUPABASE_CLIPS_BUCKET,
        help="Supabase Storage bucket for the rendered clip.",
    )
    return parser.parse_args()


if __name__ == "__main__":
    main()
