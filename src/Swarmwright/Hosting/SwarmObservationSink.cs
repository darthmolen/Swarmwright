using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Swarmwright.Models.Enums;

namespace Swarmwright.Hosting;

/// <summary>
/// Default in-memory implementation of <see cref="ISwarmObservationSink"/>.
/// Registered as a singleton in <c>SwarmServiceExtensions</c>; the dispatcher
/// and state-transition service inject it to publish, the manager injects it
/// to expose <c>WaitForCompletionAsync</c> / <c>WaitForStateChangeAsync</c> /
/// <c>SwarmCompleted</c>.
/// </summary>
internal sealed partial class SwarmObservationSink : ISwarmObservationSink
{
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<SwarmExecution>> completionWaiters = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentQueue<TaskCompletionSource<SwarmInstanceState>>> stateWaiters = new();
    private readonly List<Func<Guid, SwarmExecution, Task>> terminalCallbacks = new();
    private readonly Lock callbacksLock = new();
    private readonly ILogger<SwarmObservationSink> logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SwarmObservationSink"/> class.
    /// </summary>
    /// <param name="logger">Optional logger used to record terminal-callback failures.</param>
    public SwarmObservationSink(ILogger<SwarmObservationSink>? logger = null)
    {
        this.logger = logger ?? NullLogger<SwarmObservationSink>.Instance;
    }

    /// <inheritdoc/>
    public void RegisterCompletionWaiter(Guid swarmId)
    {
        var tcs = new TaskCompletionSource<SwarmExecution>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!this.completionWaiters.TryAdd(swarmId, tcs))
        {
            throw new InvalidOperationException(
                $"Swarm {swarmId} already has a pending completion waiter; subscribe to SwarmCompleted for tail observation.");
        }
    }

    /// <inheritdoc/>
    public async Task SignalTerminalAsync(Guid swarmId, SwarmExecution execution)
    {
        ArgumentNullException.ThrowIfNull(execution);

        if (this.completionWaiters.TryGetValue(swarmId, out var tcs))
        {
            tcs.TrySetResult(execution);
        }

        await this.InvokeTerminalCallbacksAsync(swarmId, execution).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<SwarmExecution> WaitForCompletionAsync(Guid swarmId, CancellationToken cancellationToken)
    {
        if (!this.completionWaiters.TryGetValue(swarmId, out var tcs))
        {
            throw new InvalidOperationException(
                $"No completion waiter registered for swarm {swarmId}; call RegisterCompletionWaiter first.");
        }

        try
        {
            return await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Cleanup happens after the wait resolves (success or cancellation) so
            // a terminal signal that arrives after the cleanup is a no-op rather
            // than a hang on a removed TCS reference.
            this.completionWaiters.TryRemove(swarmId, out _);
        }
    }

    /// <inheritdoc/>
    public void OnTerminal(Func<Guid, SwarmExecution, Task> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (this.callbacksLock)
        {
            this.terminalCallbacks.Add(callback);
        }
    }

    /// <inheritdoc/>
    public Task SignalStateChangeAsync(Guid swarmId, SwarmInstanceState newState)
    {
        if (this.stateWaiters.TryGetValue(swarmId, out var queue)
            && queue.TryDequeue(out var tcs))
        {
            tcs.TrySetResult(newState);
        }

        // Signals that arrive while no waiter is enqueued are dropped — v1 contract.
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<SwarmInstanceState> WaitForStateChangeAsync(Guid swarmId, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<SwarmInstanceState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = this.stateWaiters.GetOrAdd(
            swarmId,
            static _ => new ConcurrentQueue<TaskCompletionSource<SwarmInstanceState>>());
        queue.Enqueue(tcs);

        return tcs.Task.WaitAsync(cancellationToken);
    }

    private async Task InvokeTerminalCallbacksAsync(Guid swarmId, SwarmExecution execution)
    {
        Func<Guid, SwarmExecution, Task>[] snapshot;
        lock (this.callbacksLock)
        {
            snapshot = this.terminalCallbacks.ToArray();
        }

        foreach (var callback in snapshot)
        {
            try
            {
                await callback(swarmId, execution).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Subscriber callbacks are user code; a single throw must not starve siblings.
            catch (Exception ex)
            {
                this.LogTerminalCallbackFailed(swarmId, ex);
            }
#pragma warning restore CA1031
        }
    }

    [LoggerMessage(LogLevel.Warning, "Terminal-callback subscriber threw for swarm {SwarmId}; remaining subscribers will still fire.")]
    private partial void LogTerminalCallbackFailed(Guid swarmId, Exception exception);
}
