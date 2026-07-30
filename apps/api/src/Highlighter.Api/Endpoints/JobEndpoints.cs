using System.Globalization;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using Highlighter.Api.Infrastructure;
using Highlighter.Api.Services;

namespace Highlighter.Api.Endpoints;

/// <summary>Job reads are scoped to the requesting user exactly like project
/// reads: someone else's job (its argv carries their prompts and sources, its
/// logs their transcripts) reads as nonexistent.</summary>
public static class JobEndpoints
{
    public static IEndpointConventionBuilder MapJobEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/jobs");

        group.MapGet("/", (ClaimsPrincipal user, PipelineJobService jobs, string? state,
                Guid? projectId) =>
                Results.Ok(jobs.List(state, projectId, AuthHelpers.Uid(user))))
            .WithName("ListJobs");

        group.MapGet("/{id}", (string id, ClaimsPrincipal user, PipelineJobService jobs) =>
                Visible(jobs.Get(id), user) is { } job ? Results.Ok(job.ToDto()) : NotFound(id))
            .WithName("GetJob");

        group.MapGet("/{id}/logs", (string id, ClaimsPrincipal user, PipelineJobService jobs,
                int? tail) =>
            {
                var count = Math.Clamp(tail ?? 200, 1, 4000);
                if (jobs.Get(id) is { } tracked)
                    return Visible(tracked, user) is { } job
                        ? Results.Ok(job.Tail(count))
                        : NotFound(id);
                // Registry is in-memory; after an API restart the durable file still serves.
                var fromFile = jobs.ReadLogFileTail(id, count);
                return fromFile is null ? NotFound(id) : Results.Ok(fromFile);
            })
            .WithName("GetJobLogs");

        group.MapGet("/{id}/logs/stream",
            (string id, ClaimsPrincipal user, PipelineJobService jobs, int? tail,
                CancellationToken ct) =>
            {
                var job = Visible(jobs.Get(id), user);
                if (job is null) return NotFound(id);
                return TypedResults.ServerSentEvents(
                    StreamAsync(job, Math.Clamp(tail ?? 200, 0, 4000), ct));
            })
            .WithName("StreamJobLogs");

        return group;
    }

    private static PipelineJob? Visible(PipelineJob? job, ClaimsPrincipal user) =>
        job is not null && job.VisibleTo(AuthHelpers.Uid(user)) ? job : null;

    private static async IAsyncEnumerable<SseItem<object>> StreamAsync(
        PipelineJob job, int tail, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var line in job.StreamLogsAsync(tail, ct))
            yield return new SseItem<object>(line, "log")
            {
                EventId = line.Seq.ToString(CultureInfo.InvariantCulture),
            };
        // Terminal marker with the final job state, so clients know why the stream ended.
        yield return new SseItem<object>(job.ToDto(), "end");
    }

    private static IResult NotFound(string id) =>
        Results.Problem(title: "Job not found", detail: $"No job with id {id}", statusCode: 404);
}
