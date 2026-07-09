using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Swarmwright.Database.Models;

/// <summary>
/// Audit row capturing a single swarm-instance state transition.
/// </summary>
[Table("swarm_state_transitions")]
public sealed class SwarmStateTransition
{
    /// <summary>Gets or sets the transition row identifier.</summary>
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Gets or sets the swarm whose state changed.</summary>
    [Column("swarm_id")]
    public Guid SwarmId { get; set; }

    /// <summary>Gets or sets the state the swarm was in prior to this transition.</summary>
    [Required]
    [MaxLength(40)]
    [Column("from_state")]
    public string FromState { get; set; } = string.Empty;

    /// <summary>Gets or sets the state the swarm moved into.</summary>
    [Required]
    [MaxLength(40)]
    [Column("to_state")]
    public string ToState { get; set; } = string.Empty;

    /// <summary>Gets or sets the reason label (e.g. <c>user_continue</c>, <c>lock_acquired</c>).</summary>
    [Required]
    [MaxLength(60)]
    [Column("reason")]
    public string Reason { get; set; } = string.Empty;

    /// <summary>Gets or sets the actor that initiated the transition (e.g. user principal name, <c>system</c>, <c>leader</c>).</summary>
    [MaxLength(200)]
    [Column("actor")]
    public string? Actor { get; set; }

    /// <summary>Gets or sets a free-form note (e.g. leader repair rationale).</summary>
    [Column("note")]
    public string? Note { get; set; }

    /// <summary>Gets or sets the wall-clock time when the transition was recorded.</summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
