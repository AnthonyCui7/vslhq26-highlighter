using System.Text.Json.Nodes;

namespace Highlighter.Pipeline;

/// <summary>Port of highlighter_pipeline/publish.py.
///
/// Publish finished clips and long-form edits to social platforms.
///
/// Posting goes through upload-post.com (one API, per-platform OAuth handled on
/// their side): create a profile there, link the social accounts, and set
/// UPLOAD_POST_API_KEY + UPLOAD_POST_USER in the environment.
///
/// Targets: "longform" (latest version, or --version N) or a clip mp4 filename
/// from the project's clips directory. Short clips post their captioned vertical
/// when one exists (--plain for the clean vertical); YouTube decides Short vs
/// regular video from the file itself. X is a promo layer, not a video upload:
/// it requires another platform in --platforms, and once that post is live a
/// short model-written X post goes out with the link. --dry-run resolves
/// everything and prints what would be posted without calling the API.</summary>
public static class Publish
{
    public const string UPLOAD_POST_BASE_URL = "https://api.upload-post.com/api";
    public static readonly string[] VIDEO_PLATFORMS = { "tiktok", "instagram", "youtube" };
    public static readonly string[] KNOWN_PLATFORMS = { "tiktok", "instagram", "youtube", "x" };

    private static readonly HttpClient Http = new()
    {
        Timeout = System.Threading.Timeout.InfiniteTimeSpan,
    };

    private const string X_POST_SCHEMA_JSON =
        """
        {
          "type": "object",
          "properties": {
            "post": {
              "type": "string",
              "description": "The complete X post text, link included."
            }
          },
          "required": ["post"],
          "additionalProperties": false
        }
        """;

    public const string X_POST_SYSTEM_PROMPT =
        """
        You write the X (Twitter) post announcing a creator's new upload. You get the
        video's title, where it was published, the link, and research about the
        creator's voice and audience. Write ONE short post — a hook in the creator's
        own register, no hashtag spam, at most a couple of sentences — and include the
        link verbatim. Return ONLY one JSON object matching the schema.
        """;

    public static void Main(string[] args)
    {
        Config.LoadEnv();
        var parsed = ParseArgs(args);

        var outputRoot = parsed.OutputRoot
            ?? Config.Env("OUTPUT_ROOT", Defaults.DEFAULT_OUTPUT_ROOT);
        var projectDir = Path.Combine(outputRoot, "projects", parsed.ProjectId);
        if (!File.Exists(Path.Combine(projectDir, "project.json")))
            throw new PipelineError($"No local project record at {projectDir}");
        var db = parsed.ProjectId.StartsWith("local-") ? null : new SupabaseClient();
        var records = new ProjectRecords(projectDir);

        var platforms = ParsePlatforms(parsed.Platforms);
        var videoPlatforms = platforms.Where(platform => platform != "x").ToList();

        string targetKind;
        string? mediaPath;
        string mediaLabel;
        string? title;
        string? description;
        JsonObject targetMeta;
        if (parsed.Target == "longform")
        {
            targetKind = "longform";
            var row = PickLongformRow(projectDir, parsed.Version);
            var render = row["metadata"]?["render"] as JsonObject ?? new JsonObject();
            mediaPath = ResolveMediaPath(
                JsonUtil.StrOrNull(render["local_path"]), projectDir, "longform");
            mediaLabel = "long-form edit";
            title = parsed.Title ?? LongformTitle(projectDir);
            description = null;
            targetMeta = new JsonObject { ["longform_version"] = JsonUtil.C(row["version"]) };
        }
        else
        {
            targetKind = "clip";
            var row = PickClipRow(projectDir, parsed.Target);
            var render = row["metadata"]?["render"] as JsonObject ?? new JsonObject();
            var (rawMedia, label) = PickClipMedia(render, parsed.Plain);
            mediaLabel = label;
            mediaPath = ResolveMediaPath(rawMedia, projectDir, "clips");
            title = parsed.Title ?? JsonUtil.StrOrNull(row["title"]);
            description = JsonUtil.StrOrNull(row["description"]);
            targetMeta = new JsonObject { ["filename"] = parsed.Target };
        }
        if (string.IsNullOrEmpty(title))
            throw new PipelineError("No title on record for this target; pass --title");
        if (mediaPath is null)
            throw new PipelineError($"Could not locate the media file for {parsed.Target}");

        string? thumbnailUrl = null;
        JsonObject? thumbnailUpload = null;
        if (!string.IsNullOrEmpty(parsed.Thumbnail))
        {
            if (targetKind != "longform" || !platforms.Contains("youtube"))
                throw new PipelineError(
                    "--thumbnail applies to longform targets published to youtube");
            (thumbnailUrl, thumbnailUpload) = ResolveThumbnail(
                parsed.Thumbnail, projectDir, parsed.ProjectId, db, parsed.DryRun);
        }

        if (parsed.DryRun)
        {
            PrintDryRun(mediaPath, mediaLabel, title, description, platforms,
                videoPlatforms, thumbnailUrl);
            return;
        }

        var apiKey = Config.RequiredEnv("UPLOAD_POST_API_KEY");
        var user = Config.RequiredEnv("UPLOAD_POST_USER");

        var fields = new List<(string Name, string Value)> { ("title", title), ("user", user) };
        fields.AddRange(videoPlatforms.Select(platform => ("platform[]", platform)));
        if (!string.IsNullOrEmpty(description) && videoPlatforms.Contains("youtube"))
            fields.Add(("description", description));
        if (!string.IsNullOrEmpty(thumbnailUrl))
            fields.Add(("thumbnail_url", thumbnailUrl));

        Console.WriteLine(
            $"Publishing {mediaLabel} ({Path.GetFileName(mediaPath)}) to "
            + string.Join(", ", videoPlatforms));
        var response = UploadPostRequest("upload", apiKey, fields, mediaPath);
        var results = response["results"] as JsonObject ?? new JsonObject();

        var published = new Dictionary<string, string?>();
        foreach (var platform in videoPlatforms)
        {
            var entry = results[platform] as JsonObject ?? new JsonObject();
            if (JsonUtil.Truthy(entry["success"]))
            {
                var url = ResultUrl(entry);
                published[platform] = url;
                Console.WriteLine($"{platform}: published{(url is null ? "" : $" — {url}")}");
                var metadata = JsonUtil.CloneObj(targetMeta);
                if (thumbnailUpload is not null)
                    foreach (var pair in thumbnailUpload)
                        metadata[pair.Key] = JsonUtil.C(pair.Value);
                RecordPublication(records, db, parsed.ProjectId, targetKind, platform,
                    url, title, metadata);
            }
            else
            {
                var error = JsonUtil.StrOrNull(entry["error"])
                    ?? (entry.Count > 0 ? JsonUtil.Dumps(entry) : "no result returned");
                Console.WriteLine($"{platform}: failed — {error}");
            }
        }

        if (platforms.Contains("x"))
        {
            var link = published.Values.FirstOrDefault(url => url is not null);
            if (link is null)
            {
                Console.WriteLine("x: skipped — no published post to link to");
            }
            else
            {
                PublishXPost(apiKey, user, title, link, projectDir, parsed.ProjectId,
                    targetKind, targetMeta, records, db,
                    published.Where(pair => pair.Value is not null)
                        .Select(pair => pair.Key).ToList());
            }
        }

        if (published.Count == 0 && videoPlatforms.Count > 0)
            Environment.Exit(1);
    }

    private static void PublishXPost(
        string apiKey,
        string user,
        string title,
        string link,
        string projectDir,
        string projectId,
        string targetKind,
        JsonObject targetMeta,
        ProjectRecords records,
        SupabaseClient? db,
        List<string> publishedPlatforms)
    {
        var postText = ComposeXPost(title, link, ReadResearch(projectDir), publishedPlatforms);
        var response = UploadPostRequest(
            "upload_text", apiKey,
            new List<(string, string)> { ("title", postText), ("user", user), ("platform[]", "x") },
            filePath: null);
        var entry = (response["results"] as JsonObject)?["x"] as JsonObject ?? new JsonObject();
        if (!JsonUtil.Truthy(entry["success"]))
        {
            var error = JsonUtil.StrOrNull(entry["error"])
                ?? (entry.Count > 0 ? JsonUtil.Dumps(entry) : "no result returned");
            Console.WriteLine($"x: failed — {error}");
            return;
        }
        var url = ResultUrl(entry);
        Console.WriteLine($"x: published{(url is null ? "" : $" — {url}")}");
        var metadata = JsonUtil.CloneObj(targetMeta);
        metadata["post_text"] = postText;
        RecordPublication(records, db, projectId, targetKind, "x", url, title, metadata);
    }

    private static void RecordPublication(
        ProjectRecords records,
        SupabaseClient? db,
        string projectId,
        string target,
        string platform,
        string? url,
        string title,
        JsonObject metadata)
    {
        records.AppendPublication(new JsonObject
        {
            ["project_id"] = projectId,
            ["target"] = target,
            ["platform"] = platform,
            ["url"] = url,
            ["title"] = title,
            ["metadata"] = JsonUtil.CloneObj(metadata),
        });
        db?.InsertPublication(projectId, target, platform, url, title, metadata);
    }

    public static List<string> ParsePlatforms(string raw)
    {
        var platforms = new List<string>();
        foreach (var token in raw.Split(','))
        {
            var platform = token.Trim().ToLowerInvariant();
            if (platform.Length == 0) continue;
            if (!KNOWN_PLATFORMS.Contains(platform))
                throw new PipelineError(
                    $"Unknown platform '{platform}'; choose from {string.Join(", ", KNOWN_PLATFORMS)}");
            if (!platforms.Contains(platform)) platforms.Add(platform);
        }
        if (platforms.Count == 0) throw new PipelineError("No platforms given");
        if (platforms.Contains("x") && platforms.Count == 1)
            throw new PipelineError(
                "x is a promo post linking to a published video; pick at least one of "
                + $"{string.Join(", ", VIDEO_PLATFORMS)} alongside it");
        return platforms;
    }

    /// <summary>The file a short clip publishes: the captioned vertical when one
    /// exists, unless --plain asked for the clean vertical.</summary>
    public static (string? Path, string Label) PickClipMedia(JsonObject render, bool plain)
    {
        if (!plain && JsonUtil.Truthy(render["captioned_path"]))
            return (JsonUtil.Str(render["captioned_path"]), "captioned vertical");
        if (JsonUtil.Truthy(render["vertical_path"]))
            return (JsonUtil.Str(render["vertical_path"]), "vertical");
        return (JsonUtil.StrOrNull(render["local_path"]), "16:9 clip");
    }

    private static JsonObject PickLongformRow(string projectDir, int? version)
    {
        var rows = ReadJsonl(Path.Combine(projectDir, "longform_edits.jsonl"));
        if (rows.Count == 0)
            throw new PipelineError("No long-form edits recorded for this project");
        if (version is not null)
        {
            foreach (var row in rows)
                if (JsonUtil.TryDouble(row["version"], out var v) && (int)v == version)
                    return row;
            throw new PipelineError($"No long-form version {version} on record");
        }
        return rows.OrderByDescending(row => JsonUtil.DoubleOr(row["version"], 0)).First();
    }

    private static JsonObject PickClipRow(string projectDir, string filename)
    {
        foreach (var row in ReadJsonl(Path.Combine(projectDir, "clips.jsonl")))
        {
            var render = row["metadata"]?["render"] as JsonObject;
            if (render is not null && JsonUtil.StrOrNull(render["filename"]) == filename)
                return row;
        }
        throw new PipelineError(
            $"No clip named {filename} on record; pass the mp4 filename from clips/");
    }

    private static string? LongformTitle(string projectDir)
    {
        var rows = ReadJsonl(Path.Combine(projectDir, "longform_edits.jsonl"));
        foreach (var row in rows.OrderByDescending(row => JsonUtil.DoubleOr(row["version"], 0)))
        {
            var title = JsonUtil.StrOrNull((row["metadata"]?["render"] as JsonObject)?["title"]);
            if (!string.IsNullOrEmpty(title)) return title;
        }
        return null;
    }

    /// <summary>A generated variant number (1-3) or a path to the user's own image.
    /// Returns (public URL, cleanup metadata for an uploaded custom file).</summary>
    public static (string Url, JsonObject? Upload) ResolveThumbnail(
        string raw, string projectDir, string projectId, SupabaseClient? db, bool dryRun)
    {
        if (raw.All(char.IsDigit))
        {
            var wanted = int.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
            var rows = ReadJsonl(Path.Combine(projectDir, "longform_edits.jsonl"))
                .OrderByDescending(row => JsonUtil.DoubleOr(row["version"], 0));
            foreach (var row in rows)
            {
                var thumbnails = (row["metadata"]?["render"] as JsonObject)?["thumbnails"] as JsonObject;
                foreach (var variantNode in thumbnails?["variants"] as JsonArray ?? new JsonArray())
                {
                    if (variantNode is not JsonObject variant) continue;
                    if (JsonUtil.TryDouble(variant["index"], out var index)
                        && (int)index == wanted
                        && JsonUtil.Truthy(variant["url"]))
                        return (JsonUtil.Str(variant["url"]), null);
                }
            }
            throw new PipelineError($"No generated thumbnail #{wanted} with a hosted URL on record");
        }

        if (!File.Exists(raw)) throw new PipelineError($"Thumbnail file not found: {raw}");
        if (db is null)
            throw new PipelineError("Importing a thumbnail file needs a Supabase-backed project");
        var bucket = Config.Env("SUPABASE_CLIPS_BUCKET", "clips");
        var suffix = Path.GetExtension(raw).ToLowerInvariant();
        var key = $"projects/{projectId}/thumbnails/custom{(suffix.Length > 0 ? suffix : ".jpg")}";
        if (dryRun) return ($"(would upload {raw} to {bucket}/{key})", null);
        var url = db.UploadStorageObject(bucket, key, raw);
        return (url, new JsonObject
        {
            ["thumbnail_bucket"] = bucket,
            ["thumbnail_storage_path"] = key,
        });
    }

    public static string ComposeXPost(
        string title, string link, JsonObject? researchContext, List<string> platforms)
    {
        var promptLines = new List<string>
        {
            $"Video title: {title}",
            $"Published on: {string.Join(", ", platforms)}",
            $"Link to include verbatim: {link}",
        };
        if (researchContext is not null)
        {
            promptLines.Add("");
            promptLines.Add("Research context:");
            promptLines.Add(JsonUtil.DumpsIndented(researchContext));
        }
        try
        {
            var providers = Providers.EditorProviders(
                title: "highlighter publish",
                openrouterReasoningEffort: Defaults.DEFAULT_LLM_REASONING_EFFORT);
            var (decision, _) = Providers.RunWithFallback(
                providers, provider => RequestXPost(provider, string.Join("\n", promptLines)));
            var post = (JsonUtil.StrOrNull(decision["post"]) ?? "").Trim();
            if (post.Length > 0) return post.Contains(link) ? post : $"{post}\n{link}";
        }
        catch (Exception exc)
        {
            Console.WriteLine($"X post drafting failed; using the title and link: {exc.Message}");
        }
        return $"{title} — {link}";
    }

    private static JsonObject RequestXPost(ChatProvider provider, string prompt)
    {
        var body = new JsonObject
        {
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = X_POST_SYSTEM_PROMPT },
                new JsonObject { ["role"] = "user", ["content"] = prompt },
            },
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = "x_post",
                    ["schema"] = JsonUtil.ParseObject(X_POST_SCHEMA_JSON),
                },
            },
        };
        provider.ApplyRequestOptions(body);

        JsonObject response;
        using (var client = provider.Client(timeoutSeconds: 120.0))
        {
            response = client.ChatCompletions(body);
        }
        var content = response["choices"] is JsonArray { Count: > 0 } choices
            ? JsonUtil.StrOrNull(choices[0]?["message"]?["content"])
            : null;
        if (string.IsNullOrEmpty(content))
            throw new PipelineError($"{provider.Label} X post response was empty");
        return Llm.JsonFromText(content);
    }

    private static JsonObject? ReadResearch(string projectDir)
    {
        var path = Path.Combine(projectDir, "research.json");
        if (!File.Exists(path)) path = Path.Combine(projectDir, "research_short.json");
        try
        {
            return File.Exists(path) ? JsonUtil.ParseObject(File.ReadAllText(path)) : null;
        }
        catch
        {
            return null;
        }
    }

    private static JsonObject UploadPostRequest(
        string endpoint,
        string apiKey,
        List<(string Name, string Value)> fields,
        string? filePath)
    {
        try
        {
            using var content = new MultipartFormDataContent();
            foreach (var (name, value) in fields)
                content.Add(new StringContent(value), name);
            if (filePath is not null)
            {
                var videoPart = new ByteArrayContent(File.ReadAllBytes(filePath));
                videoPart.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue("video/mp4");
                content.Add(videoPart, "video", Path.GetFileName(filePath));
            }

            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"{UPLOAD_POST_BASE_URL}/{endpoint}")
            {
                Content = content,
            };
            request.Headers.TryAddWithoutValidation("Authorization", $"Apikey {apiKey}");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(600));
            using var response = Http.Send(request, HttpCompletionOption.ResponseContentRead, cts.Token);
            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
                throw new PipelineError($"upload-post request failed: {body}");
            return JsonUtil.ParseObject(body);
        }
        catch (HttpRequestException exc)
        {
            throw new PipelineError($"upload-post request failed: {exc.Message}", exc);
        }
        catch (TaskCanceledException exc)
        {
            throw new PipelineError("upload-post request failed: timed out", exc);
        }
    }

    public static string? ResultUrl(JsonObject entry)
    {
        var url = JsonUtil.StrOrNull(entry["url"])
            ?? JsonUtil.StrOrNull(entry["post_url"])
            ?? JsonUtil.StrOrNull(entry["video_url"]);
        return string.IsNullOrEmpty(url) ? null : url;
    }

    private static List<JsonObject> ReadJsonl(string path)
    {
        if (!File.Exists(path)) return new List<JsonObject>();
        return File.ReadAllLines(path)
            .Where(line => line.Trim().Length > 0)
            .Select(line => JsonUtil.ParseObject(line))
            .ToList();
    }

    /// <summary>Media paths were recorded relative to the original run's working
    /// directory; fall back to the project directory when that CWD moved.</summary>
    private static string? ResolveMediaPath(string? raw, string projectDir, string subdir)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        if (File.Exists(raw)) return raw;
        var fallback = Path.Combine(projectDir, subdir, Path.GetFileName(raw));
        return File.Exists(fallback) ? fallback : null;
    }

    private static void PrintDryRun(
        string mediaPath,
        string mediaLabel,
        string title,
        string? description,
        List<string> platforms,
        List<string> videoPlatforms,
        string? thumbnailUrl)
    {
        var sizeMb = new FileInfo(mediaPath).Length / (1024.0 * 1024.0);
        Console.WriteLine("Dry run — nothing was posted.");
        Console.WriteLine($"  file: {mediaPath} ({mediaLabel}, {Py.F(sizeMb, 1)} MB)");
        Console.WriteLine($"  title: {title}");
        if (!string.IsNullOrEmpty(description)) Console.WriteLine($"  description: {description}");
        Console.WriteLine($"  video platforms: {string.Join(", ", videoPlatforms)}");
        if (!string.IsNullOrEmpty(thumbnailUrl))
            Console.WriteLine($"  youtube thumbnail: {thumbnailUrl}");
        if (platforms.Contains("x"))
            Console.WriteLine(
                "  x: promo post with the first published link, drafted by the editor model");
        var user = Config.Env("UPLOAD_POST_USER");
        Console.WriteLine(
            $"  upload-post profile: {(string.IsNullOrEmpty(user) ? "UPLOAD_POST_USER not set" : user)}");
    }

    private sealed record ParsedArgs(
        string ProjectId,
        string Target,
        string Platforms,
        string? Title,
        int? Version,
        string? Thumbnail,
        bool Plain,
        bool DryRun,
        string? OutputRoot);

    private static ParsedArgs ParseArgs(string[] argv)
    {
        var positionals = new List<string>();
        string? platforms = null;
        string? title = null;
        int? version = null;
        string? thumbnail = null;
        var plain = false;
        var dryRun = false;
        string? outputRoot = null;
        for (var i = 0; i < argv.Length; i++)
        {
            var (flag, inlineValue) = Argv.SplitFlag(argv[i]);
            switch (flag)
            {
                case "--platforms":
                    platforms = inlineValue ?? Argv.NextValue(argv, ref i, flag);
                    break;
                case "--title":
                    title = inlineValue ?? Argv.NextValue(argv, ref i, flag);
                    break;
                case "--version":
                    version = Argv.Int(inlineValue ?? Argv.NextValue(argv, ref i, flag), flag);
                    break;
                case "--thumbnail":
                    thumbnail = inlineValue ?? Argv.NextValue(argv, ref i, flag);
                    break;
                case "--plain":
                    plain = true;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--output-root":
                    outputRoot = inlineValue ?? Argv.NextValue(argv, ref i, flag);
                    break;
                default:
                    Argv.Positional(flag, positionals);
                    break;
            }
        }
        if (positionals.Count != 2)
            throw new PipelineError("the following arguments are required: project_id, target");
        if (platforms is null)
            throw new PipelineError("the following arguments are required: --platforms");
        return new ParsedArgs(
            positionals[0], positionals[1], platforms, title, version, thumbnail,
            plain, dryRun, outputRoot);
    }
}
