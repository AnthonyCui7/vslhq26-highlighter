namespace Highlighter.Api.Contracts;

/// <summary>A tracked worker process. State: starting | running | succeeded |
/// failed | killed. LogPath is the durable on-disk record and survives API
/// restarts; the in-memory buffer serves tails and live streams.</summary>
public record JobDto(
    string Id,
    string Kind,
    Guid? ProjectId,
    string State,
    IReadOnlyList<string> Argv,
    int? ExitCode,
    string? FailureReason,
    string? LogPath,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    long LastLogSeq);

public record JobLogLineDto(long Seq, DateTimeOffset At, string Stream, string Line);
