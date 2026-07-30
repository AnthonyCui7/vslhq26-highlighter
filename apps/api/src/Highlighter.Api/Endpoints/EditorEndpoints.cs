using System.Security.Claims;
using System.Text.Json.Nodes;
using Highlighter.Api.Contracts;
using Highlighter.Api.Infrastructure;
using Highlighter.Api.Services;
using static Highlighter.Api.Endpoints.ProjectEndpoints;

namespace Highlighter.Api.Endpoints;

/// <summary>The timeline editor's persistence + export surface. Clip documents
/// live on the clip row; long-form drafts live on the version row they edit.
/// Exports run as in-process jobs (same registry/logs as worker jobs).</summary>
public static class EditorEndpoints
{
    public static IEndpointConventionBuilder MapEditorEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects");

        // ---- clips ------------------------------------------------------- //

        group.MapGet("/{id:guid}/clips/{clipId:guid}/editor",
            async (Guid id, Guid clipId, ClaimsPrincipal user, SupabaseDb db, CancellationToken ct) =>
            {
                if (!await CanSeeAsync(db, id, AuthHelpers.Uid(user), ct)) return NotFound(id);
                var row = await db.GetClipAsync(clipId, id, ct);
                if (row is null)
                    return Problem(404, "Clip not found", $"No clip {clipId} in project {id}");
                return Results.Ok(await ClipDocResponseAsync(db, id, row, ct));
            })
            .WithName("GetClipEditorDoc");

        group.MapPut("/{id:guid}/clips/{clipId:guid}/editor",
            async (Guid id, Guid clipId, SaveEditorRequest body, ClaimsPrincipal user,
                SupabaseDb db, CancellationToken ct) =>
            {
                if (body.Doc is null)
                    return Problem(400, "Invalid request", "doc is required");
                if (!await CanSeeAsync(db, id, AuthHelpers.Uid(user), ct)) return NotFound(id);
                var row = await db.GetClipAsync(clipId, id, ct);
                if (row is null)
                    return Problem(404, "Clip not found", $"No clip {clipId} in project {id}");
                if (EditorDocs.Validate(body.Doc, ClipDuration(row)) is { } invalid)
                    return Problem(400, "Invalid document", invalid);

                var saved = await new EditorStore(db).SaveClipDocAsync(id, clipId, body.Doc, ct);
                if (saved is null)
                    return Problem(404, "Clip not found", $"No clip {clipId} in project {id}");
                return Results.Ok(await ClipDocResponseAsync(db, id, saved, ct));
            })
            .WithName("SaveClipEditorDoc");

        group.MapPost("/{id:guid}/clips/{clipId:guid}/editor/export",
            async (Guid id, Guid clipId, ExportEditorRequest? body, ClaimsPrincipal user,
                SupabaseDb db, EditorExportService exports, CancellationToken ct) =>
            {
                if (!await CanSeeAsync(db, id, AuthHelpers.Uid(user), ct)) return NotFound(id);
                var row = await db.GetClipAsync(clipId, id, ct);
                if (row is null)
                    return Problem(404, "Clip not found", $"No clip {clipId} in project {id}");

                EditorDoc? doc = body?.Doc;
                if (doc is not null)
                {
                    if (EditorDocs.Validate(doc, ClipDuration(row)) is { } invalid)
                        return Problem(400, "Invalid document", invalid);
                    row = await new EditorStore(db).SaveClipDocAsync(id, clipId, doc, ct) ?? row;
                }
                doc ??= EditorStore.ClipDoc(row);
                if (doc is null)
                    return Problem(409, "Nothing to export",
                        "No saved editor document — save (or pass) one first");

                var clip = ProjectShaper.Clip(row);
                if (clip.VerticalUrl is null && clip.VideoUrl is null && clip.CaptionedUrl is null)
                    return Problem(409, "Clip not exportable", "The clip has no rendered media yet");

                var job = exports.StartClipExport(id, clip, doc);
                return Results.Accepted($"/api/jobs/{job.Id}", job.ToDto());
            })
            .WithName("ExportClipEdit");

        // ---- long-form ---------------------------------------------------- //

        group.MapGet("/{id:guid}/longform/editor",
            async (Guid id, int? version, ClaimsPrincipal user, SupabaseDb db, CancellationToken ct) =>
            {
                if (!await CanSeeAsync(db, id, AuthHelpers.Uid(user), ct)) return NotFound(id);
                var row = await db.GetLongformEditAsync(id, version, ct);
                if (row is null)
                    return Problem(404, "No long-form edit",
                        "This project has no long-form cut to edit");
                return Results.Ok(LongformDocResponse(row));
            })
            .WithName("GetLongformEditorDoc");

        group.MapPut("/{id:guid}/longform/editor",
            async (Guid id, int? version, SaveEditorRequest body, ClaimsPrincipal user,
                SupabaseDb db, CancellationToken ct) =>
            {
                if (body.Doc is null)
                    return Problem(400, "Invalid request", "doc is required");
                if (!await CanSeeAsync(db, id, AuthHelpers.Uid(user), ct)) return NotFound(id);
                var row = await db.GetLongformEditAsync(id, version, ct);
                if (row is null)
                    return Problem(404, "No long-form edit",
                        "This project has no long-form cut to edit");
                if (EditorDocs.Validate(body.Doc, LongformDuration(row)) is { } invalid)
                    return Problem(400, "Invalid document", invalid);

                var saved = await new EditorStore(db).SaveLongformDraftAsync(row, body.Doc, ct);
                return Results.Ok(LongformDocResponse(saved ?? row));
            })
            .WithName("SaveLongformEditorDoc");

        group.MapPost("/{id:guid}/longform/editor/export",
            async (Guid id, int? version, ExportEditorRequest? body, ClaimsPrincipal user,
                SupabaseDb db, EditorExportService exports, CancellationToken ct) =>
            {
                if (!await CanSeeAsync(db, id, AuthHelpers.Uid(user), ct)) return NotFound(id);
                var row = await db.GetLongformEditAsync(id, version, ct);
                if (row is null)
                    return Problem(404, "No long-form edit",
                        "This project has no long-form cut to edit");
                if (row["video_url"]?.GetValue<string>() is null)
                    return Problem(409, "Not exportable", "This version has no rendered video");

                EditorDoc? doc = body?.Doc;
                if (doc is not null)
                {
                    if (EditorDocs.Validate(doc, LongformDuration(row)) is { } invalid)
                        return Problem(400, "Invalid document", invalid);
                    row = await new EditorStore(db).SaveLongformDraftAsync(row, doc, ct) ?? row;
                }
                doc ??= EditorStore.LongformDraft(row);
                if (doc is null)
                    return Problem(409, "Nothing to export",
                        "No saved editor document — save (or pass) one first");

                var job = exports.StartLongformExport(id, row, doc);
                return Results.Accepted($"/api/jobs/{job.Id}", job.ToDto());
            })
            .WithName("ExportLongformEdit");

        return group;
    }

    private static double ClipDuration(JsonObject clipRow)
    {
        var start = clipRow["start_seconds"]?.GetValue<double>() ?? 0;
        var end = clipRow["end_seconds"]?.GetValue<double>() ?? 0;
        return Math.Max(0, end - start);
    }

    private static double LongformDuration(JsonObject editRow) =>
        editRow["duration_seconds"]?.GetValue<double>() ?? 0;

    private static async Task<EditorDocResponse> ClipDocResponseAsync(
        SupabaseDb db, Guid projectId, JsonObject row, CancellationToken ct)
    {
        var clip = ProjectShaper.Clip(row);
        var doc = EditorStore.ClipDoc(row);
        if (doc is null)
        {
            // Fresh document: whole clip on the timeline, captions seeded from
            // the transcript words inside the clip's source window.
            var chunks = await db.ListTranscriptChunksAsync(projectId, includeWords: true, ct);
            var captions = EditorDocs.SeedCaptions(
                chunks.OfType<JsonObject>(), clip.StartSeconds, clip.EndSeconds);
            doc = EditorDocs.Default(clip.DurationSeconds, captions);
        }
        return new EditorDocResponse(
            doc,
            Target: "clip",
            ClipId: clip.Id,
            LongformVersion: null,
            SourceUrl: clip.VerticalUrl ?? clip.VideoUrl ?? clip.CaptionedUrl,
            PosterUrl: clip.ThumbnailUrl,
            SourceDuration: clip.DurationSeconds,
            SavedAt: EditorStore.ClipSavedAt(row),
            Export: EditorStore.ClipExport(row));
    }

    private static EditorDocResponse LongformDocResponse(JsonObject row)
    {
        var edit = ProjectShaper.Longform(row);
        var doc = EditorStore.LongformDraft(row)
            ?? EditorDocs.Default(edit.DurationSeconds ?? 0, []);
        return new EditorDocResponse(
            doc,
            Target: "longform",
            ClipId: null,
            LongformVersion: edit.Version,
            SourceUrl: edit.VideoUrl,
            PosterUrl: edit.ThumbnailUrl,
            SourceDuration: edit.DurationSeconds ?? 0,
            SavedAt: EditorStore.LongformSavedAt(row),
            Export: null);
    }
}
