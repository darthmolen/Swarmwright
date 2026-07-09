using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Swarmwright.Extensions;

/// <summary>
/// Top-level convenience entry point for registering the Swarmwright
/// Swarm stack on an <see cref="IServiceCollection"/>. Wraps the three
/// fine-grained helpers (<see cref="SwarmBuilderExtensions.AddSwarmOrchestration"/>,
/// <see cref="SwarmServiceExtensions.AddSwarmDomain"/>, and
/// <see cref="SwarmAuthorizationExtensions.AddSwarmAuthorization"/>) in a
/// single call so hosts don't need to know the correct call order or which
/// parameters each helper requires.
/// </summary>
/// <remarks>
/// The class name <c>IServiceCollectionExtensions</c> intentionally uses the
/// <c>I</c> prefix to mirror the extended type. StyleCop <c>SA1302</c> is
/// scoped to interfaces, so a static class with this name is allowed.
/// </remarks>
public static class IServiceCollectionExtensions
{
    /// <summary>
    /// Registers the full Swarmwright Swarm stack on an
    /// <see cref="IServiceCollection"/> in one call: orchestration
    /// (the Azure OpenAI chat client), domain services (database,
    /// repositories, hosted dispatcher, templates), and authorization
    /// policies (<c>Swarm.Read</c> / <c>Swarm.Write</c>).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">
    /// The application configuration. Must contain the top-level
    /// <c>AzureOpenAI</c> section (Endpoint, ApiKey, DeploymentName, and
    /// optionally NetworkTimeoutSeconds / MaxLlmRetries / MaxPollyRetries /
    /// RetryBaseDelaySeconds — see <c>AzureOpenAIOptions</c>) and optionally
    /// the <c>Swarm:Database</c> and <c>Swarm:TemplatesDirectory</c> sections.
    /// </param>
    /// <param name="hostingEnvironment">
    /// The hosting environment. Used to opt-in to automatic database migrations
    /// under the <c>Development</c> and <c>Testing</c> environment names.
    /// </param>
    /// <param name="discoverCustomToolProviders">
    /// When <c>true</c> (default), the framework scans loaded assemblies for concrete
    /// <c>ICustomToolProvider</c> implementations and registers each one automatically
    /// using the lifetime declared by its <c>[SwarmToolProvider]</c> attribute
    /// (Transient when unspecified). Set to <c>false</c> if the consumer wants to register
    /// providers manually — e.g. in tests, or when a provider needs conditional registration.
    /// Manual registrations always win: discovered types that are already registered are skipped.
    /// </param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddSwarmwright(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment hostingEnvironment,
        bool discoverCustomToolProviders = true)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(hostingEnvironment);

        services.AddSwarmOrchestration(configuration);
        services.AddSwarmDomain(configuration, hostingEnvironment);
        services.AddSwarmHttpServices();
        services.AddSwarmAuthorization();

        if (discoverCustomToolProviders)
        {
            services.DiscoverCustomToolProviders();
        }

        return services;
    }

    /// <summary>
    /// Registers the ASP.NET-scoped Swarm services that back the HTTP surface — currently the
    /// refinement chat handler. Call this (with <c>AddSwarmAuthorization</c>) when composing the
    /// swarm manually instead of via <see cref="AddSwarmwright"/> (e.g. a host wiring a non-Azure
    /// <c>IChatClient</c>).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddSwarmHttpServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<Swarmwright.Refinement.RefinementChatHandler>();
        return services;
    }
}
