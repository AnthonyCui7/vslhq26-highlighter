namespace Highlighter.Api.Infrastructure;

/// <summary>The pipeline worker binary could not be resolved or launched.
/// Mapped to 502 by the global exception handler.</summary>
public sealed class WorkerUnavailableException(string message) : Exception(message);

/// <summary>Another job blocks this request (one active job per project;
/// cleanup is globally single-flight). Mapped to 409.</summary>
public sealed class JobConflictException(string message) : Exception(message);
