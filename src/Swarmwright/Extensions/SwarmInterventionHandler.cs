using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Swarmwright.Configuration;
using Swarmwright.Database.Models;
using Swarmwright.Database.Repositories;
using Swarmwright.Hosting;
using Swarmwright.Hosting.StateMachine;
using Swarmwright.Models;
using Swarmwright.Models.Enums;

namespace Swarmwright.Extensions;

/// <summary>
/// Transport-agnostic logic for the swarm intervention endpoints
/// (<c>/continue</c>, <c>/smart-continue</c>, <c>/skip</c>, <c>/cancel</c>,
/// <c>/lock</c>). Returns <see cref="InterventionResult"/> records that the
/// minimal-API bindings translate into HTTP responses. Registered as scoped
/// so every request gets a fresh instance aligned with the DbContext
/// lifetime owned by <see cref="ISwarmRepository"/>.
/// </summary>
public sealed partial class SwarmInterventionHandler : ISwarmInterventionHandler
{
    private readonly ISwarmManager manager;
    private readonly IStateTransitionService stateService;
    private readonly ISwarmRepository repository;
    private readonly ILeaderRepairAdvisor advisor;
    private readonly SwarmOptions options;
    private readonly ILogger<SwarmInterventionHandler> logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SwarmInterventionHandler"/> class.
    /// </summary>
    /// <param name="manager">The swarm manager for signalling the orchestrator loop.</param>
    /// <param name="stateService">The single write surface for state transitions.</param>
    /// <param name="repository">The swarm repository for entity lookups.</param>
    /// <param name="advisor">The leader repair advisor (only used by SmartContinue).</param>
    /// <param name="options">Swarm configuration snapshot (reads <c>MaxTaskRetries</c> for Continue).</param>
    /// <param name="logger">Diagnostic logger.</param>
    public SwarmInterventionHandler(
        ISwarmManager manager,
        IStateTransitionService stateService,
        ISwarmRepository repository,
        ILeaderRepairAdvisor advisor,
        IOptions<SwarmOptions> options,
        ILogger<SwarmInterventionHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(stateService);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(advisor);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        this.manager = manager;
        this.stateService = stateService;
        this.repository = repository;
        this.advisor = advisor;
        this.options = options.Value;
        this.logger = logger;
    }

    /// <inheritdoc/>
    public async Task<InterventionResult> ContinueAsync(
        Guid swarmId,
        string? actor,
        CancellationToken cancellationToken = default)
    {
        const string action = "continue";

        var entity = await this.repository.GetSwarmAsync(swarmId).ConfigureAwait(false);
        if (entity is null)
        {
            return this.Reject(
                action,
                swarmId,
                actor,
                state: null,
                lockHolder: null,
                code: "swarm_not_found",
                result: InterventionResult.NotFound("swarm_not_found", "Swarm not found."));
        }

        if (GuardTerminal(entity) is { } terminal)
        {
            return this.Reject(action, swarmId, actor, entity.State, entity.LockedBy, "terminal_state", terminal);
        }

        if (GuardLock(entity, actor) is { } locked)
        {
            return this.Reject(action, swarmId, actor, entity.State, entity.LockedBy, "locked", locked);
        }

        if (!Enum.TryParse<SwarmInstanceState>(entity.State, out var currentState))
        {
            currentState = SwarmInstanceState.Created;
        }

        if (currentState is not SwarmInstanceState.AwaitingIntervention
            and not SwarmInstanceState.NeedsDiagnosis)
        {
            return this.Reject(
                action,
                swarmId,
                actor,
                entity.State,
                entity.LockedBy,
                "invalid_transition",
                result: InterventionResult.Conflict(
                    "invalid_transition",
                    new
                    {
                        code = "invalid_transition",
                        from = entity.State,
                        to = nameof(SwarmInstanceState.Executing),
                        reason = TransitionReasons.UserContinue,
                    }));
        }

        if (currentState == SwarmInstanceState.NeedsDiagnosis)
        {
            return this.Reject(
                action,
                swarmId,
                actor,
                entity.State,
                entity.LockedBy,
                "no_retry_budget",
                result: InterventionResult.Conflict(
                    "no_retry_budget",
                    new { code = "no_retry_budget", message = "Swarm is in NeedsDiagnosis; Smart Continue is required." }));
        }

        var tasks = await this.repository.GetTasksAsync(swarmId).ConfigureAwait(false);
        var eligible = tasks
            .Where(t => string.Equals(t.State, nameof(TaskState.Failed), StringComparison.Ordinal)
                     && t.RetryCount < this.options.MaxTaskRetries)
            .ToList();
        var hasViablePending = tasks
            .Any(t => string.Equals(t.State, nameof(TaskState.Pending), StringComparison.Ordinal));
        var orphans = tasks
            .Where(t => string.Equals(t.State, nameof(TaskState.InProgress), StringComparison.Ordinal))
            .ToList();

        // Continue is a deterministic resume. It needs SOMETHING to run: a failed
        // task with budget, a pending task waiting for a worker, OR an orphan
        // InProgress task left over from a crashed run (defense-in-depth Layer 2).
        // If none of the three exists, reject — the caller should use Smart
        // Continue or Force Synthesis.
        if (eligible.Count == 0 && !hasViablePending && orphans.Count == 0)
        {
            return this.Reject(
                action,
                swarmId,
                actor,
                entity.State,
                entity.LockedBy,
                "no_retry_budget",
                result: InterventionResult.Conflict(
                    "no_retry_budget",
                    new { code = "no_retry_budget", message = "No Failed task has retry budget remaining, no viable Pending work, and no orphan InProgress tasks." }));
        }

        foreach (var t in eligible)
        {
            await this.stateService.TransitionTaskAsync(
                swarmId,
                t.Id,
                TaskState.Pending,
                TransitionReasons.UserContinue,
                actor,
                retryCountDelta: 1,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        // Orphan reset. The worker didn't complete, so retry_count stays put —
        // this is not a budget-charged user decision, it's the system recovering
        // from its own dropped in-flight work. Different reason string from
        // user_continue so audit filters can tell them apart.
        foreach (var t in orphans)
        {
            await this.stateService.TransitionTaskAsync(
                swarmId,
                t.Id,
                TaskState.Pending,
                TransitionReasons.OrphanResume,
                actor,
                retryCountDelta: 0,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await this.stateService.TransitionSwarmAsync(
                swarmId,
                SwarmInstanceState.Executing,
                TransitionReasons.UserContinue,
                actor,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidStateTransitionException)
        {
            return this.Reject(
                action,
                swarmId,
                actor,
                entity.State,
                entity.LockedBy,
                "invalid_transition",
                result: InterventionResult.Conflict(
                    "invalid_transition",
                    new { code = "invalid_transition", from = entity.State, to = nameof(SwarmInstanceState.Executing) }));
        }
        catch (SwarmConcurrencyConflictException ex)
        {
            return this.MapConcurrencyConflict(
                action,
                swarmId,
                actor,
                entity.State,
                ex);
        }

        await this.ReleaseLockIfHolderAsync(entity, actor, cancellationToken).ConfigureAwait(false);

        // Orchestrator-lifecycle handoff MUST run AFTER the state writes above
        // so that the orchestrator's LoadAsync (invoked by the dispatcher when
        // EnsureLiveAsync enqueues an evicted swarm) reads the post-handler DB
        // state. Putting this before the writes — as the endpoint used to —
        // races the DB update and leaves the orchestrator running against
        // pre-handler state. SignalContinue handles the already-running case
        // (orchestrator sitting in EnterSuspendWaitAsync); EnsureLiveAsync
        // handles the evicted case. Both paths are needed to cover every
        // swarm lifetime.
        await this.manager.EnsureLiveAsync(swarmId, cancellationToken).ConfigureAwait(false);
        this.manager.SignalContinue(swarmId);
        return this.Accept(
            action,
            swarmId,
            actor,
            entity.State,
            InterventionResult.NoContent());
    }

    /// <inheritdoc/>
    public async Task<InterventionResult> SmartContinueAsync(
        Guid swarmId,
        string? actor,
        CancellationToken cancellationToken = default)
    {
        const string action = "smart-continue";

        var entity = await this.repository.GetSwarmAsync(swarmId).ConfigureAwait(false);
        if (entity is null)
        {
            return this.Reject(
                action,
                swarmId,
                actor,
                state: null,
                lockHolder: null,
                code: "swarm_not_found",
                result: InterventionResult.NotFound("swarm_not_found", "Swarm not found."));
        }

        if (GuardTerminal(entity) is { } terminal)
        {
            return this.Reject(
                action,
                swarmId,
                actor,
                entity.State,
                entity.LockedBy,
                "terminal_state",
                terminal);
        }

        if (GuardLock(entity, actor) is { } locked)
        {
            return this.Reject(
                action,
                swarmId,
                actor,
                entity.State,
                entity.LockedBy,
                "locked",
                locked);
        }

        if (!Enum.TryParse<SwarmInstanceState>(entity.State, out var currentState))
        {
            currentState = SwarmInstanceState.Created;
        }

        if (currentState is not SwarmInstanceState.AwaitingIntervention
            and not SwarmInstanceState.NeedsDiagnosis)
        {
            return this.Reject(
                action,
                swarmId,
                actor,
                entity.State,
                entity.LockedBy,
                "invalid_transition",
                result: InterventionResult.Conflict(
                    "invalid_transition",
                    new { code = "invalid_transition", from = entity.State, to = nameof(SwarmInstanceState.Executing), reason = TransitionReasons.UserSmartContinue }));
        }

        var allTasks = await this.repository.GetTasksAsync(swarmId).ConfigureAwait(false);
        var failedTasks = allTasks
            .Where(t => string.Equals(t.State, nameof(TaskState.Failed), StringComparison.Ordinal))
            .ToList();

        // Short-circuit: zero failures + viable work is NOT a leader-repair situation.
        // Calling the advisor with nothing to repair produces a spurious null → 409
        // "Leader did not produce a repair plan" in the UI (this was the demo bug).
        // Transition to Executing directly with a dedicated audit reason so the
        // forensic trail shows a human pressed Smart Continue against a no-failure
        // swarm intentionally.
        if (failedTasks.Count == 0)
        {
            var hasOpenWork = allTasks.Any(t =>
                string.Equals(t.State, nameof(TaskState.Pending), StringComparison.Ordinal)
                || string.Equals(t.State, nameof(TaskState.Blocked), StringComparison.Ordinal));

            if (hasOpenWork)
            {
                try
                {
                    await this.stateService.TransitionSwarmAsync(
                        swarmId,
                        SwarmInstanceState.Executing,
                        TransitionReasons.UserSmartContinueNoFailures,
                        actor,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidStateTransitionException)
                {
                    return this.Reject(
                        action,
                        swarmId,
                        actor,
                        entity.State,
                        entity.LockedBy,
                        "invalid_transition",
                        result: InterventionResult.Conflict(
                            "invalid_transition",
                            new { code = "invalid_transition", from = entity.State, to = nameof(SwarmInstanceState.Executing) }));
                }
                catch (SwarmConcurrencyConflictException ex)
                {
                    return this.MapConcurrencyConflict(
                        action,
                        swarmId,
                        actor,
                        entity.State,
                        ex);
                }

                await this.ReleaseLockIfHolderAsync(entity, actor, cancellationToken).ConfigureAwait(false);
                await this.manager.EnsureLiveAsync(swarmId, cancellationToken).ConfigureAwait(false);
                this.manager.SignalContinue(swarmId);
                return this.Accept(
                    action,
                    swarmId,
                    actor,
                    entity.State,
                    InterventionResult.NoContent());
            }
        }

        RepairPlan? plan;
        try
        {
            plan = await this.advisor.RequestRepairAsync(swarmId, failedTasks, entity.TemplateKey, cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The advisor reaches out to an LLM; any failure should fold into a 409 rather than bubble.
        catch
#pragma warning restore CA1031
        {
            plan = null;
        }

        if (plan is null)
        {
            return this.Reject(
                action,
                swarmId,
                actor,
                entity.State,
                entity.LockedBy,
                "repair_failed",
                result: InterventionResult.Conflict(
                    "repair_failed",
                    new { code = "repair_failed", message = "Leader did not produce a repair plan." }));
        }

        if (plan.ResetTaskIds.Count == 0 && plan.AddTasks.Count == 0 && plan.AbandonTaskIds.Count == 0)
        {
            return this.Reject(
                action,
                swarmId,
                actor,
                entity.State,
                entity.LockedBy,
                "repair_empty",
                result: InterventionResult.Conflict(
                    "repair_empty",
                    new { code = "repair_empty", message = "Repair plan specified no reset, add, or abandon actions." }));
        }

        foreach (var taskId in plan.ResetTaskIds)
        {
            try
            {
                await this.stateService.TransitionTaskAsync(
                    swarmId,
                    taskId,
                    TaskState.Pending,
                    TransitionReasons.LeaderRepairPlan,
                    actor,
                    retryCountDelta: 0,
                    note: plan.Note,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidStateTransitionException)
            {
                // Task wasn't in Failed — the leader's plan is stale. Skip.
            }
            catch (InvalidOperationException)
            {
                // Task id the leader named is not in this swarm. Skip.
            }
        }

        foreach (var spec in plan.AddTasks)
        {
            var newId = "t-" + Guid.NewGuid().ToString("N")[..8];
            var initial = spec.BlockedBy is { Count: > 0 } ? TaskState.Blocked : TaskState.Pending;
            var blockedByJson = spec.BlockedBy is null
                ? "[]"
                : JsonSerializer.Serialize(spec.BlockedBy);

            await this.repository.CreateTaskAsync(new TaskEntity
            {
                SwarmId = swarmId,
                Id = newId,
                Subject = spec.Subject,
                Description = spec.Description,
                WorkerRole = spec.WorkerRole,
                WorkerName = spec.WorkerName,
                State = initial.ToString(),
                BlockedByJson = blockedByJson,
            }).ConfigureAwait(false);
        }

        var abandonNote = plan.AbandonTaskIds.Count > 0
            ? $"{plan.Note} [abandoned: {string.Join(",", plan.AbandonTaskIds)}]"
            : plan.Note;

        if (plan.AbandonTaskIds.Count > 0)
        {
            await this.StripAbandonedDepsAsync(
                swarmId,
                plan.AbandonTaskIds,
                actor,
                plan.Note,
                cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await this.stateService.TransitionSwarmAsync(
                swarmId,
                SwarmInstanceState.Executing,
                TransitionReasons.UserSmartContinue,
                actor,
                note: abandonNote,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidStateTransitionException)
        {
            return this.Reject(
                action,
                swarmId,
                actor,
                entity.State,
                entity.LockedBy,
                "invalid_transition",
                result: InterventionResult.Conflict(
                    "invalid_transition",
                    new { code = "invalid_transition", from = entity.State, to = nameof(SwarmInstanceState.Executing) }));
        }
        catch (SwarmConcurrencyConflictException ex)
        {
            return this.MapConcurrencyConflict(
                action,
                swarmId,
                actor,
                entity.State,
                ex);
        }

        await this.ReleaseLockIfHolderAsync(entity, actor, cancellationToken).ConfigureAwait(false);

        await this.manager.EnsureLiveAsync(swarmId, cancellationToken).ConfigureAwait(false);
        this.manager.SignalContinue(swarmId);
        return this.Accept(
            action,
            swarmId,
            actor,
            entity.State,
            InterventionResult.NoContent());
    }

    /// <inheritdoc/>
    public async Task<InterventionResult> SkipAsync(
        Guid swarmId,
        string? actor,
        CancellationToken cancellationToken = default)
    {
        const string action = "skip";

        var entity = await this.repository.GetSwarmAsync(swarmId).ConfigureAwait(false);
        if (entity is null)
        {
            return this.Reject(
                action,
                swarmId,
                actor,
                state: null,
                lockHolder: null,
                code: "swarm_not_found",
                result: InterventionResult.NotFound("swarm_not_found", "Swarm not found."));
        }

        if (GuardTerminal(entity) is { } terminal)
        {
            return this.Reject(action, swarmId, actor, entity.State, entity.LockedBy, "terminal_state", terminal);
        }

        if (GuardLock(entity, actor) is { } locked)
        {
            return this.Reject(action, swarmId, actor, entity.State, entity.LockedBy, "locked", locked);
        }

        try
        {
            await this.stateService.TransitionSwarmAsync(
                swarmId,
                SwarmInstanceState.Synthesizing,
                TransitionReasons.UserSkip,
                actor,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidStateTransitionException)
        {
            return this.Reject(
                action,
                swarmId,
                actor,
                entity.State,
                entity.LockedBy,
                "invalid_transition",
                result: InterventionResult.Conflict(
                    "invalid_transition",
                    new { code = "invalid_transition", from = entity.State, to = nameof(SwarmInstanceState.Synthesizing) }));
        }
        catch (SwarmConcurrencyConflictException ex)
        {
            return this.MapConcurrencyConflict(
                action,
                swarmId,
                actor,
                entity.State,
                ex);
        }

        await this.ReleaseLockIfHolderAsync(entity, actor, cancellationToken).ConfigureAwait(false);

        await this.manager.EnsureLiveAsync(swarmId, cancellationToken).ConfigureAwait(false);
        this.manager.SignalSkip(swarmId);
        return this.Accept(
            action,
            swarmId,
            actor,
            entity.State,
            InterventionResult.NoContent());
    }

    /// <inheritdoc/>
    public async Task<InterventionResult> CancelAsync(
        Guid swarmId,
        string? actor,
        CancellationToken cancellationToken = default)
    {
        const string action = "cancel";

        var entity = await this.repository.GetSwarmAsync(swarmId).ConfigureAwait(false);
        if (entity is null)
        {
            return this.Reject(
                action,
                swarmId,
                actor,
                state: null,
                lockHolder: null,
                code: "swarm_not_found",
                result: InterventionResult.NotFound("swarm_not_found", "Swarm not found."));
        }

        if (GuardTerminal(entity) is { } terminal)
        {
            return this.Reject(
                action,
                swarmId,
                actor,
                entity.State,
                entity.LockedBy,
                "terminal_state",
                terminal);
        }

        if (GuardLock(entity, actor) is { } locked)
        {
            return this.Reject(
                action,
                swarmId,
                actor,
                entity.State,
                entity.LockedBy,
                "locked",
                locked);
        }

        try
        {
            await this.stateService.TransitionSwarmAsync(
                swarmId,
                SwarmInstanceState.Cancelled,
                TransitionReasons.UserCancel,
                actor,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidStateTransitionException)
        {
            return this.Reject(
                action,
                swarmId,
                actor,
                entity.State,
                entity.LockedBy,
                "invalid_transition",
                result: InterventionResult.Conflict(
                    "invalid_transition",
                    new { code = "invalid_transition", from = entity.State, to = nameof(SwarmInstanceState.Cancelled) }));
        }
        catch (SwarmConcurrencyConflictException ex)
        {
            return this.MapConcurrencyConflict(
                action,
                swarmId,
                actor,
                entity.State,
                ex);
        }

        await this.ReleaseLockIfHolderAsync(entity, actor, cancellationToken).ConfigureAwait(false);

        await this.manager.CancelSwarmAsync(swarmId).ConfigureAwait(false);
        return this.Accept(
            action,
            swarmId,
            actor,
            entity.State,
            InterventionResult.NoContent());
    }

    /// <inheritdoc/>
    public async Task<InterventionResult> LockAsync(
        Guid swarmId,
        string? actor,
        bool steal,
        CancellationToken cancellationToken = default)
    {
        const string action = "lock";

        if (string.IsNullOrWhiteSpace(actor))
        {
            return this.Reject(
                action,
                swarmId,
                actor,
                state: null,
                lockHolder: null,
                code: "actor_required",
                result: InterventionResult.Conflict(
                    "actor_required",
                    new { code = "actor_required", message = "An actor identity is required to acquire a diagnose lock." }));
        }

        var entity = await this.repository.GetSwarmAsync(swarmId).ConfigureAwait(false);
        if (entity is null)
        {
            return this.Reject(
                action,
                swarmId,
                actor,
                state: null,
                lockHolder: null,
                code: "swarm_not_found",
                result: InterventionResult.NotFound("swarm_not_found", "Swarm not found."));
        }

        if (GuardTerminal(entity) is { } terminal)
        {
            return this.Reject(
                action,
                swarmId,
                actor,
                entity.State,
                entity.LockedBy,
                "terminal_state",
                terminal);
        }

        var holder = entity.LockedBy;
        if (!string.IsNullOrEmpty(holder) && !string.Equals(holder, actor, StringComparison.Ordinal))
        {
            if (!steal)
            {
                return this.Reject(
                    action,
                    swarmId,
                    actor,
                    entity.State,
                    entity.LockedBy,
                    "locked",
                    result: InterventionResult.Locked(holder, entity.LockedAt ?? DateTime.UtcNow));
            }
        }

        var now = DateTime.UtcNow;
        var wasStolen = !string.IsNullOrEmpty(holder) && !string.Equals(holder, actor, StringComparison.Ordinal);
        await this.repository.SetLockAsync(swarmId, actor, now, cancellationToken).ConfigureAwait(false);

        await this.stateService.RecordSwarmAuditAsync(
            swarmId,
            wasStolen ? TransitionReasons.LockStolen : TransitionReasons.LockAcquired,
            actor,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return this.Accept(
            action,
            swarmId,
            actor,
            entity.State,
            InterventionResult.Ok(new { lockedBy = actor, lockedAt = now }));
    }

    /// <inheritdoc/>
    public async Task<InterventionResult> UnlockAsync(
        Guid swarmId,
        string? actor,
        CancellationToken cancellationToken = default)
    {
        const string action = "unlock";

        var entity = await this.repository.GetSwarmAsync(swarmId).ConfigureAwait(false);
        if (entity is null)
        {
            return this.Reject(
                action,
                swarmId,
                actor,
                state: null,
                lockHolder: null,
                code: "swarm_not_found",
                result: InterventionResult.NotFound("swarm_not_found", "Swarm not found."));
        }

        if (string.IsNullOrEmpty(entity.LockedBy))
        {
            return this.Accept(
                action,
                swarmId,
                actor,
                entity.State,
                InterventionResult.NoContent());
        }

        if (!string.Equals(entity.LockedBy, actor, StringComparison.Ordinal))
        {
            return this.Reject(
                action,
                swarmId,
                actor,
                entity.State,
                entity.LockedBy,
                "not_lock_holder",
                result: InterventionResult.Forbidden(
                    "not_lock_holder",
                    $"Lock is held by '{entity.LockedBy}'; only the holder may release it."));
        }

        await this.repository.SetLockAsync(swarmId, null, null, cancellationToken).ConfigureAwait(false);
        await this.stateService.RecordSwarmAuditAsync(
            swarmId,
            TransitionReasons.LockReleased,
            actor,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return this.Accept(
            action,
            swarmId,
            actor,
            entity.State,
            InterventionResult.NoContent());
    }

    /// <inheritdoc/>
    public async Task<InterventionResult> MarkAsAwaitingInterventionAsync(
        Guid swarmId,
        string? actor,
        CancellationToken cancellationToken = default)
    {
        const string action = "mark-as-awaiting-intervention";

        var entity = await this.repository.GetSwarmAsync(swarmId).ConfigureAwait(false);
        if (entity is null)
        {
            return this.Reject(
                action,
                swarmId,
                actor,
                state: null,
                lockHolder: null,
                code: "swarm_not_found",
                result: InterventionResult.NotFound("swarm_not_found", "Swarm not found."));
        }

        // Manual Recover is valid only from Failed. Complete and Cancelled
        // stay off-limits (intentional terminals). Non-terminal source
        // states are also rejected — use the standard intervention actions
        // for those.
        if (!string.Equals(entity.State, nameof(SwarmInstanceState.Failed), StringComparison.Ordinal))
        {
            return this.Reject(
                action,
                swarmId,
                actor,
                entity.State,
                entity.LockedBy,
                "invalid_transition",
                result: InterventionResult.Conflict(
                    "invalid_transition",
                    new
                    {
                        code = "invalid_transition",
                        from = entity.State,
                        to = nameof(SwarmInstanceState.AwaitingIntervention),
                        reason = TransitionReasons.UserMarkForIntervention,
                    }));
        }

        // Carry the original failure note forward so the audit trail shows
        // why the swarm was Failed at the time of the flip.
        var latest = await this.repository
            .GetLatestSwarmTransitionAsync(swarmId, cancellationToken)
            .ConfigureAwait(false);
        var originalNote = latest?.Note ?? "(no note)";
        var carriedNote = $"Recovered from: {originalNote}";

        try
        {
            await this.stateService.TransitionSwarmAsync(
                swarmId,
                SwarmInstanceState.AwaitingIntervention,
                TransitionReasons.UserMarkForIntervention,
                actor,
                note: carriedNote,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidStateTransitionException)
        {
            return this.Reject(
                action,
                swarmId,
                actor,
                entity.State,
                entity.LockedBy,
                "invalid_transition",
                result: InterventionResult.Conflict(
                    "invalid_transition",
                    new { code = "invalid_transition", from = entity.State, to = nameof(SwarmInstanceState.AwaitingIntervention) }));
        }
        catch (SwarmConcurrencyConflictException ex)
        {
            return this.MapConcurrencyConflict(
                action,
                swarmId,
                actor,
                entity.State,
                ex);
        }

        return this.Accept(action, swarmId, actor, entity.State, InterventionResult.NoContent());
    }

    /// <summary>
    /// Shared terminal-state guard. Returns a 410 Gone result when the
    /// swarm is in <c>Complete</c>, <c>Cancelled</c>, or <c>Failed</c>.
    /// </summary>
    private static InterventionResult? GuardTerminal(SwarmEntity entity)
    {
        if (!Enum.TryParse<SwarmInstanceState>(entity.State, out var currentState))
        {
            return null;
        }

        return SwarmStateGuards.IsTerminal(currentState)
            ? InterventionResult.Gone(entity.State)
            : null;
    }

    /// <summary>
    /// Shared lock-holder guard. Returns a 423 Locked result when the
    /// swarm is locked by someone other than the caller.
    /// </summary>
    private static InterventionResult? GuardLock(SwarmEntity entity, string? actor)
    {
        if (string.IsNullOrEmpty(entity.LockedBy))
        {
            return null;
        }

        return string.Equals(entity.LockedBy, actor, StringComparison.Ordinal)
            ? null
            : InterventionResult.Locked(entity.LockedBy, entity.LockedAt ?? DateTime.UtcNow);
    }

    private InterventionResult Reject(
        string action,
        Guid swarmId,
        string? actor,
        string? state,
        string? lockHolder,
        string code,
        InterventionResult result)
    {
        this.LogInterventionRejected(
            action,
            swarmId,
            state ?? "?",
            actor ?? "(anon)",
            code,
            result.StatusCode,
            lockHolder ?? "(none)");
        return result;
    }

    private InterventionResult Accept(
        string action,
        Guid swarmId,
        string? actor,
        string? state,
        InterventionResult result)
    {
        this.LogInterventionAccepted(
            action,
            swarmId,
            state ?? "?",
            actor ?? "(anon)",
            result.StatusCode);
        return result;
    }

    private InterventionResult MapConcurrencyConflict(
        string action,
        Guid swarmId,
        string? actor,
        string? state,
        SwarmConcurrencyConflictException ex)
    {
        this.LogConcurrencyConflict(
            action,
            swarmId,
            state ?? "?",
            actor ?? "(anon)",
            ex.EntityKind,
            ex.EntityId);

        return InterventionResult.Conflict(
            "concurrency_conflict",
            new
            {
                code = "concurrency_conflict",
                entity = ex.EntityKind,
                id = ex.EntityId,
                message = ex.Message,
            });
    }

    private async Task ReleaseLockIfHolderAsync(
        SwarmEntity entity,
        string? actor,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(entity.LockedBy)
            || !string.Equals(entity.LockedBy, actor, StringComparison.Ordinal))
        {
            return;
        }

        await this.repository.SetLockAsync(entity.Id, null, null, cancellationToken).ConfigureAwait(false);
        await this.stateService.RecordSwarmAuditAsync(
            entity.Id,
            TransitionReasons.LockReleased,
            actor,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task StripAbandonedDepsAsync(
        Guid swarmId,
        IReadOnlyList<string> abandonedIds,
        string? actor,
        string? note,
        CancellationToken cancellationToken)
    {
        var abandonSet = abandonedIds.ToHashSet(StringComparer.Ordinal);
        var tasks = await this.repository.GetTasksAsync(swarmId).ConfigureAwait(false);

        foreach (var task in tasks)
        {
            if (abandonSet.Contains(task.Id))
            {
                continue;
            }

            var currentDeps = string.IsNullOrWhiteSpace(task.BlockedByJson)
                ? []
                : JsonSerializer.Deserialize<List<string>>(task.BlockedByJson) ?? [];

            if (currentDeps.Count == 0 || !currentDeps.Any(abandonSet.Contains))
            {
                continue;
            }

            var filtered = currentDeps.Where(d => !abandonSet.Contains(d)).ToList();
            await this.repository.UpdateTaskBlockedByAsync(swarmId, task.Id, filtered, cancellationToken).ConfigureAwait(false);

            if (filtered.Count == 0
                && string.Equals(task.State, nameof(TaskState.Blocked), StringComparison.Ordinal))
            {
                try
                {
                    await this.stateService.TransitionTaskAsync(
                        swarmId,
                        task.Id,
                        TaskState.Pending,
                        TransitionReasons.AbandonedDepStripped,
                        actor,
                        retryCountDelta: 0,
                        note: note,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidStateTransitionException)
                {
                    // Task moved between read and write; orchestrator will reconcile.
                }
            }
        }
    }

    [LoggerMessage(
        LogLevel.Warning,
        "Intervention {Action} rejected for swarm {SwarmId} at state {State} by actor {Actor} (code={Code}, status={StatusCode}, lockHolder={LockHolder}).")]
    private partial void LogInterventionRejected(
        string action,
        Guid swarmId,
        string state,
        string actor,
        string code,
        int statusCode,
        string lockHolder);

    [LoggerMessage(
        LogLevel.Information,
        "Intervention {Action} accepted for swarm {SwarmId} at state {State} by actor {Actor} (status={StatusCode}).")]
    private partial void LogInterventionAccepted(
        string action,
        Guid swarmId,
        string state,
        string actor,
        int statusCode);

    [LoggerMessage(
        LogLevel.Warning,
        "Intervention {Action} hit concurrency conflict for swarm {SwarmId} at state {State} by actor {Actor} (entityKind={EntityKind}, entityId={EntityId}). Caller should refetch and retry.")]
    private partial void LogConcurrencyConflict(
        string action,
        Guid swarmId,
        string state,
        string actor,
        string entityKind,
        string entityId);
}
