namespace Swarmwright.Recommendation;

/// <summary>
/// Computes the server's opinion about the right recovery action for a swarm
/// given its current persisted state. Invoked at query time by the
/// <c>GET /api/swarm/{id}</c> endpoint (and the equivalent MCP tool) — the
/// result is not cached or persisted. Recomputing each read keeps the
/// canonical state column as the single source of truth.
/// </summary>
/// <remarks>
/// Implementations must return <see langword="null"/> when the swarm is in
/// a state where no recovery action applies — for example, when the swarm
/// is still running, has already completed, or was cancelled. The
/// actionable non-terminal states are
/// <see cref="Models.Enums.SwarmInstanceState.AwaitingIntervention"/>
/// and
/// <see cref="Models.Enums.SwarmInstanceState.NeedsDiagnosis"/>.
/// </remarks>
public interface IRecommendedSwarmContinueProvider
{
    /// <summary>
    /// Computes the recommendation for the given swarm, or returns
    /// <see langword="null"/> when the swarm is not in an actionable
    /// non-terminal state.
    /// </summary>
    /// <param name="swarmId">The swarm identifier.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>
    /// The recommendation, or <see langword="null"/> when no recovery action
    /// applies (including when the swarm does not exist).
    /// </returns>
    public Task<SwarmContinueRecommendation?> GetRecommendationAsync(
        Guid swarmId,
        CancellationToken cancellationToken = default);
}
