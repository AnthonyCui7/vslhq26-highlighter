using System.Text;
using System.Text.Json.Nodes;

namespace Highlighter.Api.Services;

/// <summary>Supabase Storage client for the public clips bucket (service-role
/// key, server-side only). Uploads are upserts so deterministic paths — the
/// editor's export objects, imported thumbnails — overwrite instead of piling
/// up versions.</summary>
public sealed class SupabaseStorage
{
    public const string HttpClientName = "supabase-storage";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http;
    private readonly ILogger _log;
    private readonly string? _baseUrl;
    private readonly string? _key;

    public string Bucket { get; }

    public SupabaseStorage(HttpClient http, ILogger<SupabaseStorage> log,
        string? baseUrl, string? key, string bucket)
    {
        _http = http;
        _log = log;
        _baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl.TrimEnd('/');
        _key = string.IsNullOrWhiteSpace(key) ? null : key;
        Bucket = bucket;
    }

    public static SupabaseStorage FromEnv(IHttpClientFactory factory, ILogger<SupabaseStorage> log) =>
        new(factory.CreateClient(HttpClientName), log,
            Environment.GetEnvironmentVariable("SUPABASE_URL"),
            Environment.GetEnvironmentVariable("SUPABASE_SERVICE_ROLE_KEY")
                ?? Environment.GetEnvironmentVariable("SUPABASE_SECRET_KEY"),
            Environment.GetEnvironmentVariable("SUPABASE_CLIPS_BUCKET") ?? "clips");

    public bool IsConfigured => _baseUrl is not null && _key is not null;

    public string PublicUrl(string path) =>
        $"{_baseUrl}/storage/v1/object/public/{Bucket}/{path}";

    public async Task UploadAsync(string path, byte[] content, string contentType,
        CancellationToken ct = default)
    {
        using var request = Request(HttpMethod.Post, $"/storage/v1/object/{Bucket}/{path}");
        request.Headers.TryAddWithoutValidation("x-upsert", "true");
        request.Content = new ByteArrayContent(content);
        request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        await SendAsync(request, $"upload {path}", ct);
    }

    public async Task UploadFileAsync(string path, string localFile, string contentType,
        CancellationToken ct = default) =>
        await UploadAsync(path, await File.ReadAllBytesAsync(localFile, ct), contentType, ct);

    /// <summary>Object names directly under a prefix (flat listing, paged).</summary>
    public async Task<List<string>> ListAsync(string prefix, CancellationToken ct = default)
    {
        var names = new List<string>();
        for (var offset = 0; ; offset += 100)
        {
            using var request = Request(HttpMethod.Post, $"/storage/v1/object/list/{Bucket}");
            request.Content = new StringContent(
                new JsonObject { ["prefix"] = prefix, ["limit"] = 100, ["offset"] = offset }.ToJsonString(),
                Encoding.UTF8, "application/json");
            var body = await SendAsync(request, $"list {prefix}", ct);
            var page = (JsonNode.Parse(body) as JsonArray ?? [])
                .OfType<JsonObject>()
                .Select(entry => entry["name"]?.GetValue<string>())
                .OfType<string>()
                .ToList();
            names.AddRange(page);
            if (page.Count < 100) return names;
        }
    }

    public async Task DeleteAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        if (paths.Count == 0) return;
        using var request = Request(HttpMethod.Delete, $"/storage/v1/object/{Bucket}");
        request.Content = new StringContent(
            new JsonObject { ["prefixes"] = new JsonArray(paths.Select(p => (JsonNode)p).ToArray()) }
                .ToJsonString(),
            Encoding.UTF8, "application/json");
        await SendAsync(request, $"delete {paths.Count} object(s)", ct);
    }

    /// <summary>Best-effort removal of everything under {prefix}/ — used on
    /// project delete for objects the DB outbox triggers don't know about
    /// (editor exports, thumbnail variants).</summary>
    public async Task<int> DeletePrefixAsync(string prefix, CancellationToken ct = default)
    {
        if (!IsConfigured) return 0;
        try
        {
            var names = await ListAsync(prefix, ct);
            var paths = names.Select(name => $"{prefix}/{name}").ToList();
            await DeleteAsync(paths, ct);
            return paths.Count;
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException)
        {
            _log.LogWarning("Storage prefix sweep {Prefix} failed: {Message}", prefix, exception.Message);
            return 0;
        }
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        if (!IsConfigured)
            throw new InvalidOperationException(
                "Supabase storage is not configured (SUPABASE_URL + service key)");
        var request = new HttpRequestMessage(method, _baseUrl + path);
        request.Headers.TryAddWithoutValidation("apikey", _key);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_key}");
        return request;
    }

    private async Task<string> SendAsync(HttpRequestMessage request, string operation, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException($"storage {operation} timed out");
        }
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(CancellationToken.None);
            if (!response.IsSuccessStatusCode)
            {
                _log.LogError("Storage {Operation} -> {Status}: {Body}", operation,
                    (int)response.StatusCode, text.Length > 300 ? text[..300] : text);
                throw new InvalidOperationException(
                    $"storage {operation} failed ({(int)response.StatusCode})");
            }
            return text;
        }
    }
}
