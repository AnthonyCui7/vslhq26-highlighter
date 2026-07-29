using System.Text;
using System.Text.Json.Nodes;

namespace Highlighter.Pipeline;

/// <summary>Raised for non-success HTTP statuses — the port of the openai SDK's
/// APIStatusError, carrying the response body for callers that quote it.</summary>
public class OpenRouterStatusException : Exception
{
    public int StatusCode { get; }
    public string ResponseText { get; }

    public OpenRouterStatusException(int statusCode, string responseText)
        : base($"OpenRouter request failed with status {statusCode}")
    {
        StatusCode = statusCode;
        ResponseText = responseText;
    }
}

/// <summary>Chat-completions client for OpenRouter over raw HTTP.
///
/// The Python pipeline calls OpenRouter through the openai SDK; this port
/// speaks the same wire protocol directly so OpenRouter-specific request
/// fields (provider pinning, reasoning effort, plugins) are plain JSON rather
/// than SDK extension points. It also reproduces the SDK behavior the Python
/// code silently relies on: two retries with exponential backoff on
/// 408/429/5xx, connection errors, and timeouts.</summary>
public sealed class OpenRouterClient : IDisposable
{
    public const string OPENROUTER_BASE_URL = "https://openrouter.ai/api/v1";
    public const string OPENROUTER_VERTEX_PROVIDER = "google-vertex";

    private const int MaxRetries = 2;

    private readonly HttpClient _http;

    public OpenRouterClient(string apiKey, double timeoutSeconds, string title)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("HTTP-Referer", "http://localhost");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("X-OpenRouter-Title", title);
    }

    /// <summary>POST /chat/completions; returns the parsed response object.
    /// Throws OpenRouterStatusException for a non-retryable (or retry-exhausted)
    /// HTTP error status.</summary>
    public JsonObject ChatCompletions(JsonObject body)
    {
        var payload = JsonUtil.Dumps(body);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Post, OPENROUTER_BASE_URL + "/chat/completions")
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json"),
                };
                using var response = _http.Send(request, HttpCompletionOption.ResponseContentRead);
                var text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                if (response.IsSuccessStatusCode) return JsonUtil.ParseObject(text);

                var status = (int)response.StatusCode;
                var retryable = status == 408 || status == 429 || status >= 500;
                if (!retryable || attempt >= MaxRetries)
                    throw new OpenRouterStatusException(status, text);
            }
            catch (HttpRequestException) when (attempt < MaxRetries)
            {
                // connection error — retry
            }
            catch (TaskCanceledException) when (attempt < MaxRetries)
            {
                // request timeout — retry (the SDK retries APITimeoutError too)
            }

            Backoff(attempt);
        }
    }

    private static void Backoff(int attempt)
    {
        // The openai SDK's schedule: 0.5s * 2^n with jitter, capped at 8s.
        var seconds = Math.Min(0.5 * Math.Pow(2, attempt), 8.0);
        seconds *= 1.0 - Random.Shared.NextDouble() * 0.25;
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
    }

    public void Dispose() => _http.Dispose();
}
