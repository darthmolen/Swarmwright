namespace Swarmwright.Telemetry;

/// <summary>
/// Provides the canonical OpenTelemetry <c>ActivitySource</c> names emitted by
/// Microsoft's Agent Framework and by the Swarmwright swarm layer. Hosts register these
/// names with their <c>TracerProvider</c> (see
/// <c>AddAgentFrameworkTelemetrySources</c>) to capture the corresponding
/// spans. Exposed as constants so callers reference the strings from one
/// place, letting rename-safety flow through the compiler.
/// </summary>
public static class AgentTelemetry
{
    /// <summary>
    /// Agent Framework's primary <c>ActivitySource</c> for agent spans (agent
    /// invocation, compaction, tool calls). Emits <c>gen_ai.*</c> tags.
    /// </summary>
    public const string AgentsActivitySourceName = "Experimental.Microsoft.Agents.AI";

    /// <summary>
    /// Agent Framework's <c>ActivitySource</c> for declarative workflow spans
    /// (workflow build, session, invoke, executor edges).
    /// </summary>
    public const string WorkflowsActivitySourceName = "Microsoft.Agents.AI.Workflows";

    /// <summary>
    /// Swarmwright swarm orchestration <c>ActivitySource</c>. Produces the parent
    /// span for a swarm run carrying the <c>swarm.id</c> and
    /// <c>swarm.template</c> tags so queries can pivot on a single swarm.
    /// </summary>
    public const string SwarmActivitySourceName = "Swarmwright";

    /// <summary>
    /// Swarmwright swarm-workflow bridge <c>ActivitySource</c>. Produces spans for
    /// <see cref="Type"/>s in <c>Swarmwright.Workflows</c>
    /// (currently <c>SwarmExecutor&lt;TOutput&gt;</c>). Registered alongside
    /// the swarm orchestration source so a workflow consumer can correlate
    /// "executor dispatched" → "swarm ran" via the shared
    /// <see cref="SwarmIdTagName"/>.
    /// </summary>
    public const string SwarmWorkflowsActivitySourceName = "Swarmwright.Workflows";

    /// <summary>
    /// Span name for the root swarm orchestration span.
    /// </summary>
    public const string SwarmRunActivityName = "swarm.run";

    /// <summary>
    /// Span name for the root <c>SwarmExecutor.ExecuteCoreAsync</c> span.
    /// One per workflow invocation; the orchestrator's <see cref="SwarmRunActivityName"/>
    /// runs as a sibling correlated by <see cref="SwarmIdTagName"/>.
    /// </summary>
    public const string SwarmExecutorExecuteActivityName = "swarm.executor.execute";

    /// <summary>
    /// Span name for the dispatch phase (<c>CreateSwarmAsync</c> or <c>EnsureLiveAsync</c>).
    /// </summary>
    public const string SwarmExecutorDispatchActivityName = "swarm.executor.dispatch";

    /// <summary>
    /// Span name for one intervention cycle: opens when the executor observes
    /// a routed pause state, closes when the dispatched mechanism returns.
    /// Tagged with <see cref="SwarmInterventionStateTagName"/> and
    /// <see cref="SwarmInterventionDecisionTagName"/>.
    /// </summary>
    public const string SwarmExecutorInterventionActivityName = "swarm.executor.intervention";

    /// <summary>
    /// Span name for the <c>IInterventionPolicy.DecideAsync</c> call.
    /// Tagged with the resulting <see cref="SwarmInterventionDecisionTagName"/>.
    /// </summary>
    public const string SwarmExecutorPolicyDecideActivityName = "swarm.executor.policy_decide";

    /// <summary>
    /// Tag key for the canonical swarm identifier.
    /// </summary>
    public const string SwarmIdTagName = "swarm.id";

    /// <summary>
    /// Tag key for the template key used to configure the swarm.
    /// </summary>
    public const string SwarmTemplateTagName = "swarm.template";

    /// <summary>
    /// Tag key (boolean) — true when the executor took the resume path
    /// (<c>EnsureLiveAsync</c>) instead of dispatching a fresh swarm.
    /// </summary>
    public const string SwarmIsResumeTagName = "swarm.is_resume";

    /// <summary>
    /// Tag key for the swarm pause state observed by the executor wait loop
    /// (<c>AwaitingIntervention</c>, <c>AwaitingFeedback</c>, <c>NeedsDiagnosis</c>).
    /// </summary>
    public const string SwarmInterventionStateTagName = "swarm.intervention_state";

    /// <summary>
    /// Tag key for the decision returned by an <c>IInterventionPolicy</c>
    /// (<c>Continue</c>, <c>SmartContinue</c>, <c>Skip</c>, <c>Bail</c>).
    /// </summary>
    public const string SwarmInterventionDecisionTagName = "swarm.intervention_decision";

    /// <summary>
    /// Tag key for the terminal state the swarm reached
    /// (<c>Complete</c>, <c>Failed</c>, <c>Cancelled</c>).
    /// </summary>
    public const string SwarmFinalStateTagName = "swarm.final_state";

    /// <summary>
    /// Tag key for the cumulative number of intervention attempts the
    /// executor handled before reaching a terminal state.
    /// </summary>
    public const string SwarmAttemptsTagName = "swarm.attempts";
}
