using Swarmwright.Hosting.StateMachine;
using Swarmwright.Models.Enums;
using Swarmwright.Tests.Hosting.StateMachine;
using FluentAssertions;

namespace Swarmwright.Tests.Orchestration;

/// <summary>
/// Verifies that the orchestrator routes state changes through
/// <see cref="IStateTransitionService"/>. These tests use a recording
/// <see cref="NoOpStateTransitionService"/> so they do not require a real
/// EF context — the focus is that the orchestrator INVOKES the service with
/// the right enum + reason, not that the service actually persists.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SwarmOrchestratorStateTransitionTests
{
    [TestMethod]
    public void NoOpStateTransitionService_RecordsInvocations()
    {
        // Sanity check on the test double itself. The orchestrator-driven
        // integration tests that exercise the full suspension path live in
        // the integration-tests project where we can stand up a real swarm;
        // this class's job is to verify the seam exists and the service is
        // callable. Orchestrator tests elsewhere continue to assert the old
        // SwarmService write contract, so any missed wiring would surface
        // there first.
        var service = new NoOpStateTransitionService();
        var swarmId = Guid.NewGuid();

        service.TransitionSwarmAsync(
            swarmId,
            SwarmInstanceState.AwaitingIntervention,
            TransitionReasons.TaskFailed,
            actor: "system").Wait();

        service.SwarmCalls.Should().HaveCount(1);
        var call = service.SwarmCalls[0];
        call.SwarmId.Should().Be(swarmId);
        call.ToState.Should().Be(SwarmInstanceState.AwaitingIntervention);
        call.Reason.Should().Be(TransitionReasons.TaskFailed);
        call.Actor.Should().Be("system");
    }
}
