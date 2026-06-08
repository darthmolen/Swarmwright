using Microsoft.Extensions.Options;
using Swarmwright.Configuration;
using Swarmwright.Database.Models;
using Swarmwright.Database.Repositories;
using Swarmwright.Models.Enums;

namespace Swarmwright.Recommendation;

/// <summary>
/// Pure-function recommendation provider driven by a small switch table
/// over (swarm state, task states, retry counts). No LLM, no caching —
/// recomputed on every read. Adding a new rule = adding a private method
/// plus a unit test row.
/// </summary>
public sealed class DeterministicSwarmContinueProvider : IRecommendedSwarmContinueProvider
{
    private static readonly IReadOnlyList<string> AllActions =
        ["continue", "smart-continue", "force-synthesis", "cancel"];

    private readonly ISwarmRepository repository;
    private readonly IOptions<SwarmOptions> options;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeterministicSwarmContinueProvider"/> class.
    /// </summary>
    /// <param name="repository">Swarm persistence access used to read the current state + tasks.</param>
    /// <param name="options">Swarm options providing <see cref="SwarmOptions.MaxTaskRetries"/>.</param>
    public DeterministicSwarmContinueProvider(
        ISwarmRepository repository,
        IOptions<SwarmOptions> options)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(options);
        this.repository = repository;
        this.options = options;
    }

    /// <inheritdoc/>
    public async Task<SwarmContinueRecommendation?> GetRecommendationAsync(
        Guid swarmId,
        CancellationToken cancellationToken = default)
    {
        var swarm = await this.repository.GetSwarmAsync(swarmId).ConfigureAwait(false);
        if (swarm is null)
        {
            return null;
        }

        if (!TryParseActionableState(swarm.State, out _))
        {
            return null;
        }

        var tasks = await this.repository.GetTasksAsync(swarmId).ConfigureAwait(false);
        var maxRetries = this.options.Value.MaxTaskRetries;

        return Recommend(tasks, maxRetries);
    }

    private static SwarmContinueRecommendation Recommend(
        IReadOnlyList<TaskEntity> tasks,
        int maxRetries)
    {
        var failed = tasks.Where(IsState(TaskState.Failed)).ToList();
        var pending = tasks.Where(IsState(TaskState.Pending)).ToList();
        var blocked = tasks.Where(IsState(TaskState.Blocked)).ToList();
        var inProgress = tasks.Where(IsState(TaskState.InProgress)).ToList();

        // Force Synthesis only when there is truly nothing to rescue — only Completed
        // tasks exist. Failed-exhausted tasks are still "rescuable" because the leader
        // can reset them back to Pending during Smart Continue; orphan InProgress is
        // rescuable by Continue via orphan_resume (defense-in-depth Layer 3 §3a).
        // Neither kind counts as terminal-for-recommendation purposes.
        var failedWithBudget = failed.Count(t => t.RetryCount < maxRetries);
        if (pending.Count == 0 && blocked.Count == 0 && failed.Count == 0 && inProgress.Count == 0)
        {
            return Build(
                "force-synthesis",
                "No open work and no rescuable failures; Force Synthesis produces the report from Completed tasks.");
        }

        if (failed.Count > 0)
        {
            var exhausted = failed.Count - failedWithBudget;

            if (exhausted == 0)
            {
                return Build(
                    "continue",
                    $"{failed.Count} failed task(s) with retry budget remaining; Continue will retry them.");
            }

            if (failedWithBudget == 0)
            {
                return Build(
                    "smart-continue",
                    $"{failed.Count} failed task(s), all retry budget exhausted. Smart Continue required — leader must reset or abandon.");
            }

            return Build(
                "continue",
                $"{failedWithBudget} of {failed.Count} failed tasks have retry budget; Continue will retry those. Remaining failures need Smart Continue on a second pass.");
        }

        // Orphan InProgress rule (defense-in-depth Layer 3 §3b). No failed tasks
        // and no Pending, but InProgress rows exist from a crashed or cancelled
        // run — Continue resets them via orphan_resume without consuming retry
        // budget. Evaluated before the viable-Pending rule's fallthrough so the
        // operator sees the orphan-specific rationale.
        if (pending.Count == 0 && inProgress.Count > 0)
        {
            return Build(
                "continue",
                $"{inProgress.Count} orphan InProgress task(s) detected (no live worker — typically a crashed run). Continue will reset and retry without consuming retry budget.");
        }

        if (pending.Count > 0)
        {
            return Build(
                "continue",
                $"No failures. {pending.Count} Pending task(s) viable. Continue resumes the workflow.");
        }

        // No failed, no pending, no orphan InProgress — only Blocked tasks
        // remain. The dependency chain can't resolve itself; ask the leader.
        return Build(
            "smart-continue",
            $"Dependency chain stuck with {blocked.Count} Blocked task(s) and no viable Pending; Smart Continue to unblock via leader.");
    }

    private static SwarmContinueRecommendation Build(string recommendedAction, string rationale)
        => new(AllActions, recommendedAction, rationale);

    private static Func<TaskEntity, bool> IsState(TaskState state)
        => t => string.Equals(t.State, state.ToString(), StringComparison.Ordinal);

    private static bool TryParseActionableState(string persistedState, out SwarmInstanceState parsed)
    {
        if (!Enum.TryParse(persistedState, ignoreCase: false, out parsed))
        {
            return false;
        }

        return parsed is SwarmInstanceState.AwaitingIntervention
            or SwarmInstanceState.NeedsDiagnosis;
    }
}
