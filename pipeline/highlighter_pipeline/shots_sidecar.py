"""JSON-lines sidecar exposing TransNetV2 shot detection to the .NET pipeline.

The C# port keeps every part of shots.py except the PyTorch inference, which
stays here behind a line protocol:

    stdin   {"path": "<segment.ts>", "start_seconds": 0.0}\n      one per request
    stdout  {"cuts": [12.34, ...]}\n  or  {"error": "..."}\n      one per request

On startup the sidecar emits {"ready": true} once the optional dependencies
imported; an import failure exits non-zero before the handshake, which the C#
side treats exactly like the missing-import case in shot_detector_from_env()
(log once, run without scene cuts). The model loads lazily on the first
request and is reused for the rest of the run, matching ShotDetector.

Protocol writes go to the real stdout only; sys.stdout is rebound to stderr so
stray prints from torch/transnetv2 cannot corrupt the stream.
"""

import json
import sys


def main() -> None:
    protocol = sys.stdout
    sys.stdout = sys.stderr  # stray prints must not corrupt the protocol

    def send(payload: dict) -> None:
        protocol.write(json.dumps(payload) + "\n")
        protocol.flush()

    try:
        import numpy  # noqa: F401
        import torch  # noqa: F401
        from transnetv2_pytorch import TransNetV2  # noqa: F401
    except ImportError as exc:
        print(f"shots sidecar: optional dependencies missing ({exc})", file=sys.stderr)
        raise SystemExit(1)

    from .shots import ShotDetector

    detector = ShotDetector()
    send({"ready": True})

    for raw_line in sys.stdin:
        line = raw_line.strip()
        if not line:
            continue
        try:
            request = json.loads(line)
            cuts = detector.detect_cuts(
                segment_path=request["path"],
                segment_start_seconds=float(request["start_seconds"]),
            )
            send({"cuts": cuts})
        except Exception as exc:  # per-request failures must not kill the sidecar
            send({"error": str(exc)})


if __name__ == "__main__":
    main()
