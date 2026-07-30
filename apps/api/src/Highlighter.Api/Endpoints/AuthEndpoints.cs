using Highlighter.Api.Contracts;
using Highlighter.Api.Infrastructure;
using Highlighter.Api.Services;
using static Highlighter.Api.Endpoints.ProjectEndpoints;

namespace Highlighter.Api.Endpoints;

/// <summary>Email+password auth proxied to Supabase GoTrue. These endpoints are
/// never behind the JWT gate — they are how a client gets a token. The browser
/// never sees a Supabase key; it only ever holds the short-lived user JWT.</summary>
public static class AuthEndpoints
{
    public static IEndpointConventionBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth");

        group.MapPost("/signup", async (SignupRequest body, SupabaseAuth auth, CancellationToken ct) =>
            {
                if (ValidateCredentials(body.Email, body.Password) is { } invalid) return invalid;
                return await Run(() => auth.SignupAsync(body.Email!.Trim(), body.Password!, ct));
            })
            .WithName("Signup");

        group.MapPost("/login", async (LoginRequest body, SupabaseAuth auth, CancellationToken ct) =>
            {
                if (ValidateCredentials(body.Email, body.Password) is { } invalid) return invalid;
                return await Run(() => auth.LoginAsync(body.Email!.Trim(), body.Password!, ct));
            })
            .WithName("Login");

        group.MapPost("/refresh", async (RefreshRequest body, SupabaseAuth auth, CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(body.RefreshToken))
                    return Problem(400, "Invalid request", "refreshToken is required");
                return await Run(() => auth.RefreshAsync(body.RefreshToken, ct));
            })
            .WithName("Refresh");

        group.MapPost("/logout", async (HttpContext context, SupabaseAuth auth, CancellationToken ct) =>
            {
                var header = context.Request.Headers.Authorization.ToString();
                if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    await auth.LogoutAsync(header["Bearer ".Length..].Trim(), ct);
                return Results.NoContent();
            })
            .WithName("Logout");

        return group;
    }

    private static IResult? ValidateCredentials(string? email, string? password)
    {
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@') || email.Trim().Contains(' '))
            return Problem(400, "Invalid request", "a valid email is required");
        if (string.IsNullOrEmpty(password) || password.Length < 6)
            return Problem(400, "Invalid request", "password must be at least 6 characters");
        return null;
    }

    private static async Task<IResult> Run(Func<Task<AuthSessionDto>> flow)
    {
        try
        {
            return Results.Ok(await flow());
        }
        catch (AuthFlowException exception)
        {
            return Problem(exception.Status, exception.Title, exception.Message);
        }
    }
}
