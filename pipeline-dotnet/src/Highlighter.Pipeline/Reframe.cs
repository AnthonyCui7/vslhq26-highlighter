using System.Text.Json.Nodes;

namespace Highlighter.Pipeline;

/// <summary>Port of highlighter_pipeline/reframe.py.
///
/// Auto-reframe rendered short-form clips to a 9:16 vertical.
///
/// One Gemini call per clip decides where the sharp region sits: the model sees a
/// sampled frame from each shot (plus the clip's opening frame), the scene-cut
/// timings, and the clip's editorial context, and returns a small set of
/// horizontal crop centers — a starting framing, then a new center whenever the
/// speaker or action moves. Spans that cannot be cropped honestly (side-by-side
/// call layouts, wide action) are flagged wide and show the whole 16:9 frame
/// fitted to the canvas width instead. Rendering is deterministic: a full-height
/// square crop at those centers (or the fitted wide frame) fills the width of a
/// 720x1280 canvas, with a blurred, darkened zoom-fill of the same frame above
/// and below. Framing is static between keyframes — hard cuts, no tracking.</summary>
public static class Reframe
{
    public const int CANVAS_WIDTH = 720;
    public const int CANVAS_HEIGHT = 1280;
    public const int BLUR_SIGMA = 25;
    // One frame per shot is plenty; more frames anchor worse, not better.
    public const int MAX_SAMPLE_FRAMES = 10;
    // Crop moves closer together than this read as jitter, not reframing.
    public const double MIN_KEYFRAME_SPACING_SECONDS = 1.5;
    public const double MIN_CENTER_DELTA = 0.03;
    // Framing is a perceptual where's-the-subject call; deep reasoning only adds
    // latency per clip.
    public const string REFRAME_REASONING_EFFORT = "low";

    public const string REFRAME_SYSTEM_PROMPT =
        """
        You are the framing director converting a 16:9 highlight clip into a vertical
        short. The vertical canvas shows a full-height square crop of the source at
        full canvas width; a blurred fill covers the rest. Your only decision is where
        that square sits horizontally over time.

        You get one sampled frame per shot (each labeled with its timestamp in seconds
        from the start of the clip), the clip's scene-cut timings, and editorial
        context about what happens in it. Return crop keyframes: a starting center_x
        at 0 seconds, then a new keyframe ONLY when the subject clearly sits somewhere
        else in the frame — typically because the shot changed. center_x is the
        horizontal center of the square as a fraction of the source width (0 = left
        edge, 0.5 = middle, 1 = right edge).

        Some shots cannot be cropped honestly: side-by-side call layouts with rapid
        back-and-forth dialogue, wide action involving several people at once, or
        graphics spanning the full frame. For those spans set wide to true — the whole
        16:9 frame is shown fitted to the canvas width instead of a crop. Wide is the
        honest fallback, not the default: use it when cropping would either lose the
        conversation or force constant jumping between speakers.

        Rules:
        - Frame what a viewer should watch: the person speaking over the person
          listening, faces over bodies, the action over the scenery.
        - Framing is static between keyframes and jumps at each keyframe. Never try to
          track or slide — a few well-placed static framings beat many small moves.
        - Add a keyframe only when staying put would leave the subject out of frame or
          clearly off-center. When one framing covers the whole clip, return just the
          starting keyframe.
        - Keyframes belong on shot changes (the scene-cut timings) unless the subject
          clearly moves within a shot.
        - Prefer one wide span over rapid crop-jumping between two speakers trading
          short lines.

        Return only JSON matching the schema.
        """;

    private const string REFRAME_RESPONSE_SCHEMA_JSON =
        """
        {
          "type": "object",
          "properties": {
            "keyframes": {
              "type": "array",
              "description": "Crop positions in clip order; the first starts at 0 seconds.",
              "items": {
                "type": "object",
                "properties": {
                  "start_seconds": {
                    "type": "number",
                    "description": "Clip-relative time this framing takes effect."
                  },
                  "center_x": {
                    "type": "number",
                    "description": "Horizontal center of the square crop as a fraction of source width. Use 0.5 when wide is true.",
                    "minimum": 0,
                    "maximum": 1
                  },
                  "wide": {
                    "type": "boolean",
                    "description": "True when this span cannot be cropped honestly: show the whole 16:9 frame fitted to the canvas width instead."
                  }
                },
                "required": ["start_seconds", "center_x", "wide"],
                "additionalProperties": false
              }
            },
            "notes": {
              "type": "string",
              "description": "One line on the framing choices."
            }
          },
          "required": ["keyframes", "notes"],
          "additionalProperties": false
        }
        """;

    public static JsonObject ReframeResponseSchema() =>
        JsonUtil.ParseObject(REFRAME_RESPONSE_SCHEMA_JSON);

    /// <summary>One Gemini call deciding the crop keyframes for a rendered clip.
    ///
    /// sceneCuts are clip-relative seconds. Returns {keyframes, notes, model}
    /// with keyframes validated (first at 0, sorted, de-jittered); throws on
    /// failure — the caller keeps the 16:9 clip.</summary>
    public static JsonObject PlanCropTrack(
        string clipPath,
        double clipDurationSeconds,
        IReadOnlyList<double> sceneCuts,
        string title,
        string description,
        JsonObject? researchContext = null,
        string model = Defaults.DEFAULT_OPENROUTER_MODEL)
    {
        var apiKey = Config.Env("OPENROUTER_API_KEY");
        if (string.IsNullOrEmpty(apiKey))
            throw new PipelineError("OPENROUTER_API_KEY is required for auto-reframing");

        var sampleTimes = SampleTimes(clipDurationSeconds, sceneCuts);
        var cutList = sceneCuts.Count > 0
            ? string.Join(", ", sceneCuts.Select(cut => Py.F(cut, 1)))
            : "none detected";
        var content = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = string.Join("\n", new[]
                {
                    $"Clip: {title}",
                    $"What happens: {description}",
                    $"Duration: {Py.F(clipDurationSeconds, 1)}s",
                    $"Scene cuts (clip-relative seconds): {cutList}",
                    "",
                    "Content research context:",
                    JsonUtil.DumpsIndented(researchContext ?? new JsonObject()),
                    "",
                    "Sampled frames follow, one per shot.",
                }),
            },
        };

        JsonObject response;
        using (var tmp = new TempDir(prefix: "reframe-"))
        {
            for (var index = 0; index < sampleTimes.Count; index++)
            {
                var atSeconds = sampleTimes[index];
                var framePath = Path.Combine(tmp.Path, $"frame_{index}.jpg");
                Render.ExtractThumbnail(
                    clipPath: clipPath, outputPath: framePath, atSeconds: atSeconds);
                var frameB64 = Convert.ToBase64String(File.ReadAllBytes(framePath));
                content.Add(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = $"Frame at {Py.F(atSeconds, 1)}s:",
                });
                content.Add(new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject
                    {
                        ["url"] = $"data:image/jpeg;base64,{frameB64}",
                    },
                });
            }

            var body = new JsonObject
            {
                ["model"] = model,
                ["messages"] = new JsonArray
                {
                    new JsonObject { ["role"] = "system", ["content"] = REFRAME_SYSTEM_PROMPT },
                    new JsonObject { ["role"] = "user", ["content"] = content },
                },
                ["temperature"] = 0,
                ["response_format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new JsonObject
                    {
                        ["name"] = "crop_track",
                        ["schema"] = ReframeResponseSchema(),
                    },
                },
                ["provider"] = new JsonObject
                {
                    ["order"] = new JsonArray(OpenRouterClient.OPENROUTER_VERTEX_PROVIDER),
                    ["allow_fallbacks"] = false,
                },
                ["reasoning"] = new JsonObject { ["effort"] = REFRAME_REASONING_EFFORT },
            };

            using var client = new OpenRouterClient(
                apiKey, timeoutSeconds: 120.0, title: "highlighter reframe");
            response = client.ChatCompletions(body);
        }

        var contentText = response["choices"] is JsonArray choices && choices.Count > 0
            ? JsonUtil.StrOrNull(choices[0]?["message"]?["content"])
            : null;
        if (string.IsNullOrEmpty(contentText))
            throw new PipelineError("Reframe response did not include content");
        var decision = Llm.JsonFromText(contentText);
        var keyframes = new JsonArray();
        foreach (var keyframe in ValidateKeyframes(decision["keyframes"], clipDurationSeconds))
            keyframes.Add(keyframe);
        return new JsonObject
        {
            ["keyframes"] = keyframes,
            ["notes"] = JsonUtil.Truthy(decision["notes"]) ? JsonUtil.Str(decision["notes"]) : "",
            ["model"] = model,
        };
    }

    /// <summary>One frame just after the clip start and just after each cut, capped at
    /// MAX_SAMPLE_FRAMES by evenly thinning the cut frames (the opener stays).</summary>
    public static List<double> SampleTimes(double durationSeconds, IReadOnlyList<double> sceneCuts)
    {
        var times = new List<double> { Math.Min(0.2, Math.Max(0.0, durationSeconds / 2)) };
        foreach (var cut in sceneCuts.OrderBy(cut => cut))
        {
            var atSeconds = cut + 0.3;
            if (0.5 < atSeconds && atSeconds < durationSeconds - 0.1)
                times.Add(Py.Round(atSeconds, 2));
        }
        if (times.Count > MAX_SAMPLE_FRAMES)
        {
            var rest = times.Skip(1).ToList();
            var step = (double)rest.Count / (MAX_SAMPLE_FRAMES - 1);
            var thinned = new List<double> { times[0] };
            for (var i = 0; i < MAX_SAMPLE_FRAMES - 1; i++)
                thinned.Add(rest[(int)(i * step)]);
            times = thinned;
        }
        return times;
    }

    /// <summary>Sorted, de-jittered keyframes with the first forced to 0 seconds.
    /// Falls back to a single centered framing when nothing usable comes back.</summary>
    public static List<JsonObject> ValidateKeyframes(JsonNode? raw, double durationSeconds)
    {
        var keyframes = new List<JsonObject>();
        foreach (var rawItem in raw as JsonArray ?? new JsonArray())
        {
            if (rawItem is not JsonObject item) continue;
            if (!JsonUtil.TryDouble(
                    item.TryGetPropertyValue("start_seconds", out var startNode) ? startNode : null,
                    out var start))
                continue;
            var wide = JsonUtil.Truthy(item.TryGetPropertyValue("wide", out var wideNode)
                ? wideNode
                : null);
            double center;
            if (wide)
            {
                center = 0.5;
            }
            else
            {
                if (!JsonUtil.TryDouble(
                        item.TryGetPropertyValue("center_x", out var centerNode) ? centerNode : null,
                        out var centerValue))
                    continue;
                center = Math.Min(1.0, Math.Max(0.0, centerValue));
            }
            if (start < durationSeconds)
            {
                keyframes.Add(new JsonObject
                {
                    ["start_seconds"] = Math.Max(0.0, Py.Round(start, 2)),
                    ["center_x"] = Py.Round(center, 3),
                    ["wide"] = wide,
                });
            }
        }

        keyframes = keyframes.OrderBy(k => JsonUtil.Double(k["start_seconds"])).ToList();
        if (keyframes.Count == 0)
        {
            return new List<JsonObject>
            {
                new() { ["start_seconds"] = 0.0, ["center_x"] = 0.5, ["wide"] = false },
            };
        }

        keyframes[0]["start_seconds"] = 0.0;
        var kept = new List<JsonObject> { keyframes[0] };
        foreach (var keyframe in keyframes.Skip(1))
        {
            var previous = kept[^1];
            if (JsonUtil.Double(keyframe["start_seconds"]) - JsonUtil.Double(previous["start_seconds"])
                < MIN_KEYFRAME_SPACING_SECONDS)
                continue;
            var keyframeWide = JsonUtil.Truthy(keyframe["wide"]);
            var previousWide = JsonUtil.Truthy(previous["wide"]);
            if (keyframeWide == previousWide && (
                    keyframeWide
                    || Math.Abs(JsonUtil.Double(keyframe["center_x"])
                        - JsonUtil.Double(previous["center_x"])) < MIN_CENTER_DELTA))
                continue;
            kept.Add(keyframe);
        }
        return kept;
    }

    /// <summary>Overlay enable expressions for the square-crop and wide framings, from
    /// the keyframes' spans (the last span runs to the end of the clip).</summary>
    public static (string CropEnable, string WideEnable) ModeEnables(
        IReadOnlyList<JsonObject> keyframes)
    {
        var cropSpans = new List<string>();
        var wideSpans = new List<string>();
        for (var i = 0; i < keyframes.Count; i++)
        {
            var keyframe = keyframes[i];
            var next = i + 1 < keyframes.Count ? keyframes[i + 1] : null;
            var end = next is null ? "1e9" : Py.G(JsonUtil.Double(next["start_seconds"]));
            var span = $"between(t,{Py.G(JsonUtil.Double(keyframe["start_seconds"]))},{end})";
            (JsonUtil.Truthy(keyframe.TryGetPropertyValue("wide", out var wide) ? wide : null)
                ? wideSpans
                : cropSpans).Add(span);
        }
        return (
            cropSpans.Count > 0 ? string.Join("+", cropSpans) : "0",
            wideSpans.Count > 0 ? string.Join("+", wideSpans) : "0");
    }

    /// <summary>Piecewise-constant ffmpeg crop x expression (pixels) for a full-height
    /// square crop. Positions are clamped so the square stays inside the frame.</summary>
    public static string CropXExpression(
        IReadOnlyList<JsonObject> keyframes, int sourceWidth, int sourceHeight)
    {
        var cropWidth = Math.Min(sourceWidth, sourceHeight);
        var maxX = sourceWidth - cropWidth;
        var positions = keyframes
            .Select(keyframe => Math.Min(maxX, Math.Max(0,
                (int)Math.Round(
                    JsonUtil.Double(keyframe["center_x"]) * sourceWidth - cropWidth / 2.0,
                    MidpointRounding.ToEven))))
            .ToList();
        var expression = positions[^1].ToString(System.Globalization.CultureInfo.InvariantCulture);
        for (var i = keyframes.Count - 2; i >= 0; i--)
        {
            var keyframe = keyframes[i + 1];
            var position = positions[i];
            expression =
                $"if(lt(t,{Py.G(JsonUtil.Double(keyframe["start_seconds"]))}),{position},{expression})";
        }
        return expression;
    }

    /// <summary>Render the blur-pad vertical: blurred zoom-fill canvas with the sharp
    /// framing overlaid at full width — the square crop jumping between keyframe
    /// positions, or the whole 16:9 frame fitted to the width on wide spans.</summary>
    public static void RenderVertical(
        string sourcePath, string outputPath, IReadOnlyList<JsonObject> keyframes)
    {
        var (sourceWidth, sourceHeight) = VideoDimensions(sourcePath);
        var cropSize = Math.Min(sourceWidth, sourceHeight);
        var xExpression = CropXExpression(
            keyframes, sourceWidth: sourceWidth, sourceHeight: sourceHeight);
        var (cropEnable, wideEnable) = ModeEnables(keyframes);
        var filtergraph =
            "[0:v]split=3[bg][fgc][fgw];"
            + $"[bg]scale={CANVAS_WIDTH}:{CANVAS_HEIGHT}:force_original_aspect_ratio=increase,"
            + $"crop={CANVAS_WIDTH}:{CANVAS_HEIGHT},gblur=sigma={BLUR_SIGMA},eq=brightness=-0.06[b];"
            + $"[fgc]crop=w={cropSize}:h={cropSize}:x='{xExpression}':y=0,"
            + $"scale={CANVAS_WIDTH}:{CANVAS_WIDTH}[fc];"
            + $"[fgw]scale={CANVAS_WIDTH}:-2[fw];"
            + $"[b][fc]overlay=0:(H-h)/2:enable='{cropEnable}'[bc];"
            + $"[bc][fw]overlay=0:(H-h)/2:enable='{wideEnable}'[v]";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        var (code, _, stderr) = Proc.Run(new List<string>
        {
            "ffmpeg",
            "-hide_banner",
            "-loglevel",
            "error",
            "-y",
            "-i",
            sourcePath,
            "-filter_complex",
            filtergraph,
            "-map",
            "[v]",
            "-map",
            "0:a?",
            "-c:v",
            "libx264",
            "-preset",
            "veryfast",
            "-crf",
            "30",
            "-c:a",
            "copy",
            "-movflags",
            "+faststart",
            outputPath,
        });
        if (code != 0)
        {
            var details = stderr.Trim();
            throw new PipelineError(
                details.Length > 0 ? details : "ffmpeg failed while rendering the vertical clip");
        }
    }

    public static (int Width, int Height) VideoDimensions(string path)
    {
        var (code, stdout, stderr) = Proc.Run(new List<string>
        {
            "ffprobe",
            "-v",
            "error",
            "-select_streams",
            "v:0",
            "-show_entries",
            "stream=width,height",
            "-of",
            "csv=p=0",
            path,
        });
        var firstLine = stdout.Trim().Split('\n').FirstOrDefault() ?? "";
        var parts = firstLine.Split(',');
        if (parts.Length == 2
            && int.TryParse(parts[0], out var width)
            && int.TryParse(parts[1], out var height))
            return (width, height);
        var details = stderr.Trim();
        throw new PipelineError(
            details.Length > 0 ? details : $"Could not read video dimensions from {path}");
    }
}
