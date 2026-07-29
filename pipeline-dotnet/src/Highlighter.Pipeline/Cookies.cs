namespace Highlighter.Pipeline;

/// <summary>Port of highlighter_pipeline/cookies.py.
///
/// Optional YouTube cookie support for yt-dlp.
///
/// Datacenter IPs trip YouTube's "Sign in to confirm you're not a bot" gate, so
/// the worker's secrets bundle MAY carry YTDLP_COOKIES_B64 (a base64-encoded
/// Netscape cookies.txt). When set, it is decoded once to a private (0600) file
/// and every yt-dlp invocation gets --cookies &lt;file&gt;; when empty or absent,
/// behavior is unchanged. Cookie contents are never logged.</summary>
public static class Cookies
{
    public const string COOKIES_ENV_VAR = "YTDLP_COOKIES_B64";

    private static readonly string CookiePath = Path.Combine("data", "yt_cookies.txt");

    // null = not prepared yet; [] = prepared, no cookies; ["--cookies", path] = prepared.
    private static List<string>? _cookieArgs;

    /// <summary>Decode YTDLP_COOKIES_B64 to a private cookie file (idempotent).
    ///
    /// Call once at startup so a malformed value fails fast: the PipelineError is
    /// raised before capture begins, where ingest's pre-capture failure handling
    /// writes the readable error back to the project row in attach mode.</summary>
    public static void PrepareYtdlpCookies()
    {
        if (_cookieArgs is not null) return;

        var raw = Config.Env(COOKIES_ENV_VAR) ?? "";
        var encoded = string.Concat(
            raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (encoded.Length == 0)
        {
            _cookieArgs = new List<string>();
            return;
        }

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(encoded);
        }
        catch (FormatException exc)
        {
            throw new PipelineError(
                $"{COOKIES_ENV_VAR} is not valid base64 (expected a base64-encoded Netscape cookies.txt)",
                exc);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(CookiePath)!);
        // Owner-only from creation, and re-chmod in case the file already existed.
        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var file = new FileStream(CookiePath, options))
        {
            file.Write(decoded);
        }
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(CookiePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        _cookieArgs = new List<string> { "--cookies", CookiePath };
    }

    /// <summary>Extra yt-dlp arguments: ["--cookies", &lt;path&gt;] when cookies were
    /// provided, else [].</summary>
    public static List<string> YtdlpCookieArgs()
    {
        PrepareYtdlpCookies();
        return new List<string>(_cookieArgs!);
    }
}
