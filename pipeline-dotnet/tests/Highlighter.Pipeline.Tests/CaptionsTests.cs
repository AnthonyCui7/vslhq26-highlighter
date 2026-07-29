using System.Text.Json.Nodes;
using Highlighter.Pipeline;
using Xunit;

namespace Highlighter.Pipeline.Tests;

/// <summary>Port of pipeline/tests/test_captions.py.</summary>
public class TestBuildWhisperTranscript
{
    private static JsonObject Word(
        double start, double end, string text, string? punctuated = null) => new()
    {
        ["word"] = text,
        ["punctuated_word"] = punctuated ?? text,
        ["absolute_start"] = start,
        ["absolute_end"] = end,
    };

    [Fact]
    public void TimesRelativeToClip()
    {
        var transcript = Captions.BuildWhisperTranscript(
            new JsonArray(Word(10.5, 10.9, "hey", "Hey"), Word(11.0, 11.4, "there", "there.")),
            clipStartSeconds: 10.0,
            clipEndSeconds: 20.0);
        var segment = (JsonObject)((JsonArray)transcript!["segments"]!)[0]!;
        var words = ((JsonArray)segment["words"]!).OfType<JsonObject>().ToList();
        Assert.Equal("Hey", JsonUtil.Str(words[0]["word"]));
        Assert.Equal(0.5, JsonUtil.Double(words[0]["start"]));
        Assert.Equal(0.9, JsonUtil.Double(words[0]["end"]));
        Assert.Equal("there.", JsonUtil.Str(words[1]["word"]));
        Assert.Equal("Hey there.", JsonUtil.Str(segment["text"]));
        Assert.Equal(0.5, JsonUtil.Double(segment["start"]));
        Assert.Equal(1.4, JsonUtil.Double(segment["end"]));
    }

    [Fact]
    public void WordsOutsideWindowDropped()
    {
        var transcript = Captions.BuildWhisperTranscript(
            new JsonArray(
                Word(8.0, 9.0, "before"),
                Word(10.2, 10.8, "inside"),
                Word(21.0, 22.0, "after")),
            clipStartSeconds: 10.0,
            clipEndSeconds: 20.0);
        var segment = (JsonObject)((JsonArray)transcript!["segments"]!)[0]!;
        var words = ((JsonArray)segment["words"]!).OfType<JsonObject>().ToList();
        Assert.Equal(new[] { "inside" }, words.Select(w => JsonUtil.Str(w["word"])).ToArray());
    }

    [Fact]
    public void BoundaryWordClamped()
    {
        var transcript = Captions.BuildWhisperTranscript(
            new JsonArray(Word(9.8, 10.4, "straddle"), Word(19.7, 20.6, "tail")),
            clipStartSeconds: 10.0,
            clipEndSeconds: 20.0);
        var words = ((JsonArray)transcript!["segments"]!)
            .OfType<JsonObject>()
            .SelectMany(segment => ((JsonArray)segment["words"]!).OfType<JsonObject>())
            .ToList();
        Assert.Equal(0.0, JsonUtil.Double(words[0]["start"]));
        Assert.Equal(10.0, JsonUtil.Double(words[^1]["end"]));
    }

    [Fact]
    public void SegmentsSplitOnSilence()
    {
        var transcript = Captions.BuildWhisperTranscript(
            new JsonArray(
                Word(10.0, 10.5, "one"),
                Word(10.6, 11.0, "two"),
                Word(13.0, 13.5, "three")),
            clipStartSeconds: 10.0,
            clipEndSeconds: 20.0);
        var segments = ((JsonArray)transcript!["segments"]!).OfType<JsonObject>().ToList();
        Assert.Equal(2, segments.Count);
        Assert.Equal("one two", JsonUtil.Str(segments[0]["text"]));
        Assert.Equal("three", JsonUtil.Str(segments[1]["text"]));
        Assert.Equal(new[] { 0, 1 }, segments.Select(s => JsonUtil.Int(s["id"])).ToArray());
    }

    [Fact]
    public void NoWordsReturnsNull()
    {
        Assert.Null(Captions.BuildWhisperTranscript(
            new JsonArray(Word(1.0, 2.0, "far")), clipStartSeconds: 10.0, clipEndSeconds: 20.0));
        Assert.Null(Captions.BuildWhisperTranscript(
            new JsonArray(), clipStartSeconds: 0.0, clipEndSeconds: 5.0));
    }
}
