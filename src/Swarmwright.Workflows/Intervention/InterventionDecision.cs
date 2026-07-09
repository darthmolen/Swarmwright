namespace Swarmwright.Workflows.Intervention;

/// <summary>
/// Decision returned by <see cref="IInterventionPolicy.DecideAsync"/> when
/// the executor observes a routed pause state
/// (<c>AwaitingIntervention</c> / <c>AwaitingFeedback</c>). The executor
/// translates each value into the right mechanism call —
/// <c>ISwarmInterventionHandler</c> for the intervention-handler decisions,
/// <c>ISwarmManager.SignalContinue</c>/<c>SignalSkip</c> for the
/// awaiting-feedback path. <c>NeedsDiagnosis</c> is hardcoded to bail in v1
/// and never flows through the policy.
/// </summary>
public enum InterventionDecision
{
    /// <summary>
    /// Continue the swarm without recovery — bumps retry_count, moves Failed
    /// tasks back to Executing. Maps to
    /// <c>ISwarmInterventionHandler.ContinueAsync</c> for
    /// <c>AwaitingIntervention</c>; maps to <c>ISwarmManager.SignalContinue</c>
    /// for <c>AwaitingFeedback</c>.
    /// </summary>
    Continue,

    /// <summary>
    /// Continue with leader-driven repair plan (reset + add + abandon). Only
    /// valid for <c>AwaitingIntervention</c>; maps to
    /// <c>ISwarmInterventionHandler.SmartContinueAsync</c>.
    /// </summary>
    SmartContinue,

    /// <summary>
    /// Force the swarm to synthesize from current task state without further
    /// recovery attempts. Maps to <c>ISwarmInterventionHandler.SkipAsync</c>
    /// for <c>AwaitingIntervention</c>; maps to <c>ISwarmManager.SignalSkip</c>
    /// for <c>AwaitingFeedback</c>.
    /// </summary>
    Skip,

    /// <summary>
    /// Stop waiting and surface a typed failure to the workflow caller. The
    /// executor throws <c>SwarmInterventionBailedException</c> and does not
    /// invoke any handler.
    /// </summary>
    Bail,
}
