using ModelContextProtocol.Client;

namespace Swarmwright.Mcp;

/// <summary>
/// Optional factory for Model Context Protocol (MCP) clients. The orchestrator resolves this via
/// <c>GetService</c>; when no implementation is registered, MCP tool loading is skipped. Implement
/// and register this to let workers load tools from MCP endpoints named in their template
/// <c>mcp_endpoints:</c> frontmatter.
/// </summary>
public interface IMcpClientFactory
{
    /// <summary>
    /// Gets an existing MCP client for the endpoint or creates a new one if none exists.
    /// </summary>
    /// <param name="endpointName">The name of the MCP endpoint.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>An MCP client configured for the specified endpoint.</returns>
    /// <exception cref="ArgumentException">Thrown when the endpoint name is null, empty, or not configured.</exception>
    /// <exception cref="InvalidOperationException">Thrown when client creation fails.</exception>
    public Task<McpClient> GetOrCreateClientAsync(string endpointName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Invalidates the cached client for the specified endpoint, forcing recreation on next request.
    /// </summary>
    /// <param name="endpointName">The name of the MCP endpoint to invalidate.</param>
    public void InvalidateClient(string endpointName);
}
