using System.Text.Json.Nodes;
using Highlighter.Pipeline;
using Xunit;

namespace Highlighter.Pipeline.Tests;

/// <summary>Port of pipeline/tests/test_scoring.py.</summary>
public static class ScoringTestData
{
    public static JsonObject Candidate(
        int chunkIndex,
        double start,
        double end,
        double score = 0.5,
        string? title = null,
        JsonArray? researchSources = null)
    {
        return new JsonObject
        {
            ["chunk_index"] = chunkIndex,
            ["is_clip_worthy"] = true,
            ["title"] = title ?? $"clip {chunkIndex}",
            ["description"] = "d",
            ["start_seconds"] = start,
            ["end_seconds"] = end,
            ["score"] = score,
            ["reason"] = "r",
            ["research_sources"] = researchSources ?? new JsonArray(),
            ["model"] = "test-model",
            ["reasoning_effort"] = "low",
            ["raw_decision"] = new JsonObject(),
        };
    }

    public static JsonObject MakeChunk(int index, int chunkSeconds = 90, int wordsPerSecond = 1)
    {
        var start = index * chunkSeconds;
        var end = (index + 1) * chunkSeconds;
        var words = new JsonArray();
        for (var i = 0; i < chunkSeconds; i += wordsPerSecond)
        {
            words.Add(new JsonObject
            {
                ["word"] = $"w{index}_{i}",
                ["absolute_start"] = (double)(start + i),
                ["absolute_end"] = (double)(start + i) + 0.5,
            });
        }
        return new JsonObject
        {
            ["chunk_index"] = index,
            ["start_seconds"] = start,
            ["end_seconds"] = end,
            ["transcript"] = $"chunk {index}",
            ["words"] = words,
        };
    }
}

public class TestMergeCandidates
{
    [Fact]
    public void OverlappingCandidatesUnion()
    {
        var merged = Scoring.MergeCandidates(
            new[]
            {
                ScoringTestData.Candidate(0, 10, 30, score: 0.4),
                ScoringTestData.Candidate(1, 25, 45, score: 0.7),
            },
            mergeGapSeconds: 1.0,
            maxClipSeconds: 120);
        Assert.Single(merged);
        Assert.Equal(10, JsonUtil.Double(merged[0]["start_seconds"]));
        Assert.Equal(45, JsonUtil.Double(merged[0]["end_seconds"]));
        // The higher-scoring constituent wins the editorial fields.
        Assert.Equal(0.7, JsonUtil.Double(merged[0]["score"]));
        Assert.Equal("clip 1", JsonUtil.Str(merged[0]["title"]));
        Assert.Equal(1, JsonUtil.Int(merged[0]["chunk_index"]));
        Assert.Equal(
            new[] { 0, 1 },
            JsonUtil.Objects(merged[0]["merged_from"])
                .Select(c => JsonUtil.Int(c["chunk_index"]))
                .ToArray());
    }

    [Fact]
    public void AbuttingWithinGapMerges()
    {
        var merged = Scoring.MergeCandidates(
            new[]
            {
                ScoringTestData.Candidate(0, 10, 30),
                ScoringTestData.Candidate(1, 30.8, 40),
            },
            mergeGapSeconds: 1.0,
            maxClipSeconds: 120);
        Assert.Single(merged);
        Assert.Equal(40, JsonUtil.Double(merged[0]["end_seconds"]));
    }

    [Fact]
    public void BeyondGapStaysSeparate()
    {
        var merged = Scoring.MergeCandidates(
            new[]
            {
                ScoringTestData.Candidate(0, 10, 30),
                ScoringTestData.Candidate(1, 31.5, 40),
            },
            mergeGapSeconds: 1.0,
            maxClipSeconds: 120);
        Assert.Equal(2, merged.Count);
        Assert.False(merged[0].ContainsKey("merged_from"));
    }

    [Fact]
    public void MaxLengthCapPreventsMerge()
    {
        var merged = Scoring.MergeCandidates(
            new[]
            {
                ScoringTestData.Candidate(0, 0, 80),
                ScoringTestData.Candidate(1, 79, 130),
            },
            mergeGapSeconds: 1.0,
            maxClipSeconds: 120);
        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void ThreeWayChainMerges()
    {
        var merged = Scoring.MergeCandidates(
            new[]
            {
                ScoringTestData.Candidate(0, 0, 10),
                ScoringTestData.Candidate(0, 9, 20),
                ScoringTestData.Candidate(1, 19, 30),
            },
            mergeGapSeconds: 1.0,
            maxClipSeconds: 120);
        Assert.Single(merged);
        Assert.Equal(0, JsonUtil.Double(merged[0]["start_seconds"]));
        Assert.Equal(30, JsonUtil.Double(merged[0]["end_seconds"]));
        Assert.Equal(3, JsonUtil.Objects(merged[0]["merged_from"]).Count);
    }

    [Fact]
    public void ResearchSourcesUnionDedupedByUrl()
    {
        var a = ScoringTestData.Candidate(0, 10, 30, researchSources: new JsonArray
        {
            new JsonObject { ["title"] = "t", ["url"] = "u1", ["claim"] = "c" },
        });
        var b = ScoringTestData.Candidate(1, 25, 45, researchSources: new JsonArray
        {
            new JsonObject { ["title"] = "t2", ["url"] = "u1", ["claim"] = "c2" },
            new JsonObject { ["title"] = "t3", ["url"] = "u2", ["claim"] = "c3" },
        });
        var merged = Scoring.MergeCandidates(
            new[] { a, b }, mergeGapSeconds: 1.0, maxClipSeconds: 120);
        Assert.Equal(
            new[] { "u1", "u2" },
            JsonUtil.Objects(merged[0]["research_sources"])
                .Select(s => JsonUtil.Str(s["url"]))
                .ToArray());
    }

    [Fact]
    public void UnsortedInputIsSortedFirst()
    {
        var merged = Scoring.MergeCandidates(
            new[]
            {
                ScoringTestData.Candidate(1, 40, 50),
                ScoringTestData.Candidate(0, 10, 20),
            },
            mergeGapSeconds: 1.0,
            maxClipSeconds: 120);
        Assert.Equal(
            new[] { 10.0, 40.0 },
            merged.Select(c => JsonUtil.Double(c["start_seconds"])).ToArray());
    }
}

public class TestCoordinator
{
    [Fact]
    public void RejectsWideContext()
    {
        Assert.Throws<PipelineError>(() => new ClipScoringCoordinator(
            scoreChunk: (chunk, before, after, vs, ve) => (new List<JsonObject>(), ""),
            emitDecision: d => { },
            chunkSeconds: 15,
            contextSeconds: 10,
            concurrency: 2,
            mergeGapSeconds: 1.0,
            maxClipSeconds: 120));
    }

    [Fact]
    public void OutOfOrderCompletionEmitsInChunkOrder()
    {
        var emitted = new List<JsonObject>();
        // Chunk 0 is slow, later chunks fast: completion order is scrambled
        // but emission must stay 0, 1, 2, 3.
        var delays = new Dictionary<int, int> { [0] = 300, [1] = 0, [2] = 100, [3] = 0 };

        (List<JsonObject>, string) Score(
            JsonObject chunk, List<JsonObject> before, List<JsonObject> after, int vs, int ve)
        {
            var index = JsonUtil.Int(chunk["chunk_index"]);
            Thread.Sleep(delays[index]);
            var start = JsonUtil.Double(chunk["start_seconds"]);
            return (
                new List<JsonObject> { ScoringTestData.Candidate(index, start + 5, start + 15) },
                "ok");
        }

        var coordinator = new ClipScoringCoordinator(
            scoreChunk: Score,
            emitDecision: d => emitted.Add(d),
            chunkSeconds: 90,
            contextSeconds: 10,
            concurrency: 4,
            mergeGapSeconds: 1.0,
            maxClipSeconds: 120);
        for (var i = 0; i < 4; i++) coordinator.AddChunk(ScoringTestData.MakeChunk(i));
        coordinator.Finish();
        Assert.Equal(
            new[] { 0, 1, 2, 3 },
            emitted.Select(d => JsonUtil.Int(d["chunk_index"])).ToArray());
        Assert.All(emitted, d => Assert.True(JsonUtil.Truthy(d["is_clip_worthy"])));
    }

    [Fact]
    public void ContextWordsAndVisibleWindow()
    {
        var seen = new Dictionary<int, (List<JsonObject> Before, List<JsonObject> After, int Vs, int Ve)>();

        (List<JsonObject>, string) Score(
            JsonObject chunk, List<JsonObject> before, List<JsonObject> after, int vs, int ve)
        {
            lock (seen)
            {
                seen[JsonUtil.Int(chunk["chunk_index"])] = (before, after, vs, ve);
            }
            return (new List<JsonObject>(), "nothing");
        }

        var coordinator = new ClipScoringCoordinator(
            scoreChunk: Score,
            emitDecision: d => { },
            chunkSeconds: 90,
            contextSeconds: 10,
            concurrency: 2,
            mergeGapSeconds: 1.0,
            maxClipSeconds: 120);
        for (var i = 0; i < 3; i++) coordinator.AddChunk(ScoringTestData.MakeChunk(i));
        coordinator.Finish();

        var (before0, after0, vs0, ve0) = seen[0];
        Assert.Equal((0, 100), (vs0, ve0)); // first chunk: no left context
        Assert.Empty(before0);
        Assert.NotEmpty(after0);
        Assert.All(after0, w =>
        {
            var start = JsonUtil.Double(w["absolute_start"]);
            Assert.True(90 <= start && start < 100);
        });

        var (before1, after1, vs1, ve1) = seen[1];
        Assert.Equal((80, 190), (vs1, ve1));
        Assert.NotEmpty(before1);
        Assert.All(before1, w =>
        {
            var start = JsonUtil.Double(w["absolute_start"]);
            Assert.True(80 <= start && start < 90);
        });
        Assert.NotEmpty(after1);
        Assert.All(after1, w =>
        {
            var start = JsonUtil.Double(w["absolute_start"]);
            Assert.True(180 <= start && start < 190);
        });

        var (_, after2, vs2, ve2) = seen[2];
        Assert.Equal((170, 270), (vs2, ve2)); // last chunk: no right context
        Assert.Empty(after2);
    }

    [Fact]
    public void BoundaryCandidatesFromAdjacentChunksAreStitched()
    {
        var emitted = new List<JsonObject>();

        (List<JsonObject>, string) Score(
            JsonObject chunk, List<JsonObject> before, List<JsonObject> after, int vs, int ve)
        {
            var index = JsonUtil.Int(chunk["chunk_index"]);
            if (index == 0)
            {
                // Ends right at the boundary (within the hold-back margin).
                return (
                    new List<JsonObject> { ScoringTestData.Candidate(0, 70, 95, score: 0.4) },
                    "ok");
            }
            if (index == 1)
            {
                // Continuation proposed by the next chunk.
                return (
                    new List<JsonObject>
                    {
                        ScoringTestData.Candidate(1, 94, 110, score: 0.9, title: "the payoff"),
                    },
                    "ok");
            }
            return (new List<JsonObject>(), "nothing");
        }

        var coordinator = new ClipScoringCoordinator(
            scoreChunk: Score,
            emitDecision: d => emitted.Add(d),
            chunkSeconds: 90,
            contextSeconds: 10,
            concurrency: 2,
            mergeGapSeconds: 1.0,
            maxClipSeconds: 120);
        for (var i = 0; i < 3; i++) coordinator.AddChunk(ScoringTestData.MakeChunk(i));
        coordinator.Finish();

        var worthy = emitted.Where(d => JsonUtil.Truthy(d["is_clip_worthy"])).ToList();
        Assert.Single(worthy);
        Assert.Equal(70, JsonUtil.Double(worthy[0]["start_seconds"]));
        Assert.Equal(110, JsonUtil.Double(worthy[0]["end_seconds"]));
        Assert.Equal("the payoff", JsonUtil.Str(worthy[0]["title"]));
        Assert.Equal(
            new[] { 0, 1 },
            JsonUtil.Objects(worthy[0]["merged_from"])
                .Select(c => JsonUtil.Int(c["chunk_index"]))
                .ToArray());
        // The no-clip chunk still produced a record.
        Assert.Contains(emitted, d => !JsonUtil.Truthy(d["is_clip_worthy"]));
    }

    [Fact]
    public void HeldCandidateFlushesAtFinish()
    {
        var emitted = new List<JsonObject>();

        (List<JsonObject>, string) Score(
            JsonObject chunk, List<JsonObject> before, List<JsonObject> after, int vs, int ve)
        {
            if (JsonUtil.Int(chunk["chunk_index"]) == 1)
            {
                // Last scored chunk, candidate in the hold-back margin.
                return (new List<JsonObject> { ScoringTestData.Candidate(1, 160, 185) }, "ok");
            }
            return (new List<JsonObject>(), "nothing");
        }

        var coordinator = new ClipScoringCoordinator(
            scoreChunk: Score,
            emitDecision: d => emitted.Add(d),
            chunkSeconds: 90,
            contextSeconds: 10,
            concurrency: 2,
            mergeGapSeconds: 1.0,
            maxClipSeconds: 120);
        coordinator.AddChunk(ScoringTestData.MakeChunk(0));
        coordinator.AddChunk(ScoringTestData.MakeChunk(1));
        coordinator.Finish();
        var worthy = emitted.Where(d => JsonUtil.Truthy(d["is_clip_worthy"])).ToList();
        Assert.Single(worthy);
        Assert.Equal(185, JsonUtil.Double(worthy[0]["end_seconds"]));
    }

    [Fact]
    public void ScoringFailureRetriesThenDegrades()
    {
        var emitted = new List<JsonObject>();
        var attempts = new List<int>();

        (List<JsonObject>, string) Score(
            JsonObject chunk, List<JsonObject> before, List<JsonObject> after, int vs, int ve)
        {
            lock (attempts)
            {
                attempts.Add(JsonUtil.Int(chunk["chunk_index"]));
            }
            throw new InvalidOperationException("boom");
        }

        var coordinator = new ClipScoringCoordinator(
            scoreChunk: Score,
            emitDecision: d => emitted.Add(d),
            chunkSeconds: 90,
            contextSeconds: 10,
            concurrency: 2,
            mergeGapSeconds: 1.0,
            maxClipSeconds: 120);
        coordinator.AddChunk(ScoringTestData.MakeChunk(0));
        coordinator.Finish();
        Assert.Equal(new[] { 0, 0 }, attempts.ToArray()); // one retry
        Assert.Single(emitted);
        Assert.False(JsonUtil.Truthy(emitted[0]["is_clip_worthy"]));
        Assert.Contains("LLM scoring failed", JsonUtil.Str(emitted[0]["reason"]));
    }

    [Fact]
    public void CancelledFinishDoesNotHangAndSkipsUndispatched()
    {
        var emitted = new List<JsonObject>();
        var started = new ManualResetEventSlim(false);

        (List<JsonObject>, string) Score(
            JsonObject chunk, List<JsonObject> before, List<JsonObject> after, int vs, int ve)
        {
            started.Set();
            Thread.Sleep(200);
            return (new List<JsonObject>(), "slow");
        }

        var coordinator = new ClipScoringCoordinator(
            scoreChunk: Score,
            emitDecision: d => emitted.Add(d),
            chunkSeconds: 90,
            contextSeconds: 10,
            concurrency: 1, // chunk 1 queues behind chunk 0
            mergeGapSeconds: 1.0,
            maxClipSeconds: 120);
        for (var i = 0; i < 4; i++) coordinator.AddChunk(ScoringTestData.MakeChunk(i));
        started.Wait(TimeSpan.FromSeconds(5));
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        coordinator.Finish(cancelled: true);
        Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(5));
        // Chunk 3 was never dispatched (no next chunk arrived, cancelled), and
        // every dispatched chunk produced exactly one record.
        Assert.Subset(
            new HashSet<int> { 0, 1, 2 },
            emitted.Select(d => JsonUtil.Int(d["chunk_index"])).ToHashSet());
        Assert.All(emitted, d => Assert.False(JsonUtil.Truthy(d["is_clip_worthy"])));
    }

    [Fact]
    public void EmitExceptionDoesNotStopLaterChunks()
    {
        var emitted = new List<JsonObject>();

        void Emit(JsonObject decision)
        {
            if (JsonUtil.Int(decision["chunk_index"]) == 0)
                throw new InvalidOperationException("render exploded");
            emitted.Add(decision);
        }

        var coordinator = new ClipScoringCoordinator(
            scoreChunk: (chunk, before, after, vs, ve) =>
            {
                var index = JsonUtil.Int(chunk["chunk_index"]);
                var start = JsonUtil.Double(chunk["start_seconds"]);
                return (
                    new List<JsonObject>
                    {
                        ScoringTestData.Candidate(index, start + 5, start + 15),
                    },
                    "ok");
            },
            emitDecision: Emit,
            chunkSeconds: 90,
            contextSeconds: 10,
            concurrency: 2,
            mergeGapSeconds: 1.0,
            maxClipSeconds: 120);
        for (var i = 0; i < 2; i++) coordinator.AddChunk(ScoringTestData.MakeChunk(i));
        coordinator.Finish();
        Assert.Equal(
            new[] { 1 },
            emitted.Select(d => JsonUtil.Int(d["chunk_index"])).ToArray());
    }

    [Fact]
    public void ChunkCompleteReportsHeldStateAndFinishCallbackRuns()
    {
        var completions = new List<(int Index, double? MinHeld)>();
        var finished = new List<bool>();

        (List<JsonObject>, string) Score(
            JsonObject chunk, List<JsonObject> before, List<JsonObject> after, int vs, int ve)
        {
            if (JsonUtil.Int(chunk["chunk_index"]) == 0)
            {
                // held (within 10s of 90)
                return (new List<JsonObject> { ScoringTestData.Candidate(0, 70, 95) }, "ok");
            }
            return (new List<JsonObject>(), "nothing");
        }

        var coordinator = new ClipScoringCoordinator(
            scoreChunk: Score,
            emitDecision: d => { },
            chunkSeconds: 90,
            contextSeconds: 10,
            concurrency: 2,
            mergeGapSeconds: 1.0,
            maxClipSeconds: 120,
            onChunkComplete: (index, minHeld) => completions.Add((index, minHeld)),
            onFinish: () => finished.Add(true));
        coordinator.AddChunk(ScoringTestData.MakeChunk(0));
        coordinator.AddChunk(ScoringTestData.MakeChunk(1));
        coordinator.Finish();
        Assert.Equal((0, 70.0), completions[0]); // candidate held with start 70
        Assert.Equal((1, null), completions[1]); // emitted while processing chunk 1
        Assert.Equal(new[] { true }, finished.ToArray());
    }

    [Fact]
    public void NoChunksFinishIsClean()
    {
        var finished = new List<bool>();
        var coordinator = new ClipScoringCoordinator(
            scoreChunk: (chunk, before, after, vs, ve) => (new List<JsonObject>(), ""),
            emitDecision: d => { },
            chunkSeconds: 90,
            contextSeconds: 10,
            concurrency: 2,
            mergeGapSeconds: 1.0,
            maxClipSeconds: 120,
            onFinish: () => finished.Add(true));
        coordinator.Finish();
        Assert.Equal(new[] { true }, finished.ToArray());
    }

    [Fact]
    public void DoubleFinishIsNoop()
    {
        var coordinator = new ClipScoringCoordinator(
            scoreChunk: (chunk, before, after, vs, ve) => (new List<JsonObject>(), "n"),
            emitDecision: d => { },
            chunkSeconds: 90,
            contextSeconds: 10,
            concurrency: 2,
            mergeGapSeconds: 1.0,
            maxClipSeconds: 120);
        coordinator.AddChunk(ScoringTestData.MakeChunk(0));
        coordinator.Finish();
        coordinator.Finish();
    }
}

public class TestNoClipRecord
{
    [Fact]
    public void Shape()
    {
        var record = Scoring.NoClipRecord(3, "nothing happened");
        Assert.Equal(3, JsonUtil.Int(record["chunk_index"]));
        Assert.False(JsonUtil.Truthy(record["is_clip_worthy"]));
        Assert.Equal("nothing happened", JsonUtil.Str(record["reason"]));
    }

    [Fact]
    public void EmptyReasonGetsDefault()
    {
        Assert.True(JsonUtil.Str(Scoring.NoClipRecord(0, "")["reason"]).Length > 0);
    }
}
