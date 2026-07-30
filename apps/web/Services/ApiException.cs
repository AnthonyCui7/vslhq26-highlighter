using System.Net.Http.Json;
using Highlighter.Web.Models;

namespace Highlighter.Web.Services;

/// <summary>A non-2xx API response, carrying the ProblemDetails body when there
/// was one. UserMessage is safe to show in the UI verbatim.</summary>
public sealed class ApiException(int status, string? title, string? detail)
    : Exception(detail ?? title ?? $"Request failed ({status})")
{
    public int Status { get; } = status;
    public string? Title { get; } = title;
    public string UserMessage => Message;

    public static async Task<ApiException> FromResponseAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        ProblemDto? problem = null;
        try
        {
            problem = await response.Content.ReadFromJsonAsync<ProblemDto>(ct);
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or NotSupportedException)
        {
            // 401s from the JWT gate (and proxies) have empty/non-JSON bodies.
        }
        return new ApiException((int)response.StatusCode, problem?.Title, problem?.Detail);
    }
}
