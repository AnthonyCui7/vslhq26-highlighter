using System.Text.Json.Nodes;
using Highlighter.Pipeline;
using Xunit;

namespace Highlighter.Pipeline.Tests;

/// <summary>Port of pipeline/tests/test_research.py.</summary>
public sealed class ResearchTests : IDisposable
{
    private static readonly string[] CoreFields =
    {
        "creator_profile",
        "content_context",
        "target_audience",
        "inside_references",
        "recent_context",
        "thumbnail_patterns",
        "avoid",
        "sources",
    };

    private readonly string _tmpDir = Directory.CreateTempSubdirectory("research-tests").FullName;

    public void Dispose() => Directory.Delete(_tmpDir, recursive: true);

    private static HashSet<string> Fields(JsonObject schema) =>
        ((JsonObject)schema["properties"]!).Select(pair => pair.Key).ToHashSet();

    private static HashSet<string> Required(JsonObject schema) =>
        ((JsonArray)schema["required"]!).Select(node => JsonUtil.Str(node)).ToHashSet();

    [Fact]
    public void ShortSchemaResearchesClipCraft()
    {
        var schema = Research.ResearchSchema("short");
        var fields = Fields(schema);
        Assert.True(CoreFields.All(fields.Contains));
        Assert.True(new[] { "successful_clip_patterns", "useful_hooks", "platform_notes" }
            .All(fields.Contains));
        Assert.DoesNotContain("structure_patterns", fields);
        Assert.Equal(fields, Required(schema));
        Assert.False(JsonUtil.Truthy(schema["additionalProperties"]));
    }

    [Fact]
    public void LongSchemaResearchesStructureAndRetention()
    {
        var schema = Research.ResearchSchema("long");
        var fields = Fields(schema);
        Assert.True(CoreFields.All(fields.Contains));
        Assert.True(new[] { "structure_patterns", "pacing_and_retention", "title_patterns" }
            .All(fields.Contains));
        Assert.DoesNotContain("useful_hooks", fields);
        Assert.Equal(fields, Required(schema));
        Assert.False(JsonUtil.Truthy(schema["additionalProperties"]));
    }

    [Fact]
    public void SystemPromptsSpecializePerMode()
    {
        var shortPrompt = Research.ResearchSystemPrompt("short");
        var longPrompt = Research.ResearchSystemPrompt("long");
        Assert.NotEqual(shortPrompt, longPrompt);
        Assert.Contains("short-form vertical clips", shortPrompt);
        Assert.Contains("TikTok/Reels/Shorts/X", shortPrompt);
        Assert.DoesNotContain("retention", shortPrompt);
        Assert.Contains("long-form edit", longPrompt);
        Assert.Contains("retention", longPrompt);
        Assert.DoesNotContain("TikTok", longPrompt);
        // The shared framing survives in both.
        foreach (var prompt in new[] { shortPrompt, longPrompt })
        {
            Assert.Contains("Inside references", prompt);
            Assert.Contains("Return ONLY one JSON object", prompt);
        }
    }

    [Fact]
    public void WriteResearchFilenamesPerMode()
    {
        var records = new ProjectRecords(_tmpDir);
        records.WriteResearch(new JsonObject { ["creator_profile"] = "long fork" });
        records.WriteResearch(
            new JsonObject { ["creator_profile"] = "short fork" }, mode: "short");
        Assert.True(File.Exists(Path.Combine(_tmpDir, "research.json")));
        Assert.True(File.Exists(Path.Combine(_tmpDir, "research_short.json")));
        Assert.Contains("long fork", File.ReadAllText(Path.Combine(_tmpDir, "research.json")));
        Assert.Contains(
            "short fork", File.ReadAllText(Path.Combine(_tmpDir, "research_short.json")));
    }

    [Fact]
    public void ClipFilenameSuffixDisambiguatesForks()
    {
        var plain = Render.ClipFilename(chunkIndex: 3, startSeconds: 10.0, endSeconds: 20.5);
        Assert.Equal("clip_00003_10000_20500.mp4", plain);
        var shortName = Render.ClipFilename(
            chunkIndex: 3, startSeconds: 10.0, endSeconds: 20.5, suffix: "_short");
        var longName = Render.ClipFilename(
            chunkIndex: 3, startSeconds: 10.0, endSeconds: 20.5, suffix: "_long");
        Assert.Equal("clip_00003_10000_20500_short.mp4", shortName);
        Assert.Equal("clip_00003_10000_20500_long.mp4", longName);
        Assert.Equal(3, new[] { plain, shortName, longName }.Distinct().Count());
    }
}
