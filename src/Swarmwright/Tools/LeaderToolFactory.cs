using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Swarmwright.Models;

namespace Swarmwright.Tools;

/// <summary>
/// Creates <see cref="AITool"/> instances for the leader agent, each paired with
/// a <see cref="TaskCompletionSource{T}"/> that the orchestrator awaits.
/// </summary>
public static class LeaderToolFactory
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Creates the plan tool that captures a <see cref="SwarmPlan"/> from the leader LLM.
    /// </summary>
    /// <returns>A tuple of the AI tool and the task completion source for the plan.</returns>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Tool functions must return error JSON to the LLM, not throw.")]
    public static (AITool Tool, TaskCompletionSource<SwarmPlan> PlanSource) CreatePlanTool()
    {
        var planSource = new TaskCompletionSource<SwarmPlan>();

        var tool = AIFunctionFactory.Create(
            (
                [Description("A description of the team composition and roles.")] string team_description,
                [Description("An array of task plans; each task has subject, description, workerRole, workerName, and blockedByIndices.")] JsonElement tasks) =>
            {
                try
                {
                    // LLMs call this tool with two shapes for `tasks`:
                    //   1. A JSON array directly (most models, matches the schema type):
                    //        "tasks": [{"subject":"...",...}]
                    //   2. A JSON string containing an array (some models double-encode):
                    //        "tasks": "[{\"subject\":\"...\",...}]"
                    // Accept both so we don't fail the whole planning phase on a schema quirk.
                    var taskPlans = tasks.ValueKind switch
                    {
                        JsonValueKind.Array => JsonSerializer.Deserialize<List<TaskPlan>>(tasks.GetRawText(), JsonOptions) ?? [],
                        JsonValueKind.String => JsonSerializer.Deserialize<List<TaskPlan>>(tasks.GetString() ?? "[]", JsonOptions) ?? [],
                        JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.Object
                            or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
                            => throw new JsonException(
                                $"create_plan 'tasks' must be a JSON array (or stringified JSON array); got {tasks.ValueKind}."),
                        _ => throw new JsonException(
                            $"create_plan 'tasks' has unknown JSON value kind: {tasks.ValueKind}."),
                    };

                    var plan = new SwarmPlan { TeamDescription = team_description };
                    plan.Tasks.AddRange(taskPlans);
                    planSource.TrySetResult(plan);
                    return JsonSerializer.Serialize(new { success = true, taskCount = taskPlans.Count }, JsonOptions);
                }
                catch (Exception ex)
                {
                    planSource.TrySetException(ex);
                    return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
                }
            },
            "create_plan",
            "Submits the swarm execution plan with team description and task breakdown.");

        return (tool, planSource);
    }

    /// <summary>
    /// Creates the report tool that captures the final report from the leader LLM.
    /// </summary>
    /// <returns>A tuple of the AI tool and the task completion source for the report.</returns>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Tool functions must return error JSON to the LLM, not throw.")]
    public static (AITool Tool, TaskCompletionSource<string> ReportSource) CreateReportTool()
    {
        var reportSource = new TaskCompletionSource<string>();

        var tool = AIFunctionFactory.Create(
            ([Description("The final consolidated report summarizing all work completed.")] string report) =>
            {
                try
                {
                    reportSource.TrySetResult(report);
                    return JsonSerializer.Serialize(new { success = true }, JsonOptions);
                }
                catch (Exception ex)
                {
                    reportSource.TrySetException(ex);
                    return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
                }
            },
            "submit_report",
            "Submits the final consolidated report for the swarm execution.");

        return (tool, reportSource);
    }

    /// <summary>
    /// Creates the <c>repair_plan_after_failure</c> tool used during Smart
    /// Continue. The leader calls this tool once with the set of
    /// <c>reset_tasks</c>, <c>add_tasks</c>, and <c>abandon_tasks</c> it
    /// wants to apply along with a free-form rationale note. The handler
    /// awaits the captured <see cref="RepairPlan"/>.
    /// </summary>
    /// <returns>A tuple of the AI tool and the task completion source for the repair plan.</returns>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Tool functions must return error JSON to the LLM, not throw.")]
    public static (AITool Tool, TaskCompletionSource<RepairPlan> PlanSource) CreateRepairPlanTool()
    {
        var planSource = new TaskCompletionSource<RepairPlan>();

        var tool = AIFunctionFactory.Create(
            (
                [Description("Identifiers of Failed tasks to flip back to Pending without consuming retry budget (editorial retry).")] JsonElement reset_tasks,
                [Description("Array of new task specs to append to the plan. Each element is an object with subject, description, workerRole, workerName, and optional blockedBy array.")] JsonElement add_tasks,
                [Description("Identifiers of Failed tasks to leave terminal and strip from any surviving task's blocked_by chain.")] JsonElement abandon_tasks,
                [Description("Required free-form rationale recorded on the transition audit row.")] string note) =>
            {
                try
                {
                    var resetIds = ParseStringArray(reset_tasks, "reset_tasks");
                    var abandonIds = ParseStringArray(abandon_tasks, "abandon_tasks");
                    var addSpecs = ParseRepairTaskSpecs(add_tasks);

                    if (resetIds.Count == 0 && abandonIds.Count == 0 && addSpecs.Count == 0)
                    {
                        throw new JsonException(
                            "repair_plan_after_failure must specify at least one of reset_tasks, add_tasks, or abandon_tasks.");
                    }

                    var plan = new RepairPlan(resetIds, addSpecs, abandonIds, note);
                    planSource.TrySetResult(plan);
                    return JsonSerializer.Serialize(new { success = true, resetCount = resetIds.Count, addCount = addSpecs.Count, abandonCount = abandonIds.Count }, JsonOptions);
                }
                catch (Exception ex)
                {
                    planSource.TrySetException(ex);
                    return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
                }
            },
            "repair_plan_after_failure",
            "Rewrites the current plan in response to task failures. Provide at least one of reset_tasks (task ids to retry without consuming budget), add_tasks (new task specs), or abandon_tasks (ids to skip). Include a short rationale in `note`.");

        return (tool, planSource);
    }

    private static List<string> ParseStringArray(JsonElement element, string fieldName)
    {
        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return [];
        }

        return element.ValueKind switch
        {
            JsonValueKind.Array => JsonSerializer.Deserialize<List<string>>(element.GetRawText(), JsonOptions) ?? [],
            JsonValueKind.String => JsonSerializer.Deserialize<List<string>>(element.GetString() ?? "[]", JsonOptions) ?? [],
            JsonValueKind.Object or JsonValueKind.Number or JsonValueKind.True
                or JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined
                => throw new JsonException($"{fieldName} must be a JSON array of strings; got {element.ValueKind}."),
            _ => throw new JsonException($"{fieldName} has unknown JSON value kind: {element.ValueKind}."),
        };
    }

    private static List<RepairTaskSpec> ParseRepairTaskSpecs(JsonElement element)
    {
        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return [];
        }

        var raw = element.ValueKind switch
        {
            JsonValueKind.Array => element.GetRawText(),
            JsonValueKind.String => element.GetString() ?? "[]",
            JsonValueKind.Object or JsonValueKind.Number or JsonValueKind.True
                or JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined
                => throw new JsonException($"add_tasks must be a JSON array; got {element.ValueKind}."),
            _ => throw new JsonException($"add_tasks has unknown JSON value kind: {element.ValueKind}."),
        };

        var specs = JsonSerializer.Deserialize<List<RepairTaskSpec>>(raw, JsonOptions);
        return specs ?? [];
    }

    /// <summary>
    /// Creates the begin_swarm tool that captures the refined goal from the leader LLM.
    /// </summary>
    /// <returns>A tuple of the AI tool and the task completion source for the goal.</returns>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Tool functions must return error JSON to the LLM, not throw.")]
    public static (AITool Tool, TaskCompletionSource<string> GoalSource) CreateBeginSwarmTool()
    {
        var goalSource = new TaskCompletionSource<string>();

        var tool = AIFunctionFactory.Create(
            ([Description("The refined and clarified goal for the swarm to execute.")] string refined_goal) =>
            {
                try
                {
                    goalSource.TrySetResult(refined_goal);
                    return JsonSerializer.Serialize(new { success = true }, JsonOptions);
                }
                catch (Exception ex)
                {
                    goalSource.TrySetException(ex);
                    return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
                }
            },
            "begin_swarm",
            "Begins swarm execution with a refined goal.");

        return (tool, goalSource);
    }
}
