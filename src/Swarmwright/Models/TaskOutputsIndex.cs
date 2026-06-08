namespace Swarmwright.Models;

/// <summary>
/// The agent-attributed output index (<c>task-outputs.json</c>) emitted at
/// the end of every swarm run. Provides deterministic, pre-synthesis ground
/// truth — a (input, output, label-join-key) triple per completed task that
/// survives synthesis dedup/merge and is produced on failed runs too.
/// </summary>
/// <param name="SwarmId">The swarm identifier.</param>
/// <param name="TemplateKey">The template key the swarm ran under, when present.</param>
/// <param name="CompletedUtc">The UTC timestamp at which the index was written.</param>
/// <param name="Tasks">The per-task attributed output entries.</param>
public sealed record TaskOutputsIndex(
    string SwarmId,
    string? TemplateKey,
    DateTime CompletedUtc,
    IReadOnlyList<TaskOutputEntry> Tasks);
