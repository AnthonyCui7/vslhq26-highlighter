using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Highlighter.Pipeline;

/// <summary>Port of highlighter_pipeline/agents.py.
///
/// Microsoft Agent Framework seam for the pipeline's model calls. Each
/// provider wraps into a framework chat client: the framework runs the agents
/// (instructions, sessions, the function-invocation loop) while
/// PipelineChatClient keeps the exact request bodies the pipeline has always
/// sent — the provider's request kwargs, raw JSON-schema response formats,
/// and provider-specific assistant-message fields that must replay verbatim
/// across turns (the raw message object is echoed as-is, so fields like
/// reasoning thought signatures survive the round trip untouched).</summary>
public static class Agents
{
    // Tool results are plain text on the wire; a result carrying this key also
    // queues audio parts for a follow-up user message (audio cannot ride in a
    // tool message, so it arrives in the next message — same layout as ever).
    public const string AUDIO_PARTS_KEY = "_audio_parts";

    public static ChatMessage TextMessage(ChatRole role, string text) =>
        new(role, text);

    /// <summary>A message whose wire content is exactly these chat.completions parts.</summary>
    public static ChatMessage RawPartsMessage(ChatRole role, JsonArray parts)
    {
        var message = new ChatMessage(role, contents: new List<AIContent>());
        message.AdditionalProperties ??= new AdditionalPropertiesDictionary();
        message.AdditionalProperties["wire_parts"] = parts;
        return message;
    }

    /// <summary>A listen-style tool result: the text the model reads in the tool
    /// message, plus audio parts delivered in the following user message.</summary>
    public static JsonObject ToolResultWithAudio(string text, JsonArray mp3Parts) =>
        new() { ["text"] = text, [AUDIO_PARTS_KEY] = mp3Parts };

    /// <summary>Framework conversation -> the exact chat.completions message list.
    ///
    /// Assistant messages replay their raw provider object verbatim; function
    /// results become tool messages; audio queued by tool results lands in one
    /// user message after that batch of tool messages, never inside them.</summary>
    public static JsonArray WireMessages(IEnumerable<ChatMessage> messages)
    {
        var wire = new JsonArray();
        var pendingAudio = new JsonArray();

        void FlushAudio()
        {
            if (pendingAudio.Count == 0) return;
            var parts = new JsonArray();
            foreach (var part in pendingAudio.ToList())
            {
                pendingAudio.Remove(part);
                parts.Add(part);
            }
            wire.Add(new JsonObject { ["role"] = "user", ["content"] = parts });
        }

        foreach (var message in messages)
        {
            var results = message.Contents.OfType<FunctionResultContent>().ToList();
            if (results.Count > 0)
            {
                foreach (var content in results)
                {
                    var (text, audioParts) = UnwrapToolResult(content.Result);
                    foreach (var part in audioParts) pendingAudio.Add(JsonUtil.C(part));
                    wire.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = content.CallId,
                        ["content"] = text,
                    });
                }
                continue;
            }
            FlushAudio();
            if (message.Role == ChatRole.Assistant && message.RawRepresentation is JsonObject raw)
            {
                wire.Add(JsonUtil.CloneObj(raw));
                continue;
            }
            if (message.AdditionalProperties?.TryGetValue("wire_parts", out var wireParts) == true
                && wireParts is JsonArray partsArray)
            {
                wire.Add(new JsonObject
                {
                    ["role"] = RoleName(message.Role),
                    ["content"] = (JsonArray)JsonUtil.C(partsArray)!,
                });
                continue;
            }
            wire.Add(new JsonObject
            {
                ["role"] = RoleName(message.Role),
                ["content"] = message.Text,
            });
        }
        FlushAudio();
        return wire;
    }

    /// <summary>AIFunction declarations -> chat.completions tool declarations.</summary>
    public static JsonArray? WireTools(IEnumerable<AITool>? tools)
    {
        if (tools is null) return null;
        var declarations = new JsonArray();
        foreach (var tool in tools.OfType<AIFunction>())
        {
            declarations.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description ?? "",
                    ["parameters"] = JsonNode.Parse(tool.JsonSchema.GetRawText()),
                },
            });
        }
        return declarations.Count > 0 ? declarations : null;
    }

    /// <summary>One single-shot agent call: build an agent over the provider,
    /// run the prompt, return the response text.</summary>
    public static string RunAgentText(
        ChatProvider provider,
        string instructions,
        string prompt,
        double timeoutSeconds = 300.0,
        JsonObject? extraOptions = null)
    {
        using var client = new PipelineChatClient(provider, timeoutSeconds, extraOptions);
        var agent = new ChatClientAgent(client, instructions: instructions);
        var response = agent.RunAsync(prompt).GetAwaiter().GetResult();
        var text = response.Text;
        if (string.IsNullOrEmpty(text))
            throw new PipelineError($"{provider.Label} response did not include text content");
        return text;
    }

    internal static string RoleName(ChatRole role) => role.Value.ToLowerInvariant();

    /// <summary>The invocation layer stores tool results as objects or JSON
    /// text; an audio-carrying result is unwrapped back into text + parts.</summary>
    internal static (string Text, JsonArray AudioParts) UnwrapToolResult(object? result)
    {
        JsonObject? carrier = null;
        if (result is JsonObject direct && direct.ContainsKey(AUDIO_PARTS_KEY))
        {
            carrier = direct;
        }
        else if (result is string text0 && text0.Contains(AUDIO_PARTS_KEY))
        {
            try
            {
                carrier = JsonNode.Parse(text0) as JsonObject;
            }
            catch (JsonException)
            {
                carrier = null;
            }
            if (carrier is not null && !carrier.ContainsKey(AUDIO_PARTS_KEY)) carrier = null;
        }
        else if (result is JsonElement element && element.ValueKind == JsonValueKind.String)
        {
            return UnwrapToolResult(element.GetString());
        }
        else if (result is JsonElement objElement && objElement.ValueKind == JsonValueKind.Object)
        {
            var parsed = JsonNode.Parse(objElement.GetRawText()) as JsonObject;
            if (parsed is not null && parsed.ContainsKey(AUDIO_PARTS_KEY)) carrier = parsed;
            else return (objElement.GetRawText(), new JsonArray());
        }

        if (carrier is not null)
        {
            var parts = carrier[AUDIO_PARTS_KEY] as JsonArray ?? new JsonArray();
            return (JsonUtil.Str(carrier["text"]), (JsonArray)JsonUtil.C(parts)!);
        }
        var plain = result switch
        {
            null => "",
            string text => text,
            JsonElement el when el.ValueKind == JsonValueKind.String => el.GetString() ?? "",
            JsonNode node => node.ToJsonString(),
            _ => result.ToString() ?? "",
        };
        return (plain, new JsonArray());
    }
}

/// <summary>An AIFunction whose declaration is a hand-written JSON schema —
/// the pipeline's tool schemas are the single source of truth, not signatures.</summary>
public sealed class RawSchemaFunction : AIFunction
{
    private readonly string _name;
    private readonly string _description;
    private readonly JsonElement _schema;
    private readonly Func<JsonObject, object?> _invoke;

    public RawSchemaFunction(
        string name, string description, JsonObject schema, Func<JsonObject, object?> invoke)
    {
        _name = name;
        _description = description;
        _schema = JsonDocument.Parse(schema.ToJsonString()).RootElement;
        _invoke = invoke;
    }

    public override string Name => _name;

    public override string Description => _description;

    public override JsonElement JsonSchema => _schema;

    protected override ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var args = new JsonObject();
        foreach (var pair in arguments)
        {
            args[pair.Key] = pair.Value switch
            {
                null => null,
                JsonElement element => JsonNode.Parse(element.GetRawText()),
                JsonNode node => JsonUtil.C(node),
                string text => JsonValue.Create(text),
                bool flag => JsonValue.Create(flag),
                int number => JsonValue.Create(number),
                long number => JsonValue.Create(number),
                double number => JsonValue.Create(number),
                _ => JsonValue.Create(pair.Value.ToString()),
            };
        }
        return ValueTask.FromResult(_invoke(args));
    }
}

/// <summary>A framework chat client over one provider from the pipeline's
/// chains. State the callers drive directly: ExtraOptions (raw request fields
/// merged into every body), ForcedToolChoice (a tool_choice value, only sent
/// when the request declares tools), ShortCircuitText (the next request
/// returns this text without calling the provider — how a finished tool loop
/// ends without spending another model call), and Requests (provider calls
/// made so far).</summary>
public class PipelineChatClient : IChatClient
{
    private readonly ChatProvider _provider;
    private readonly double _timeoutSeconds;
    private ChatCompletionsClient? _sdkClient;

    public PipelineChatClient(
        ChatProvider provider, double timeoutSeconds = 300.0, JsonObject? extraOptions = null)
    {
        _provider = provider;
        _timeoutSeconds = timeoutSeconds;
        ExtraOptions = extraOptions ?? new JsonObject();
    }

    public JsonObject ExtraOptions { get; }

    public JsonObject? ForcedToolChoice { get; set; }

    public string? ShortCircuitText { get; set; }

    public int Requests { get; private set; }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (ShortCircuitText is not null)
        {
            return Task.FromResult(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, ShortCircuitText)));
        }

        var wire = Agents.WireMessages(messages);
        if (!string.IsNullOrEmpty(options?.Instructions))
        {
            wire.Insert(0, new JsonObject
            {
                ["role"] = "system",
                ["content"] = options.Instructions,
            });
        }
        var body = new JsonObject { ["messages"] = wire };
        var tools = Agents.WireTools(options?.Tools);
        if (tools is not null)
        {
            body["tools"] = tools;
            body["tool_choice"] = ForcedToolChoice is not null
                ? JsonUtil.CloneObj(ForcedToolChoice)
                : "auto";
        }
        foreach (var pair in ExtraOptions) body[pair.Key] = pair.Value?.DeepClone();
        _provider.ApplyRequestOptions(body);

        Requests += 1;
        var sdkMessage = Send(body);
        return Task.FromResult(
            new ChatResponse(ToMessage(sdkMessage)) { RawRepresentation = sdkMessage });
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The pipeline's model calls do not stream");

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }

    /// <summary>One chat.completions call; returns the provider's message object.</summary>
    public virtual JsonObject Send(JsonObject body)
    {
        _sdkClient ??= _provider.Client(_timeoutSeconds);
        var response = _sdkClient.ChatCompletions(body);
        if (response["choices"] is not JsonArray choices || choices.Count == 0)
            throw new PipelineError($"{_provider.Label} response did not include choices");
        return choices[0]?["message"] as JsonObject
            ?? throw new PipelineError($"{_provider.Label} response did not include a message");
    }

    /// <summary>Provider message -> framework message. The raw object rides
    /// along and is what gets replayed on later turns.</summary>
    internal static ChatMessage ToMessage(JsonObject sdkMessage)
    {
        var contents = new List<AIContent>();
        var text = JsonUtil.StrOrNull(sdkMessage["content"]);
        if (!string.IsNullOrEmpty(text)) contents.Add(new TextContent(text));
        foreach (var node in sdkMessage["tool_calls"] as JsonArray ?? new JsonArray())
        {
            if (node is not JsonObject call) continue;
            var function = call["function"] as JsonObject;
            var argumentsJson = JsonUtil.StrOrNull(function?["arguments"]);
            var arguments = string.IsNullOrEmpty(argumentsJson)
                ? new Dictionary<string, object?>()
                : JsonSerializer.Deserialize<Dictionary<string, object?>>(argumentsJson)
                    ?? new Dictionary<string, object?>();
            contents.Add(new FunctionCallContent(
                JsonUtil.Str(call["id"]), JsonUtil.Str(function?["name"]), arguments));
        }
        return new ChatMessage(ChatRole.Assistant, contents) { RawRepresentation = sdkMessage };
    }
}
