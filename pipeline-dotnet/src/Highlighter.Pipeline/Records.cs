using System.Globalization;
using System.Text.Json.Nodes;

namespace Highlighter.Pipeline;

/// <summary>Port of highlighter_pipeline/records.py.
///
/// Local project records: a file mirror of what lands in Supabase.
///
/// Every run — Supabase-backed or --local-only — writes these under the project
/// directory, so `outputs/projects/&lt;id&gt;/` is always a self-contained record with
/// the same shape the database has:
///
///     project.json             the projects row (status, metadata, error)
///     transcript_chunks.jsonl  one line per transcript_chunks row (with words)
///     clips.jsonl              one line per clips row (worthy, rendered clips)
///     decisions.jsonl          full editorial log: every decision, worthy or not
///     longform_edits.jsonl     one line per stitched long-form version
///     research.json            content research context, when research ran
/// </summary>
public class ProjectRecords
{
    public string ProjectDir { get; }

    private readonly JsonObject _project;

    public ProjectRecords(string projectDir)
    {
        ProjectDir = projectDir;
        Directory.CreateDirectory(projectDir);
        // Resume an existing record (e.g. the revision loop reopening a
        // finished project) instead of starting a fresh row over it.
        var projectFile = Path.Combine(projectDir, "project.json");
        _project = File.Exists(projectFile)
            ? JsonUtil.ParseObject(File.ReadAllText(projectFile))
            : new JsonObject { ["created_at"] = Now() };
    }

    private static string Now() =>
        DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff+00:00", CultureInfo.InvariantCulture);

    /// <summary>Merge fields into project.json (null values are ignored so a status
    /// write never erases metadata written earlier).</summary>
    public void UpdateProject(params (string Key, JsonNode? Value)[] fields)
    {
        foreach (var (key, value) in fields)
            if (value is not null)
                _project[key] = JsonUtil.C(value);
        _project["updated_at"] = Now();
        WriteJson("project.json", _project);
    }

    public void AppendChunk(JsonObject row) => AppendJsonl("transcript_chunks.jsonl", row);

    public void AppendClip(JsonObject row) => AppendJsonl("clips.jsonl", row);

    public void AppendDecision(JsonObject row) => AppendJsonl("decisions.jsonl", row);

    public void AppendLongformEdit(JsonObject row) => AppendJsonl("longform_edits.jsonl", row);

    public void WriteResearch(JsonObject context) => WriteJson("research.json", context);

    private void WriteJson(string name, JsonObject payload) =>
        File.WriteAllText(Path.Combine(ProjectDir, name), JsonUtil.DumpsIndented(payload));

    private void AppendJsonl(string name, JsonObject row) =>
        File.AppendAllText(Path.Combine(ProjectDir, name), JsonUtil.Dumps(row) + "\n");
}
