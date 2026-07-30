using System.Globalization;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using Highlighter.Api.Services;

namespace Highlighter.Api.Endpoints;

public static class JobEndpoints
{
    public static IEndpointConventionBuilder MapJobEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/jobs");

        group.MapGet("/", (PipelineJobService jobs, string? state, Guid? projectId) =>
                Results.Ok(jobs.List(state, projectId)))
            .WithName("ListJobs");

        group.MapGet("/{id}", (string id, PipelineJobService jobs) =>
                jobs.Get(id) is { } job ? Results.Ok(job.ToDto()) : NotFound(id))
            .WithName("GetJob");

        group.MapGet("/{id}/logs", (string id, PipelineJobService jobs, int? tail) =>
            {
                var count = Math.Clamp(tail ?? 200, 1, 4000);
                if (jobs.Get(id) is { } job) return Results.Ok(job.Tail(count));
                // Registry is in-memory; after an API restart the durable file still serves.
                var fromFile = jobs.ReadLogFileTail(id, count);
                return fromFile is null ? NotFound(id) : Results.Ok(fromFile);
            })
            .WithName("GetJobLogs");

        group.MapGet("/{id}/logs/stream",
            (string id, PipelineJobService jobs, int? tail, CancellationToken ct) =>
            {
                var job = jobs.Get(id);
                if (job is null) return NotFound(id);
                return TypedResults.ServerSentEvents(
                    StreamAsync(job, Math.Clamp(tail ?? 200, 0, 4000), ct));
            })
            .WithName("StreamJobLogs");

        return group;
    }

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
