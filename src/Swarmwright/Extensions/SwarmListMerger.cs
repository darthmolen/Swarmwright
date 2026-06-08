using Swarmwright.Database.Models;
using Swarmwright.Hosting;

namespace Swarmwright.Extensions;

/// <summary>
/// Merges the in-memory active swarm registry with the database-backed historical
/// projection into a single deduplicated, time-ordered list suitable for the
/// <c>GET /api/swarm/</c> response. The merger is deterministic, side-effect free,
/// and easy to unit-test in isolation from the ASP.NET Core request pipeline.
/// </summary>
public static class SwarmListMerger
{
    /// <summary>
    /// Unions the active in-memory executions with the historical database entries,
    /// preferring the in-memory live-state fields (phase, running, last-event) where
    /// both sources describe the same swarm, and borrowing count fields from the
    /// database where the in-memory projection does not carry them.
    /// </summary>
    /// <param name="active">The live in-memory swarm executions from the swarm manager.</param>
    /// <param name="historical">The historical swarm summaries loaded from the repository.</param>
    /// <param name="limit">
    /// The cap applied only to the HISTORICAL slice. Active entries are always
    /// included regardless of the value so that a user with many concurrent swarms
    /// never loses visibility of their working set. This mirrors the documented
    /// review decision (I3) for this endpoint.
    /// </param>
    /// <returns>The merged, deduplicated, newest-first list of swarm summaries.</returns>
    public static IReadOnlyList<SwarmListEntry> Merge(
        IReadOnlyList<SwarmExecution> active,
        IReadOnlyList<SwarmListEntry> historical,
        int limit)
    {
        ArgumentNullException.ThrowIfNull(active);
        ArgumentNullException.ThrowIfNull(historical);

        // Index historical entries by id so the active projection can read its
        // counts and completion timestamps in O(1).
        var historicalById = new Dictionary<Guid, SwarmListEntry>(historical.Count);
        foreach (var entry in historical)
        {
            historicalById[entry.SwarmId] = entry;
        }

        var activeIds = new HashSet<Guid>();
        var merged = new List<SwarmListEntry>(active.Count + historical.Count);

        // Project every active execution. In-memory wins on phase/isRunning/lastEventAt,
        // DB fills in counts and createdAt/completedAt when the swarm has been persisted.
        foreach (var execution in active)
        {
            activeIds.Add(execution.SwarmId);
            historicalById.TryGetValue(execution.SwarmId, out var dbEntry);

            var createdAt = dbEntry?.CreatedAt ?? execution.CreatedAt;
            var completedAt = dbEntry?.CompletedAt;
            var lastEventAt = dbEntry?.LastEventAt;
            var taskCount = dbEntry?.TaskCount ?? 0;
            var workerCount = dbEntry?.WorkerCount ?? 0;

            // Phase is now sourced from the DB (single source of truth after
            // the state-machine migration). `SwarmService.CreateSwarmAsync`
            // persists the row immediately so an active swarm always has a
            // historical entry to borrow from. "Running" is the defensive
            // fallback for the transient window between manager insertion
            // and the first DB write.
            var phase = dbEntry?.Phase ?? "Running";

            merged.Add(new SwarmListEntry(
                execution.SwarmId,
                execution.Goal,
                execution.TemplateKey,
                phase,
                execution.IsRunning,
                createdAt,
                completedAt,
                lastEventAt,
                taskCount,
                workerCount));
        }

        // Apply the limit to the historical-only slice. The rule (per the plan's
        // review I3): active entries are always included; the limit bounds the
        // total response size, so the historical capacity is `limit - active_count`
        // clamped to zero. When the active working set already meets or exceeds
        // the limit, no historical entries are returned.
        var historicalCapacity = Math.Max(0, limit - merged.Count);
        var historicalOnly = historical
            .Where(h => !activeIds.Contains(h.SwarmId))
            .Take(historicalCapacity)
            .ToList();
        merged.AddRange(historicalOnly);

        // Sort by effective activity descending. Use lastEventAt when available,
        // otherwise fall back to createdAt so every row has a sortable key.
        merged.Sort((a, b) =>
        {
            var aKey = a.LastEventAt ?? a.CreatedAt;
            var bKey = b.LastEventAt ?? b.CreatedAt;
            return bKey.CompareTo(aKey);
        });

        return merged;
    }
}
