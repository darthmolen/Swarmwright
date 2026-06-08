using Swarmwright.Models.Enums;

namespace Swarmwright.Events;

/// <summary>
/// Published by the dispatcher at the swarm terminal point to trigger durable
/// archival of the completed run. Published via <see cref="ISwarmNotificationPublisher"/>, the
/// publish returns immediately (the terminal observation signal is never delayed) while the
/// archive runs off-thread in <see cref="SwarmNotificationBackgroundService"/>.
/// </summary>
public sealed record SwarmRunCompletedNotification
{
    /// <summary>Gets the swarm identifier.</summary>
    public Guid SwarmId { get; init; }

    /// <summary>Gets the local work-directory root to archive.</summary>
    public string WorkDirectory { get; init; } = string.Empty;

    /// <summary>Gets the user-provided goal.</summary>
    public string Goal { get; init; } = string.Empty;

    /// <summary>Gets the template key the swarm ran under, when present.</summary>
    public string? TemplateKey { get; init; }

    /// <summary>Gets the execution creation timestamp.</summary>
    public DateTime CreatedUtc { get; init; }

    /// <summary>Gets the terminal-point timestamp.</summary>
    public DateTime CompletedUtc { get; init; }

    /// <summary>Gets the terminal swarm state.</summary>
    public SwarmInstanceState FinalState { get; init; }

    /// <summary>Gets the failure reason when <see cref="FinalState"/> is Failed; otherwise <c>null</c>.</summary>
    public string? FailureReason { get; init; }

    /// <summary>Gets the per-swarm run-context bag supplied at creation, carried through to the archive manifest.</summary>
    public IReadOnlyDictionary<string, string> Context { get; init; } = new Dictionary<string, string>();
}
