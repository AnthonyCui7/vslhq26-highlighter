using System.Text.Json.Nodes;
using Highlighter.Pipeline;
using Xunit;

namespace Highlighter.Pipeline.Tests;

/// <summary>Port of pipeline/tests/test_thumbnails.py.</summary>
public class TestConceptsSchema
{
    [Fact]
    public void Shape()
    {
        var schema = Thumbnails.ThumbnailConceptsSchema();
        var properties = (JsonObject)schema["properties"]!;
        var required = ((JsonArray)schema["required"]!)
            .Select(node => JsonUtil.Str(node)).ToHashSet();
        Assert.Equal(properties.Select(pair => pair.Key).ToHashSet(), required);
        Assert.False(JsonUtil.Truthy(schema["additionalProperties"]));
        var item = (JsonObject)((JsonObject)properties["concepts"]!)["items"]!;
        var itemRequired = ((JsonArray)item["required"]!)
            .Select(node => JsonUtil.Str(node)).ToHashSet();
        Assert.Equal(
            new HashSet<string> { "direction", "image_prompt", "overlay_text" }, itemRequired);
        Assert.False(JsonUtil.Truthy(item["additionalProperties"]));
    }
}

public class TestReferenceOffsets
{
    private static JsonObject Entry(double start, double end) => new()
    {
        ["start_seconds"] = start,
        ["end_seconds"] = end,
    };

    [Fact]
    public void MidpointsInOutputTimeline()
    {
        // Segments cut from 100-160 and 300-340 land at 0-60 and 60-100 in the
        // stitched output; midpoints are positions there, not source times.
        var offsets = Thumbnails.ReferenceOffsets(
            new List<JsonObject> { Entry(100, 160), Entry(300, 340) });
        Assert.Equal(new List<double> { 30.0, 80.0 }, offsets);
    }

    [Fact]
    public void ThinnedToMaxFrames()
    {
        var entries = Enumerable.Range(0, 10)
            .Select(i => Entry(i * 10, i * 10 + 10)).ToList();
        var offsets = Thumbnails.ReferenceOffsets(entries);
        Assert.Equal(Thumbnails.MAX_REFERENCE_FRAMES, offsets.Count);
        Assert.Equal(5.0, offsets[0]);
        Assert.Equal(95.0, offsets[^1]);
        Assert.Equal(offsets.OrderBy(x => x).ToList(), offsets);
    }
}

public class TestImagePrompt
{
    [Fact]
    public void OverlayTextIncluded()
    {
        var prompt = Thumbnails.ImagePrompt(
            new JsonObject
            {
                ["image_prompt"] = "Two hosts at a desk.",
                ["overlay_text"] = "HE'S BACK",
            },
            "The Reunion");
        Assert.Contains("Render the text \"HE'S BACK\"", prompt);
        Assert.Contains("\"The Reunion\"", prompt);
    }

    [Fact]
    public void NoOverlayRequestsTextlessImage()
    {
        var prompt = Thumbnails.ImagePrompt(
            new JsonObject
            {
                ["image_prompt"] = "Two hosts at a desk.",
                ["overlay_text"] = " ",
            },
            null);
        Assert.Contains("Do not render any text", prompt);
    }
}
