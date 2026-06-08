using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Swarmwright.Database.Models;

/// <summary>
/// Entity representing a file produced by a swarm.
/// </summary>
[Table("swarm_files")]
public sealed class FileEntity
{
    /// <summary>Gets or sets the auto-increment file identifier.</summary>
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    [Column("id")]
    public long Id { get; set; }

    /// <summary>Gets or sets the parent swarm identifier.</summary>
    [Column("swarm_id")]
    public Guid SwarmId { get; set; }

    /// <summary>Gets or sets the file path.</summary>
    [Required]
    [Column("path")]
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets the file size in bytes.</summary>
    [Column("size_bytes")]
    public long SizeBytes { get; set; }

    /// <summary>Gets or sets the creation timestamp.</summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
