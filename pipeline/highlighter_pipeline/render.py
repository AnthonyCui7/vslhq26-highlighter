import subprocess
import tempfile
from pathlib import Path

from .cookies import ytdlp_cookie_args


def clip_filename(*, chunk_index: int, start_seconds: float, end_seconds: float) -> str:
    start_ms = int(round(start_seconds * 1000))
    end_ms = int(round(end_seconds * 1000))
    return f"clip_{chunk_index:05d}_{start_ms}_{end_ms}.mp4"


def render_clip_from_video_url(
    *,
    source_url: str,
    output_path: Path,
    start_seconds: float,
    end_seconds: float,
) -> None:
    _validate_window(start_seconds=start_seconds, end_seconds=end_seconds)
    output_path.parent.mkdir(parents=True, exist_ok=True)

    with tempfile.TemporaryDirectory(dir=output_path.parent) as tmpdir:
        template = str(Path(tmpdir) / "clip.%(ext)s")
        _run(
            [
                "yt-dlp",
                "--quiet",
                "--no-warnings",
                "--no-playlist",
                *ytdlp_cookie_args(),
                "-f",
                "bv*+ba/b",
                "--download-sections",
                f"*{_format_timestamp(start_seconds)}-{_format_timestamp(end_seconds)}",
                "--force-keyframes-at-cuts",
                "--merge-output-format",
                "mp4",
                "-o",
                template,
                source_url,
            ]
        )

        candidates = sorted(Path(tmpdir).glob("clip.*"), key=lambda path: path.stat().st_mtime)
        if not candidates:
            raise RuntimeError("yt-dlp did not produce a rendered clip")

        rendered = candidates[-1]
        _transcode_to_mp4(source_path=rendered, output_path=output_path)


def render_clip_from_segments(
    *,
    segment_paths: list[Path],
    output_path: Path,
    first_segment_start_seconds: float,
    start_seconds: float,
    end_seconds: float,
) -> None:
    """Render a clip window that may span several consecutive archive segments.

    Segments were cut on keyframes, so nominal offsets can drift by up to a
    keyframe interval (~2s) from true stream time."""
    _validate_window(start_seconds=start_seconds, end_seconds=end_seconds)
    missing = [str(path) for path in segment_paths if not path.exists()]
    if missing:
        raise RuntimeError(f"Missing source segments for clip render: {', '.join(missing)}")

    output_path.parent.mkdir(parents=True, exist_ok=True)
    relative_start = max(0, start_seconds - first_segment_start_seconds)
    duration = end_seconds - start_seconds

    with tempfile.TemporaryDirectory(dir=output_path.parent) as tmpdir:
        concat_list = Path(tmpdir) / "segments.txt"
        concat_list.write_text(
            "".join(f"file '{path.resolve()}'\n" for path in segment_paths)
        )
        _run(
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
                "-ss",
                f"{relative_start:.3f}",
                "-t",
                f"{duration:.3f}",
                "-map",
                "0:v:0",
                "-map",
                "0:a:0?",
                "-c:v",
                "libx264",
                "-preset",
                "veryfast",
                "-crf",
                "30",
                "-vf",
                "scale='min(720,iw)':-2",
                "-c:a",
                "aac",
                "-b:a",
                "128k",
                "-movflags",
                "+faststart",
                str(output_path),
            ]
        )


def extract_thumbnail(*, clip_path: Path, output_path: Path, at_seconds: float) -> None:
    """Extract a single JPEG frame (~480px wide) from a rendered clip."""
    output_path.parent.mkdir(parents=True, exist_ok=True)
    _run(
        [
            "ffmpeg",
            "-hide_banner",
            "-loglevel",
            "error",
            "-y",
            "-ss",
            f"{max(0.0, at_seconds):.3f}",
            "-i",
            str(clip_path),
            "-frames:v",
            "1",
            "-vf",
            "scale='min(480,iw)':-2",
            "-q:v",
            "4",
            str(output_path),
        ]
    )
    if not output_path.exists():
        raise RuntimeError("ffmpeg produced no thumbnail frame")


def _transcode_to_mp4(*, source_path: Path, output_path: Path) -> None:
    _run(
        [
            "ffmpeg",
            "-hide_banner",
            "-loglevel",
            "error",
            "-y",
            "-i",
            str(source_path),
            "-c:v",
            "libx264",
            "-preset",
            "veryfast",
            "-crf",
            "30",
            "-vf",
            "scale='min(720,iw)':-2",
            "-c:a",
            "aac",
            "-b:a",
            "128k",
            "-movflags",
            "+faststart",
            str(output_path),
        ]
    )


def _validate_window(*, start_seconds: float, end_seconds: float) -> None:
    if end_seconds <= start_seconds:
        raise RuntimeError("Clip end_seconds must be greater than start_seconds")


def _format_timestamp(seconds: float) -> str:
    whole_seconds = int(seconds)
    milliseconds = int(round((seconds - whole_seconds) * 1000))
    if milliseconds == 1000:
        whole_seconds += 1
        milliseconds = 0

    hours, remainder = divmod(whole_seconds, 3600)
    minutes, secs = divmod(remainder, 60)
    return f"{hours:02d}:{minutes:02d}:{secs:02d}.{milliseconds:03d}"


def _run(command: list[str]) -> None:
    result = subprocess.run(command, capture_output=True, text=True)
    if result.returncode != 0:
        details = (result.stderr or result.stdout).strip()
        raise RuntimeError(details or f"Command failed with exit code {result.returncode}")
