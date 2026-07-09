using System.Text.Json.Serialization;

namespace Swarmwright.Models;

/// <summary>
/// Represents a single task within a swarm plan.
/// </summary>
[JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
public class TaskPlan
{
    /// <summary>Gets or sets the short task title.</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>Gets or sets the detailed task description.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Gets or sets the specialist role required.</summary>
    public string WorkerRole { get; set; } = string.Empty;

    /// <summary>Gets or sets the worker name (snake_case).</summary>
    public string WorkerName { get; set; } = string.Empty;

    /// <summary>Gets the 0-based indices of tasks that must complete first.</summary>
    public List<int> BlockedByIndices { get; } = [];
}
