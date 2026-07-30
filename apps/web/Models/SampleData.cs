using System.Globalization;

namespace Highlighter.Web.Models;

public record ProjectInfo(string Title, string Dur, string Kind, string Outputs, string Status, bool Processing, bool HasLong);

public record ClipInfo(int Index, string Title, string Dur, int Score, int At, bool HasCaptions = true)
{
    public string ScoreOpacity =>
        (0.35 + 0.65 * Math.Max(0, (Score - 50) / 50.0)).ToString("0.00", CultureInfo.InvariantCulture);
}

public record ThumbVariant(int Index, string Direction, string OverlayText, string Fill);

public record SegmentInfo(string At, string Title, string Dur);

public record BinItem(string Name, string Meta, string Thumb, string Glyph);

public record CaptionLine(string T, string Text);

public record SliderInfo(string Label, string Val, string Pct, string Fill);

public record ToolDef(string Key, string Icon, string Label);

public record WaveBar(string H, string C);

public record TrackBlock(string L, string W, string Label, bool ShowLabel, string Bg, string Border, string LabelColor, IReadOnlyList<WaveBar>? Bars = null);

public record TrackInfo(string Name, string Kind, string Tog1, string Tog2, string H, IReadOnlyList<TrackBlock> Blocks);

public static class SampleData
{
    public static readonly IReadOnlyList<ProjectInfo> Projects =
    [
        new("The Deep End — Ep. 47", "2:01:44", "VOD", "Highlights + Long-form", "Editing · 2 pipelines", Processing: true, HasLong: true),
        new("The Deep End — Ep. 46", "1:48:12", "VOD", "Highlights + Long-form", "Ready", Processing: false, HasLong: true),
        new("Live Q&A — June mailbag", "3:22:05", "LIVESTREAM", "Highlights", "Ready", Processing: false, HasLong: false),
    ];

    public static readonly IReadOnlyList<ClipInfo> Clips =
    [
        new(0, "The $40k GPU bill nobody saw coming", "0:42", 94, 82),
        new(1, "“Agents don’t need memory” — hot take", "0:58", 91, 47),
        new(2, "Why evals lie to you in production", "0:36", 88, 65),
        new(3, "The retry storm that took down prod", "0:51", 86, 101),
        new(4, "Ship the boring architecture first", "0:44", 81, 19),
        new(5, "Guest’s origin story: fired, then funded", "0:39", 78, 8, HasCaptions: false),
        new(6, "Rapid fire: overrated AI tools", "0:33", 74, 121),
        new(7, "The one metric that predicted churn", "0:47", 71, 72, HasCaptions: false),
    ];

    public const string LongformTitle = "The $40k Weekend: Anatomy of an Agent Swarm Meltdown";

    // Generated long-form thumbnail concepts; each mock frame is a distinct
    // gradient standing in for the rendered image.
    public static readonly IReadOnlyList<ThumbVariant> Thumbnails =
    [
        new(1, "Disaster Close-Up", "$40K IN 48 HRS",
            "linear-gradient(135deg, #3B1D1D 0%, #6E2B18 55%, #D9F24B 130%)"),
        new(2, "Split-Screen Debate", "WHO'S RIGHT?",
            "linear-gradient(120deg, #16324F 0%, #1D1B2E 50%, #532B4D 100%)"),
        new(3, "Terminal Horror", "retry storm",
            "linear-gradient(160deg, #101418 0%, #16281C 60%, #2F5D33 120%)"),
    ];

    public static readonly IReadOnlyList<SegmentInfo> Segments =
    [
        new("00:00", "Cold open — the $40k weekend", "2:10"),
        new("02:10", "Guest intro & background", "3:28"),
        new("05:38", "Scaling the swarm to 200 agents", "6:04"),
        new("11:42", "The retry storm post-mortem", "5:51"),
        new("17:33", "Why evals lie in production", "4:22"),
        new("21:55", "Closing takes & rapid fire", "2:41"),
    ];

    public const string BinThumb = "repeating-linear-gradient(90deg, #26301A 0 8px, #2B3620 8px 16px)";

    public static readonly IReadOnlyList<BinItem> BinItems =
    [
        new("Hook", "0:12.6 · V1", BinThumb, ""),
        new("Setup", "0:16.0 · V1", BinThumb, ""),
        new("Payoff", "0:13.4 · V1", BinThumb, ""),
        new("lofi-bed-04.mp3", "0:42 · A2", "#1C1B19", "∿∿∿"),
    ];

    public static readonly IReadOnlyList<CaptionLine> Captions =
    [
        new("00:00.4", "so we scaled the swarm to two hundred agents overnight"),
        new("00:04.1", "and nobody budgets for the retry storm"),
        new("00:07.8", "every failed call retried three times — with backoff, sure"),
        new("00:12.0", "but backoff across two hundred agents is still a stampede"),
        new("00:16.5", "the GPU bill tripled before the on-call even woke up"),
        new("00:21.2", "forty thousand dollars. one weekend."),
        new("00:24.9", "and here’s the part that kills me —"),
        new("00:27.3", "the fix was a four line semaphore"),
    ];

    public static readonly IReadOnlyList<SliderInfo> InspectorSliders =
    [
        new("Scale", "112%", "56%", "#D9F24B"),
        new("Position X", "−64 px", "38%", "#D9F24B"),
        new("Speed", "1.0×", "40%", "#D9F24B"),
        new("Voice volume", "0 dB", "72%", "#D9F24B"),
        new("Music bed", "−18 dB", "30%", "#55524C"),
    ];

    public static readonly IReadOnlyList<ToolDef> Tools =
    [
        new("select", "➤", "Select"),
        new("razor", "∥", "Razor"),
        new("trim", "⟷", "Trim"),
        new("slip", "⇄", "Slip"),
        new("text", "T", "Text"),
        new("marker", "◆", "Marker"),
    ];

    public static readonly IReadOnlyList<string> RulerMarks =
        ["00:00", "00:05", "00:10", "00:15", "00:20", "00:25", "00:30", "00:35", "00:40"];

    public const string SegBg = "repeating-linear-gradient(90deg, #26301A 0 22px, #2B3620 22px 44px)";

    // Mirrors the design's wave(n, base, amp, color) generator.
    public static IReadOnlyList<WaveBar> Wave(int n, double baseH, double amp, string color) =>
        Enumerable.Range(0, n).Select(i =>
        {
            var v = Math.Abs(Math.Sin(i * 0.9) * 0.5 + Math.Sin(i * 0.23) * 0.35 + Math.Sin(i * 2.7) * 0.15);
            var h = (int)Math.Round(baseH + v * amp, MidpointRounding.AwayFromZero);
            return new WaveBar(h.ToString(CultureInfo.InvariantCulture) + "%", color);
        }).ToList();

    public static readonly IReadOnlyList<WaveBar> VoiceWave = Wave(120, 12, 78, "#5E6E33");
    public static readonly IReadOnlyList<WaveBar> MusicWave = Wave(120, 8, 32, "#3E3B37");

    public static IReadOnlyList<TrackInfo> BuildTracks(int activeCaption)
    {
        const string A = "#D9F24B", MUT = "#8A857C";
        var capBlocks = Captions.Select((c, i) => new TrackBlock(
            L: (1 + i * 12.3).ToString(CultureInfo.InvariantCulture) + "%",
            W: "11.5%",
            Label: c.Text[..14] + "…",
            ShowLabel: true,
            Bg: activeCaption == i ? A : "#22201E",
            Border: activeCaption == i ? A : "#2E2C29",
            LabelColor: activeCaption == i ? "#141312" : MUT)).ToList();

        return
        [
            new TrackInfo("T1", "TEXT", "◉", "⊘", "26px", capBlocks),
            new TrackInfo("V1", "VIDEO", "◉", "⊘", "52px",
            [
                new TrackBlock("0.5%", "30%", "Hook · 0:12.6", true, SegBg, "#3A4626", "#C9D98A"),
                new TrackBlock("31.5%", "38%", "Setup · 0:16.0", true, SegBg, A, "#C9D98A"),
                new TrackBlock("70.5%", "29%", "Payoff · 0:13.4", true, SegBg, "#3A4626", "#C9D98A"),
            ]),
            new TrackInfo("A1", "VOICE", "M", "S", "44px",
            [
                new TrackBlock("0.5%", "99%", "", false, "#161811", "#2A2F1C", MUT, VoiceWave),
            ]),
            new TrackInfo("A2", "MUSIC", "M", "S", "34px",
            [
                new TrackBlock("0.5%", "99%", "", false, "#171615", "#242220", MUT, MusicWave),
            ]),
        ];
    }
}
