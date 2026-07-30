namespace Highlighter.Web.Models;

public record AuthUser(Guid Id, string Email);

public record AuthTokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);

/// <summary>What survives in ProtectedLocalStorage between visits. Version guards
/// against stale shapes after upgrades — mismatches read as signed out.</summary>
public record StoredSession(int Version, AuthTokens Tokens, AuthUser User);
