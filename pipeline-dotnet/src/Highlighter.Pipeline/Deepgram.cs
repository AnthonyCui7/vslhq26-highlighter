using System.Text.Json.Nodes;

namespace Highlighter.Pipeline;

/// <summary>Port of highlighter_pipeline/deepgram.py.</summary>
public static class Deepgram
{
    private static readonly HttpClient Http = new()
    {
        Timeout = System.Threading.Timeout.InfiniteTimeSpan,
    };

    public static JsonObject TranscribeAudioFile(
        string audioPath, string model = Defaults.DEFAULT_DEEPGRAM_MODEL)
    {
        var effectiveModel = string.IsNullOrEmpty(model) ? Defaults.DEFAULT_DEEPGRAM_MODEL : model;
        var query = string.Join("&", new[]
        {
            $"model={Uri.EscapeDataString(effectiveModel)}",
            "smart_format=true",
            "paragraphs=true",
            "utterances=true",
        });

        string payload;
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"https://api.deepgram.com/v1/listen?{query}")
            {
                Content = new ByteArrayContent(File.ReadAllBytes(audioPath)),
            };
            request.Content.Headers.TryAddWithoutValidation("Content-Type", "audio/wav");
            request.Headers.TryAddWithoutValidation(
                "Authorization", $"Token {Config.RequiredEnv("DEEPGRAM_API_KEY")}");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            using var response = Http.Send(request, HttpCompletionOption.ResponseContentRead, cts.Token);
            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
                throw new PipelineError($"Deepgram request failed: {body}");
            payload = body;
        }
        catch (HttpRequestException exc)
        {
            throw new PipelineError($"Deepgram request failed: {exc.Message}", exc);
        }
        catch (TaskCanceledException exc)
        {
            throw new PipelineError("Deepgram request failed: timed out", exc);
        }

        var data = JsonUtil.ParseObject(payload);
        var channels = data["results"]?["channels"] as JsonArray;
        var alternatives = channels is { Count: > 0 }
            ? channels[0]?["alternatives"] as JsonArray
            : null;
        var alternative = alternatives is { Count: > 0 }
            ? alternatives[0] as JsonObject ?? new JsonObject()
            : new JsonObject();
        return new JsonObject
        {
            ["transcript"] = alternative.TryGetPropertyValue("transcript", out var transcript)
                ? JsonUtil.C(transcript) ?? ""
                : "",
            ["words"] = alternative["words"] is JsonArray words
                ? (JsonArray)words.DeepClone()
                : new JsonArray(),
            ["metadata"] = data["metadata"] is JsonObject metadata
                ? (JsonObject)metadata.DeepClone()
                : new JsonObject(),
        };
    }
}
