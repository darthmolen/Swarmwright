using System.Globalization;
using Serilog;
using Swarmwright.Extensions;
using Swarmwright.MicrosoftAgentFramework.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Structured logging via Serilog with a console sink. ReadFrom.Configuration lets the optional
// "Serilog" appsettings section tune levels; WriteTo.Console guarantees the console sink is wired.
builder.Services.AddSerilog((services, loggerConfiguration) => loggerConfiguration
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture));

// Layer the sidecar appsettings.swarm-*.json shipped by the installed template packages
// (deep-research, azure-solutions-agent, microsoft-deep-research) and copy their template
// content next to the app so the TemplateLoader can find it.
builder.Configuration.AddSwarmTemplatePackages();

// LLM backend. When OpenAI:Endpoint is configured (e.g. a local vLLM or Ollama server) the swarm
// talks to that OpenAI-compatible endpoint; otherwise it falls back to Azure OpenAI via the
// top-level AzureOpenAI configuration section. The OpenAI-compatible registration runs first so
// its IChatClient wins the idempotent TryAddSingleton inside the swarm registration.
var openAiEndpoint = builder.Configuration["OpenAI:Endpoint"];
if (!string.IsNullOrWhiteSpace(openAiEndpoint))
{
    builder.Services.AddSwarmwrightOpenAI(
        openAiEndpoint,
        builder.Configuration["OpenAI:Model"] ?? "Qwen/Qwen2.5-7B-Instruct",
        builder.Configuration["OpenAI:ApiKey"]);
    builder.Services.AddSwarmDomain(builder.Configuration, builder.Environment);
    builder.Services.AddSwarmHttpServices();
    builder.Services.AddSwarmAuthorization();
}
else
{
    builder.Services.AddAISwarm(builder.Configuration, builder.Environment);
}

// Entra ID (Azure AD) authentication. When the AzureAd section is populated — e.g. via
// dotnet user-secrets, see scripts/set-user-secrets.ps1 — the swarm REST API validates bearer
// tokens and the admin SPA is handed real MSAL config from /api/spa-config. Without that config
// the host stays anonymous so a bare `dotnet run` still works for local spikes.
var authEnabled = !string.IsNullOrWhiteSpace(builder.Configuration["AzureAd:ClientId"])
    && !string.IsNullOrWhiteSpace(builder.Configuration["AzureAd:TenantId"]);
if (authEnabled)
{
    builder.Services.AddSwarmAzureAdAuthentication(builder.Configuration);
    builder.Services.AddSwarmSpaConfiguration(builder.Configuration);
}

builder.Services.AddHttpContextAccessor();

var app = builder.Build();

// Emit a concise Serilog line per HTTP request.
app.UseSerilogRequestLogging();

// Serve the embedded admin SPA (static assets) when present, then the swarm REST + SSE API.
app.UseStaticFiles();

if (authEnabled)
{
    app.UseAuthentication();
    app.UseAuthorization();

    // Anonymous config endpoint the SPA fetches before login to configure MSAL.
    app.MapSwarmSpaConfig();
}

app.MapSwarmEndpoints(useSwarmPolicies: authEnabled);
app.MapFallbackToFile("index.html");

app.Run();

/// <summary>
/// Explicit entry-point marker so <c>WebApplicationFactory&lt;Program&gt;</c> in the integration-test
/// project can boot this host in-process.
/// </summary>
public partial class Program;
