using System.Net;
using System.Text.Json.Nodes;
using Highlighter.Api.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Highlighter.Api.Tests;

public class SupabaseDbTests
{
    private static readonly Guid Id = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public async Task ListProjects_UsesEmbeddedCountsAndOrdering()
    {
        var (db, handler) = TestDb.Create();

        await db.ListProjectsAsync(100);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(
            "https://stub.supabase.local/rest/v1/projects"
            + "?select=*,clips(count),transcript_chunks(count),longform_edits(count)"
            + ",thumbs:longform_edits(thumbnail_url,version),clip_thumbs:clips(metadata,score)"
            + "&thumbs.order=version.desc&thumbs.limit=1"
            + "&clip_thumbs.order=score.desc.nullslast&clip_thumbs.limit=8"
            + "&order=created_at.desc&limit=100",
            request.Url);
    }

    [Fact]
    public async Task EveryRequest_CarriesServiceRoleHeaders()
    {
        var (db, handler) = TestDb.Create(key: "the-key");

        await db.ListProjectsAsync(1);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("the-key", request.Headers["apikey"]);
        Assert.Equal("Bearer the-key", request.Headers["Authorization"]);
    }

    [Fact]
    public async Task GetProjectDetail_EmbedsChildrenWithOrdering()
    {
        var (db, handler) = TestDb.Create();

        await db.GetProjectDetailAsync(Id);

        var url = Assert.Single(handler.Requests).Url;
        Assert.Contains($"id=eq.{Id}", url);
        Assert.Contains("clips(*),longform_edits(*),publications(*),transcript_chunks(count)", url);
        Assert.Contains("clips.order=start_seconds.asc", url);
        Assert.Contains("longform_edits.order=version.desc", url);
        Assert.Contains("publications.order=created_at.desc", url);
    }

    [Fact]
    public async Task PatchProjectGuarded_FiltersOnStatusAndReturnsNullOnGuardMiss()
    {
        var (db, handler) = TestDb.Create();
        handler.Enqueue(HttpStatusCode.OK, "[]");

        var patch = new JsonObject { ["status"] = "stopping" };
        var row = await db.PatchProjectGuardedAsync(Id, ["created", "ingesting"], patch);

        Assert.Null(row);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Patch, request.Method);
        Assert.Contains($"id=eq.{Id}&status=in.(created,ingesting)", request.Url);
        Assert.Equal("return=representation", request.Headers["Prefer"]);
        Assert.Equal("""{"status":"stopping"}""", request.Body);
    }

    [Fact]
    public async Task PatchProjectGuarded_ReturnsUpdatedRow()
    {
        var (db, handler) = TestDb.Create();
        handler.Enqueue(HttpStatusCode.OK, $$"""[{"id":"{{Id}}","status":"stopping"}]""");

        var row = await db.PatchProjectGuardedAsync(Id, ["ingesting"], new JsonObject { ["status"] = "stopping" });

        Assert.Equal("stopping", row!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task InsertProject_ReturnsRepresentation()
    {
        var (db, handler) = TestDb.Create();
        handler.Enqueue(HttpStatusCode.Created, $$"""[{"id":"{{Id}}","status":"created"}]""");

        var row = await db.InsertProjectAsync(new JsonObject { ["name"] = "n" });

        Assert.Equal(Id.ToString(), row["id"]!.GetValue<string>());
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("return=representation", request.Headers["Prefer"]);
        Assert.Equal("""{"name":"n"}""", request.Body);
    }

    [Fact]
    public async Task DeleteProject_TrueOnlyWhenARowCameBack()
    {
        var (db, handler) = TestDb.Create();
        handler.Enqueue(HttpStatusCode.OK, $$"""[{"id":"{{Id}}"}]""");
        handler.Enqueue(HttpStatusCode.OK, "[]");

        Assert.True(await db.DeleteProjectAsync(Id));
        Assert.False(await db.DeleteProjectAsync(Id));
        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Delete, request.Method));
    }

    [Fact]
    public async Task ListClips_EncodesTheJsonPipelineFilter()
    {
        var (db, handler) = TestDb.Create();

        await db.ListClipsAsync(Id, pipeline: "short", status: "rendered", orderBy: "score");

        var url = Assert.Single(handler.Requests).Url;
        Assert.Contains("order=score.desc.nullslast", url);
        Assert.Contains("metadata-%3E%3Epipeline=eq.short", url);
        Assert.Contains("status=eq.rendered", url);
    }

    [Fact]
    public async Task ListClips_OrderByStart()
    {
        var (db, handler) = TestDb.Create();

        await db.ListClipsAsync(Id, pipeline: null, status: null, orderBy: "start");

        var url = Assert.Single(handler.Requests).Url;
        Assert.Contains("order=start_seconds.asc", url);
        Assert.DoesNotContain("metadata", url);
    }

    [Fact]
    public async Task ListTranscriptChunks_OmitsWordsUnlessAsked()
    {
        var (db, handler) = TestDb.Create();

        await db.ListTranscriptChunksAsync(Id, includeWords: false);
        await db.ListTranscriptChunksAsync(Id, includeWords: true);

        Assert.DoesNotContain(",words", handler.Requests[0].Url);
        Assert.Contains(",words", handler.Requests[1].Url);
        Assert.Contains("order=chunk_index.asc", handler.Requests[0].Url);
    }

    [Fact]
    public async Task CountCleanupJobs_ParsesPostgrestContentRange()
    {
        var (db, handler) = TestDb.Create();
        handler.Enqueue(HttpStatusCode.OK, "[]", contentRange: "0-0/57");
        handler.Enqueue(HttpStatusCode.OK, "[]", contentRange: "*/0");

        Assert.Equal(57, await db.CountCleanupJobsAsync("pending"));
        Assert.Equal(0, await db.CountCleanupJobsAsync("failed"));
        Assert.Equal("count=exact", handler.Requests[0].Headers["Prefer"]);
        Assert.Contains("status=eq.pending", handler.Requests[0].Url);
    }

    [Fact]
    public async Task NonSuccess_ThrowsPostgrestExceptionWithBodyPrefix()
    {
        var (db, handler) = TestDb.Create();
        handler.Enqueue(HttpStatusCode.Unauthorized, """{"message":"bad jwt"}""");

        var exception = await Assert.ThrowsAsync<PostgrestException>(() => db.ListProjectsAsync(1));

        Assert.Contains("bad jwt", exception.Message);
        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
    }

    [Fact]
    public async Task Unconfigured_ThrowsWithSetupHint()
    {
        var db = new Services.SupabaseDb(
            new HttpClient(new RecordingHandler()),
            NullLogger<Services.SupabaseDb>.Instance, null, null);

        Assert.False(db.IsConfigured);
        var exception = await Assert.ThrowsAsync<PostgrestException>(() => db.ListProjectsAsync(1));
        Assert.Contains("SUPABASE_URL", exception.Message);
    }
}
