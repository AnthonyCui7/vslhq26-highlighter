namespace Highlighter.Api.Contracts;

/// <summary>POST /api/projects body. SourceUrl and Pipeline are required; the rest
/// mirror the worker's ingest knobs (MaxChunks 0 = unlimited — always passed
/// explicitly so the worker's dev default of 1 can never truncate an API run).</summary>
public record CreateProjectRequest(
    string? SourceUrl,
    string? Pipeline,
    string? Name = null,
    string? Instructions = null,
    string? TargetMinutes = null,
    double? MinClipScore = null,
    int MaxChunks = 0,
    int ChunkSeconds = 90,
    bool NoResearch = false,
    bool NoShots = false,
    bool NoReframe = false,
    bool NoCaptions = false,
    bool NoThumbnails = false,
    bool NoArchive = false);

/// <summary>Stage is the row status with "created" renamed "queued". Percent derives
/// from stored chunks vs the expected total (probed source minutes / chunk seconds,
/// capped by max chunks); null when the total is unknowable (livestreams).</summary>
public record ProgressDto(string Stage, double? Percent, int ChunksStored, int? ChunksExpected);

public record ProjectSummaryDto(
    Guid Id,
    string Name,
    string SourceType,
    string SourceUrl,
    string Status,
    string Pipeline,
    string? Error,
    double? MinClipScore,
    double? DurationSeconds,
    int ClipCount,
    int ChunkCount,
    int LongformCount,
    bool Processing,
    ProgressDto Progress,
    string? ActiveJobId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public record ProjectDetailDto(
    ProjectSummaryDto Project,
    bool HasLocalMirror,
    IReadOnlyList<ClipDto> Clips,
    IReadOnlyList<LongformEditDto> LongformEdits,
    IReadOnlyList<PublicationDto> Publications);

public record CancelResultDto(Guid ProjectId, string Status, string? JobId, bool ForceKilled);
