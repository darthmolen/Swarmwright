using Swarmwright.Database.Models;
using Swarmwright.Models;

namespace Swarmwright.Extensions;

/// <summary>
/// Invokes the leader chat client with a <c>repair_plan_after_failure</c>
/// tool and returns the leader's proposed repair. Called by the
/// <c>POST /api/swarm/{id}/smart-continue</c> endpoint handler.
/// </summary>
public interface ILeaderRepairAdvisor
{
    /// <summary>
    /// Asks the leader to propose a repair plan for the given swarm's
    /// failed tasks. Implementations build a prompt from the template's
    /// leader prompt + the failed task summaries, call the leader with
    /// the repair tool exposed, and return the captured tool arguments.
    /// </summary>
    /// <param name="swarmId">The swarm whose failed tasks need repair.</param>
    /// <param name="failedTasks">The current <c>Failed</c> task entities.</param>
    /// <param name="templateKey">Optional template key whose leader prompt drives the repair conversation.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The leader's proposed <see cref="RepairPlan"/>, or <see langword="null"/> when the leader declined / the LLM call failed.</returns>
    public Task<RepairPlan?> RequestRepairAsync(
        Guid swarmId,
        IReadOnlyList<TaskEntity> failedTasks,
        string? templateKey,
        CancellationToken cancellationToken);
}
