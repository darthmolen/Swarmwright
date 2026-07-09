using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Swarmwright.Database.Models;

/// <summary>
/// Entity representing a swarm orchestration session.
/// </summary>
[Table("swarms")]
public sealed class SwarmEntity
{
    /// <summary>Gets or sets the unique swarm identifier.</summary>
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Gets or sets the goal of the swarm.</summary>
    [Required]
    [Column("goal")]
    public string Goal { get; set; } = string.Empty;

    /// <summary>Gets or sets the QA-refined goal.</summary>
    [Column("qa_refined_goal")]
    public string? QaRefinedGoal { get; set; }

    /// <summary>
    /// Gets or sets the canonical PascalCase form of the swarm instance state
    /// (see <see cref="Swarmwright.Models.Enums.SwarmInstanceState"/>).
    /// Written only by <c>StateTransitionService</c>.
    /// </summary>
    [Required]
    [MaxLength(40)]
    [Column("state")]
    public string State { get; set; } = "Created";

    /// <summary>Gets or sets the template key used by the swarm.</summary>
    [MaxLength(100)]
    [Column("template_key")]
    public string? TemplateKey { get; set; }

    /// <summary>Gets or sets the synthesis session identifier.</summary>
    [MaxLength(200)]
    [Column("synthesis_session_id")]
    public string? SynthesisSessionId { get; set; }

    /// <summary>
    /// Gets or sets the JSON-serialized free-form key/value context supplied at
    /// swarm creation. Stored as a plain string column (not Postgres <c>jsonb</c>)
    /// so the SQLite/InMemory providers work; defaults to an empty JSON object so
    /// existing rows backfill cleanly. Rehydrated into the run context on resume.
    /// </summary>
    [Required]
    [Column("context_json")]
    public string ContextJson { get; set; } = "{}";

    /// <summary>Gets or sets the final report.</summary>
    [Column("report")]
    public string? Report { get; set; }

    /// <summary>Gets or sets the current round number.</summary>
    [Column("current_round")]
    public int CurrentRound { get; set; }

    /// <summary>Gets or sets the maximum number of rounds.</summary>
    [Column("max_rounds")]
    public int MaxRounds { get; set; } = 8;

    /// <summary>Gets or sets the count of automatic Smart Continue attempts consumed for this swarm.</summary>
    [Column("auto_smart_continue_count")]
    public int AutoSmartContinueCount { get; set; }

    /// <summary>Gets or sets the principal name holding the diagnose lock, if any.</summary>
    [MaxLength(200)]
    [Column("locked_by")]
    public string? LockedBy { get; set; }

    /// <summary>Gets or sets the wall-clock time the diagnose lock was acquired.</summary>
    [Column("locked_at")]
    public DateTime? LockedAt { get; set; }

    /// <summary>Gets or sets the creation timestamp.</summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Gets or sets the last update timestamp.</summary>
    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Gets or sets the completion timestamp.</summary>
    [Column("completed_at")]
    public DateTime? CompletedAt { get; set; }

    /// <summary>Gets or sets the Postgres <c>xmin</c> system column mapped as a concurrency token.</summary>
    [Column("xmin", TypeName = "xid")]
    public uint Xmin { get; set; }
}
