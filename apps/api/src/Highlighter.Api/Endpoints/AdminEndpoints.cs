using Highlighter.Api.Contracts;
using Highlighter.Api.Services;

namespace Highlighter.Api.Endpoints;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        // Manual outbox drain (the scheduler also runs one periodically and after
        // deletes). Single-flight is enforced by the job service (409 when racing).
        app.MapPost("/api/admin/cleanup", (CleanupRequestDto? body, PipelineJobService jobs) =>
            {
                var limit = Math.Clamp(body?.Limit ?? 100, 1, 1000);
                var job = jobs.Start("cleanup", null, WorkerArgs.Cleanup(limit));
                return Results.Accepted($"/api/jobs/{job.Id}", job.ToDto());
            })
            .WithName("RunCleanup");
    }
}
