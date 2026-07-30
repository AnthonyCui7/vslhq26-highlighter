using System.Net;
using System.Text.Json;
using Highlighter.Api.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Highlighter.Api.Tests;

/// <summary>WebApplicationFactory smoke tests. Supabase is replaced with a stub
/// handler and hosted services are removed, so nothing touches the network.</summary>
public class ApiSmokeTests
{
    private static WebApplicationFactory<Program> CreateFactory(
        RecordingHandler? handler = null, string apiKey = "")
    {
        handler ??= new RecordingHandler();
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Api:ApiKey", apiKey);
            // These tests cover pre-auth behavior; AuthGateTests covers the gate.
            builder.UseSetting("Api:RequireAuth", "false");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<SupabaseDb>();
                services.AddSingleton(new SupabaseDb(
                    new HttpClient(handler),
                    NullLogger<SupabaseDb>.Instance,
                    "https://stub.supabase.local",
                    "stub-key"));
            });
        });
    }

    [Fact]
    public async Task Healthz_ReturnsFullShape()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/healthz");

        // "ok" vs "degraded" depends on whether the worker dll is built on this
        // machine — assert the shape, not the verdict.
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.ServiceUnavailable);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.TryGetProperty("status", out _));
        Assert.True(doc.RootElement.TryGetProperty("env", out var env));
        Assert.True(env.TryGetProperty("uploadPostUser", out _));
        Assert.True(doc.RootElement.TryGetProperty("worker", out _));
        Assert.True(doc.RootElement.TryGetProperty("binaries", out _));
        Assert.True(doc.RootElement.GetProperty("supabase").TryGetProperty("configured", out var configured));
        Assert.True(configured.GetBoolean());
    }

    [Fact]
    public async Task OpenApiDocument_IsServed()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ApiKeyGate_RejectsMissingAndWrongKey()
    {
        using var factory = CreateFactory(apiKey: "sekret");
        using var client = factory.CreateClient();

        var missing = await client.GetAsync("/api/anything");
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);

        using var wrong = new HttpRequestMessage(HttpMethod.Get, "/api/anything");
        wrong.Headers.Add("X-Api-Key", "nope");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(wrong)).StatusCode);

        using var right = new HttpRequestMessage(HttpMethod.Get, "/api/anything");
        right.Headers.Add("X-Api-Key", "sekret");
        // Passes the gate; no such endpoint exists yet, so 404.
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(right)).StatusCode);
    }

    [Fact]
    public async Task ApiKeyGate_IsDisabledByDefault()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/anything")).StatusCode);
    }

    [Theory]
    [InlineData("""{"sourceUrl":"https://example.com/x","pipeline":"short"}""", "Unsupported source URL")]
    [InlineData("""{"sourceUrl":"https://youtu.be/abc","pipeline":"medium"}""", "pipeline must be one of")]
    [InlineData("""{"pipeline":"short"}""", "sourceUrl is required")]
    [InlineData("""{"sourceUrl":"https://youtu.be/abc","pipeline":"short","minClipScore":1.5}""", "minClipScore")]
    [InlineData("""{"sourceUrl":"https://youtu.be/abc","pipeline":"short","targetMinutes":"soonish"}""", "targetMinutes")]
    [InlineData("""{"sourceUrl":"https://youtu.be/abc","pipeline":"short","chunkSeconds":5}""", "chunkSeconds")]
    public async Task CreateProject_RejectsInvalidRequestsBeforeAnyInsert(string body, string expectedDetail)
    {
        var handler = new RecordingHandler();
        using var factory = CreateFactory(handler);
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/projects",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(expectedDetail, await response.Content.ReadAsStringAsync());
        Assert.Empty(handler.Requests); // validation failed before any Supabase call
    }

    [Fact]
    public async Task CancelUnknownProject_Returns404()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            $"/api/projects/{Guid.NewGuid()}/cancel", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UnknownJob_Returns404OnAllJobRoutes()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/jobs/job_missing")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/jobs/job_missing/logs")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/jobs/job_missing/logs/stream")).StatusCode);
    }

    [Fact]
    public async Task Healthz_IsExemptFromApiKeyGate()
    {
        using var factory = CreateFactory(apiKey: "sekret");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/healthz");

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
