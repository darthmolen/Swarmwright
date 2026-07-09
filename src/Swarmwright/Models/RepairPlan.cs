namespace Swarmwright.Models;

/// <summary>
/// The shape captured from the leader's <c>repair_plan_after_failure</c>
/// tool call during Smart Continue. The leader inspects the failed tasks
/// and proposes a surgical rewrite of the plan.
/// </summary>
/// <param name="ResetTaskIds">Failed task ids to flip back to Pending without consuming retry budget (editorial retry).</param>
/// <param name="AddTasks">New tasks to append to the plan.</param>
/// <param name="AbandonTaskIds">Failed task ids to leave terminal and strip from any surviving task's <c>blocked_by</c> chain.</param>
/// <param name="Note">Free-form rationale recorded on the transition history row.</param>
public sealed record RepairPlan(
    IReadOnlyList<string> ResetTaskIds,
    IReadOnlyList<RepairTaskSpec> AddTasks,
    IReadOnlyList<string> AbandonTaskIds,
    string? Note);
