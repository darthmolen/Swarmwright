using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Swarmwright.MicrosoftAgentFramework.Extensions;

namespace Swarmwright.Extensions;

/// <summary>
/// Extension methods for configuring the Swarm orchestration pipeline.
/// </summary>
public static class SwarmBuilderExtensions
{
    /// <summary>
    /// Registers the shared <c>IChatClient</c> used by swarm orchestration. Delegates
    /// to <see cref="ServiceCollectionExtensions.AddSwarmwrightAzureOpenAI(IServiceCollection, IConfiguration)"/>,
    /// which reads the top-level <c>AzureOpenAI</c> configuration section and is idempotent —
    /// safe to call alongside an explicit consumer-side <c>AddSwarmwrightAzureOpenAI</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddSwarmOrchestration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSwarmwrightAzureOpenAI(configuration);

        return services;
    }
}
