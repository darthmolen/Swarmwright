using Swarmwright.Recommendation;

namespace Swarmwright.McpServer.Contracts;

/// <summary>
/// Phase-dependent digest for a swarm, designed to be token-efficient so a caller
/// can decide whether to drill deeper with <c>list_tasks</c>/<c>list_agents</c>/<c>read_artifact</c>.
/// </summary>
/// <param name="SwarmId">The unique swarm identifier.</param>
/// <param name="Goal">The user-supplied goal.</param>
/// <param name="TemplateKey">The template key used, if any.</param>
/// <param name="Phase">The current lifecycle phase.</param>
/// <param name="IsRunning">A value indicating whether the swarm is still running.</param>
/// <param name="Headline">One-sentence phase-appropriate headline.</param>
/// <param name="TaskCompletionRatio">Ratio of completed tasks to total (<c>0..1</c>).</param>
/// <param name="TotalTasks">Total number of tasks on the board.</param>
/// <param name="CompletedTasks">Number of tasks in <c>Completed</c> status.</param>
/// <param name="FailedTasks">Number of tasks in <c>Failed</c> or <c>Timeout</c> status.</param>
/// <param name="PrimaryArtifactPath">Path of the primary artifact (e.g., <c>synthesis-report.md</c>) when terminal, else null.</param>
/// <param name="Recommendation">
/// Server-computed opinion about the right recovery action when the swarm is in an
/// actionable non-terminal state (<c>AwaitingIntervention</c> / <c>NeedsDiagnosis</c>);
/// otherwise <see langword="null"/>. External agents should treat this as a prior,
/// not a gate — all actions in <c>ValidActions</c> remain callable. See
/// <c>continue_swarm</c>, <c>smart_continue_swarm</c>, <c>force_synthesis_swarm</c>.
/// </param>
public sealed record SwarmSummary(
    Guid SwarmId,
    string Goal,
    string? TemplateKey,
    string Phase,
    bool IsRunning,
    string Headline,
    double TaskCompletionRatio,
    int TotalTasks,
    int CompletedTasks,
    int FailedTasks,
    string? PrimaryArtifactPath,
    SwarmContinueRecommendation? Recommendation);
