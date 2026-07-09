using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Swarmwright.Database.Models;

/// <summary>
/// Audit row capturing a single task state transition.
/// </summary>
[Table("task_state_transitions")]
public sealed class TaskStateTransition
{
    /// <summary>Gets or sets the transition row identifier.</summary>
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Gets or sets the owning swarm identifier.</summary>
    [Column("swarm_id")]
    public Guid SwarmId { get; set; }

    /// <summary>Gets or sets the task whose state changed.</summary>
    [Required]
    [MaxLength(50)]
    [Column("task_id")]
    public string TaskId { get; set; } = string.Empty;

    /// <summary>Gets or sets the state the task was in prior to this transition.</summary>
    [Required]
    [MaxLength(40)]
    [Column("from_state")]
    public string FromState { get; set; } = string.Empty;

    /// <summary>Gets or sets the state the task moved into.</summary>
    [Required]
    [MaxLength(40)]
    [Column("to_state")]
    public string ToState { get; set; } = string.Empty;

    /// <summary>Gets or sets the reason label.</summary>
    [Required]
    [MaxLength(60)]
    [Column("reason")]
    public string Reason { get; set; } = string.Empty;

    /// <summary>Gets or sets the actor that initiated the transition.</summary>
    [MaxLength(200)]
    [Column("actor")]
    public string? Actor { get; set; }

    /// <summary>Gets or sets the retry_count snapshot after the transition (unchanged when not a retry).</summary>
    [Column("retry_count_after")]
    public int RetryCountAfter { get; set; }

    /// <summary>Gets or sets a free-form note.</summary>
    [Column("note")]
    public string? Note { get; set; }

    /// <summary>Gets or sets the wall-clock time when the transition was recorded.</summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
