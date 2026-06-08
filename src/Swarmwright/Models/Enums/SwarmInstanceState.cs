namespace Swarmwright.Models.Enums;

/// <summary>
/// Represents the lifecycle state of a swarm instance.
/// Single source of truth for the swarm-level state machine.
/// </summary>
public enum SwarmInstanceState
{
    /// <summary>Swarm is initializing.</summary>
    Created,

    /// <summary>Leader is decomposing goal into tasks.</summary>
    Planning,

    /// <summary>Worker agents are being created.</summary>
    Spawning,

    /// <summary>Workers are executing tasks in rounds.</summary>
    Executing,

    /// <summary>Swarm is paused awaiting user intervention after one or more task failures; recovery budget remains.</summary>
    AwaitingIntervention,

    /// <summary>Recovery budget exhausted; swarm requires human diagnosis.</summary>
    NeedsDiagnosis,

    /// <summary>An agent requested user feedback mid-flight.</summary>
    AwaitingFeedback,

    /// <summary>Leader is synthesizing results into a report.</summary>
    Synthesizing,

    /// <summary>Swarm completed successfully.</summary>
    Complete,

    /// <summary>Swarm was cancelled by user.</summary>
    Cancelled,

    /// <summary>Swarm failed due to an unrecoverable error.</summary>
    Failed,
}
