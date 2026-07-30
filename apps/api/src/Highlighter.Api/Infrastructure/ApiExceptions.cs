namespace Highlighter.Api.Infrastructure;

/// <summary>The pipeline worker binary could not be resolved or launched.
/// Mapped to 502 by the global exception handler.</summary>
public sealed class WorkerUnavailableException(string message) : Exception(message);

/// <summary>Another job blocks this request (one active job per project;
/// cleanup is globally single-flight). Mapped to 409.</summary>
public sealed class JobConflictException(string message) : Exception(message);

/// <summary>An auth flow failed in a way the client should see (wrong password,
/// duplicate account, GoTrue unreachable). Carries the HTTP status + title the
/// endpoint returns as ProblemDetails.</summary>
public sealed class AuthFlowException(int status, string title, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Title { get; } = title;
}
