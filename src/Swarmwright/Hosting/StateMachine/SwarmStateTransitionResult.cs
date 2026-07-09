using Swarmwright.Models.Enums;

namespace Swarmwright.Hosting.StateMachine;

/// <summary>
/// Return value from a swarm state transition. Captures both the before
/// and after state so the caller (endpoint, orchestrator, event adapter)
/// does not have to re-read the DB.
/// </summary>
/// <param name="SwarmId">The owning swarm id.</param>
/// <param name="FromState">The state prior to the transition.</param>
/// <param name="ToState">The state after the transition.</param>
/// <param name="Reason">The reason label recorded in history.</param>
/// <param name="Actor">The actor recorded in history.</param>
/// <param name="TransitionId">The <see cref="Guid"/> of the written history row.</param>
public readonly record struct SwarmStateTransitionResult(
    Guid SwarmId,
    SwarmInstanceState FromState,
    SwarmInstanceState ToState,
    string Reason,
    string? Actor,
    Guid TransitionId);
