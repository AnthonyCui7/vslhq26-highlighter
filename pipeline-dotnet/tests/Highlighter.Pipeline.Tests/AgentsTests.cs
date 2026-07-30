using System.Text.Json.Nodes;
using Highlighter.Pipeline;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace Highlighter.Pipeline.Tests;

public class ScriptedClient : PipelineChatClient
{
    private readonly Queue<JsonObject> _script;

    public ScriptedClient(ChatProvider provider, IEnumerable<JsonObject> script,
        JsonObject? extraOptions = null)
        : base(provider, 300.0, extraOptions)
    {
        _script = new Queue<JsonObject>(script);
    }

    public List<JsonObject> Captured { get; } = new();

    public override JsonObject Send(JsonObject body)
    {
        Captured.Add((JsonObject)body.DeepClone());
        return _script.Dequeue();
    }
}

public class AgentsTests
{
    private static ChatProvider Provider() => new(
        Name: "azure",
        Label: "Test provider",
        BaseUrl: "http://example",
        ApiKey: "key",
        Model: "test-model",
        Temperature: null,
        SupportsJsonSchema: true,
        ExtraBody: new JsonObject { ["reasoning_effort"] = "high" },
        DefaultHeaders: null);

    private static JsonObject SdkMessage(
        string? content = null, JsonArray? toolCalls = null, JsonObject? extra = null)
    {
        var message = new JsonObject { ["role"] = "assistant", ["content"] = content };
        if (toolCalls is not null) message["tool_calls"] = toolCalls;
        foreach (var pair in extra ?? new JsonObject()) message[pair.Key] = pair.Value?.DeepClone();
        return message;
    }

    private static JsonObject ToolCall(string id, string name, JsonObject args) => new()
    {
        ["id"] = id,
        ["function"] = new JsonObject
        {
            ["name"] = name,
            ["arguments"] = args.ToJsonString(),
        },
    };

    [Fact]
    public void TextMessagesUseBareStrings()
    {
        var wire = Agents.WireMessages(new[] { Agents.TextMessage(ChatRole.User, "hello") });
        Assert.Equal("""[{"role":"user","content":"hello"}]""", wire.ToJsonString());
    }

    [Fact]
    public void AssistantRawRepresentationReplaysVerbatim()
    {
        var raw = SdkMessage(content: "x",
            extra: new JsonObject { ["reasoning_details"] = new JsonArray(new JsonObject { ["sig"] = "S" }) });
        var message = new ChatMessage(ChatRole.Assistant, "x") { RawRepresentation = raw };
        var wire = Agents.WireMessages(new[] { message });
        Assert.Equal(raw.ToJsonString(), wire[0]!.ToJsonString());
    }

    [Fact]
    public void RawPartsMessagePassesThrough()
    {
        var parts = new JsonArray(new JsonObject
        {
            ["type"] = "input_audio",
            ["input_audio"] = new JsonObject { ["data"] = "QUJD", ["format"] = "mp3" },
        });
        var wire = Agents.WireMessages(new[] { Agents.RawPartsMessage(ChatRole.User, parts) });
        Assert.Equal("user", JsonUtil.Str(wire[0]!["role"]));
        Assert.Equal(parts.ToJsonString(), wire[0]!["content"]!.ToJsonString());
    }

    [Fact]
    public void ToolResultsBecomeToolMessagesAndAudioFollows()
    {
        var result = Agents.ToolResultWithAudio(
            "Audio attached in the next message.",
            new JsonArray(new JsonObject
            {
                ["type"] = "input_audio",
                ["input_audio"] = new JsonObject { ["data"] = "QUJD", ["format"] = "mp3" },
            }));
        // The invocation layer stores results as JSON text.
        var content = new FunctionResultContent("call-1", result.ToJsonString());
        var wire = Agents.WireMessages(new[]
        {
            new ChatMessage(ChatRole.Tool, new List<AIContent> { content }),
        });
        Assert.Equal("tool", JsonUtil.Str(wire[0]!["role"]));
        Assert.Equal("call-1", JsonUtil.Str(wire[0]!["tool_call_id"]));
        Assert.Equal("Audio attached in the next message.", JsonUtil.Str(wire[0]!["content"]));
        Assert.Equal("user", JsonUtil.Str(wire[1]!["role"]));
        Assert.Equal(
            "input_audio", JsonUtil.Str(((JsonArray)wire[1]!["content"]!)[0]!["type"]));
    }

    [Fact]
    public void PlainToolResultHasNoAudioMessage()
    {
        var content = new FunctionResultContent("call-2", "transcript text");
        var wire = Agents.WireMessages(new[]
        {
            new ChatMessage(ChatRole.Tool, new List<AIContent> { content }),
        });
        Assert.Single(wire);
        Assert.Equal("transcript text", JsonUtil.Str(wire[0]!["content"]));
    }

    [Fact]
    public void RawSchemaFunctionSerializesItsSchema()
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["x"] = new JsonObject { ["type"] = "number" } },
            ["required"] = new JsonArray("x"),
        };
        var tool = new RawSchemaFunction("demo", "Demo.", schema, _ => "ok");
        var wire = Agents.WireTools(new AITool[] { tool })!;
        var function = (JsonObject)wire[0]!["function"]!;
        Assert.Equal("demo", JsonUtil.Str(function["name"]));
        Assert.Equal(schema.ToJsonString(), function["parameters"]!.ToJsonString());
    }

    [Fact]
    public void SingleShotBodyShape()
    {
        var client = new ScriptedClient(
            Provider(),
            new[] { SdkMessage(content: """{"ok": true}""") },
            extraOptions: new JsonObject { ["response_format"] = new JsonObject { ["type"] = "json_schema" } });
        var agent = new ChatClientAgent(client, instructions: "System words.");
        var response = agent.RunAsync("user words").GetAwaiter().GetResult();
        Assert.Equal("""{"ok": true}""", response.Text);
        var request = client.Captured[0];
        var messages = (JsonArray)request["messages"]!;
        Assert.Equal("system", JsonUtil.Str(messages[0]!["role"]));
        Assert.Equal("System words.", JsonUtil.Str(messages[0]!["content"]));
        Assert.Equal("user words", JsonUtil.Str(messages[1]!["content"]));
        Assert.Equal("json_schema", JsonUtil.Str(request["response_format"]!["type"]));
        Assert.Equal("test-model", JsonUtil.Str(request["model"]));
        Assert.Equal("high", JsonUtil.Str(request["reasoning_effort"]));
        Assert.False(request.ContainsKey("temperature"));
    }

    [Fact]
    public void ToolLoopReplaysRawAndShortCircuits()
    {
        JsonObject? recorded = null;
        ScriptedClient? client = null;
        var tool = new RawSchemaFunction(
            "apply_edit",
            "Commit.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["selections"] = new JsonObject { ["type"] = "array" },
                    ["notes"] = new JsonObject { ["type"] = "string" },
                },
                ["required"] = new JsonArray("selections"),
            },
            args =>
            {
                recorded = args;
                client!.ShortCircuitText = "Edit accepted.";
                return "Edit accepted.";
            });
        var raw = new JsonObject { ["reasoning_details"] = new JsonArray(new JsonObject { ["sig"] = "XYZ" }) };
        client = new ScriptedClient(Provider(), new[]
        {
            SdkMessage(toolCalls: new JsonArray(ToolCall(
                "c1", "apply_edit",
                new JsonObject { ["selections"] = new JsonArray(1), ["notes"] = "n" })), extra: raw),
        });
        var agent = new ChatClientAgent(
            client, instructions: "Loop.", tools: new List<AITool> { tool });
        var session = agent.CreateSessionAsync().GetAwaiter().GetResult();
        var response = agent.RunAsync("go", session).GetAwaiter().GetResult();
        Assert.NotNull(recorded);
        Assert.Equal("n", JsonUtil.Str(recorded!["notes"]));
        Assert.Equal("Edit accepted.", response.Text);
        // Only one real provider call: the follow-up was short-circuited.
        Assert.Equal(1, client.Requests);
    }

    [Fact]
    public void ForcedToolChoiceRidesInTheBody()
    {
        var client = new ScriptedClient(Provider(), new[] { SdkMessage(content: "done") });
        client.ForcedToolChoice = new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject { ["name"] = "apply_edit" },
        };
        var tool = new RawSchemaFunction(
            "apply_edit", "Commit.", new JsonObject { ["type"] = "object" }, _ => "Edit accepted.");
        var agent = new ChatClientAgent(
            client, instructions: "Force.", tools: new List<AITool> { tool });
        agent.RunAsync("go").GetAwaiter().GetResult();
        var request = client.Captured[0];
        Assert.Equal("apply_edit", JsonUtil.Str(request["tool_choice"]!["function"]!["name"]));
    }
}
