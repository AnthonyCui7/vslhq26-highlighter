using Highlighter.Api.Infrastructure;

namespace Highlighter.Api.Services;

/// <summary>Drains the media_cleanup_jobs outbox by running the worker's `cleanup`
/// verb — nothing else in the system schedules it. Periodic, plus an immediate
/// nudge after project deletes. Single-flight: two concurrent drainers would
/// double-process the same jobs (the outbox has no claim/lease).</summary>
public sealed class MediaCleanupScheduler(
    PipelineJobService jobs,
    SupabaseDb db,
    PipelineOptions options,
    ILogger<MediaCleanupScheduler> log) : BackgroundService
{
    private readonly SemaphoreSlim _wake = new(0);

    public void RequestDrain() => _wake.Release();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, options.CleanupIntervalMinutes));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _wake.WaitAsync(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            while (_wake.Wait(0))
            {
                // Collapse queued nudges into this run.
            }

            if (!db.IsConfigured) continue;
            try
            {
                var pending = await db.CountCleanupJobsAsync("pending", stoppingToken);
                if (pending == 0) continue;
                log.LogInformation("Media cleanup: {Pending} pending job(s); starting drain", pending);
                jobs.Start("cleanup", null, WorkerArgs.Cleanup(Math.Max(1, options.CleanupLimit)));
            }
            catch (JobConflictException)
            {
                // A drain is already running; the next tick catches leftovers.
            }
            catch (WorkerUnavailableException exception)
            {
                log.LogWarning("Media cleanup skipped: {Message}", exception.Message);
            }
            catch (PostgrestException exception)
            {
                log.LogWarning("Media cleanup check failed: {Message}", exception.Message);
            }
            catch (Exception exception)
            {
                // Never let an unexpected error escape ExecuteAsync — the default
                // BackgroundService behavior would take the whole API down with it.
                log.LogError(exception, "Media cleanup tick failed unexpectedly");
            }
        }
    }
}
