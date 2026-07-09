using System.Diagnostics;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using Swarmwright.Extensions;
using Swarmwright.Hosting;
using Swarmwright.Models.Enums;
using Swarmwright.Telemetry;
using Swarmwright.Workflows.Intervention;

namespace Swarmwright.Workflows;

/// <summary>
/// Workflow executor that wraps an <see cref="ISwarmManager"/>-driven swarm
/// dispatch as a single step in a Microsoft.Agents.AI.Workflows graph. Handles
/// dispatch (or resume), polling-free observation via the manager's
/// <c>WaitForCompletionAsync</c> + <c>WaitForStateChangeAsync</c> surface,
/// intervention-policy decisions for routed pause states, and typed
/// work-directory result mapping.
/// </summary>
/// <typeparam name="TOutput">The typed result the executor returns to the workflow graph.</typeparam>
public partial class SwarmExecutor<TOutput> : Executor<SwarmInvocationInput, TOutput>
{
    private const int DefaultTimeoutSeconds = 300;

    private static readonly ActivitySource ActivitySource = new(AgentTelemetry.SwarmWorkflowsActivitySourceName);

    private readonly ISwarmManager swarmManager;
    private readonly ISwarmInterventionHandler interventionHandler;
    private readonly IInterventionPolicy policy;
    private readonly Func<string, CancellationToken, Task<TOutput>> resultMapper;
    private readonly ILogger<SwarmExecutor<TOutput>> logger;
    private readonly int timeoutSeconds;

    /// <summary>
    /// Initializes a new instance of the <see cref="SwarmExecutor{TOutput}"/> class.
    /// </summary>
    /// <param name="id">Unique executor id.</param>
    /// <param name="logger">Diagnostic logger.</param>
    /// <param name="swarmManager">The swarm observation/control surface.</param>
    /// <param name="interventionHandler">Handles <c>SmartContinue</c>/<c>Continue</c>/<c>Skip</c> for <c>AwaitingIntervention</c> decisions.</param>
    /// <param name="policy">The intervention policy consulted on routed pause states.</param>
    /// <param name="resultMapper">Maps the swarm's work-directory contents to the typed <typeparamref name="TOutput"/>.</param>
    /// <param name="timeoutSeconds">Per-run completion-wait budget in seconds. Defaults to 300.</param>
    public SwarmExecutor(
        string id,
        ILogger<SwarmExecutor<TOutput>> logger,
        ISwarmManager swarmManager,
        ISwarmInterventionHandler interventionHandler,
        IInterventionPolicy policy,
        Func<string, CancellationToken, Task<TOutput>> resultMapper,
        int timeoutSeconds = DefaultTimeoutSeconds)
        : base(id)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(swarmManager);
        ArgumentNullException.ThrowIfNull(interventionHandler);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(resultMapper);

        this.timeoutSeconds = Math.Max(1, timeoutSeconds);
        this.swarmManager = swarmManager;
        this.interventionHandler = interventionHandler;
        this.policy = policy;
        this.resultMapper = resultMapper;
        this.logger = logger;
    }

    /// <summary>
    /// Dispatches (or resumes) the swarm and observes it to a terminal state, applying the
    /// intervention policy on routed pause states, then maps the work directory to the typed result.
    /// </summary>
    /// <param name="input">The swarm invocation input (new goal or resume id).</param>
    /// <param name="context">The workflow context.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The typed result mapped from the completed swarm's work directory.</returns>
    public override async ValueTask<TOutput> HandleAsync(
        SwarmInvocationInput input,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadlineCts.CancelAfter(TimeSpan.FromSeconds(this.timeoutSeconds));
        var waitCt = deadlineCts.Token;

        using var rootActivity = ActivitySource.StartActivity(
            AgentTelemetry.SwarmExecutorExecuteActivityName,
            ActivityKind.Internal);
        var isResume = input.ResumeSwarmId is not null;
        rootActivity?.SetTag(AgentTelemetry.SwarmIsResumeTagName, isResume);
        if (input.TemplateKey is { Length: > 0 } templateKeyForTag)
        {
            rootActivity?.SetTag(AgentTelemetry.SwarmTemplateTagName, templateKeyForTag);
        }

        Guid swarmId;
        using (var dispatchActivity = ActivitySource.StartActivity(
            AgentTelemetry.SwarmExecutorDispatchActivityName,
            ActivityKind.Internal))
        {
            dispatchActivity?.SetTag(AgentTelemetry.SwarmIsResumeTagName, isResume);

            if (input.ResumeSwarmId is { } incomingResumeId)
            {
                swarmId = incomingResumeId;
                this.LogResumingSwarm(swarmId);
            }
            else
            {
                var goalLength = input.Goal.Length;
                var templateKey = input.TemplateKey;
                swarmId = await this.swarmManager
                    .CreateSwarmAsync(input.Goal, templateKey, input.Context)
                    .ConfigureAwait(false);
                this.LogDispatchedNewSwarm(swarmId, goalLength, templateKey);
            }

            var swarmIdString = swarmId.ToString();
            dispatchActivity?.SetTag(AgentTelemetry.SwarmIdTagName, swarmIdString);
            rootActivity?.SetTag(AgentTelemetry.SwarmIdTagName, swarmIdString);
        }

        // Register and arm both wait channels BEFORE EnsureLiveAsync (resume path)
        // to close the host-restart race: if the dispatcher reaches a terminal state
        // before our wait is in place, the sink would have nothing to fire on.
        // The waits use the deadline-linked token; EnsureLiveAsync and the
        // intervention dispatches below keep the outer caller token because
        // those are operator-driven recovery actions, not subject to the
        // executor's per-run completion-wait budget.
        this.swarmManager.RegisterCompletionWaiter(swarmId);
        var completion = this.swarmManager.WaitForCompletionAsync(swarmId, waitCt);
        var stateChange = this.swarmManager.WaitForStateChangeAsync(swarmId, waitCt);
        this.LogWaitersArmed(swarmId);

        if (input.ResumeSwarmId is { } resumeId)
        {
            var live = await this.swarmManager
                .EnsureLiveAsync(resumeId, cancellationToken)
                .ConfigureAwait(false);
            if (live is null)
            {
                this.LogResumeUnrecoverable(resumeId);
                rootActivity?.SetStatus(ActivityStatusCode.Error, "Resume target unrecoverable.");
                throw new SwarmInterventionBailedException(
                    $"Resume target swarm {resumeId} is not recoverable.");
            }
        }

        var attempts = 0;
        try
        {
            while (true)
            {
                var done = await Task.WhenAny(completion, stateChange).ConfigureAwait(false);

                if (done == completion)
                {
                    var terminalExec = await completion.ConfigureAwait(false);
                    rootActivity?.SetTag(AgentTelemetry.SwarmAttemptsTagName, attempts);
                    if (terminalExec.FinalState is { } finalStateForTag)
                    {
                        rootActivity?.SetTag(AgentTelemetry.SwarmFinalStateTagName, finalStateForTag.ToString());
                    }

                    return await this.HandleTerminalAsync(terminalExec, rootActivity, cancellationToken)
                        .ConfigureAwait(false);
                }

                var observed = await stateChange.ConfigureAwait(false);
                this.LogStateChangeObserved(swarmId, observed, attempts);
                var observedTag = observed.ToString();
                rootActivity?.AddEvent(new ActivityEvent(
                    "state_change_observed",
                    tags: new ActivityTagsCollection
                    {
                        [AgentTelemetry.SwarmInterventionStateTagName] = observedTag,
                    }));

                if (observed == SwarmInstanceState.NeedsDiagnosis)
                {
                    this.LogNeedsDiagnosisHardBail(swarmId);
                    rootActivity?.SetStatus(ActivityStatusCode.Error, "NeedsDiagnosis hard bail.");
                    throw new SwarmInterventionBailedException(
                        $"Swarm {swarmId} reached NeedsDiagnosis (recovery budget exhausted).");
                }

                if (IsRoutedPauseState(observed))
                {
                    attempts++;
                    using var interventionActivity = ActivitySource.StartActivity(
                        AgentTelemetry.SwarmExecutorInterventionActivityName,
                        ActivityKind.Internal);
                    var swarmIdTagValue = swarmId.ToString();
                    interventionActivity?.SetTag(AgentTelemetry.SwarmIdTagName, swarmIdTagValue);
                    interventionActivity?.SetTag(AgentTelemetry.SwarmInterventionStateTagName, observedTag);

                    this.LogConsultingPolicy(swarmId, observed, attempts);

                    InterventionDecision decision;
                    using (var decideActivity = ActivitySource.StartActivity(
                        AgentTelemetry.SwarmExecutorPolicyDecideActivityName,
                        ActivityKind.Internal))
                    {
                        decideActivity?.SetTag(AgentTelemetry.SwarmIdTagName, swarmIdTagValue);
                        decideActivity?.SetTag(AgentTelemetry.SwarmInterventionStateTagName, observedTag);

                        var policyContext = new InterventionContext(swarmId, observed, attempts, LastFailureReason: null);
                        decision = await this.policy
                            .DecideAsync(policyContext, cancellationToken)
                            .ConfigureAwait(false);

                        var decisionTag = decision.ToString();
                        decideActivity?.SetTag(AgentTelemetry.SwarmInterventionDecisionTagName, decisionTag);
                    }

                    var decisionTagForIntervention = decision.ToString();
                    interventionActivity?.SetTag(AgentTelemetry.SwarmInterventionDecisionTagName, decisionTagForIntervention);
                    this.LogPolicyDecision(swarmId, observed, decision);

                    await this.DispatchDecisionAsync(decision, observed, swarmId, cancellationToken)
                        .ConfigureAwait(false);
                }

                // Re-arm only the state-change branch; completion stays parked.
                stateChange = this.swarmManager.WaitForStateChangeAsync(swarmId, waitCt);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation cascade: tell the swarm to cancel itself so the
            // sink's TCS resolves cleanly and the work directory is left in a
            // known state. Swallow secondary failures from the cancel call —
            // the original cancel reason is what callers care about.
            this.LogCancelCascade(swarmId);
            rootActivity?.SetTag(AgentTelemetry.SwarmAttemptsTagName, attempts);
            rootActivity?.SetStatus(ActivityStatusCode.Error, "Caller cancelled.");
            try
            {
                await this.swarmManager.CancelSwarmAsync(swarmId).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Best-effort cancel notification; original cancel must not be shadowed.
            catch (Exception cascadeFailure)
            {
                this.LogCancelCascadeFailed(swarmId, cascadeFailure);
            }
#pragma warning restore CA1031

            throw;
        }
        catch (OperationCanceledException) when (
            deadlineCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Deadline fired while the caller's token is healthy. Cascade-cancel
            // the swarm so it doesn't keep working past the deadline, then
            // surface a typed bail with a message operators can read as
            // "deadline" rather than "swarm failure" or "caller cancel".
            this.LogTimeoutCascade(swarmId, this.timeoutSeconds);
            rootActivity?.SetTag(AgentTelemetry.SwarmAttemptsTagName, attempts);
            var timeoutStatus = $"Timed out after {this.timeoutSeconds}s.";
            rootActivity?.SetStatus(ActivityStatusCode.Error, timeoutStatus);
            try
            {
                await this.swarmManager.CancelSwarmAsync(swarmId).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Best-effort cancel notification; original timeout must not be shadowed.
            catch (Exception cascadeFailure)
            {
                this.LogCancelCascadeFailed(swarmId, cascadeFailure);
            }
#pragma warning restore CA1031

            throw new SwarmInterventionBailedException(
                $"Swarm {swarmId} timed out after {this.timeoutSeconds}s; cascade-cancelled.");
        }
        catch (SwarmInterventionBailedException)
        {
            rootActivity?.SetTag(AgentTelemetry.SwarmAttemptsTagName, attempts);
            if (rootActivity?.Status == ActivityStatusCode.Unset)
            {
                rootActivity?.SetStatus(ActivityStatusCode.Error, "Bail.");
            }

            throw;
        }
    }

    private static bool IsRoutedPauseState(SwarmInstanceState state) =>
        state is SwarmInstanceState.AwaitingIntervention
            or SwarmInstanceState.AwaitingFeedback;

    private async Task<TOutput> HandleTerminalAsync(SwarmExecution exec, Activity? rootActivity, CancellationToken ct)
    {
        var swarmId = exec.SwarmId;
        var finalState = exec.FinalState;
        var workDir = exec.WorkDirectory;
        var failureReason = exec.FailureReason;

        switch (finalState)
        {
            case SwarmInstanceState.Complete:
                this.LogTerminalComplete(swarmId, workDir);
                rootActivity?.SetStatus(ActivityStatusCode.Ok);
                return await this.resultMapper(workDir, ct).ConfigureAwait(false);

            case SwarmInstanceState.Failed:
                this.LogTerminalFailed(swarmId, failureReason);
                rootActivity?.SetStatus(ActivityStatusCode.Error, failureReason ?? "Swarm failed.");
                throw new SwarmInterventionBailedException(
                    failureReason ?? "Swarm failed without a recorded reason.");

            case SwarmInstanceState.Cancelled:
                this.LogTerminalCancelled(swarmId);
                rootActivity?.SetStatus(ActivityStatusCode.Error, "Swarm cancelled.");
                throw new OperationCanceledException(
                    "Swarm reached terminal Cancelled state.",
                    ct.IsCancellationRequested ? ct : CancellationToken.None);

            default:
                throw new InvalidOperationException(
                    $"Sink resolved completion with non-terminal state {finalState}; this is a bug.");
        }
    }

    private async Task DispatchDecisionAsync(
        InterventionDecision decision,
        SwarmInstanceState observedState,
        Guid swarmId,
        CancellationToken cancellationToken)
    {
        // Decision -> mechanism mapping. AwaitingIntervention routes through
        // ISwarmInterventionHandler; AwaitingFeedback routes through
        // ISwarmManager.SignalContinue / SignalSkip — see plan Step 5 table.
        switch (decision)
        {
            case InterventionDecision.Continue:
                if (observedState is SwarmInstanceState.AwaitingFeedback)
                {
                    this.LogSignalContinueOnManager(swarmId);
                    this.swarmManager.SignalContinue(swarmId);
                }
                else
                {
                    this.LogContinueViaHandler(swarmId);
                    await this.interventionHandler
                        .ContinueAsync(swarmId, actor: "swarm-executor", cancellationToken)
                        .ConfigureAwait(false);
                }

                break;

            case InterventionDecision.SmartContinue:
                this.LogSmartContinueViaHandler(swarmId);
                await this.interventionHandler.SmartContinueAsync(swarmId, actor: "swarm-executor", cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                break;

            case InterventionDecision.Skip:
                if (observedState is SwarmInstanceState.AwaitingFeedback)
                {
                    this.LogSignalSkipOnManager(swarmId);
                    this.swarmManager.SignalSkip(swarmId);
                }
                else
                {
                    this.LogSkipViaHandler(swarmId);
                    await this.interventionHandler
                        .SkipAsync(swarmId, actor: "swarm-executor", cancellationToken)
                        .ConfigureAwait(false);
                }

                break;

            case InterventionDecision.Bail:
                this.LogPolicyBail(swarmId, observedState);
                throw new SwarmInterventionBailedException(
                    $"Policy bailed on {observedState} after {nameof(InterventionDecision.Bail)} decision.");

            default:
                throw new InvalidOperationException(
                    $"Unhandled InterventionDecision value: {decision}.");
        }
    }

    [LoggerMessage(LogLevel.Information, "SwarmExecutor dispatched new swarm {SwarmId} (goal length {GoalLength}, template {TemplateKey}).")]
    private partial void LogDispatchedNewSwarm(Guid swarmId, int goalLength, string? templateKey);

    [LoggerMessage(LogLevel.Information, "SwarmExecutor resuming swarm {SwarmId} via EnsureLiveAsync.")]
    private partial void LogResumingSwarm(Guid swarmId);

    [LoggerMessage(LogLevel.Warning, "SwarmExecutor resume target swarm {SwarmId} is unrecoverable; bailing.")]
    private partial void LogResumeUnrecoverable(Guid swarmId);

    [LoggerMessage(LogLevel.Information, "SwarmExecutor armed completion + state-change waiters for swarm {SwarmId}.")]
    private partial void LogWaitersArmed(Guid swarmId);

    [LoggerMessage(LogLevel.Information, "SwarmExecutor observed state change for swarm {SwarmId}: {NewState} (attempts so far {AttemptsSoFar}).")]
    private partial void LogStateChangeObserved(Guid swarmId, SwarmInstanceState newState, int attemptsSoFar);

    [LoggerMessage(LogLevel.Warning, "SwarmExecutor swarm {SwarmId} reached NeedsDiagnosis (recovery budget exhausted); hardcoded bail without consulting policy.")]
    private partial void LogNeedsDiagnosisHardBail(Guid swarmId);

    [LoggerMessage(LogLevel.Information, "SwarmExecutor consulting policy for swarm {SwarmId} on state {State} (attempt {Attempt}).")]
    private partial void LogConsultingPolicy(Guid swarmId, SwarmInstanceState state, int attempt);

    [LoggerMessage(LogLevel.Information, "SwarmExecutor policy decision for swarm {SwarmId} on {State}: {Decision}.")]
    private partial void LogPolicyDecision(Guid swarmId, SwarmInstanceState state, InterventionDecision decision);

    [LoggerMessage(LogLevel.Information, "SwarmExecutor swarm {SwarmId} reached terminal Complete; mapping work directory {WorkDirectory}.")]
    private partial void LogTerminalComplete(Guid swarmId, string workDirectory);

    [LoggerMessage(LogLevel.Warning, "SwarmExecutor swarm {SwarmId} reached terminal Failed: {FailureReason}.")]
    private partial void LogTerminalFailed(Guid swarmId, string? failureReason);

    [LoggerMessage(LogLevel.Information, "SwarmExecutor swarm {SwarmId} reached terminal Cancelled.")]
    private partial void LogTerminalCancelled(Guid swarmId);

    [LoggerMessage(LogLevel.Information, "SwarmExecutor invoking SmartContinueAsync on intervention handler for swarm {SwarmId}.")]
    private partial void LogSmartContinueViaHandler(Guid swarmId);

    [LoggerMessage(LogLevel.Information, "SwarmExecutor invoking ContinueAsync on intervention handler for swarm {SwarmId}.")]
    private partial void LogContinueViaHandler(Guid swarmId);

    [LoggerMessage(LogLevel.Information, "SwarmExecutor invoking SkipAsync on intervention handler for swarm {SwarmId}.")]
    private partial void LogSkipViaHandler(Guid swarmId);

    [LoggerMessage(LogLevel.Information, "SwarmExecutor invoking ISwarmManager.SignalContinue for swarm {SwarmId} (AwaitingFeedback path).")]
    private partial void LogSignalContinueOnManager(Guid swarmId);

    [LoggerMessage(LogLevel.Information, "SwarmExecutor invoking ISwarmManager.SignalSkip for swarm {SwarmId} (AwaitingFeedback path).")]
    private partial void LogSignalSkipOnManager(Guid swarmId);

    [LoggerMessage(LogLevel.Warning, "SwarmExecutor bailing on swarm {SwarmId}: policy returned Bail on {State}.")]
    private partial void LogPolicyBail(Guid swarmId, SwarmInstanceState state);

    [LoggerMessage(LogLevel.Information, "SwarmExecutor cancellation requested; sending CancelSwarmAsync for swarm {SwarmId}.")]
    private partial void LogCancelCascade(Guid swarmId);

    [LoggerMessage(LogLevel.Warning, "SwarmExecutor swarm {SwarmId} timed out after {TimeoutSeconds}s; sending CancelSwarmAsync.")]
    private partial void LogTimeoutCascade(Guid swarmId, int timeoutSeconds);

    [LoggerMessage(LogLevel.Warning, "SwarmExecutor failed to send CancelSwarmAsync for swarm {SwarmId}; original cancellation will still propagate.")]
    private partial void LogCancelCascadeFailed(Guid swarmId, Exception exception);
}
