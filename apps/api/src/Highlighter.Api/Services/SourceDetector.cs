namespace Highlighter.Api.Services;

public sealed record SourceInfo(string Platform, string SourceType, string Name);

/// <summary>Faithful port of the worker's URL classifier
/// (pipeline-dotnet/src/Highlighter.Pipeline/Ingest.cs DetectSource) so the API
/// can reject unsupported URLs up front and pre-fill the row's source_type/name.
/// The worker re-detects from source_url itself, so drift here is self-healing —
/// SourceDetectorTests pin this port to the worker's behavior.</summary>
public static class SourceDetector
{
    public static bool TryDetect(string url, out SourceInfo info)
    {
        info = default!;
        Uri parsed;
        try
        {
            parsed = new Uri(url);
        }
        catch (UriFormatException)
        {
            return false;
        }

        var host = parsed.Host.ToLowerInvariant();
        if (host.StartsWith("www.")) host = host[4..];
        var path = parsed.AbsolutePath.TrimEnd('/');
        var segments = path.Split('/').Where(segment => segment.Length > 0).ToList();

        if (host.EndsWith("twitch.tv"))
        {
            if (segments.Count >= 2 && segments[^2] == "videos")
            {
                info = new SourceInfo("twitch", "video", $"twitch video {segments[^1]}");
                return true;
            }
            var channel = segments.Count > 0 ? segments[^1] : "twitch";
            info = new SourceInfo("twitch", "livestream", $"{channel} livestream");
            return true;
        }

        if (host.EndsWith("youtube.com"))
        {
            if (segments.Count >= 2 && segments[^1] == "live")
            {
                info = new SourceInfo("youtube", "livestream", $"{segments[^2].TrimStart('@')} livestream");
                return true;
            }
            var videoId = QueryParam(parsed.Query, "v");
            if (!string.IsNullOrEmpty(videoId))
            {
                info = new SourceInfo("youtube", "video", $"youtube video {videoId}");
                return true;
            }
            if (segments.Count > 0)
            {
                info = new SourceInfo("youtube", "livestream", $"{segments[^1].TrimStart('@')} livestream");
                return true;
            }
        }

        if (host == "youtu.be" && segments.Count > 0)
        {
            info = new SourceInfo("youtube", "video", $"youtube video {segments[^1]}");
            return true;
        }

        return false;
    }

    /// <summary>Same message the worker raises, so the two reject identically.</summary>
    public static string UnsupportedMessage(string url) =>
        $"Unsupported source URL (expected a twitch.tv or youtube URL): {url}";

    private static string? QueryParam(string query, string name)
    {
        foreach (var pair in query.TrimStart('?').Split('&'))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0) continue;
            if (Uri.UnescapeDataString(pair[..separator]) == name)
                return Uri.UnescapeDataString(pair[(separator + 1)..]);
        }
        return null;
    }
}
