using System.Text.Json.Nodes;
using Highlighter.Pipeline;
using Highlighter.Web.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Highlighter.Web.Services;

/// <summary>The editing agent in the studio's right panel: a Microsoft Agent
/// Framework agent running inside the Blazor app, whose tools are the
/// IStudioBackend surface. Capabilities differ by context — the long-form cut
/// gets the revision loop and thumbnails; a highlight clip gets research,
/// reformatting, and honest explanations of what it cannot do.</summary>
public class StudioAgentService(StudioState state, IStudioBackend backend)
{
    private ChatClientAgent? _agent;
    private AgentSession? _session;
    private string? _sessionContext;
    private int _providerIndex;

    public async Task SendAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || state.AgentBusy) return;
        state.AddAgentMessage("user", text.Trim());
        state.SetAgentBusy(true);
        try
        {
            var providers = Providers.EditorProviders(title: "highlighter studio");
            while (true)
            {
                try
                {
                    EnsureSession();
                    var response = await _agent!.RunAsync(text.Trim(), _session);
                    state.AddAgentMessage("agent",
                        string.IsNullOrWhiteSpace(response.Text)
                            ? "Done — anything else on this cut?"
                            : response.Text.Trim());
                    break;
                }
                catch (Exception exc) when (_providerIndex + 1 < providers.Count)
                {
                    // Conversations do not survive a provider swap; the chat
                    // list the human sees does.
                    Console.WriteLine($"Studio agent provider failed; trying the next: {exc.Message}");
                    _providerIndex += 1;
                    _agent = null;
                    _session = null;
                }
            }
        }
        catch (Exception exc)
        {
            state.AddAgentMessage("agent",
                $"The editing agent is unavailable right now: {exc.Message}");
        }
        finally
        {
            state.SetAgentBusy(false);
        }
    }

    /// <summary>The opening context card for a fresh conversation (no model
    /// call — it summarizes what the agent is looking at).</summary>
    public void SeedContext()
    {
        if (_sessionContext == ContextKey()) return;
        state.AgentMessages.Clear();
        state.AddAgentMessage("agent", state.AgentContext == "long"
            ? $"Working on the long-form cut \"{SampleData.LongformTitle}\" (v2, 24:36). "
              + "I can revise the cut, rerun research, and generate or select thumbnails."
            : $"Working on \"{state.ActiveClipTitle}\". I can rerun research, re-render this "
              + "clip in another format, and answer questions about the project.");
        ResetAgent();
    }

    private string ContextKey() => $"{state.AgentContext}:{state.ActiveClip}";

    private void ResetAgent()
    {
        _agent = null;
        _session = null;
        _sessionContext = ContextKey();
    }

    private void EnsureSession()
    {
        if (_agent is not null && _sessionContext == ContextKey()) return;
        _sessionContext = ContextKey();
        var providers = Providers.EditorProviders(title: "highlighter studio");
        var provider = providers[Math.Min(_providerIndex, providers.Count - 1)];
        var client = new PipelineChatClient(provider, timeoutSeconds: 120.0);
        _agent = new ChatClientAgent(client, Instructions(), tools: Tools());
        _session = _agent.CreateSessionAsync().GetAwaiter().GetResult();
    }

    private string Instructions()
    {
        var shared =
            "You are the editing agent inside the Highlighter studio, working beside a human "
            + "editor on one project. Use your tools for anything they ask about the project; "
            + "answer in one or two plain sentences and report what actually happened. You "
            + "cannot create new projects — point them at the New project button (top left of "
            + "the Projects page). You cannot publish — that is the Publish button in the "
            + "editor. When something is unavailable, say so plainly and offer the closest "
            + "alternative.";
        return state.AgentContext == "long"
            ? shared + $" Context: the long-form cut \"{SampleData.LongformTitle}\" (v2). You "
              + "can revise the cut with revise_longform (a re-cut keeps the thumbnail and "
              + "title), refresh research, and generate, select, or list thumbnails."
            : shared + $" Context: the highlight clip \"{state.ActiveClipTitle}\". Revisions "
              + "apply only to the long-form cut, not clips — if asked to revise, explain "
              + "that and point at the long-form editor or this panel's manual format and "
              + "caption options. You can refresh research and re-render this clip in another "
              + "format with reformat_clip.";
    }

    private List<AITool> Tools()
    {
        var tools = new List<AITool>
        {
            Tool("get_project_status", "The project's status, outputs, and long-form title.",
                NoArgs(), _ => backend.GetProjectStatusAsync().GetAwaiter().GetResult()),
            Tool("list_clips", "Every highlight clip with duration, score, and caption state.",
                NoArgs(), _ => backend.ListClipsAsync().GetAwaiter().GetResult()),
            Tool("get_longform_versions", "The long-form versions and their thumbnails.",
                NoArgs(), _ => backend.GetLongformVersionsAsync().GetAwaiter().GetResult()),
            Tool("rerun_research", "Refresh the web-grounded content research with a focus.",
                Args(("focus", "string", "What the research should dig into.")),
                args => backend.RerunResearchAsync(Str(args, "focus")).GetAwaiter().GetResult()),
            Tool("get_job_status", "Check a started job by its id.",
                Args(("job_id", "string", "The job id a tool returned.")),
                args => backend.GetJobStatusAsync(Str(args, "job_id")).GetAwaiter().GetResult()),
        };
        if (state.AgentContext == "long")
        {
            tools.Add(Tool("revise_longform",
                "Revise the long-form cut from a natural-language request; renders the next version.",
                Args(("request", "string", "What to change in the cut.")),
                args => backend.ReviseLongformAsync(Str(args, "request")).GetAwaiter().GetResult()));
            tools.Add(Tool("generate_thumbnails",
                "Generate another thumbnail concept, optionally steered by a prompt.",
                Args(("prompt", "string", "Optional creative direction.")),
                args => backend.GenerateThumbnailsAsync(Str(args, "prompt")).GetAwaiter().GetResult()));
            tools.Add(Tool("select_thumbnail", "Make a generated variant the video's thumbnail.",
                Args(("index", "number", "The variant number to select.")),
                args => backend.SelectThumbnailAsync(
                    (int)(args["index"]?.GetValue<double>() ?? 0)).GetAwaiter().GetResult()));
        }
        else
        {
            tools.Add(Tool("reformat_clip",
                "Re-render this clip in another delivery format.",
                Args(
                    ("format", "string", "square for a 1:1 center crop."),
                    ("captions", "boolean", "Whether to burn captions into the new render.")),
                args => backend.ReformatClipAsync(
                    Str(args, "format"),
                    args["captions"]?.GetValue<bool>() ?? false).GetAwaiter().GetResult()));
        }
        return tools;
    }

    private static AITool Tool(
        string name, string description, JsonObject schema, Func<JsonObject, object?> invoke) =>
        new RawSchemaFunction(name, description, schema, invoke);

    private static JsonObject NoArgs() =>
        new() { ["type"] = "object", ["properties"] = new JsonObject() };

    private static JsonObject Args(params (string Name, string Type, string Description)[] args)
    {
        var properties = new JsonObject();
        foreach (var (name, type, description) in args)
        {
            properties[name] = new JsonObject
            {
                ["type"] = type,
                ["description"] = description,
            };
        }
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray(args.Select(a => (JsonNode?)a.Name).ToArray()),
        };
    }

    private static string Str(JsonObject args, string key) =>
        args[key]?.GetValue<string>() ?? "";
}
