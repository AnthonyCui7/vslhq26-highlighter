using System.Text.Json;
using System.Text.Json.Nodes;
using Highlighter.Api.Contracts;

namespace Highlighter.Api.Services;

/// <summary>Pure EDL helpers: defaults, transcript-word caption seeding,
/// validation, source→output time mapping, and (de)serialization for the
/// metadata jsonb columns the documents persist in.</summary>
public static class EditorDocs
{
    public const int Version = 1;
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    public static readonly string[] CaptionStyles = ["boxed", "plain", "karaoke"];

    public static EditorDoc Default(double sourceDuration, IReadOnlyList<EdlCaption> captions) =>
        new(Version,
            Segments: [new EdlSegment("s1", 0, Math.Max(0.04, sourceDuration))],
            Captions: captions,
            CaptionStyle: "boxed",
            Texts: [],
            Markers: [],
            Transform: new EdlTransform(),
            Audio: new EdlAudio(),
            Reframe: "auto");

    /// <summary>Caption lines from transcript words whose ABSOLUTE timings fall
    /// inside [windowStart, windowEnd] on the source clock; emitted times are
    /// re-based to the edited file (t=0 at windowStart). Lines break on speech
    /// gaps, word count, or line duration — matching short-form caption pacing.</summary>
    public static List<EdlCaption> SeedCaptions(
        IEnumerable<JsonObject> transcriptChunks, double windowStart, double windowEnd)
    {
        var words = new List<(string Text, double Start, double End)>();
        foreach (var chunk in transcriptChunks)
        {
            foreach (var node in chunk["words"] as JsonArray ?? [])
            {
                if (node is not JsonObject word) continue;
                var start = Dbl(word["absolute_start"]) ?? Dbl(word["start"]);
                var end = Dbl(word["absolute_end"]) ?? Dbl(word["end"]);
                var text = word["punctuated_word"]?.GetValue<string>()
                    ?? word["word"]?.GetValue<string>();
                if (start is null || end is null || string.IsNullOrWhiteSpace(text)) continue;
                if (end <= windowStart || start >= windowEnd) continue;
                words.Add((text!, Math.Max(start.Value, windowStart), Math.Min(end.Value, windowEnd)));
            }
        }
        words.Sort((a, b) => a.Start.CompareTo(b.Start));

        var captions = new List<EdlCaption>();
        var line = new List<(string Text, double Start, double End)>();
        void Flush()
        {
            if (line.Count == 0) return;
            var index = captions.Count + 1;
            captions.Add(new EdlCaption(
                $"c{index}",
                Math.Round(line[0].Start - windowStart, 3),
                Math.Round(line[^1].End - windowStart, 3),
                string.Join(' ', line.Select(w => w.Text)),
                line.Select(w => new EdlWord(w.Text,
                    Math.Round(w.Start - windowStart, 3),
                    Math.Round(w.End - windowStart, 3))).ToList()));
            line.Clear();
        }

        foreach (var word in words)
        {
            if (line.Count > 0)
            {
                var gap = word.Start - line[^1].End;
                var span = word.End - line[0].Start;
                if (gap > 0.6 || line.Count >= 6 || span > 3.5) Flush();
            }
            line.Add(word);
        }
        Flush();
        return captions;
    }

    /// <summary>Null when valid, else a caller-facing error message.</summary>
    public static string? Validate(EditorDoc doc, double sourceDuration)
    {
        if (doc.Segments is not { Count: > 0 })
            return "the timeline needs at least one segment";
        var previousEnd = -1e-3;
        foreach (var segment in doc.Segments)
        {
            if (segment.SrcStart < -1e-3 || segment.SrcEnd <= segment.SrcStart)
                return $"segment {segment.Id}: start must be >= 0 and before end";
            if (sourceDuration > 0 && segment.SrcEnd > sourceDuration + 0.5)
                return $"segment {segment.Id}: ends past the end of the source";
            if (segment.SrcStart < previousEnd - 1e-3)
                return $"segment {segment.Id}: segments must be chronological and non-overlapping";
            if (segment.Speed is < 0.5 or > 2.0)
                return $"segment {segment.Id}: speed must be between 0.5 and 2";
            previousEnd = segment.SrcEnd;
        }
        if (!CaptionStyles.Contains(doc.CaptionStyle))
            return "captionStyle must be boxed, plain or karaoke";
        if (doc.Reframe is not ("auto" or "manual"))
            return "reframe must be auto or manual";
        if (doc.Transform.Scale is < 1.0 or > 3.0) return "transform.scale must be 1..3";
        if (doc.Transform.PosX is < -1.0 or > 1.0) return "transform.posX must be -1..1";
        if (doc.Audio.Voice is < 0 or > 2) return "audio.voice must be 0..2";
        if (doc.Audio.Music is < 0 or > 1) return "audio.music must be 0..1";
        foreach (var caption in doc.Captions)
            if (caption.End <= caption.Start)
                return $"caption {caption.Id}: end must be after start";
        foreach (var text in doc.Texts)
        {
            if (text.End <= text.Start) return $"text {text.Id}: end must be after start";
            if (text.Y is < 0 or > 1) return $"text {text.Id}: y must be 0..1";
        }
        return null;
    }

    public static double OutputDuration(EditorDoc doc) =>
        doc.Segments.Sum(segment => (segment.SrcEnd - segment.SrcStart) / segment.Speed);

    /// <summary>Map a source time into output time; null when it falls in a cut
    /// region. Speed within a segment compresses/stretches proportionally.</summary>
    public static double? SourceToOutput(EditorDoc doc, double sourceTime)
    {
        double elapsed = 0;
        foreach (var segment in doc.Segments)
        {
            if (sourceTime < segment.SrcStart - 1e-6)
                return null;
            if (sourceTime <= segment.SrcEnd + 1e-6)
                return elapsed + Math.Max(0, sourceTime - segment.SrcStart) / segment.Speed;
            elapsed += (segment.SrcEnd - segment.SrcStart) / segment.Speed;
        }
        return null;
    }

    /// <summary>Map a source window into the output timeline, clipped to the
    /// visible parts. A window spanning a cut yields its visible extent.</summary>
    public static (double Start, double End)? MapWindow(EditorDoc doc, double start, double end)
    {
        double elapsed = 0;
        double? outStart = null, outEnd = null;
        foreach (var segment in doc.Segments)
        {
            var visibleStart = Math.Max(start, segment.SrcStart);
            var visibleEnd = Math.Min(end, segment.SrcEnd);
            if (visibleEnd > visibleStart)
            {
                var mappedStart = elapsed + (visibleStart - segment.SrcStart) / segment.Speed;
                var mappedEnd = elapsed + (visibleEnd - segment.SrcStart) / segment.Speed;
                outStart ??= mappedStart;
                outEnd = mappedEnd;
            }
            elapsed += (segment.SrcEnd - segment.SrcStart) / segment.Speed;
        }
        return outStart is { } s && outEnd is { } e && e > s ? (s, e) : null;
    }

    public static JsonNode ToJson(EditorDoc doc) => JsonSerializer.SerializeToNode(doc, Json)!;

    public static EditorDoc? FromJson(JsonNode? node)
    {
        if (node is null) return null;
        try
        {
            return node.Deserialize<EditorDoc>(Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static double? Dbl(JsonNode? node)
    {
        try
        {
            return node?.GetValue<double>();
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
