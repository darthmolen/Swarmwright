namespace Swarmwright.McpServer.Contracts;

/// <summary>
/// Return value from <c>create_swarm</c>.
/// </summary>
/// <param name="SwarmId">The identifier of the newly created swarm.</param>
/// <param name="Status">A short status label (<c>"starting"</c> on success).</param>
public sealed record SwarmCreatedResult(Guid SwarmId, string Status);
