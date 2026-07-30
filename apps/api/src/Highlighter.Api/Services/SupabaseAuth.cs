using System.Text;
using System.Text.Json.Nodes;
using Highlighter.Api.Contracts;
using Highlighter.Api.Infrastructure;

namespace Highlighter.Api.Services;

/// <summary>GoTrue (Supabase Auth) client. Signup uses the admin API with the
/// service-role key so accounts are created pre-confirmed (the hosted project
/// has mailer_autoconfirm off — a plain signup would dead-end on an email
/// link). Login/refresh use the anon key. Keys never leave this process.</summary>
public sealed class SupabaseAuth
{
    public const string HttpClientName = "supabase-auth";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private readonly HttpClient _http;
    private readonly ILogger _log;
    private readonly string? _baseUrl;
    private readonly string? _anonKey;
    private readonly string? _serviceKey;

    public SupabaseAuth(HttpClient http, ILogger<SupabaseAuth> log,
        string? baseUrl, string? anonKey, string? serviceKey)
    {
        _http = http;
        _log = log;
        _baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl.TrimEnd('/');
        _anonKey = string.IsNullOrWhiteSpace(anonKey) ? null : anonKey;
        _serviceKey = string.IsNullOrWhiteSpace(serviceKey) ? null : serviceKey;
    }

    public static SupabaseAuth FromEnv(IHttpClientFactory factory, ILogger<SupabaseAuth> log) =>
        new(factory.CreateClient(HttpClientName), log,
            Environment.GetEnvironmentVariable("SUPABASE_URL"),
            Environment.GetEnvironmentVariable("SUPABASE_ANON_KEY"),
            Environment.GetEnvironmentVariable("SUPABASE_SERVICE_ROLE_KEY")
                ?? Environment.GetEnvironmentVariable("SUPABASE_SECRET_KEY"));

    public bool IsConfigured => _baseUrl is not null && _anonKey is not null && _serviceKey is not null;

    public string JwksUrl => $"{_baseUrl}/auth/v1/.well-known/jwks.json";

    public async Task<AuthSessionDto> SignupAsync(string email, string password, CancellationToken ct)
    {
        // Admin-create with email_confirm so the very next password grant works.
        var response = await SendAsync(HttpMethod.Post, "/auth/v1/admin/users", _serviceKey!,
            bearer: _serviceKey, new JsonObject
            {
                ["email"] = email,
                ["password"] = password,
                ["email_confirm"] = true,
            }, ct);
        if (!response.Success)
        {
            if (response.Status == 422 || response.ErrorCode is "email_exists" or "user_already_exists"
                || response.Message.Contains("already", StringComparison.OrdinalIgnoreCase))
                throw new AuthFlowException(409, "Account already exists",
                    "An account with that email already exists — try signing in.");
            if (response.ErrorCode is "weak_password"
                || response.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
                throw new AuthFlowException(400, "Weak password", response.Message);
            throw Unexpected("signup", response);
        }
        return await LoginAsync(email, password, ct);
    }

    public async Task<AuthSessionDto> LoginAsync(string email, string password, CancellationToken ct)
    {
        var response = await SendAsync(HttpMethod.Post, "/auth/v1/token?grant_type=password",
            _anonKey!, bearer: null,
            new JsonObject { ["email"] = email, ["password"] = password }, ct);
        if (!response.Success)
        {
            if (response.Status is 400 or 401)
                throw new AuthFlowException(401, "Sign-in failed", "Wrong email or password.");
            throw Unexpected("login", response);
        }
        return ParseSession(response.Body!);
    }

    public async Task<AuthSessionDto> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        var response = await SendAsync(HttpMethod.Post, "/auth/v1/token?grant_type=refresh_token",
            _anonKey!, bearer: null, new JsonObject { ["refresh_token"] = refreshToken }, ct);
        if (!response.Success)
        {
            if (response.Status is 400 or 401)
                throw new AuthFlowException(401, "Session expired", "Sign in again.");
            throw Unexpected("refresh", response);
        }
        return ParseSession(response.Body!);
    }

    public async Task LogoutAsync(string accessToken, CancellationToken ct)
    {
        try
        {
            await SendAsync(HttpMethod.Post, "/auth/v1/logout", _anonKey!, bearer: accessToken, null, ct);
        }
        catch (AuthFlowException)
        {
            // Best-effort: an already-dead session is a successful logout.
        }
    }

    private static AuthFlowException Unexpected(string operation, GoTrueResponse response) =>
        new(502, "Supabase auth request failed",
            $"{operation} failed ({response.Status}): {response.Message}");

    internal static AuthSessionDto ParseSession(JsonObject body)
    {
        var user = body["user"] as JsonObject
            ?? throw new AuthFlowException(502, "Supabase auth request failed",
                "GoTrue returned a session without a user object");
        var expiresAt = body["expires_at"]?.GetValue<long>() is { } unix
            ? DateTimeOffset.FromUnixTimeSeconds(unix)
            : DateTimeOffset.UtcNow.AddSeconds(body["expires_in"]?.GetValue<double>() ?? 3600);
        return new AuthSessionDto(
            body["access_token"]!.GetValue<string>(),
            body["refresh_token"]!.GetValue<string>(),
            expiresAt,
            new AuthUserDto(
                Guid.Parse(user["id"]!.GetValue<string>()),
                user["email"]?.GetValue<string>() ?? ""));
    }

    private sealed record GoTrueResponse(int Status, bool Success, JsonObject? Body,
        string Message, string? ErrorCode);

    private async Task<GoTrueResponse> SendAsync(HttpMethod method, string path,
        string apiKey, string? bearer, JsonObject? body, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new AuthFlowException(502, "Supabase auth is not configured",
                "Set SUPABASE_URL, SUPABASE_ANON_KEY and SUPABASE_SERVICE_ROLE_KEY in the repo-root .env");

        using var request = new HttpRequestMessage(method, _baseUrl + path);
        request.Headers.TryAddWithoutValidation("apikey", apiKey);
        if (bearer is not null)
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {bearer}");
        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AuthFlowException(502, "Supabase auth request failed", $"{path} timed out");
        }
        catch (HttpRequestException exception)
        {
            throw new AuthFlowException(502, "Supabase auth request failed", exception.Message);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(CancellationToken.None);
            JsonObject? parsed = null;
            try
            {
                parsed = JsonNode.Parse(text) as JsonObject;
            }
            catch (System.Text.Json.JsonException)
            {
            }

            if (!response.IsSuccessStatusCode)
                _log.LogWarning("GoTrue {Path} -> {Status}: {Body}", path, (int)response.StatusCode,
                    text.Length > 300 ? text[..300] : text);

            // GoTrue error bodies vary by version: {code, error_code, msg} vs
            // {error, error_description} vs bare {msg,message}.
            var message = parsed?["msg"]?.GetValue<string>()
                ?? parsed?["error_description"]?.GetValue<string>()
                ?? parsed?["message"]?.GetValue<string>()
                ?? parsed?["error"]?.GetValue<string>()
                ?? (text.Length > 200 ? text[..200] : text);
            var errorCode = parsed?["error_code"]?.GetValue<string>()
                ?? parsed?["error"]?.GetValue<string>();
            return new GoTrueResponse((int)response.StatusCode, response.IsSuccessStatusCode,
                parsed, message, errorCode);
        }
    }
}
