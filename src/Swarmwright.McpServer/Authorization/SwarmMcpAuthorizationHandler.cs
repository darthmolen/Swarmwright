using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace Swarmwright.McpServer.Authorization;

/// <summary>
/// Handler for <see cref="SwarmMcpAuthorizationRequirement"/>. Succeeds when
/// the principal has the required role (<c>roles</c> or <c>ClaimTypes.Role</c> claim)
/// or the required scope in the <c>scp</c> claim (space-separated per Azure AD).
/// </summary>
public sealed class SwarmMcpAuthorizationHandler : AuthorizationHandler<SwarmMcpAuthorizationRequirement>
{
    private const string ScopeClaimType = "scp";
    private const string RoleClaimType = "roles";

    /// <inheritdoc/>
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        SwarmMcpAuthorizationRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        if (HasRole(context.User, requirement.RequiredRole)
            || HasScope(context.User, requirement.RequiredScope))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }

    private static bool HasRole(ClaimsPrincipal user, string requiredRole)
    {
        if (string.IsNullOrEmpty(requiredRole))
        {
            return false;
        }

        if (user.IsInRole(requiredRole))
        {
            return true;
        }

        foreach (var claim in user.FindAll(RoleClaimType))
        {
            if (string.Equals(claim.Value, requiredRole, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasScope(ClaimsPrincipal user, string requiredScope)
    {
        if (string.IsNullOrEmpty(requiredScope))
        {
            return false;
        }

        foreach (var claim in user.FindAll(ScopeClaimType))
        {
            var scopes = claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var scope in scopes)
            {
                if (string.Equals(scope, requiredScope, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
