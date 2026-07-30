using System.Net;

namespace Highlighter.Api.Infrastructure;

/// <summary>A Supabase (PostgREST/Storage) request failed. Carries the operation and
/// a body prefix for diagnostics — never the service-role key. Mapped to a 502
/// ProblemDetails by the global exception handler.</summary>
public sealed class PostgrestException : Exception
{
    public PostgrestException(string operation, HttpStatusCode? statusCode, string detail)
        : base($"Supabase {operation} failed{(statusCode is null ? "" : $" ({(int)statusCode})")}: {detail}")
    {
        Operation = operation;
        StatusCode = statusCode;
    }

    public string Operation { get; }

    public HttpStatusCode? StatusCode { get; }
}
