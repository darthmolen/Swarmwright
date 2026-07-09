using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Swarmwright.Database.Models;

/// <summary>
/// Entity representing a task within a swarm.
/// </summary>
[Table("swarm_tasks")]
public sealed class TaskEntity
{
    /// <summary>Gets or sets the parent swarm identifier.</summary>
    [Column("swarm_id")]
    public Guid SwarmId { get; set; }

    /// <summary>Gets or sets the task identifier.</summary>
    [MaxLength(50)]
    [Column("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the task subject.</summary>
    [Required]
    [Column("subject")]
    public string Subject { get; set; } = string.Empty;

    /// <summary>Gets or sets the task description.</summary>
    [Required]
    [Column("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>Gets or sets the worker role assigned to this task.</summary>
    [MaxLength(100)]
    [Column("worker_role")]
    public string WorkerRole { get; set; } = string.Empty;

    /// <summary>Gets or sets the worker name assigned to this task.</summary>
    [MaxLength(100)]
    [Column("worker_name")]
    public string WorkerName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the canonical PascalCase form of the task state
    /// (see <see cref="Swarmwright.Models.Enums.TaskState"/>).
    /// Written only by <c>StateTransitionService</c>.
    /// </summary>
    [Required]
    [MaxLength(40)]
    [Column("state")]
    public string State { get; set; } = "Pending";

    /// <summary>Gets or sets the number of <c>Failed → Pending</c> transitions consumed via <c>Continue</c>.</summary>
    [Column("retry_count")]
    public int RetryCount { get; set; }

    /// <summary>Gets or sets the blocked-by list as JSON.</summary>
    [Column("blocked_by_json")]
    public string BlockedByJson { get; set; } = "[]";

    /// <summary>Gets or sets the task result.</summary>
    [Column("result")]
    public string Result { get; set; } = string.Empty;

    /// <summary>Gets or sets the creation timestamp.</summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Gets or sets the last update timestamp.</summary>
    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Gets or sets the Postgres <c>xmin</c> system column mapped as a concurrency token.</summary>
    [Column("xmin", TypeName = "xid")]
    public uint Xmin { get; set; }
}
