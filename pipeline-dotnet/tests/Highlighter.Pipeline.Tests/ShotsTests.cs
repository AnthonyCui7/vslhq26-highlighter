using System.Text.Json.Nodes;
using Highlighter.Pipeline;
using Xunit;

namespace Highlighter.Pipeline.Tests;

/// <summary>Port of pipeline/tests/test_shots.py.</summary>
public class ShotsTests
{
    private static readonly List<(double, double)> NoWords = new();

    [Fact]
    public void NoCutsLeavesWindowUnchanged()
    {
        var (start, end, info) = Shots.SnapWindow(10.0, 40.0, cuts: new List<double>(), wordSpans: NoWords);
        Assert.Equal((10.0, 40.0), (start, end));
        Assert.Null(info);
    }

    [Fact]
    public void BoundariesSnapToNearbyCuts()
    {
        var (start, end, info) = Shots.SnapWindow(
            10.0, 40.0, cuts: new List<double> { 9.2, 41.1 }, wordSpans: NoWords);
        Assert.Equal((9.2, 41.1), (start, end));
        Assert.NotNull(info);
        Assert.Equal(10.0, JsonUtil.Double(info["original_start"]));
        Assert.Equal(40.0, JsonUtil.Double(info["original_end"]));
        Assert.Equal(9.2, JsonUtil.Double(info["snapped_start"]));
        Assert.Equal(41.1, JsonUtil.Double(info["snapped_end"]));
    }

    [Fact]
    public void CutOutsideToleranceIsIgnored()
    {
        var (start, end, info) = Shots.SnapWindow(
            10.0, 40.0, cuts: new List<double> { 7.0, 44.0 }, wordSpans: NoWords);
        Assert.Equal((10.0, 40.0), (start, end));
        Assert.Null(info);
    }

    [Fact]
    public void NearestQualifyingCutWins()
    {
        var (start, _, _) = Shots.SnapWindow(
            10.0, 40.0, cuts: new List<double> { 8.8, 10.4 }, wordSpans: NoWords);
        Assert.Equal(10.4, start);
    }

    [Fact]
    public void CutInsideAWordIsRefused()
    {
        // 9.2 falls mid-word; the boundary must not clip speech.
        var (start, end, info) = Shots.SnapWindow(
            10.0, 40.0,
            cuts: new List<double> { 9.2 },
            wordSpans: new List<(double, double)> { (9.0, 9.5) });
        Assert.Equal((10.0, 40.0), (start, end));
        Assert.Null(info);
    }

    [Fact]
    public void CutAtWordEdgeIsAllowed()
    {
        var (start, _, info) = Shots.SnapWindow(
            10.0, 40.0,
            cuts: new List<double> { 9.0 },
            wordSpans: new List<(double, double)> { (9.0, 9.5) });
        Assert.Equal(9.0, start);
        Assert.NotNull(info);
    }

    [Fact]
    public void SnapThatCollapsesTheClipIsRefused()
    {
        // Both boundaries would snap to nearby cuts, but the result is under the
        // minimum clip length, so the original window is kept.
        var (start, end, info) = Shots.SnapWindow(
            10.0, 12.0,
            cuts: new List<double> { 10.9, 11.4 },
            wordSpans: NoWords,
            minDurationSeconds: 2.0);
        Assert.Equal((10.0, 12.0), (start, end));
        Assert.Null(info);
    }

    [Fact]
    public void SingleBoundarySnap()
    {
        var (start, end, info) = Shots.SnapWindow(
            10.0, 40.0, cuts: new List<double> { 39.5 }, wordSpans: NoWords);
        Assert.Equal((10.0, 39.5), (start, end));
        Assert.NotNull(info);
        Assert.Equal(10.0, JsonUtil.Double(info["snapped_start"]));
    }
}
