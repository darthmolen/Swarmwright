using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Swarmwright.Database.Models;

/// <summary>
/// Entity representing an agent within a swarm.
/// </summary>
[Table("swarm_agents")]
public sealed class AgentEntity
{
    /// <summary>Gets or sets the parent swarm identifier.</summary>
    [Column("swarm_id")]
    public Guid SwarmId { get; set; }

    /// <summary>Gets or sets the agent name.</summary>
    [MaxLength(100)]
    [Column("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the agent role.</summary>
    [MaxLength(200)]
    [Column("role")]
    public string Role { get; set; } = string.Empty;

    /// <summary>Gets or sets the agent display name.</summary>
    [MaxLength(200)]
    [Column("display_name")]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Gets or sets the session identifier.</summary>
    [MaxLength(200)]
    [Column("session_id")]
    public string? SessionId { get; set; }

    /// <summary>Gets or sets the agent status.</summary>
    [MaxLength(30)]
    [Column("status")]
    public string Status { get; set; } = "idle";

    /// <summary>Gets or sets the number of tasks completed.</summary>
    [Column("tasks_completed")]
    public int TasksCompleted { get; set; }
}
