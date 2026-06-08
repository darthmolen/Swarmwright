using Swarmwright.Models.Enums;

namespace Swarmwright.Hosting;

/// <summary>
/// Observation seam between the swarm dispatcher / state-transition service and
/// the public <see cref="ISwarmManager"/> wait surface. Decouples the dispatcher
/// from <see cref="SwarmManager"/>'s public API for testability and exposes two
/// independent observation channels:
/// <list type="bullet">
///   <item>
///     Terminal-completion: single-shot per swarm. Callers register a waiter,
///     the dispatcher signals on terminal-state, and the registered task
///     resolves with the populated <see cref="SwarmExecution"/> (carrying
///     <see cref="SwarmExecution.FinalState"/> and
///     <see cref="SwarmExecution.FailureReason"/>).
///   </item>
///   <item>
///     State-change: queued per swarm. Each call to
///     <see cref="WaitForStateChangeAsync"/> enqueues a single-shot waiter that
///     resolves on the next <see cref="SignalStateChangeAsync"/> for that swarm.
///     A signal arriving when no waiter is enqueued is dropped (callers that
///     are not yet awaiting are not the sink's concern).
///   </item>
/// </list>
/// </summary>
public interface ISwarmObservationSink
{
    /// <summary>
    /// Registers a single completion waiter for <paramref name="swarmId"/>.
    /// Throws <see cref="InvalidOperationException"/> when a waiter for the same
    /// swarm already exists — v1 is single-waiter; tail observers should
    /// subscribe to the manager's <c>SwarmCompleted</c> event instead.
    /// </summary>
    /// <param name="swarmId">The swarm to register a waiter for.</param>
    public void RegisterCompletionWaiter(Guid swarmId);

    /// <summary>
    /// Signals that <paramref name="swarmId"/> has reached a terminal state. The
    /// caller is responsible for populating <see cref="SwarmExecution.FinalState"/>
    /// and (when failed) <see cref="SwarmExecution.FailureReason"/> on
    /// <paramref name="execution"/> before invoking this method. Resolves any
    /// registered completion waiter and fires the manager's
    /// <c>SwarmCompleted</c> event.
    /// </summary>
    /// <param name="swarmId">The swarm that reached a terminal state.</param>
    /// <param name="execution">The fully-populated execution to publish.</param>
    /// <returns>A task that completes once event subscribers have been awaited.</returns>
    public Task SignalTerminalAsync(Guid swarmId, SwarmExecution execution);

    /// <summary>
    /// Returns a task that resolves when the swarm reaches a terminal state. If
    /// <see cref="RegisterCompletionWaiter"/> has not been called for
    /// <paramref name="swarmId"/>, this method throws
    /// <see cref="InvalidOperationException"/>.
    /// </summary>
    /// <param name="swarmId">The swarm to await.</param>
    /// <param name="cancellationToken">Token cancelled by the caller.</param>
    /// <returns>The swarm's execution at terminal state.</returns>
    public Task<SwarmExecution> WaitForCompletionAsync(Guid swarmId, CancellationToken cancellationToken);

    /// <summary>
    /// Registers a callback that fires once per terminal transition. Used by
    /// <see cref="SwarmManager"/> to back its public <c>SwarmCompleted</c>
    /// event without exposing the sink. Callbacks are invoked in registration
    /// order; per-callback exceptions are logged and swallowed so one bad
    /// subscriber cannot starve the rest.
    /// </summary>
    /// <param name="callback">The callback to invoke on terminal transitions.</param>
    public void OnTerminal(Func<Guid, SwarmExecution, Task> callback);

    /// <summary>
    /// Signals that <paramref name="swarmId"/> has transitioned to
    /// <paramref name="newState"/>. Wakes one queued
    /// <see cref="WaitForStateChangeAsync"/> waiter for that swarm. If no waiter
    /// is enqueued the signal is dropped — state changes that arrive while no
    /// caller is awaiting are not buffered.
    /// </summary>
    /// <param name="swarmId">The swarm that transitioned.</param>
    /// <param name="newState">The state the swarm transitioned to.</param>
    /// <returns>A task that completes once a queued waiter (if any) is signalled.</returns>
    public Task SignalStateChangeAsync(Guid swarmId, SwarmInstanceState newState);

    /// <summary>
    /// Returns a task that resolves with the next state transition for
    /// <paramref name="swarmId"/>. Each call enqueues a fresh waiter; concurrent
    /// callers each receive their own task in FIFO order.
    /// </summary>
    /// <param name="swarmId">The swarm whose next state change is awaited.</param>
    /// <param name="cancellationToken">Token cancelled by the caller.</param>
    /// <returns>The next state the swarm transitions to.</returns>
    public Task<SwarmInstanceState> WaitForStateChangeAsync(Guid swarmId, CancellationToken cancellationToken);
}
