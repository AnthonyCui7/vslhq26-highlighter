using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;

namespace Highlighter.Api.Tests;

/// <summary>Records every request and replays canned responses. Default response is
/// 200 with an empty JSON array (the PostgREST empty result).</summary>
public sealed class RecordingHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new();

    public List<CapturedRequest> Requests { get; } = [];

    public void Enqueue(HttpStatusCode status, string body, string? contentRange = null)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (contentRange is not null)
            response.Content.Headers.TryAddWithoutValidation("Content-Range", contentRange);
        _responses.Enqueue(response);
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(new CapturedRequest(
            request.Method,
            request.RequestUri!.OriginalString,
            request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken),
            request.Headers.ToDictionary(
                header => header.Key,
                header => string.Join(",", header.Value))));
        return _responses.Count > 0
            ? _responses.Dequeue()
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json"),
            };
    }
}

public sealed record CapturedRequest(
    HttpMethod Method, string Url, string? Body, Dictionary<string, string> Headers);

public static class TestDb
{
    public static (Services.SupabaseDb Db, RecordingHandler Handler) Create(
        string baseUrl = "https://stub.supabase.local", string key = "test-service-role-key")
    {
        var handler = new RecordingHandler();
        var db = new Services.SupabaseDb(
            new HttpClient(handler),
            NullLogger<Services.SupabaseDb>.Instance,
            baseUrl,
            key);
        return (db, handler);
    }
}
