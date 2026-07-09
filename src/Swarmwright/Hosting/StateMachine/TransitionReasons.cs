namespace Swarmwright.Hosting.StateMachine;

/// <summary>
/// Canonical reason labels recorded on transition history rows. Keeping
/// these as string constants keeps the reason column query-friendly without
/// a second enum that duplicates the set of code paths.
/// </summary>
public static class TransitionReasons
{
    /// <summary>System-driven phase advance during normal orchestration.</summary>
    public const string PhaseAdvanced = "phase_advanced";

    /// <summary>Task failed during execution.</summary>
    public const string TaskFailed = "task_failed";

    /// <summary>User clicked Continue; retry_count consumed.</summary>
    public const string UserContinue = "user_continue";

    /// <summary>User clicked Smart Continue.</summary>
    public const string UserSmartContinue = "user_smart_continue";

    /// <summary>
    /// User clicked Smart Continue on a swarm with zero failed tasks. The handler
    /// short-circuits directly to Executing without invoking the leader advisor —
    /// there's nothing to repair, only pending/blocked work to dispatch.
    /// </summary>
    public const string UserSmartContinueNoFailures = "user_smart_continue_no_failures";

    /// <summary>Orchestrator auto-invoked Smart Continue within budget.</summary>
    public const string AutoSmartContinue = "auto_smart_continue";

    /// <summary>
    /// User Continue reset an orphan <c>InProgress</c> task back to <c>Pending</c>.
    /// Used when the task was stuck from a crashed orchestrator run — the worker
    /// never completed, so <c>retry_count</c> is NOT bumped (not a budget charge).
    /// Defense-in-depth Layer 2: even when upstream catch blocks missed the
    /// cleanup, Continue recovers the orphan silently. Distinct from
    /// <see cref="UserContinue"/> so audit-row filters can separate
    /// crash-cleanup resets from operator-driven retries.
    /// </summary>
    public const string OrphanResume = "orphan_resume";

    /// <summary>Leader's <c>repair_plan_after_failure</c> tool rewrote the plan.</summary>
    public const string LeaderRepairPlan = "leader_repair_plan";

    /// <summary>No recovery budget remaining; swarm escalates to needs_diagnosis.</summary>
    public const string BudgetExhausted = "budget_exhausted";

    /// <summary>User clicked Force Synthesis.</summary>
    public const string UserSkip = "user_skip";

    /// <summary>User cancelled the swarm.</summary>
    public const string UserCancel = "user_cancel";

    /// <summary>
    /// User clicked Manual Recover on a Failed swarm, flipping its state
    /// to <c>AwaitingIntervention</c>. Pure state transition — does not
    /// resume the orchestrator. Meta-action that bypasses the normal
    /// terminal-state rule so transient failures (infrastructure errors
    /// captured by the orchestrator's catch-all) can be walked back to
    /// the intervention decision table, where the operator then picks a
    /// recovery action.
    /// </summary>
    public const string UserMarkForIntervention = "user_mark_for_intervention";

    /// <summary>Swarm run started.</summary>
    public const string RunStarted = "run_started";

    /// <summary>Swarm run completed successfully.</summary>
    public const string RunCompleted = "run_completed";

    /// <summary>Swarm run failed unrecoverably.</summary>
    public const string RunFailed = "run_failed";

    /// <summary>Diagnose lock acquired.</summary>
    public const string LockAcquired = "lock_acquired";

    /// <summary>Diagnose lock released explicitly.</summary>
    public const string LockReleased = "lock_released";

    /// <summary>Diagnose lock stolen from another holder.</summary>
    public const string LockStolen = "lock_stolen";

    /// <summary>Diagnose lock released via stale timeout.</summary>
    public const string LockExpired = "lock_expired";

    /// <summary>
    /// Surviving task promoted from Blocked to Pending after the leader
    /// abandoned an upstream dependency; retry_count is unchanged.
    /// </summary>
    public const string AbandonedDepStripped = "abandoned_dep_stripped";
}
