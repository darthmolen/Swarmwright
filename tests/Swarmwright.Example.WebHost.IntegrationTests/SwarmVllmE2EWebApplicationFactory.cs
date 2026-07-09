using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Swarmwright.Example.WebHost.IntegrationTests;

/// <summary>
/// Boots the real Swarmwright example web host in-process for end-to-end tests, pointing the swarm
/// at a local OpenAI-compatible model server (vLLM/Ollama) supplied via environment variables. The
/// example host maps the swarm endpoints with <c>useSwarmPolicies: false</c>, so no authentication
/// stack is needed. The database is the InMemory provider (from the host's appsettings).
/// </summary>
public sealed class SwarmVllmE2EWebApplicationFactory : WebApplicationFactory<Program>
{
    /// <summary>The environment variable carrying the OpenAI-compatible endpoint (e.g. <c>http://localhost:8000/v1</c>).</summary>
    public const string EndpointVariable = "SWARMWRIGHT_VLLM_ENDPOINT";

    /// <summary>The environment variable carrying the served model name.</summary>
    public const string ModelVariable = "SWARMWRIGHT_VLLM_MODEL";

    /// <summary>The environment variable carrying the API key (optional for most local servers).</summary>
    public const string ApiKeyVariable = "SWARMWRIGHT_VLLM_API_KEY";

    /// <summary>
    /// Initializes a new instance of the <see cref="SwarmVllmE2EWebApplicationFactory"/> class,
    /// exporting the OpenAI-compatible settings as <c>OpenAI__*</c> environment variables. The host's
    /// <c>Program</c> reads <c>OpenAI:Endpoint</c> at service-registration time (before the
    /// WebApplicationFactory layers its own configuration), so the values must be visible to the
    /// default environment-variable configuration source that runs first. A placeholder endpoint is
    /// used when no model server is configured so the host always boots via the OpenAI-compatible
    /// branch — it is never contacted by tests that do not drive the model.
    /// </summary>
    public SwarmVllmE2EWebApplicationFactory()
    {
        var endpoint = Environment.GetEnvironmentVariable(EndpointVariable);
        Environment.SetEnvironmentVariable(
            "OpenAI__Endpoint",
            string.IsNullOrWhiteSpace(endpoint) ? "http://localhost:8000/v1" : endpoint);
        Environment.SetEnvironmentVariable(
            "OpenAI__Model",
            Environment.GetEnvironmentVariable(ModelVariable) ?? "Qwen/Qwen2.5-7B-Instruct");
        Environment.SetEnvironmentVariable(
            "OpenAI__ApiKey",
            Environment.GetEnvironmentVariable(ApiKeyVariable) ?? "swarmwright");
    }

    /// <inheritdoc/>
    protected override IHost CreateHost(IHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment(Environments.Development);
        builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));

        // Force the host onto its anonymous path regardless of any ambient AzureAd user-secrets on
        // the developer's machine (scripts/set-user-secrets.ps1 may have populated them). This keeps
        // the E2E swarm test deterministic — it exercises the orchestration, not authentication.
        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["AzureAd:ClientId"] = string.Empty,
                ["AzureAd:TenantId"] = string.Empty,
            }));

        return base.CreateHost(builder);
    }
}
