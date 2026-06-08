using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Swarmwright.Database.Models;
using Swarmwright.Models.Enums;

namespace Swarmwright.Database.Repositories;

/// <summary>
/// Repository implementation for swarm persistence operations.
/// </summary>
/// <remarks>
/// Each public method creates a short-lived <see cref="SwarmDbContext"/> via
/// <see cref="IDbContextFactory{TContext}"/> so that concurrent callers (for example,
/// parallel swarm workers) never share a single context instance. Sharing a context
/// across threads triggers EF Core's <c>ConcurrencyDetector</c> and produces
/// <c>InvalidOperationException: A second operation was started on this context instance</c>.
/// </remarks>
public class SwarmRepository : ISwarmRepository
{
    private readonly IDbContextFactory<SwarmDbContext> contextFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="SwarmRepository"/> class.
    /// </summary>
    /// <param name="contextFactory">The database context factory used to create a fresh
    /// context for each repository operation.</param>
    public SwarmRepository(IDbContextFactory<SwarmDbContext> contextFactory)
    {
        this.contextFactory = contextFactory;
    }

    /// <inheritdoc/>
    public async Task CreateSwarmAsync(SwarmEntity swarm)
    {
        await using var context = await this.contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        context.Swarms.Add(swarm);
        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<SwarmEntity?> GetSwarmAsync(Guid swarmId)
    {
        await using var context = await this.contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.Swarms
            .FindAsync(swarmId)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task UpdateRoundAsync(Guid swarmId, int round)
    {
        await using var context = await this.contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var swarm = await context.Swarms
            .FindAsync(swarmId)
            .ConfigureAwait(false);

        if (swarm is not null)
        {
            swarm.CurrentRound = round;
            swarm.UpdatedAt = DateTime.UtcNow;
            await context.SaveChangesAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async Task SetLockAsync(Guid swarmId, string? lockedBy, DateTime? lockedAt, CancellationToken cancellationToken = default)
    {
        await using var context = await this.contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var swarm = await context.Swarms
            .FindAsync([swarmId], cancellationToken)
            .ConfigureAwait(false);

        if (swarm is null)
        {
            return;
        }

        swarm.LockedBy = lockedBy;
        swarm.LockedAt = lockedAt;
        swarm.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<List<SwarmEntity>> ListSwarmsByStateAsync(params string[] states)
    {
        await using var context = await this.contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.Swarms
            .Where(s => states.Contains(s.State))
            .ToListAsync()
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task CreateTaskAsync(TaskEntity task)
    {
        await using var context = await this.contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        context.Tasks.Add(task);
        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<List<TaskEntity>> GetTasksAsync(Guid swarmId)
    {
        await using var context = await this.contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.Tasks
            .Where(t => t.SwarmId == swarmId)
            .ToListAsync()
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<List<TaskEntity>> GetRunnableTasksAsync(Guid swarmId)
    {
        var pendingState = nameof(TaskState.Pending);
        await using var context = await this.contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.Tasks
            .Where(t => t.SwarmId == swarmId && t.State == pendingState)
            .ToListAsync()
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task UpdateTaskBlockedByAsync(
        Guid swarmId,
        string taskId,
        IReadOnlyList<string> blockedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(blockedBy);
        await using var context = await this.contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var task = await context.Tasks
            .FindAsync([swarmId, taskId], cancellationToken)
            .ConfigureAwait(false);

        if (task is null)
        {
            return;
        }

        task.BlockedByJson = JsonSerializer.Serialize(blockedBy);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task RegisterAgentAsync(AgentEntity agent)
    {
        await using var context = await this.contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        context.Agents.Add(agent);
        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<List<AgentEntity>> GetAgentsAsync(Guid swarmId)
    {
        await using var context = await this.contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.Agents
            .Where(a => a.SwarmId == swarmId)
            .ToListAsync()
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task UpdateAgentStatusAsync(Guid swarmId, string name, string status)
    {
        await using var context = await this.contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var agent = await context.Agents
            .FindAsync(swarmId, name)
            .ConfigureAwait(false);

        if (agent is not null)
        {
            agent.Status = status;
            await context.SaveChangesAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async Task SaveMessageAsync(MessageEntity message)
    {
        await using var context = await this.contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        context.Messages.Add(message);
        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<List<MessageEntity>> GetMessagesAsync(Guid swarmId)
    {
        await using var context = await this.contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.Messages
            .Where(m => m.SwarmId == swarmId)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync()
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task SaveEventAsync(EventEntity eventEntity)
    {
        await using var context = await this.contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        context.Events.Add(eventEntity);
        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<List<EventEntity>> GetEventsAsync(Guid swarmId, int? limit)
    {
        await using var context = await this.contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        // When no limit is provided, return the full event history in chronological
        // order so callers that want a full replay see the stream from the start.
        if (!limit.HasValue)
        {
            return await context.Events
                .Where(e => e.SwarmId == swarmId)
                .OrderBy(e => e.CreatedAt)
                .ToListAsync()
                .ConfigureAwait(false);
        }

        // NEWEST-N semantics: callers that pass a limit want the most recent slice of
        // the timeline — not the oldest N events. Order descending, take N, then
        // reverse back to ascending so downstream consumers (including the Batch 3
        // frontend hydration path that replays the slice through the per-swarm
        // reducer) still observe events in chronological order.
        var newest = await context.Events
            .Where(e => e.SwarmId == swarmId)
            .OrderByDescending(e => e.CreatedAt)
            .Take(limit.Value)
            .ToListAsync()
            .ConfigureAwait(false);

        newest.Reverse();
        return newest;
    }

    /// <inheritdoc/>
    public async Task SaveFileAsync(FileEntity file)
    {
        await using var context = await this.contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        context.Files.Add(file);
        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<List<FileEntity>> GetFilesAsync(Guid swarmId)
    {
        await using var context = await this.contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.Files
            .Where(f => f.SwarmId == swarmId)
            .ToListAsync()
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SwarmListEntry>> ListAllSwarmsAsync(
        int limit,
        DateTime? since,
        CancellationToken cancellationToken)
    {
        await using var context = await this.contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // 1. Load swarm rows, filtered by `since` and capped by `limit`, ordered by
        //    most-recent activity (UpdatedAt) descending. Materialize before joining
        //    to avoid provider-specific group-join issues (the four-query fan-out
        //    below is simple enough that it executes the same on both SQLite and
        //    the InMemory provider used by tests).
        var query = context.Swarms.AsQueryable();
        if (since.HasValue)
        {
            var cutoff = since.Value;
            query = query.Where(s => s.UpdatedAt >= cutoff);
        }

        var swarms = await query
            .OrderByDescending(s => s.UpdatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (swarms.Count == 0)
        {
            return Array.Empty<SwarmListEntry>();
        }

        var ids = swarms.Select(s => s.Id).ToList();

        // 2. Task counts per swarm.
        var taskCounts = await context.Tasks
            .Where(t => ids.Contains(t.SwarmId))
            .GroupBy(t => t.SwarmId)
            .Select(g => new { SwarmId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var taskCountBySwarm = taskCounts.ToDictionary(x => x.SwarmId, x => x.Count);

        // 3. Worker (agent) counts per swarm.
        var workerCounts = await context.Agents
            .Where(a => ids.Contains(a.SwarmId))
            .GroupBy(a => a.SwarmId)
            .Select(g => new { SwarmId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var workerCountBySwarm = workerCounts.ToDictionary(x => x.SwarmId, x => x.Count);

        // 4. Most-recent event timestamp per swarm (events use a nullable SwarmId).
        var latestEvents = await context.Events
            .Where(e => e.SwarmId != null && ids.Contains(e.SwarmId.Value))
            .GroupBy(e => e.SwarmId!.Value)
            .Select(g => new { SwarmId = g.Key, Max = g.Max(e => e.CreatedAt) })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var lastEventBySwarm = latestEvents.ToDictionary(x => x.SwarmId, x => x.Max);

        // 5. Stitch in memory. SQLite (and the InMemory provider) does not preserve
        //    DateTime.Kind across a round-trip, so every returned timestamp is projected
        //    through DateTime.SpecifyKind(value, DateTimeKind.Utc) to restore the
        //    expected Z-suffix serialization for downstream consumers.
        var entries = new List<SwarmListEntry>(swarms.Count);
        foreach (var swarm in swarms)
        {
            taskCountBySwarm.TryGetValue(swarm.Id, out var taskCount);
            workerCountBySwarm.TryGetValue(swarm.Id, out var workerCount);

            DateTime? lastEventAt = lastEventBySwarm.TryGetValue(swarm.Id, out var evtMax)
                ? DateTime.SpecifyKind(evtMax, DateTimeKind.Utc)
                : DateTime.SpecifyKind(swarm.UpdatedAt, DateTimeKind.Utc);

            DateTime? completedAt = swarm.CompletedAt.HasValue
                ? DateTime.SpecifyKind(swarm.CompletedAt.Value, DateTimeKind.Utc)
                : null;

            // Database rows represent swarms that are not live in memory, so
            // IsRunning is always false at the repository layer. The merger
            // overwrites this when the same id also appears in the active set.
            entries.Add(new SwarmListEntry(
                swarm.Id,
                swarm.Goal,
                swarm.TemplateKey,
                swarm.State,
                IsRunning: false,
                DateTime.SpecifyKind(swarm.CreatedAt, DateTimeKind.Utc),
                completedAt,
                lastEventAt,
                taskCount,
                workerCount));
        }

        return entries;
    }

    /// <inheritdoc/>
    public async Task<(SwarmEntity? Swarm, List<TaskEntity> Tasks, List<AgentEntity> Agents, List<MessageEntity> Messages)> LoadSwarmStateAsync(Guid swarmId)
    {
        await using var context = await this.contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var swarm = await context.Swarms
            .FindAsync(swarmId)
            .ConfigureAwait(false);

        if (swarm is null)
        {
            return (null, [], [], []);
        }

        var tasks = await context.Tasks
            .Where(t => t.SwarmId == swarmId)
            .ToListAsync()
            .ConfigureAwait(false);
        var agents = await context.Agents
            .Where(a => a.SwarmId == swarmId)
            .ToListAsync()
            .ConfigureAwait(false);
        var messages = await context.Messages
            .Where(m => m.SwarmId == swarmId)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync()
            .ConfigureAwait(false);

        return (swarm, tasks, agents, messages);
    }

    /// <inheritdoc/>
    public async Task<SwarmStateTransition?> GetLatestSwarmTransitionAsync(Guid swarmId, CancellationToken cancellationToken = default)
    {
        await using var context = await this.contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.SwarmStateTransitions
            .AsNoTracking()
            .Where(t => t.SwarmId == swarmId)
            .OrderByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
