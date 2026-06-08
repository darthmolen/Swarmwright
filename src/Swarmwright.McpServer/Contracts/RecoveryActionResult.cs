using Swarmwright.Recommendation;

namespace Swarmwright.McpServer.Contracts;

/// <summary>
/// Result shape returned from the recovery-action MCP tools
/// (<c>continue_swarm</c>, <c>smart_continue_swarm</c>, <c>force_synthesis_swarm</c>,
/// <c>mark_swarm_awaiting_intervention</c>). Carries enough context for an external
/// agent to decide its next move without a follow-up round trip: the observed HTTP
/// status, the canonical error code on rejection, the swarm's current phase after
/// the attempt, and a fresh recommendation snapshot.
/// </summary>
/// <param name="Ok">
/// <see langword="true"/> when the handler accepted the action (HTTP 204).
/// <see langword="false"/> on any non-success response; the agent should read
/// <see cref="Code"/> and <see cref="Recommendation"/> to pick a recovery.
/// </param>
/// <param name="StatusCode">The HTTP-equivalent status code from the handler.</param>
/// <param name="Action">The recovery action invoked: <c>continue</c>, <c>smart-continue</c>, <c>force-synthesis</c>, <c>mark-awaiting-intervention</c>.</param>
/// <param name="Code">The canonical error code on rejection (<c>no_retry_budget</c>, <c>repair_failed</c>, <c>terminal_state</c>, <c>locked</c>, …) or <c>null</c> on success.</param>
/// <param name="Message">A short human-readable explanation suitable for agent reasoning or UI surfacing.</param>
/// <param name="CurrentPhase">The swarm's <see cref="Swarmwright.Models.Enums.SwarmInstanceState"/> (as a PascalCase string) observed after the attempt.</param>
/// <param name="Recommendation">A fresh recommendation snapshot computed after the attempt, or <see langword="null"/> when the swarm is not in an actionable state (e.g., the action succeeded and transitioned the swarm to <c>Executing</c>).</param>
public sealed record RecoveryActionResult(
    bool Ok,
    int StatusCode,
    string Action,
    string? Code,
    string Message,
    string CurrentPhase,
    SwarmContinueRecommendation? Recommendation);
