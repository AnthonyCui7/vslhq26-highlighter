using System.Diagnostics;

namespace Highlighter.Pipeline;

/// <summary>Port of highlighter_pipeline/capture.py: tap the source with
/// streamlink/yt-dlp, tee it through one ffmpeg segmenter into 16kHz mono WAV
/// chunks (plus optional codec-copied video segments), and hand finished files
/// to callbacks as they appear.</summary>
public static class Capture
{
    public delegate void ChunkCallback(string audioPath, int chunkIndex, int startSeconds, int endSeconds);

    public delegate void VideoSegmentCallback(string segmentPath, int segmentIndex);

    public static void AssertCapturePrerequisites(string downloader = "streamlink")
    {
        AssertBinary("ffmpeg", "brew install ffmpeg");
        AssertBinary("ffprobe", "brew install ffmpeg");
        if (downloader == "yt-dlp") AssertBinary("yt-dlp", "pip install yt-dlp");
        else AssertBinary("streamlink", "brew install streamlink");
    }

    /// <summary>Capture audio chunks for transcription; optionally also archive the source
    /// video as codec-copied segments (cut on keyframes, so boundaries are approximate).
    ///
    /// downloader: 'streamlink' taps live Twitch broadcasts; 'yt-dlp' handles
    /// everything else (all of YouTube, since only yt-dlp has cookie support for
    /// YouTube's bot wall, plus finished Twitch VODs).
    ///
    /// sourceType: for yt-dlp, 'livestream' emits the full AV stream as pipeable
    /// MPEG-TS (so the live tee into wav chunks + archived .ts segments works
    /// like the streamlink path); 'video' also pulls the full AV stream whenever
    /// archiveDir is set — clips are rendered from the local segments — and
    /// falls back to audio-only for transcription-only runs. Ignored for
    /// streamlink, which always taps live.
    ///
    /// shouldStop: polled between sweeps and before each ready file within a
    /// sweep; returning true ends the capture early, skipping any files that have
    /// not been processed yet (the caller handles queued leftovers).</summary>
    public static int CaptureAudioChunks(
        string sourceUrl,
        string outputDir,
        int chunkSeconds,
        int maxChunks,
        string streamlinkQuality,
        ChunkCallback onChunk,
        string downloader = "streamlink",
        string sourceType = "video",
        string? archiveDir = null,
        VideoSegmentCallback? onVideoSegment = null,
        Func<bool>? shouldStop = null)
    {
        AssertCapturePrerequisites(downloader);
        Directory.CreateDirectory(outputDir);
        if (archiveDir is not null) Directory.CreateDirectory(archiveDir);

        List<string> sourceCmd;
        if (downloader == "yt-dlp" && sourceType == "livestream")
        {
            // Full AV stream to stdout for the live tee. -f b: best pre-merged
            // format (merging separate streams is impossible when writing to
            // stdout). --hls-use-mpegts keeps HLS output pipeable MPEG-TS even
            // when the "livestream" turns out to be a plain uploaded video
            // (yt-dlp only defaults to it for actual live streams); such a
            // mislabeled video simply ends at EOF like a finished broadcast.
            sourceCmd = new List<string> { "yt-dlp", "--quiet", "--no-warnings" };
            sourceCmd.AddRange(Cookies.YtdlpCookieArgs());
            sourceCmd.AddRange(new[] { "-f", "b", "--no-part", "--hls-use-mpegts", "-o", "-", sourceUrl });
        }
        else if (downloader == "yt-dlp" && archiveDir is not null)
        {
            // VOD with a local archive: pull the full AV stream once — clips are
            // rendered from the archived segments, because seeking remote HLS is
            // unreliable (Debian ffmpeg mangles Twitch VOD seeks into empty
            // files) and VODs/highlights can be deleted mid-job.
            sourceCmd = new List<string> { "yt-dlp", "--quiet", "--no-warnings" };
            sourceCmd.AddRange(Cookies.YtdlpCookieArgs());
            sourceCmd.AddRange(new[]
            {
                // Twitch highlight VODs place the HLS init fragment after media
                // fragments; yt-dlp's native downloader refuses that ("unable to
                // download"), ffmpeg streams it fine. Scoped to m3u8 so other
                // protocols keep the native downloader.
                "--downloader",
                "m3u8:ffmpeg",
                "-f",
                "b",
                "--no-part",
                "-o",
                "-",
                sourceUrl,
            });
        }
        else if (downloader == "yt-dlp")
        {
            sourceCmd = new List<string> { "yt-dlp", "--quiet", "--no-warnings" };
            sourceCmd.AddRange(Cookies.YtdlpCookieArgs());
            sourceCmd.AddRange(new[]
            {
                // Same m3u8 quirk as above; audio-only is enough when no local
                // archive is kept (transcription-only runs).
                "--downloader",
                "m3u8:ffmpeg",
                "-f",
                "ba/b",
                "-o",
                "-",
                sourceUrl,
            });
        }
        else
        {
            sourceCmd = new List<string>
            {
                "streamlink",
                "--stdout",
                sourceUrl,
                string.IsNullOrEmpty(streamlinkQuality)
                    ? Defaults.DEFAULT_STREAMLINK_QUALITY
                    : streamlinkQuality,
            };
        }

        var sourceInfo = Proc.StartInfo(sourceCmd);
        var source = Process.Start(sourceInfo)
            ?? throw new PipelineError($"Could not start {sourceCmd[0]}");
        var sourceStderr = new StderrTail(source);

        var durationArgs = maxChunks > 0
            ? new List<string> { "-t", Py.S(maxChunks * chunkSeconds) }
            : new List<string>();
        var segmentArgs = new List<string>
        {
            "-f",
            "segment",
            "-segment_time",
            Py.S(chunkSeconds),
            "-reset_timestamps",
            "1",
        };
        // Archive segments keep the continuous capture timeline: copy cuts land
        // on keyframes, so a segment's true start drifts past its nominal
        // boundary, and the preserved timestamps are how downstream rendering
        // recovers the real position (render reads it back with ProbeStartTime).
        var archiveSegmentArgs = new List<string>
        {
            "-f",
            "segment",
            "-segment_time",
            Py.S(chunkSeconds),
        };

        var ffmpegArgs = new List<string>
        {
            "ffmpeg",
            "-hide_banner",
            "-loglevel",
            "warning",
            "-i",
            "pipe:0",
            // 16kHz mono WAV chunks for transcription
            "-vn",
            "-map",
            "0:a:0",
            "-ac",
            "1",
            "-ar",
            "16000",
        };
        ffmpegArgs.AddRange(durationArgs);
        ffmpegArgs.AddRange(segmentArgs);
        ffmpegArgs.AddRange(new[] { "-c:a", "pcm_s16le", Path.Combine(outputDir, "audio_%05d.wav") });
        if (archiveDir is not null)
        {
            // full source archive, no re-encode
            ffmpegArgs.AddRange(new[] { "-map", "0", "-c", "copy" });
            ffmpegArgs.AddRange(durationArgs);
            ffmpegArgs.AddRange(archiveSegmentArgs);
            ffmpegArgs.Add(Path.Combine(archiveDir, "video_%05d.ts"));
        }

        var ffmpegInfo = Proc.StartInfo(ffmpegArgs);
        ffmpegInfo.RedirectStandardInput = true;
        ffmpegInfo.RedirectStandardOutput = false;
        var ffmpeg = Process.Start(ffmpegInfo)
            ?? throw new PipelineError("Could not start ffmpeg");
        var ffmpegStderr = new StderrTail(ffmpeg);
        // The pump replaces the kernel-level pipe Python gets by handing the fd
        // over; closing ffmpeg's stdin at source EOF is what lets ffmpeg flush
        // and finalize the in-progress output files.
        _ = Task.Run(() =>
        {
            try
            {
                source.StandardOutput.BaseStream.CopyTo(ffmpeg.StandardInput.BaseStream);
            }
            catch (IOException)
            {
                // ffmpeg exited first — same as a broken pipe in Python
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                try { ffmpeg.StandardInput.Close(); } catch { }
            }
        });

        var processed = new HashSet<string>();
        var archived = new HashSet<string>();
        var processedFailures = 0;
        var archivedFailures = 0;

        void Sweep(bool includeLatest)
        {
            // Video segments before audio chunks: both finalize together (one
            // ffmpeg segmenter), and the segment handler derives per-chunk scene
            // cuts that the transcript store and clip scoring want available.
            if (archiveDir is not null && onVideoSegment is not null)
            {
                ProcessReadyFiles(
                    archiveDir,
                    "video_*.ts",
                    archived,
                    includeLatest: includeLatest,
                    handler: (path, index) => onVideoSegment(path, index),
                    consecutiveFailures: ref archivedFailures,
                    shouldStop: shouldStop);
            }
            ProcessReadyFiles(
                outputDir,
                "audio_*.wav",
                processed,
                includeLatest: includeLatest,
                handler: (path, index) => onChunk(
                    path, index, index * chunkSeconds, (index + 1) * chunkSeconds),
                consecutiveFailures: ref processedFailures,
                shouldStop: shouldStop);
        }

        var stopped = false;
        try
        {
            while (!ffmpeg.HasExited)
            {
                if (shouldStop is not null && shouldStop())
                {
                    stopped = true;
                    // Stop like a natural stream end: close the source so ffmpeg
                    // flushes and finalizes the in-progress output files.
                    Terminate(source);
                    if (!ffmpeg.WaitForExit(10_000)) Terminate(ffmpeg);
                    break;
                }
                Sweep(includeLatest: false);
                Thread.Sleep(2000);
            }

            // The final sweep can hold the entire backlog (a VOD finishes
            // downloading long before its chunks are processed), so honor a stop
            // here too instead of grinding through every remaining file.
            if (shouldStop is not null && shouldStop()) stopped = true;
            if (!stopped) Sweep(includeLatest: true);
        }
        catch
        {
            Terminate(ffmpeg);
            Terminate(source);
            throw;
        }

        // Whether the source ended on its own, before Terminate below reaps it
        // (a still-running source is normal when maxChunks capped the capture).
        var sourceEnded = false;
        try { sourceEnded = source.HasExited; } catch (InvalidOperationException) { }
        Terminate(source);

        if (!stopped)
        {
            ffmpeg.WaitForExit();
            if (ffmpeg.ExitCode != 0)
            {
                var details = string.Join("\n",
                    new[] { ffmpegStderr.Text(), sourceStderr.Text() }
                        .Where(part => part.Length > 0));
                throw new PipelineError(
                    $"ffmpeg exited with code {ffmpeg.ExitCode}"
                    + (details.Length > 0 ? "\n" + details : ""));
            }
            if (sourceEnded)
            {
                try
                {
                    if (source.ExitCode != 0)
                    {
                        var tail = sourceStderr.Text();
                        Console.WriteLine(
                            $"Warning: {sourceCmd[0]} exited with code {source.ExitCode}; "
                            + "the source ended early and the capture may be partial."
                            + (tail.Length > 0 ? "\n" + tail : ""));
                    }
                }
                catch (InvalidOperationException)
                {
                    // Exit code unavailable; nothing to report.
                }
            }
        }

        return processed.Count;
    }

    private static void ProcessReadyFiles(
        string directory,
        string pattern,
        HashSet<string> processed,
        bool includeLatest,
        Action<string, int> handler,
        ref int consecutiveFailures,
        Func<bool>? shouldStop = null)
    {
        var files = Directory.GetFiles(directory, pattern)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        var readyFiles = includeLatest ? files : files.Take(Math.Max(0, files.Count - 1)).ToList();

        foreach (var path in readyFiles)
        {
            var name = Path.GetFileName(path);
            if (processed.Contains(name)) continue;
            if (shouldStop is not null && shouldStop())
            {
                // A stop arrived mid-backlog: skip the remaining unprocessed files.
                return;
            }

            int index;
            try
            {
                index = int.Parse(
                    Path.GetFileNameWithoutExtension(path).Split('_')[1],
                    System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception exc) when (exc is FormatException or OverflowException or IndexOutOfRangeException)
            {
                Console.WriteLine($"Skipping {name}: could not parse its index ({exc.Message})");
                processed.Add(name);
                continue;
            }

            try
            {
                handler(path, index);
                consecutiveFailures = 0;
            }
            catch (Exception exc)
            {
                // One bad file must not end the capture, but a persistent outage
                // must not silently produce an empty run either.
                consecutiveFailures++;
                if (consecutiveFailures >= 5)
                {
                    Console.WriteLine(
                        $"Processing {name} failed ({exc.Message}); "
                        + $"{consecutiveFailures} consecutive failures, giving up");
                    throw;
                }
                Console.WriteLine($"Processing {name} failed (non-fatal): {exc.Message}");
            }
            processed.Add(name);
        }
    }

    private static void AssertBinary(string name, string installHint)
    {
        if (Proc.Which(name) is null)
            throw new PipelineError(
                $"Missing required binary: {name}. Install locally with '{installHint}'.");
    }

    /// <summary>Python Popen.terminate() (SIGTERM), then kill() after 5s — the
    /// process alone, not its tree, matching Python's behavior.</summary>
    public static void Terminate(Process process)
    {
        if (process.HasExited) return;
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                using var kill = Process.Start(new ProcessStartInfo("kill")
                {
                    ArgumentList = { "-TERM", process.Id.ToString() },
                    RedirectStandardError = true,
                });
                kill?.WaitForExit();
            }
            else
            {
                process.Kill();
            }
        }
        catch
        {
        }
        if (!process.WaitForExit(5000))
        {
            try { process.Kill(); } catch { }
            process.WaitForExit();
        }
    }
}

/// <summary>Port of capture.py's _StderrTail: a daemon reader keeping the last
/// 80 stderr lines of a process for error reporting.</summary>
public sealed class StderrTail
{
    private readonly object _lock = new();
    private readonly Queue<string> _lines = new();

    public StderrTail(Process process)
    {
        var thread = new Thread(() => ReadLines(process)) { IsBackground = true };
        thread.Start();
    }

    public string Text()
    {
        lock (_lock)
        {
            return string.Join("\n", _lines).Trim();
        }
    }

    private void ReadLines(Process process)
    {
        try
        {
            while (process.StandardError.ReadLine() is { } line)
            {
                lock (_lock)
                {
                    _lines.Enqueue(line);
                    while (_lines.Count > 80) _lines.Dequeue();
                }
            }
        }
        catch
        {
        }
    }
}
