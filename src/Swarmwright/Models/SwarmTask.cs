using Swarmwright.Models.Enums;

namespace Swarmwright.Models;

/// <summary>
/// Represents a task on the swarm task board.
/// </summary>
public class SwarmTask
{
    /// <summary>Gets or sets the parent swarm identifier.</summary>
    public Guid SwarmId { get; set; }

    /// <summary>Gets or sets the unique task identifier.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the short title of the task.</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>Gets or sets the detailed task description.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Gets or sets the specialist role required for this task.</summary>
    public string WorkerRole { get; set; } = string.Empty;

    /// <summary>Gets or sets the name of the assigned worker.</summary>
    public string WorkerName { get; set; } = string.Empty;

    /// <summary>Gets or sets the current task status.</summary>
    public TaskState Status { get; set; } = TaskState.Pending;

    /// <summary>Gets the list of task IDs that must complete before this task can start.</summary>
    public List<string> BlockedBy { get; } = [];

    /// <summary>Gets or sets the result produced by the worker.</summary>
    public string Result { get; set; } = string.Empty;

    /// <summary>Gets or sets the creation timestamp.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Gets or sets the last update timestamp.</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
