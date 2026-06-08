namespace Swarmwright.McpServer.Configuration;

/// <summary>
/// Authentication mode for the Swarm MCP server endpoint.
/// </summary>
public enum SwarmMcpAuthMode
{
    /// <summary>No authentication. Principal is fabricated with both Read and Write claims. Dev only.</summary>
    None,

    /// <summary>Shared-secret API key supplied via the <c>X-API-Key</c> request header.</summary>
    ApiKey,

    /// <summary>Azure AD JWT bearer tokens validated via Microsoft.Identity.Web.</summary>
    AzureAD,
}
