using Swarmwright.Events;
using Swarmwright.Models.Enums;

namespace Swarmwright.Tests.Hosting.StateMachine;

/// <summary>
/// Null implementation of <see cref="ISwarmEmissionBroker"/> for tests
/// that exercise <see cref="Swarmwright.Hosting.StateMachine.StateTransitionService"/>
/// without caring about <c>SWARM_TASK_UPDATED</c> fan-out. The real broker
/// pulls the AG-UI adapter from <c>ISwarmManager</c> which most unit tests
/// don't stand up.
/// </summary>
internal sealed class NullEmissionBroker : ISwarmEmissionBroker
{
    public Task EmitTaskUpdatedAsync(
        Guid swarmId,
        string taskId,
        TaskState status,
        string? agentName,
        CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
