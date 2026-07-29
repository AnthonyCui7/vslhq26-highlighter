using System.Text.Json.Nodes;

namespace Highlighter.Pipeline;

/// <summary>Port of highlighter_pipeline/editor.py.
///
/// Long-form pass 2: one global editor call makes the final cut.
///
/// Pass 1 (chunked scoring) proposes candidate segments and renders each one.
/// This runs once at the end of a long-form run with the full ordered candidate
/// list — timings, titles, summaries, scores — plus the budget arithmetic
/// (source minutes, candidate minutes, target minutes -> keep rate), the research
/// context, and the user's instructions. It returns the final selection: which
/// segments make the edit, each optionally tightened within its own window.
/// Assembly (trims + stitch) stays in Ingest; any failure here falls back to
/// stitching every pass-1 selection, so pass 2 can only improve a run.</summary>
public static class Editor
{
    // Above this, the weakest candidates are dropped from the prompt: a model
    // wading through hundreds of mediocre candidates anchors worse than one
    // reading a strong shortlist, and pass 1 should be tightened instead.
    public const int MAX_PROMPT_CANDIDATES = 150;
    // Selections tighter than this are editorial noise, not an edit.
    public const double MIN_SELECTION_SECONDS = 2.0;

    public const string EDITOR_SYSTEM_PROMPT =
        """
        You are the supervising editor making the final cut of one long-form video.

        A first-pass team went through the full source chronologically and nominated
        candidate segments — each with timings, a working title, a summary, a 0.0-1.0
        score for how essential it seemed, and the reason it was nominated. Every
        candidate is already rendered; the final video will be exactly the segments
        you keep, concatenated in strict source-chronological order. Never plan an
        edit that depends on reordering — selections are always assembled in source
        order regardless of the order you return them.

        Your job is the actual edit:
        - Keep the candidates that belong in the final video and drop the rest.
        - Cut redundant beats: when two candidates cover the same story, joke, or
          point, keep the stronger one.
        - Prefer a coherent, watchable arc — setup before payoff, context before
          callbacks — over a highlight reel of disconnected peaks.
        - You may tighten a kept segment by moving its start later and/or its end
          earlier within that segment's own window; never extend beyond it. Tighten
          only to drop real dead weight at the edges, and never cut a spoken thought
          in half.
        - The user's instructions, when present, take priority over general taste.

        Budget:
        The brief states the target runtime, the minutes of candidate material, and
        the keep rate they imply. Treat the target as guidance from the client: land
        reasonably close, and when in doubt prefer dropping a weak segment over
        padding the runtime. Scores are advisory input from the first pass, not a
        ranking you must obey.

        Return only JSON matching the schema: one selections entry per kept segment
        (the candidate's index, your possibly-tightened start_seconds and end_seconds,
        and a short reason), plus edit_notes briefly explaining the shape of the edit
        and the main things you dropped.
        """;

    private const string EDIT_RESPONSE_SCHEMA_JSON =
        """
        {
          "type": "object",
          "properties": {
            "selections": {
              "type": "array",
              "description": "One entry per candidate segment kept in the final edit.",
              "items": {
                "type": "object",
                "properties": {
                  "index": {
                    "type": "integer",
                    "description": "The kept candidate's index from the candidate list."
                  },
                  "start_seconds": {
                    "type": "number",
                    "description": "Final start, at or after the candidate's own start."
                  },
                  "end_seconds": {
                    "type": "number",
                    "description": "Final end, at or before the candidate's own end."
                  },
                  "reason": {
                    "type": "string",
                    "description": "Brief reason this segment earns its place."
                  }
                },
                "required": ["index", "start_seconds", "end_seconds", "reason"],
                "additionalProperties": false
              }
            },
            "edit_notes": {
              "type": "string",
              "description": "Brief explanation of the edit's shape and the main drops."
            }
          },
          "required": ["selections", "edit_notes"],
          "additionalProperties": false
        }
        """;

    public static JsonObject EditResponseSchema() => JsonUtil.ParseObject(EDIT_RESPONSE_SCHEMA_JSON);

    /// <summary>Run the global editor call over pass-1 candidates (chronologically
    /// sorted dicts with start_seconds/end_seconds/title/description/score/
    /// reason). Returns {selections, edit_notes, arithmetic, model, ...}; throws
    /// on failure — the caller falls back to stitching every candidate.</summary>
    public static JsonObject SelectLongformSegments(
        IReadOnlyList<JsonObject> candidates,
        double sourceMinutes,
        string? targetLength,
        JsonObject? researchContext = null,
        string? userInstructions = null,
        string reasoningEffort = Defaults.DEFAULT_LLM_REASONING_EFFORT)
    {
        var (indexedCandidates, droppedFromPrompt) = CapCandidates(candidates);
        if (droppedFromPrompt > 0)
        {
            Console.WriteLine(
                $"Long-form editor prompt capped at {MAX_PROMPT_CANDIDATES} candidates by score; "
                + $"dropped the {droppedFromPrompt} weakest of {candidates.Count}");
        }
        var arithmetic = BudgetArithmetic(
            indexedCandidates.Select(pair => pair.Candidate).ToList(),
            sourceMinutes: sourceMinutes,
            targetLength: targetLength);

        var userPrompt = BuildUserPrompt(
            indexedCandidates,
            arithmetic: arithmetic,
            researchContext: researchContext,
            userInstructions: userInstructions);
        var providers = Providers.EditorProviders(
            title: "highlighter editor",
            openrouterReasoningEffort: string.IsNullOrEmpty(reasoningEffort)
                ? Defaults.DEFAULT_LLM_REASONING_EFFORT
                : reasoningEffort);
        var (decision, provider) = Providers.RunWithFallback(
            providers, candidateProvider => RequestEdit(candidateProvider, userPrompt));

        var offered = indexedCandidates.ToDictionary(pair => pair.Index, pair => pair.Candidate);
        var selections = new JsonArray();
        foreach (var selection in ValidateSelections(decision, candidates, offered))
            selections.Add(selection);
        return new JsonObject
        {
            ["selections"] = selections,
            ["edit_notes"] = JsonUtil.Truthy(decision["edit_notes"])
                ? JsonUtil.Str(decision["edit_notes"])
                : "",
            ["arithmetic"] = arithmetic,
            ["model"] = provider.Model,
            ["candidates_considered"] = indexedCandidates.Count,
            ["candidates_dropped_from_prompt"] = droppedFromPrompt,
        };
    }

    private static JsonObject RequestEdit(ChatProvider provider, string userPrompt)
    {
        var body = new JsonObject
        {
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = EDITOR_SYSTEM_PROMPT },
                new JsonObject { ["role"] = "user", ["content"] = userPrompt },
            },
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = "longform_edit",
                    ["schema"] = EditResponseSchema(),
                },
            },
        };
        provider.ApplyRequestOptions(body);

        JsonObject response;
        using (var client = provider.Client(timeoutSeconds: 300.0))
        {
            response = client.ChatCompletions(body);
        }

        if (response["choices"] is not JsonArray choices || choices.Count == 0)
            throw new PipelineError($"{provider.Label} editor response did not include choices");
        var content = JsonUtil.StrOrNull(choices[0]?["message"]?["content"]);
        if (string.IsNullOrEmpty(content))
            throw new PipelineError($"{provider.Label} editor response did not include text content");
        return Llm.JsonFromText(content);
    }

    /// <summary>Cap the prompt to the strongest MAX_PROMPT_CANDIDATES, keeping
    /// chronological order and each candidate's original index (selections index
    /// into the caller's full list). Returns (indexed candidates, dropped count).</summary>
    public static (List<(int Index, JsonObject Candidate)> Indexed, int Dropped) CapCandidates(
        IReadOnlyList<JsonObject> candidates)
    {
        var indexed = candidates.Select((candidate, index) => (Index: index, Candidate: candidate))
            .ToList();
        if (indexed.Count <= MAX_PROMPT_CANDIDATES) return (indexed, 0);
        var strongest = indexed
            .OrderByDescending(pair => JsonUtil.DoubleOr(pair.Candidate["score"], 0))
            .ToList();
        var kept = strongest.Take(MAX_PROMPT_CANDIDATES).Select(pair => pair.Index).ToHashSet();
        return (
            indexed.Where(pair => kept.Contains(pair.Index)).ToList(),
            indexed.Count - MAX_PROMPT_CANDIDATES);
    }

    /// <summary>The precomputed numbers the editor calibrates against, in minutes.</summary>
    public static JsonObject BudgetArithmetic(
        IReadOnlyList<JsonObject> candidates, double sourceMinutes, string? targetLength)
    {
        var candidateMinutes = candidates.Sum(c =>
            JsonUtil.Double(c["end_seconds"]) - JsonUtil.Double(c["start_seconds"])) / 60;
        var target = string.IsNullOrEmpty(targetLength)
            ? Defaults.DEFAULT_TARGET_LENGTH_MINUTES
            : targetLength;
        var arithmetic = new JsonObject
        {
            ["source_minutes"] = Py.Round(sourceMinutes, 1),
            ["candidate_count"] = candidates.Count,
            ["candidate_minutes"] = Py.Round(candidateMinutes, 1),
            ["target_minutes"] = target,
        };
        var bounds = Llm.ParseTargetMinutes(target);
        if (bounds is not null && candidateMinutes > 0)
        {
            var midpoint = (bounds.Value.Low + bounds.Value.High) / 2;
            arithmetic["keep_percent"] = Math.Max(1, Math.Min(100,
                (int)Math.Round(100 * midpoint / candidateMinutes, MidpointRounding.ToEven)));
        }
        return arithmetic;
    }

    private static string BuildUserPrompt(
        IReadOnlyList<(int Index, JsonObject Candidate)> indexedCandidates,
        JsonObject arithmetic,
        JsonObject? researchContext,
        string? userInstructions)
    {
        var brief = new List<string>
        {
            "Final-cut brief:",
            $"- Source: roughly {Py.Repr(JsonUtil.Double(arithmetic["source_minutes"]))} minutes captured.",
            $"- Candidates: {JsonUtil.Str(arithmetic["candidate_count"])} segments totaling "
            + $"{Py.Repr(JsonUtil.Double(arithmetic["candidate_minutes"]))} minutes.",
            $"- Target runtime: {JsonUtil.Str(arithmetic["target_minutes"])} minutes.",
        };
        if (arithmetic.ContainsKey("keep_percent"))
        {
            brief.Add(
                $"- Keep rate: roughly {JsonUtil.Str(arithmetic["keep_percent"])}% of the candidate "
                + "minutes should survive. Be harsh at a low rate, looser at a high one.");
        }

        var instructionsBlock = !string.IsNullOrEmpty(userInstructions)
            ? new List<string>
            {
                "User instructions (editorial guidance from the human in the loop;",
                "follow the spirit, but they are not rigid constraints):",
                userInstructions,
                "",
            }
            : new List<string>();

        var lines = indexedCandidates.Select(pair =>
        {
            var (index, c) = pair;
            var score = c.TryGetPropertyValue("score", out var scoreNode) && !JsonUtil.IsNull(scoreNode)
                ? JsonUtil.Str(scoreNode)
                : "?";
            var duration = JsonUtil.Double(c["end_seconds"]) - JsonUtil.Double(c["start_seconds"]);
            var line =
                $"{index}. [{Py.F(JsonUtil.Double(c["start_seconds"]), 1)}s-{Py.F(JsonUtil.Double(c["end_seconds"]), 1)}s | "
                + $"{Py.F(duration, 1)}s | score {score}] "
                + $"{(JsonUtil.Truthy(c["title"]) ? JsonUtil.Str(c["title"]) : "Untitled")} — "
                + $"{(JsonUtil.Truthy(c["description"]) ? JsonUtil.Str(c["description"]) : "(no summary)")}";
            if (JsonUtil.Truthy(c.TryGetPropertyValue("reason", out var reason) ? reason : null))
                line += $" (nominated: {JsonUtil.Str(reason)})";
            return line;
        });

        var parts = new List<string>();
        parts.AddRange(brief);
        parts.Add("");
        parts.AddRange(instructionsBlock);
        parts.AddRange(new[]
        {
            "Content research context:",
            JsonUtil.DumpsIndented(researchContext ?? new JsonObject()),
            "",
            "Candidate segments (chronological):",
        });
        parts.AddRange(lines);
        return string.Join("\n", parts);
    }

    /// <summary>Normalize the model's selections: drop unknown/duplicate indexes, clamp
    /// boundaries into each candidate's own window (tighten-only), drop windows
    /// shorter than MIN_SELECTION_SECONDS, and sort chronologically. Indexes refer
    /// to the caller's full candidate list; only offered ones are valid.</summary>
    public static List<JsonObject> ValidateSelections(
        JsonObject decision,
        IReadOnlyList<JsonObject> candidates,
        IReadOnlyDictionary<int, JsonObject> offered)
    {
        var raw = decision["selections"] as JsonArray ?? new JsonArray();
        var selections = new List<JsonObject>();
        var seen = new HashSet<int>();
        foreach (var rawItem in raw)
        {
            if (rawItem is not JsonObject item) continue;
            if (!JsonUtil.TryDouble(item["index"], out var indexNumber)) continue;
            var index = (int)indexNumber;
            if (seen.Contains(index) || !offered.ContainsKey(index)) continue;
            seen.Add(index);
            var candidate = candidates[index];
            var start = Clamp(
                item["start_seconds"],
                JsonUtil.Double(candidate["start_seconds"]),
                JsonUtil.Double(candidate["end_seconds"]));
            var end = Clamp(
                item["end_seconds"],
                JsonUtil.Double(candidate["start_seconds"]),
                JsonUtil.Double(candidate["end_seconds"]));
            if (end - start < MIN_SELECTION_SECONDS) continue;
            selections.Add(new JsonObject
            {
                ["index"] = index,
                ["start_seconds"] = Py.Round(start, 3),
                ["end_seconds"] = Py.Round(end, 3),
                ["reason"] = JsonUtil.Truthy(item["reason"]) ? JsonUtil.Str(item["reason"]) : "",
            });
        }
        return selections.OrderBy(s => JsonUtil.Double(s["start_seconds"])).ToList();
    }

    private static double Clamp(JsonNode? value, double minimum, double maximum)
    {
        if (!JsonUtil.TryDouble(value, out var number)) number = minimum;
        return Math.Min(Math.Max(number, minimum), maximum);
    }
}
