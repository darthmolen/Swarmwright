using Swarmwright.Models.Enums;

namespace Swarmwright.Archival;

/// <summary>
/// The terminal-run metadata an archiver needs to promote a completed swarm
/// work directory to durable storage and write its manifest. All fields are
/// available at the dispatcher terminal point; the agent roster is read from
/// the archived <c>.chat/agents.json</c> rather than carried here.
/// </summary>
/// <param name="SwarmId">The swarm identifier.</param>
/// <param name="WorkDirectory">The local work-directory root to copy verbatim.</param>
/// <param name="Goal">The user-provided goal.</param>
/// <param name="TemplateKey">The template key the swarm ran under, when present.</param>
/// <param name="CreatedUtc">The execution creation timestamp.</param>
/// <param name="CompletedUtc">The terminal-point timestamp.</param>
/// <param name="FinalState">The terminal swarm state.</param>
/// <param name="FailureReason">The failure reason when <paramref name="FinalState"/> is Failed; otherwise <c>null</c>.</param>
/// <param name="Context">The per-swarm run-context bag supplied at creation (sourced from <c>ISwarmRunContext</c>); written to the manifest so downstream consumers can correlate the run.</param>
public sealed record SwarmRunArchiveContext(
    Guid SwarmId,
    string WorkDirectory,
    string Goal,
    string? TemplateKey,
    DateTime CreatedUtc,
    DateTime CompletedUtc,
    SwarmInstanceState FinalState,
    string? FailureReason,
    IReadOnlyDictionary<string, string> Context);
