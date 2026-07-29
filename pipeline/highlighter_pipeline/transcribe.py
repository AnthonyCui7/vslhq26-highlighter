"""Chunk transcription: Azure AI Speech first, Deepgram fallback.

Azure's fast transcription API runs when AZURE_SPEECH_KEY and
AZURE_SPEECH_REGION are set; any Azure failure falls back to Deepgram for that
chunk, and Deepgram alone runs when Azure is not configured. Both backends
return the same shape — {"transcript", "words", "metadata", "backend"} with
Deepgram-style word dicts ({word, punctuated_word, start, end, confidence},
in seconds) — so everything downstream is backend-agnostic.
"""

import json
import os
import uuid
from pathlib import Path
from typing import Any
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen

from .deepgram import transcribe_audio_file as _deepgram_transcribe
from .defaults import DEFAULT_AZURE_SPEECH_LOCALE, DEFAULT_DEEPGRAM_MODEL

AZURE_SPEECH_API_VERSION = "2024-11-15"


def _azure_speech_base_url() -> str | None:
    """The Speech resource endpoint when configured: AZURE_SPEECH_ENDPOINT
    wins, else the regional endpoint built from AZURE_SPEECH_REGION."""
    if not os.environ.get("AZURE_SPEECH_KEY"):
        return None
    endpoint = os.environ.get("AZURE_SPEECH_ENDPOINT")
    if endpoint:
        return endpoint.rstrip("/")
    region = os.environ.get("AZURE_SPEECH_REGION")
    if region:
        return f"https://{region}.api.cognitive.microsoft.com"
    return None


def transcription_backend_label() -> str:
    if _azure_speech_base_url():
        return "azure-speech (fallback: deepgram)"
    return "deepgram"


def transcribe_audio_file(
    audio_path: Path, *, model: str = DEFAULT_DEEPGRAM_MODEL
) -> dict[str, Any]:
    base_url = _azure_speech_base_url()
    if base_url:
        try:
            return _azure_fast_transcription(
                audio_path, key=os.environ["AZURE_SPEECH_KEY"], base_url=base_url
            )
        except Exception as exc:
            print(f"Azure Speech transcription failed; falling back to Deepgram: {exc}")
    result = _deepgram_transcribe(audio_path, model=model)
    return {**result, "backend": "deepgram"}


def _azure_fast_transcription(
    audio_path: Path, *, key: str, base_url: str
) -> dict[str, Any]:
    locale = os.environ.get("AZURE_SPEECH_LOCALE", DEFAULT_AZURE_SPEECH_LOCALE)
    body, content_type = _multipart_body(
        audio_path, definition=json.dumps({"locales": [locale]})
    )
    request = Request(
        f"{base_url}/speechtotext/transcriptions:transcribe"
        f"?api-version={AZURE_SPEECH_API_VERSION}",
        data=body,
        headers={"Ocp-Apim-Subscription-Key": key, "Content-Type": content_type},
        method="POST",
    )

    try:
        with urlopen(request, timeout=120) as response:
            payload = response.read().decode("utf-8")
    except HTTPError as exc:
        message = exc.read().decode("utf-8", errors="replace")
        raise RuntimeError(f"Azure Speech request failed: {message}") from exc
    except URLError as exc:
        raise RuntimeError(f"Azure Speech request failed: {exc.reason}") from exc

    return _parse_fast_transcription(json.loads(payload), locale=locale)


def _parse_fast_transcription(data: dict[str, Any], *, locale: str) -> dict[str, Any]:
    """Map a fast-transcription response onto the Deepgram word shape the rest
    of the pipeline consumes. Azure has no per-word confidence, so each word
    carries its phrase's confidence."""
    words = [
        {
            "word": word["text"],
            "punctuated_word": word["text"],
            "start": round(word["offsetMilliseconds"] / 1000, 3),
            "end": round(
                (word["offsetMilliseconds"] + word["durationMilliseconds"]) / 1000, 3
            ),
            "confidence": phrase.get("confidence"),
        }
        for phrase in data.get("phrases", [])
        for word in phrase.get("words", [])
    ]
    transcript = " ".join(
        phrase.get("text", "") for phrase in data.get("combinedPhrases", [])
    ).strip()
    return {
        "transcript": transcript,
        "words": words,
        "metadata": {
            "locale": locale,
            "duration_milliseconds": data.get("durationMilliseconds"),
        },
        "backend": "azure-speech",
    }


def _multipart_body(audio_path: Path, *, definition: str) -> tuple[bytes, str]:
    boundary = uuid.uuid4().hex
    parts = [
        f"--{boundary}\r\n"
        'Content-Disposition: form-data; name="definition"\r\n'
        "Content-Type: application/json\r\n\r\n"
        f"{definition}\r\n",
        f"--{boundary}\r\n"
        f'Content-Disposition: form-data; name="audio"; filename="{audio_path.name}"\r\n'
        "Content-Type: audio/wav\r\n\r\n",
    ]
    body = (
        parts[0].encode("utf-8")
        + parts[1].encode("utf-8")
        + audio_path.read_bytes()
        + f"\r\n--{boundary}--\r\n".encode("utf-8")
    )
    return body, f"multipart/form-data; boundary={boundary}"
