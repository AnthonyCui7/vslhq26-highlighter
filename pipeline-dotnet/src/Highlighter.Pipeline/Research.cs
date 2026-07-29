using System.Text.Json.Nodes;

namespace Highlighter.Pipeline;

/// <summary>Port of highlighter_pipeline/research.py.
///
/// Content research layer: one research call per run.
///
/// The Azure OpenAI editor deployment takes the first attempt when configured;
/// otherwise (or on any failure) the call runs as a single web-grounded Claude
/// Sonnet 5 request through OpenRouter's web-search plugin. Either way the
/// result is the same structured, source-cited editorial context that feeds
/// the clip-selection model as researchContext (Llm threads it into the
/// scoring prompt).</summary>
public static class Research
{
    private const string CONTENT_RESEARCH_SCHEMA_JSON =
        """
        {
          "type": "object",
          "properties": {
            "creator_profile": {"type": "string"},
            "content_context": {"type": "string"},
            "target_audience": {"type": "array", "items": {"type": "string"}},
            "inside_references": {"type": "array", "items": {"type": "string"}},
            "recent_context": {"type": "array", "items": {"type": "string"}},
            "successful_clip_patterns": {"type": "array", "items": {"type": "string"}},
            "thumbnail_patterns": {"type": "array", "items": {"type": "string"}},
            "platform_notes": {"type": "array", "items": {"type": "string"}},
            "useful_hooks": {"type": "array", "items": {"type": "string"}},
            "avoid": {"type": "array", "items": {"type": "string"}},
            "sources": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "title": {"type": "string"},
                  "url": {"type": "string"},
                  "claim": {"type": "string"}
                },
                "required": ["title", "url", "claim"],
                "additionalProperties": false
              }
            }
          },
          "required": [
            "creator_profile",
            "content_context",
            "target_audience",
            "inside_references",
            "recent_context",
            "successful_clip_patterns",
            "thumbnail_patterns",
            "platform_notes",
            "useful_hooks",
            "avoid",
            "sources"
          ],
          "additionalProperties": false
        }
        """;

    public static JsonObject ContentResearchSchema() =>
        JsonUtil.ParseObject(CONTENT_RESEARCH_SCHEMA_JSON);

    public const string RESEARCH_SYSTEM_PROMPT =
        """
        You are a content researcher for an AI video editing
        pipeline. Given a source video (creator/channel, platform, live or VOD), use
        web search to build practical, current editorial context for the editor model
        that will select clips from it.

        Research and report:
        - Who the creator is and what their content/channel is about.
        - The target audience: who watches this, what they care about.
        - Inside references: recurring jokes, lore, memes, nicknames, callbacks, and
          running bits this creator's audience recognizes instantly.
        - Recent context: what has happened lately with this creator and in this niche
          (events, drama, releases, discourse) that a moment might reference.
        - The content genre and what is currently trending or discussed in it.
        - Clip formats and editing patterns that perform well for similar content.
        - Thumbnail and title patterns that work in this niche.
        - What to avoid (overdone formats, sensitive topics for this audience).

        Rules:
        - Keep every field compact and practical: the consumer is another LLM making
          clip decisions, not a human reading a report.
        - Every factual outside-world claim must be backed by an entry in sources with
          a real URL you found via search.
        - If you cannot find much about this specific creator, research the genre and
          platform patterns instead and say so in creator_profile.
        - Return ONLY one JSON object matching the provided schema. No markdown fences,
          no commentary before or after.
        """;

    public static string ResearchBackendLabel()
    {
        var provider = Providers.AzureEditorProvider();
        return provider is not null
            ? $"{provider.Label} (fallback: web-grounded {Defaults.DEFAULT_RESEARCH_MODEL})"
            : $"web-grounded {Defaults.DEFAULT_RESEARCH_MODEL}";
    }

    /// <summary>Run the research call and return an object matching
    /// CONTENT_RESEARCH_SCHEMA. Throws on failure; callers treat research as
    /// best-effort and continue without it.</summary>
    public static JsonObject ResearchContentContext(
        JsonObject sourceContext,
        string pipelineMode = "short",
        string? userInstructions = null,
        string model = Defaults.DEFAULT_RESEARCH_MODEL)
    {
        var prompt = ResearchPrompt(
            sourceContext: sourceContext,
            pipelineMode: pipelineMode,
            userInstructions: userInstructions);
        var provider = Providers.AzureEditorProvider();
        if (provider is not null)
        {
            try
            {
                return RequestAzureResearch(provider, prompt);
            }
            catch (Exception exc)
            {
                Console.WriteLine(
                    $"{provider.Label} research failed; falling back to "
                    + $"web-grounded {model}: {exc.Message}");
            }
        }
        return WebGroundedResearch(prompt, model);
    }

    private static string ResearchPrompt(
        JsonObject sourceContext, string pipelineMode, string? userInstructions)
    {
        var goal = pipelineMode == "short"
            ? "The editor is cutting short-form vertical clips (TikTok/Reels/Shorts/X) from this source."
            : "The editor is cutting one long-form edit (a YouTube-ready video) from this source.";
        var promptParts = new List<string>
        {
            "Source video:",
            JsonUtil.DumpsIndented(sourceContext),
            "",
            goal,
        };
        if (!string.IsNullOrEmpty(userInstructions))
        {
            promptParts.AddRange(new[]
            {
                "",
                "The user gave the editor these instructions (use them to focus your research):",
                userInstructions,
            });
        }
        promptParts.AddRange(new[]
        {
            "",
            "Return only a JSON object matching this schema:",
            JsonUtil.DumpsIndented(ContentResearchSchema()),
        });
        return string.Join("\n", promptParts);
    }

    private static JsonObject RequestAzureResearch(ChatProvider provider, string prompt)
    {
        var body = new JsonObject
        {
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = RESEARCH_SYSTEM_PROMPT },
                new JsonObject { ["role"] = "user", ["content"] = prompt },
            },
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = "content_research",
                    ["schema"] = ContentResearchSchema(),
                },
            },
        };
        provider.ApplyRequestOptions(body);

        JsonObject response;
        using (var client = provider.Client(timeoutSeconds: 300.0))
        {
            response = client.ChatCompletions(body);
        }
        var content = response["choices"] is JsonArray choices && choices.Count > 0
            ? JsonUtil.StrOrNull(choices[0]?["message"]?["content"])
            : null;
        if (string.IsNullOrEmpty(content))
            throw new PipelineError($"{provider.Label} research response did not include content");
        var context = JsonFromText(content) as JsonObject
            ?? throw new PipelineError($"{provider.Label} research response was not a JSON object");
        if (!context.ContainsKey("sources")) context["sources"] = new JsonArray();
        return context;
    }

    private static JsonObject WebGroundedResearch(string prompt, string model)
    {
        var apiKey = Config.Env("OPENROUTER_API_KEY");
        if (string.IsNullOrEmpty(apiKey))
            throw new PipelineError("OPENROUTER_API_KEY is required for content research");

        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = RESEARCH_SYSTEM_PROMPT },
                new JsonObject { ["role"] = "user", ["content"] = prompt },
            },
            ["temperature"] = 0.3,
            ["max_tokens"] = 8000,
            ["plugins"] = new JsonArray
            {
                new JsonObject { ["id"] = "web", ["max_results"] = 5 },
            },
        };

        JsonObject response;
        using (var client = new ChatCompletionsClient(
                   Providers.OPENROUTER_BASE_URL,
                   apiKey,
                   timeoutSeconds: 300.0,
                   headers: new Dictionary<string, string>
                   {
                       ["HTTP-Referer"] = "http://localhost",
                       ["X-OpenRouter-Title"] = "highlighter research",
                   }))
        {
            response = client.ChatCompletions(body);
        }

        if (response["choices"] is not JsonArray choices || choices.Count == 0)
            throw new PipelineError("Research response did not include choices");
        var content = JsonUtil.StrOrNull(choices[0]?["message"]?["content"]);
        if (string.IsNullOrEmpty(content))
            throw new PipelineError("Research response did not include text content");

        var context = JsonFromText(content) as JsonObject
            ?? throw new PipelineError("Research response was not a JSON object");
        if (!context.ContainsKey("sources")) context["sources"] = new JsonArray();
        return context;
    }

    private static JsonNode? JsonFromText(string text)
    {
        var stripped = text.Trim();
        if (stripped.StartsWith("```"))
        {
            stripped = stripped.Trim('`').Trim();
            if (stripped.StartsWith("json")) stripped = stripped[4..].Trim();
        }
        // Web-grounded responses sometimes lead with prose despite instructions;
        // fall back to the outermost JSON object.
        try
        {
            return JsonUtil.Parse(stripped);
        }
        catch (System.Text.Json.JsonException)
        {
            var start = stripped.IndexOf('{');
            var end = stripped.LastIndexOf('}');
            if (start == -1 || end <= start) throw;
            return JsonUtil.Parse(stripped[start..(end + 1)]);
        }
    }
}
