using System.Text.Json.Nodes;

namespace Highlighter.Pipeline;

/// <summary>Port of highlighter_pipeline/llm.py: the clip-scoring prompts and
/// the audio scoring call on the audio provider chain.</summary>
public static class Llm
{
    public const string CLIPPER_AUDIO_BITRATE = "32k";
    public const string CLIPPER_AUDIO_SAMPLE_RATE = "16000";

    public const string SHORTFORM_SYSTEM_PROMPT =
        """
        You are the lead short-form editor for a professional clipping team.

        Your job is to find only the moments that are actually worth clipping for
        TikTok, Reels, Shorts, or X. You are not summarizing the source. You are making
        hard editorial decisions for cold viewers with short attention spans.

        Decision hierarchy:
        1. Transcript evidence is the source of truth for the spoken content.
        2. Audio is the source of truth for delivery: energy, pauses, laughter,
           awkwardness, yelling, surprise, timing, reactions, and the moment the hook
           actually starts.
        3. Content research is only an audience-context layer: creator lore,
           current discourse, category norms, common clip formats, audience interests,
           and platform-specific patterns.
        4. Research can raise or lower confidence only when the transcript/audio already has a
           real moment. It must never turn ordinary filler into a clip.

        Selection standard:
        You may return multiple distinct clips per chunk. Usually there are zero,
        sometimes one, occasionally more. Most transcript chunks are not clip-worthy.
        The bar per clip does not drop when a chunk is busy: every clip must clear the
        same standard on its own. A clip-worthy moment should create a reason to keep
        watching almost immediately: curiosity, tension, conflict, surprise, confusion,
        a sharp joke, a strong reaction, a reversal, or a clear payoff. The moment
        should be understandable to a cold viewer with minimal setup.

        Reject chunks that are mainly setup, logistics, name-reading, repeated lines,
        dead air, weak banter, generic gameplay commentary, or creator-only context.
        Also reject moments that are merely "on topic" with the research but do not have
        a hook and payoff in the transcript/audio itself. When nothing qualifies, return
        an empty clips list and explain why in chunk_assessment.

        Windows and timing:
        The timestamp markers, transcript, and attached audio cover the visible window
        [visible_start, visible_end], which includes context from neighboring chunks.
        The chunk window [start, end] is your focus. A clip may extend up to the visible
        bounds when the moment genuinely spills across the chunk boundary, but the core
        hook and payoff must lie mainly inside the chunk window: neighboring chunks are
        scored separately, and overlapping proposals are merged.

        NEVER cut a speaker off mid-sentence or mid-thought. Start the clip at the
        natural beginning of the sentence, sound, pause, or reaction that carries the
        hook, and end it only after the thought or payoff is complete. Prefer extending
        a boundary slightly over clipping a word in half.

        Scene cuts: when the prompt lists scene-cut timestamps, they mark the source's
        own visual transitions. Prefer starting or ending a clip on or just after a cut
        when one lies near the natural spoken boundary, and never place a boundary in
        the middle of a transition. A complete spoken thought still takes priority over
        cut alignment.

        If selecting a clip:
        - Choose one sharp moment per clip, not the whole scene.
        - Start as close as possible to the first audible hook.
        - Include only the context needed for the viewer to understand the payoff.
        - End right after the payoff, reaction, twist, or escalation.
        - Do not pad the window because extra transcript is available.
        - Prefer a tighter, cleaner cut over a longer explanatory cut.
        - Do not default to 60-second clips. Duration is dynamic, but weak setup and
          dead air should almost always be excluded.

        Use research carefully:
        - Use it to recognize creator-specific references, currently relevant topics,
          and platform-native clip patterns.
        - Do not invent visuals, chat messages, gameplay events, or facts not present in
          the transcript.
        - Any outside-world claim in a title, description, or reason must be backed by
          a URL returned in that clip's research_sources.
        - If no research source materially helped a decision, return its
          research_sources as [].

        Return only JSON matching the schema. Prefer no clip over a weak clip.
        """;

    public const string LONGFORM_SYSTEM_PROMPT =
        """
        You are the lead editor cutting one long-form video from a full-length source
        (a VOD, podcast, or stream recording). The final video is assembled by
        concatenating, in chronological order, the segments you and your co-editors
        select across the whole source. You are seeing one chunk at a time; select the
        segments from this chunk that deserve a place in the final edit.

        Decision hierarchy:
        1. Transcript evidence is the source of truth for the spoken content.
        2. Audio is the source of truth for delivery: energy, pauses, laughter,
           awkwardness, timing, reactions, and where a passage actually begins and ends.
        3. Content research is only an audience-context layer: creator lore, current
           discourse, category norms, audience interests, and platform patterns.
        4. Research can raise or lower confidence only when the transcript/audio already
           has real substance. It must never turn filler into a keeper.

        Selection standard:
        Keep what a good human editor would keep in a tight edit of this source: complete
        stories, strong arguments, genuinely informative or entertaining passages, big
        reactions with their setup and payoff, and the connective moments a viewer needs
        to follow along. Cut filler, dead air, logistics, repeated lines, technical
        difficulties, and meandering that goes nowhere. A kept passage should assemble
        into a self-contained thought — typically from ~30 seconds up to several
        minutes — and a chunk may contribute zero, one, or several segments.

        Edit at two scales. Beyond choosing the passages, make the small cuts a human
        editor makes inside them: when a kept passage contains a cough, a sneeze, a
        false start, a restart, a long dead pause, or a filler run ("um, so, yeah,
        anyway"), return the passage as multiple adjacent segments that skip the junk
        instead of one segment that includes it. Consecutive segments are butt-spliced
        in the final video — a jump cut, which is standard in edited long-form content.
        Parts of a split passage may be short; the duration guidance above applies to
        the assembled passage, not to each part. Only cut what a viewer is glad to
        lose: never sacrifice the natural rhythm of speech for density.

        Target runtime:
        The final edit is aiming for roughly {target_length} minutes in total across all
        selections from the whole source. This is guidance, not a restriction: take the
        time the content deserves. A dense source can justify running long; a thin one
        should come out short rather than padded. Since you see one chunk at a time,
        apply the target as a bar for selectivity — keep roughly the fraction of
        material that would produce an edit of that scale — not as a quota to fill.{budget_calibration}

        Windows and timing:
        The timestamp markers, transcript, and attached audio cover the visible window
        [visible_start, visible_end], which includes context from neighboring chunks.
        The chunk window [start, end] is your focus. A segment may extend up to the
        visible bounds when it genuinely spills across the chunk boundary; neighboring
        chunks are edited separately, and selections that overlap or nearly touch
        (within about a second) are merged, so a passage larger than one chunk still
        comes together in the final cut. Segments you deliberately separated by a
        wider gap stay separate.

        NEVER cut a speaker off mid-sentence or mid-thought. Start each segment at the
        natural beginning of the sentence or beat that opens it, and end only after the
        thought is complete. Prefer extending a boundary slightly over clipping a word
        in half.

        Scene cuts: when the prompt lists scene-cut timestamps, they mark the source's
        own visual transitions. Prefer starting or ending a segment on or just after a
        cut when one lies near the natural spoken boundary, and never place a boundary
        in the middle of a transition. A complete spoken thought still takes priority
        over cut alignment.

        Use research carefully:
        - Use it to recognize creator-specific references and what this audience values.
        - Do not invent visuals, chat messages, or facts not present in the transcript.
        - Any outside-world claim in a title, description, or reason must be backed by a
          URL returned in that segment's research_sources; return [] when none was used.

        Return only JSON matching the schema. In it, "clips" means the segments you are
        keeping for the final edit, and "score" (0.0-1.0) means how essential the
        segment is to that edit. Prefer a coherent, watchable cut over hitting a number.
        """;

    private const string CLIP_RESPONSE_SCHEMA_JSON =
        """
        {
          "type": "object",
          "properties": {
            "clips": {
              "type": "array",
              "description": "Every distinct clip-worthy moment in this chunk. Usually empty; sometimes one entry; occasionally more.",
              "items": {
                "type": "object",
                "properties": {
                  "title": {
                    "type": "string",
                    "description": "Short working title for the clip."
                  },
                  "description": {
                    "type": "string",
                    "description": "One sentence explaining what happens in the clip."
                  },
                  "start_seconds": {
                    "type": "number",
                    "description": "Absolute source timestamp where the clip should start, at the natural beginning of the sentence or thought that carries the hook."
                  },
                  "end_seconds": {
                    "type": "number",
                    "description": "Absolute source timestamp where the clip should end, only after the thought or payoff is complete."
                  },
                  "score": {
                    "type": "number",
                    "description": "Clip strength from 0.0 to 1.0.",
                    "minimum": 0,
                    "maximum": 1
                  },
                  "reason": {
                    "type": "string",
                    "description": "Brief reason this moment clears the bar."
                  },
                  "research_sources": {
                    "type": "array",
                    "description": "Source URLs used for external content research context. Empty when no external context was used.",
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
                  "title",
                  "description",
                  "start_seconds",
                  "end_seconds",
                  "score",
                  "reason",
                  "research_sources"
                ],
                "additionalProperties": false
              }
            },
            "chunk_assessment": {
              "type": "string",
              "description": "One-line editorial assessment of the chunk; when clips is empty, why nothing qualified."
            }
          },
          "required": ["clips", "chunk_assessment"],
          "additionalProperties": false
        }
        """;

    public static JsonObject ClipResponseSchema() => JsonUtil.ParseObject(CLIP_RESPONSE_SCHEMA_JSON);

    /// <summary>The editor system prompt for a pipeline mode ('short' or 'long').</summary>
    public static string SystemPrompt(
        string pipelineMode, string? targetLength = null, double? sourceMinutes = null)
    {
        if (pipelineMode == "long")
        {
            var target = string.IsNullOrEmpty(targetLength)
                ? Defaults.DEFAULT_TARGET_LENGTH_MINUTES
                : targetLength;
            return LONGFORM_SYSTEM_PROMPT
                .Replace("{target_length}", target)
                .Replace("{budget_calibration}", BudgetCalibration(target, sourceMinutes));
        }
        return SHORTFORM_SYSTEM_PROMPT;
    }

    /// <summary>Parse a target-runtime string ('10' or '7-15') into (low, high) minutes.</summary>
    public static (double Low, double High)? ParseTargetMinutes(string? target)
    {
        if (string.IsNullOrEmpty(target)) return null;
        var parts = target.Split('-').Select(part => part.Trim());
        var numbers = new List<double>();
        foreach (var part in parts.Where(part => part.Length > 0))
        {
            if (!double.TryParse(part, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var number))
                return null;
            numbers.Add(number);
        }
        if (numbers.Count == 1) return numbers[0] > 0 ? (numbers[0], numbers[0]) : null;
        if (numbers.Count == 2 && 0 < numbers[0] && numbers[0] <= numbers[1])
            return (numbers[0], numbers[1]);
        return null;
    }

    /// <summary>Precomputed keep-rate sentence for the long-form prompt; empty when the
    /// source length is unknown (livestreams) or the target does not parse.</summary>
    private static string BudgetCalibration(string target, double? sourceMinutes)
    {
        var bounds = ParseTargetMinutes(target);
        if (bounds is null || sourceMinutes is not double minutes || minutes <= 0) return "";
        var midpoint = (bounds.Value.Low + bounds.Value.High) / 2;
        var keepPercent = Math.Max(1, Math.Min(100,
            (int)Math.Round(100 * midpoint / minutes, MidpointRounding.ToEven)));
        return
            $"\nConcretely: this source runs roughly {Py.F(minutes, 0)} minutes, so a"
            + $" {target}-minute edit keeps roughly {keepPercent}% of it. Hold every"
            + " selection to that level of selectivity.";
    }

    /// <summary>Score one chunk for clip candidates.
    ///
    /// Returns (candidates, chunkAssessment): a list of normalized candidate
    /// decision dicts (possibly empty — most chunks have no clip) plus the model's
    /// one-line assessment of the chunk, which callers use as the reason for a
    /// no-clip record when the list is empty. The transcript visible to the model
    /// spans [visibleStartSeconds, visibleEndSeconds] — the chunk window plus
    /// a little context from the neighboring chunks — and candidates are clamped
    /// to that visible window.</summary>
    public static (List<JsonObject> Candidates, string Assessment) DetectClipCandidates(
        string transcript,
        IReadOnlyList<JsonObject> words,
        int chunkIndex,
        int startSeconds,
        int endSeconds,
        int? visibleStartSeconds = null,
        int? visibleEndSeconds = null,
        IReadOnlyList<JsonObject>? contextWordsBefore = null,
        IReadOnlyList<JsonObject>? contextWordsAfter = null,
        string reasoningEffort = Defaults.DEFAULT_LLM_REASONING_EFFORT,
        int markerSeconds = Defaults.DEFAULT_LLM_MARKER_SECONDS,
        string pipelineMode = "short",
        string? userInstructions = null,
        string? targetLength = null,
        JsonObject? sourceContext = null,
        JsonObject? researchContext = null,
        IReadOnlyList<JsonObject>? audioContext = null,
        List<double>? sceneCuts = null,
        double? sourceMinutes = null)
    {
        var visibleStart = visibleStartSeconds ?? startSeconds;
        var visibleEnd = visibleEndSeconds ?? endSeconds;

        var providers = Providers.AudioProviders(
            title: "highlighter pipeline",
            openrouterReasoningEffort: string.IsNullOrEmpty(reasoningEffort)
                ? Defaults.DEFAULT_LLM_REASONING_EFFORT
                : reasoningEffort);
        var (decision, provider) = Providers.RunWithFallback(
            providers,
            candidateProvider => RequestClipCandidates(
                provider: candidateProvider,
                transcript: transcript,
                words: words,
                chunkIndex: chunkIndex,
                startSeconds: startSeconds,
                endSeconds: endSeconds,
                visibleStartSeconds: visibleStart,
                visibleEndSeconds: visibleEnd,
                contextWordsBefore: contextWordsBefore ?? new List<JsonObject>(),
                contextWordsAfter: contextWordsAfter ?? new List<JsonObject>(),
                markerSeconds: markerSeconds,
                pipelineMode: pipelineMode,
                userInstructions: userInstructions,
                targetLength: targetLength,
                researchContext: researchContext,
                audioContext: audioContext ?? new List<JsonObject>(),
                sceneCuts: sceneCuts,
                sourceMinutes: sourceMinutes));
        return NormalizeCandidates(
            decision,
            chunkIndex: chunkIndex,
            chunkStartSeconds: startSeconds,
            chunkEndSeconds: endSeconds,
            visibleStartSeconds: visibleStart,
            visibleEndSeconds: visibleEnd,
            model: provider.Model,
            reasoningEffort: string.IsNullOrEmpty(reasoningEffort)
                ? Defaults.DEFAULT_LLM_REASONING_EFFORT
                : reasoningEffort);
    }

    private static JsonObject RequestClipCandidates(
        ChatProvider provider,
        string transcript,
        IReadOnlyList<JsonObject> words,
        int chunkIndex,
        int startSeconds,
        int endSeconds,
        int visibleStartSeconds,
        int visibleEndSeconds,
        IReadOnlyList<JsonObject> contextWordsBefore,
        IReadOnlyList<JsonObject> contextWordsAfter,
        int markerSeconds,
        string pipelineMode,
        string? userInstructions,
        string? targetLength,
        JsonObject? researchContext,
        IReadOnlyList<JsonObject> audioContext,
        List<double>? sceneCuts,
        double? sourceMinutes)
    {
        var userPrompt = BuildUserPrompt(
            transcript: transcript,
            words: words,
            chunkIndex: chunkIndex,
            startSeconds: startSeconds,
            endSeconds: endSeconds,
            visibleStartSeconds: visibleStartSeconds,
            visibleEndSeconds: visibleEndSeconds,
            contextWordsBefore: contextWordsBefore,
            contextWordsAfter: contextWordsAfter,
            markerSeconds: markerSeconds,
            userInstructions: userInstructions,
            researchContext: researchContext,
            sceneCuts: sceneCuts);
        if (!provider.SupportsJsonSchema)
        {
            userPrompt +=
                "\n\nReturn ONLY a JSON object matching this schema, with no markdown fences:\n"
                + JsonUtil.Dumps(ClipResponseSchema());
        }
        var userContent = new JsonArray
        {
            new JsonObject { ["type"] = "text", ["text"] = userPrompt },
        };
        var audioB64 = AudioContextBase64(
            audioContext: audioContext,
            visibleStartSeconds: visibleStartSeconds,
            visibleEndSeconds: visibleEndSeconds);
        if (audioB64 is not null)
        {
            userContent.Add(new JsonObject
            {
                ["type"] = "input_audio",
                ["input_audio"] = new JsonObject { ["data"] = audioB64, ["format"] = "mp3" },
            });
        }

        var body = new JsonObject
        {
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "system",
                    ["content"] = SystemPrompt(
                        pipelineMode, targetLength: targetLength, sourceMinutes: sourceMinutes),
                },
                new JsonObject { ["role"] = "user", ["content"] = userContent },
            },
            ["user"] = $"chunk-{chunkIndex}",
        };
        if (provider.SupportsJsonSchema)
        {
            body["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = "clip_candidates",
                    ["schema"] = ClipResponseSchema(),
                },
            };
        }
        provider.ApplyRequestOptions(body);

        JsonObject response;
        try
        {
            using var client = provider.Client(timeoutSeconds: 240.0);
            response = client.ChatCompletions(body);
        }
        catch (ChatCompletionsStatusException exc)
        {
            var text = exc.ResponseText;
            throw new PipelineError(
                $"{provider.Label} request failed: {text[..Math.Min(4000, text.Length)]}", exc);
        }

        if (response["choices"] is not JsonArray choices || choices.Count == 0)
            throw new PipelineError($"{provider.Label} response did not include choices");
        var content = JsonUtil.StrOrNull(choices[0]?["message"]?["content"]);
        if (string.IsNullOrEmpty(content))
            throw new PipelineError($"{provider.Label} response did not include text content");
        return JsonFromText(content);
    }

    private static string? AudioContextBase64(
        IReadOnlyList<JsonObject> audioContext,
        double visibleStartSeconds,
        double visibleEndSeconds)
    {
        if (audioContext.Count == 0) return null;
        var mp3 = EncodeAudioContextMp3(
            audioContext: audioContext,
            visibleStartSeconds: visibleStartSeconds,
            visibleEndSeconds: visibleEndSeconds);
        return Convert.ToBase64String(mp3);
    }

    public static byte[] EncodeAudioContextMp3(
        IReadOnlyList<JsonObject> audioContext,
        double visibleStartSeconds,
        double visibleEndSeconds)
    {
        var segments = audioContext
            .Where(segment => JsonUtil.Truthy(
                segment.TryGetPropertyValue("path", out var path) ? path : null))
            .Select(segment => (
                Path: JsonUtil.Str(segment["path"]),
                StartSeconds: JsonUtil.Double(segment["start_seconds"]),
                EndSeconds: JsonUtil.Double(segment["end_seconds"])))
            .ToList();
        if (segments.Count == 0)
            throw new PipelineError("No audio segments were available for Gemini scoring");
        segments.Sort((a, b) => a.StartSeconds.CompareTo(b.StartSeconds));
        var sourceStart = segments[0].StartSeconds;
        var offset = Math.Max(0.0, visibleStartSeconds - sourceStart);
        var duration = Math.Max(0.1, visibleEndSeconds - visibleStartSeconds);

        using var tmp = new TempDir(prefix: "clip-audio-");
        var outputPath = Path.Combine(tmp.Path, "context.mp3");
        List<string> inputArgs;
        if (segments.Count == 1)
        {
            inputArgs = new List<string> { "-i", segments[0].Path };
        }
        else
        {
            var concatFile = Path.Combine(tmp.Path, "inputs.txt");
            File.WriteAllText(concatFile, string.Concat(
                segments.Select(segment => $"file '{FfmpegConcatPath(segment.Path)}'\n")));
            inputArgs = new List<string> { "-f", "concat", "-safe", "0", "-i", concatFile };
        }

        var command = new List<string>
        {
            "ffmpeg",
            "-hide_banner",
            "-loglevel",
            "error",
            "-y",
        };
        command.AddRange(inputArgs);
        command.AddRange(new[]
        {
            "-ss",
            Py.F(offset, 3),
            "-t",
            Py.F(duration, 3),
            "-vn",
            "-ac",
            "1",
            "-ar",
            CLIPPER_AUDIO_SAMPLE_RATE,
            "-c:a",
            "libmp3lame",
            "-b:a",
            CLIPPER_AUDIO_BITRATE,
            outputPath,
        });
        var (code, _, stderr) = Proc.Run(command);
        if (code != 0)
        {
            var details = stderr.Trim();
            throw new PipelineError(
                details.Length > 0 ? details : "ffmpeg failed while encoding clip audio");
        }
        return File.ReadAllBytes(outputPath);
    }

    public static string FfmpegConcatPath(string path) =>
        Path.GetFullPath(path).Replace("'", "'\\''");

    public static JsonObject JsonFromText(string text)
    {
        var stripped = text.Trim();
        if (stripped.StartsWith("```"))
        {
            stripped = stripped.Trim('`').Trim();
            if (stripped.StartsWith("json")) stripped = stripped[4..].Trim();
        }
        return JsonUtil.ParseObject(stripped);
    }

    public static string BuildUserPrompt(
        string transcript,
        IReadOnlyList<JsonObject> words,
        int chunkIndex,
        int startSeconds,
        int endSeconds,
        int visibleStartSeconds,
        int visibleEndSeconds,
        IReadOnlyList<JsonObject> contextWordsBefore,
        IReadOnlyList<JsonObject> contextWordsAfter,
        int markerSeconds,
        string? userInstructions = null,
        JsonObject? researchContext = null,
        List<double>? sceneCuts = null)
    {
        var visibleWords = contextWordsBefore.Concat(words).Concat(contextWordsAfter).ToList();
        var markers = TimestampMarkers(
            words: visibleWords,
            startSeconds: visibleStartSeconds,
            endSeconds: visibleEndSeconds,
            markerSeconds: markerSeconds);
        var beforeText = string.Join(" ",
            contextWordsBefore.Select(WordText).Where(part => part.Length > 0));
        var afterText = string.Join(" ",
            contextWordsAfter.Select(WordText).Where(part => part.Length > 0));
        var instructionsBlock = !string.IsNullOrEmpty(userInstructions)
            ? new List<string>
            {
                "User instructions (editorial guidance from the human in the loop;",
                "follow the spirit, but they are not rigid constraints):",
                userInstructions,
                "",
            }
            : new List<string>();
        List<string> cutLines;
        if (sceneCuts is null)
            cutLines = new List<string>();
        else if (sceneCuts.Count > 0)
            cutLines = new List<string>
            {
                "Scene cuts in the visible window (absolute seconds): "
                + string.Join(", ", sceneCuts.Select(cut => Py.F(cut, 1))),
            };
        else
            cutLines = new List<string> { "Scene cuts in the visible window: none detected." };

        var lines = new List<string>
        {
            $"Chunk index: {chunkIndex}",
            $"Absolute chunk window (focus): {startSeconds}s to {endSeconds}s",
            $"Visible window (markers and transcript below): {visibleStartSeconds}s to {visibleEndSeconds}s",
            "Attached audio covers the same visible window when available.",
        };
        lines.AddRange(cutLines);
        lines.Add("");
        lines.AddRange(instructionsBlock);
        lines.AddRange(new[]
        {
            "Timestamp markers:",
            markers.Length > 0 ? markers : "(No word-level markers available.)",
            "",
            "Content research context:",
            JsonUtil.DumpsIndented(researchContext ?? new JsonObject()),
            "",
            "Context before chunk:",
            beforeText.Length > 0 ? beforeText : "(No earlier context.)",
            "",
            "Chunk transcript:",
            transcript.Length > 0 ? transcript : "(No transcript text.)",
            "",
            "Context after chunk:",
            afterText.Length > 0 ? afterText : "(No later context.)",
        });
        return string.Join("\n", lines);
    }

    public static string TimestampMarkers(
        IReadOnlyList<JsonObject> words, int startSeconds, int endSeconds, int markerSeconds)
    {
        if (markerSeconds <= 0) markerSeconds = Defaults.DEFAULT_LLM_MARKER_SECONDS;

        var lines = new List<string>();
        var current = startSeconds;
        while (current < endSeconds)
        {
            var nextMark = Math.Min(current + markerSeconds, endSeconds);
            var markerWords = words
                .Where(word =>
                {
                    var start = WordAbsoluteStart(word);
                    return current <= start && start < nextMark;
                })
                .Select(WordText);
            var snippet = string.Join(" ", markerWords.Where(part => part.Length > 0)).Trim();
            if (snippet.Length > 0)
                lines.Add($"{current}s: {snippet[..Math.Min(240, snippet.Length)]}");
            current = nextMark;
        }
        return string.Join("\n", lines);
    }

    private static double WordAbsoluteStart(JsonObject word) =>
        word.TryGetPropertyValue("absolute_start", out var absolute)
            ? JsonUtil.Double(absolute)
            : word.TryGetPropertyValue("start", out var start)
                ? JsonUtil.Double(start)
                : 0;

    public static string WordText(JsonObject word)
    {
        var punctuated = word.TryGetPropertyValue("punctuated_word", out var p) ? p : null;
        var text = JsonUtil.Truthy(punctuated)
            ? JsonUtil.Str(punctuated)
            : JsonUtil.Truthy(word.TryGetPropertyValue("word", out var w) ? w : null)
                ? JsonUtil.Str(word["word"])
                : "";
        return text.Trim();
    }

    /// <summary>Normalize a multi-clip model response into per-candidate decision dicts.
    ///
    /// Candidates are clamped to the visible window (chunk window plus context
    /// margins). Candidates with a non-positive duration after clamping are
    /// dropped, as are candidates that do not intersect the chunk window at all —
    /// a clip living entirely in a context margin belongs to the neighbor chunk.</summary>
    public static (List<JsonObject> Candidates, string Assessment) NormalizeCandidates(
        JsonObject decision,
        int chunkIndex,
        int chunkStartSeconds,
        int chunkEndSeconds,
        int visibleStartSeconds,
        int visibleEndSeconds,
        string model,
        string reasoningEffort)
    {
        var rawClips = decision["clips"] is JsonArray clipsArray
            ? clipsArray.ToList()
            : new List<JsonNode?>();
        var chunkAssessment = JsonUtil.Truthy(decision["chunk_assessment"])
            ? JsonUtil.Str(decision["chunk_assessment"])
            : "";

        var candidates = new List<JsonObject>();
        foreach (var rawItem in rawClips)
        {
            if (rawItem is not JsonObject item) continue;
            var clipStart = ClampFloat(item["start_seconds"], visibleStartSeconds, visibleEndSeconds);
            var clipEnd = ClampFloat(item["end_seconds"], visibleStartSeconds, visibleEndSeconds);
            if (clipEnd <= clipStart) continue;
            if (clipEnd <= chunkStartSeconds || clipStart >= chunkEndSeconds) continue;

            var reason = item.TryGetPropertyValue("reason", out var reasonNode)
                ? JsonUtil.Str(reasonNode)
                : "";
            candidates.Add(new JsonObject
            {
                ["chunk_index"] = chunkIndex,
                ["is_clip_worthy"] = true,
                ["title"] = JsonUtil.Truthy(item["title"])
                    ? JsonUtil.Str(item["title"])
                    : $"Clip candidate {chunkIndex}",
                ["description"] = JsonUtil.Truthy(item["description"])
                    ? JsonUtil.Str(item["description"])
                    : reason,
                ["start_seconds"] = Py.Round(clipStart, 3),
                ["end_seconds"] = Py.Round(clipEnd, 3),
                ["score"] = ClampFloat(
                    item.TryGetPropertyValue("score", out var score) ? score : 0, 0, 1),
                ["reason"] = reason,
                ["research_sources"] = item["research_sources"] is JsonArray sources
                    ? (JsonArray)sources.DeepClone()
                    : new JsonArray(),
                ["model"] = model,
                ["reasoning_effort"] = reasoningEffort,
                ["raw_decision"] = item.DeepClone(),
            });
        }

        return (candidates, chunkAssessment);
    }

    public static double ClampFloat(JsonNode? value, double minimum, double maximum)
    {
        if (!JsonUtil.TryDouble(value, out var number)) number = minimum;
        return Math.Min(Math.Max(number, minimum), maximum);
    }
}
