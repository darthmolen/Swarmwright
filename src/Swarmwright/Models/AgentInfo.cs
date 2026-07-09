using Swarmwright.Models.Enums;

namespace Swarmwright.Models;

/// <summary>
/// Represents information about a swarm agent.
/// </summary>
public class AgentInfo
{
    /// <summary>Gets or sets the unique agent name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the agent's role description.</summary>
    public string Role { get; set; } = string.Empty;

    /// <summary>Gets or sets the human-readable display name.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Gets or sets the current agent status.</summary>
    public AgentStatus Status { get; set; } = AgentStatus.Idle;

    /// <summary>Gets or sets the number of tasks completed by this agent.</summary>
    public int TasksCompleted { get; set; }
}
