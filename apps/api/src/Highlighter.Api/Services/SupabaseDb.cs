using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Highlighter.Api.Infrastructure;

namespace Highlighter.Api.Services;

/// <summary>Async PostgREST client for the hosted Supabase project, authenticated
/// with the service-role key (RLS defines no write policies at all, and no anon
/// read policies — every API access goes through this trusted server-side path).
/// Every query string the API sends lives in this class.</summary>
public sealed class SupabaseDb
{
    public const string HttpClientName = "supabase";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly ILogger<SupabaseDb> _log;
    private readonly string? _baseUrl;
    private readonly string? _key;

    public SupabaseDb(HttpClient http, ILogger<SupabaseDb> log, string? baseUrl, string? key)
    {
        _http = http;
        _log = log;
        _baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl.TrimEnd('/');
        _key = string.IsNullOrWhiteSpace(key) ? null : key;
    }

    /// <summary>Same env contract as the pipeline worker: SUPABASE_URL plus
    /// SUPABASE_SERVICE_ROLE_KEY (or the newer SUPABASE_SECRET_KEY alias).</summary>
    public static SupabaseDb FromEnv(IHttpClientFactory factory, ILogger<SupabaseDb> log) =>
        new(factory.CreateClient(HttpClientName), log,
            Environment.GetEnvironmentVariable("SUPABASE_URL"),
            FirstNonEmpty("SUPABASE_SERVICE_ROLE_KEY", "SUPABASE_SECRET_KEY"));

    public bool IsConfigured => _baseUrl is not null && _key is not null;

    public async Task<bool> ProbeAsync(CancellationToken ct = default)
    {
        if (!IsConfigured) return false;
        try
        {
            await GetArrayAsync("projects", "select=id&limit=1", ct);
            return true;
        }
        catch (PostgrestException exception)
        {
            _log.LogWarning("Supabase probe failed: {Message}", exception.Message);
            return false;
        }
    }

    // ---- projects ----

    public Task<JsonArray> ListProjectsAsync(int limit, CancellationToken ct = default) =>
        GetArrayAsync("projects",
            "select=*,clips(count),transcript_chunks(count),longform_edits(count)"
            + $"&order=created_at.desc&limit={limit}", ct);

    public async Task<JsonObject?> GetProjectDetailAsync(Guid id, CancellationToken ct = default) =>
        FirstOrNull(await GetArrayAsync("projects",
            $"id=eq.{id}&select=*,clips(*),longform_edits(*),publications(*),transcript_chunks(count)"
            + "&clips.order=start_seconds.asc&longform_edits.order=version.desc"
            + "&publications.order=created_at.desc", ct));

    public async Task<JsonObject?> GetProjectAsync(Guid id, string select = "*", CancellationToken ct = default) =>
        FirstOrNull(await GetArrayAsync("projects", $"id=eq.{id}&select={select}", ct));

    public async Task<string?> GetProjectStatusAsync(Guid id, CancellationToken ct = default)
    {
        var row = await GetProjectAsync(id, "status", ct);
        return row?["status"]?.GetValue<string>();
    }

    public async Task<JsonObject> InsertProjectAsync(JsonObject row, CancellationToken ct = default)
    {
        var body = await SendAsync(HttpMethod.Post, "projects", "select=*", row,
            "return=representation", ct);
        return FirstOrNull(AsArray(body, "POST projects"))
            ?? throw new PostgrestException("POST projects", null, "insert returned no row");
    }

    /// <summary>The worker's compare-and-set idiom: PATCH filtered on both id and the
    /// allowed current statuses. Null means the guard matched nothing — someone else
    /// (usually the worker) moved the row first.</summary>
    public async Task<JsonObject?> PatchProjectGuardedAsync(
        Guid id, IReadOnlyList<string> whenStatusIn, JsonObject patch, CancellationToken ct = default)
    {
        var guard = string.Join(",", whenStatusIn);
        var body = await SendAsync(HttpMethod.Patch, "projects",
            $"id=eq.{id}&status=in.({guard})&select=*", patch, "return=representation", ct);
        return FirstOrNull(AsArray(body, "PATCH projects"));
    }

    /// <summary>True when a row was deleted. Cascades and the media-cleanup outbox
    /// triggers fire inside Postgres.</summary>
    public async Task<bool> DeleteProjectAsync(Guid id, CancellationToken ct = default)
    {
        var body = await SendAsync(HttpMethod.Delete, "projects",
            $"id=eq.{id}&select=id", null, "return=representation", ct);
        return AsArray(body, "DELETE projects").Count > 0;
    }

    public Task<JsonArray> ListNonTerminalProjectsAsync(CancellationToken ct = default) =>
        GetArrayAsync("projects",
            "status=in.(created,ingesting,stopping)&select=id,status,instance_id,updated_at"
            + "&order=created_at.desc", ct);

    // ---- child tables ----

    public Task<JsonArray> ListClipsAsync(
        Guid projectId, string? pipeline, string? status, string orderBy, CancellationToken ct = default)
    {
        var order = orderBy == "start" ? "start_seconds.asc" : "score.desc.nullslast";
        var query = new StringBuilder($"project_id=eq.{projectId}&select=*&order={order}");
        // "->>" must be pre-encoded: RFC 3986 forbids raw ">" in URLs.
        if (pipeline is not null) query.Append($"&metadata-%3E%3Epipeline=eq.{Uri.EscapeDataString(pipeline)}");
        if (status is not null) query.Append($"&status=eq.{Uri.EscapeDataString(status)}");
        return GetArrayAsync("clips", query.ToString(), ct);
    }

    public async Task<JsonObject?> GetClipAsync(Guid clipId, Guid projectId, CancellationToken ct = default) =>
        FirstOrNull(await GetArrayAsync("clips",
            $"id=eq.{clipId}&project_id=eq.{projectId}&select=*", ct));

    public Task<JsonArray> ListLongformEditsAsync(Guid projectId, CancellationToken ct = default) =>
        GetArrayAsync("longform_edits", $"project_id=eq.{projectId}&select=*&order=version.desc", ct);

    public async Task<bool> HasRenderedLongformAsync(Guid projectId, CancellationToken ct = default)
    {
        var rows = await GetArrayAsync("longform_edits",
            $"project_id=eq.{projectId}&status=eq.rendered&select=id&limit=1", ct);
        return rows.Count > 0;
    }

    public Task<JsonArray> ListPublicationsAsync(Guid projectId, CancellationToken ct = default) =>
        GetArrayAsync("publications", $"project_id=eq.{projectId}&select=*&order=created_at.desc", ct);

    public Task<JsonArray> ListTranscriptChunksAsync(
        Guid projectId, bool includeWords, CancellationToken ct = default)
    {
        var columns = "id,chunk_index,start_seconds,end_seconds,transcript";
        if (includeWords) columns += ",words";
        return GetArrayAsync("transcript_chunks",
            $"project_id=eq.{projectId}&select={columns}&order=chunk_index.asc", ct);
    }

    // ---- media cleanup outbox ----

    public async Task<int> CountCleanupJobsAsync(string status, CancellationToken ct = default)
    {
        using var response = await SendRawAsync(HttpMethod.Get, "media_cleanup_jobs",
            $"select=id&status=eq.{status}&limit=1", null, "count=exact", ct);
        // PostgREST reports the exact total in Content-Range: "0-0/57" (or "*/0").
        var contentRange = response.Content.Headers.ContentRange;
        if (contentRange?.Length is { } length) return (int)length;
        var raw = response.Content.Headers.TryGetValues("Content-Range", out var values)
            ? values.FirstOrDefault()
            : null;
        var slash = raw?.LastIndexOf('/') ?? -1;
        return slash >= 0 && int.TryParse(raw![(slash + 1)..], out var total) ? total : 0;
    }

    // ---- request core ----

    public async Task<JsonArray> GetArrayAsync(string table, string query, CancellationToken ct = default)
    {
        var body = await SendAsync(HttpMethod.Get, table, query, null, null, ct);
        return AsArray(body, $"GET {table}");
    }

    private async Task<JsonNode?> SendAsync(
        HttpMethod method, string table, string query, JsonNode? body, string? prefer, CancellationToken ct)
    {
        using var response = await SendRawAsync(method, table, query, body, prefer, ct);
        var text = await response.Content.ReadAsStringAsync(CancellationToken.None);
        return text.Length == 0 ? null : JsonNode.Parse(text);
    }

    private async Task<HttpResponseMessage> SendRawAsync(
        HttpMethod method, string table, string query, JsonNode? body, string? prefer, CancellationToken ct)
    {
        var operation = $"{method.Method} {table}";
        if (!IsConfigured)
            throw new PostgrestException(operation, null,
                "Supabase is not configured: set SUPABASE_URL and SUPABASE_SERVICE_ROLE_KEY "
                + "(or SUPABASE_SECRET_KEY) in the repo-root .env");

        using var request = new HttpRequestMessage(method, $"{_baseUrl}/rest/v1/{table}?{query}");
        request.Headers.TryAddWithoutValidation("apikey", _key);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_key}");
        if (prefer is not null) request.Headers.TryAddWithoutValidation("Prefer", prefer);
        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new PostgrestException(operation, null, "timed out");
        }
        catch (HttpRequestException exception)
        {
            throw new PostgrestException(operation, null, exception.Message);
        }

        if (response.IsSuccessStatusCode) return response;

        var status = response.StatusCode;
        var errorBody = await response.Content.ReadAsStringAsync(CancellationToken.None);
        response.Dispose();
        var detail = errorBody.Length > 500 ? errorBody[..500] : errorBody;
        _log.LogError("Supabase {Operation} failed with {Status}: {Detail}", operation, (int)status, detail);
        throw new PostgrestException(operation, status, detail);
    }

    private static JsonArray AsArray(JsonNode? body, string operation) =>
        body as JsonArray
        ?? throw new PostgrestException(operation, null, $"expected a JSON array, got: {body?.ToJsonString() ?? "empty body"}");

    private static JsonObject? FirstOrNull(JsonArray rows) =>
        rows.Count > 0 ? rows[0] as JsonObject : null;

    private static string? FirstNonEmpty(params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrEmpty(value)) return value;
        }
        return null;
    }
}
