using Swarmwright.Database.Models;
using Swarmwright.Models.Enums;

namespace Swarmwright.Hosting.StateMachine;

/// <summary>
/// Single write surface for swarm and task state changes. Validates guards,
/// writes the new state and an audit row in one EF transaction, then returns.
/// Every successful task transition unconditionally emits
/// <c>SWARM_TASK_UPDATED</c> via the injected
/// <see cref="Events.ISwarmEmissionBroker"/>; an emission failure is logged
/// but does not roll back the persisted transition.
/// </summary>
public interface IStateTransitionService
{
    /// <summary>
    /// Transitions a swarm instance into a new state, writing the transition
    /// row and updating <c>SwarmEntity.State</c> atomically.
    /// </summary>
    /// <param name="swarmId">The target swarm.</param>
    /// <param name="toState">The requested state.</param>
    /// <param name="reason">The reason label (see <see cref="TransitionReasons"/>).</param>
    /// <param name="actor">The actor recorded on the history row.</param>
    /// <param name="note">An optional free-form note.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A <see cref="SwarmStateTransitionResult"/> describing the applied transition.</returns>
    /// <exception cref="InvalidStateTransitionException">When <see cref="SwarmStateGuards.CanTransitionSwarm"/> rejects the transition.</exception>
    /// <exception cref="InvalidOperationException">When the swarm cannot be located.</exception>
    public Task<SwarmStateTransitionResult> TransitionSwarmAsync(
        Guid swarmId,
        SwarmInstanceState toState,
        string reason,
        string? actor = null,
        string? note = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Transitions a task into a new state. <see cref="TaskEntity.RetryCount"/>
    /// is increased by <paramref name="retryCountDelta"/> in the same write.
    /// When <paramref name="result"/> is non-null the task's
    /// <see cref="TaskEntity.Result"/> column is overwritten — used by the
    /// orchestrator's terminal-state writes (Completed / Failed) so the
    /// worker's final report text reaches synthesis. When the task lands
    /// on <see cref="TaskState.Completed"/>, the service strips this id
    /// from every dependent task's <see cref="TaskEntity.BlockedByJson"/>
    /// and promotes any dependent whose list is now empty from
    /// <see cref="TaskState.Blocked"/> to <see cref="TaskState.Pending"/>
    /// in the same DB transaction (F01.3 — formerly done by
    /// SwarmService.UpdateTaskStatusAsync).
    /// </summary>
    /// <param name="swarmId">The owning swarm.</param>
    /// <param name="taskId">The task to transition.</param>
    /// <param name="toState">The requested state.</param>
    /// <param name="reason">The reason label (see <see cref="TransitionReasons"/>).</param>
    /// <param name="actor">The actor recorded on the history row.</param>
    /// <param name="retryCountDelta">
    /// Amount to add to the task's retry_count column. <c>0</c> for non-retry
    /// transitions; <c>1</c> for <c>user_continue</c>; <c>0</c> for
    /// <c>leader_repair_plan</c>. Caller owns the decision.
    /// </param>
    /// <param name="note">An optional free-form note.</param>
    /// <param name="result">
    /// Optional final result text to persist on the task row. When non-null,
    /// the value overwrites the existing <see cref="TaskEntity.Result"/>
    /// column. Pass <see langword="null"/> to leave Result untouched.
    /// </param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A <see cref="TaskStateTransitionResult"/> describing the applied transition.</returns>
    /// <exception cref="InvalidStateTransitionException">When <see cref="SwarmStateGuards.CanTransitionTask"/> rejects the transition.</exception>
    /// <exception cref="InvalidOperationException">When the task cannot be located.</exception>
    public Task<TaskStateTransitionResult> TransitionTaskAsync(
        Guid swarmId,
        string taskId,
        TaskState toState,
        string reason,
        string? actor = null,
        int retryCountDelta = 0,
        string? note = null,
        string? result = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a lock-related event (<c>lock_acquired</c>, <c>lock_released</c>,
    /// <c>lock_stolen</c>, <c>lock_expired</c>) as a transition row whose
    /// <c>from_state == to_state</c>. The swarm state does not change.
    /// </summary>
    /// <param name="swarmId">The target swarm.</param>
    /// <param name="reason">The reason label (one of the lock reasons in <see cref="TransitionReasons"/>).</param>
    /// <param name="actor">The actor recorded on the history row.</param>
    /// <param name="note">An optional free-form note.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A <see cref="SwarmStateTransitionResult"/> describing the applied transition.</returns>
    /// <exception cref="InvalidOperationException">When the swarm cannot be located.</exception>
    public Task<SwarmStateTransitionResult> RecordSwarmAuditAsync(
        Guid swarmId,
        string reason,
        string? actor = null,
        string? note = null,
        CancellationToken cancellationToken = default);
}
