import asyncio
import json
from types import SimpleNamespace

from agent_framework import Agent, AgentSession, Content, FunctionTool, Message

from highlighter_pipeline.agents import (
    PipelineChatClient,
    raw_parts_message,
    text_message,
    tool_result_with_audio,
    wire_messages,
    wire_tools,
)
from highlighter_pipeline.providers import Provider


def provider(**overrides):
    values = dict(
        name="azure",
        label="Test provider",
        base_url="http://example",
        api_key="key",
        model="test-model",
        temperature=None,
        extra_body={"reasoning_effort": "high"},
    )
    values.update(overrides)
    return Provider(**values)


def sdk_message(content=None, tool_calls=None, extra=None):
    message = SimpleNamespace(role="assistant", content=content, tool_calls=tool_calls)
    for key, value in (extra or {}).items():
        setattr(message, key, value)
    return message


def tool_call(call_id, name, args):
    return SimpleNamespace(
        id=call_id, function=SimpleNamespace(name=name, arguments=json.dumps(args))
    )


class ScriptedClient(PipelineChatClient):
    """PipelineChatClient with a canned transport; records every request."""

    def __init__(self, *args, script=None, **kwargs):
        super().__init__(*args, **kwargs)
        self.__dict__["script"] = list(script or [])
        self.__dict__["captured"] = []

    def _send(self, request):
        self.__dict__["captured"].append(request)
        return self.__dict__["script"].pop(0)


class TestWireMessages:
    def test_text_messages_use_bare_strings(self):
        wire = wire_messages([text_message("user", "hello")])
        assert wire == [{"role": "user", "content": "hello"}]

    def test_assistant_raw_representation_replays_verbatim(self):
        raw = sdk_message(content="x", extra={"reasoning_details": [{"sig": "S"}]})
        message = Message("assistant", [Content.from_text("x")], raw_representation=raw)
        wire = wire_messages([message])
        assert wire == [raw]

    def test_raw_parts_message_passes_through(self):
        parts = [{"type": "input_audio", "input_audio": {"data": "QUJD", "format": "mp3"}}]
        wire = wire_messages([raw_parts_message("user", parts)])
        assert wire == [{"role": "user", "content": parts}]

    def test_tool_results_become_tool_messages_and_audio_follows(self):
        result = tool_result_with_audio(
            "Audio attached in the next message.",
            [{"type": "input_audio", "input_audio": {"data": "QUJD", "format": "mp3"}}],
        )
        # The invocation layer stores results as JSON text.
        content = Content.from_function_result("call-1", result=json.dumps(result))
        wire = wire_messages([Message("tool", [content])])
        assert wire[0] == {
            "role": "tool",
            "tool_call_id": "call-1",
            "content": "Audio attached in the next message.",
        }
        assert wire[1]["role"] == "user"
        assert wire[1]["content"][0]["type"] == "input_audio"

    def test_plain_tool_result_has_no_audio_message(self):
        content = Content.from_function_result("call-2", result="transcript text")
        wire = wire_messages([Message("tool", [content])])
        assert wire == [
            {"role": "tool", "tool_call_id": "call-2", "content": "transcript text"}
        ]


class TestWireTools:
    def test_function_tools_serialize_with_their_schemas(self):
        schema = {
            "type": "object",
            "properties": {"x": {"type": "number"}},
            "required": ["x"],
        }
        tool = FunctionTool(name="demo", description="Demo.", func=lambda x: x, input_model=schema)
        assert wire_tools([tool]) == [
            {
                "type": "function",
                "function": {"name": "demo", "description": "Demo.", "parameters": schema},
            }
        ]

    def test_empty_is_none(self):
        assert wire_tools([]) is None


class TestAgentLoop:
    def test_single_shot_body_shape(self):
        client = ScriptedClient(
            provider(),
            script=[sdk_message(content='{"ok": true}')],
            extra_options={"response_format": {"type": "json_schema"}},
        )
        response = asyncio.run(Agent(client, "System words.").run("user words"))
        assert response.text == '{"ok": true}'
        request = client.__dict__["captured"][0]
        assert request["messages"][0] == {"role": "system", "content": "System words."}
        assert request["messages"][1] == {"role": "user", "content": "user words"}
        assert request["response_format"] == {"type": "json_schema"}
        assert request["model"] == "test-model"
        assert request["extra_body"] == {"reasoning_effort": "high"}
        assert "temperature" not in request

    def test_tool_loop_replays_raw_and_short_circuits(self):
        recorded = {}

        def apply_edit(selections: list, notes: str = ""):
            recorded["edit"] = {"selections": selections, "notes": notes}
            client.short_circuit_text = "Edit accepted."
            return "Edit accepted."

        tools = [
            FunctionTool(
                name="apply_edit",
                description="Commit.",
                func=apply_edit,
                input_model={
                    "type": "object",
                    "properties": {"selections": {"type": "array"}, "notes": {"type": "string"}},
                    "required": ["selections"],
                },
            )
        ]
        raw_extra = {"reasoning_details": [{"sig": "XYZ"}]}
        client = ScriptedClient(
            provider(),
            script=[
                sdk_message(
                    tool_calls=[tool_call("c1", "apply_edit", {"selections": [1], "notes": "n"})],
                    extra=raw_extra,
                )
            ],
        )
        response = asyncio.run(
            Agent(client, "Loop.").run("go", session=AgentSession(), tools=tools)
        )
        assert recorded["edit"] == {"selections": [1], "notes": "n"}
        assert response.text == "Edit accepted."
        # Only one real provider call: the follow-up was short-circuited.
        assert client.requests == 1

    def test_forced_tool_choice_rides_in_the_body(self):
        client = ScriptedClient(provider(), script=[sdk_message(content="done")])
        client.forced_tool_choice = {"type": "function", "function": {"name": "apply_edit"}}
        tools = [
            FunctionTool(
                name="apply_edit",
                description="Commit.",
                func=lambda selections=None, notes="": "Edit accepted.",
                input_model={"type": "object", "properties": {}},
            )
        ]
        asyncio.run(Agent(client, "Force.").run("go", tools=tools))
        request = client.__dict__["captured"][0]
        assert request["tool_choice"] == {
            "type": "function",
            "function": {"name": "apply_edit"},
        }
