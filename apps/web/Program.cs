using Highlighter.Web.Components;
using Highlighter.Web.Models;
using Highlighter.Web.Services;

// The studio agent's model credentials live in the monorepo root .env, the
// same file the pipeline uses.
Highlighter.Pipeline.Config.LoadEnv();

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddScoped<StudioState>();
builder.Services.AddScoped<IStudioBackend, SampleStudioBackend>();
builder.Services.AddScoped<StudioAgentService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
