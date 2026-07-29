"""Optional YouTube cookie support for yt-dlp.

Datacenter IPs trip YouTube's "Sign in to confirm you're not a bot" gate, so
the worker's secrets bundle MAY carry YTDLP_COOKIES_B64 (a base64-encoded
Netscape cookies.txt). When set, it is decoded once to a private (0600) file
and every yt-dlp invocation gets --cookies <file>; when empty or absent,
behavior is unchanged. Cookie contents are never logged."""

import base64
import os
from pathlib import Path

COOKIES_ENV_VAR = "YTDLP_COOKIES_B64"

_COOKIE_PATH = Path("data") / "yt_cookies.txt"

# None = not prepared yet; [] = prepared, no cookies; ['--cookies', path] = prepared.
_cookie_args: list[str] | None = None


def prepare_ytdlp_cookies() -> None:
    """Decode YTDLP_COOKIES_B64 to a private cookie file (idempotent).

    Call once at startup so a malformed value fails fast: the RuntimeError is
    raised before capture begins, where ingest's pre-capture failure handling
    writes the readable error back to the project row in attach mode."""
    global _cookie_args
    if _cookie_args is not None:
        return

    encoded = "".join(os.environ.get(COOKIES_ENV_VAR, "").split())
    if not encoded:
        _cookie_args = []
        return

    try:
        decoded = base64.b64decode(encoded, validate=True)
    except ValueError as exc:
        raise RuntimeError(
            f"{COOKIES_ENV_VAR} is not valid base64 (expected a base64-encoded Netscape cookies.txt)"
        ) from exc

    _COOKIE_PATH.parent.mkdir(parents=True, exist_ok=True)
    # Owner-only from creation, and re-chmod in case the file already existed.
    fd = os.open(_COOKIE_PATH, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(fd, "wb") as file:
        file.write(decoded)
    os.chmod(_COOKIE_PATH, 0o600)
    _cookie_args = ["--cookies", str(_COOKIE_PATH)]


def ytdlp_cookie_args() -> list[str]:
    """Extra yt-dlp arguments: ['--cookies', <path>] when cookies were provided, else []."""
    prepare_ytdlp_cookies()
    return list(_cookie_args)
