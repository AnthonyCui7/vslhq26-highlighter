"""Model provider seam: ordered per-role chains over Azure OpenAI and OpenRouter.

Every LLM call in the pipeline is one of two roles:

- editor — text and vision calls (long-form pass 2, the auto-reframe framing
  call, the research attempt): the Azure deployment first when its env vars
  are set (AZURE_EDITOR_ENDPOINT / AZURE_EDITOR_KEY / AZURE_EDITOR_DEPLOYMENT),
  then OpenRouter Gemini.
- audio — calls that hear the source (pass-1 clip scoring, the revision
  agent): OpenRouter Gemini first, then the Azure deployment
  (AZURE_AUDIO_ENDPOINT / AZURE_AUDIO_KEY / AZURE_AUDIO_DEPLOYMENT).

A single shared resource also works: AZURE_OPENAI_ENDPOINT and
AZURE_OPENAI_API_KEY with AZURE_OPENAI_EDIT_DEPLOYMENT /
AZURE_OPENAI_AUDIO_DEPLOYMENT are the fallback names for both roles, and the
role-specific names override them per role.

run_with_fallback tries a chain in order, so a missing or failing provider
degrades to the next one instead of failing the run.

Azure requests go through the resource's OpenAI-compatible v1 endpoint
(<endpoint>/openai/v1) with the deployment name as the model. Reasoning
deployments run at the highest reasoning effort (AZURE_REASONING_EFFORT to
override) and are sent no temperature — they reject explicit values. Audio
deployments take temperature but no reasoning knob, and the gpt-audio family
has no structured outputs — callers check supports_json_schema and fall back
to a schema embedded in the prompt.
"""

import os
from dataclasses import dataclass, field
from typing import Any, Callable, TypeVar

from .defaults import (
    DEFAULT_LLM_REASONING_EFFORT,
    DEFAULT_OPENROUTER_MODEL,
)

OPENROUTER_BASE_URL = "https://openrouter.ai/api/v1"
OPENROUTER_VERTEX_PROVIDER = "google-vertex"

T = TypeVar("T")


@dataclass(frozen=True)
class Provider:
    name: str  # "azure" | "openrouter"
    label: str  # for logs and records, e.g. "Azure OpenAI (gpt-5.4)"
    base_url: str
    api_key: str
    model: str  # Azure deployment name or OpenRouter model id
    temperature: float | None  # None -> omitted (Azure reasoning models reject it)
    supports_json_schema: bool = True
    extra_body: dict[str, Any] = field(default_factory=dict)
    default_headers: dict[str, str] | None = None

    def client(self, *, timeout: float) -> Any:
        try:
            from openai import OpenAI
        except ImportError as exc:
            raise RuntimeError("LLM calls require the 'openai' package") from exc
        return OpenAI(
            base_url=self.base_url,
            api_key=self.api_key,
            timeout=timeout,
            default_headers=self.default_headers,
        )

    def request_kwargs(self) -> dict[str, Any]:
        kwargs: dict[str, Any] = {"model": self.model}
        if self.temperature is not None:
            kwargs["temperature"] = self.temperature
        if self.extra_body:
            kwargs["extra_body"] = self.extra_body
        return kwargs


def azure_editor_provider() -> Provider | None:
    """The editor-role Azure deployment alone, when configured."""
    return _azure_provider(
        endpoint_keys=["AZURE_EDITOR_ENDPOINT", "AZURE_OPENAI_ENDPOINT"],
        api_keys=["AZURE_EDITOR_KEY", "AZURE_EDITOR_API_KEY", "AZURE_OPENAI_API_KEY"],
        deployment_keys=["AZURE_EDITOR_DEPLOYMENT", "AZURE_OPENAI_EDIT_DEPLOYMENT"],
        reasoning=True,
    )


def editor_providers(
    *,
    title: str,
    openrouter_reasoning_effort: str = DEFAULT_LLM_REASONING_EFFORT,
) -> list[Provider]:
    """Provider chain for text/vision editorial calls."""
    return _chain(
        azure_editor_provider(),
        _openrouter_provider(title=title, reasoning_effort=openrouter_reasoning_effort),
        role="editor",
    )


def audio_providers(
    *,
    title: str,
    openrouter_reasoning_effort: str = DEFAULT_LLM_REASONING_EFFORT,
) -> list[Provider]:
    """Provider chain for calls that attach source audio."""
    return _chain(
        _openrouter_provider(title=title, reasoning_effort=openrouter_reasoning_effort),
        _azure_provider(
            endpoint_keys=["AZURE_AUDIO_ENDPOINT", "AZURE_OPENAI_ENDPOINT"],
            api_keys=["AZURE_AUDIO_KEY", "AZURE_AUDIO_API_KEY", "AZURE_OPENAI_API_KEY"],
            deployment_keys=["AZURE_AUDIO_DEPLOYMENT", "AZURE_OPENAI_AUDIO_DEPLOYMENT"],
            reasoning=False,
        ),
        role="audio",
    )


def chain_label(providers: list[Provider]) -> str:
    if len(providers) == 1:
        return providers[0].label
    fallbacks = ", ".join(provider.label for provider in providers[1:])
    return f"{providers[0].label} (fallback: {fallbacks})"


def run_with_fallback(
    providers: list[Provider], call: Callable[[Provider], T]
) -> tuple[T, Provider]:
    """Try the chain in order; return (result, provider that produced it)."""
    last_error: Exception = RuntimeError("No model provider is configured")
    for index, provider in enumerate(providers):
        try:
            return call(provider), provider
        except Exception as exc:
            last_error = exc
            if index + 1 < len(providers):
                print(
                    f"{provider.label} call failed; falling back to "
                    f"{providers[index + 1].label}: {exc}"
                )
    raise last_error


def _chain(
    first: Provider | None, second: Provider | None, *, role: str
) -> list[Provider]:
    providers = [provider for provider in (first, second) if provider is not None]
    if not providers:
        raise RuntimeError(
            f"No {role} model is configured: set AZURE_{role.upper()}_ENDPOINT/KEY/"
            "DEPLOYMENT (or the shared AZURE_OPENAI_* names) or OPENROUTER_API_KEY"
        )
    return providers


def _max_reasoning_effort(deployment: str) -> str:
    """The highest reasoning effort the deployment accepts: xhigh exists only
    on the gpt-5.4+ line; everything else tops out at high."""
    return "xhigh" if any(family in deployment for family in ("5.4", "5.5", "5.6")) else "high"


def _first_env(keys: list[str]) -> str | None:
    for key in keys:
        value = os.environ.get(key)
        if value:
            return value
    return None


def _azure_provider(
    *,
    endpoint_keys: list[str],
    api_keys: list[str],
    deployment_keys: list[str],
    reasoning: bool,
) -> Provider | None:
    endpoint = _first_env(endpoint_keys)
    key = _first_env(api_keys)
    deployment = _first_env(deployment_keys)
    if not (endpoint and key and deployment):
        return None
    if reasoning:
        # gpt-5.x reasoning deployments: highest effort the family accepts,
        # default temperature (explicit values are rejected), structured
        # outputs supported.
        extra_body: dict[str, Any] = {
            "reasoning_effort": os.environ.get("AZURE_REASONING_EFFORT")
            or _max_reasoning_effort(deployment)
        }
        temperature = None
        supports_json_schema = True
    else:
        # gpt-audio deployments: no reasoning knob, no structured outputs;
        # text-only output requested explicitly.
        extra_body = {"modalities": ["text"]}
        temperature = 0.0
        supports_json_schema = False

    base_url = endpoint.rstrip("/")
    if not base_url.endswith("/openai/v1"):
        base_url += "/openai/v1"
    return Provider(
        name="azure",
        label=f"Azure OpenAI ({deployment})",
        base_url=base_url,
        api_key=key,
        model=deployment,
        temperature=temperature,
        supports_json_schema=supports_json_schema,
        extra_body=extra_body,
    )


def _openrouter_provider(*, title: str, reasoning_effort: str) -> Provider | None:
    api_key = os.environ.get("OPENROUTER_API_KEY")
    if not api_key:
        return None
    return Provider(
        name="openrouter",
        label=f"OpenRouter Gemini ({DEFAULT_OPENROUTER_MODEL})",
        base_url=OPENROUTER_BASE_URL,
        api_key=api_key,
        model=DEFAULT_OPENROUTER_MODEL,
        temperature=0.0,
        extra_body={
            "provider": {
                "order": [OPENROUTER_VERTEX_PROVIDER],
                "allow_fallbacks": False,
            },
            "reasoning": {"effort": reasoning_effort or DEFAULT_LLM_REASONING_EFFORT},
        },
        default_headers={
            "HTTP-Referer": "http://localhost",
            "X-OpenRouter-Title": title,
        },
    )
