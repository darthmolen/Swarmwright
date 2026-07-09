using Swarmwright.Models.Enums;

namespace Swarmwright.Hosting;

/// <summary>
/// Defines the contract for managing swarm executions.
/// </summary>
public interface ISwarmManager
{
    /// <summary>
    /// Fires once per swarm reaching a terminal state. Subscribers receive the
    /// fully-populated <see cref="SwarmExecution"/> and may take fan-in actions
    /// (logging, notification, batched aggregation) without holding a wait
    /// reference. Per-subscriber exceptions are logged and swallowed.
    /// </summary>
    /// <remarks>
    /// V1 does not support unsubscription — subscribers are intended to be
    /// process-lifetime observers (logging, metrics).
    /// </remarks>
#pragma warning disable CA1003 // Async-subscriber semantics require Func<...,Task>; EventHandler<T> is sync-void and unsuitable.
    public event Func<Guid, SwarmExecution, Task> SwarmCompleted;
#pragma warning restore CA1003

    /// <summary>
    /// Creates a new swarm execution and enqueues it for processing.
    /// </summary>
    /// <param name="goal">The user-provided goal for the swarm.</param>
    /// <param name="templateKey">The optional template key used to configure the swarm.</param>
    /// <param name="context">
    /// Optional free-form key/value context exposed to scoped custom tool
    /// providers via <c>ISwarmRunContext</c> and persisted so it survives
    /// eviction/resume. Defaults to an empty context when omitted.
    /// </param>
    /// <returns>The unique identifier of the created swarm.</returns>
    public Task<Guid> CreateSwarmAsync(
        string goal,
        string? templateKey = null,
        IReadOnlyDictionary<string, string>? context = null);

    /// <summary>
    /// Gets the swarm execution for the specified identifier, or null if not found.
    /// </summary>
    /// <param name="swarmId">The unique identifier of the swarm.</param>
    /// <returns>The swarm execution, or null if not found.</returns>
    public SwarmExecution? GetSwarm(Guid swarmId);

    /// <summary>
    /// Lists all active swarm executions.
    /// </summary>
    /// <returns>A read-only list of active swarm executions.</returns>
    public IReadOnlyList<SwarmExecution> ListActiveSwarms();

    /// <summary>
    /// Cancels the swarm with the specified identifier.
    /// </summary>
    /// <param name="swarmId">The unique identifier of the swarm to cancel.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public Task CancelSwarmAsync(Guid swarmId);

    /// <summary>
    /// Signals the swarm to continue execution after a suspension.
    /// </summary>
    /// <param name="swarmId">The unique identifier of the swarm to continue.</param>
    /// <returns><c>true</c> if a live swarm execution received the signal; <c>false</c>
    /// if no execution with that id is currently tracked (e.g. it has already
    /// completed or been evicted).</returns>
    public bool SignalContinue(Guid swarmId);

    /// <summary>
    /// Signals the swarm to skip remaining execution and proceed to synthesis.
    /// </summary>
    /// <param name="swarmId">The unique identifier of the swarm to skip.</param>
    /// <returns><c>true</c> if a live swarm execution received the signal; <c>false</c>
    /// if no execution with that id is currently tracked.</returns>
    public bool SignalSkip(Guid swarmId);

    /// <summary>
    /// Resolves the work directory for the specified swarm. Returns the in-memory
    /// execution's work directory if available, otherwise constructs the path from
    /// configuration and verifies it exists on disk.
    /// </summary>
    /// <param name="swarmId">The unique identifier of the swarm.</param>
    /// <returns>The work directory path, or null if the directory does not exist.</returns>
    public string? GetWorkDirectory(Guid swarmId);

    /// <summary>
    /// Returns the live <see cref="SwarmExecution"/> for <paramref name="swarmId"/>.
    /// If the swarm is already active, returns the existing instance. If it is
    /// evicted from memory but non-terminal in the database, registers a
    /// skeleton execution and enqueues a <see cref="SwarmRequest"/> onto the
    /// dispatcher channel so the dispatcher picks it up and calls the
    /// resume-aware <c>RunAsync</c>. Returns <see langword="null"/> when the
    /// swarm is unknown or in a terminal state.
    /// </summary>
    /// <remarks>
    /// Idempotent under concurrent callers: two threads racing on the same id
    /// both observe the same <see cref="SwarmExecution"/> and exactly one
    /// <see cref="SwarmRequest"/> is enqueued.
    /// </remarks>
    /// <param name="swarmId">The swarm to ensure live.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The live execution, or <see langword="null"/> when not recoverable.</returns>
    public Task<SwarmExecution?> EnsureLiveAsync(Guid swarmId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers a single-shot completion waiter for <paramref name="swarmId"/>.
    /// Must be called before <see cref="WaitForCompletionAsync"/>. v1 is
    /// single-waiter — duplicate registration throws
    /// <see cref="InvalidOperationException"/> with a message pointing tail
    /// observers at the <see cref="SwarmCompleted"/> event.
    /// </summary>
    /// <param name="swarmId">The swarm to register a waiter for.</param>
    public void RegisterCompletionWaiter(Guid swarmId);

    /// <summary>
    /// Returns a task that resolves when the swarm reaches a terminal state
    /// (<see cref="SwarmInstanceState.Complete"/>,
    /// <see cref="SwarmInstanceState.Failed"/>, or
    /// <see cref="SwarmInstanceState.Cancelled"/>). The returned execution carries
    /// <see cref="SwarmExecution.FinalState"/> and (when failed)
    /// <see cref="SwarmExecution.FailureReason"/> so callers can branch on the
    /// outcome without a database round-trip. Throws
    /// <see cref="InvalidOperationException"/> if no waiter is registered for
    /// <paramref name="swarmId"/>.
    /// </summary>
    /// <param name="swarmId">The swarm to await.</param>
    /// <param name="cancellationToken">Token cancelled by the caller.</param>
    /// <returns>The swarm execution at terminal state.</returns>
    public Task<SwarmExecution> WaitForCompletionAsync(Guid swarmId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a task that resolves with the next state transition for
    /// <paramref name="swarmId"/>. Each call enqueues a fresh single-shot waiter;
    /// signals that arrive while no waiter is enqueued are dropped. Used by
    /// the workflow executor alongside <see cref="WaitForCompletionAsync"/> in a
    /// <c>Task.WhenAny</c> to observe non-terminal pause states
    /// (<see cref="SwarmInstanceState.AwaitingIntervention"/> /
    /// <see cref="SwarmInstanceState.AwaitingFeedback"/> /
    /// <see cref="SwarmInstanceState.NeedsDiagnosis"/>) without taking a
    /// dependency on internal swarm types.
    /// </summary>
    /// <param name="swarmId">The swarm whose next state change is awaited.</param>
    /// <param name="cancellationToken">Token cancelled by the caller.</param>
    /// <returns>The next state the swarm transitions to.</returns>
    public Task<SwarmInstanceState> WaitForStateChangeAsync(Guid swarmId, CancellationToken cancellationToken = default);
}
