"""Microsoft Agent Framework seam for the pipeline's model calls.

Each provider in providers.py wraps into a framework chat client: the
framework runs the agents (instructions, sessions, the function-invocation
loop) while PipelineChatClient keeps the exact request bodies the pipeline
has always sent — the provider's request kwargs, raw JSON-schema response
formats, and provider-specific assistant-message fields that must replay
verbatim across turns (the raw message object is echoed as-is, so fields
like reasoning thought signatures survive the round trip untouched).
"""

import base64
import json
from typing import Any, Mapping, Sequence

from agent_framework import (
    Agent,
    AgentSession,
    BaseChatClient,
    ChatResponse,
    Content,
    FunctionInvocationLayer,
    FunctionTool,
    Message,
)

from .providers import Provider

AUDIO_MEDIA_TYPE = "audio/mpeg"

# Tool results are plain text on the wire; a result carrying this key also
# queues audio parts for a follow-up user message (audio cannot ride in a
# tool message, so it arrives in the next message — same layout as ever).
AUDIO_PARTS_KEY = "_audio_parts"


def text_message(role: str, text: str) -> Message:
    return Message(role, [Content.from_text(text)])


def audio_user_message(*, text: str, mp3_bytes: bytes) -> Message:
    """A user message carrying narration text plus one mp3 attachment."""
    return Message(
        "user",
        [Content.from_text(text), Content.from_data(data=mp3_bytes, media_type=AUDIO_MEDIA_TYPE)],
    )


def tool_result_with_audio(text: str, mp3_parts: list[dict[str, Any]]) -> dict[str, Any]:
    """A listen-style tool result: the text the model reads in the tool
    message, plus audio parts delivered in the following user message."""
    return {"text": text, AUDIO_PARTS_KEY: mp3_parts}


def raw_parts_message(role: str, parts: list[dict[str, Any]]) -> Message:
    """A message whose wire content is exactly these chat.completions parts."""
    return Message(role, None, additional_properties={"wire_parts": list(parts)})


def wire_content(contents: Sequence[Content]) -> Any:
    """Framework message contents -> a chat.completions content value: a bare
    string for text-only messages, a parts list when audio rides along."""
    parts: list[dict[str, Any]] = []
    for content in contents:
        if content.type == "text":
            parts.append({"type": "text", "text": content.text or ""})
        elif content.type == "data":
            data = content.data if isinstance(getattr(content, "data", None), bytes) else b""
            parts.append(
                {
                    "type": "input_audio",
                    "input_audio": {
                        "data": base64.b64encode(data).decode("ascii"),
                        "format": "mp3",
                    },
                }
            )
    if len(parts) == 1 and parts[0].get("type") == "text":
        return parts[0]["text"]
    return parts


def wire_messages(messages: Sequence[Message]) -> list[Any]:
    """Framework conversation -> the exact chat.completions message list.

    Assistant messages replay their raw provider object verbatim; function
    results become tool messages; audio queued by tool results lands in one
    user message after that batch of tool messages, never inside them."""
    wire: list[Any] = []
    pending_audio: list[dict[str, Any]] = []

    def flush_audio() -> None:
        if pending_audio:
            wire.append({"role": "user", "content": list(pending_audio)})
            pending_audio.clear()

    for message in messages:
        role = str(message.role)
        results = [c for c in message.contents if c.type == "function_result"]
        if results:
            for content in results:
                result = content.result
                # The invocation layer stores tool results as JSON text; an
                # audio-carrying result is unwrapped back into text + parts.
                if isinstance(result, str) and AUDIO_PARTS_KEY in result:
                    try:
                        parsed = json.loads(result)
                    except json.JSONDecodeError:
                        parsed = None
                    if isinstance(parsed, Mapping) and AUDIO_PARTS_KEY in parsed:
                        result = parsed
                if isinstance(result, Mapping) and AUDIO_PARTS_KEY in result:
                    text = str(result.get("text") or "")
                    pending_audio.extend(result[AUDIO_PARTS_KEY])
                else:
                    text = result if isinstance(result, str) else str(result)
                wire.append(
                    {"role": "tool", "tool_call_id": content.call_id, "content": text}
                )
            continue
        flush_audio()
        if role == "assistant" and message.raw_representation is not None:
            wire.append(message.raw_representation)
            continue
        wire_parts = (message.additional_properties or {}).get("wire_parts")
        if wire_parts:
            wire.append({"role": role, "content": list(wire_parts)})
            continue
        wire.append({"role": role, "content": wire_content(message.contents)})
    flush_audio()
    return wire


def wire_tools(tools: Sequence[Any]) -> list[dict[str, Any]] | None:
    """FunctionTool definitions -> chat.completions tool declarations."""
    declarations = [
        {
            "type": "function",
            "function": {
                "name": tool.name,
                "description": tool.description or "",
                "parameters": tool.parameters(),
            },
        }
        for tool in tools
        if isinstance(tool, FunctionTool)
    ]
    return declarations or None


class PipelineChatClient(FunctionInvocationLayer, BaseChatClient):
    """A framework chat client over one provider from the pipeline's chains.

    State the callers drive directly:
    - extra_options: raw request fields merged into every body (for example a
      json_schema response_format).
    - forced_tool_choice: a tool_choice value for the next requests, or None
      for "auto" (only sent when the request declares tools).
    - short_circuit_text: when set, the next request returns this text without
      calling the provider — how a finished tool loop ends without spending
      another model call.
    - requests: how many provider calls this client has made.
    """

    def __init__(
        self,
        provider: Provider,
        *,
        timeout: float = 300.0,
        extra_options: dict[str, Any] | None = None,
    ) -> None:
        super().__init__()
        self.provider = provider
        self.timeout = timeout
        self.extra_options = dict(extra_options or {})
        self.forced_tool_choice: dict[str, Any] | None = None
        self.short_circuit_text: str | None = None
        self.requests = 0
        self._sdk_client: Any = None

    async def _inner_get_response(  # type: ignore[override]
        self, *, messages: Sequence[Message], stream: bool, options: Mapping[str, Any], **kwargs: Any
    ) -> ChatResponse:
        if stream:
            raise RuntimeError("The pipeline's model calls do not stream")
        if self.short_circuit_text is not None:
            return ChatResponse(
                messages=[text_message("assistant", self.short_circuit_text)]
            )

        wire = wire_messages(messages)
        instructions = (options or {}).get("instructions")
        if instructions:
            wire.insert(0, {"role": "system", "content": str(instructions)})
        request: dict[str, Any] = {"messages": wire}
        tools = wire_tools(list((options or {}).get("tools") or []))
        if tools:
            request["tools"] = tools
            request["tool_choice"] = self.forced_tool_choice or "auto"
        request.update(self.extra_options)
        request.update(self.provider.request_kwargs())

        self.requests += 1
        sdk_message = self._send(request)
        return ChatResponse(
            messages=[self._to_message(sdk_message)], raw_representation=sdk_message
        )

    def _send(self, request: dict[str, Any]) -> Any:
        """One chat.completions call; returns the provider's message object."""
        if self._sdk_client is None:
            self._sdk_client = self.provider.client(timeout=self.timeout)
        response = self._sdk_client.chat.completions.create(**request)
        if not response.choices:
            raise RuntimeError(f"{self.provider.label} response did not include choices")
        return response.choices[0].message

    @staticmethod
    def _to_message(sdk_message: Any) -> Message:
        """Provider message -> framework message. The raw object rides along
        and is what gets replayed on later turns."""
        contents: list[Content] = []
        text = getattr(sdk_message, "content", None)
        if text:
            contents.append(Content.from_text(text))
        for call in getattr(sdk_message, "tool_calls", None) or []:
            contents.append(
                Content.from_function_call(
                    call.id,
                    call.function.name,
                    arguments=call.function.arguments or "{}",
                )
            )
        return Message("assistant", contents, raw_representation=sdk_message)


def run_agent_text(
    *,
    provider: Provider,
    instructions: str,
    prompt: str,
    timeout: float = 300.0,
    extra_options: dict[str, Any] | None = None,
) -> str:
    """One single-shot agent call: build an agent over the provider, run the
    prompt, return the response text."""
    import asyncio

    client = PipelineChatClient(provider, timeout=timeout, extra_options=extra_options)
    agent = Agent(client, instructions)
    response = asyncio.run(agent.run(prompt))
    text = response.text
    if not text:
        raise RuntimeError(f"{provider.label} response did not include text content")
    return text
