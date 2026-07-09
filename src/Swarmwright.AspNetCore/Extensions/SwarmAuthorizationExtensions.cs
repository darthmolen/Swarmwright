using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;

namespace Swarmwright.Extensions;

/// <summary>
/// Extension methods for registering Swarm authorization policies.
/// </summary>
public static class SwarmAuthorizationExtensions
{
    /// <summary>
    /// The policy name for swarm read operations (status, tasks, agents, events, templates, stream).
    /// </summary>
    public const string SwarmReadPolicy = "Swarm.Read";

    /// <summary>
    /// The policy name for swarm write operations (create, cancel, continue, skip).
    /// </summary>
    public const string SwarmWritePolicy = "Swarm.Write";

    /// <summary>
    /// The app role for machine-to-machine full access to swarm operations.
    /// </summary>
    public const string SwarmAdminRole = "Swarm.Admin";

    /// <summary>
    /// Adds Swarm authorization policies that check for Swarm.Read/Swarm.Write scopes
    /// or the Swarm.Admin app role (for machine-to-machine calls).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddSwarmAuthorization(this IServiceCollection services)
    {
        services.AddAuthorizationBuilder()
            .AddPolicy(SwarmReadPolicy, policy =>
            {
                policy.AuthenticationSchemes.Add("Bearer");
                policy.RequireAuthenticatedUser();
                policy.RequireAssertion(context =>
                    HasScope(context.User, "Swarm.Read") ||
                    HasRole(context.User, SwarmAdminRole));
            })
            .AddPolicy(SwarmWritePolicy, policy =>
            {
                policy.AuthenticationSchemes.Add("Bearer");
                policy.RequireAuthenticatedUser();
                policy.RequireAssertion(context =>
                    HasScope(context.User, "Swarm.Write") ||
                    HasRole(context.User, SwarmAdminRole));
            });

        return services;
    }

    /// <summary>
    /// Checks if the user has the specified scope in their token claims.
    /// Handles both space-delimited scp claim and individual scope claims.
    /// </summary>
    /// <param name="user">The claims principal.</param>
    /// <param name="scope">The scope to check for.</param>
    /// <returns>True if the user has the scope.</returns>
    private static bool HasScope(ClaimsPrincipal user, string scope)
    {
        return user.Claims.Any(c =>
            (string.Equals(c.Type, "scp", StringComparison.Ordinal) ||
             string.Equals(c.Type, "http://schemas.microsoft.com/identity/claims/scope", StringComparison.Ordinal)) &&
            c.Value.Split(' ').Contains(scope, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Checks if the user has the specified app role in their token claims.
    /// </summary>
    /// <param name="user">The claims principal.</param>
    /// <param name="role">The role to check for.</param>
    /// <returns>True if the user has the role.</returns>
    private static bool HasRole(ClaimsPrincipal user, string role)
    {
        return user.IsInRole(role) ||
               user.HasClaim("roles", role) ||
               user.HasClaim(ClaimTypes.Role, role);
    }
}
