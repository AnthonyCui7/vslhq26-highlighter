using System.Diagnostics;

namespace Highlighter.Api.Services;

/// <summary>Best-effort background lookup of a source's real title (yt-dlp) to
/// replace the URL-derived placeholder name on freshly created projects. Runs
/// off the request path and never fails project creation: any error simply
/// leaves the placeholder in place.</summary>
public sealed class SourceTitleService
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(45);

    private readonly SupabaseDb _db;
    private readonly RepoLayout _layout;
    private readonly ILogger<SourceTitleService> _log;

    public SourceTitleService(SupabaseDb db, RepoLayout layout, ILogger<SourceTitleService> log)
    {
        _db = db;
        _layout = layout;
        _log = log;
    }

    /// <summary>Fire-and-forget: fetch the source's title and rename the project,
    /// guarded on the placeholder so a name the user typed (or later edited) is
    /// never overwritten.</summary>
    public void QueueRename(Guid projectId, string sourceUrl, string placeholderName)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var title = await FetchTitleAsync(sourceUrl);
                if (title is null || title == placeholderName) return;
                var renamed = await _db.RenameProjectIfAsync(projectId, placeholderName, title);
                if (renamed)
                    _log.LogInformation("Project {ProjectId} renamed to source title \"{Title}\"",
                        projectId, title);
            }
            catch (Exception exception)
            {
                _log.LogWarning("Source title lookup failed for project {ProjectId}: {Message}",
                    projectId, exception.Message);
            }
        });
    }

    /// <summary>The source's title via yt-dlp, or null when it can't be read
    /// (offline channel, bot wall, missing binary, timeout).</summary>
    public async Task<string?> FetchTitleAsync(string sourceUrl, CancellationToken ct = default)
    {
        var info = new ProcessStartInfo("yt-dlp")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = _layout.RepoRoot,
        };
        foreach (var arg in new[]
                 {
                     "--quiet", "--no-warnings", "--no-playlist", "--skip-download",
                     "--print", "title",
                 })
            info.ArgumentList.Add(arg);
        // Reuse the worker's decoded cookie jar when present (YouTube bot wall).
        var cookieJar = Path.Combine(_layout.RepoRoot, "data", "yt_cookies.txt");
        if (File.Exists(cookieJar))
        {
            info.ArgumentList.Add("--cookies");
            info.ArgumentList.Add(cookieJar);
        }
        info.ArgumentList.Add(sourceUrl);

        Process? process;
        try
        {
            process = Process.Start(info);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
                                              or InvalidOperationException)
        {
            _log.LogWarning("yt-dlp unavailable for title lookup: {Message}", exception.Message);
            return null;
        }
        if (process is null) return null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            // Drain both pipes concurrently so neither can fill and stall yt-dlp.
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var stdout = await stdoutTask;
            await stderrTask;
            return process.ExitCode == 0 ? CleanTitle(stdout) : null;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return null;
        }
        finally
        {
            process.Dispose();
        }
    }

    /// <summary>First non-empty line, whitespace collapsed, length capped — safe
    /// for the DB column and the projects grid.</summary>
    internal static string? CleanTitle(string raw)
    {
        var firstLine = raw.Split('\n').FirstOrDefault(line => line.Trim().Length > 0);
        if (firstLine is null) return null;
        var collapsed = string.Join(' ',
            firstLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (collapsed.Length == 0 || collapsed is "NA" or "none" or "null") return null;
        return collapsed.Length > 140 ? collapsed[..140].TrimEnd() + "…" : collapsed;
    }
}
