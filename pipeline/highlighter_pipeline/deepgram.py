import json
from pathlib import Path
from typing import Any
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode
from urllib.request import Request, urlopen

from .config import required_env
from .defaults import DEFAULT_DEEPGRAM_MODEL


def transcribe_audio_file(audio_path: Path, *, model: str = DEFAULT_DEEPGRAM_MODEL) -> dict[str, Any]:
    query = urlencode(
        {
            "model": model or DEFAULT_DEEPGRAM_MODEL,
            "smart_format": "true",
            "paragraphs": "true",
            "utterances": "true",
        }
    )
    request = Request(
        f"https://api.deepgram.com/v1/listen?{query}",
        data=audio_path.read_bytes(),
        headers={
            "Authorization": f"Token {required_env('DEEPGRAM_API_KEY')}",
            "Content-Type": "audio/wav",
        },
        method="POST",
    )

    try:
        with urlopen(request, timeout=120) as response:
            payload = response.read().decode("utf-8")
    except HTTPError as exc:
        message = exc.read().decode("utf-8", errors="replace")
        raise RuntimeError(f"Deepgram request failed: {message}") from exc
    except URLError as exc:
        raise RuntimeError(f"Deepgram request failed: {exc.reason}") from exc

    data = json.loads(payload)
    alternative = data.get("results", {}).get("channels", [{}])[0].get("alternatives", [{}])[0]
    return {
        "transcript": alternative.get("transcript", ""),
        "words": alternative.get("words", []),
        "metadata": data.get("metadata", {}),
    }
