namespace Highlighter.Pipeline;

/// <summary>Port of highlighter_pipeline/render.py.</summary>
public static class Render
{
    /// <summary>A combined run passes suffix ("_short"/"_long") so the two
    /// forks never collide on a filename when they pick the same window.</summary>
    public static string ClipFilename(
        int chunkIndex, double startSeconds, double endSeconds, string suffix = "")
    {
        var startMs = (int)Math.Round(startSeconds * 1000, MidpointRounding.ToEven);
        var endMs = (int)Math.Round(endSeconds * 1000, MidpointRounding.ToEven);
        return $"clip_{chunkIndex:00000}_{startMs}_{endMs}{suffix}.mp4";
    }

    public static void RenderClipFromVideoUrl(
        string sourceUrl, string outputPath, double startSeconds, double endSeconds)
    {
        ValidateWindow(startSeconds: startSeconds, endSeconds: endSeconds);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

        using var tmpdir = new TempDir(parent: Path.GetDirectoryName(Path.GetFullPath(outputPath)));
        var template = Path.Combine(tmpdir.Path, "clip.%(ext)s");
        var command = new List<string>
        {
            "yt-dlp",
            "--quiet",
            "--no-warnings",
            "--no-playlist",
        };
        command.AddRange(Cookies.YtdlpCookieArgs());
        command.AddRange(new[]
        {
            "-f",
            "bv*+ba/b",
            "--download-sections",
            $"*{FormatTimestamp(startSeconds)}-{FormatTimestamp(endSeconds)}",
            "--force-keyframes-at-cuts",
            "--merge-output-format",
            "mp4",
            "-o",
            template,
            sourceUrl,
        });
        RunChecked(command);

        var candidates = Directory.GetFiles(tmpdir.Path, "clip.*")
            .OrderBy(path => File.GetLastWriteTimeUtc(path))
            .ToList();
        if (candidates.Count == 0)
            throw new PipelineError("yt-dlp did not produce a rendered clip");

        var rendered = candidates[^1];
        TranscodeToMp4(sourcePath: rendered, outputPath: outputPath);
    }

    /// <summary>Render a clip window that may span several consecutive archive segments.
    ///
    /// Segments were cut on keyframes, so nominal offsets can drift by up to a
    /// keyframe interval (~2s) from true stream time.</summary>
    public static void RenderClipFromSegments(
        IReadOnlyList<string> segmentPaths,
        string outputPath,
        double firstSegmentStartSeconds,
        double startSeconds,
        double endSeconds)
    {
        ValidateWindow(startSeconds: startSeconds, endSeconds: endSeconds);
        var missing = segmentPaths.Where(path => !File.Exists(path)).ToList();
        if (missing.Count > 0)
            throw new PipelineError(
                $"Missing source segments for clip render: {string.Join(", ", missing)}");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        var relativeStart = Math.Max(0, startSeconds - firstSegmentStartSeconds);
        var duration = endSeconds - startSeconds;

        using var tmpdir = new TempDir(parent: Path.GetDirectoryName(Path.GetFullPath(outputPath)));
        var concatList = Path.Combine(tmpdir.Path, "segments.txt");
        File.WriteAllText(concatList, string.Concat(
            segmentPaths.Select(path => $"file '{Path.GetFullPath(path)}'\n")));
        RunChecked(new List<string>
        {
            "ffmpeg",
            "-hide_banner",
            "-loglevel",
            "error",
            "-y",
            "-f",
            "concat",
            "-safe",
            "0",
            "-i",
            concatList,
            "-ss",
            Py.F(relativeStart, 3),
            "-t",
            Py.F(duration, 3),
            "-map",
            "0:v:0",
            "-map",
            "0:a:0?",
            "-c:v",
            "libx264",
            "-preset",
            "veryfast",
            "-crf",
            "30",
            "-vf",
            "scale='min(720,iw)':-2",
            "-c:a",
            "aac",
            "-b:a",
            "128k",
            "-movflags",
            "+faststart",
            outputPath,
        });
    }

    /// <summary>Re-encode a window of an already-rendered clip (same encode settings as
    /// renders, so trimmed segments still codec-copy concat with untrimmed ones).</summary>
    public static void TrimClip(
        string sourcePath, string outputPath, double startOffsetSeconds, double durationSeconds)
    {
        if (durationSeconds <= 0)
            throw new PipelineError("Trim duration must be greater than 0");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        RunChecked(new List<string>
        {
            "ffmpeg",
            "-hide_banner",
            "-loglevel",
            "error",
            "-y",
            "-i",
            sourcePath,
            "-ss",
            Py.F(Math.Max(0.0, startOffsetSeconds), 3),
            "-t",
            Py.F(durationSeconds, 3),
            "-c:v",
            "libx264",
            "-preset",
            "veryfast",
            "-crf",
            "30",
            "-vf",
            "scale='min(720,iw)':-2",
            "-c:a",
            "aac",
            "-b:a",
            "128k",
            "-movflags",
            "+faststart",
            outputPath,
        });
    }

    /// <summary>Extract a single JPEG frame (maxWidth wide at most) from a rendered clip.</summary>
    public static void ExtractThumbnail(string clipPath, string outputPath, double atSeconds, int maxWidth = 480)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        RunChecked(new List<string>
        {
            "ffmpeg",
            "-hide_banner",
            "-loglevel",
            "error",
            "-y",
            "-ss",
            Py.F(Math.Max(0.0, atSeconds), 3),
            "-i",
            clipPath,
            "-frames:v",
            "1",
            "-vf",
            $"scale='min({maxWidth},iw)':-2",
            "-q:v",
            "4",
            outputPath,
        });
        if (!File.Exists(outputPath))
            throw new PipelineError("ffmpeg produced no thumbnail frame");
    }

    private static void TranscodeToMp4(string sourcePath, string outputPath)
    {
        RunChecked(new List<string>
        {
            "ffmpeg",
            "-hide_banner",
            "-loglevel",
            "error",
            "-y",
            "-i",
            sourcePath,
            "-c:v",
            "libx264",
            "-preset",
            "veryfast",
            "-crf",
            "30",
            "-vf",
            "scale='min(720,iw)':-2",
            "-c:a",
            "aac",
            "-b:a",
            "128k",
            "-movflags",
            "+faststart",
            outputPath,
        });
    }

    private static void ValidateWindow(double startSeconds, double endSeconds)
    {
        if (endSeconds <= startSeconds)
            throw new PipelineError("Clip end_seconds must be greater than start_seconds");
    }

    public static string FormatTimestamp(double seconds)
    {
        var wholeSeconds = (int)seconds;
        var milliseconds = (int)Math.Round((seconds - wholeSeconds) * 1000, MidpointRounding.ToEven);
        if (milliseconds == 1000)
        {
            wholeSeconds += 1;
            milliseconds = 0;
        }

        var hours = wholeSeconds / 3600;
        var remainder = wholeSeconds % 3600;
        var minutes = remainder / 60;
        var secs = remainder % 60;
        return $"{hours:00}:{minutes:00}:{secs:00}.{milliseconds:000}";
    }

    private static void RunChecked(IReadOnlyList<string> command)
    {
        var (code, stdout, stderr) = Proc.Run(command);
        if (code != 0)
        {
            var details = (stderr.Length > 0 ? stderr : stdout).Trim();
            throw new PipelineError(
                details.Length > 0 ? details : $"Command failed with exit code {code}");
        }
    }
}
