using Swarmwright.Models.Enums;

namespace Swarmwright.Hosting.StateMachine;

/// <summary>
/// Return value from a task state transition.
/// </summary>
/// <param name="SwarmId">The owning swarm id.</param>
/// <param name="TaskId">The task id.</param>
/// <param name="FromState">The state prior to the transition.</param>
/// <param name="ToState">The state after the transition.</param>
/// <param name="Reason">The reason label recorded in history.</param>
/// <param name="Actor">The actor recorded in history.</param>
/// <param name="RetryCountAfter">The retry count after the transition (may be unchanged).</param>
/// <param name="TransitionId">The <see cref="Guid"/> of the written history row.</param>
public readonly record struct TaskStateTransitionResult(
    Guid SwarmId,
    string TaskId,
    TaskState FromState,
    TaskState ToState,
    string Reason,
    string? Actor,
    int RetryCountAfter,
    Guid TransitionId);
