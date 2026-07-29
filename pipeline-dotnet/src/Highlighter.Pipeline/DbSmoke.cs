using System.Text.Json.Nodes;

namespace Highlighter.Pipeline;

/// <summary>Port of highlighter_pipeline/db_smoke.py.</summary>
public static class DbSmoke
{
    public static void Main()
    {
        Config.LoadEnv();
        var db = new SupabaseClient();
        var projectId = db.CreateProject(
            name: "DB smoke test",
            sourceType: "upload",
            sourceUrl: "smoke://local",
            metadata: new JsonObject { ["kind"] = "db-smoke" });
        db.InsertTranscriptChunk(
            projectId: projectId,
            chunkIndex: 0,
            startSeconds: 0,
            endSeconds: 90,
            transcript: "Smoke test transcript for a 90 second chunk.",
            words: new JsonArray
            {
                new JsonObject
                {
                    ["word"] = "smoke",
                    ["start"] = 0,
                    ["end"] = 0.5,
                    ["absolute_start"] = 0,
                    ["absolute_end"] = 0.5,
                },
            },
            metadata: new JsonObject { ["smoke"] = true });
        db.InsertClip(
            projectId: projectId,
            title: "Smoke test highlight",
            description: "Fake clip inserted by db_smoke.",
            startSeconds: 10,
            endSeconds: 25,
            score: 42,
            metadata: new JsonObject { ["smoke"] = true });
        db.SetProjectStatus(
            projectId,
            status: "ready",
            metadata: new JsonObject { ["kind"] = "db-smoke", ["chunks"] = 1 });
        Console.WriteLine(
            $"Inserted smoke project {projectId} with one transcript_chunks row and one clips row.");
    }
}
