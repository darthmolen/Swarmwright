using Swarmwright.Models.Enums;

namespace Swarmwright.Workflows.Intervention;

/// <summary>
/// Captures the situational state a policy needs when deciding what to do
/// about a routed pause state.
/// </summary>
/// <param name="SwarmId">The swarm currently parked at <paramref name="State"/>.</param>
/// <param name="State">The pause state the swarm transitioned to. v1: <c>AwaitingIntervention</c> or <c>AwaitingFeedback</c>.</param>
/// <param name="Attempt">
/// The number of times the executor has previously delegated a decision for
/// this swarm (1 on the first visit). The executor — not the policy — owns
/// the counter so it survives across attempts within a single
/// <c>ExecuteCoreAsync</c> call and resets to zero between calls (transient
/// executor lifetime).
/// </param>
/// <param name="LastFailureReason">Optional reason the swarm reported when it parked, when available.</param>
public sealed record InterventionContext(
    System.Guid SwarmId,
    SwarmInstanceState State,
    int Attempt,
    string? LastFailureReason);
