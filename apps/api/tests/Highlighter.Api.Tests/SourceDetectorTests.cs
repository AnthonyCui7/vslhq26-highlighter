using Highlighter.Api.Services;
using Xunit;

namespace Highlighter.Api.Tests;

/// <summary>Pins the port to the worker's DetectSource
/// (pipeline-dotnet/src/Highlighter.Pipeline/Ingest.cs) — same URLs, same verdicts.</summary>
public class SourceDetectorTests
{
    [Theory]
    [InlineData("https://www.twitch.tv/somechannel", "twitch", "livestream", "somechannel livestream")]
    [InlineData("https://twitch.tv/somechannel/", "twitch", "livestream", "somechannel livestream")]
    [InlineData("https://TWITCH.TV/SomeChannel", "twitch", "livestream", "SomeChannel livestream")]
    [InlineData("https://www.twitch.tv/videos/123456", "twitch", "video", "twitch video 123456")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "youtube", "video", "youtube video dQw4w9WgXcQ")]
    [InlineData("https://youtube.com/watch?feature=share&v=abc123", "youtube", "video", "youtube video abc123")]
    [InlineData("https://youtu.be/abc123", "youtube", "video", "youtube video abc123")]
    [InlineData("https://www.youtube.com/@somehandle/live", "youtube", "livestream", "somehandle livestream")]
    [InlineData("https://www.youtube.com/@somehandle", "youtube", "livestream", "somehandle livestream")]
    [InlineData("https://www.youtube.com/c/SomeCreator", "youtube", "livestream", "SomeCreator livestream")]
    public void SupportedUrls(string url, string platform, string sourceType, string name)
    {
        Assert.True(SourceDetector.TryDetect(url, out var info));
        Assert.Equal(new SourceInfo(platform, sourceType, name), info);
    }

    [Theory]
    [InlineData("https://example.com/watch?v=abc")]
    [InlineData("https://vimeo.com/12345")]
    [InlineData("not a url at all")]
    [InlineData("twitch.tv/somechannel")] // scheme-less: same rejection as the worker
    public void UnsupportedUrls(string url)
    {
        Assert.False(SourceDetector.TryDetect(url, out _));
    }

    [Fact]
    public void UnsupportedMessage_MatchesTheWorkersWording()
    {
        Assert.Equal(
            "Unsupported source URL (expected a twitch.tv or youtube URL): https://x",
            SourceDetector.UnsupportedMessage("https://x"));
    }
}
