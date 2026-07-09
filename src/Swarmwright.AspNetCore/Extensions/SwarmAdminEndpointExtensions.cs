using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Swarmwright.Configuration;

namespace Swarmwright.Extensions;

/// <summary>
/// Extension methods for mapping the Swarm admin SPA support endpoints.
/// </summary>
public static class SwarmAdminEndpointExtensions
{
    /// <summary>
    /// Maps <c>GET /api/spa-config</c>, the anonymous endpoint the React admin SPA fetches at
    /// startup to configure MSAL.js authentication. Returning the configuration from the host
    /// (rather than baking it into the bundle) lets the same SPA build target any environment —
    /// only the host's <see cref="SpaConfiguration"/> changes.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The endpoint route builder for chaining.</returns>
    public static IEndpointRouteBuilder MapSwarmSpaConfig(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(
            "/api/spa-config",
            static ([FromServices] IOptions<SpaConfiguration> spaConfig) =>
            {
                var config = spaConfig.Value;
                return Results.Ok(new
                {
                    clientId = config.ClientId,
                    tenantId = config.TenantId,
                    defaultScope = config.DefaultScope,
                    requiredPermissions = config.RequiredPermissions,
                });
            })
            .WithName("GetSpaConfiguration")
            .WithTags("Configuration")
            .AllowAnonymous();

        return endpoints;
    }
}
