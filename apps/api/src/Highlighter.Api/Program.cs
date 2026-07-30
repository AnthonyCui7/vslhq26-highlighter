using Highlighter.Api;
using Highlighter.Api.Endpoints;
using Highlighter.Api.Infrastructure;
using Highlighter.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Events;

EnvLoader.Load();

var builder = WebApplication.CreateBuilder(args);

var apiOptions = builder.Configuration.GetSection("Api").Get<ApiOptions>() ?? new();
var pipelineOptions = builder.Configuration.GetSection("Pipeline").Get<PipelineOptions>() ?? new();
var layout = new RepoLayout(pipelineOptions);

Directory.CreateDirectory(layout.ApiLogDir);
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(Path.Combine(layout.ApiLogDir, "api-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}")
    .CreateLogger();
builder.Host.UseSerilog();

builder.Services.AddSingleton(apiOptions);
builder.Services.AddSingleton(pipelineOptions);
builder.Services.AddSingleton(layout);
builder.Services.AddHttpClient(SupabaseDb.HttpClientName);
builder.Services.AddSingleton(provider => SupabaseDb.FromEnv(
    provider.GetRequiredService<IHttpClientFactory>(),
    provider.GetRequiredService<ILogger<SupabaseDb>>()));
builder.Services.AddHttpClient(SupabaseStorage.HttpClientName);
builder.Services.AddHttpClient(EditorExportService.HttpClientName,
    client => client.Timeout = TimeSpan.FromMinutes(10));
builder.Services.AddSingleton(provider => SupabaseStorage.FromEnv(
    provider.GetRequiredService<IHttpClientFactory>(),
    provider.GetRequiredService<ILogger<SupabaseStorage>>()));
builder.Services.AddSingleton<EditorRenderer>();
builder.Services.AddSingleton<EditorExportService>();
builder.Services.AddSingleton<PipelineJobService>();
builder.Services.AddSingleton<MediaCleanupScheduler>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<MediaCleanupScheduler>());

// Auth: /api/auth proxies GoTrue; everything else validates the Supabase user
// JWT (ES256 against the project JWKS) when Api:RequireAuth is on.
builder.Services.AddHttpClient(SupabaseAuth.HttpClientName);
builder.Services.AddSingleton(provider => SupabaseAuth.FromEnv(
    provider.GetRequiredService<IHttpClientFactory>(),
    provider.GetRequiredService<ILogger<SupabaseAuth>>()));
builder.Services.AddSingleton(provider => SupabaseJwks.FromEnv(
    provider.GetRequiredService<IHttpClientFactory>(),
    provider.GetRequiredService<ILogger<SupabaseJwks>>()));
var supabaseUrl = Environment.GetEnvironmentVariable("SUPABASE_URL")?.TrimEnd('/');
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IServiceProvider>((options, services) =>
    {
        options.MapInboundClaims = false; // keep "sub" as "sub"
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = $"{supabaseUrl}/auth/v1",
            ValidAudience = "authenticated",
            ValidAlgorithms = ["ES256"],
            IssuerSigningKeyResolver = (_, _, kid, _) =>
                services.GetRequiredService<SupabaseJwks>().GetKeys(kid),
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins(apiOptions.CorsOrigins).AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

app.UseSerilogRequestLogging();
app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    var (status, title) = error switch
    {
        PostgrestException => (StatusCodes.Status502BadGateway, "Supabase request failed"),
        WorkerUnavailableException => (StatusCodes.Status502BadGateway, "Pipeline worker unavailable"),
        JobConflictException => (StatusCodes.Status409Conflict, "Conflicting job"),
        _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred"),
    };
    context.Response.StatusCode = status;
    await context.Response.WriteAsJsonAsync(
        new { type = "about:blank", title, status, detail = error?.Message },
        options: null, contentType: "application/problem+json");
}));
app.UseStatusCodePages();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<ApiKeyMiddleware>();

app.MapOpenApi();
app.MapScalarApiReference();
app.MapHealthEndpoints();
app.MapAuthEndpoints();
IEndpointConventionBuilder[] gated =
[
    app.MapProjectEndpoints(),
    app.MapProjectActionEndpoints(),
    app.MapEditorEndpoints(),
    app.MapJobEndpoints(),
    app.MapAdminEndpoints(),
];
if (apiOptions.RequireAuth)
    foreach (var endpoints in gated) endpoints.RequireAuthorization();

app.Run();

public partial class Program;
