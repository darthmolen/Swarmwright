using Swarmwright.Core;
using Swarmwright.Models;
using Swarmwright.Models.Enums;

namespace Swarmwright.Services;

/// <summary>
/// Defines the contract for the swarm service that coordinates in-memory caches
/// and optional write-through persistence.
/// </summary>
public interface ISwarmService
{
    /// <summary>Gets the unique identifier for the current swarm instance.</summary>
    public Guid SwarmId { get; }

    /// <summary>Gets the inbox system for agent messaging.</summary>
    public IInboxSystem InboxSystem { get; }

    /// <summary>Gets the team registry for tracking agents.</summary>
    public ITeamRegistry TeamRegistry { get; }

    /// <summary>Gets the current swarm state.</summary>
    public SwarmState State { get; }

    /// <summary>
    /// Creates a new swarm instance with the specified identifier and goal.
    /// </summary>
    /// <param name="swarmId">The unique swarm identifier.</param>
    /// <param name="goal">The user-provided goal for the swarm.</param>
    /// <param name="templateKey">Optional template key used to initialize the swarm.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public Task CreateSwarmAsync(Guid swarmId, string goal, string? templateKey = null);

    /// <summary>
    /// Updates the current execution round number.
    /// </summary>
    /// <param name="round">The round number.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public Task UpdateRoundAsync(int round);

    /// <summary>
    /// Adds a task to the swarm task board.
    /// </summary>
    /// <param name="task">The task to add.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public Task AddTaskAsync(SwarmTask task);

    /// <summary>
    /// Gets all tasks, optionally filtered by worker name.
    /// </summary>
    /// <param name="workerName">Optional worker name to filter by.</param>
    /// <returns>A read-only list of swarm tasks.</returns>
    public Task<IReadOnlyList<SwarmTask>> GetTasksAsync(string? workerName = null);

    /// <summary>
    /// Gets all runnable (pending) tasks, optionally filtered by worker name.
    /// </summary>
    /// <param name="workerName">Optional worker name to filter by.</param>
    /// <returns>A read-only list of runnable swarm tasks.</returns>
    public Task<IReadOnlyList<SwarmTask>> GetRunnableTasksAsync(string? workerName = null);

    /// <summary>
    /// Registers an agent in both the team registry and inbox system.
    /// </summary>
    /// <param name="agent">The agent information to register.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public Task RegisterAgentAsync(AgentInfo agent);

    /// <summary>
    /// Sends a message from one agent to another.
    /// </summary>
    /// <param name="sender">The name of the sending agent.</param>
    /// <param name="recipient">The name of the recipient agent.</param>
    /// <param name="content">The message content.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public Task SendMessageAsync(string sender, string recipient, string content);

    /// <summary>
    /// Saves a file reference with its size in bytes.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="sizeBytes">The file size in bytes.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public Task SaveFileAsync(string path, long sizeBytes);

    /// <summary>
    /// Loads a swarm from the repository, clearing and repopulating all caches.
    /// </summary>
    /// <param name="swarmId">The identifier of the swarm to load.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public Task LoadAsync(Guid swarmId);

    /// <summary>
    /// Reads the persisted <see cref="SwarmInstanceState"/> for the given
    /// swarm from the repository without touching in-memory caches.
    /// Used by the orchestrator at the top of <c>RunAsync</c> to decide
    /// whether it is starting fresh or resuming a rehydrated swarm past
    /// an earlier phase.
    /// </summary>
    /// <param name="swarmId">The swarm identifier.</param>
    /// <returns>
    /// The parsed state from the persisted row, or <see langword="null"/>
    /// when no row exists (fresh run, or a test harness backed by a bare
    /// repository mock).
    /// </returns>
    public Task<SwarmInstanceState?> GetPersistedStateAsync(Guid swarmId);

    /// <summary>
    /// Returns <see langword="true"/> when every Failed task in the current
    /// swarm has <c>retry_count &gt;= maxRetries</c>. Used by the orchestrator
    /// to decide whether an <c>AwaitingIntervention</c> state should auto-
    /// escalate to <c>NeedsDiagnosis</c>.
    /// </summary>
    /// <param name="maxRetries">The per-task Continue retry cap.</param>
    /// <returns><see langword="true"/> when no Failed task has budget remaining; <see langword="false"/> when at least one does (or when there are no Failed tasks).</returns>
    public Task<bool> IsRetryBudgetExhaustedAsync(int maxRetries);
}
