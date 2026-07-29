using System.Text.Json.Nodes;

namespace Highlighter.Pipeline;

/// <summary>Port of highlighter_pipeline/captions.py.
///
/// Burned-in captions for short-form vertical clips.
///
/// pycaps renders styled word-timed captions onto a clip. The pipeline already
/// has word timings from transcription, so the transcript is handed over in
/// Whisper's JSON shape and pycaps never runs its own speech-to-text. pycaps is
/// an external CLI like ffmpeg: when it isn't installed the pipeline skips
/// captions with a log line and ships the clean vertical only.</summary>
public static class Captions
{
    // A silence this long starts a new caption segment.
    public const double CAPTION_SEGMENT_GAP_SECONDS = 1.5;

    public static bool CaptionsAvailable() => Proc.Which("pycaps") is not null;

    /// <summary>Caption styles ship with the pipeline package (found by walking up
    /// from the working directory); a template name that matches a directory there
    /// resolves to its absolute path, anything else passes through to pycaps.</summary>
    public static string ResolveCaptionTemplate(string template)
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName, "pipeline", "highlighter_pipeline",
                "caption_templates", template);
            if (File.Exists(Path.Combine(candidate, "pycaps.template.json")))
                return candidate;
            directory = directory.Parent!;
        }
        return template;
    }

    /// <summary>Map word timings onto Whisper's segments[].words[] JSON shape, with
    /// times relative to the clip. Returns null when no words fall in the clip.</summary>
    public static JsonObject? BuildWhisperTranscript(
        JsonArray words, double clipStartSeconds, double clipEndSeconds)
    {
        var clipWords = new List<JsonObject>();
        foreach (var node in words)
        {
            if (node is not JsonObject word) continue;
            var start = word.ContainsKey("absolute_start") ? word["absolute_start"] : word["start"];
            var end = word.ContainsKey("absolute_end") ? word["absolute_end"] : word["end"];
            if (!JsonUtil.TryDouble(start, out var startSeconds)) continue;
            if (!JsonUtil.TryDouble(end, out var endSeconds)) continue;
            if (endSeconds <= clipStartSeconds || startSeconds >= clipEndSeconds) continue;
            var text = JsonUtil.StrOrNull(word["punctuated_word"]) ?? JsonUtil.StrOrNull(word["word"]) ?? "";
            if (text.Length == 0) continue;
            clipWords.Add(new JsonObject
            {
                ["word"] = text,
                ["start"] = Py.Round(Math.Max(0.0, startSeconds - clipStartSeconds), 3),
                ["end"] = Py.Round(Math.Min(endSeconds, clipEndSeconds) - clipStartSeconds, 3),
            });
        }
        if (clipWords.Count == 0) return null;

        var segments = new List<List<JsonObject>>();
        var current = new List<JsonObject>();
        foreach (var word in clipWords)
        {
            if (current.Count > 0
                && JsonUtil.Double(word["start"]) - JsonUtil.Double(current[^1]["end"])
                    > CAPTION_SEGMENT_GAP_SECONDS)
            {
                segments.Add(current);
                current = new List<JsonObject>();
            }
            current.Add(word);
        }
        segments.Add(current);

        var segmentArray = new JsonArray();
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            var segmentWords = new JsonArray();
            foreach (var word in segment) segmentWords.Add(word);
            segmentArray.Add(new JsonObject
            {
                ["id"] = index,
                ["start"] = JsonUtil.C(segment[0]["start"]),
                ["end"] = JsonUtil.C(segment[^1]["end"]),
                ["text"] = string.Join(" ", segment.Select(word => JsonUtil.Str(word["word"]))),
                ["words"] = segmentWords,
            });
        }
        return new JsonObject { ["segments"] = segmentArray };
    }

    /// <summary>Render a captioned copy of a vertical clip. Best-effort: any failure
    /// logs one line and returns null so the clean vertical still ships.</summary>
    public static string? CaptionClip(
        string verticalPath,
        JsonArray words,
        double clipStartSeconds,
        double clipEndSeconds,
        string outputPath,
        string template = Defaults.DEFAULT_CAPTION_TEMPLATE)
    {
        var transcript = BuildWhisperTranscript(words, clipStartSeconds, clipEndSeconds);
        if (transcript is null) return null;
        try
        {
            var transcriptPath = Path.Combine(
                Path.GetTempPath(), $"captions-{Guid.NewGuid():N}.json");
            File.WriteAllText(transcriptPath, JsonUtil.Dumps(transcript));
            try
            {
                var (code, stdout, stderr) = Proc.Run(new List<string>
                {
                    "pycaps",
                    "render",
                    "--input",
                    verticalPath,
                    "--output",
                    outputPath,
                    "--template",
                    ResolveCaptionTemplate(template),
                    "--transcript",
                    transcriptPath,
                    "--transcript-format",
                    "whisper_json",
                });
                if (code != 0)
                {
                    var details = (string.IsNullOrEmpty(stderr) ? stdout : stderr).Trim();
                    throw new PipelineError(
                        details.Length > 0 ? details : $"Command failed with exit code {code}");
                }
            }
            finally
            {
                File.Delete(transcriptPath);
            }
            if (!File.Exists(outputPath))
                throw new PipelineError("pycaps produced no output file");
            return outputPath;
        }
        catch (Exception exc)
        {
            Console.WriteLine($"Caption render failed (non-fatal): {exc.Message}");
            return null;
        }
    }
}
