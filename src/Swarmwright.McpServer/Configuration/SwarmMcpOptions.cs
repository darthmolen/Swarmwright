namespace Swarmwright.McpServer.Configuration;

/// <summary>
/// Options for the Swarm MCP server.
/// </summary>
public sealed class SwarmMcpOptions
{
    /// <summary>
    /// The configuration section name these options bind to.
    /// </summary>
    public const string SectionName = "SwarmMcp";

    /// <summary>
    /// Gets or sets the authentication mode.
    /// </summary>
    public SwarmMcpAuthMode AuthMode { get; set; } = SwarmMcpAuthMode.None;

    /// <summary>
    /// Gets or sets the HTTP path at which the MCP server is mapped.
    /// </summary>
    public string EndpointPath { get; set; } = "/swarm/mcp";

    /// <summary>
    /// Gets or sets the shared API key used when <see cref="AuthMode"/> is <see cref="SwarmMcpAuthMode.ApiKey"/>.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the maximum number of bytes that <c>read_artifact</c> will return in a single call.
    /// Files larger than this are truncated with an explanatory suffix.
    /// </summary>
    public int MaxArtifactBytes { get; set; } = 256 * 1024;
}
