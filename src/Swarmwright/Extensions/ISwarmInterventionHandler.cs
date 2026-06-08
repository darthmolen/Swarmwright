namespace Swarmwright.Extensions;

/// <summary>
/// Transport-agnostic logic for the swarm intervention endpoints
/// (<c>/continue</c>, <c>/smart-continue</c>, <c>/skip</c>, <c>/cancel</c>,
/// <c>/lock</c>). Minimal-API bindings translate the returned
/// <see cref="InterventionResult"/> records into HTTP responses; unit tests
/// inspect the record directly without spinning up a test server.
/// </summary>
public interface ISwarmInterventionHandler
{
    /// <summary>
    /// Handles <c>POST /api/swarm/{id}/continue</c>. Bumps retry_count on
    /// every eligible Failed task and transitions the swarm back to
    /// <c>Executing</c>. Releases any lock the caller currently holds,
    /// atomically with the state change.
    /// </summary>
    /// <param name="swarmId">The target swarm.</param>
    /// <param name="actor">The resolved caller identity stamped on audit rows.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A <see cref="InterventionResult"/> describing the HTTP response.</returns>
    public Task<InterventionResult> ContinueAsync(
        Guid swarmId,
        string? actor,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Handles <c>POST /api/swarm/{id}/smart-continue</c>. Invokes the
    /// leader to produce a repair plan, applies it (reset + add + abandon),
    /// and transitions the swarm back to <c>Executing</c>. Atomic lock
    /// release happens when the caller holds the diagnose lock.
    /// </summary>
    /// <param name="swarmId">The target swarm.</param>
    /// <param name="actor">The resolved caller identity stamped on audit rows.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A <see cref="InterventionResult"/> describing the HTTP response.</returns>
    public Task<InterventionResult> SmartContinueAsync(
        Guid swarmId,
        string? actor,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Handles <c>POST /api/swarm/{id}/skip</c> (Force Synthesis).
    /// </summary>
    /// <param name="swarmId">The target swarm.</param>
    /// <param name="actor">The resolved caller identity stamped on audit rows.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A <see cref="InterventionResult"/> describing the HTTP response.</returns>
    public Task<InterventionResult> SkipAsync(
        Guid swarmId,
        string? actor,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Handles <c>POST /api/swarm/{id}/cancel</c>.
    /// </summary>
    /// <param name="swarmId">The target swarm.</param>
    /// <param name="actor">The resolved caller identity stamped on audit rows.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A <see cref="InterventionResult"/> describing the HTTP response.</returns>
    public Task<InterventionResult> CancelAsync(
        Guid swarmId,
        string? actor,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Handles <c>POST /api/swarm/{id}/lock</c>. Acquires the diagnose
    /// lock on behalf of <paramref name="actor"/>. Idempotent when the
    /// caller already holds the lock; returns 423 Locked when held by
    /// someone else, unless <paramref name="steal"/> is <see langword="true"/>.
    /// </summary>
    /// <param name="swarmId">The target swarm.</param>
    /// <param name="actor">The caller identity acquiring the lock.</param>
    /// <param name="steal">When <see langword="true"/>, bypasses the 423 holder check.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A <see cref="InterventionResult"/> describing the HTTP response.</returns>
    public Task<InterventionResult> LockAsync(
        Guid swarmId,
        string? actor,
        bool steal,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Handles <c>DELETE /api/swarm/{id}/lock</c>. Releases the lock when
    /// <paramref name="actor"/> is the current holder. Returns 204 No
    /// Content on success or when the swarm was already unlocked; 403
    /// Forbidden when a non-holder tries to release.
    /// </summary>
    /// <param name="swarmId">The target swarm.</param>
    /// <param name="actor">The caller identity releasing the lock.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A <see cref="InterventionResult"/> describing the HTTP response.</returns>
    public Task<InterventionResult> UnlockAsync(
        Guid swarmId,
        string? actor,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Handles <c>POST /api/swarm/{id}/mark-as-awaiting-intervention</c>.
    /// Flips a <c>Failed</c> swarm's state to <c>AwaitingIntervention</c>
    /// so the operator can choose a recovery strategy (Continue / Smart
    /// Continue / Force Synthesis / Cancel) from the standard intervention
    /// UI. This is a pure state transition — it does <b>not</b> re-enqueue
    /// the swarm on the dispatcher; the orchestrator stays asleep until
    /// the operator explicitly picks a recovery action. Forward-copies the
    /// most recent transition's note into the new audit row as
    /// <c>"Recovered from: &lt;original&gt;"</c>. Valid only when the swarm
    /// is currently in <c>Failed</c>; 409 for any other state. Unlocked —
    /// no diagnose lock required.
    /// </summary>
    /// <param name="swarmId">The target swarm.</param>
    /// <param name="actor">The resolved caller identity stamped on audit rows.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A <see cref="InterventionResult"/> describing the HTTP response.</returns>
    public Task<InterventionResult> MarkAsAwaitingInterventionAsync(
        Guid swarmId,
        string? actor,
        CancellationToken cancellationToken = default);
}
