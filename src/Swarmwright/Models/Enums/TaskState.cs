namespace Swarmwright.Models.Enums;

/// <summary>
/// Represents the lifecycle state of a task within a swarm.
/// Single source of truth for the task-level state machine.
/// </summary>
public enum TaskState
{
    /// <summary>Task is waiting for dependencies to complete.</summary>
    Blocked,

    /// <summary>Task is ready to be picked up by a worker.</summary>
    Pending,

    /// <summary>Task is currently being worked on.</summary>
    InProgress,

    /// <summary>Task completed successfully.</summary>
    Completed,

    /// <summary>Task failed during execution.</summary>
    Failed,

    /// <summary>Task paused awaiting user answer to a feedback request.</summary>
    AwaitingFeedback,
}
