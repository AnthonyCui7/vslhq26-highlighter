using System.Globalization;

namespace Highlighter.Web.Models;

/// <summary>A long-form thumbnail concept as the picker renders it. Fill is a
/// CSS background — the hosted image when one exists, a gradient placeholder
/// while a variant is still rendering.</summary>
public record ThumbVariant(int Index, string Direction, string OverlayText, string Fill);

public record WaveBar(string H, string C);

/// <summary>What's left of the original static design data: the deterministic
/// waveform generator the timeline uses as audio-track decoration. Everything
/// else the studio shows now comes from the API.</summary>
public static class SampleData
{
    // Mirrors the design's wave(n, base, amp, color) generator. base/amp accept
    // either percent units (12/78) or 0..1 fractions (0.35/0.5).
    public static IReadOnlyList<WaveBar> Wave(int n, double baseH, double amp, string color)
    {
        if (baseH <= 1.0 && amp <= 1.0)
        {
            baseH *= 100;
            amp *= 100;
        }
        return Enumerable.Range(0, n).Select(i =>
        {
            var v = Math.Abs(Math.Sin(i * 0.9) * 0.5 + Math.Sin(i * 0.23) * 0.35 + Math.Sin(i * 2.7) * 0.15);
            var h = (int)Math.Round(baseH + v * amp, MidpointRounding.AwayFromZero);
            return new WaveBar(h.ToString(CultureInfo.InvariantCulture) + "%", color);
        }).ToList();
    }
}
