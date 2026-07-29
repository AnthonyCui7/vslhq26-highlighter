using System.Text.Json.Nodes;

namespace Highlighter.Pipeline;

/// <summary>Port of highlighter_pipeline/revise.py.
///
/// Natural-language revision loop for a finished long-form edit.
///
/// `highlighter revise &lt;project_id&gt; "&lt;request&gt;"` reopens a project's local record
/// and puts the supervising editor (Gemini, tool-calling) in charge of producing
/// the next version. The agent starts with the full edit state — candidates, the
/// current cut, editor notes, prior revisions — and pulls everything else through
/// tools: the word-timestamped transcript, the actual audio, scene cuts, and the
/// research context. It can rerun pipeline stages (research, per-chunk pass-1
/// rescoring) before committing the new cut with apply_edit, whose selections are
/// free windows: split, tighten, extend, or add — always assembled in strict
/// source-chronological order.
///
/// Assembly reuses what exists: a selection matching a rendered candidate uses its
/// file, one inside a candidate window is trimmed from it, and anything else is
/// fetched from the source URL (long-form sources are VODs). Each version lands as
/// longform/longform_v&lt;N&gt;.mp4 plus a longform_edits row, so the whole history
/// stays queryable and a failed revision changes nothing.</summary>
public static class Revise
{
    public const int MAX_AGENT_TURNS = 16;
    public const double MIN_SELECTION_SECONDS = 2.0;
    // A selection whose edges sit within this of a rendered candidate's reuses the
    // file as-is; inside the window it is trimmed from it; anything else is
    // fetched from the source URL.
    public const double REUSE_TOLERANCE_SECONDS = 0.25;
    public const double MAX_TRANSCRIPT_WINDOW_SECONDS = 1200;
    public const double MAX_AUDIO_WINDOW_SECONDS = 600;
    public const int MAX_RESCORE_CHUNKS = 6;

    /// <summary>Injection seams for the render helpers (the tests' monkeypatch
    /// equivalents): (sourcePath, outputPath, startOffsetSeconds, durationSeconds).</summary>
    public static Action<string, string, double, double> TrimClipFn { get; set; } =
        (sourcePath, outputPath, startOffset, duration) =>
            Render.TrimClip(sourcePath, outputPath, startOffset, duration);

    /// <summary>(sourceUrl, outputPath, startSeconds, endSeconds)</summary>
    public static Action<string, string, double, double> RenderClipFromVideoUrlFn { get; set; } =
        (sourceUrl, outputPath, start, end) =>
            Render.RenderClipFromVideoUrl(sourceUrl, outputPath, start, end);

    public const string REVISE_SYSTEM_PROMPT =
        """
        You are the supervising editor revising an existing long-form edit to satisfy
        the client's revision request.

        You start with the full state of the current edit: the candidate segments the
        first pass nominated, the segments the current version keeps, the editor's
        notes, and any earlier revisions. Tools give you everything else on demand:
        the word-timestamped transcript, the actual audio, the source's scene-cut
        timings, and the content research. You can also rerun parts of the pipeline:
        rerun_research refreshes the research with a new focus, and rescore_chunks
        re-edits specific chunks of the source with fresh guidance when you suspect
        the first pass missed material the request needs.

        Work like an editor, not a chatbot: gather only what the request actually
        requires, then commit. When the request points at specific content ("cut the
        sponsor talk"), read the transcript around the likely spots to find exact
        boundaries; listen to the audio when delivery or timing matters.

        The final video is your selections butt-spliced in strict source-chronological
        order — never plan an edit that depends on reordering. Selections are free
        windows over the source: you may split, tighten, extend, or add material.
        Never cut a spoken word in half; start and end at natural speech boundaries.
        A small excision inside a passage (a cough, a false start) is two adjacent
        selections that skip the junk.

        Finish by calling apply_edit exactly once with every selection for the NEW
        version. It replaces the current edit outright, so include the segments you
        are keeping unchanged, not just the ones you touched.
        """;

    private const string TOOLS_JSON =
        """
        [
          {
            "type": "function",
            "function": {
              "name": "get_transcript",
              "description": "Word-timestamped transcript for a window of the source (absolute seconds).",
              "parameters": {
                "type": "object",
                "properties": {
                  "start_seconds": {"type": "number"},
                  "end_seconds": {"type": "number"}
                },
                "required": ["start_seconds", "end_seconds"]
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "listen_audio",
              "description": "Hear the source audio for a window (absolute seconds, up to 10 minutes). The audio arrives in the next message.",
              "parameters": {
                "type": "object",
                "properties": {
                  "start_seconds": {"type": "number"},
                  "end_seconds": {"type": "number"}
                },
                "required": ["start_seconds", "end_seconds"]
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "get_scene_cuts",
              "description": "The source's detected visual scene-cut timestamps in a window (absolute seconds). Clip boundaries near a cut read cleaner.",
              "parameters": {
                "type": "object",
                "properties": {
                  "start_seconds": {"type": "number"},
                  "end_seconds": {"type": "number"}
                },
                "required": ["start_seconds", "end_seconds"]
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "get_research",
              "description": "The cached content research context for this source.",
              "parameters": {"type": "object", "properties": {}}
            }
          },
          {
            "type": "function",
            "function": {
              "name": "rerun_research",
              "description": "Rerun the web-grounded content research with a new focus; the refreshed context is cached and returned.",
              "parameters": {
                "type": "object",
                "properties": {
                  "focus": {
                    "type": "string",
                    "description": "What the research should dig into this time."
                  }
                },
                "required": ["focus"]
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "rescore_chunks",
              "description": "Rerun first-pass segment nomination on specific chunks with fresh guidance (transcript + audio are re-read). Returns the new candidates; use their windows in apply_edit if they earn a place.",
              "parameters": {
                "type": "object",
                "properties": {
                  "chunk_indexes": {
                    "type": "array",
                    "items": {"type": "integer"},
                    "description": "Chunk indexes to re-edit (at most 6 per call)."
                  },
                  "guidance": {
                    "type": "string",
                    "description": "What the first pass should look for this time."
                  }
                },
                "required": ["chunk_indexes", "guidance"]
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "apply_edit",
              "description": "Commit the new cut. Selections are absolute-seconds windows over the source, assembled in chronological order.",
              "parameters": {
                "type": "object",
                "properties": {
                  "selections": {
                    "type": "array",
                    "items": {
                      "type": "object",
                      "properties": {
                        "start_seconds": {"type": "number"},
                        "end_seconds": {"type": "number"},
                        "reason": {"type": "string"}
                      },
                      "required": ["start_seconds", "end_seconds", "reason"]
                    }
                  },
                  "notes": {
                    "type": "string",
                    "description": "Brief explanation of what changed and why."
                  }
                },
                "required": ["selections", "notes"]
              }
            }
          }
        ]
        """;

    public static JsonArray Tools() => (JsonArray)JsonUtil.Parse(TOOLS_JSON)!;

    public static void Main(string projectId, string request, string? outputRoot = null)
    {
        Config.LoadEnv();
        var root = outputRoot
            ?? Config.Env("OUTPUT_ROOT", Defaults.DEFAULT_OUTPUT_ROOT);
        var projectDir = Path.Combine(root, "projects", projectId);
        if (!File.Exists(Path.Combine(projectDir, "project.json")))
            throw new PipelineError($"No local project record at {projectDir}");

        var state = LoadState(projectDir);
        var db = projectId.StartsWith("local-") ? null : new SupabaseClient();
        var records = new ProjectRecords(projectDir);

        Console.WriteLine(
            $"Revising project {projectId} "
            + $"(current version {state.CurrentVersion}): {request}");
        var (edit, toolsUsed) = RunAgent(state: state, records: records, request: request);
        var result = ApplyRevision(
            state: state,
            records: records,
            db: db,
            edit: edit,
            request: request,
            toolsUsed: toolsUsed);
        Console.WriteLine(
            $"Revision v{JsonUtil.Str(result["version"])} ready: {JsonUtil.Str(result["local_path"])} "
            + $"({JsonUtil.Str(result["segments"])} segment(s), "
            + $"~{Py.F(JsonUtil.Double(result["duration_seconds"]) / 60, 1)} min)");
    }

    /// <summary>Everything the agent and assembly need, read from the local mirror.</summary>
    public static ReviseState LoadState(string projectDir)
    {
        var project = JsonUtil.ParseObject(
            File.ReadAllText(Path.Combine(projectDir, "project.json")));
        var chunks = ReadJsonl(Path.Combine(projectDir, "transcript_chunks.jsonl"));
        chunks.Sort((a, b) =>
            JsonUtil.Int(a["chunk_index"]).CompareTo(JsonUtil.Int(b["chunk_index"])));

        var candidates = ReadJsonl(Path.Combine(projectDir, "clips.jsonl"))
            .Where(row =>
                JsonUtil.StrOrNull((row["metadata"] as JsonObject)?["source"]) == "llm"
                && JsonUtil.StrOrNull(row["status"]) == "rendered")
            .OrderBy(row => JsonUtil.Double(row["start_seconds"]))
            .ToList();

        var longformEdits = ReadJsonl(Path.Combine(projectDir, "longform_edits.jsonl"));
        var revisions = ReadJsonl(Path.Combine(projectDir, "decisions.jsonl"))
            .Where(row => JsonUtil.StrOrNull(row["type"]) == "revision")
            .ToList();

        var researchFile = Path.Combine(projectDir, "research.json");
        var research = File.Exists(researchFile)
            ? JsonUtil.ParseObject(File.ReadAllText(researchFile))
            : null;

        var wordSpans = new List<(double Start, double End)>();
        var cuts = new List<double>();
        foreach (var chunk in chunks)
        {
            foreach (var word in JsonUtil.Objects(chunk["words"]))
                wordSpans.Add((
                    JsonUtil.Double(word["absolute_start"]),
                    JsonUtil.Double(word["absolute_end"])));
            foreach (var cut in (chunk["metadata"] as JsonObject)?["scene_cuts"] as JsonArray
                ?? new JsonArray())
                cuts.Add(JsonUtil.Double(cut));
        }

        var ingest = (project["metadata"] as JsonObject)?["ingest"] as JsonObject
            ?? new JsonObject();
        var versions = longformEdits
            .Select(row => JsonUtil.Truthy(row["version"]) ? JsonUtil.Int(row["version"]) : 1)
            .ToList();
        var currentVersion = versions.Count > 0
            ? versions.Max()
            : File.Exists(Path.Combine(projectDir, "longform", "longform.mp4")) ? 1 : 0;

        return new ReviseState
        {
            ProjectDir = projectDir,
            Project = project,
            ProjectId = JsonUtil.Truthy(project["id"])
                ? JsonUtil.Str(project["id"])
                : Path.GetFileName(projectDir.TrimEnd(Path.DirectorySeparatorChar)),
            SourceUrl = JsonUtil.StrOrNull(project["source_url"]),
            Chunks = chunks,
            Candidates = candidates,
            LongformEdits = longformEdits,
            Revisions = revisions,
            Research = research,
            WordSpans = wordSpans,
            Cuts = cuts.OrderBy(cut => cut).ToList(),
            TargetLength = JsonUtil.Truthy(ingest["target_length_minutes"])
                ? JsonUtil.Str(ingest["target_length_minutes"])
                : Defaults.DEFAULT_TARGET_LENGTH_MINUTES,
            SourceMinutes = JsonUtil.TryDouble(ingest["source_minutes"], out var minutes)
                ? minutes
                : null,
            UserInstructions = JsonUtil.StrOrNull(ingest["user_instructions"]),
            SourceEndSeconds = chunks.Count > 0
                ? chunks.Max(chunk => JsonUtil.Double(chunk["end_seconds"]))
                : 0.0,
            CurrentVersion = currentVersion,
        };
    }

    private static List<JsonObject> ReadJsonl(string path)
    {
        if (!File.Exists(path)) return new List<JsonObject>();
        return File.ReadAllLines(path)
            .Where(line => line.Trim().Length > 0)
            .Select(line => JsonUtil.ParseObject(line))
            .ToList();
    }

    /// <summary>Run the tool loop until apply_edit lands, trying each provider in
    /// the audio chain with a fresh loop (histories do not survive a provider
    /// swap). Returns (edit, toolsUsed); throws when no provider produced a
    /// valid edit.</summary>
    public static (JsonObject Edit, List<string> ToolsUsed) RunAgent(
        ReviseState state,
        ProjectRecords records,
        string request,
        string reasoningEffort = Defaults.DEFAULT_LLM_REASONING_EFFORT)
    {
        var providers = Providers.AudioProviders(
            title: "highlighter revise",
            openrouterReasoningEffort: string.IsNullOrEmpty(reasoningEffort)
                ? Defaults.DEFAULT_LLM_REASONING_EFFORT
                : reasoningEffort);
        Exception lastError = new PipelineError("No revision provider is configured");
        for (var index = 0; index < providers.Count; index++)
        {
            try
            {
                return RunAgentAttempt(
                    provider: providers[index], state: state, records: records, request: request);
            }
            catch (Exception exc)
            {
                lastError = exc;
                if (index + 1 < providers.Count)
                {
                    Console.WriteLine(
                        $"{providers[index].Label} revision attempt failed; retrying with "
                        + $"{providers[index + 1].Label}: {exc.Message}");
                }
            }
        }
        throw lastError;
    }

    private static (JsonObject Edit, List<string> ToolsUsed) RunAgentAttempt(
        ChatProvider provider, ReviseState state, ProjectRecords records, string request)
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = REVISE_SYSTEM_PROMPT },
            new JsonObject { ["role"] = "user", ["content"] = OpeningMessage(state, request) },
        };
        if (provider.Name == "azure")
        {
            // The gpt-audio deployments reject any request whose input contains no
            // audio at all, and the opening turns have none until listen_audio
            // runs; seed the conversation with the source's first minute so every
            // turn carries audio.
            var orientation = OrientationAudioParts(state);
            if (orientation.Count > 0)
            {
                var orientationContent = new JsonArray();
                foreach (var part in orientation) orientationContent.Add(part);
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = orientationContent });
            }
        }
        var toolsUsed = new List<string>();

        using var client = provider.Client(timeoutSeconds: 300.0);
        for (var turn = 0; turn < MAX_AGENT_TURNS; turn++)
        {
            var forcing = turn >= MAX_AGENT_TURNS - 2;
            var body = new JsonObject
            {
                ["messages"] = (JsonArray)messages.DeepClone(),
                ["tools"] = Tools(),
                // The last turns force the commit; a named tool_choice is
                // stronger than pleading in a user message.
                ["tool_choice"] = forcing
                    ? new JsonObject
                    {
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = "apply_edit" },
                    }
                    : "auto",
            };
            provider.ApplyRequestOptions(body);
            var response = client.ChatCompletions(body);
            if (response["choices"] is not JsonArray choices || choices.Count == 0)
                throw new PipelineError("Revision agent response did not include choices");
            var message = choices[0]?["message"] as JsonObject
                ?? throw new PipelineError("Revision agent response did not include a message");
            // Replayed verbatim: reasoning_details carries the Gemini thought
            // signatures, and the provider rejects a history without them.
            messages.Add(message.DeepClone());

            var calls = message["tool_calls"] as JsonArray ?? new JsonArray();
            if (calls.Count == 0)
            {
                messages.Add(new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = "Continue with a tool call; call apply_edit once the new cut is ready.",
                });
                continue;
            }

            var audioParts = new List<JsonObject>();
            JsonObject? edit = null;
            foreach (var callNode in calls)
            {
                if (callNode is not JsonObject call) continue;
                var name = JsonUtil.Str(call["function"]?["name"]);
                var rawArguments = JsonUtil.StrOrNull(call["function"]?["arguments"]);
                JsonObject callArgs;
                try
                {
                    callArgs = JsonUtil.ParseObject(
                        string.IsNullOrEmpty(rawArguments) ? "{}" : rawArguments);
                }
                catch (System.Text.Json.JsonException exc)
                {
                    messages.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = JsonUtil.C(call["id"]),
                        ["content"] = $"Invalid JSON arguments: {exc.Message}",
                    });
                    continue;
                }
                toolsUsed.Add(name);
                Console.WriteLine($"[agent] {name}({SummarizeArgs(callArgs)})");

                string resultText;
                if (name == "apply_edit" && edit is null)
                {
                    var (selections, problem) = ValidateSelections(
                        callArgs["selections"], state.SourceEndSeconds);
                    if (problem is not null)
                    {
                        resultText = problem;
                    }
                    else
                    {
                        var selectionsArray = new JsonArray();
                        foreach (var selection in selections) selectionsArray.Add(selection);
                        edit = new JsonObject
                        {
                            ["selections"] = selectionsArray,
                            ["notes"] = JsonUtil.Truthy(callArgs["notes"])
                                ? JsonUtil.Str(callArgs["notes"])
                                : "",
                        };
                        resultText = "Edit accepted.";
                    }
                }
                else
                {
                    var (text, parts) = RunTool(name, callArgs, state, records);
                    resultText = text;
                    audioParts.AddRange(parts);
                }
                messages.Add(new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = JsonUtil.C(call["id"]),
                    ["content"] = resultText,
                });
            }

            if (audioParts.Count > 0)
            {
                var content = new JsonArray();
                foreach (var part in audioParts) content.Add(part);
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = content });
            }
            if (edit is not null)
                return (edit, toolsUsed.Distinct().OrderBy(t => t, StringComparer.Ordinal).ToList());
        }

        throw new PipelineError(
            $"The revision agent did not produce a valid edit within {MAX_AGENT_TURNS} turns");
    }

    /// <summary>The source's opening minute as input_audio content parts; empty
    /// when the chunk audio is missing (the Azure attempt then fails over to the
    /// next provider on its first request).</summary>
    private static List<JsonObject> OrientationAudioParts(ReviseState state)
    {
        try
        {
            if (state.Chunks.Count == 0) return new List<JsonObject>();
            var chunk = state.Chunks[0];
            var audioPath = ResolveMediaPath(
                JsonUtil.StrOrNull((chunk["metadata"] as JsonObject)?["audio_path"]),
                state.ProjectDir,
                "audio");
            if (audioPath is null) return new List<JsonObject>();
            var chunkStart = JsonUtil.Double(chunk["start_seconds"]);
            var chunkEnd = JsonUtil.Double(chunk["end_seconds"]);
            var end = Math.Min(chunkStart + 60.0, chunkEnd);
            var mp3 = Llm.EncodeAudioContextMp3(
                audioContext: new List<JsonObject>
                {
                    new()
                    {
                        ["path"] = audioPath,
                        ["start_seconds"] = chunkStart,
                        ["end_seconds"] = chunkEnd,
                    },
                },
                visibleStartSeconds: chunkStart,
                visibleEndSeconds: end);
            return new List<JsonObject>
            {
                new()
                {
                    ["type"] = "text",
                    ["text"] = $"Source audio {Py.F(chunkStart, 0)}s-{Py.F(end, 0)}s for "
                        + "orientation; use listen_audio for the windows you need.",
                },
                new()
                {
                    ["type"] = "input_audio",
                    ["input_audio"] = new JsonObject
                    {
                        ["data"] = Convert.ToBase64String(mp3),
                        ["format"] = "mp3",
                    },
                },
            };
        }
        catch (Exception exc)
        {
            Console.WriteLine($"Orientation audio unavailable (continuing): {exc.Message}");
            return new List<JsonObject>();
        }
    }

    private static string OpeningMessage(ReviseState state, string request)
    {
        var current = state.LongformEdits.Count > 0 ? state.LongformEdits[^1] : null;
        List<string> currentLines;
        if (current is not null)
        {
            currentLines = JsonUtil.Objects(current["segments"])
                .Select((s, i) =>
                {
                    var start = JsonUtil.Double(s["start_seconds"]);
                    var end = JsonUtil.Double(s["end_seconds"]);
                    var title = JsonUtil.Truthy(s.TryGetPropertyValue("title", out var t) ? t : null)
                        ? JsonUtil.Str(s["title"])
                        : "Untitled";
                    return $"{i}. [{Py.F(start, 1)}s-{Py.F(end, 1)}s | {Py.F(end - start, 1)}s] {title}";
                })
                .ToList();
        }
        else
        {
            currentLines = new List<string>
            {
                "(no long-form version exists yet — build one from the candidates)",
            };
        }
        var editorMeta = current?["editor"] as JsonObject ?? new JsonObject();
        var candidateLines = state.Candidates
            .Select((c, i) =>
            {
                var start = JsonUtil.Double(c["start_seconds"]);
                var end = JsonUtil.Double(c["end_seconds"]);
                return $"{i}. [{Py.F(start, 1)}s-{Py.F(end, 1)}s | "
                    + $"{Py.F(end - start, 1)}s | score {JsonUtil.PyStr(c["score"])}] "
                    + $"{JsonUtil.PyStr(c["title"])} — "
                    + $"{(JsonUtil.Truthy(c["description"]) ? JsonUtil.Str(c["description"]) : "")}";
            })
            .ToList();
        var revisionLines = state.Revisions
            .Select(r =>
                $"v{JsonUtil.PyStr(r["version"])}: {JsonUtil.PyStr(r["request"])} -> {JsonUtil.PyStr(r["notes"])}")
            .ToList();
        var sourceMinutes = state.SourceMinutes ?? state.SourceEndSeconds / 60;
        var lines = new List<string>
        {
            $"Revision request: {request}",
            "",
            $"Source: ~{Py.F(sourceMinutes, 0)} minutes captured "
            + $"(0s to {Py.F(state.SourceEndSeconds, 0)}s). "
            + $"Target runtime: {state.TargetLength} minutes.",
            $"Current edit (version {state.CurrentVersion}):",
        };
        lines.AddRange(currentLines);
        lines.Add(
            $"Editor notes: {(JsonUtil.Truthy(editorMeta["edit_notes"]) ? JsonUtil.Str(editorMeta["edit_notes"]) : "(none)")}");
        lines.Add("");
        lines.Add("Earlier revisions:");
        lines.AddRange(revisionLines.Count > 0 ? revisionLines : new List<string> { "(none)" });
        lines.Add("");
        lines.Add("Candidate segments from the first pass (chronological):");
        lines.AddRange(candidateLines.Count > 0
            ? candidateLines
            : new List<string> { "(none rendered)" });
        return string.Join("\n", lines);
    }

    private static string SummarizeArgs(JsonObject callArgs)
    {
        var parts = new List<string>();
        foreach (var pair in callArgs)
        {
            var text = JsonUtil.Dumps(pair.Value);
            parts.Add(
                $"{pair.Key}={text[..Math.Min(80, text.Length)]}{(text.Length > 80 ? "..." : "")}");
        }
        return string.Join(", ", parts);
    }

    /// <summary>Execute one read/rerun tool. Returns (tool result text, audio content
    /// parts to deliver in a follow-up user message). Failures come back as text
    /// so the agent can adjust instead of dying.</summary>
    private static (string Text, List<JsonObject> AudioParts) RunTool(
        string name, JsonObject callArgs, ReviseState state, ProjectRecords records)
    {
        var none = new List<JsonObject>();
        try
        {
            if (name == "get_transcript")
            {
                var (start, end) = Window(callArgs, state, MAX_TRANSCRIPT_WINDOW_SECONDS);
                var words = ChunksInWindow(state, start, end)
                    .SelectMany(chunk => JsonUtil.Objects(chunk["words"]))
                    .Where(word => start <= JsonUtil.Double(word["absolute_start"])
                        && JsonUtil.Double(word["absolute_start"]) < end)
                    .ToList();
                var markers = Llm.TimestampMarkers(
                    words: words, startSeconds: (int)start, endSeconds: (int)end + 1,
                    markerSeconds: 10);
                return (markers.Length > 0 ? markers : "(no speech in this window)", none);
            }

            if (name == "listen_audio")
            {
                var (start, end) = Window(callArgs, state, MAX_AUDIO_WINDOW_SECONDS);
                var audioContext = ChunksInWindow(state, start, end)
                    .Select(chunk => new JsonObject
                    {
                        ["path"] = ResolveMediaPath(
                            JsonUtil.StrOrNull((chunk["metadata"] as JsonObject)?["audio_path"]),
                            state.ProjectDir,
                            "audio"),
                        ["start_seconds"] = JsonUtil.C(chunk["start_seconds"]),
                        ["end_seconds"] = JsonUtil.C(chunk["end_seconds"]),
                    })
                    .ToList();
                var mp3 = Llm.EncodeAudioContextMp3(
                    audioContext: audioContext,
                    visibleStartSeconds: start,
                    visibleEndSeconds: end);
                var parts = new List<JsonObject>
                {
                    new()
                    {
                        ["type"] = "text",
                        ["text"] = $"Audio for {Py.F(start, 1)}s-{Py.F(end, 1)}s:",
                    },
                    new()
                    {
                        ["type"] = "input_audio",
                        ["input_audio"] = new JsonObject
                        {
                            ["data"] = Convert.ToBase64String(mp3),
                            ["format"] = "mp3",
                        },
                    },
                };
                return (
                    $"Audio for {Py.F(start, 1)}s-{Py.F(end, 1)}s is attached in the next message.",
                    parts);
            }

            if (name == "get_scene_cuts")
            {
                var (start, end) = Window(callArgs, state, null);
                var cuts = state.Cuts.Where(cut => start <= cut && cut <= end).ToList();
                if (cuts.Count == 0) return ("No scene cuts detected in this window.", none);
                return (
                    "Scene cuts (absolute seconds): "
                    + string.Join(", ", cuts.Select(c => Py.F(c, 1))),
                    none);
            }

            if (name == "get_research")
            {
                if (state.Research is null)
                    return ("No research context is cached for this project.", none);
                return (JsonUtil.DumpsIndented(state.Research), none);
            }

            if (name == "rerun_research")
            {
                var focus = JsonUtil.Truthy(callArgs["focus"]) ? JsonUtil.Str(callArgs["focus"]) : "";
                var instructions = string.Join("\n",
                    new[] { state.UserInstructions, $"Research focus: {focus}" }
                        .Where(part => !string.IsNullOrEmpty(part)));
                var project = state.Project;
                var context = Research.ResearchContentContext(
                    sourceContext: new JsonObject
                    {
                        ["platform"] = JsonUtil.C((project["metadata"] as JsonObject)?["platform"]),
                        ["source_type"] = JsonUtil.C(project["source_type"]),
                        ["name"] = JsonUtil.C(project["name"]),
                        ["source_url"] = JsonUtil.C(project["source_url"]),
                    },
                    pipelineMode: "long",
                    userInstructions: instructions);
                state.Research = context;
                records.WriteResearch(context);
                return (JsonUtil.DumpsIndented(context), none);
            }

            if (name == "rescore_chunks")
                return (RescoreChunks(callArgs, state), none);

            return ($"Unknown tool: {name}", none);
        }
        catch (Exception exc)
        {
            return ($"{name} failed: {exc.Message}", none);
        }
    }

    private static string RescoreChunks(JsonObject callArgs, ReviseState state)
    {
        var indexes = (callArgs["chunk_indexes"] as JsonArray ?? new JsonArray())
            .Where(node => JsonUtil.TryDouble(node, out _))
            .Select(node => (int)JsonUtil.Double(node))
            .Take(MAX_RESCORE_CHUNKS)
            .ToList();
        var guidance = JsonUtil.Truthy(callArgs["guidance"]) ? JsonUtil.Str(callArgs["guidance"]) : "";
        var instructions = string.Join("\n",
            new[] { state.UserInstructions, guidance }
                .Where(part => !string.IsNullOrEmpty(part)));
        var byIndex = state.Chunks.ToDictionary(
            chunk => JsonUtil.Int(chunk["chunk_index"]), chunk => chunk);
        var results = new JsonArray();
        foreach (var index in indexes)
        {
            if (!byIndex.TryGetValue(index, out var chunk))
            {
                results.Add(new JsonObject
                {
                    ["chunk_index"] = index,
                    ["error"] = "no such chunk",
                });
                continue;
            }
            var audioPath = ResolveMediaPath(
                JsonUtil.StrOrNull((chunk["metadata"] as JsonObject)?["audio_path"]),
                state.ProjectDir,
                "audio");
            var chunkStart = JsonUtil.Int(chunk["start_seconds"]);
            var chunkEnd = JsonUtil.Int(chunk["end_seconds"]);
            var audioContext = audioPath is not null
                ? new List<JsonObject>
                {
                    new()
                    {
                        ["path"] = audioPath,
                        ["start_seconds"] = JsonUtil.C(chunk["start_seconds"]),
                        ["end_seconds"] = JsonUtil.C(chunk["end_seconds"]),
                    },
                }
                : new List<JsonObject>();
            var (candidates, assessment) = Llm.DetectClipCandidates(
                transcript: JsonUtil.Str(chunk["transcript"]),
                words: JsonUtil.Objects(chunk["words"]),
                chunkIndex: index,
                startSeconds: chunkStart,
                endSeconds: chunkEnd,
                pipelineMode: "long",
                userInstructions: instructions.Length > 0 ? instructions : null,
                targetLength: state.TargetLength,
                researchContext: state.Research,
                audioContext: audioContext,
                sceneCuts: state.Cuts
                    .Where(cut => chunkStart <= cut && cut <= chunkEnd)
                    .ToList(),
                sourceMinutes: state.SourceMinutes);
            var candidateRows = new JsonArray();
            foreach (var c in candidates)
            {
                candidateRows.Add(new JsonObject
                {
                    ["start_seconds"] = JsonUtil.C(c["start_seconds"]),
                    ["end_seconds"] = JsonUtil.C(c["end_seconds"]),
                    ["title"] = JsonUtil.C(c["title"]),
                    ["description"] = JsonUtil.C(c["description"]),
                    ["score"] = JsonUtil.C(c["score"]),
                    ["reason"] = JsonUtil.C(c["reason"]),
                });
            }
            results.Add(new JsonObject
            {
                ["chunk_index"] = index,
                ["assessment"] = assessment,
                ["candidates"] = candidateRows,
            });
        }
        return JsonUtil.DumpsIndented(results);
    }

    public static (double Start, double End) Window(
        JsonObject callArgs, ReviseState state, double? capSeconds)
    {
        var start = Math.Max(0.0, JsonUtil.DoubleOr(callArgs["start_seconds"], 0));
        var end = Math.Min(JsonUtil.DoubleOr(callArgs["end_seconds"], 0), state.SourceEndSeconds);
        if (end <= start)
            throw new PipelineError("end_seconds must be greater than start_seconds");
        if (capSeconds is double cap && end - start > cap) end = start + cap;
        return (start, end);
    }

    private static List<JsonObject> ChunksInWindow(ReviseState state, double start, double end) =>
        state.Chunks
            .Where(chunk => JsonUtil.Double(chunk["start_seconds"]) < end
                && JsonUtil.Double(chunk["end_seconds"]) > start)
            .ToList();

    /// <summary>Media paths were recorded relative to the original run's working
    /// directory; fall back to the project directory when that CWD moved.</summary>
    public static string? ResolveMediaPath(string? raw, string projectDir, string subdir)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        if (File.Exists(raw)) return raw;
        var fallback = Path.Combine(projectDir, subdir, Path.GetFileName(raw));
        return File.Exists(fallback) ? fallback : null;
    }

    /// <summary>Clamp to the source, drop degenerate windows, sort chronologically, and
    /// merge overlaps. Returns (selections, problem) — problem is a message for
    /// the agent when nothing usable was submitted.</summary>
    public static (List<JsonObject> Selections, string? Problem) ValidateSelections(
        JsonNode? raw, double sourceEndSeconds)
    {
        var selections = new List<JsonObject>();
        foreach (var rawItem in raw as JsonArray ?? new JsonArray())
        {
            if (rawItem is not JsonObject item) continue;
            if (!item.TryGetPropertyValue("start_seconds", out var startNode)
                || !item.TryGetPropertyValue("end_seconds", out var endNode)
                || !JsonUtil.TryDouble(startNode, out var startValue)
                || !JsonUtil.TryDouble(endNode, out var endValue))
                continue;
            var start = Math.Max(0.0, startValue);
            var end = Math.Min(endValue, sourceEndSeconds);
            if (end - start < MIN_SELECTION_SECONDS) continue;
            selections.Add(new JsonObject
            {
                ["start_seconds"] = Py.Round(start, 3),
                ["end_seconds"] = Py.Round(end, 3),
                ["reason"] = JsonUtil.Truthy(item.TryGetPropertyValue("reason", out var reason)
                    ? reason
                    : null)
                    ? JsonUtil.Str(reason)
                    : "",
            });
        }
        if (selections.Count == 0)
        {
            return (new List<JsonObject>(), (
                "No usable selections: every window was missing, outside the source, "
                + $"or shorter than {Py.G(MIN_SELECTION_SECONDS)}s. Submit apply_edit again."));
        }

        selections = selections
            .OrderBy(s => JsonUtil.Double(s["start_seconds"]))
            .ThenBy(s => JsonUtil.Double(s["end_seconds"]))
            .ToList();
        var merged = new List<JsonObject> { selections[0] };
        foreach (var selection in selections.Skip(1))
        {
            var last = merged[^1];
            if (JsonUtil.Double(selection["start_seconds"]) < JsonUtil.Double(last["end_seconds"]))
            {
                last["end_seconds"] = Math.Max(
                    JsonUtil.Double(last["end_seconds"]),
                    JsonUtil.Double(selection["end_seconds"]));
                continue;
            }
            merged.Add(selection);
        }
        return (merged, null);
    }

    /// <summary>Materialize the agent's selections into the next long-form version.</summary>
    public static JsonObject ApplyRevision(
        ReviseState state,
        ProjectRecords records,
        SupabaseClient? db,
        JsonObject edit,
        string request,
        List<string> toolsUsed)
    {
        var projectDir = state.ProjectDir;
        var projectId = state.ProjectId;
        var version = state.CurrentVersion + 1;
        var segmentsDir = Path.Combine(projectDir, "longform", "segments");

        var entries = new List<JsonObject>();
        var selections = edit["selections"] as JsonArray ?? new JsonArray();
        for (var order = 0; order < selections.Count; order++)
        {
            if (selections[order] is not JsonObject selection) continue;
            var start = JsonUtil.Double(selection["start_seconds"]);
            var end = JsonUtil.Double(selection["end_seconds"]);
            JsonObject? snapInfo = null;
            if (state.Cuts.Count > 0)
            {
                (start, end, snapInfo) = Shots.SnapWindow(
                    start, end, cuts: state.Cuts, wordSpans: state.WordSpans);
            }
            var (path, source) = MaterializeSelection(
                state: state,
                startSeconds: start,
                endSeconds: end,
                segmentsDir: segmentsDir,
                version: version,
                order: order);
            var entry = new JsonObject
            {
                ["path"] = path,
                ["start_seconds"] = start,
                ["end_seconds"] = end,
                ["title"] = JsonUtil.C(source["title"]),
                ["reason"] = JsonUtil.C(selection["reason"]),
                ["render_source"] = JsonUtil.C(source["mode"]),
            };
            if (snapInfo is not null) entry["boundary_snap"] = snapInfo;
            entries.Add(entry);
        }

        var outputPath = Path.Combine(projectDir, "longform", $"longform_v{version}.mp4");
        var totalSeconds = entries.Sum(entry =>
            JsonUtil.Double(entry["end_seconds"]) - JsonUtil.Double(entry["start_seconds"]));
        Console.WriteLine(
            $"Stitching {entries.Count} segment(s) (~{Py.F(totalSeconds / 60, 1)} min) into {outputPath}");
        Stitch.StitchClips(
            clipPaths: entries.Select(entry => JsonUtil.Str(entry["path"])).ToList(),
            outputPath: outputPath);

        var segmentWindows = new JsonArray();
        foreach (var entry in entries)
        {
            segmentWindows.Add(new JsonObject
            {
                ["title"] = JsonUtil.C(entry["title"]),
                ["start_seconds"] = JsonUtil.C(entry["start_seconds"]),
                ["end_seconds"] = JsonUtil.C(entry["end_seconds"]),
                ["reason"] = JsonUtil.C(entry["reason"]),
            });
        }
        var result = new JsonObject
        {
            ["status"] = "rendered",
            ["version"] = version,
            ["local_path"] = Path.GetRelativePath(Directory.GetCurrentDirectory(), outputPath),
            ["filename"] = Path.GetFileName(outputPath),
            ["segments"] = entries.Count,
            ["duration_seconds"] = Py.Round(totalSeconds, 3),
            ["segment_windows"] = segmentWindows,
        };

        string? thumbnailPath = null;
        try
        {
            thumbnailPath = Path.ChangeExtension(outputPath, ".jpg");
            Render.ExtractThumbnail(
                clipPath: outputPath, outputPath: thumbnailPath, atSeconds: totalSeconds / 2);
        }
        catch (Exception exc)
        {
            thumbnailPath = null;
            Console.WriteLine($"Revision thumbnail extraction failed (non-fatal): {exc.Message}");
        }

        var revisionMeta = new JsonObject
        {
            ["request"] = request,
            ["notes"] = JsonUtil.C(edit["notes"]),
            ["tools_used"] = JsonUtil.Arr(toolsUsed.Select(tool => (JsonNode?)tool)),
        };
        var clipsBucket = Config.Env("SUPABASE_CLIPS_BUCKET", Defaults.DEFAULT_SUPABASE_CLIPS_BUCKET);
        if (db is not null)
        {
            try
            {
                var storageKey = $"projects/{projectId}/longform/{Path.GetFileName(outputPath)}";
                var videoUrl = db.UploadStorageObject(
                    bucket: clipsBucket, key: storageKey, path: outputPath);
                result["bucket"] = clipsBucket;
                result["storage_path"] = storageKey;
                result["video_url"] = videoUrl;
                if (thumbnailPath is not null)
                {
                    var thumbnailKey =
                        $"projects/{projectId}/longform/{Path.GetFileName(thumbnailPath)}";
                    result["thumbnail_url"] = db.UploadStorageObject(
                        bucket: clipsBucket, key: thumbnailKey, path: thumbnailPath);
                    result["thumbnail_storage_path"] = thumbnailKey;
                }
                db.InsertLongformEdit(
                    projectId: projectId,
                    version: version,
                    status: "rendered",
                    videoUrl: videoUrl,
                    thumbnailUrl: JsonUtil.StrOrNull(result["thumbnail_url"]),
                    durationSeconds: Py.Round(totalSeconds, 3),
                    segments: segmentWindows,
                    revision: revisionMeta,
                    metadata: new JsonObject
                    {
                        ["source"] = "revision",
                        ["render"] = JsonUtil.CloneObj(result),
                    });
            }
            catch (Exception exc)
            {
                Console.WriteLine($"Revision upload failed (kept locally at {outputPath}): {exc.Message}");
                result["upload_error"] = exc.Message;
            }
        }

        records.AppendLongformEdit(new JsonObject
        {
            ["project_id"] = projectId,
            ["version"] = version,
            ["status"] = "rendered",
            ["video_url"] = JsonUtil.Truthy(result["video_url"])
                ? JsonUtil.C(result["video_url"])
                : JsonUtil.C(result["local_path"]),
            ["thumbnail_url"] = JsonUtil.C(result["thumbnail_url"]),
            ["duration_seconds"] = Py.Round(totalSeconds, 3),
            ["segments"] = (JsonArray)segmentWindows.DeepClone(),
            ["editor"] = null,
            ["revision"] = JsonUtil.CloneObj(revisionMeta),
            ["metadata"] = new JsonObject
            {
                ["source"] = "revision",
                ["render"] = JsonUtil.CloneObj(result),
            },
        });
        var decision = new JsonObject
        {
            ["type"] = "revision",
            ["version"] = version,
        };
        foreach (var pair in revisionMeta) decision[pair.Key] = JsonUtil.C(pair.Value);
        var selectionEntries = new JsonArray();
        foreach (var entry in entries) selectionEntries.Add(JsonUtil.CloneObj(entry));
        decision["selections"] = selectionEntries;
        records.AppendDecision(decision);

        var metadata = JsonUtil.Merged(
            state.Project["metadata"] as JsonObject ?? new JsonObject(),
            new JsonObject { ["longform"] = JsonUtil.CloneObj(result) });
        records.UpdateProject(("metadata", metadata));
        if (db is not null)
        {
            try
            {
                var remote = db.GetProject(projectId)["metadata"] as JsonObject ?? new JsonObject();
                db.UpdateProjectMetadata(projectId, JsonUtil.Merged(
                    remote, new JsonObject { ["longform"] = JsonUtil.CloneObj(result) }));
            }
            catch (Exception exc)
            {
                Console.WriteLine($"Project metadata update failed (non-fatal): {exc.Message}");
            }
        }

        return result;
    }

    /// <summary>The rendered file for one selection: an existing candidate render when
    /// the window matches, a trim of one when it fits inside, else a fresh fetch
    /// from the source URL.</summary>
    public static (string Path, JsonObject Source) MaterializeSelection(
        ReviseState state,
        double startSeconds,
        double endSeconds,
        string segmentsDir,
        int version,
        int order)
    {
        foreach (var candidate in state.Candidates)
        {
            var cStart = JsonUtil.Double(candidate["start_seconds"]);
            var cEnd = JsonUtil.Double(candidate["end_seconds"]);
            if (cStart - REUSE_TOLERANCE_SECONDS <= startSeconds
                && endSeconds <= cEnd + REUSE_TOLERANCE_SECONDS)
            {
                var rawPath = JsonUtil.StrOrNull(
                    ((candidate["metadata"] as JsonObject)?["render"] as JsonObject)?["local_path"]);
                var resolved = ResolveMediaPath(rawPath, state.ProjectDir, "clips");
                if (resolved is null) continue;
                if (startSeconds - cStart <= REUSE_TOLERANCE_SECONDS
                    && cEnd - endSeconds <= REUSE_TOLERANCE_SECONDS)
                {
                    return (resolved, new JsonObject
                    {
                        ["mode"] = "candidate",
                        ["title"] = JsonUtil.C(candidate["title"]),
                    });
                }
                var trimmedPath = Path.Combine(segmentsDir, $"v{version}_{order:000}.mp4");
                TrimClipFn(
                    resolved,
                    trimmedPath,
                    Math.Max(0.0, startSeconds - cStart),
                    endSeconds - startSeconds);
                return (trimmedPath, new JsonObject
                {
                    ["mode"] = "trimmed",
                    ["title"] = JsonUtil.C(candidate["title"]),
                });
            }
        }

        if (string.IsNullOrEmpty(state.SourceUrl))
        {
            throw new PipelineError(
                $"Selection {Py.F(startSeconds, 1)}s-{Py.F(endSeconds, 1)}s is outside every rendered "
                + "candidate and the project has no source URL to fetch from");
        }
        Console.WriteLine(
            $"Fetching {Py.F(startSeconds, 1)}s-{Py.F(endSeconds, 1)}s from the source "
            + "(outside every rendered candidate)");
        var outputPath = Path.Combine(segmentsDir, $"v{version}_{order:000}.mp4");
        RenderClipFromVideoUrlFn(state.SourceUrl!, outputPath, startSeconds, endSeconds);
        return (outputPath, new JsonObject { ["mode"] = "fetched", ["title"] = null });
    }
}

/// <summary>The revision loop's working state — the port of _load_state's dict.</summary>
public class ReviseState
{
    public required string ProjectDir { get; init; }
    public required JsonObject Project { get; init; }
    public required string ProjectId { get; init; }
    public string? SourceUrl { get; init; }
    public required List<JsonObject> Chunks { get; init; }
    public required List<JsonObject> Candidates { get; init; }
    public required List<JsonObject> LongformEdits { get; init; }
    public required List<JsonObject> Revisions { get; init; }
    public JsonObject? Research { get; set; }
    public required List<(double Start, double End)> WordSpans { get; init; }
    public required List<double> Cuts { get; init; }
    public required string TargetLength { get; init; }
    public double? SourceMinutes { get; init; }
    public string? UserInstructions { get; init; }
    public double SourceEndSeconds { get; init; }
    public int CurrentVersion { get; init; }
}
