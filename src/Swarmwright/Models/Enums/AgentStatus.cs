namespace Swarmwright.Models.Enums;

/// <summary>
/// Represents the current status of a swarm agent.
/// </summary>
public enum AgentStatus
{
    /// <summary>Agent is idle and waiting for work.</summary>
    Idle,

    /// <summary>Agent is thinking or reasoning.</summary>
    Thinking,

    /// <summary>Agent is actively working on a task.</summary>
    Working,

    /// <summary>Agent has completed all assigned work.</summary>
    Ready,

    /// <summary>Agent encountered an unrecoverable error.</summary>
    Failed,
}
