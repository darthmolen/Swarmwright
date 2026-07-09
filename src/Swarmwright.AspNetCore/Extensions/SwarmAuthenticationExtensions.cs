using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Identity.Web;
using Swarmwright.Configuration;

namespace Swarmwright.Extensions;

/// <summary>
/// Extension methods for wiring Entra ID (Azure AD) JWT bearer authentication and the
/// SPA configuration that backs the Swarm REST API and the React admin login.
/// </summary>
public static class SwarmAuthenticationExtensions
{
    /// <summary>
    /// The configuration section <see cref="AddSwarmAzureAdAuthentication"/> reads the
    /// Microsoft.Identity.Web settings (Instance, TenantId, ClientId, Audience) from.
    /// </summary>
    public const string AzureAdSectionName = "AzureAd";

    /// <summary>
    /// Registers Entra ID JWT bearer validation for the Swarm REST API using
    /// Microsoft.Identity.Web. Tokens are validated against the <see cref="AzureAdSectionName"/>
    /// configuration section under the standard <c>Bearer</c> scheme, which the
    /// <see cref="SwarmAuthorizationExtensions.SwarmReadPolicy"/> and
    /// <see cref="SwarmAuthorizationExtensions.SwarmWritePolicy"/> policies bind to.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">
    /// The application configuration; must contain the <see cref="AzureAdSectionName"/> section.
    /// </param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddSwarmAzureAdAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddMicrosoftIdentityWebApi(
                configuration,
                configSectionName: AzureAdSectionName,
                jwtBearerScheme: JwtBearerDefaults.AuthenticationScheme);

        return services;
    }

    /// <summary>
    /// Binds the <see cref="SpaConfiguration"/> options from the
    /// <see cref="SpaConfiguration.SectionName"/> configuration section so the admin SPA can
    /// fetch them from <c>GET /api/spa-config</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">
    /// The application configuration; the <see cref="SpaConfiguration.SectionName"/> section
    /// supplies the SPA's client/tenant IDs and requested scopes.
    /// </param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddSwarmSpaConfiguration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<SpaConfiguration>(configuration.GetSection(SpaConfiguration.SectionName));

        return services;
    }
}
