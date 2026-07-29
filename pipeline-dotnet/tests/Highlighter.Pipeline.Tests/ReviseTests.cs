using System.Text.Json.Nodes;
using Highlighter.Pipeline;
using Xunit;

namespace Highlighter.Pipeline.Tests;

/// <summary>Port of pipeline/tests/test_revise.py.</summary>
public class ReviseTests : IDisposable
{
    private readonly string _tmpPath;

    public ReviseTests()
    {
        _tmpPath = Path.Combine(Path.GetTempPath(), "revise-tests-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_tmpPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpPath, recursive: true); } catch { }
    }

    [Fact]
    public void ValidateRejectsGarbageWithAMessage()
    {
        var (selections, problem) = Revise.ValidateSelections(null, 600.0);
        Assert.Empty(selections);
        Assert.NotNull(problem);
        (selections, problem) = Revise.ValidateSelections(
            new JsonArray
            {
                new JsonObject { ["start_seconds"] = 10, ["end_seconds"] = 11 },
            },
            600.0);
        Assert.Empty(selections);
        Assert.NotNull(problem); // under the 2s minimum
    }

    [Fact]
    public void ValidateClampsSortsAndMergesOverlaps()
    {
        var (selections, problem) = Revise.ValidateSelections(
            new JsonArray
            {
                new JsonObject
                {
                    ["start_seconds"] = 500,
                    ["end_seconds"] = 700,
                    ["reason"] = "tail",
                },
                new JsonObject
                {
                    ["start_seconds"] = -3,
                    ["end_seconds"] = 20,
                    ["reason"] = "open",
                },
                new JsonObject
                {
                    ["start_seconds"] = 15,
                    ["end_seconds"] = 40,
                    ["reason"] = "overlaps open",
                },
            },
            600.0);
        Assert.Null(problem);
        Assert.Equal(2, selections.Count);
        Assert.Equal(0.0, JsonUtil.Double(selections[0]["start_seconds"]));
        Assert.Equal(40.0, JsonUtil.Double(selections[0]["end_seconds"]));
        Assert.Equal("open", JsonUtil.Str(selections[0]["reason"]));
        Assert.Equal(500.0, JsonUtil.Double(selections[1]["start_seconds"]));
        Assert.Equal(600.0, JsonUtil.Double(selections[1]["end_seconds"]));
        Assert.Equal("tail", JsonUtil.Str(selections[1]["reason"]));
    }

    [Fact]
    public void WindowCapsAndValidates()
    {
        var state = MinimalState(sourceEndSeconds: 900.0);
        Assert.Equal(
            (100.0, 400.0),
            Revise.Window(
                new JsonObject { ["start_seconds"] = 100, ["end_seconds"] = 2000 }, state, 300));
        Assert.Throws<PipelineError>(() => Revise.Window(
            new JsonObject { ["start_seconds"] = 50, ["end_seconds"] = 50 }, state, null));
    }

    private static ReviseState MinimalState(double sourceEndSeconds) => new()
    {
        ProjectDir = ".",
        Project = new JsonObject(),
        ProjectId = "p",
        Chunks = new List<JsonObject>(),
        Candidates = new List<JsonObject>(),
        LongformEdits = new List<JsonObject>(),
        Revisions = new List<JsonObject>(),
        WordSpans = new List<(double, double)>(),
        Cuts = new List<double>(),
        TargetLength = "7-15",
        SourceEndSeconds = sourceEndSeconds,
    };

    private string WriteProject(
        string subdir = "root",
        IReadOnlyList<JsonObject>? longformRows = null,
        bool withLongformMp4 = false)
    {
        var projectDir = Path.Combine(_tmpPath, subdir, "projects", "p1");
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(
            Path.Combine(projectDir, "project.json"),
            JsonUtil.Dumps(new JsonObject
            {
                ["id"] = "p1",
                ["source_url"] = "https://example.com/vod",
                ["metadata"] = new JsonObject
                {
                    ["ingest"] = new JsonObject
                    {
                        ["target_length_minutes"] = "7-15",
                        ["source_minutes"] = 30,
                    },
                },
            }));
        var chunks = new[]
        {
            new JsonObject
            {
                ["chunk_index"] = 0,
                ["start_seconds"] = 0,
                ["end_seconds"] = 90,
                ["transcript"] = "hello",
                ["words"] = new JsonArray
                {
                    new JsonObject { ["absolute_start"] = 1.0, ["absolute_end"] = 1.5 },
                },
                ["metadata"] = new JsonObject
                {
                    ["scene_cuts"] = new JsonArray { 12.0 },
                    ["audio_path"] = "audio/audio_00000.wav",
                },
            },
            new JsonObject
            {
                ["chunk_index"] = 1,
                ["start_seconds"] = 90,
                ["end_seconds"] = 180,
                ["transcript"] = "world",
                ["words"] = new JsonArray(),
                ["metadata"] = new JsonObject(),
            },
        };
        File.WriteAllLines(
            Path.Combine(projectDir, "transcript_chunks.jsonl"),
            chunks.Select(chunk => JsonUtil.Dumps(chunk)));
        var clips = new[]
        {
            new JsonObject
            {
                ["start_seconds"] = 10.0,
                ["end_seconds"] = 60.0,
                ["title"] = "Kept",
                ["status"] = "rendered",
                ["metadata"] = new JsonObject
                {
                    ["source"] = "llm",
                    ["render"] = new JsonObject { ["local_path"] = "clips/kept.mp4" },
                },
            },
            new JsonObject
            {
                ["start_seconds"] = 20.0,
                ["end_seconds"] = 45.0,
                ["title"] = "Short-fork clip",
                ["status"] = "rendered",
                ["metadata"] = new JsonObject { ["source"] = "llm", ["pipeline"] = "short" },
            },
            new JsonObject
            {
                ["start_seconds"] = 65.0,
                ["end_seconds"] = 85.0,
                ["title"] = "Long tagged",
                ["status"] = "rendered",
                ["metadata"] = new JsonObject { ["source"] = "llm", ["pipeline"] = "long" },
            },
            new JsonObject
            {
                ["start_seconds"] = 100.0,
                ["end_seconds"] = 130.0,
                ["title"] = "Failed render",
                ["status"] = "failed",
                ["metadata"] = new JsonObject { ["source"] = "llm" },
            },
        };
        File.WriteAllLines(
            Path.Combine(projectDir, "clips.jsonl"),
            clips.Select(clip => JsonUtil.Dumps(clip)));
        if (longformRows is { Count: > 0 })
        {
            File.WriteAllLines(
                Path.Combine(projectDir, "longform_edits.jsonl"),
                longformRows.Select(row => JsonUtil.Dumps(row)));
        }
        if (withLongformMp4)
        {
            Directory.CreateDirectory(Path.Combine(projectDir, "longform"));
            File.WriteAllBytes(
                Path.Combine(projectDir, "longform", "longform.mp4"), Array.Empty<byte>());
        }
        return projectDir;
    }

    [Fact]
    public void LoadStateVersionsAndCandidateFiltering()
    {
        var state = Revise.LoadState(WriteProject());
        Assert.Equal(0, state.CurrentVersion);

        state = Revise.LoadState(WriteProject(subdir: "b", withLongformMp4: true));
        Assert.Equal(1, state.CurrentVersion);

        state = Revise.LoadState(WriteProject(
            subdir: "c",
            longformRows: new List<JsonObject>
            {
                new() { ["version"] = 1 },
                new() { ["version"] = 3 },
            }));
        Assert.Equal(3, state.CurrentVersion);

        // Only rendered long-form LLM candidates survive (an untagged row is an
        // older long-form run; a combined run's short clips are excluded); cuts
        // and word spans flatten.
        Assert.Equal(
            new[] { "Kept", "Long tagged" },
            state.Candidates.Select(candidate => JsonUtil.Str(candidate["title"])).ToArray());
        Assert.Equal(new List<double> { 12.0 }, state.Cuts);
        Assert.Equal(new List<(double, double)> { (1.0, 1.5) }, state.WordSpans);
        Assert.Equal(180, state.SourceEndSeconds);
        Assert.Equal("7-15", state.TargetLength);
    }

    private static (string Path, JsonObject Source) Materialize(
        ReviseState state, double start, double end)
    {
        return Revise.MaterializeSelection(
            state: state,
            startSeconds: start,
            endSeconds: end,
            segmentsDir: Path.Combine(state.ProjectDir, "longform", "segments"),
            version: 2,
            order: 0);
    }

    [Fact]
    public void MaterializeReusesMatchingCandidate()
    {
        var projectDir = WriteProject();
        Directory.CreateDirectory(Path.Combine(projectDir, "clips"));
        File.WriteAllBytes(Path.Combine(projectDir, "clips", "kept.mp4"), Array.Empty<byte>());
        var state = Revise.LoadState(projectDir);

        var (path, source) = Materialize(state, 10.1, 59.9);
        Assert.Equal("candidate", JsonUtil.Str(source["mode"]));
        Assert.Equal("Kept", JsonUtil.Str(source["title"]));
        Assert.Equal("kept.mp4", Path.GetFileName(path));
    }

    [Fact]
    public void MaterializeTrimsInsideCandidateWindow()
    {
        var projectDir = WriteProject();
        Directory.CreateDirectory(Path.Combine(projectDir, "clips"));
        File.WriteAllBytes(Path.Combine(projectDir, "clips", "kept.mp4"), Array.Empty<byte>());
        var state = Revise.LoadState(projectDir);

        var trims = new List<(string Source, string Output, double StartOffset, double Duration)>();
        var original = Revise.TrimClipFn;
        try
        {
            Revise.TrimClipFn = (sourcePath, outputPath, startOffset, duration) =>
                trims.Add((sourcePath, outputPath, startOffset, duration));
            var (path, source) = Materialize(state, 20.0, 45.0);
            Assert.Equal("trimmed", JsonUtil.Str(source["mode"]));
            Assert.Equal(10.0, trims[0].StartOffset);
            Assert.Equal(25.0, trims[0].Duration);
            Assert.Equal("v2_000.mp4", Path.GetFileName(path));
        }
        finally
        {
            Revise.TrimClipFn = original;
        }
    }

    [Fact]
    public void MaterializeFetchesOutsideCandidates()
    {
        var projectDir = WriteProject();
        var state = Revise.LoadState(projectDir);

        var fetches = new List<(string SourceUrl, string Output, double Start, double End)>();
        var original = Revise.RenderClipFromVideoUrlFn;
        try
        {
            Revise.RenderClipFromVideoUrlFn = (sourceUrl, outputPath, start, end) =>
                fetches.Add((sourceUrl, outputPath, start, end));
            var (_, source) = Materialize(state, 140.0, 170.0);
            Assert.Equal("fetched", JsonUtil.Str(source["mode"]));
            Assert.Equal("https://example.com/vod", fetches[0].SourceUrl);
            Assert.Equal(140.0, fetches[0].Start);
        }
        finally
        {
            Revise.RenderClipFromVideoUrlFn = original;
        }
    }

    [Fact]
    public void MaterializeFetchesWhenCandidateFileIsMissing()
    {
        // The candidate window matches but its rendered file is gone from disk.
        var projectDir = WriteProject();
        var state = Revise.LoadState(projectDir);

        var fetches = new List<(string SourceUrl, string Output, double Start, double End)>();
        var original = Revise.RenderClipFromVideoUrlFn;
        try
        {
            Revise.RenderClipFromVideoUrlFn = (sourceUrl, outputPath, start, end) =>
                fetches.Add((sourceUrl, outputPath, start, end));
            var (_, source) = Materialize(state, 10.0, 60.0);
            Assert.Equal("fetched", JsonUtil.Str(source["mode"]));
        }
        finally
        {
            Revise.RenderClipFromVideoUrlFn = original;
        }
    }
}
