"""Stitch rendered clip MP4s into one long-form video.

Clips come from render.py with identical encode settings (h264/aac, same scale
filter), so a codec-copy concat normally works; when it doesn't (e.g. the
source changed resolution mid-VOD), fall back to a re-encode."""

import subprocess
import tempfile
from pathlib import Path


def stitch_clips(*, clip_paths: list[Path], output_path: Path) -> None:
    if not clip_paths:
        raise RuntimeError("No clips to stitch")
    missing = [str(path) for path in clip_paths if not path.exists()]
    if missing:
        raise RuntimeError(f"Missing clips for stitching: {', '.join(missing)}")

    output_path.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(dir=output_path.parent) as tmpdir:
        concat_list = Path(tmpdir) / "clips.txt"
        concat_list.write_text(
            "".join(f"file '{_concat_path(path)}'\n" for path in clip_paths)
        )

        copy_result = _run_ffmpeg(concat_list, output_path, reencode=False)
        if copy_result.returncode != 0:
            print("Codec-copy stitch failed; re-encoding the long-form video")
            reencode_result = _run_ffmpeg(concat_list, output_path, reencode=True)
            if reencode_result.returncode != 0:
                details = (reencode_result.stderr or "").strip()
                raise RuntimeError(details or "ffmpeg failed while stitching clips")


def _run_ffmpeg(concat_list: Path, output_path: Path, *, reencode: bool) -> subprocess.CompletedProcess:
    codec_args = (
        ["-c:v", "libx264", "-preset", "veryfast", "-crf", "30", "-c:a", "aac", "-b:a", "128k"]
        if reencode
        else ["-c", "copy"]
    )
    return subprocess.run(
        [
            "ffmpeg",
            "-hide_banner",
            "-loglevel",
            "error",
            "-y",
            "-f",
            "concat",
            "-safe",
            "0",
            "-i",
            str(concat_list),
            *codec_args,
            "-movflags",
            "+faststart",
            str(output_path),
        ],
        capture_output=True,
        text=True,
    )


def _concat_path(path: Path) -> str:
    return str(path.resolve()).replace("'", "'\\''")
