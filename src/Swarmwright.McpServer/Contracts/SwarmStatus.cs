namespace Swarmwright.McpServer.Contracts;

/// <summary>
/// Status snapshot for a single swarm.
/// </summary>
/// <param name="SwarmId">The unique swarm identifier.</param>
/// <param name="Phase">The current lifecycle phase as a PascalCase string.</param>
/// <param name="IsRunning">A value indicating whether the swarm is currently running.</param>
/// <param name="AgentCount">The number of registered agents.</param>
/// <param name="TaskCountsByStatus">Task counts keyed by status name (e.g., <c>Pending</c>, <c>Completed</c>).</param>
public sealed record SwarmStatus(
    Guid SwarmId,
    string Phase,
    bool IsRunning,
    int AgentCount,
    IReadOnlyDictionary<string, int> TaskCountsByStatus);
