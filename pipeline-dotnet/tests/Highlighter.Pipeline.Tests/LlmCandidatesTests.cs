using System.Text.Json.Nodes;
using Highlighter.Pipeline;
using Xunit;

namespace Highlighter.Pipeline.Tests;

/// <summary>Port of pipeline/tests/test_llm_candidates.py.</summary>
public class LlmCandidatesTests
{
    private static (List<JsonObject> Candidates, string Assessment) Normalize(
        JsonArray clips,
        string assessment = "assessed",
        int chunkIndex = 1,
        int chunkStartSeconds = 90,
        int chunkEndSeconds = 180,
        int visibleStartSeconds = 80,
        int visibleEndSeconds = 190)
    {
        return Llm.NormalizeCandidates(
            new JsonObject { ["clips"] = clips, ["chunk_assessment"] = assessment },
            chunkIndex: chunkIndex,
            chunkStartSeconds: chunkStartSeconds,
            chunkEndSeconds: chunkEndSeconds,
            visibleStartSeconds: visibleStartSeconds,
            visibleEndSeconds: visibleEndSeconds,
            model: "test-model",
            reasoningEffort: "low");
    }

    private static JsonObject Clip(double start, double end, double score = 0.5) => new()
    {
        ["title"] = "t",
        ["description"] = "d",
        ["start_seconds"] = start,
        ["end_seconds"] = end,
        ["score"] = score,
        ["reason"] = "r",
        ["research_sources"] = new JsonArray(),
    };

    [Fact]
    public void EmptyClipsReturnsAssessment()
    {
        var (candidates, assessment) = Normalize(new JsonArray(), assessment: "all filler");
        Assert.Empty(candidates);
        Assert.Equal("all filler", assessment);
    }

    [Fact]
    public void MultipleClipsNormalized()
    {
        var (candidates, _) = Normalize(new JsonArray { Clip(95, 120), Clip(150, 170, score: 0.9) });
        Assert.Equal(2, candidates.Count);
        Assert.All(candidates, c => Assert.True(JsonUtil.Truthy(c["is_clip_worthy"])));
        Assert.All(candidates, c => Assert.Equal(1, JsonUtil.Int(c["chunk_index"])));
        Assert.Equal(0.9, JsonUtil.Double(candidates[1]["score"]));
    }

    [Fact]
    public void ClampedToVisibleWindowNotChunkWindow()
    {
        var (candidates, _) = Normalize(new JsonArray { Clip(60, 200) });
        Assert.Equal(80, JsonUtil.Double(candidates[0]["start_seconds"]));
        Assert.Equal(190, JsonUtil.Double(candidates[0]["end_seconds"]));
    }

    [Fact]
    public void BoundarySpillIsPreserved()
    {
        var (candidates, _) = Normalize(new JsonArray { Clip(85, 95) });
        Assert.Equal(85, JsonUtil.Double(candidates[0]["start_seconds"])); // 5s into the left margin
    }

    [Fact]
    public void MarginOnlyClipDropped()
    {
        // Lives entirely in the left context margin: the previous chunk owns it.
        var (candidates, _) = Normalize(new JsonArray { Clip(80, 90) });
        Assert.Empty(candidates);
    }

    [Fact]
    public void InvalidWindowDropped()
    {
        var (candidates, _) = Normalize(new JsonArray { Clip(120, 100) });
        Assert.Empty(candidates);
    }

    [Fact]
    public void ScoreClampedToUnitInterval()
    {
        var (candidates, _) = Normalize(new JsonArray { Clip(100, 120, score: 7) });
        Assert.Equal(1.0, JsonUtil.Double(candidates[0]["score"]));
    }

    [Fact]
    public void MalformedItemsSkipped()
    {
        var (candidates, _) = Normalize(new JsonArray { "nonsense", Clip(100, 120) });
        Assert.Single(candidates);
    }

    [Fact]
    public void MissingClipsKeyIsEmpty()
    {
        var (candidates, assessment) = Llm.NormalizeCandidates(
            new JsonObject(),
            chunkIndex: 0,
            chunkStartSeconds: 0,
            chunkEndSeconds: 90,
            visibleStartSeconds: 0,
            visibleEndSeconds: 90,
            model: "m",
            reasoningEffort: "low");
        Assert.Empty(candidates);
        Assert.Equal("", assessment);
    }
}
