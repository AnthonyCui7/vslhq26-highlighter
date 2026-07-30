using Microsoft.IdentityModel.Tokens;

namespace Highlighter.Api.Infrastructure;

/// <summary>Lazy-cached JWKS for validating Supabase (GoTrue) access tokens.
/// GoTrue publishes ES256 keys at /auth/v1/.well-known/jwks.json but no OIDC
/// discovery document, so JwtBearer's metadata pipeline can't be used; this is
/// wired through TokenValidationParameters.IssuerSigningKeyResolver instead.
/// Fetches on first use (no startup network dependency), refreshes every
/// 10 minutes or when an unknown kid shows up (key rotation).</summary>
public class SupabaseJwks(IHttpClientFactory factory, ILogger<SupabaseJwks> log, string? jwksUrl)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    private readonly object _gate = new();
    private IReadOnlyList<SecurityKey> _keys = [];
    private DateTimeOffset _fetchedAt = DateTimeOffset.MinValue;

    public static SupabaseJwks FromEnv(IHttpClientFactory factory, ILogger<SupabaseJwks> log)
    {
        var baseUrl = Environment.GetEnvironmentVariable("SUPABASE_URL")?.TrimEnd('/');
        return new SupabaseJwks(factory, log,
            baseUrl is null ? null : $"{baseUrl}/auth/v1/.well-known/jwks.json");
    }

    public virtual IEnumerable<SecurityKey> GetKeys(string? kid)
    {
        var stale = DateTimeOffset.UtcNow - _fetchedAt > CacheTtl;
        var unknownKid = kid is not null && !_keys.Any(key => key.KeyId == kid);
        if (_keys.Count == 0 || stale || unknownKid) Refresh();
        return _keys;
    }

    private void Refresh()
    {
        lock (_gate)
        {
            if (jwksUrl is null) return;
            // Re-check under the lock so a burst of 401-adjacent requests fetches once.
            if (_keys.Count > 0 && DateTimeOffset.UtcNow - _fetchedAt < TimeSpan.FromSeconds(30)) return;
            try
            {
                var json = factory.CreateClient(Services.SupabaseAuth.HttpClientName)
                    .GetStringAsync(jwksUrl).GetAwaiter().GetResult();
                _keys = [.. new JsonWebKeySet(json).GetSigningKeys()];
                _fetchedAt = DateTimeOffset.UtcNow;
                log.LogInformation("Loaded {Count} Supabase signing key(s)", _keys.Count);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                log.LogError("JWKS fetch failed: {Message}", exception.Message);
            }
        }
    }
}
