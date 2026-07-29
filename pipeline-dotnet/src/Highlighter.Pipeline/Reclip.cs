using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Highlighter.Pipeline;

/// <summary>Port of highlighter_pipeline/reclip.py.
///
/// Cut a new clip from a project's archived source video in S3.
///
/// Looks up the project's source_archive pointer, downloads the segments covering
/// the requested window, renders an MP4, uploads it to the clips bucket, and
/// inserts a clips row — the full S3 -> Supabase round trip, long after the
/// original stream ended.</summary>
public static class Reclip
{
    public static void Main(string[] argv)
    {
        Config.LoadEnv();
        var positionals = new List<string>();
        string? title = null;
        string? description = null;
        var clipsBucket = Defaults.DEFAULT_SUPABASE_CLIPS_BUCKET;
        for (var i = 0; i < argv.Length; i++)
        {
            var (flag, inlineValue) = Argv.SplitFlag(argv[i]);
            string Next() => inlineValue ?? Argv.NextValue(argv, ref i, flag);
            switch (flag)
            {
                case "--title":
                    title = Next();
                    break;
                case "--description":
                    description = Next();
                    break;
                case "--clips-bucket":
                    clipsBucket = Next();
                    break;
                default:
                    Argv.Positional(flag, positionals);
                    break;
            }
        }
        if (positionals.Count != 3)
            throw new PipelineError(
                "the following arguments are required: project_id, start_seconds, end_seconds");
        var projectId = positionals[0];
        var startSeconds = Argv.Float(positionals[1], "start_seconds");
        var endSeconds = Argv.Float(positionals[2], "end_seconds");

        if (endSeconds <= startSeconds)
            throw new PipelineError("end must be greater than start");

        var db = new SupabaseClient();
        var project = db.GetProject(projectId);
        var archive = (project["metadata"] as JsonObject)?["source_archive"] as JsonObject;
        if (archive is null || !JsonUtil.Truthy(archive))
        {
            throw new PipelineError(
                $"Project {projectId} has no source_archive; only archived livestreams can be re-clipped.");
        }

        var segmentSeconds = JsonUtil.Int(archive["segment_seconds"]);
        var firstIndex = (int)(startSeconds / segmentSeconds);
        var lastIndex = (int)(Math.Max(startSeconds, endSeconds - 0.001) / segmentSeconds);
        var keys = SegmentKeys(archive, firstIndex: firstIndex, lastIndex: lastIndex);

        var storage = new S3Storage(
            JsonUtil.Str(archive["bucket"]),
            JsonUtil.Truthy(archive["region"])
                ? JsonUtil.Str(archive["region"])
                : Config.Env("AWS_REGION", Defaults.DEFAULT_AWS_REGION));
        var clipTitle = title ?? $"Reclip {Py.G(startSeconds)}s-{Py.G(endSeconds)}s";
        var filename = Render.ClipFilename(
            chunkIndex: firstIndex,
            startSeconds: startSeconds,
            endSeconds: endSeconds);

        var outputRoot = Config.Env("OUTPUT_ROOT", Defaults.DEFAULT_OUTPUT_ROOT);
        var projectDir = Path.Combine(outputRoot, "projects", projectId);
        var outputPath = Path.Combine(projectDir, "clips", filename);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        using (var tmpdir = new TempDir(parent: Path.GetDirectoryName(Path.GetFullPath(outputPath))))
        {
            var segmentPaths = new List<string>();
            foreach (var key in keys)
            {
                var local = Path.Combine(tmpdir.Path, Path.GetFileName(key));
                Console.WriteLine($"Downloading s3://{storage.Bucket}/{key}");
                storage.DownloadFile(key, local);
                segmentPaths.Add(local);
            }

            Console.WriteLine($"Rendering {filename} from {segmentPaths.Count} segment(s)");
            Render.RenderClipFromSegments(
                segmentPaths: segmentPaths,
                outputPath: outputPath,
                firstSegmentStartSeconds: firstIndex * (double)segmentSeconds,
                startSeconds: startSeconds,
                endSeconds: endSeconds);
        }

        var storageKey = $"projects/{projectId}/clips/{filename}";
        var videoUrl = db.UploadStorageObject(
            bucket: clipsBucket,
            key: storageKey,
            path: outputPath);
        db.InsertClip(
            projectId: projectId,
            title: clipTitle,
            description: description,
            startSeconds: startSeconds,
            endSeconds: endSeconds,
            videoUrl: videoUrl,
            status: "rendered",
            metadata: new JsonObject
            {
                ["source"] = "reclip",
                ["render"] = new JsonObject
                {
                    ["status"] = "rendered",
                    ["bucket"] = clipsBucket,
                    ["storage_path"] = storageKey,
                    ["video_url"] = videoUrl,
                    ["filename"] = filename,
                    ["segment_keys"] = JsonUtil.Arr(keys.Select(key => (JsonNode?)key)),
                },
            });
        Console.WriteLine($"Clip stored: {videoUrl}");
        Console.WriteLine($"Local copy: {outputPath}");
    }

    private static List<string> SegmentKeys(JsonObject archive, int firstIndex, int lastIndex)
    {
        var knownKeys = (archive["segment_keys"] as JsonArray ?? new JsonArray())
            .Select(node => JsonUtil.Str(node))
            .ToList();
        var byIndex = new Dictionary<int, string>();
        foreach (var key in knownKeys)
        {
            var match = Regex.Match(key, @"video_(\d+)\.ts$");
            if (match.Success)
                byIndex[int.Parse(match.Groups[1].Value)] = key;
        }

        var keys = new List<string>();
        for (var index = firstIndex; index <= lastIndex; index++)
        {
            if (byIndex.TryGetValue(index, out var key))
            {
                keys.Add(key);
            }
            else if (knownKeys.Count == 0 && JsonUtil.Truthy(archive["prefix"]))
            {
                // Older projects recorded only a prefix; segment names are deterministic.
                keys.Add($"{JsonUtil.Str(archive["prefix"]).TrimEnd('/')}/video_{index:00000}.ts");
            }
            else
            {
                throw new PipelineError(
                    $"Requested window needs archive segment {index}, which is not in this project's archive.");
            }
        }
        return keys;
    }
}
