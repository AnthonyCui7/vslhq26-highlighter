namespace Highlighter.Api.Contracts;

public record SignupRequest(string? Email, string? Password);

public record LoginRequest(string? Email, string? Password);

public record RefreshRequest(string? RefreshToken);

public record AuthUserDto(Guid Id, string Email);

/// <summary>A Supabase (GoTrue) session as returned by /api/auth/signup, /login
/// and /refresh. The access token is the Bearer credential for every /api/*
/// call; the refresh token is single-use (GoTrue rotates it on refresh).</summary>
public record AuthSessionDto(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    AuthUserDto User);
