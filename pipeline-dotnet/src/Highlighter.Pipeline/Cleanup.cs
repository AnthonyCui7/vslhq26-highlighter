using System.Text.Json.Nodes;

namespace Highlighter.Pipeline;

/// <summary>Port of highlighter_pipeline/cleanup.py.
///
/// Drain the media_cleanup_jobs outbox: delete the S3/Supabase Storage objects
/// that deleted DB rows pointed at. Safe to run repeatedly; deletions are idempotent.</summary>
public static class Cleanup
{
    public static void Main(string[] argv)
    {
        Config.LoadEnv();
        var limit = 100;
        for (var i = 0; i < argv.Length; i++)
        {
            var (flag, inlineValue) = Argv.SplitFlag(argv[i]);
            if (flag == "--limit")
                limit = Argv.Int(inlineValue ?? Argv.NextValue(argv, ref i, flag), "--limit");
            else
                throw new PipelineError($"unrecognized arguments: {argv[i]}");
        }

        var db = new SupabaseClient();
        var jobs = db.PendingCleanupJobs(limit: limit);
        if (jobs.Count == 0)
        {
            Console.WriteLine("No pending media cleanup jobs.");
            return;
        }

        var s3Clients = new Dictionary<string, S3Storage>();
        var succeeded = 0;
        var failed = 0;

        foreach (var job in jobs)
        {
            db.BumpCleanupAttempts(
                JsonUtil.Str(job["id"]), JsonUtil.Int(job["attempts"]) + 1);
            try
            {
                DeleteMedia(job, db: db, s3Clients: s3Clients);
                succeeded++;
                db.FinishCleanupJob(JsonUtil.Str(job["id"]), status: "succeeded");
                Console.WriteLine($"deleted {Describe(job)}");
            }
            catch (Exception exc)
            {
                failed++;
                db.FinishCleanupJob(
                    JsonUtil.Str(job["id"]), status: "failed", error: exc.Message);
                Console.WriteLine($"FAILED {Describe(job)}: {exc.Message}");
            }
        }

        Console.WriteLine($"Cleanup finished: {succeeded} succeeded, {failed} failed.");
        if (failed > 0) Environment.Exit(1);
    }

    private static void DeleteMedia(
        JsonObject job, SupabaseClient db, Dictionary<string, S3Storage> s3Clients)
    {
        var store = JsonUtil.Str(job["store"]);
        if (store == "s3")
        {
            var bucket = JsonUtil.Str(job["bucket"]);
            if (!s3Clients.TryGetValue(bucket, out var client))
                s3Clients[bucket] = client = new S3Storage(bucket, S3Region());
            if (JsonUtil.Truthy(job["is_prefix"]))
                client.DeletePrefix(JsonUtil.Str(job["object_key"]));
            else
                client.DeleteObject(JsonUtil.Str(job["object_key"]));
        }
        else if (store == "supabase_storage")
        {
            db.DeleteStorageObject(
                bucket: JsonUtil.Str(job["bucket"]), key: JsonUtil.Str(job["object_key"]));
        }
        else
        {
            throw new PipelineError($"Unknown media store: {store}");
        }
    }

    private static string S3Region() =>
        Config.Env("AWS_REGION", Defaults.DEFAULT_AWS_REGION);

    private static string Describe(JsonObject job)
    {
        var suffix = JsonUtil.Truthy(job["is_prefix"]) ? "/*" : "";
        return $"{JsonUtil.Str(job["store"])}://{JsonUtil.Str(job["bucket"])}/"
            + $"{JsonUtil.Str(job["object_key"])}{suffix} "
            + $"(from {JsonUtil.Str(job["origin_table"])} {JsonUtil.Str(job["origin_id"])})";
    }
}
