import pytest

from highlighter_pipeline.providers import (
    audio_providers,
    chain_label,
    editor_providers,
    run_with_fallback,
)
from highlighter_pipeline.transcribe import _parse_fast_transcription

ALL_ENV_KEYS = [
    "OPENROUTER_API_KEY",
    "AZURE_EDITOR_ENDPOINT",
    "AZURE_EDITOR_KEY",
    "AZURE_EDITOR_API_KEY",
    "AZURE_EDITOR_DEPLOYMENT",
    "AZURE_AUDIO_ENDPOINT",
    "AZURE_AUDIO_KEY",
    "AZURE_AUDIO_API_KEY",
    "AZURE_AUDIO_DEPLOYMENT",
    "AZURE_OPENAI_ENDPOINT",
    "AZURE_OPENAI_API_KEY",
    "AZURE_OPENAI_EDIT_DEPLOYMENT",
    "AZURE_OPENAI_AUDIO_DEPLOYMENT",
    "AZURE_REASONING_EFFORT",
]


@pytest.fixture(autouse=True)
def clean_env(monkeypatch):
    for key in ALL_ENV_KEYS:
        monkeypatch.delenv(key, raising=False)


def _set_azure(monkeypatch, prefix, deployment):
    monkeypatch.setenv(f"{prefix}_ENDPOINT", "https://example.openai.azure.com")
    monkeypatch.setenv(f"{prefix}_KEY", "azure-key")
    monkeypatch.setenv(f"{prefix}_DEPLOYMENT", deployment)


def test_openrouter_only_without_azure_env(monkeypatch):
    monkeypatch.setenv("OPENROUTER_API_KEY", "or-key")
    providers = editor_providers(title="t", openrouter_reasoning_effort="high")
    assert [p.name for p in providers] == ["openrouter"]
    assert providers[0].extra_body["provider"]["allow_fallbacks"] is False
    assert providers[0].extra_body["reasoning"] == {"effort": "high"}
    assert providers[0].temperature == 0.0
    assert providers[0].supports_json_schema


def test_azure_editor_runs_first_at_max_reasoning(monkeypatch):
    monkeypatch.setenv("OPENROUTER_API_KEY", "or-key")
    _set_azure(monkeypatch, "AZURE_EDITOR", "gpt-5.4")
    providers = editor_providers(title="t")
    assert [p.name for p in providers] == ["azure", "openrouter"]
    azure = providers[0]
    assert azure.model == "gpt-5.4"
    assert azure.base_url == "https://example.openai.azure.com/openai/v1"
    assert azure.extra_body == {"reasoning_effort": "xhigh"}
    assert azure.temperature is None  # reasoning deployments reject explicit values
    assert azure.supports_json_schema


def test_audio_chain_runs_gemini_first(monkeypatch):
    monkeypatch.setenv("OPENROUTER_API_KEY", "or-key")
    _set_azure(monkeypatch, "AZURE_AUDIO", "gpt-audio-mini")
    providers = audio_providers(title="t")
    assert [p.name for p in providers] == ["openrouter", "azure"]


def test_azure_audio_has_no_reasoning_or_json_schema(monkeypatch):
    _set_azure(monkeypatch, "AZURE_AUDIO", "gpt-audio-mini")
    providers = audio_providers(title="t")
    azure = providers[0]
    assert azure.extra_body == {"modalities": ["text"]}
    assert azure.temperature == 0.0
    assert not azure.supports_json_schema


def test_shared_azure_openai_names_cover_both_roles(monkeypatch):
    monkeypatch.setenv("AZURE_OPENAI_ENDPOINT", "https://shared.openai.azure.com")
    monkeypatch.setenv("AZURE_OPENAI_API_KEY", "shared-key")
    monkeypatch.setenv("AZURE_OPENAI_EDIT_DEPLOYMENT", "gpt-5.4")
    monkeypatch.setenv("AZURE_OPENAI_AUDIO_DEPLOYMENT", "gpt-audio-mini")
    assert editor_providers(title="t")[0].model == "gpt-5.4"
    assert audio_providers(title="t")[0].model == "gpt-audio-mini"


def test_role_specific_names_override_shared_ones(monkeypatch):
    monkeypatch.setenv("AZURE_OPENAI_ENDPOINT", "https://shared.openai.azure.com")
    monkeypatch.setenv("AZURE_OPENAI_API_KEY", "shared-key")
    monkeypatch.setenv("AZURE_OPENAI_AUDIO_DEPLOYMENT", "gpt-audio-mini")
    monkeypatch.setenv("AZURE_AUDIO_ENDPOINT", "https://audio.openai.azure.com")
    monkeypatch.setenv("AZURE_AUDIO_API_KEY", "audio-key")
    audio = audio_providers(title="t")[0]
    assert audio.base_url == "https://audio.openai.azure.com/openai/v1"
    assert audio.api_key == "audio-key"
    assert audio.model == "gpt-audio-mini"


def test_endpoint_already_ending_in_v1_is_not_doubled(monkeypatch):
    monkeypatch.setenv("AZURE_EDITOR_ENDPOINT", "https://example.openai.azure.com/openai/v1/")
    monkeypatch.setenv("AZURE_EDITOR_KEY", "k")
    monkeypatch.setenv("AZURE_EDITOR_DEPLOYMENT", "gpt-5.4")
    providers = editor_providers(title="t")
    assert providers[0].base_url == "https://example.openai.azure.com/openai/v1"


def test_no_configuration_raises():
    with pytest.raises(RuntimeError, match="No editor model is configured"):
        editor_providers(title="t")


def test_request_kwargs_shape(monkeypatch):
    _set_azure(monkeypatch, "AZURE_EDITOR", "gpt-5.4")
    kwargs = editor_providers(title="t")[0].request_kwargs()
    assert kwargs == {"model": "gpt-5.4", "extra_body": {"reasoning_effort": "xhigh"}}


def test_run_with_fallback_uses_second_provider(monkeypatch):
    monkeypatch.setenv("OPENROUTER_API_KEY", "or-key")
    _set_azure(monkeypatch, "AZURE_AUDIO", "gpt-audio-mini")
    providers = audio_providers(title="t")

    def call(provider):
        if provider.name == "openrouter":
            raise RuntimeError("openrouter down")
        return "ok"

    result, provider = run_with_fallback(providers, call)
    assert result == "ok"
    assert provider.name == "azure"


def test_run_with_fallback_raises_last_error(monkeypatch):
    monkeypatch.setenv("OPENROUTER_API_KEY", "or-key")
    providers = audio_providers(title="t")

    def call(provider):
        raise RuntimeError("everything down")

    with pytest.raises(RuntimeError, match="everything down"):
        run_with_fallback(providers, call)


def test_chain_label(monkeypatch):
    monkeypatch.setenv("OPENROUTER_API_KEY", "or-key")
    _set_azure(monkeypatch, "AZURE_AUDIO", "gpt-audio-mini")
    label = chain_label(audio_providers(title="t"))
    assert label.endswith("(fallback: Azure OpenAI (gpt-audio-mini))")
    assert label.startswith("OpenRouter Gemini")


def test_fast_transcription_parsing_maps_to_deepgram_word_shape():
    data = {
        "durationMilliseconds": 4000,
        "combinedPhrases": [{"channel": 0, "text": "Good afternoon. Welcome back."}],
        "phrases": [
            {
                "offsetMilliseconds": 960,
                "durationMilliseconds": 640,
                "text": "Good afternoon.",
                "confidence": 0.93,
                "words": [
                    {"text": "Good", "offsetMilliseconds": 960, "durationMilliseconds": 240},
                    {"text": "afternoon.", "offsetMilliseconds": 1200, "durationMilliseconds": 400},
                ],
            }
        ],
    }
    result = _parse_fast_transcription(data, locale="en-US")
    assert result["transcript"] == "Good afternoon. Welcome back."
    assert result["backend"] == "azure-speech"
    assert result["words"][0] == {
        "word": "Good",
        "punctuated_word": "Good",
        "start": 0.96,
        "end": 1.2,
        "confidence": 0.93,
    }
    assert result["words"][1]["end"] == 1.6
