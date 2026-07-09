using Microsoft.AspNetCore.Authorization;

namespace Swarmwright.McpServer.Authorization;

/// <summary>
/// Authorization requirement for Swarm MCP policies. Succeeds when the principal
/// has any configured role <em>or</em> any configured scope, supporting both
/// app-to-app (role claim) and delegated (scope claim) callers.
/// </summary>
public sealed class SwarmMcpAuthorizationRequirement : IAuthorizationRequirement
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SwarmMcpAuthorizationRequirement"/> class.
    /// </summary>
    /// <param name="requiredRole">The role that satisfies this requirement for app-to-app callers.</param>
    /// <param name="requiredScope">The scope that satisfies this requirement for delegated callers.</param>
    public SwarmMcpAuthorizationRequirement(string requiredRole, string requiredScope)
    {
        this.RequiredRole = requiredRole;
        this.RequiredScope = requiredScope;
    }

    /// <summary>Gets the role that satisfies this requirement.</summary>
    public string RequiredRole { get; }

    /// <summary>Gets the scope that satisfies this requirement.</summary>
    public string RequiredScope { get; }
}
