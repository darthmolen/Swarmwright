using Swarmwright.Models.Enums;

namespace Swarmwright.Events;

/// <summary>
/// Translates a swarm task state change into a <c>SWARM_TASK_UPDATED</c>
/// AG-UI event on the target swarm's <see cref="AgUI.SwarmEventAdapter"/>.
/// Callers supply only the swarm id; the broker resolves the per-swarm
/// adapter internally, removing the need for every state-machine caller
/// to thread the adapter through its call stack.
/// </summary>
/// <remarks>
/// Registered as a singleton alongside <c>ISwarmManager</c>. The broker
/// depends on the manager's public <c>GetSwarm</c> contract so the
/// active-swarms dictionary remains encapsulated inside the manager.
/// When the swarm is not active (evicted, never registered, or already
/// disposed) the broker logs a Warning and returns — it does not throw,
/// matching the <c>IStateTransitionService</c> contract that emission
/// failures never roll back the persisted transition.
/// </remarks>
public interface ISwarmEmissionBroker
{
    /// <summary>
    /// Emits <c>SWARM_TASK_UPDATED</c> for the given task on the swarm's
    /// AG-UI adapter. No-ops with a Warning log when the swarm is not
    /// currently tracked by <see cref="Hosting.ISwarmManager"/>.
    /// </summary>
    /// <param name="swarmId">The owning swarm identifier.</param>
    /// <param name="taskId">The task whose state changed.</param>
    /// <param name="status">The new <see cref="TaskState"/>.</param>
    /// <param name="agentName">The worker name attributed to the event; surfaced on the payload and on the event's top-level agent field.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public Task EmitTaskUpdatedAsync(
        Guid swarmId,
        string taskId,
        TaskState status,
        string? agentName,
        CancellationToken cancellationToken = default);
}
