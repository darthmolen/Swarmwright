namespace Swarmwright.McpServer.Contracts;

/// <summary>
/// Agent descriptor returned by <c>list_agents</c>.
/// Named <c>AgentSummary</c> to avoid collision with the Swarm domain's
/// <c>Swarmwright.Models.AgentInfo</c>.
/// </summary>
/// <param name="Name">Unique agent name.</param>
/// <param name="DisplayName">Human-readable display name.</param>
/// <param name="Role">Agent role description.</param>
/// <param name="Status">Agent status as a PascalCase string.</param>
/// <param name="TasksCompleted">Number of tasks completed by this agent.</param>
public sealed record AgentSummary(
    string Name,
    string DisplayName,
    string Role,
    string Status,
    int TasksCompleted);
