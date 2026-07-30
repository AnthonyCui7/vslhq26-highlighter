using Highlighter.Api.Services;
using Xunit;

namespace Highlighter.Api.Tests;

public class JobStateMachineTests : IDisposable
{
    private readonly string _logDir = Path.Combine(Path.GetTempPath(), $"joblogs-{Guid.NewGuid():N}");

    public JobStateMachineTests() => Directory.CreateDirectory(_logDir);

    public void Dispose() => Directory.Delete(_logDir, recursive: true);

    private PipelineJob NewJob(string kind = "ingest") =>
        new($"job_{Guid.NewGuid():N}"[..16], kind, Guid.NewGuid(),
            ["dotnet", "highlighter.dll", kind], Path.Combine(_logDir, $"{Guid.NewGuid():N}.log"));

    [Fact]
    public void LifecycleStates()
    {
        var job = NewJob();
        Assert.Equal(JobState.Starting, job.State);
        Assert.False(job.IsTerminal);

        job.MarkRunning();
        Assert.Equal(JobState.Running, job.State);

        job.MarkExited(0, null);
        Assert.Equal(JobState.Succeeded, job.State);
        Assert.True(job.IsTerminal);
        Assert.Equal(0, job.ExitCode);
        Assert.Null(job.FailureReason);
        Assert.NotNull(job.EndedAt);
    }

    [Fact]
    public void NonZeroExitIsFailedWithReason()
    {
        var job = NewJob();
        job.MarkRunning();
        job.MarkExited(1, null);

        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("exited with code 1", job.FailureReason);
    }

    [Fact]
    public void KillRequestedWinsOverExitCode()
    {
        var job = NewJob();
        job.MarkRunning();
        job.RequestKill();
        job.MarkExited(137, null);

        Assert.Equal(JobState.Killed, job.State);
        Assert.Equal("killed by force-cancel", job.FailureReason);
    }

    [Fact]
    public void SpawnFailureIsTerminalFailed()
    {
        var job = NewJob();
        job.MarkFailed("spawn failed: file not found");

        Assert.Equal(JobState.Failed, job.State);
        Assert.True(job.IsTerminal);
        Assert.Null(job.ExitCode);
    }

    [Fact]
    public void SinkWritesHeaderLinesAndFooter()
    {
        var job = NewJob();
        job.OpenSink(["job header", "command: x"]);
        job.MarkRunning();
        job.Append("out", "hello");
        job.Append("err", "warning");
        job.MarkExited(0, "row status after exit: ready");

        var lines = File.ReadAllLines(job.LogPath);
        Assert.Contains(lines, line => line.Contains("[api] job header"));
        Assert.Contains(lines, line => line.Contains("[out] hello"));
        Assert.Contains(lines, line => line.Contains("[err] warning"));
        Assert.Contains(lines, line => line.Contains("state succeeded"));
        Assert.Contains(lines, line => line.Contains("row status after exit: ready"));
    }

    [Fact]
    public async Task StreamLogs_ReplaysSnapshotThenFollowsLive()
    {
        var job = NewJob();
        job.MarkRunning();
        job.Append("out", "one");
        job.Append("out", "two");

        var collected = new List<string>();
        var reading = Task.Run(async () =>
        {
            await foreach (var line in job.StreamLogsAsync(tail: 10))
                collected.Add(line.Line);
        });

        // Give the reader a moment to drain the snapshot, then go live.
        await Task.Delay(100);
        job.Append("out", "three");
        job.MarkExited(0, null);
        await reading.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["one", "two", "three"], collected);
    }

    [Fact]
    public async Task StreamLogs_OnTerminalJobEndsAfterSnapshot()
    {
        var job = NewJob();
        job.MarkRunning();
        job.Append("out", "only");
        job.MarkExited(0, null);

        var collected = new List<string>();
        await foreach (var line in job.StreamLogsAsync(tail: 10))
            collected.Add(line.Line);

        Assert.Equal(["only"], collected);
    }

    // ---- DecideRowFixup: the API may only fix the row once the worker is observed dead ----

    [Theory]
    [InlineData("created", "failed", "created")]
    [InlineData("ingesting", "failed", "ingesting")]
    [InlineData("stopping", "cancelled", "stopping")]
    public void RowFixup_NonTerminalRowsGetGuardedTerminalWrites(
        string rowStatus, string expectedStatus, string expectedGuard)
    {
        var fixup = PipelineJobService.DecideRowFixup(rowStatus, exitCode: 1, jobId: "job_abc");

        Assert.NotNull(fixup);
        Assert.Equal(expectedStatus, fixup.Value.Status);
        Assert.Equal([expectedGuard], fixup.Value.Guard);
        if (expectedStatus == "failed")
        {
            Assert.Contains("job_abc", fixup.Value.Error);
            Assert.Contains("exit 1", fixup.Value.Error);
        }
        else
        {
            Assert.Null(fixup.Value.Error);
        }
    }

    [Theory]
    [InlineData("ready")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    [InlineData(null)]
    public void RowFixup_TerminalOrMissingRowsAreLeftAlone(string? rowStatus)
    {
        Assert.Null(PipelineJobService.DecideRowFixup(rowStatus, exitCode: 0, jobId: "job_abc"));
    }

    [Fact]
    public void ParseLogLine_RoundTripsTheSinkFormat()
    {
        var at = new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
        var raw = PipelineJob.Format("out", "Stored chunk 3: hello", at);

        var parsed = PipelineJobService.ParseLogLine(raw, seq: 7);

        Assert.Equal(7, parsed.Seq);
        Assert.Equal("out", parsed.Stream);
        Assert.Equal("Stored chunk 3: hello", parsed.Line);
        Assert.Equal(at, parsed.At);
    }
}
