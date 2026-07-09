namespace Swarmwright.Models;

/// <summary>
/// A new task the leader wants to add to the plan via <c>repair_plan_after_failure</c>.
/// </summary>
/// <param name="Subject">Short task subject line.</param>
/// <param name="Description">Detailed description handed to the worker.</param>
/// <param name="WorkerRole">Worker role for assignment.</param>
/// <param name="WorkerName">Specific worker name this task binds to.</param>
/// <param name="BlockedBy">Optional list of task ids this new task depends on.</param>
public sealed record RepairTaskSpec(
    string Subject,
    string Description,
    string WorkerRole,
    string WorkerName,
    IReadOnlyList<string>? BlockedBy);
