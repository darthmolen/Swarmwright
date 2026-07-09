using Swarmwright.Database.Models;

namespace Swarmwright.Database.Repositories;

/// <summary>
/// Repository interface for swarm persistence operations.
/// </summary>
public interface ISwarmRepository
{
    /// <summary>
    /// Creates a new swarm entity.
    /// </summary>
    /// <param name="swarm">The swarm entity to persist.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task CreateSwarmAsync(SwarmEntity swarm);

    /// <summary>
    /// Gets a swarm entity by identifier.
    /// </summary>
    /// <param name="swarmId">The swarm identifier.</param>
    /// <returns>The swarm entity, or null if not found.</returns>
    public Task<SwarmEntity?> GetSwarmAsync(Guid swarmId);

    /// <summary>
    /// Updates the current round of a swarm.
    /// </summary>
    /// <param name="swarmId">The swarm identifier.</param>
    /// <param name="round">The new round number.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task UpdateRoundAsync(Guid swarmId, int round);

    /// <summary>
    /// Sets or clears the diagnose lock on a swarm. When
    /// <paramref name="lockedBy"/> is <see langword="null"/> the lock is
    /// cleared; otherwise it is acquired on behalf of the specified actor.
    /// </summary>
    /// <param name="swarmId">The swarm identifier.</param>
    /// <param name="lockedBy">The actor acquiring the lock, or <see langword="null"/> to release.</param>
    /// <param name="lockedAt">The acquisition timestamp, or <see langword="null"/> to clear.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task SetLockAsync(Guid swarmId, string? lockedBy, DateTime? lockedAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists swarms whose persisted <see cref="SwarmEntity.State"/> matches
    /// any of the supplied state names. Replaces the legacy
    /// <c>ListSwarmsByPhaseAsync</c> — the <c>phase</c> column is going
    /// away in the Phase B5 cleanup migration.
    /// </summary>
    /// <param name="states">The state names (PascalCase, from <see cref="Swarmwright.Models.Enums.SwarmInstanceState"/>) to filter by.</param>
    /// <returns>A list of matching swarm entities.</returns>
    public Task<List<SwarmEntity>> ListSwarmsByStateAsync(params string[] states);

    /// <summary>
    /// Creates a new task entity.
    /// </summary>
    /// <param name="task">The task entity to persist.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task CreateTaskAsync(TaskEntity task);

    /// <summary>
    /// Gets all tasks for a swarm.
    /// </summary>
    /// <param name="swarmId">The swarm identifier.</param>
    /// <returns>A list of task entities.</returns>
    public Task<List<TaskEntity>> GetTasksAsync(Guid swarmId);

    /// <summary>
    /// Gets all tasks for a swarm whose persisted <see cref="TaskEntity.State"/>
    /// equals the <c>Pending</c> enum name. Used by the orchestrator
    /// round dispatcher.
    /// </summary>
    /// <param name="swarmId">The swarm identifier.</param>
    /// <returns>A list of pending task entities.</returns>
    public Task<List<TaskEntity>> GetRunnableTasksAsync(Guid swarmId);

    /// <summary>
    /// Rewrites the <see cref="TaskEntity.BlockedByJson"/> for a single task.
    /// Callers use this to prune abandoned-task ids from surviving tasks'
    /// dependency chains during Smart Continue; the task's own state is
    /// untouched — promotion out of Blocked happens through the state
    /// transition service.
    /// </summary>
    /// <param name="swarmId">The owning swarm identifier.</param>
    /// <param name="taskId">The task whose dependency list is being rewritten.</param>
    /// <param name="blockedBy">The new dependency list; serialized as JSON.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task UpdateTaskBlockedByAsync(
        Guid swarmId,
        string taskId,
        IReadOnlyList<string> blockedBy,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers an agent entity.
    /// </summary>
    /// <param name="agent">The agent entity to persist.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task RegisterAgentAsync(AgentEntity agent);

    /// <summary>
    /// Gets all agents for a swarm.
    /// </summary>
    /// <param name="swarmId">The swarm identifier.</param>
    /// <returns>A list of agent entities.</returns>
    public Task<List<AgentEntity>> GetAgentsAsync(Guid swarmId);

    /// <summary>
    /// Updates the status of an agent.
    /// </summary>
    /// <param name="swarmId">The swarm identifier.</param>
    /// <param name="name">The agent name.</param>
    /// <param name="status">The new status value.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task UpdateAgentStatusAsync(Guid swarmId, string name, string status);

    /// <summary>
    /// Saves a message entity.
    /// </summary>
    /// <param name="message">The message entity to persist.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task SaveMessageAsync(MessageEntity message);

    /// <summary>
    /// Gets all messages for a swarm ordered by creation time.
    /// </summary>
    /// <param name="swarmId">The swarm identifier.</param>
    /// <returns>A list of message entities.</returns>
    public Task<List<MessageEntity>> GetMessagesAsync(Guid swarmId);

    /// <summary>
    /// Saves an event entity.
    /// </summary>
    /// <param name="eventEntity">The event entity to persist.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task SaveEventAsync(EventEntity eventEntity);

    /// <summary>
    /// Gets events for a swarm with an optional limit. When <paramref name="limit"/>
    /// is non-null, the implementation returns the NEWEST <c>N</c> events (those
    /// with the latest <c>CreatedAt</c> timestamps) but re-sorts the slice into
    /// ascending chronological order before returning, so callers can replay the
    /// returned list through a reducer without re-sorting. When <paramref name="limit"/>
    /// is null, all events are returned in chronological order.
    /// </summary>
    /// <param name="swarmId">The swarm identifier.</param>
    /// <param name="limit">The optional maximum number of events to return. When set, the newest N events are selected then returned chronologically ascending.</param>
    /// <returns>A list of event entities in chronological order.</returns>
    public Task<List<EventEntity>> GetEventsAsync(Guid swarmId, int? limit);

    /// <summary>
    /// Saves a file entity.
    /// </summary>
    /// <param name="file">The file entity to persist.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task SaveFileAsync(FileEntity file);

    /// <summary>
    /// Gets all files for a swarm.
    /// </summary>
    /// <param name="swarmId">The swarm identifier.</param>
    /// <returns>A list of file entities.</returns>
    public Task<List<FileEntity>> GetFilesAsync(Guid swarmId);

    /// <summary>
    /// Loads the full state of a swarm including tasks, agents, and messages.
    /// </summary>
    /// <param name="swarmId">The swarm identifier.</param>
    /// <returns>A tuple containing the swarm, tasks, agents, and messages.</returns>
    public Task<(SwarmEntity? Swarm, List<TaskEntity> Tasks, List<AgentEntity> Agents, List<MessageEntity> Messages)> LoadSwarmStateAsync(Guid swarmId);

    /// <summary>
    /// Returns the most recent <see cref="SwarmStateTransition"/> row for the
    /// given swarm, ordered by <see cref="SwarmStateTransition.CreatedAt"/>
    /// descending. Used by the Recover action to forward-copy the original
    /// failure's note into the new transition so the audit trail remains
    /// forensically useful.
    /// </summary>
    /// <param name="swarmId">The swarm identifier.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The newest transition row, or <see langword="null"/> when the swarm has no transitions.</returns>
    public Task<SwarmStateTransition?> GetLatestSwarmTransitionAsync(Guid swarmId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists swarms ordered by most-recent activity, joined with per-swarm task,
    /// agent, and latest-event aggregates. Returns a projection shaped for the
    /// <c>GET /api/swarm/</c> list endpoint. All timestamps on returned entries
    /// are normalized to <see cref="DateTimeKind.Utc"/> so round-tripping through
    /// providers that strip <see cref="DateTime.Kind"/> (for example, SQLite)
    /// does not silently drop the UTC designation.
    /// </summary>
    /// <param name="limit">The maximum number of swarms to return, ordered newest-first by update time.</param>
    /// <param name="since">Optional cutoff; when provided, only swarms updated on or after this UTC timestamp are returned.</param>
    /// <param name="cancellationToken">Token used to cancel the database operation.</param>
    /// <returns>A read-only list of swarm summary projections.</returns>
    public Task<IReadOnlyList<SwarmListEntry>> ListAllSwarmsAsync(
        int limit,
        DateTime? since,
        CancellationToken cancellationToken);
}
