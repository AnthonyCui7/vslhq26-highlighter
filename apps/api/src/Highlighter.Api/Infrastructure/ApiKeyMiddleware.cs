using System.Security.Cryptography;
using System.Text;

namespace Highlighter.Api.Infrastructure;

/// <summary>Optional API-key gate for /api/* routes. Disabled while Api:ApiKey is
/// empty (the local default); /healthz, /openapi, and /scalar stay open so probes
/// and docs work either way.</summary>
public sealed class ApiKeyMiddleware(RequestDelegate next, ApiOptions options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (options.ApiKey.Length == 0 || !context.Request.Path.StartsWithSegments("/api"))
        {
            await next(context);
            return;
        }

        if (context.Request.Headers.TryGetValue("X-Api-Key", out var provided)
            && provided.Count == 1
            && FixedTimeEquals(provided[0] ?? "", options.ApiKey))
        {
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsJsonAsync(new
        {
            type = "about:blank",
            title = "Unauthorized",
            status = 401,
            detail = "Missing or invalid X-Api-Key header.",
        });
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
