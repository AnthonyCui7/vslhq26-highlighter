using System.Text.Json.Nodes;
using Highlighter.Pipeline;
using Xunit;

namespace Highlighter.Pipeline.Tests;

/// <summary>Port of pipeline/tests/test_publish.py.</summary>
public class TestParsePlatforms
{
    [Fact]
    public void ValidListDedupedInOrder() =>
        Assert.Equal(
            new List<string> { "youtube", "tiktok" },
            Publish.ParsePlatforms("youtube, tiktok,youtube"));

    [Fact]
    public void CaseInsensitive() =>
        Assert.Equal(
            new List<string> { "tiktok", "x", "instagram" },
            Publish.ParsePlatforms("TikTok,X,Instagram"));

    [Fact]
    public void UnknownPlatformRejected()
    {
        var exc = Assert.Throws<PipelineError>(() => Publish.ParsePlatforms("youtube,facebook"));
        Assert.Contains("Unknown platform 'facebook'", exc.Message);
    }

    [Fact]
    public void XAloneRejected()
    {
        var exc = Assert.Throws<PipelineError>(() => Publish.ParsePlatforms("x"));
        Assert.Contains("promo post", exc.Message);
    }

    [Fact]
    public void XWithVideoPlatformOk() =>
        Assert.Equal(new List<string> { "x", "youtube" }, Publish.ParsePlatforms("x,youtube"));

    [Fact]
    public void EmptyRejected()
    {
        var exc = Assert.Throws<PipelineError>(() => Publish.ParsePlatforms(" , "));
        Assert.Contains("No platforms", exc.Message);
    }
}

public class TestPickClipMedia
{
    private static JsonObject Render() => new()
    {
        ["local_path"] = "clips/c.mp4",
        ["vertical_path"] = "clips/c_vertical.mp4",
        ["captioned_path"] = "clips/c_vertical_captions.mp4",
    };

    [Fact]
    public void CaptionedPreferred()
    {
        var (path, label) = Publish.PickClipMedia(Render(), plain: false);
        Assert.Equal("clips/c_vertical_captions.mp4", path);
        Assert.Equal("captioned vertical", label);
    }

    [Fact]
    public void PlainSkipsCaptioned()
    {
        var (path, label) = Publish.PickClipMedia(Render(), plain: true);
        Assert.Equal("clips/c_vertical.mp4", path);
        Assert.Equal("vertical", label);
    }

    [Fact]
    public void FallsBackThroughTheChain()
    {
        var (path, _) = Publish.PickClipMedia(
            new JsonObject { ["local_path"] = "clips/c.mp4" }, plain: false);
        Assert.Equal("clips/c.mp4", path);
        var (nullPath, label) = Publish.PickClipMedia(new JsonObject(), plain: false);
        Assert.Null(nullPath);
        Assert.Equal("16:9 clip", label);
    }
}

public class TestResultUrl
{
    [Fact]
    public void UrlKeyVariants()
    {
        Assert.Equal("https://a", Publish.ResultUrl(new JsonObject { ["url"] = "https://a" }));
        Assert.Equal(
            "https://b", Publish.ResultUrl(new JsonObject { ["post_url"] = "https://b" }));
        Assert.Equal(
            "https://c", Publish.ResultUrl(new JsonObject { ["video_url"] = "https://c" }));
        Assert.Null(Publish.ResultUrl(new JsonObject { ["success"] = true }));
    }
}
