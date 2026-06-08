using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Swarmwright.Database.Models;

/// <summary>
/// Entity representing an event emitted by the swarm system.
/// </summary>
[Table("swarm_events")]
public sealed class EventEntity
{
    /// <summary>Gets or sets the auto-increment event identifier.</summary>
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    [Column("id")]
    public long Id { get; set; }

    /// <summary>Gets or sets the optional swarm identifier.</summary>
    [Column("swarm_id")]
    public Guid? SwarmId { get; set; }

    /// <summary>Gets or sets the event type.</summary>
    [MaxLength(100)]
    [Column("event_type")]
    public string EventType { get; set; } = string.Empty;

    /// <summary>Gets or sets the event data as JSON.</summary>
    [Column("data_json")]
    public string DataJson { get; set; } = "{}";

    /// <summary>Gets or sets the creation timestamp.</summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
