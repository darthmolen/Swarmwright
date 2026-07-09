namespace Swarmwright.Recommendation;

/// <summary>
/// Server-computed opinion about which recovery action a caller should take
/// on an actionable non-terminal swarm. Consumed by both the UI and
/// external agents (MCP) so neither has to reimplement the routing logic.
/// </summary>
/// <param name="ValidActions">
/// The full set of actions the caller may invoke — capability parity.
/// Present even when one action is recommended so external agents see the
/// complete menu.
/// </param>
/// <param name="RecommendedAction">
/// The single action the server suggests. One of
/// <c>continue</c>, <c>smart-continue</c>, <c>force-synthesis</c>,
/// <c>cancel</c>.
/// </param>
/// <param name="Rationale">
/// A one-line human-readable explanation of why this action is
/// recommended. Suitable for surfacing as a UI tooltip.
/// </param>
public sealed record SwarmContinueRecommendation(
    IReadOnlyList<string> ValidActions,
    string RecommendedAction,
    string Rationale);
