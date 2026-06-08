using Swarmwright.Events;
using Swarmwright.Events.AgUI;
using Swarmwright.Models.Enums;
using Swarmwright.Orchestration;

namespace Swarmwright.Hosting;

/// <summary>
/// Tracks a single swarm execution including its cancellation token, event bus, and running task.
/// </summary>
public class SwarmExecution : IDisposable
{
    /// <summary>Gets the unique identifier for the swarm.</summary>
    public required Guid SwarmId { get; init; }

    /// <summary>Gets the user-provided goal for the swarm.</summary>
    public required string Goal { get; init; }

    /// <summary>Gets the optional template key used to configure the swarm.</summary>
    public string? TemplateKey { get; init; }

    /// <summary>
    /// Gets the UTC timestamp at which this execution was created. Used by the
    /// <c>GET /api/swarm/</c> list endpoint to surface a non-null creation time
    /// for active swarms that have not yet been persisted to the database.
    /// </summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Gets or sets a value indicating whether the swarm's orchestrator has
    /// finished running (successfully, cancelled, or failed). The <c>/stream</c>
    /// endpoint reads this flag to decide when to close the SSE connection;
    /// the dispatcher sets it once <see cref="ISwarmOrchestrator.RunAsync"/>
    /// returns.
    /// </summary>
    public bool IsTerminal { get; set; }

    /// <summary>
    /// Gets or sets the terminal state the swarm reached, populated by the
    /// orchestrator alongside the corresponding state-transition write.
    /// Consumers awaiting <c>WaitForCompletionAsync</c> read this to
    /// distinguish <see cref="SwarmInstanceState.Complete"/> /
    /// <see cref="SwarmInstanceState.Failed"/> /
    /// <see cref="SwarmInstanceState.Cancelled"/> without a second database round-trip.
    /// </summary>
    public SwarmInstanceState? FinalState { get; set; }

    /// <summary>
    /// Gets or sets the optional human-readable failure reason populated when
    /// <see cref="FinalState"/> is <see cref="SwarmInstanceState.Failed"/>.
    /// Mirrors the audit-row note recorded by the orchestrator's failure path.
    /// </summary>
    public string? FailureReason { get; set; }

    /// <summary>Gets or sets the orchestrator driving the swarm lifecycle.</summary>
    public ISwarmOrchestrator? Orchestrator { get; set; }

    /// <summary>Gets or sets the task representing the running swarm.</summary>
    public Task? RunningTask { get; set; }

    /// <summary>Gets the cancellation token source for this swarm execution.</summary>
    public required CancellationTokenSource Cts { get; init; }

    /// <summary>Gets the per-swarm event bus for this execution.</summary>
    public required ISwarmEventBus EventBus { get; init; }

    /// <summary>Gets the per-swarm AG-UI event adapter for this execution.</summary>
    public required SwarmEventAdapter AgUiAdapter { get; init; }

    /// <summary>
    /// Gets the per-swarm work directory where agents read and write file artifacts.
    /// Created on swarm creation by <c>SwarmManager</c>.
    /// </summary>
    public required string WorkDirectory { get; init; }

    /// <summary>
    /// Gets the optional free-form key/value context supplied at swarm creation.
    /// Defaults to an empty dictionary. Persisted as <c>ContextJson</c> and
    /// rehydrated on resume so it survives eviction; surfaced to custom tool
    /// providers via the scoped <c>ISwarmRunContext</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string> Context { get; init; }
        = new Dictionary<string, string>();

    /// <summary>
    /// Gets or sets the optional event logger that persists bus events to the database.
    /// </summary>
    public SwarmEventLogger? EventLogger { get; set; }

    /// <summary>Gets a value indicating whether the swarm is currently running.</summary>
    public bool IsRunning => this.RunningTask is { IsCompleted: false };

    /// <inheritdoc/>
    public void Dispose()
    {
        this.Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases unmanaged and optionally managed resources.
    /// </summary>
    /// <param name="disposing">A value indicating whether managed resources should be disposed.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            this.EventLogger?.Dispose();
            this.Cts.Dispose();
        }
    }
}
