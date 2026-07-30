using Highlighter.Web.Models;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;

namespace Highlighter.Web.Services;

/// <summary>Per-circuit auth state, following the StudioState idiom (private
/// setters + Changed event). The session persists across refreshes in
/// ProtectedLocalStorage — a DataProtection-encrypted blob the browser cannot
/// read; raw tokens never reach browser JS, and Supabase keys never leave the
/// API process.</summary>
public sealed class AuthSession(ProtectedLocalStorage storage, AuthApiClient authApi)
{
    private const string Key = "hl.session";
    private const int SchemaVersion = 1;
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private AuthTokens? _tokens;
    private bool _initialized;

    public AuthUser? User { get; private set; }
    public bool Initialized => _initialized;
    public bool IsAuthenticated => _tokens is not null;
    public string Email => User?.Email ?? "";

    public string Initials
    {
        get
        {
            var local = Email.Split('@')[0];
            return local.Length == 0 ? "?" : local[..Math.Min(2, local.Length)].ToUpperInvariant();
        }
    }

    public event Action? Changed;

    /// <summary>Restore from ProtectedLocalStorage. JS interop — call only after
    /// the circuit is interactive (pages render with prerender disabled, so
    /// OnInitializedAsync is safe). Idempotent.</summary>
    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            var stored = await storage.GetAsync<StoredSession>(Key);
            if (stored.Success && stored.Value is { Version: SchemaVersion } session)
            {
                _tokens = session.Tokens;
                User = session.User;
            }
        }
        catch (Exception)
        {
            // Key-ring rotation or a tampered payload — treat as signed out.
        }
        Changed?.Invoke();
    }

    public async Task SignInAsync(AuthSessionResponse session)
    {
        _tokens = new AuthTokens(session.AccessToken, session.RefreshToken, session.ExpiresAt);
        User = new AuthUser(session.User.Id, session.User.Email);
        await PersistAsync();
        Changed?.Invoke();
    }

    public async Task SignOutAsync()
    {
        var token = _tokens?.AccessToken;
        _tokens = null;
        User = null;
        try
        {
            await storage.DeleteAsync(Key);
        }
        catch (Exception)
        {
        }
        if (token is not null) await authApi.LogoutAsync(token);
        Changed?.Invoke();
    }

    /// <summary>The bearer for API calls, proactively refreshed when within 60 s
    /// of expiry. Null means signed out (possibly just now, on refresh failure).</summary>
    public async Task<string?> GetValidAccessTokenAsync(CancellationToken ct = default)
    {
        if (_tokens is null) return null;
        if (_tokens.ExpiresAt - DateTimeOffset.UtcNow > RefreshSkew) return _tokens.AccessToken;
        return await TryRefreshAsync(ct) ? _tokens?.AccessToken : null;
    }

    /// <summary>Single-flight: GoTrue rotates refresh tokens, and a second use of
    /// a rotated token revokes the whole family — the poll loop and a user click
    /// must never race a refresh.</summary>
    public async Task<bool> TryRefreshAsync(CancellationToken ct = default)
    {
        await _refreshLock.WaitAsync(ct);
        try
        {
            if (_tokens is null) return false;
            if (_tokens.ExpiresAt - DateTimeOffset.UtcNow > RefreshSkew) return true; // raced: already fresh
            var session = await authApi.RefreshAsync(_tokens.RefreshToken, ct);
            _tokens = new AuthTokens(session.AccessToken, session.RefreshToken, session.ExpiresAt);
            User = new AuthUser(session.User.Id, session.User.Email);
            await PersistAsync();
            return true;
        }
        catch (ApiException)
        {
            _tokens = null;
            User = null;
            try
            {
                await storage.DeleteAsync(Key);
            }
            catch (Exception)
            {
            }
            Changed?.Invoke();
            return false;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task PersistAsync()
    {
        try
        {
            await storage.SetAsync(Key, new StoredSession(SchemaVersion, _tokens!, User!));
        }
        catch (Exception)
        {
            // Storage write failure only costs refresh-survival, not this session.
        }
    }
}
