using Highlighter.Pipeline;
using Xunit;

namespace Highlighter.Pipeline.Tests;

public class IngestTests
{
    private static Func<int, double> Starts(Dictionary<int, double> mapping) =>
        index => Ingest.MeasuredSegmentStart(mapping, index, 90);

    [Fact]
    public void MeasuredPositionsAreRelativeToSegmentZero()
    {
        var starts = new Dictionary<int, double> { [0] = 1.44, [1] = 93.25, [2] = 183.11 };
        Assert.Equal(0.0, Ingest.MeasuredSegmentStart(starts, 0, 90));
        Assert.Equal(91.81, Math.Round(Ingest.MeasuredSegmentStart(starts, 1, 90), 2));
        Assert.Equal(181.67, Math.Round(Ingest.MeasuredSegmentStart(starts, 2, 90), 2));
    }

    [Fact]
    public void MissingProbeFallsBackToNominal()
    {
        Assert.Equal(180.0, Ingest.MeasuredSegmentStart(new() { [0] = 1.44 }, 2, 90));
        Assert.Equal(180.0, Ingest.MeasuredSegmentStart(new() { [2] = 183.11 }, 2, 90));
        Assert.Equal(0.0, Ingest.MeasuredSegmentStart(new(), 0, 90));
        Assert.Equal(270.0, Ingest.MeasuredSegmentStart(new(), 3, 90));
    }

    [Fact]
    public void WindowRangeIsNominalWhenNoProbes()
    {
        Assert.Equal((0, 1), Ingest.WindowSegmentRange(66.0, 97.5, 90, Starts(new())));
        Assert.Equal((2, 2), Ingest.WindowSegmentRange(191.1, 261.48, 90, Starts(new())));
    }

    [Fact]
    public void WindowStartingInPreviousSegmentsTail()
    {
        // Segment 1 truly begins at 91.81s, so a 91.0s start lives in segment 0.
        var starts = Starts(new Dictionary<int, double> { [0] = 1.44, [1] = 93.25 });
        Assert.Equal((0, 1), Ingest.WindowSegmentRange(91.0, 120.0, 90, starts));
    }

    [Fact]
    public void WindowEndingBeforeASegmentsContent()
    {
        // Nominal arithmetic wants segment 1 for a 90.5s end, but segment 1's
        // content starts later — segment 0 covers the whole window.
        var starts = Starts(new Dictionary<int, double> { [0] = 1.44, [1] = 93.25 });
        Assert.Equal((0, 0), Ingest.WindowSegmentRange(30.0, 90.5, 90, starts));
    }

    [Fact]
    public void ExactBoundaryEndIsExclusive()
    {
        Assert.Equal((0, 0), Ingest.WindowSegmentRange(0.0, 90.0, 90, Starts(new())));
    }

    [Fact]
    public void RangeNeverWalksBelowZero()
    {
        var starts = Starts(new Dictionary<int, double> { [0] = 5.0 });
        Assert.Equal((0, 0), Ingest.WindowSegmentRange(1.0, 20.0, 90, starts));
    }
}
