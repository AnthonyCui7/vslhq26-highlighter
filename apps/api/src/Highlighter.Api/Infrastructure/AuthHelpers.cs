using System.Security.Claims;

namespace Highlighter.Api.Infrastructure;

internal static class AuthHelpers
{
    /// <summary>The Supabase user id from the validated JWT's "sub" claim
    /// (JwtBearer runs with MapInboundClaims=false so the name survives).
    /// Null when the request is anonymous — i.e. Api:RequireAuth is off —
    /// which keeps every query unscoped, matching pre-auth behavior.</summary>
    public static Guid? Uid(ClaimsPrincipal? user) =>
        Guid.TryParse(user?.FindFirst("sub")?.Value, out var id) ? id : null;
}
