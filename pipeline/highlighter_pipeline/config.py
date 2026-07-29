import os
from pathlib import Path


def load_env(path: str | None = None) -> None:
    """Load a .env file. With no explicit path, walk up from the working
    directory so the monorepo root .env is found from any subdirectory."""
    if path is not None:
        candidates = [Path(path)]
    else:
        cwd = Path.cwd()
        candidates = [directory / ".env" for directory in (cwd, *cwd.parents)]

    env_path = next((candidate for candidate in candidates if candidate.exists()), None)
    if env_path is None:
        return

    for raw_line in env_path.read_text().splitlines():
        line = raw_line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue

        key, value = line.split("=", 1)
        key = key.strip()
        if not key or key in os.environ:
            continue

        os.environ[key] = _unquote(value.strip())


def required_env(key: str) -> str:
    value = os.environ.get(key)
    if not value:
        raise RuntimeError(f"Missing required env var: {key}")
    return value


def required_any_env(keys: list[str]) -> str:
    for key in keys:
        value = os.environ.get(key)
        if value:
            return value
    raise RuntimeError(f"Missing required env var: {' or '.join(keys)}")


def int_env(key: str, default: int) -> int:
    raw_value = os.environ.get(key)
    if not raw_value:
        return default

    try:
        return int(raw_value)
    except ValueError as exc:
        raise RuntimeError(f"{key} must be an integer") from exc


def float_env(key: str, default: float | None = None) -> float | None:
    raw_value = os.environ.get(key)
    if not raw_value:
        return default

    try:
        return float(raw_value)
    except ValueError as exc:
        raise RuntimeError(f"{key} must be a number") from exc


def _unquote(value: str) -> str:
    if len(value) >= 2 and value[0] == value[-1] and value[0] in ("'", '"'):
        return value[1:-1]
    return value
