namespace Swarmwright.Workflows.Intervention;

/// <summary>
/// Decision interface consulted by <c>SwarmExecutor&lt;TOutput&gt;</c>
/// whenever the underlying swarm transitions to a routed pause state. The
/// policy returns a decision; the executor translates the decision into
/// either an <c>ISwarmInterventionHandler</c> call (for
/// <c>AwaitingIntervention</c>) or an <c>ISwarmManager</c> signal (for
/// <c>AwaitingFeedback</c>). Implementations must be stateless — the
/// executor owns the per-dispatch attempt counter — so a single instance can
/// be registered as a singleton and shared across every workflow run.
/// </summary>
public interface IInterventionPolicy
{
    /// <summary>
    /// Returns the decision for the swarm currently parked at
    /// <paramref name="context"/>'s pause state. Implementations should throw
    /// if <c>context.State</c> is not a routed pause state — the executor
    /// short-circuits <c>NeedsDiagnosis</c> before consulting any policy and
    /// will never call this method on a terminal state.
    /// </summary>
    /// <param name="context">The situational state from the executor.</param>
    /// <param name="cancellationToken">Token cancelled by the caller.</param>
    /// <returns>The decision the executor should act on.</returns>
    public Task<InterventionDecision> DecideAsync(
        InterventionContext context,
        CancellationToken cancellationToken);
}
