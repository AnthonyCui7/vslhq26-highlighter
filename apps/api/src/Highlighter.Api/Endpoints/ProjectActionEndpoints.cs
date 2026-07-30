using System.Text.Json.Nodes;
using Highlighter.Api.Contracts;
using Highlighter.Api.Services;
using static Highlighter.Api.Endpoints.ProjectEndpoints;

namespace Highlighter.Api.Endpoints;

/// <summary>Verbs on a project: cancel (guarded status write), revise, publish,
/// reclip (worker spawns). JobConflictException and WorkerUnavailableException
/// propagate to the global handler as 409/502 ProblemDetails.</summary>
public static class ProjectActionEndpoints
{
    private static readonly string[] AllowedPlatforms = ["tiktok", "instagram", "youtube", "x"];

    public static void MapProjectActionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects");

        group.MapPost("/{id:guid}/cancel",
            async (Guid id, bool? force, SupabaseDb db, PipelineJobService jobs, CancellationToken ct) =>
            {
                // The cancel contract: write 'stopping' and let the worker converge to
                // 'cancelled' itself (it polls the row every ~10s). Never write
                // 'cancelled' while a worker may be alive.
                var job = jobs.ActiveForProject(id);
                var patched = await db.PatchProjectGuardedAsync(id, ["created", "ingesting"],
                    new JsonObject { ["status"] = "stopping" }, ct);
                if (patched is null)
                {
                    var status = await db.GetProjectStatusAsync(id, ct);
                    if (status is null) return NotFound(id);
                    if (status is not "stopping")
                        return Problem(409, "Cancellation not applicable",
                            $"Project is already '{status}'");
                    // 'stopping' already — idempotent; force may still apply below.
                }

                var forceKilled = false;
                if (force == true)
                {
                    if (job is null)
                        return Problem(409, "No tracked worker process",
                            "Cooperative cancel is requested (row is 'stopping'); a live worker "
                            + "converges to 'cancelled' within ~10 seconds. Force-kill needs a "
                            + "process tracked by this API instance.");
                    forceKilled = await jobs.ForceKillAsync(job);
                }

                var finalStatus = await db.GetProjectStatusAsync(id, ct) ?? "stopping";
                var result = new CancelResultDto(id, finalStatus, job?.Id, forceKilled);
                return patched is not null
                    ? Results.Accepted($"/api/projects/{id}", result)
                    : Results.Ok(result);
            })
            .WithName("CancelProject");

        group.MapPost("/{id:guid}/revise",
            async (Guid id, ReviseRequestDto body, SupabaseDb db, PipelineJobService jobs,
                RepoLayout layout, CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(body.Request))
                    return Problem(400, "Invalid request", "request text is required");
                if (await db.GetProjectAsync(id, "id", ct) is null) return NotFound(id);
                if (!layout.HasLocalMirror(id)) return NoMirror(id, "revise");
                if (!await db.HasRenderedLongformAsync(id, ct))
                    return Problem(409, "Nothing to revise",
                        "No rendered long-form edit exists for this project");

                var job = jobs.Start("revise", id, WorkerArgs.Revise(id, body.Request.Trim()));
                return Results.Accepted($"/api/jobs/{job.Id}", job.ToDto());
            })
            .WithName("ReviseProject");

        group.MapPost("/{id:guid}/publish",
            async (Guid id, PublishRequestDto body, SupabaseDb db, PipelineJobService jobs,
                RepoLayout layout, CancellationToken ct) =>
            {
                if (body.Target is not ("clip" or "longform"))
                    return Problem(400, "Invalid request", "target must be 'clip' or 'longform'");
                var platforms = (body.Platforms ?? [])
                    .Select(platform => platform.Trim().ToLowerInvariant())
                    .Where(platform => platform.Length > 0)
                    .Distinct()
                    .ToList();
                if (platforms.Count == 0)
                    return Problem(400, "Invalid request", "platforms is required");
                if (platforms.Except(AllowedPlatforms).FirstOrDefault() is { } unknown)
                    return Problem(400, "Invalid request",
                        $"unknown platform '{unknown}' (allowed: {string.Join(", ", AllowedPlatforms)})");
                if (platforms is ["x"])
                    return Problem(400, "Invalid request",
                        "'x' is a promo layer and requires a companion platform");

                if (await db.GetProjectAsync(id, "id", ct) is null) return NotFound(id);
                if (!layout.HasLocalMirror(id)) return NoMirror(id, "publish");

                string target;
                if (body.Target == "longform")
                {
                    target = "longform";
                }
                else
                {
                    if (body.ClipId is not { } clipId)
                        return Problem(400, "Invalid request", "clipId is required when target is 'clip'");
                    var clip = await db.GetClipAsync(clipId, id, ct);
                    if (clip is null)
                        return Problem(404, "Clip not found",
                            $"No clip {clipId} in project {id}");
                    var fileName = (((clip["metadata"] as JsonObject)?["render"]) as JsonObject)
                        ?["filename"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(fileName))
                        return Problem(409, "Clip not publishable",
                            "The clip has no rendered file on record");
                    target = fileName;
                }

                var job = jobs.Start("publish", id, WorkerArgs.Publish(
                    id, target, platforms, body.Title, body.Version, body.Thumbnail,
                    body.Plain, body.DryRun));
                return Results.Accepted($"/api/jobs/{job.Id}", job.ToDto());
            })
            .WithName("PublishProject");

        group.MapPost("/{id:guid}/reclip",
            async (Guid id, ReclipRequestDto body, SupabaseDb db, PipelineJobService jobs,
                CancellationToken ct) =>
            {
                if (body.StartSeconds < 0 || body.EndSeconds <= body.StartSeconds)
                    return Problem(400, "Invalid request",
                        "startSeconds must be >= 0 and endSeconds greater than startSeconds");
                var project = await db.GetProjectAsync(id, "id,metadata", ct);
                if (project is null) return NotFound(id);
                if ((project["metadata"] as JsonObject)?["source_archive"] is null)
                    return Problem(409, "No source archive",
                        "reclip needs an archived livestream source (metadata.source_archive); "
                        + "VOD projects and --no-archive runs cannot be re-clipped");

                var job = jobs.Start("reclip", id, WorkerArgs.Reclip(
                    id, body.StartSeconds, body.EndSeconds, body.Title, body.Description));
                return Results.Accepted($"/api/jobs/{job.Id}", job.ToDto());
            })
            .WithName("ReclipProject");
    }

    private static IResult NoMirror(Guid id, string verb) =>
        Problem(409, "No local mirror",
            $"{verb} reads the local run mirror (outputs/projects/{id}/project.json), which is "
            + "not present on this machine — it exists wherever ingest ran");
}
