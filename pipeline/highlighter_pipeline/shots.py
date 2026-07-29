"""Shot-boundary detection and cut-aligned clip boundary snapping.

TransNetV2 finds the source's own visual transitions in each archived video
segment. The cut timestamps feed the editor prompts as context (llm.py) and,
after the model has picked a clip window, snap_window() moves each boundary to
a nearby cut so clips start and end on the source's own edit points instead of
mid-transition. The model picks the moment; code picks the frame.

The torch/transnetv2 dependency is optional (`pip install
'highlighter-pipeline[shots]'`); shot_detector_from_env() degrades to None with
one log line when it is missing, and every consumer treats cuts as optional.
"""

import bisect
import subprocess
from pathlib import Path

from .defaults import DEFAULT_SHOT_FPS, DEFAULT_SHOT_SNAP_TOLERANCE_SECONDS

# Sigmoid probability above which a frame is part of a transition (the
# TransNetV2 paper's operating point).
TRANSITION_THRESHOLD = 0.5
# Snapping must never produce a degenerate clip.
MIN_SNAPPED_CLIP_SECONDS = 2.0

# TransNetV2's fixed input resolution.
_FRAME_WIDTH = 48
_FRAME_HEIGHT = 27


def shot_detector_from_env(enabled: bool) -> "ShotDetector | None":
    """A ready ShotDetector, or None when disabled or the optional
    dependencies are not installed (logged once; the pipeline runs without
    scene cuts either way)."""
    if not enabled:
        return None
    try:
        import numpy  # noqa: F401
        import torch  # noqa: F401
        from transnetv2_pytorch import TransNetV2  # noqa: F401
    except ImportError as exc:
        print(
            f"Shot detection unavailable ({exc}); continuing without scene cuts. "
            "Install with: pip install 'highlighter-pipeline[shots]'"
        )
        return None
    return ShotDetector()


class ShotDetector:
    """TransNetV2 inference over archived source segments. The model loads
    lazily on the first call and is reused for the rest of the run."""

    def __init__(self) -> None:
        self._model = None

    def detect_cuts(self, segment_path: Path, segment_start_seconds: float) -> list[float]:
        """Absolute timestamps (seconds) of the shot transitions in one
        segment. Consecutive transition frames collapse into one cut at the
        run's middle frame."""
        import numpy as np
        import torch

        frames = self._extract_frames(segment_path)
        if len(frames) == 0:
            return []

        model = self._load_model()
        with torch.no_grad():
            predictions, _ = model.predict_frames(torch.from_numpy(frames), quiet=True)
        predictions = predictions.numpy()

        cuts: list[float] = []
        run_start = None
        for index, probability in enumerate([*predictions, 0.0]):  # sentinel closes a trailing run
            if probability > TRANSITION_THRESHOLD:
                if run_start is None:
                    run_start = index
                continue
            if run_start is not None:
                middle = (run_start + index - 1) / 2
                cuts.append(round(middle / DEFAULT_SHOT_FPS + segment_start_seconds, 2))
                run_start = None
        return cuts

    def _load_model(self):
        if self._model is None:
            import warnings

            from transnetv2_pytorch import TransNetV2

            with warnings.catch_warnings():
                # The bundled weight loading trips torch.load's FutureWarning
                # about weights_only; the weights ship with the package.
                warnings.filterwarnings("ignore", category=FutureWarning)
                self._model = TransNetV2(device="cpu")
        return self._model

    def _extract_frames(self, segment_path: Path):
        """Decode the segment to TransNetV2's input: RGB frames at
        DEFAULT_SHOT_FPS, 48x27, shape (n, 27, 48, 3) uint8."""
        import numpy as np

        result = subprocess.run(
            [
                "ffmpeg",
                "-hide_banner",
                "-loglevel",
                "error",
                "-i",
                str(segment_path),
                "-vf",
                f"fps={DEFAULT_SHOT_FPS},scale={_FRAME_WIDTH}:{_FRAME_HEIGHT}",
                "-f",
                "rawvideo",
                "-pix_fmt",
                "rgb24",
                "pipe:1",
            ],
            capture_output=True,
        )
        if result.returncode != 0:
            raise RuntimeError(
                result.stderr.decode("utf-8", errors="replace").strip()
                or "ffmpeg failed while extracting shot-detection frames"
            )

        frame_bytes = _FRAME_HEIGHT * _FRAME_WIDTH * 3
        usable = len(result.stdout) - (len(result.stdout) % frame_bytes)
        # copy(): torch.from_numpy needs a writable buffer.
        return (
            np.frombuffer(result.stdout[:usable], dtype=np.uint8)
            .reshape(-1, _FRAME_HEIGHT, _FRAME_WIDTH, 3)
            .copy()
        )


def snap_window(
    start_seconds: float,
    end_seconds: float,
    *,
    cuts: list[float],
    word_spans: list[tuple[float, float]],
    tolerance_seconds: float = DEFAULT_SHOT_SNAP_TOLERANCE_SECONDS,
    min_duration_seconds: float = MIN_SNAPPED_CLIP_SECONDS,
) -> tuple[float, float, dict | None]:
    """Move each clip boundary to the nearest detected cut within
    tolerance_seconds, refusing cuts that land inside a spoken word
    (word_spans: chronologically sorted (absolute_start, absolute_end) pairs)
    so a snap never clips speech. Returns (start, end, info); info describes
    the change, or is None when nothing moved. The original window is kept
    whenever snapping would shrink the clip below min_duration_seconds."""
    snapped_start = _snap_boundary(
        start_seconds, cuts=cuts, word_spans=word_spans, tolerance_seconds=tolerance_seconds
    )
    snapped_end = _snap_boundary(
        end_seconds, cuts=cuts, word_spans=word_spans, tolerance_seconds=tolerance_seconds
    )
    if (snapped_start, snapped_end) == (start_seconds, end_seconds):
        return start_seconds, end_seconds, None
    if snapped_end - snapped_start < min_duration_seconds:
        return start_seconds, end_seconds, None
    return (
        snapped_start,
        snapped_end,
        {
            "original_start": start_seconds,
            "original_end": end_seconds,
            "snapped_start": snapped_start,
            "snapped_end": snapped_end,
        },
    )


def _snap_boundary(
    value: float,
    *,
    cuts: list[float],
    word_spans: list[tuple[float, float]],
    tolerance_seconds: float,
) -> float:
    best = None
    for cut in cuts:
        if abs(cut - value) > tolerance_seconds:
            continue
        if _inside_word(cut, word_spans):
            continue
        if best is None or abs(cut - value) < abs(best - value):
            best = cut
    return value if best is None else best


def _inside_word(timestamp: float, word_spans: list[tuple[float, float]]) -> bool:
    index = bisect.bisect_right(word_spans, (timestamp, float("inf"))) - 1
    if index < 0:
        return False
    word_start, word_end = word_spans[index]
    return word_start < timestamp < word_end
