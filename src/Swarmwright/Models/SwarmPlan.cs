namespace Swarmwright.Models;

/// <summary>
/// Represents a plan produced by the leader agent during the planning phase.
/// </summary>
public class SwarmPlan
{
    /// <summary>Gets or sets the team description from the leader.</summary>
    public string TeamDescription { get; set; } = string.Empty;

    /// <summary>Gets the list of planned tasks.</summary>
    public List<TaskPlan> Tasks { get; } = [];
}
