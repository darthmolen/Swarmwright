using Swarmwright.Hosting.StateMachine;
using Swarmwright.Models.Enums;
using FluentAssertions;

namespace Swarmwright.Tests.Hosting.StateMachine;

/// <summary>
/// Unit tests for the pure transition guard logic.
/// </summary>
[TestClass]
public class SwarmStateGuardsTests
{
    [TestMethod]
    public void CanTransitionSwarm_Created_To_Planning_IsLegal()
    {
        SwarmStateGuards.CanTransitionSwarm(SwarmInstanceState.Created, SwarmInstanceState.Planning)
            .Should().BeTrue();
    }

    [TestMethod]
    public void CanTransitionSwarm_Created_To_Failed_IsLegal()
    {
        // A swarm can fail before it ever reaches Planning — template
        // load errors, LLM-auth errors, or any exception thrown between
        // CreateSwarmAsync and the first Planning transition must be
        // recordable as Created -> Failed. Without this, the orchestrator's
        // catch-all can't record the failure and the swarm is stuck in
        // Created forever.
        SwarmStateGuards.CanTransitionSwarm(SwarmInstanceState.Created, SwarmInstanceState.Failed)
            .Should().BeTrue();
    }

    [TestMethod]
    public void CanTransitionSwarm_Executing_To_AwaitingIntervention_IsLegal()
    {
        SwarmStateGuards.CanTransitionSwarm(SwarmInstanceState.Executing, SwarmInstanceState.AwaitingIntervention)
            .Should().BeTrue();
    }

    [TestMethod]
    public void CanTransitionSwarm_AwaitingIntervention_To_NeedsDiagnosis_IsLegal()
    {
        SwarmStateGuards.CanTransitionSwarm(SwarmInstanceState.AwaitingIntervention, SwarmInstanceState.NeedsDiagnosis)
            .Should().BeTrue();
    }

    [TestMethod]
    public void CanTransitionSwarm_NeedsDiagnosis_To_Executing_IsLegal()
    {
        SwarmStateGuards.CanTransitionSwarm(SwarmInstanceState.NeedsDiagnosis, SwarmInstanceState.Executing)
            .Should().BeTrue();
    }

    [TestMethod]
    public void CanTransitionSwarm_NeedsDiagnosis_To_Synthesizing_IsLegal()
    {
        SwarmStateGuards.CanTransitionSwarm(SwarmInstanceState.NeedsDiagnosis, SwarmInstanceState.Synthesizing)
            .Should().BeTrue();
    }

    [TestMethod]
    public void CanTransitionSwarm_Complete_Is_Terminal()
    {
        SwarmStateGuards.CanTransitionSwarm(SwarmInstanceState.Complete, SwarmInstanceState.Executing)
            .Should().BeFalse();
        SwarmStateGuards.CanTransitionSwarm(SwarmInstanceState.Complete, SwarmInstanceState.Planning)
            .Should().BeFalse();
    }

    [TestMethod]
    public void CanTransitionSwarm_Cancelled_Is_Terminal()
    {
        SwarmStateGuards.CanTransitionSwarm(SwarmInstanceState.Cancelled, SwarmInstanceState.Executing)
            .Should().BeFalse();
    }

    [TestMethod]
    public void CanTransitionSwarm_Failed_Is_Terminal()
    {
        SwarmStateGuards.CanTransitionSwarm(SwarmInstanceState.Failed, SwarmInstanceState.Planning)
            .Should().BeFalse();
    }

    [TestMethod]
    public void CanTransitionSwarm_Planning_To_Complete_IsIllegal()
    {
        SwarmStateGuards.CanTransitionSwarm(SwarmInstanceState.Planning, SwarmInstanceState.Complete)
            .Should().BeFalse();
    }

    [TestMethod]
    public void CanTransitionSwarm_SameState_IsLegal_ForAuditRows()
    {
        SwarmStateGuards.CanTransitionSwarm(SwarmInstanceState.NeedsDiagnosis, SwarmInstanceState.NeedsDiagnosis)
            .Should().BeTrue();
    }

    [TestMethod]
    public void CanTransitionSwarm_AwaitingFeedback_Returns_To_OriginatingState()
    {
        SwarmStateGuards.CanTransitionSwarm(SwarmInstanceState.AwaitingFeedback, SwarmInstanceState.Planning)
            .Should().BeTrue();
        SwarmStateGuards.CanTransitionSwarm(SwarmInstanceState.AwaitingFeedback, SwarmInstanceState.Executing)
            .Should().BeTrue();
    }

    [TestMethod]
    public void CanTransitionTask_Failed_To_Pending_IsLegal()
    {
        SwarmStateGuards.CanTransitionTask(TaskState.Failed, TaskState.Pending)
            .Should().BeTrue();
    }

    [TestMethod]
    public void CanTransitionTask_Completed_Is_Terminal()
    {
        SwarmStateGuards.CanTransitionTask(TaskState.Completed, TaskState.Pending)
            .Should().BeFalse();
        SwarmStateGuards.CanTransitionTask(TaskState.Completed, TaskState.InProgress)
            .Should().BeFalse();
    }

    [TestMethod]
    public void CanTransitionTask_Pending_To_Completed_IsIllegal()
    {
        SwarmStateGuards.CanTransitionTask(TaskState.Pending, TaskState.Completed)
            .Should().BeFalse();
    }

    [TestMethod]
    public void CanTransitionTask_InProgress_To_AwaitingFeedback_IsLegal()
    {
        SwarmStateGuards.CanTransitionTask(TaskState.InProgress, TaskState.AwaitingFeedback)
            .Should().BeTrue();
    }

    [TestMethod]
    public void IsTerminal_Swarm_ReturnsExpected()
    {
        SwarmStateGuards.IsTerminal(SwarmInstanceState.Complete).Should().BeTrue();
        SwarmStateGuards.IsTerminal(SwarmInstanceState.Cancelled).Should().BeTrue();
        SwarmStateGuards.IsTerminal(SwarmInstanceState.Failed).Should().BeTrue();
        SwarmStateGuards.IsTerminal(SwarmInstanceState.Executing).Should().BeFalse();
    }

    [TestMethod]
    public void IsTerminal_Task_ReturnsExpected()
    {
        SwarmStateGuards.IsTerminal(TaskState.Completed).Should().BeTrue();
        SwarmStateGuards.IsTerminal(TaskState.Failed).Should().BeFalse();
        SwarmStateGuards.IsTerminal(TaskState.Pending).Should().BeFalse();
    }
}
