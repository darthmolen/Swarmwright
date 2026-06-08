using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Swarmwright.Database.Models;

/// <summary>
/// Entity representing a message exchanged within a swarm.
/// </summary>
[Table("swarm_messages")]
public sealed class MessageEntity
{
    /// <summary>Gets or sets the auto-increment message identifier.</summary>
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    [Column("id")]
    public long Id { get; set; }

    /// <summary>Gets or sets the parent swarm identifier.</summary>
    [Column("swarm_id")]
    public Guid SwarmId { get; set; }

    /// <summary>Gets or sets the sender name.</summary>
    [MaxLength(100)]
    [Column("sender")]
    public string Sender { get; set; } = string.Empty;

    /// <summary>Gets or sets the recipient name.</summary>
    [MaxLength(100)]
    [Column("recipient")]
    public string Recipient { get; set; } = string.Empty;

    /// <summary>Gets or sets the message content.</summary>
    [Required]
    [Column("content")]
    public string Content { get; set; } = string.Empty;

    /// <summary>Gets or sets the creation timestamp.</summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
