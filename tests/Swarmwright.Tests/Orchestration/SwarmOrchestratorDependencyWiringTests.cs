using Swarmwright.Models;
using Swarmwright.Orchestration;
using FluentAssertions;

namespace Swarmwright.Tests.Orchestration;

/// <summary>
/// Unit tests for the static <see cref="SwarmOrchestrator.BuildBlockedByList"/>
/// helper that translates the leader's <c>BlockedByIndices</c> array into the
/// concrete <c>BlockedBy</c> task-id list persisted to <c>BlockedByJson</c>.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SwarmOrchestratorDependencyWiringTests
{
    /// <summary>
    /// Duplicate indices from the leader (e.g. <c>[0, 0, 1]</c>) must not produce
    /// duplicate ids in the result. <c>PromoteDependentsAsync</c> uses
    /// <c>List&lt;string&gt;.Remove</c> which strips only the first occurrence, so
    /// duplicates in the persisted list create a permanent zombie blocker and the
    /// swarm deadlocks when the upstream task completes.
    /// </summary>
    [TestMethod]
    public void BuildBlockedByList_DuplicateIndices_DeduplicatesResult()
    {
        var taskPlan = new TaskPlan { Subject = "gate", Description = "d", WorkerName = "w" };
        taskPlan.BlockedByIndices.Add(0);
        taskPlan.BlockedByIndices.Add(0);
        taskPlan.BlockedByIndices.Add(1);

        var taskIdsSoFar = new List<string> { "id-0", "id-1", "id-current" };

        var result = SwarmOrchestrator.BuildBlockedByList(taskPlan, taskIdsSoFar);

        result.Should().Equal("id-0", "id-1");
    }

    /// <summary>
    /// Indices outside the valid range (negative, equal to or beyond the current task
    /// position) must be skipped silently. The bounds check protects against the
    /// leader emitting nonsense indices.
    /// </summary>
    [TestMethod]
    public void BuildBlockedByList_OutOfRangeIndex_IsSkipped()
    {
        var taskPlan = new TaskPlan { Subject = "task", Description = "d", WorkerName = "w" };
        taskPlan.BlockedByIndices.Add(-1);
        taskPlan.BlockedByIndices.Add(5);

        var taskIdsSoFar = new List<string> { "id-0", "id-1", "id-current" };

        var result = SwarmOrchestrator.BuildBlockedByList(taskPlan, taskIdsSoFar);

        result.Should().BeEmpty();
    }

    /// <summary>
    /// An index pointing at the current task itself (the last element of
    /// <c>taskIdsSoFar</c>) must be excluded. The <c>-1</c> in the bounds check is
    /// intentional and prevents self-blocking; this test locks that invariant.
    /// </summary>
    [TestMethod]
    public void BuildBlockedByList_SelfIndex_IsExcluded()
    {
        var taskPlan = new TaskPlan { Subject = "task", Description = "d", WorkerName = "w" };

        // taskIdsSoFar[2] is "id-current" — the task currently being wired.
        taskPlan.BlockedByIndices.Add(2);

        var taskIdsSoFar = new List<string> { "id-0", "id-1", "id-current" };

        var result = SwarmOrchestrator.BuildBlockedByList(taskPlan, taskIdsSoFar);

        result.Should().BeEmpty();
    }
}
