using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Highlighter.Api.Infrastructure;
using Highlighter.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Highlighter.Api.Tests;

// ---- SupabaseAuth (GoTrue client) unit tests ----

public class SupabaseAuthTests
{
    private const string SessionJson =
        """
        {"access_token":"at-1","token_type":"bearer","expires_in":3600,
         "expires_at":1900000000,"refresh_token":"rt-1",
         "user":{"id":"6f9619ff-8b86-d011-b42d-00cf4fc964ff","email":"a@b.co"}}
        """;

    private static (SupabaseAuth Auth, RecordingHandler Handler) Create()
    {
        var handler = new RecordingHandler();
        return (new SupabaseAuth(new HttpClient(handler), NullLogger<SupabaseAuth>.Instance,
            "https://stub.supabase.local", "anon-key", "service-key"), handler);
    }

    [Fact]
    public async Task Signup_AdminCreatesConfirmedUser_ThenLogsIn()
    {
        var (auth, handler) = Create();
        handler.Enqueue(HttpStatusCode.OK, """{"id":"u1","email":"a@b.co"}""");
        handler.Enqueue(HttpStatusCode.OK, SessionJson);

        var session = await auth.SignupAsync("a@b.co", "hunter22", CancellationToken.None);

        var create = handler.Requests[0];
        Assert.Equal("https://stub.supabase.local/auth/v1/admin/users", create.Url);
        Assert.Equal("service-key", create.Headers["apikey"]);
        Assert.Equal("Bearer service-key", create.Headers["Authorization"]);
        Assert.Contains("\"email_confirm\":true", create.Body);

        var login = handler.Requests[1];
        Assert.Equal("https://stub.supabase.local/auth/v1/token?grant_type=password", login.Url);
        Assert.Equal("anon-key", login.Headers["apikey"]);
        Assert.False(login.Headers.ContainsKey("Authorization"));

        Assert.Equal("at-1", session.AccessToken);
        Assert.Equal("rt-1", session.RefreshToken);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1900000000), session.ExpiresAt);
        Assert.Equal("a@b.co", session.User.Email);
    }

    [Fact]
    public async Task Signup_ExistingEmail_Maps409()
    {
        var (auth, handler) = Create();
        handler.Enqueue(HttpStatusCode.UnprocessableEntity,
            """{"code":422,"error_code":"email_exists","msg":"Email address already registered"}""");

        var error = await Assert.ThrowsAsync<AuthFlowException>(
            () => auth.SignupAsync("a@b.co", "hunter22", CancellationToken.None));
        Assert.Equal(409, error.Status);
    }

    [Fact]
    public async Task Login_BadCredentials_Maps401()
    {
        var (auth, handler) = Create();
        handler.Enqueue(HttpStatusCode.BadRequest,
            """{"error":"invalid_grant","error_description":"Invalid login credentials"}""");

        var error = await Assert.ThrowsAsync<AuthFlowException>(
            () => auth.LoginAsync("a@b.co", "wrong", CancellationToken.None));
        Assert.Equal(401, error.Status);
    }

    [Fact]
    public async Task Refresh_UsesRefreshGrant_AndReturnsRotatedToken()
    {
        var (auth, handler) = Create();
        handler.Enqueue(HttpStatusCode.OK, SessionJson.Replace("rt-1", "rt-2"));

        var session = await auth.RefreshAsync("rt-1", CancellationToken.None);

        Assert.Equal("https://stub.supabase.local/auth/v1/token?grant_type=refresh_token",
            handler.Requests[0].Url);
        Assert.Contains("rt-1", handler.Requests[0].Body);
        Assert.Equal("rt-2", session.RefreshToken);
    }

    [Fact]
    public async Task Refresh_DeadToken_Maps401()
    {
        var (auth, handler) = Create();
        handler.Enqueue(HttpStatusCode.BadRequest,
            """{"error_code":"refresh_token_already_used","msg":"Invalid Refresh Token"}""");

        var error = await Assert.ThrowsAsync<AuthFlowException>(
            () => auth.RefreshAsync("rt-old", CancellationToken.None));
        Assert.Equal(401, error.Status);
    }

    [Fact]
    public async Task Logout_SendsBearerAndSwallowsFailures()
    {
        var (auth, handler) = Create();
        handler.Enqueue(HttpStatusCode.Unauthorized, """{"msg":"invalid token"}""");

        await auth.LogoutAsync("at-dead", CancellationToken.None);

        Assert.Equal("https://stub.supabase.local/auth/v1/logout", handler.Requests[0].Url);
        Assert.Equal("Bearer at-dead", handler.Requests[0].Headers["Authorization"]);
    }
}

// ---- JWT gate + strict ownership integration tests ----

public class AuthGateTests
{
    private static readonly ECDsa Ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private static readonly ECDsaSecurityKey SigningKey = new(Ecdsa) { KeyId = "test-key" };

    private static WebApplicationFactory<Program> CreateFactory(RecordingHandler handler) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Api:RequireAuth", "true");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<SupabaseDb>();
                services.AddSingleton(new SupabaseDb(
                    new HttpClient(handler), NullLogger<SupabaseDb>.Instance,
                    "https://stub.supabase.local", "stub-key"));
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme,
                    options =>
                    {
                        options.MapInboundClaims = false;
                        options.TokenValidationParameters = new TokenValidationParameters
                        {
                            ValidIssuer = "test-issuer",
                            ValidAudience = "authenticated",
                            IssuerSigningKey = SigningKey,
                        };
                    });
            });
        });

    private static string Mint(Guid userId, string issuer = "test-issuer",
        string audience = "authenticated", TimeSpan? lifetime = null) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Claims = new Dictionary<string, object> { ["sub"] = userId.ToString() },
            Expires = DateTime.UtcNow.Add(lifetime ?? TimeSpan.FromHours(1)),
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.EcdsaSha256),
        });

    [Fact]
    public async Task NoToken_Gets401_EverywhereExceptOpenSurfaces()
    {
        using var factory = CreateFactory(new RecordingHandler());
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/projects")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/jobs")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsync("/api/admin/cleanup", null)).StatusCode);

        // healthz and the auth surface stay open (signup fails validation, not auth).
        Assert.NotEqual(HttpStatusCode.Unauthorized, (await client.GetAsync("/healthz")).StatusCode);
        var signup = await client.PostAsJsonAsync("/api/auth/signup", new { email = "", password = "" });
        Assert.Equal(HttpStatusCode.BadRequest, signup.StatusCode);
    }

    [Fact]
    public async Task ExpiredOrWrongAudienceToken_Gets401()
    {
        using var factory = CreateFactory(new RecordingHandler());
        using var client = factory.CreateClient();
        var uid = Guid.NewGuid();

        client.DefaultRequestHeaders.Authorization =
            new("Bearer", Mint(uid, lifetime: TimeSpan.FromMinutes(-10)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/projects")).StatusCode);

        client.DefaultRequestHeaders.Authorization = new("Bearer", Mint(uid, audience: "other"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/projects")).StatusCode);
    }

    [Fact]
    public async Task ValidToken_ScopesListToOwnProjects()
    {
        var handler = new RecordingHandler();
        using var factory = CreateFactory(handler);
        using var client = factory.CreateClient();
        var uid = Guid.NewGuid();
        client.DefaultRequestHeaders.Authorization = new("Bearer", Mint(uid));

        var response = await client.GetAsync("/api/projects");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains($"user_id=eq.{uid}", handler.Requests[0].Url);
    }

    [Fact]
    public async Task ForeignProject_ReadsAsNotFound()
    {
        var handler = new RecordingHandler(); // default response: empty array = not visible
        using var factory = CreateFactory(handler);
        using var client = factory.CreateClient();
        var uid = Guid.NewGuid();
        client.DefaultRequestHeaders.Authorization = new("Bearer", Mint(uid));

        var project = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/projects/{project}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/projects/{project}/clips")).StatusCode);
        Assert.Contains($"user_id=eq.{uid}", handler.Requests[0].Url);
    }

    [Fact]
    public async Task CreateProject_StampsOwner()
    {
        var handler = new RecordingHandler();
        var project = Guid.NewGuid();
        handler.Enqueue(HttpStatusCode.Created,
            $$"""
            [{"id":"{{project}}","name":"n","source_type":"video","source_url":"u",
              "status":"created","metadata":{} }]
            """);
        // Worker spawn will fail (no worker on the test host) -> guarded fixup PATCH.
        using var factory = CreateFactory(handler);
        using var client = factory.CreateClient();
        var uid = Guid.NewGuid();
        client.DefaultRequestHeaders.Authorization = new("Bearer", Mint(uid));

        await client.PostAsJsonAsync("/api/projects", new
        {
            sourceUrl = "https://www.youtube.com/watch?v=abc123",
            pipeline = "short",
        });

        using var body = JsonDocument.Parse(handler.Requests[0].Body!);
        Assert.Equal(uid.ToString(), body.RootElement.GetProperty("user_id").GetString());
    }
}

// ---- SupabaseDb scoping query strings ----

public class SupabaseDbScopingTests
{
    [Fact]
    public async Task ProjectQueries_CarryUserFilterOnlyWhenScoped()
    {
        var (db, handler) = TestDb.Create();
        var uid = Guid.NewGuid();
        var id = Guid.NewGuid();

        await db.ListProjectsAsync(50, uid, CancellationToken.None);
        await db.ListProjectsAsync(50, null, CancellationToken.None);
        await db.GetProjectAsync(id, "id", uid, CancellationToken.None);
        await db.DeleteProjectAsync(id, uid, CancellationToken.None);
        await db.PatchProjectGuardedAsync(id, ["created"],
            new System.Text.Json.Nodes.JsonObject { ["status"] = "stopping" }, uid, CancellationToken.None);

        Assert.Contains($"&user_id=eq.{uid}", handler.Requests[0].Url);
        Assert.DoesNotContain("user_id", handler.Requests[1].Url);
        Assert.Contains($"&user_id=eq.{uid}", handler.Requests[2].Url);
        Assert.Contains($"&user_id=eq.{uid}", handler.Requests[3].Url);
        Assert.Contains($"&user_id=eq.{uid}", handler.Requests[4].Url);
    }
}
