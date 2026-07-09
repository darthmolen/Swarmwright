namespace Swarmwright.McpServer.Configuration;

/// <summary>
/// Role and scope names used by the Swarm MCP authorization policies.
/// </summary>
public sealed class SwarmMcpAuthorizationOptions
{
    /// <summary>
    /// The configuration section name these options bind to.
    /// </summary>
    public const string SectionName = "SwarmMcpAuthorization";

    /// <summary>Name of the authorization policy that gates read-only tools.</summary>
    public const string ReadPolicyName = "SwarmMcp.Read";

    /// <summary>Name of the authorization policy that gates write/destructive tools.</summary>
    public const string WritePolicyName = "SwarmMcp.Write";

    /// <summary>Gets or sets the role required for read-only tools (app-to-app callers).</summary>
    public string ReadRole { get; set; } = "SwarmMcp.Read";

    /// <summary>Gets or sets the role required for write tools (app-to-app callers).</summary>
    public string WriteRole { get; set; } = "SwarmMcp.Write";

    /// <summary>Gets or sets the scope required for read-only tools (delegated callers).</summary>
    public string ReadScope { get; set; } = "SwarmMcp.Read";

    /// <summary>Gets or sets the scope required for write tools (delegated callers).</summary>
    public string WriteScope { get; set; } = "SwarmMcp.Write";
}
