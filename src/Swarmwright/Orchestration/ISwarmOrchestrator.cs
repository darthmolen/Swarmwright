namespace Swarmwright.Orchestration;

/// <summary>
/// Defines the contract for orchestrating a swarm lifecycle from planning through synthesis.
/// </summary>
public interface ISwarmOrchestrator
{
    /// <summary>Gets the unique identifier for the swarm instance.</summary>
    public Guid SwarmId { get; }

    /// <summary>Gets a value indicating whether the swarm has been cancelled.</summary>
    public bool IsCancelled { get; }

    /// <summary>
    /// Runs the full swarm lifecycle: planning, spawning, execution, and synthesis.
    /// </summary>
    /// <param name="swarmId">
    /// The canonical swarm identifier allocated by the dispatcher. The orchestrator
    /// adopts this id for every event, log entry, database row, and work directory
    /// so that subscribers keyed off the dispatcher's id observe the run.
    /// </param>
    /// <param name="goal">The user-provided goal for the swarm.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The final synthesized report.</returns>
    public Task<string> RunAsync(Guid swarmId, string goal, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels the swarm, stopping execution at the earliest safe point.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public Task CancelAsync();

    /// <summary>
    /// Signals the orchestrator to continue execution after a suspension.
    /// </summary>
    public void SignalContinue();

    /// <summary>
    /// Signals the orchestrator to skip remaining execution and proceed to synthesis.
    /// </summary>
    public void SignalSkip();
}
