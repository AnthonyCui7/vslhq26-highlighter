using System.Text.Json.Nodes;

namespace Highlighter.Api.Services;

/// <summary>Best-effort updates to the worker's local run mirror
/// (outputs/projects/{id}/*.jsonl). The DB row is the source of truth; keeping
/// the mirror in step matters only so mirror-reading worker verbs (thumbnails
/// --select, publish) see what the API changed. Callers must hold the
/// per-project single-active-job invariant — never write while a worker runs.</summary>
public static class MirrorStore
{
    /// <summary>Rewrite the longform_edits.jsonl row for a version in place.
    /// Returns false (without throwing) when the mirror or row is absent.</summary>
    public static bool UpdateLongformRow(string projectDir, int version, Action<JsonObject> mutate)
    {
        try
        {
            var path = Path.Combine(projectDir, "longform_edits.jsonl");
            if (!File.Exists(path)) return false;
            var lines = File.ReadAllLines(path);
            var changed = false;
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Trim().Length == 0) continue;
                if (JsonNode.Parse(lines[i]) is not JsonObject row) continue;
                if ((int)(row["version"]?.GetValue<double>() ?? 1) != version) continue;
                mutate(row);
                lines[i] = row.ToJsonString();
                changed = true;
            }
            if (changed) File.WriteAllLines(path, lines);
            return changed;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return false;
        }
    }
}
