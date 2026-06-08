using Swarmwright.Hosting.StateMachine;
using Swarmwright.Models.Enums;
using FluentAssertions;

namespace Swarmwright.Tests.Hosting.StateMachine;

/// <summary>
/// Covers the new <c>InProgress → Pending</c> task transition added for the
/// orphan-resume path (defense-in-depth Layer 2). The transition is narrow on
/// purpose — the code comment in <see cref="SwarmStateGuards"/> notes it is
/// only semantically valid via <see cref="TransitionReasons.OrphanResume"/>
/// from <c>SwarmInterventionHandler.ContinueAsync</c>. The guard itself does
/// not enforce reason strings; these tests pin the shape (which pairs are
/// legal) so we catch any accidental widening.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SwarmStateGuardsOrphanResumeTests
{
    [TestMethod]
    public void IsLegalTaskTransition_InProgressToPending_IsNowLegal()
    {
        SwarmStateGuards.CanTransitionTask(TaskState.InProgress, TaskState.Pending)
            .Should().BeTrue("orphan resume requires InProgress → Pending");
    }

    [TestMethod]
    public void IsLegalTaskTransition_CompletedToPending_StaysIllegal()
    {
        // Defensive: widening InProgress → Pending must not have accidentally
        // let Completed → Pending through. Completed is terminal.
        SwarmStateGuards.CanTransitionTask(TaskState.Completed, TaskState.Pending)
            .Should().BeFalse("Completed is terminal; orphan resume does not apply to Completed tasks");
    }

    [TestMethod]
    public void IsLegalTaskTransition_InProgressToBlocked_StaysIllegal()
    {
        // Defensive: the orphan path only makes sense for Pending; no other
        // InProgress-outbound transition should have been opened.
        SwarmStateGuards.CanTransitionTask(TaskState.InProgress, TaskState.Blocked)
            .Should().BeFalse("orphan resume only targets Pending; Blocked is not a valid reset target");
    }
}
