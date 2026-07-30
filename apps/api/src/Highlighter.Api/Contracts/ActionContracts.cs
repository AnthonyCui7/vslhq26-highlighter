namespace Highlighter.Api.Contracts;

public record ReviseRequestDto(string? Request);

/// <summary>Target "longform" publishes the stitched edit (Version picks one);
/// target "clip" requires ClipId, resolved server-side to the worker's filename
/// handle. Platforms come from the worker's allowed set; "x" is a promo layer and
/// needs a companion platform.</summary>
public record PublishRequestDto(
    string? Target,
    Guid? ClipId = null,
    int? Version = null,
    string[]? Platforms = null,
    string? Title = null,
    string? Thumbnail = null,
    bool Plain = false,
    bool DryRun = false);

public record ReclipRequestDto(
    double StartSeconds, double EndSeconds, string? Title = null, string? Description = null);

public record CleanupRequestDto(int Limit = 100);
