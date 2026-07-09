namespace Swarmwright.Database.Models;

/// <summary>
/// Summary projection for a swarm used by the <c>GET /api/swarm/</c> list endpoint.
/// Carries live-state fields sourced from in-memory executions and aggregate fields
/// sourced from the persistence layer so the frontend can render a single unified
/// session list. All timestamps are expressed in UTC.
/// </summary>
/// <param name="SwarmId">The unique swarm identifier.</param>
/// <param name="Goal">The user-provided goal of the swarm.</param>
/// <param name="TemplateKey">The optional template key used to configure the swarm.</param>
/// <param name="Phase">The swarm's current phase, serialized as its PascalCase enum name.</param>
/// <param name="IsRunning">A value indicating whether the swarm is currently running in memory.</param>
/// <param name="CreatedAt">The UTC timestamp at which the swarm was created.</param>
/// <param name="CompletedAt">The UTC timestamp at which the swarm completed, or <see langword="null"/> when still running.</param>
/// <param name="LastEventAt">The UTC timestamp of the most recent event, or the last update time when no events exist.</param>
/// <param name="TaskCount">The total number of tasks associated with the swarm.</param>
/// <param name="WorkerCount">The total number of worker agents associated with the swarm.</param>
public sealed record SwarmListEntry(
    Guid SwarmId,
    string Goal,
    string? TemplateKey,
    string Phase,
    bool IsRunning,
    DateTime CreatedAt,
    DateTime? CompletedAt,
    DateTime? LastEventAt,
    int TaskCount,
    int WorkerCount);
