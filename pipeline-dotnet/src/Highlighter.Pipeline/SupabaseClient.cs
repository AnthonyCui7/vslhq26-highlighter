using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Highlighter.Pipeline;

/// <summary>Port of highlighter_pipeline/supabase_client.py: PostgREST + Storage
/// over raw HTTP.</summary>
public class SupabaseClient
{
    private static readonly HttpClient Http = new()
    {
        Timeout = System.Threading.Timeout.InfiniteTimeSpan,
    };

    public string BaseUrl { get; }
    public string Key { get; }

    public SupabaseClient()
    {
        BaseUrl = Config.RequiredEnv("SUPABASE_URL").TrimEnd('/');
        Key = Config.RequiredAnyEnv(new[] { "SUPABASE_SERVICE_ROLE_KEY", "SUPABASE_SECRET_KEY" });
    }

    public string CreateProject(
        string name,
        string sourceType,
        string sourceUrl,
        string status = "ingesting",
        JsonObject? metadata = null)
    {
        var rows = Request(
            "POST",
            "projects",
            query: new() { ["select"] = "id" },
            body: new JsonObject
            {
                ["name"] = name,
                ["source_type"] = sourceType,
                ["source_url"] = sourceUrl,
                ["status"] = status,
                ["metadata"] = metadata is null ? new JsonObject() : JsonUtil.CloneObj(metadata),
            },
            prefer: "return=representation");
        return JsonUtil.Str((rows as JsonArray)?[0]?["id"]);
    }

    public void SetProjectStatus(
        string projectId, string status, JsonObject? metadata = null, string? error = null)
    {
        var body = new JsonObject { ["status"] = status };
        if (metadata is not null) body["metadata"] = JsonUtil.CloneObj(metadata);
        if (error is not null) body["error"] = error;

        Request(
            "PATCH",
            "projects",
            query: new() { ["id"] = $"eq.{projectId}" },
            body: body,
            prefer: "return=minimal");
    }

    /// <summary>PATCH the project only while its status is one of whenStatusIn, so the
    /// worker never overwrites a terminal/cancellation status the router wrote.
    /// Returns the updated row, or null when the guard matched nothing.</summary>
    public JsonObject? UpdateProjectStatusGuarded(
        string projectId,
        string status,
        IReadOnlyList<string> whenStatusIn,
        JsonObject? metadata = null,
        string? error = null)
    {
        var body = new JsonObject { ["status"] = status };
        if (metadata is not null) body["metadata"] = JsonUtil.CloneObj(metadata);
        if (error is not null) body["error"] = error;

        var rows = Request(
            "PATCH",
            "projects",
            query: new()
            {
                ["id"] = $"eq.{projectId}",
                ["status"] = $"in.({string.Join(",", whenStatusIn)})",
            },
            body: body,
            prefer: "return=representation");
        return rows is JsonArray { Count: > 0 } array ? array[0] as JsonObject : null;
    }

    /// <summary>PATCH only the metadata jsonb, leaving status untouched.</summary>
    public void UpdateProjectMetadata(string projectId, JsonObject metadata)
    {
        Request(
            "PATCH",
            "projects",
            query: new() { ["id"] = $"eq.{projectId}" },
            body: new JsonObject { ["metadata"] = JsonUtil.CloneObj(metadata) },
            prefer: "return=minimal");
    }

    public string? GetProjectStatus(string projectId)
    {
        var rows = Request(
            "GET",
            "projects",
            query: new() { ["id"] = $"eq.{projectId}", ["select"] = "status" });
        return rows is JsonArray { Count: > 0 } array
            ? JsonUtil.StrOrNull(array[0]?["status"])
            : null;
    }

    public void InsertTranscriptChunk(
        string projectId,
        int chunkIndex,
        double startSeconds,
        double endSeconds,
        string transcript,
        JsonArray words,
        JsonObject? metadata = null)
    {
        Request(
            "POST",
            "transcript_chunks",
            body: new JsonObject
            {
                ["project_id"] = projectId,
                ["chunk_index"] = chunkIndex,
                ["start_seconds"] = startSeconds,
                ["end_seconds"] = endSeconds,
                ["transcript"] = transcript,
                ["words"] = (JsonArray)words.DeepClone(),
                ["metadata"] = metadata is null ? new JsonObject() : JsonUtil.CloneObj(metadata),
            },
            prefer: "return=minimal");
    }

    public void InsertClip(
        string projectId,
        string title,
        double startSeconds,
        double endSeconds,
        string? description = null,
        JsonNode? score = null,
        string? videoUrl = null,
        string? verticalUrl = null,
        string? status = null,
        JsonObject? metadata = null)
    {
        var body = new JsonObject
        {
            ["project_id"] = projectId,
            ["title"] = title,
            ["start_seconds"] = startSeconds,
            ["end_seconds"] = endSeconds,
            ["description"] = description,
            ["score"] = JsonUtil.C(score),
            ["video_url"] = videoUrl,
            ["vertical_url"] = verticalUrl,
            ["metadata"] = metadata is null ? new JsonObject() : JsonUtil.CloneObj(metadata),
        };
        if (status is not null) body["status"] = status;

        Request("POST", "clips", body: body, prefer: "return=minimal");
    }

    public void InsertLongformEdit(
        string projectId,
        int version,
        string status = "rendered",
        string? videoUrl = null,
        string? thumbnailUrl = null,
        double? durationSeconds = null,
        JsonArray? segments = null,
        JsonObject? editor = null,
        JsonObject? revision = null,
        JsonObject? metadata = null)
    {
        Request(
            "POST",
            "longform_edits",
            body: new JsonObject
            {
                ["project_id"] = projectId,
                ["version"] = version,
                ["status"] = status,
                ["video_url"] = videoUrl,
                ["thumbnail_url"] = thumbnailUrl,
                ["duration_seconds"] = durationSeconds,
                ["segments"] = segments is null ? new JsonArray() : (JsonArray)segments.DeepClone(),
                ["editor"] = editor is null ? null : JsonUtil.CloneObj(editor),
                ["revision"] = revision is null ? null : JsonUtil.CloneObj(revision),
                ["metadata"] = metadata is null ? new JsonObject() : JsonUtil.CloneObj(metadata),
            },
            prefer: "return=minimal");
    }

    public JsonObject GetProject(string projectId)
    {
        var rows = Request(
            "GET",
            "projects",
            query: new() { ["id"] = $"eq.{projectId}", ["select"] = "*" });
        if (rows is not JsonArray { Count: > 0 } array || array[0] is not JsonObject row)
            throw new PipelineError($"Project not found: {projectId}");
        return row;
    }

    public List<JsonObject> PendingCleanupJobs(int limit = 100)
    {
        var rows = Request(
            "GET",
            "media_cleanup_jobs",
            query: new()
            {
                ["status"] = "eq.pending",
                ["order"] = "created_at",
                ["limit"] = limit.ToString(CultureInfo.InvariantCulture),
            });
        return JsonUtil.Objects(rows);
    }

    public void FinishCleanupJob(string jobId, string status, string? error = null)
    {
        var body = new JsonObject
        {
            ["status"] = status,
            ["completed_at"] = DateTime.UtcNow.ToString(
                "yyyy-MM-dd'T'HH:mm:ss.ffffff+00:00", CultureInfo.InvariantCulture),
        };
        if (error is not null) body["last_error"] = error;

        Request(
            "PATCH",
            "media_cleanup_jobs",
            query: new() { ["id"] = $"eq.{jobId}" },
            body: body,
            prefer: "return=minimal");
    }

    public void BumpCleanupAttempts(string jobId, int attempts)
    {
        Request(
            "PATCH",
            "media_cleanup_jobs",
            query: new() { ["id"] = $"eq.{jobId}" },
            body: new JsonObject { ["attempts"] = attempts },
            prefer: "return=minimal");
    }

    public void DeleteStorageObject(string bucket, string key)
    {
        var storagePath = $"{Uri.EscapeDataString(bucket)}/{QuoteKey(key)}";
        using var request = new HttpRequestMessage(
            HttpMethod.Delete, $"{BaseUrl}/storage/v1/object/{storagePath}");
        request.Headers.TryAddWithoutValidation("apikey", Key);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {Key}");

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var response = Http.Send(request, HttpCompletionOption.ResponseContentRead, cts.Token);
            if (response.IsSuccessStatusCode) return;
            if ((int)response.StatusCode == 404) return; // already gone — deletion is idempotent
            var message = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            throw new PipelineError($"Supabase storage delete failed: {message}");
        }
        catch (HttpRequestException exc)
        {
            throw new PipelineError($"Supabase storage delete failed: {exc.Message}", exc);
        }
        catch (TaskCanceledException exc)
        {
            throw new PipelineError("Supabase storage delete failed: timed out", exc);
        }
    }

    public string UploadStorageObject(string bucket, string key, string path)
    {
        var contentType = MimeTypes.Guess(path) ?? "application/octet-stream";
        var storagePath = $"{Uri.EscapeDataString(bucket)}/{QuoteKey(key)}";
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"{BaseUrl}/storage/v1/object/{storagePath}")
        {
            Content = new ByteArrayContent(File.ReadAllBytes(path)),
        };
        request.Headers.TryAddWithoutValidation("apikey", Key);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {Key}");
        request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        request.Headers.TryAddWithoutValidation("x-upsert", "true");

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            using var response = Http.Send(request, HttpCompletionOption.ResponseContentRead, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                var message = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                throw new PipelineError($"Supabase storage upload failed: {message}");
            }
        }
        catch (HttpRequestException exc)
        {
            throw new PipelineError($"Supabase storage upload failed: {exc.Message}", exc);
        }
        catch (TaskCanceledException exc)
        {
            throw new PipelineError("Supabase storage upload failed: timed out", exc);
        }

        return PublicStorageUrl(bucket: bucket, key: key);
    }

    public string PublicStorageUrl(string bucket, string key)
    {
        var storagePath = $"{Uri.EscapeDataString(bucket)}/{QuoteKey(key)}";
        return $"{BaseUrl}/storage/v1/object/public/{storagePath}";
    }

    /// <summary>quote(key, safe='/')</summary>
    private static string QuoteKey(string key) =>
        string.Join("/", key.Split('/').Select(Uri.EscapeDataString));

    private JsonNode? Request(
        string method,
        string table,
        Dictionary<string, string>? query = null,
        JsonObject? body = null,
        string? prefer = null)
    {
        var queryString = query is { Count: > 0 }
            ? "?" + string.Join("&", query.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"))
            : "";

        using var request = new HttpRequestMessage(
            new HttpMethod(method), $"{BaseUrl}/rest/v1/{table}{queryString}");
        if (body is not null)
            request.Content = new StringContent(JsonUtil.Dumps(body), Encoding.UTF8, "application/json");
        request.Headers.TryAddWithoutValidation("apikey", Key);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {Key}");
        if (prefer is not null) request.Headers.TryAddWithoutValidation("Prefer", prefer);

        string responseBody;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var response = Http.Send(request, HttpCompletionOption.ResponseContentRead, cts.Token);
            responseBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
                throw new PipelineError($"Supabase {method} {table} failed: {responseBody}");
        }
        catch (HttpRequestException exc)
        {
            throw new PipelineError($"Supabase {method} {table} failed: {exc.Message}", exc);
        }
        catch (TaskCanceledException exc)
        {
            throw new PipelineError($"Supabase {method} {table} failed: timed out", exc);
        }

        if (string.IsNullOrEmpty(responseBody)) return null;
        return JsonUtil.Parse(responseBody);
    }
}

/// <summary>mimetypes.guess_type, reduced to the types this pipeline uploads.</summary>
public static class MimeTypes
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp4"] = "video/mp4",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".gif"] = "image/gif",
        [".wav"] = "audio/x-wav",
        [".mp3"] = "audio/mpeg",
        [".ts"] = "video/mp2t",
        [".json"] = "application/json",
        [".txt"] = "text/plain",
        [".html"] = "text/html",
    };

    public static string? Guess(string path) =>
        Map.TryGetValue(Path.GetExtension(path), out var contentType) ? contentType : null;
}
