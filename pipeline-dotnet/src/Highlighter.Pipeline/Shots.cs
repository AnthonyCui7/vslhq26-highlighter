using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Highlighter.Pipeline;

/// <summary>Port of highlighter_pipeline/shots.py.
///
/// Shot-boundary detection and cut-aligned clip boundary snapping.
///
/// TransNetV2 finds the source's own visual transitions in each archived video
/// segment. The cut timestamps feed the editor prompts as context (Llm) and,
/// after the model has picked a clip window, SnapWindow() moves each boundary to
/// a nearby cut so clips start and end on the source's own edit points instead of
/// mid-transition. The model picks the moment; code picks the frame.
///
/// The PyTorch inference itself stays in Python: ShotDetector talks to the
/// long-lived `highlighter_pipeline.shots_sidecar` process over JSON lines.
/// ShotDetectorFromEnv() degrades to null with one log line when the sidecar
/// cannot start (missing python/torch), and every consumer treats cuts as
/// optional — the same contract as the Python optional-import path.</summary>
public static class Shots
{
    // Snapping must never produce a degenerate clip.
    public const double MIN_SNAPPED_CLIP_SECONDS = 2.0;

    /// <summary>A ready ShotDetector, or null when disabled or the Python sidecar
    /// is unavailable (logged once; the pipeline runs without scene cuts either way).</summary>
    public static ShotDetector? ShotDetectorFromEnv(bool enabled)
    {
        if (!enabled) return null;
        try
        {
            return ShotDetector.Start();
        }
        catch (Exception exc)
        {
            Console.WriteLine(
                $"Shot detection unavailable ({exc.Message}); continuing without scene cuts. "
                + "Install with: pip install 'highlighter-pipeline[shots]'");
            return null;
        }
    }

    /// <summary>Move each clip boundary to the nearest detected cut within
    /// toleranceSeconds, refusing cuts that land inside a spoken word
    /// (wordSpans: chronologically sorted (absoluteStart, absoluteEnd) pairs)
    /// so a snap never clips speech. Returns (start, end, info); info describes
    /// the change, or is null when nothing moved. The original window is kept
    /// whenever snapping would shrink the clip below minDurationSeconds.</summary>
    public static (double Start, double End, JsonObject? Info) SnapWindow(
        double startSeconds,
        double endSeconds,
        IReadOnlyList<double> cuts,
        IReadOnlyList<(double Start, double End)> wordSpans,
        double toleranceSeconds = Defaults.DEFAULT_SHOT_SNAP_TOLERANCE_SECONDS,
        double minDurationSeconds = MIN_SNAPPED_CLIP_SECONDS)
    {
        var snappedStart = SnapBoundary(
            startSeconds, cuts: cuts, wordSpans: wordSpans, toleranceSeconds: toleranceSeconds);
        var snappedEnd = SnapBoundary(
            endSeconds, cuts: cuts, wordSpans: wordSpans, toleranceSeconds: toleranceSeconds);
        if (snappedStart == startSeconds && snappedEnd == endSeconds)
            return (startSeconds, endSeconds, null);
        if (snappedEnd - snappedStart < minDurationSeconds)
            return (startSeconds, endSeconds, null);
        return (
            snappedStart,
            snappedEnd,
            new JsonObject
            {
                ["original_start"] = startSeconds,
                ["original_end"] = endSeconds,
                ["snapped_start"] = snappedStart,
                ["snapped_end"] = snappedEnd,
            });
    }

    private static double SnapBoundary(
        double value,
        IReadOnlyList<double> cuts,
        IReadOnlyList<(double Start, double End)> wordSpans,
        double toleranceSeconds)
    {
        double? best = null;
        foreach (var cut in cuts)
        {
            if (Math.Abs(cut - value) > toleranceSeconds) continue;
            if (InsideWord(cut, wordSpans)) continue;
            if (best is null || Math.Abs(cut - value) < Math.Abs(best.Value - value)) best = cut;
        }
        return best ?? value;
    }

    private static bool InsideWord(
        double timestamp, IReadOnlyList<(double Start, double End)> wordSpans)
    {
        // bisect_right over (start, end) tuples with key (timestamp, +inf):
        // the last span whose start is <= timestamp.
        int low = 0, high = wordSpans.Count;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (wordSpans[mid].Start <= timestamp) low = mid + 1;
            else high = mid;
        }
        var index = low - 1;
        if (index < 0) return false;
        var (wordStart, wordEnd) = wordSpans[index];
        return wordStart < timestamp && timestamp < wordEnd;
    }
}

/// <summary>Client for the Python TransNetV2 sidecar. One long-lived process per
/// run: the model loads lazily inside the sidecar on the first request and is
/// reused for the rest of the run, matching the Python ShotDetector.</summary>
public sealed class ShotDetector : IDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _stdin;
    private readonly StreamReader _stdout;
    private readonly object _lock = new();

    private ShotDetector(Process process)
    {
        _process = process;
        _stdin = process.StandardInput;
        _stdout = process.StandardOutput;
    }

    /// <summary>Launch `python -m highlighter_pipeline.shots_sidecar` from the
    /// monorepo's pipeline/ directory (found by walking up from the working
    /// directory) and wait for its ready handshake. Throws when the sidecar
    /// cannot start — the caller degrades to no scene cuts.</summary>
    public static ShotDetector Start()
    {
        var pipelineDir = FindPipelineDir()
            ?? throw new PipelineError("pipeline/highlighter_pipeline/shots_sidecar.py not found");
        var python = Config.EnvOrNull("PYTHON") ?? "python3";

        var info = new ProcessStartInfo(python)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false, // sidecar logs (torch warnings etc.) pass through
            UseShellExecute = false,
            WorkingDirectory = pipelineDir,
        };
        info.ArgumentList.Add("-m");
        info.ArgumentList.Add("highlighter_pipeline.shots_sidecar");

        var process = Process.Start(info)
            ?? throw new PipelineError("could not start the shots sidecar process");
        try
        {
            // The handshake arrives once imports succeed; torch alone can take a
            // while on first load, so the wait is generous.
            var handshakeTask = process.StandardOutput.ReadLineAsync();
            if (!handshakeTask.Wait(TimeSpan.FromSeconds(180)))
                throw new PipelineError("the shots sidecar did not respond in time");
            var handshake = handshakeTask.Result;
            if (handshake is null || JsonUtil.Parse(handshake) is not JsonObject ready
                || !JsonUtil.Truthy(ready["ready"]))
                throw new PipelineError("the shots sidecar exited before it was ready");
        }
        catch
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        return new ShotDetector(process);
    }

    private static string? FindPipelineDir()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName, "pipeline", "highlighter_pipeline", "shots_sidecar.py");
            if (File.Exists(candidate)) return Path.Combine(directory.FullName, "pipeline");
            directory = directory.Parent!;
        }
        return null;
    }

    /// <summary>Absolute timestamps (seconds) of the shot transitions in one
    /// segment. Consecutive transition frames collapse into one cut inside the
    /// sidecar, exactly as the Python ShotDetector does.</summary>
    public List<double> DetectCuts(string segmentPath, double segmentStartSeconds)
    {
        lock (_lock)
        {
            if (_process.HasExited)
                throw new PipelineError("the shots sidecar process has exited");
            var request = new JsonObject
            {
                ["path"] = Path.GetFullPath(segmentPath),
                ["start_seconds"] = segmentStartSeconds,
            };
            _stdin.WriteLine(JsonUtil.Dumps(request));
            _stdin.Flush();
            var line = _stdout.ReadLine()
                ?? throw new PipelineError("the shots sidecar closed its output stream");
            var response = JsonUtil.ParseObject(line);
            if (response.TryGetPropertyValue("error", out var error))
                throw new PipelineError(JsonUtil.Str(error));
            return (response["cuts"] as JsonArray ?? new JsonArray())
                .Select(cut => JsonUtil.Double(cut))
                .ToList();
        }
    }

    public void Dispose()
    {
        try
        {
            _stdin.Close(); // EOF lets the sidecar exit its read loop
            if (!_process.WaitForExit(2000)) _process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
        _process.Dispose();
    }
}
