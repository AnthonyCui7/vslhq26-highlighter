using System.Text.Json.Nodes;
using Highlighter.Pipeline;
using Xunit;

namespace Highlighter.Pipeline.Tests;

/// <summary>Port of pipeline/tests/test_editor.py.</summary>
public static class EditorTestData
{
    public static JsonObject Candidate(
        double start, double end, double score = 0.5, string title = "t") => new()
    {
        ["start_seconds"] = start,
        ["end_seconds"] = end,
        ["score"] = score,
        ["title"] = title,
        ["description"] = "d",
        ["reason"] = "r",
    };

    public static List<JsonObject> SelectionsOf(JsonArray raw, IReadOnlyList<JsonObject> candidates)
    {
        var offered = candidates
            .Select((candidate, index) => (Index: index, Candidate: candidate))
            .ToDictionary(pair => pair.Index, pair => pair.Candidate);
        return Editor.ValidateSelections(
            new JsonObject { ["selections"] = raw }, candidates, offered);
    }
}

public class TestParseTargetMinutes
{
    [Fact]
    public void SingleNumber() => Assert.Equal((10.0, 10.0), Llm.ParseTargetMinutes("10"));

    [Fact]
    public void Range() => Assert.Equal((7.0, 15.0), Llm.ParseTargetMinutes("7-15"));

    [Fact]
    public void Whitespace() => Assert.Equal((7.0, 15.0), Llm.ParseTargetMinutes(" 7 - 15 "));

    [Fact]
    public void GarbageAndEmpty()
    {
        Assert.Null(Llm.ParseTargetMinutes("abc"));
        Assert.Null(Llm.ParseTargetMinutes(""));
        Assert.Null(Llm.ParseTargetMinutes(null));
    }

    [Fact]
    public void InvertedRangeRejected() => Assert.Null(Llm.ParseTargetMinutes("15-7"));
}

public class TestValidateSelections
{
    [Fact]
    public void UnknownIndexDropped()
    {
        var candidates = new List<JsonObject> { EditorTestData.Candidate(0, 60) };
        var result = EditorTestData.SelectionsOf(
            new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 5,
                    ["start_seconds"] = 0,
                    ["end_seconds"] = 60,
                    ["reason"] = "",
                },
            },
            candidates);
        Assert.Empty(result);
    }

    [Fact]
    public void DuplicateIndexFirstWins()
    {
        var candidates = new List<JsonObject> { EditorTestData.Candidate(0, 60) };
        var result = EditorTestData.SelectionsOf(
            new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["start_seconds"] = 0,
                    ["end_seconds"] = 30,
                    ["reason"] = "first",
                },
                new JsonObject
                {
                    ["index"] = 0,
                    ["start_seconds"] = 30,
                    ["end_seconds"] = 60,
                    ["reason"] = "second",
                },
            },
            candidates);
        Assert.Single(result);
        Assert.Equal("first", JsonUtil.Str(result[0]["reason"]));
    }

    [Fact]
    public void BoundsClampedIntoCandidateWindow()
    {
        // The editor may only tighten, never extend.
        var candidates = new List<JsonObject> { EditorTestData.Candidate(100, 160) };
        var result = EditorTestData.SelectionsOf(
            new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["start_seconds"] = 80,
                    ["end_seconds"] = 200,
                    ["reason"] = "",
                },
            },
            candidates);
        Assert.Equal(100, JsonUtil.Double(result[0]["start_seconds"]));
        Assert.Equal(160, JsonUtil.Double(result[0]["end_seconds"]));
    }

    [Fact]
    public void DegenerateWindowDropped()
    {
        var candidates = new List<JsonObject> { EditorTestData.Candidate(100, 160) };
        var result = EditorTestData.SelectionsOf(
            new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["start_seconds"] = 150,
                    ["end_seconds"] = 151,
                    ["reason"] = "",
                },
            },
            candidates);
        Assert.Empty(result);
    }

    [Fact]
    public void SortedChronologically()
    {
        var candidates = new List<JsonObject>
        {
            EditorTestData.Candidate(300, 360),
            EditorTestData.Candidate(0, 60),
        };
        var result = EditorTestData.SelectionsOf(
            new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["start_seconds"] = 300,
                    ["end_seconds"] = 360,
                    ["reason"] = "",
                },
                new JsonObject
                {
                    ["index"] = 1,
                    ["start_seconds"] = 0,
                    ["end_seconds"] = 60,
                    ["reason"] = "",
                },
            },
            candidates);
        Assert.Equal(new[] { 1, 0 }, result.Select(s => JsonUtil.Int(s["index"])).ToArray());
    }

    [Fact]
    public void CappedOutCandidateNotSelectable()
    {
        var candidates = new List<JsonObject> { EditorTestData.Candidate(0, 60) };
        var result = Editor.ValidateSelections(
            new JsonObject
            {
                ["selections"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["index"] = 0,
                        ["start_seconds"] = 0,
                        ["end_seconds"] = 60,
                        ["reason"] = "",
                    },
                },
            },
            candidates,
            offered: new Dictionary<int, JsonObject>());
        Assert.Empty(result);
    }
}

public class TestCapCandidates
{
    [Fact]
    public void UnderCapKeepsOriginalIndexes()
    {
        var candidates = new List<JsonObject>
        {
            EditorTestData.Candidate(0, 60),
            EditorTestData.Candidate(60, 120),
        };
        var (indexed, dropped) = Editor.CapCandidates(candidates);
        Assert.Equal(0, dropped);
        Assert.Equal(new[] { 0, 1 }, indexed.Select(pair => pair.Index).ToArray());
    }

    [Fact]
    public void OverCapDropsWeakestKeepsOrder()
    {
        var candidates = Enumerable.Range(0, 160)
            .Select(i => EditorTestData.Candidate(i * 60, i * 60 + 30, score: i / 200.0))
            .ToList();
        var (indexed, dropped) = Editor.CapCandidates(candidates);
        Assert.Equal(10, dropped);
        Assert.Equal(150, indexed.Count);
        // The 10 lowest-scoring candidates are the earliest ones here.
        Assert.Equal(
            Enumerable.Range(10, 150).ToArray(),
            indexed.Select(pair => pair.Index).ToArray());
    }
}

public class TestBudgetArithmetic
{
    [Fact]
    public void MinutesAndKeepRate()
    {
        var candidates = new List<JsonObject>
        {
            EditorTestData.Candidate(0, 600),
            EditorTestData.Candidate(1000, 1600),
            EditorTestData.Candidate(2000, 3200),
        };
        var arithmetic = Editor.BudgetArithmetic(
            candidates, sourceMinutes: 180, targetLength: "10");
        Assert.Equal(3, JsonUtil.Int(arithmetic["candidate_count"]));
        Assert.Equal(40.0, JsonUtil.Double(arithmetic["candidate_minutes"]));
        Assert.Equal(25, JsonUtil.Int(arithmetic["keep_percent"]));
    }

    [Fact]
    public void UnparseableTargetOmitsKeepRate()
    {
        var arithmetic = Editor.BudgetArithmetic(
            new List<JsonObject> { EditorTestData.Candidate(0, 600) },
            sourceMinutes: 180,
            targetLength: "whatever");
        Assert.False(arithmetic.ContainsKey("keep_percent"));
    }
}
