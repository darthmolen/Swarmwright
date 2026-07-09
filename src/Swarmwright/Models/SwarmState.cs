using Swarmwright.Models.Enums;

namespace Swarmwright.Models;

/// <summary>
/// Represents the runtime state of a swarm instance.
/// </summary>
public class SwarmState
{
    /// <summary>Gets or sets the unique swarm identifier.</summary>
    public Guid SwarmId { get; set; }

    /// <summary>Gets or sets the user-provided goal.</summary>
    public string Goal { get; set; } = string.Empty;

    /// <summary>Gets or sets the template key used for this swarm.</summary>
    public string? TemplateKey { get; set; }

    /// <summary>Gets or sets the current swarm instance state.</summary>
    public SwarmInstanceState State { get; set; } = SwarmInstanceState.Created;

    /// <summary>Gets or sets the current execution round number.</summary>
    public int RoundNumber { get; set; }
}
