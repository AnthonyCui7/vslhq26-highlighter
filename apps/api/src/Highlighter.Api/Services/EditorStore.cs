using System.Text.Json.Nodes;
using Highlighter.Api.Contracts;

namespace Highlighter.Api.Services;

/// <summary>Loads and saves editor documents in the frozen schema's jsonb room:
/// clip docs under clips.metadata.editor, long-form drafts under
/// longform_edits.metadata.editor_draft (the `editor` COLUMN belongs to the
/// pipeline's pass-2 notes). All writes are read-modify-write of the whole
/// metadata object so worker-owned keys survive.</summary>
public sealed class EditorStore(SupabaseDb db)
{
    // ---- clip documents --------------------------------------------------- //

    public static EditorDoc? ClipDoc(JsonObject clipRow) =>
        EditorDocs.FromJson(((clipRow["metadata"] as JsonObject)?["editor"] as JsonObject)?["edl"]);

    public static DateTimeOffset? ClipSavedAt(JsonObject clipRow) =>
        ParseTime(((clipRow["metadata"] as JsonObject)?["editor"] as JsonObject)?["saved_at"]);

    public static EditorExportInfoDto? ClipExport(JsonObject clipRow)
    {
        var render = ((clipRow["metadata"] as JsonObject)?["editor"] as JsonObject)
            ?["render"] as JsonObject;
        if (render is null) return null;
        return new EditorExportInfoDto(
            render["url"]?.GetValue<string>(),
            render["thumbnail_url"]?.GetValue<string>(),
            Dbl(render["duration_seconds"]),
            ParseTime(render["exported_at"]));
    }

    public async Task<JsonObject?> SaveClipDocAsync(
        Guid projectId, Guid clipId, EditorDoc doc, CancellationToken ct)
    {
        var row = await db.GetClipAsync(clipId, projectId, ct);
        if (row is null) return null;
        var metadata = row["metadata"]?.DeepClone() as JsonObject ?? new JsonObject();
        var editor = metadata["editor"] as JsonObject ?? new JsonObject();
        editor["edl"] = EditorDocs.ToJson(doc);
        editor["saved_at"] = DateTimeOffset.UtcNow.ToString("o");
        metadata["editor"] = editor;
        return await db.PatchClipAsync(clipId, projectId, new JsonObject { ["metadata"] = metadata }, ct);
    }

    public async Task SaveClipExportAsync(
        Guid projectId, Guid clipId, JsonObject renderInfo, CancellationToken ct)
    {
        var row = await db.GetClipAsync(clipId, projectId, ct)
            ?? throw new InvalidOperationException($"clip {clipId} vanished during export");
        var metadata = row["metadata"]?.DeepClone() as JsonObject ?? new JsonObject();
        var editor = metadata["editor"] as JsonObject ?? new JsonObject();
        editor["render"] = renderInfo;
        metadata["editor"] = editor;
        await db.PatchClipAsync(clipId, projectId, new JsonObject { ["metadata"] = metadata }, ct);
    }

    // ---- long-form documents ---------------------------------------------- //

    public static EditorDoc? LongformDraft(JsonObject editRow) =>
        EditorDocs.FromJson(((editRow["metadata"] as JsonObject)?["editor_draft"] as JsonObject)?["edl"]);

    public static DateTimeOffset? LongformSavedAt(JsonObject editRow) =>
        ParseTime(((editRow["metadata"] as JsonObject)?["editor_draft"] as JsonObject)?["saved_at"]);

    public async Task<JsonObject?> SaveLongformDraftAsync(JsonObject editRow, EditorDoc doc, CancellationToken ct)
    {
        var editId = Guid.Parse(editRow["id"]!.GetValue<string>());
        var metadata = editRow["metadata"]?.DeepClone() as JsonObject ?? new JsonObject();
        var draft = metadata["editor_draft"] as JsonObject ?? new JsonObject();
        draft["edl"] = EditorDocs.ToJson(doc);
        draft["saved_at"] = DateTimeOffset.UtcNow.ToString("o");
        metadata["editor_draft"] = draft;
        return await db.PatchLongformEditAsync(editId, new JsonObject { ["metadata"] = metadata }, ct);
    }

    private static DateTimeOffset? ParseTime(JsonNode? node) =>
        DateTimeOffset.TryParse(node?.GetValue<string>(), out var at) ? at : null;

    private static double? Dbl(JsonNode? node)
    {
        try
        {
            return node?.GetValue<double>();
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
