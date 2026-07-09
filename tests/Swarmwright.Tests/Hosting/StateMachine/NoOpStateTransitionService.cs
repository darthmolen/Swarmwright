using System.Text.Json;
using Swarmwright.Events.AgUI;
using Swarmwright.Hosting.StateMachine;
using Swarmwright.Models.Enums;

namespace Swarmwright.Tests.Hosting.StateMachine;

/// <summary>
/// Test double for <see cref="IStateTransitionService"/> that records calls
/// but performs no persistence. Use this in orchestrator tests where we care
/// about the legacy <see cref="Swarmwright.Services.ISwarmService"/>
/// contract, not about audit-row writes.
/// </summary>
internal sealed class NoOpStateTransitionService : IStateTransitionService
{
    private readonly SwarmEventAdapter? adapter;

    public NoOpStateTransitionService(SwarmEventAdapter? adapter = null)
    {
        this.adapter = adapter;
    }

    /// <summary>
    /// Gets or sets an optional inner <see cref="IStateTransitionService"/>
    /// that the no-op delegates to AFTER recording each call. When set, the
    /// stub becomes a recording decorator over a real implementation —
    /// orchestrator tests use this to keep their <see cref="SwarmCalls"/> /
    /// <see cref="TaskCalls"/> assertions while still actually mutating the
    /// DB rows that the production <c>SwarmService</c> reads through to
    /// after the F01.3 cache kill. When null (the default), the stub
    /// remains a pure no-op suitable for tests that don't exercise the
    /// post-transition read surface.
    /// </summary>
    public IStateTransitionService? Inner { get; set; }

    public List<(Guid SwarmId, SwarmInstanceState ToState, string Reason, string? Actor)> SwarmCalls { get; } = new();

    public List<(Guid SwarmId, string TaskId, TaskState ToState, string Reason, string? Actor, int Delta)> TaskCalls { get; } = new();

    public List<(Guid SwarmId, string Reason, string? Actor)> AuditCalls { get; } = new();

    public async Task<SwarmStateTransitionResult> TransitionSwarmAsync(
        Guid swarmId,
        SwarmInstanceState toState,
        string reason,
        string? actor = null,
        string? note = null,
        CancellationToken cancellationToken = default)
    {
        this.SwarmCalls.Add((swarmId, toState, reason, actor));

        if (this.Inner is not null)
        {
            return await this.Inner.TransitionSwarmAsync(
                swarmId, toState, reason, actor, note, cancellationToken).ConfigureAwait(false);
        }

        return new SwarmStateTransitionResult(
            swarmId, toState, toState, reason, actor, Guid.NewGuid());
    }

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
        this.TaskCalls.Add((swarmId, taskId, toState, reason, actor, retryCountDelta));

        TaskStateTransitionResult typedResult;
        if (this.Inner is not null)
        {
            typedResult = await this.Inner.TransitionTaskAsync(
                swarmId, taskId, toState, reason, actor, retryCountDelta, note, result, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            typedResult = new TaskStateTransitionResult(
                swarmId, taskId, toState, toState, reason, actor, retryCountDelta, Guid.NewGuid());
        }

        // Optional adapter lets orchestrator-level tests observe SWARM_TASK_UPDATED
        // emissions without wiring a real StateTransitionService + broker + manager
        // chain. When null (default), emission is skipped entirely — the test
        // asserts on TaskCalls.
        if (this.adapter is not null)
        {
            await this.adapter.EmitCustomAsync(
                "SWARM_TASK_UPDATED",
                JsonSerializer.SerializeToElement(new
                {
                    taskId,
                    status = toState.ToString(),
                    agent = (string?)null,
                })).ConfigureAwait(false);
        }

        return typedResult;
    }

    public async Task<SwarmStateTransitionResult> RecordSwarmAuditAsync(
        Guid swarmId,
        string reason,
        string? actor = null,
        string? note = null,
        CancellationToken cancellationToken = default)
    {
        this.AuditCalls.Add((swarmId, reason, actor));

        if (this.Inner is not null)
        {
            return await this.Inner.RecordSwarmAuditAsync(
                swarmId, reason, actor, note, cancellationToken).ConfigureAwait(false);
        }

        return new SwarmStateTransitionResult(
            swarmId, SwarmInstanceState.Created, SwarmInstanceState.Created, reason, actor, Guid.NewGuid());
    }
}
