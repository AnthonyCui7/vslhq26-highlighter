using System.Net.Http.Headers;
using System.Net.Http.Json;
using Highlighter.Web.Models;

namespace Highlighter.Web.Services;

/// <summary>The unauthenticated /api/auth surface. Deliberately independent of
/// AuthSession (which depends on this client) — it never attaches a bearer
/// except for logout, where the caller passes the token explicitly.</summary>
public sealed class AuthApiClient(IHttpClientFactory factory)
{
    public const string HttpClientName = "api";

    public Task<AuthSessionResponse> SignupAsync(string email, string password, CancellationToken ct = default) =>
        PostAsync("/api/auth/signup", new SignupRequest(email, password), ct);

    public Task<AuthSessionResponse> LoginAsync(string email, string password, CancellationToken ct = default) =>
        PostAsync("/api/auth/login", new LoginRequest(email, password), ct);

    public Task<AuthSessionResponse> RefreshAsync(string refreshToken, CancellationToken ct = default) =>
        PostAsync("/api/auth/refresh", new RefreshRequest(refreshToken), ct);

    public async Task LogoutAsync(string accessToken, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            await factory.CreateClient(HttpClientName).SendAsync(request, ct);
        }
        catch (HttpRequestException)
        {
            // Best-effort — local sign-out proceeds regardless.
        }
    }

    private async Task<AuthSessionResponse> PostAsync<TBody>(string path, TBody body, CancellationToken ct)
    {
        var response = await factory.CreateClient(HttpClientName).PostAsJsonAsync(path, body, ct);
        if (!response.IsSuccessStatusCode) throw await ApiException.FromResponseAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<AuthSessionResponse>(ct))!;
    }
}
