using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;
using Swarmwright.McpServer.Authentication;
using Swarmwright.McpServer.Authorization;
using Swarmwright.McpServer.Configuration;
using Swarmwright.McpServer.Tools;

namespace Swarmwright.McpServer.Extensions;

/// <summary>
/// Extension methods for registering and mapping the Swarm MCP server.
/// </summary>
public static partial class SwarmMcpServerExtensions
{
    /// <summary>
    /// Registers the Swarm MCP server, its authentication scheme (per
    /// <see cref="SwarmMcpOptions.AuthMode"/>), and its authorization policies.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration containing the <c>SwarmMcp</c> and <c>SwarmMcpAuthorization</c> sections.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddSwarmMcpServer(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<SwarmMcpOptions>(configuration.GetSection(SwarmMcpOptions.SectionName));
        services.Configure<SwarmMcpAuthorizationOptions>(
            configuration.GetSection(SwarmMcpAuthorizationOptions.SectionName));

        var mcpOptions = configuration
            .GetSection(SwarmMcpOptions.SectionName)
            .Get<SwarmMcpOptions>() ?? new SwarmMcpOptions();

        var authzOptions = configuration
            .GetSection(SwarmMcpAuthorizationOptions.SectionName)
            .Get<SwarmMcpAuthorizationOptions>() ?? new SwarmMcpAuthorizationOptions();

        RegisterAuthentication(services, configuration, mcpOptions);
        RegisterAuthorization(services, authzOptions, mcpOptions.AuthMode);

        services.AddSingleton<IAuthorizationHandler, SwarmMcpAuthorizationHandler>();

        services
            .AddMcpServer()
            .WithHttpTransport()
            .WithTools<SwarmMcpTools>();

        return services;
    }

    /// <summary>
    /// Maps the Swarm MCP endpoint at the configured path with authorization applied.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The endpoint route builder for chaining.</returns>
    public static IEndpointRouteBuilder MapSwarmMcpServer(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var options = endpoints.ServiceProvider
            .GetRequiredService<IOptions<SwarmMcpOptions>>().Value;

        if (options.AuthMode == SwarmMcpAuthMode.None)
        {
            var logger = endpoints.ServiceProvider
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("Swarmwright.McpServer");
            LogNoAuthWarning(logger);
        }

        endpoints
            .MapMcp(options.EndpointPath)
            .RequireAuthorization(SwarmMcpAuthorizationOptions.ReadPolicyName);

        return endpoints;
    }

    /// <summary>
    /// Registers the Swarm MCP authentication scheme for the chosen mode without
    /// changing the host's default scheme — other endpoints keep whatever auth
    /// they had before this call. The Swarm MCP policies below bind themselves
    /// to the scheme registered here via <see cref="SchemeForMode"/>.
    /// </summary>
    private static void RegisterAuthentication(
        IServiceCollection services,
        IConfiguration configuration,
        SwarmMcpOptions mcpOptions)
    {
        switch (mcpOptions.AuthMode)
        {
            case SwarmMcpAuthMode.AzureAD:
                // Only add the JwtBearer handler if the host hasn't already wired
                // up Microsoft.Identity.Web for another endpoint. Either way the
                // scheme name is "Bearer" and the MCP policies attach to it.
                services
                    .AddAuthentication()
                    .AddMicrosoftIdentityWebApi(
                        configuration,
                        jwtBearerScheme: JwtBearerDefaults.AuthenticationScheme);
                break;

            case SwarmMcpAuthMode.ApiKey:
                services
                    .AddAuthentication()
                    .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(
                        ApiKeyAuthenticationOptions.SchemeName,
                        schemeOptions =>
                        {
                            schemeOptions.ExpectedApiKey = mcpOptions.ApiKey;
                        });
                break;

            case SwarmMcpAuthMode.None:
            default:
                services
                    .AddAuthentication()
                    .AddScheme<NoAuthenticationOptions, NoAuthenticationHandler>(
                        NoAuthenticationOptions.SchemeName,
                        _ => { });
                break;
        }
    }

    /// <summary>
    /// Registers the Swarm MCP Read and Write policies, each pinned to the
    /// authentication scheme appropriate for <paramref name="authMode"/>.
    /// Pinning the scheme on the policy means the policy always authenticates
    /// against the Swarm MCP scheme regardless of the host's default.
    /// </summary>
    private static void RegisterAuthorization(
        IServiceCollection services,
        SwarmMcpAuthorizationOptions authzOptions,
        SwarmMcpAuthMode authMode)
    {
        var schemeName = SchemeForMode(authMode);

        services.AddAuthorizationBuilder()
            .AddPolicy(
                SwarmMcpAuthorizationOptions.ReadPolicyName,
                policy =>
                {
                    policy.AuthenticationSchemes.Add(schemeName);
                    policy.Requirements.Add(
                        new SwarmMcpAuthorizationRequirement(
                            authzOptions.ReadRole,
                            authzOptions.ReadScope));
                })
            .AddPolicy(
                SwarmMcpAuthorizationOptions.WritePolicyName,
                policy =>
                {
                    policy.AuthenticationSchemes.Add(schemeName);
                    policy.Requirements.Add(
                        new SwarmMcpAuthorizationRequirement(
                            authzOptions.WriteRole,
                            authzOptions.WriteScope));
                });
    }

    private static string SchemeForMode(SwarmMcpAuthMode authMode) => authMode switch
    {
        SwarmMcpAuthMode.AzureAD => JwtBearerDefaults.AuthenticationScheme,
        SwarmMcpAuthMode.ApiKey => ApiKeyAuthenticationOptions.SchemeName,
        SwarmMcpAuthMode.None => NoAuthenticationOptions.SchemeName,
        _ => NoAuthenticationOptions.SchemeName,
    };

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "Swarm MCP server is configured with AuthMode=None. All callers are granted Read+Write. Use only in development environments.")]
    private static partial void LogNoAuthWarning(ILogger logger);
}
