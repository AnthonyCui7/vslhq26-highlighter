using System.Globalization;

namespace Highlighter.Web.Models;

/// <summary>API values → the display strings the studio UI was designed around.</summary>
public static class Fmt
{
    /// <summary>"2:01:44", "24:36", "0:42" — or "—" until the source is probed.</summary>
    public static string Duration(double? seconds)
    {
        if (seconds is not > 0) return "—";
        var t = TimeSpan.FromSeconds(seconds.Value);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";
    }

    /// <summary>Zero-padded position stamp for lists: "05:38", "1:02:11".</summary>
    public static string Stamp(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes:00}:{t.Seconds:00}";
    }

    /// <summary>Editor timecode "00:00:12:04" (30 fps frame counter).</summary>
    public static string Timecode(double seconds)
    {
        var clamped = Math.Max(0, seconds);
        var t = TimeSpan.FromSeconds(clamped);
        var frames = (int)Math.Round(t.Milliseconds / 1000.0 * 30) % 30;
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}:{frames:00}";
    }

    /// <summary>Pipeline clip scores are 0..1 doubles; the UI shows 0–100.</summary>
    public static int Score100(double? score) => (int)Math.Round((score ?? 0) * 100);

    /// <summary>SampleData's exact opacity ramp, kept for visual continuity.</summary>
    public static string ScoreOpacity(double? score) =>
        (0.35 + 0.65 * Math.Max(0, (Score100(score) - 50) / 50.0))
            .ToString("0.00", CultureInfo.InvariantCulture);

    public static string Kind(string sourceType) => sourceType.ToUpperInvariant();

    public static string Outputs(string pipeline) => pipeline switch
    {
        "both" => "Highlights + Long-form",
        "long" => "Long-form",
        _ => "Highlights",
    };

    public static string StatusLine(ProjectSummaryDto p) => p.Status switch
    {
        "created" => "Queued",
        "ingesting" => p.Progress.Percent is { } pct ? $"Editing · {pct * 100:0}%" : "Editing",
        "stopping" => "Stopping",
        "ready" => "Ready",
        "failed" => "Failed",
        "cancelled" => "Cancelled",
        var s when s.Length > 0 => char.ToUpperInvariant(s[0]) + s[1..],
        _ => "",
    };

    public static string Added(DateTimeOffset createdAt) =>
        "added " + createdAt.LocalDateTime.ToString("MMM d", CultureInfo.InvariantCulture);

    public static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";
}
