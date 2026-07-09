using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Swarmwright.Database;
using Swarmwright.Database.Models;
using Swarmwright.Events;
using Swarmwright.Models.Enums;

namespace Swarmwright.Hosting.StateMachine;

/// <summary>
/// Default implementation of <see cref="IStateTransitionService"/> backed by
/// <see cref="SwarmDbContext"/>. Performs guard validation, state mutation and
/// audit-row insertion in a single <see cref="DbContext.SaveChangesAsync(CancellationToken)"/>
/// call so they commit together. Every successful task transition then
/// unconditionally calls
/// <see cref="ISwarmEmissionBroker.EmitTaskUpdatedAsync"/>; a broker failure is
/// logged at Warning and does not roll back the persisted transition.
/// </summary>
public sealed partial class StateTransitionService : IStateTransitionService
{
    private readonly IDbContextFactory<SwarmDbContext> contextFactory;
    private readonly ISwarmEmissionBroker emissionBroker;
    private readonly ISwarmObservationSink observationSink;
    private readonly ILogger<StateTransitionService> logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StateTransitionService"/> class.
    /// </summary>
    /// <param name="contextFactory">The swarm database context factory.</param>
    /// <param name="emissionBroker">The broker that routes <c>SWARM_TASK_UPDATED</c> to the swarm's AG-UI adapter.</param>
    /// <param name="observationSink">
    /// The observation sink. Swarm state transitions are published via
    /// <see cref="ISwarmObservationSink.SignalStateChangeAsync"/> so workflow consumers
    /// awaiting <c>WaitForStateChangeAsync</c> can observe non-terminal pause states.
    /// Required: silent skip on misconfiguration would mean the workflow executor
    /// hangs forever on intervention states with no diagnostic.
    /// </param>
    /// <param name="logger">Optional logger for emission-failure warnings.</param>
    public StateTransitionService(
        IDbContextFactory<SwarmDbContext> contextFactory,
        ISwarmEmissionBroker emissionBroker,
        ISwarmObservationSink observationSink,
        ILogger<StateTransitionService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(emissionBroker);
        ArgumentNullException.ThrowIfNull(observationSink);
        this.contextFactory = contextFactory;
        this.emissionBroker = emissionBroker;
        this.observationSink = observationSink;
        this.logger = logger ?? NullLogger<StateTransitionService>.Instance;
    }

    /// <inheritdoc/>
    public async Task<SwarmStateTransitionResult> TransitionSwarmAsync(
        Guid swarmId,
        SwarmInstanceState toState,
        string reason,
        string? actor = null,
        string? note = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);

        await using var context = await this.contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        var swarm = await context.Swarms
            .FirstOrDefaultAsync(s => s.Id == swarmId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Swarm {swarmId} not found.");

        var fromState = ParseSwarmState(swarm.State);

        if (!SwarmStateGuards.CanTransitionSwarm(fromState, toState))
        {
            throw new InvalidStateTransitionException(
                entityKind: "swarm",
                entityId: swarmId.ToString(),
                fromState: fromState.ToString(),
                toState: toState.ToString(),
                reason: reason);
        }

        var transition = new SwarmStateTransition
        {
            SwarmId = swarmId,
            FromState = fromState.ToString(),
            ToState = toState.ToString(),
            Reason = reason,
            Actor = actor,
            Note = note,
            CreatedAt = DateTime.UtcNow,
        };

        swarm.State = toState.ToString();
        swarm.UpdatedAt = transition.CreatedAt;
        context.SwarmStateTransitions.Add(transition);

        await context.SaveOrThrowAsync("swarm", swarmId.ToString(), cancellationToken).ConfigureAwait(false);

        // After the state-row + audit-row commit, publish the transition to the
        // observation sink so workflow consumers awaiting WaitForStateChangeAsync
        // wake. Fires for every transition (terminal and non-terminal alike); the
        // dispatcher's separate SignalTerminalAsync covers the richer
        // SwarmExecution payload that completion waiters need.
        await this.observationSink
            .SignalStateChangeAsync(swarmId, toState)
            .ConfigureAwait(false);

        return new SwarmStateTransitionResult(
            swarmId,
            fromState,
            toState,
            reason,
            actor,
            transition.Id);
    }

    /// <inheritdoc/>
    public async Task<TaskStateTransitionResult> TransitionTaskAsync(
        Guid swarmId,
        string taskId,
        TaskState toState,
        string reason,
        string? actor = null,
        int retryCountDelta = 0,
        string? note = null,
        string? result = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(taskId);
        ArgumentException.ThrowIfNullOrEmpty(reason);

        await using var context = await this.contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        var task = await context.Tasks
            .FirstOrDefaultAsync(t => t.SwarmId == swarmId && t.Id == taskId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Task {taskId} not found for swarm {swarmId}.");

        var fromState = ParseTaskState(task.State);

        if (!SwarmStateGuards.CanTransitionTask(fromState, toState))
        {
            throw new InvalidStateTransitionException(
                entityKind: "task",
                entityId: $"{swarmId}/{taskId}",
                fromState: fromState.ToString(),
                toState: toState.ToString(),
                reason: reason);
        }

        var now = DateTime.UtcNow;
        task.State = toState.ToString();
        task.RetryCount += retryCountDelta;
        task.UpdatedAt = now;
        if (result is not null)
        {
            task.Result = result;
        }

        var transition = new TaskStateTransition
        {
            SwarmId = swarmId,
            TaskId = taskId,
            FromState = fromState.ToString(),
            ToState = toState.ToString(),
            Reason = reason,
            Actor = actor,
            Note = note,
            RetryCountAfter = task.RetryCount,
            CreatedAt = now,
        };

        context.TaskStateTransitions.Add(transition);

        // When a task lands on a terminal state (Completed or Failed), strip its id
        // from every dependent's blocked_by list and promote any dependent whose
        // list is now empty from Blocked to Pending. Failed must promote dependents
        // for the same reason Completed does — without it, dependents stay Blocked
        // forever and the swarm deadlocks. Matches the orchestrator's terminal-state
        // checks at SwarmOrchestrator.cs:650 / :715.
        var promotedTaskIds = new List<(string TaskId, string? WorkerName)>();
        if (toState is TaskState.Completed or TaskState.Failed)
        {
            promotedTaskIds.AddRange(
                await PromoteDependentsAsync(context, swarmId, taskId, now, actor, cancellationToken)
                    .ConfigureAwait(false));
        }

        await context.SaveOrThrowAsync("task", $"{swarmId}/{taskId}", cancellationToken).ConfigureAwait(false);

        try
        {
            await this.emissionBroker.EmitTaskUpdatedAsync(
                swarmId,
                taskId,
                toState,
                task.WorkerName,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.LogEmitFailed(swarmId, taskId, ex);
        }

        // Emit one SWARM_TASK_UPDATED per promoted dependent so the UI's
        // task board reflects Blocked->Pending immediately. Mirrors the
        // contract previously honored by SwarmService.UpdateTaskStatusAsync's
        // promotion loop.
        foreach (var (promotedId, promotedWorker) in promotedTaskIds)
        {
            try
            {
                await this.emissionBroker.EmitTaskUpdatedAsync(
                    swarmId,
                    promotedId,
                    TaskState.Pending,
                    promotedWorker,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.LogEmitFailed(swarmId, promotedId, ex);
            }
        }

        return new TaskStateTransitionResult(
            swarmId,
            taskId,
            fromState,
            toState,
            reason,
            actor,
            task.RetryCount,
            transition.Id);
    }

    /// <summary>
    /// Strips <paramref name="completedTaskId"/> from every sibling task's
    /// <see cref="TaskEntity.BlockedByJson"/> and writes a Blocked->Pending
    /// transition row for any sibling whose dependency list is now empty
    /// and whose state is Blocked. The mutations stay tracked on
    /// <paramref name="context"/>; the caller is responsible for the
    /// SaveChanges call so the completion + dep-promotion writes commit
    /// together.
    /// </summary>
    /// <param name="context">The active swarm DB context.</param>
    /// <param name="swarmId">The owning swarm.</param>
    /// <param name="completedTaskId">The id of the task that just transitioned to Completed.</param>
    /// <param name="now">The timestamp to stamp on promotion audit rows.</param>
    /// <param name="actor">The actor recorded on promotion audit rows.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The list of promoted task ids and their worker names so the caller can fan out SSE emissions.</returns>
    private static async Task<IReadOnlyList<(string TaskId, string? WorkerName)>> PromoteDependentsAsync(
        SwarmDbContext context,
        Guid swarmId,
        string completedTaskId,
        DateTime now,
        string? actor,
        CancellationToken cancellationToken)
    {
        var siblings = await context.Tasks
            .Where(t => t.SwarmId == swarmId && t.Id != completedTaskId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var promoted = new List<(string TaskId, string? WorkerName)>();
        foreach (var sibling in siblings)
        {
            var deps = string.IsNullOrWhiteSpace(sibling.BlockedByJson)
                ? []
                : JsonSerializer.Deserialize<List<string>>(sibling.BlockedByJson) ?? [];

            if (!deps.Remove(completedTaskId))
            {
                continue;
            }

            sibling.BlockedByJson = JsonSerializer.Serialize(deps);
            sibling.UpdatedAt = now;

            if (deps.Count == 0
                && string.Equals(sibling.State, nameof(TaskState.Blocked), StringComparison.Ordinal))
            {
                sibling.State = nameof(TaskState.Pending);
                context.TaskStateTransitions.Add(new TaskStateTransition
                {
                    SwarmId = swarmId,
                    TaskId = sibling.Id,
                    FromState = nameof(TaskState.Blocked),
                    ToState = nameof(TaskState.Pending),
                    Reason = TransitionReasons.PhaseAdvanced,
                    Actor = actor,
                    Note = null,
                    RetryCountAfter = sibling.RetryCount,
                    CreatedAt = now,
                });
                promoted.Add((sibling.Id, sibling.WorkerName));
            }
        }

        return promoted;
    }

    /// <inheritdoc/>
    public async Task<SwarmStateTransitionResult> RecordSwarmAuditAsync(
        Guid swarmId,
        string reason,
        string? actor = null,
        string? note = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);

        await using var context = await this.contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        var swarm = await context.Swarms
            .FirstOrDefaultAsync(s => s.Id == swarmId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Swarm {swarmId} not found.");

        var currentState = ParseSwarmState(swarm.State);

        var transition = new SwarmStateTransition
        {
            SwarmId = swarmId,
            FromState = currentState.ToString(),
            ToState = currentState.ToString(),
            Reason = reason,
            Actor = actor,
            Note = note,
            CreatedAt = DateTime.UtcNow,
        };

        context.SwarmStateTransitions.Add(transition);

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new SwarmStateTransitionResult(
            swarmId,
            currentState,
            currentState,
            reason,
            actor,
            transition.Id);
    }

    private static SwarmInstanceState ParseSwarmState(string raw)
    {
        return Enum.TryParse<SwarmInstanceState>(raw, ignoreCase: false, out var parsed)
            ? parsed
            : SwarmInstanceState.Created;
    }

    private static TaskState ParseTaskState(string raw)
    {
        return Enum.TryParse<TaskState>(raw, ignoreCase: false, out var parsed)
            ? parsed
            : TaskState.Pending;
    }

    [LoggerMessage(LogLevel.Warning, "Failed to emit SWARM_TASK_UPDATED for swarm {SwarmId} task {TaskId}; DB transition already committed.")]
    private partial void LogEmitFailed(Guid swarmId, string taskId, Exception exception);
}
