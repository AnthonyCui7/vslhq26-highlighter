import shutil
import subprocess
import threading
import time
from collections import deque
from pathlib import Path
from typing import Callable

from .cookies import ytdlp_cookie_args
from .defaults import DEFAULT_STREAMLINK_QUALITY


ChunkCallback = Callable[[Path, int, int, int], None]
VideoSegmentCallback = Callable[[Path, int], None]


def assert_capture_prerequisites(downloader: str = "streamlink") -> None:
    _assert_binary("ffmpeg", "brew install ffmpeg")
    if downloader == "yt-dlp":
        _assert_binary("yt-dlp", "pip install yt-dlp")
    else:
        _assert_binary("streamlink", "brew install streamlink")


def capture_audio_chunks(
    *,
    source_url: str,
    output_dir: Path,
    chunk_seconds: int,
    max_chunks: int,
    streamlink_quality: str,
    on_chunk: ChunkCallback,
    downloader: str = "streamlink",
    source_type: str = "video",
    archive_dir: Path | None = None,
    on_video_segment: VideoSegmentCallback | None = None,
    should_stop: Callable[[], bool] | None = None,
) -> int:
    """Capture audio chunks for transcription; optionally also archive the source
    video as codec-copied segments (cut on keyframes, so boundaries are approximate).

    downloader: 'streamlink' taps live Twitch broadcasts; 'yt-dlp' handles
    everything else (all of YouTube, since only yt-dlp has cookie support for
    YouTube's bot wall, plus finished Twitch VODs).

    source_type: for yt-dlp, 'livestream' emits the full AV stream as pipeable
    MPEG-TS (so the live tee into wav chunks + archived .ts segments works
    like the streamlink path); 'video' also pulls the full AV stream whenever
    archive_dir is set — clips are rendered from the local segments — and
    falls back to audio-only for transcription-only runs. Ignored for
    streamlink, which always taps live.

    should_stop: polled between sweeps and before each ready file within a
    sweep; returning True ends the capture early, skipping any files that have
    not been processed yet (the caller handles queued leftovers)."""
    assert_capture_prerequisites(downloader)
    output_dir.mkdir(parents=True, exist_ok=True)
    if archive_dir is not None:
        archive_dir.mkdir(parents=True, exist_ok=True)

    if downloader == "yt-dlp" and source_type == "livestream":
        # Full AV stream to stdout for the live tee. -f b: best pre-merged
        # format (merging separate streams is impossible when writing to
        # stdout). --hls-use-mpegts keeps HLS output pipeable MPEG-TS even
        # when the "livestream" turns out to be a plain uploaded video
        # (yt-dlp only defaults to it for actual live streams); such a
        # mislabeled video simply ends at EOF like a finished broadcast.
        source_cmd = [
            "yt-dlp",
            "--quiet",
            "--no-warnings",
            *ytdlp_cookie_args(),
            "-f",
            "b",
            "--no-part",
            "--hls-use-mpegts",
            "-o",
            "-",
            source_url,
        ]
    elif downloader == "yt-dlp" and archive_dir is not None:
        # VOD with a local archive: pull the full AV stream once — clips are
        # rendered from the archived segments, because seeking remote HLS is
        # unreliable (Debian ffmpeg mangles Twitch VOD seeks into empty
        # files) and VODs/highlights can be deleted mid-job.
        source_cmd = [
            "yt-dlp",
            "--quiet",
            "--no-warnings",
            *ytdlp_cookie_args(),
            # Twitch highlight VODs place the HLS init fragment after media
            # fragments; yt-dlp's native downloader refuses that ("unable to
            # download"), ffmpeg streams it fine. Scoped to m3u8 so other
            # protocols keep the native downloader.
            "--downloader",
            "m3u8:ffmpeg",
            "-f",
            "b",
            "--no-part",
            "-o",
            "-",
            source_url,
        ]
    elif downloader == "yt-dlp":
        source_cmd = [
            "yt-dlp",
            "--quiet",
            "--no-warnings",
            *ytdlp_cookie_args(),
            # Same m3u8 quirk as above; audio-only is enough when no local
            # archive is kept (transcription-only runs).
            "--downloader",
            "m3u8:ffmpeg",
            "-f",
            "ba/b",
            "-o",
            "-",
            source_url,
        ]
    else:
        source_cmd = [
            "streamlink",
            "--stdout",
            source_url,
            streamlink_quality or DEFAULT_STREAMLINK_QUALITY,
        ]

    source = subprocess.Popen(
        source_cmd,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )
    source_stderr = _StderrTail(source)

    duration_args = ["-t", str(max_chunks * chunk_seconds)] if max_chunks > 0 else []
    segment_args = [
        "-f",
        "segment",
        "-segment_time",
        str(chunk_seconds),
        "-reset_timestamps",
        "1",
    ]

    ffmpeg_args = [
        "ffmpeg",
        "-hide_banner",
        "-loglevel",
        "warning",
        "-i",
        "pipe:0",
        # 16kHz mono WAV chunks for transcription
        "-vn",
        "-map",
        "0:a:0",
        "-ac",
        "1",
        "-ar",
        "16000",
        *duration_args,
        *segment_args,
        "-c:a",
        "pcm_s16le",
        str(output_dir / "audio_%05d.wav"),
    ]
    if archive_dir is not None:
        ffmpeg_args.extend(
            [
                # full source archive, no re-encode
                "-map",
                "0",
                "-c",
                "copy",
                *duration_args,
                *segment_args,
                str(archive_dir / "video_%05d.ts"),
            ]
        )

    ffmpeg = subprocess.Popen(
        ffmpeg_args,
        stdin=source.stdout,
        stderr=subprocess.PIPE,
    )
    ffmpeg_stderr = _StderrTail(ffmpeg)
    if source.stdout:
        source.stdout.close()

    processed: set[str] = set()
    archived: set[str] = set()

    def _sweep(include_latest: bool) -> None:
        # Video segments before audio chunks: both finalize together (one
        # ffmpeg segmenter), and the segment handler derives per-chunk scene
        # cuts that the transcript store and clip scoring want available.
        if archive_dir is not None and on_video_segment is not None:
            _process_ready_files(
                archive_dir,
                "video_*.ts",
                archived,
                include_latest=include_latest,
                handler=on_video_segment,
                should_stop=should_stop,
            )
        _process_ready_files(
            output_dir,
            "audio_*.wav",
            processed,
            include_latest=include_latest,
            handler=lambda path, index: on_chunk(
                path, index, index * chunk_seconds, (index + 1) * chunk_seconds
            ),
            should_stop=should_stop,
        )

    stopped = False
    try:
        while ffmpeg.poll() is None:
            if should_stop is not None and should_stop():
                stopped = True
                # Stop like a natural stream end: close the source so ffmpeg
                # flushes and finalizes the in-progress output files.
                _terminate(source)
                try:
                    ffmpeg.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    _terminate(ffmpeg)
                break
            _sweep(include_latest=False)
            time.sleep(2)

        # The final sweep can hold the entire backlog (a VOD finishes
        # downloading long before its chunks are processed), so honor a stop
        # here too instead of grinding through every remaining file.
        if should_stop is not None and should_stop():
            stopped = True
        if not stopped:
            _sweep(include_latest=True)
    except Exception:
        _terminate(ffmpeg)
        _terminate(source)
        raise

    _terminate(source)

    if not stopped and ffmpeg.returncode != 0:
        details = "\n".join(part for part in [ffmpeg_stderr.text(), source_stderr.text()] if part)
        raise RuntimeError(f"ffmpeg exited with code {ffmpeg.returncode}{chr(10) + details if details else ''}")

    return len(processed)


def _process_ready_files(
    directory: Path,
    pattern: str,
    processed: set[str],
    *,
    include_latest: bool,
    handler: Callable[[Path, int], None],
    should_stop: Callable[[], bool] | None = None,
) -> None:
    files = sorted(directory.glob(pattern))
    ready_files = files if include_latest else files[:-1]

    for path in ready_files:
        if path.name in processed:
            continue
        if should_stop is not None and should_stop():
            # A stop arrived mid-backlog: skip the remaining unprocessed files.
            return

        index = int(path.stem.split("_")[1])
        handler(path, index)
        processed.add(path.name)


def _assert_binary(name: str, install_hint: str) -> None:
    if shutil.which(name) is None:
        raise RuntimeError(f"Missing required binary: {name}. Install locally with '{install_hint}'.")


def _terminate(process: subprocess.Popen) -> None:
    if process.poll() is None:
        process.terminate()
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            process.kill()


class _StderrTail:
    def __init__(self, process: subprocess.Popen) -> None:
        self._lines: deque[str] = deque(maxlen=80)
        self._thread = threading.Thread(target=self._read, args=(process,), daemon=True)
        self._thread.start()

    def text(self) -> str:
        return "".join(self._lines).strip()

    def _read(self, process: subprocess.Popen) -> None:
        if not process.stderr:
            return
        for line in iter(process.stderr.readline, b""):
            self._lines.append(line.decode("utf-8", errors="replace"))
