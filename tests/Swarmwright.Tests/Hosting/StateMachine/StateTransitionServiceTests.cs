using System.Text.Json;
using Swarmwright.Database;
using Swarmwright.Database.Models;
using Swarmwright.Events;
using Swarmwright.Hosting;
using Swarmwright.Hosting.StateMachine;
using Swarmwright.Models.Enums;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Swarmwright.Tests.Hosting.StateMachine;

/// <summary>
/// Integration-style tests for <see cref="StateTransitionService"/> using the
/// EF InMemory provider. The service is the only code path that writes
/// <see cref="SwarmEntity.State"/> / <see cref="TaskEntity.State"/> and the
/// corresponding audit rows.
/// </summary>
[TestClass]
public sealed class StateTransitionServiceTests
{
    private InMemoryDbContextFactory factory = null!;
    private Mock<ISwarmEmissionBroker> emissionBroker = null!;
    private StateTransitionService service = null!;

    [TestInitialize]
    public void Initialize()
    {
        this.factory = new InMemoryDbContextFactory("SwarmStateTx_" + Guid.NewGuid());
        this.emissionBroker = new Mock<ISwarmEmissionBroker>();
        this.service = new StateTransitionService(this.factory, this.emissionBroker.Object, Mock.Of<ISwarmObservationSink>());
    }

    // ---- Swarm transitions ----

    [TestMethod]
    public async Task TransitionSwarmAsync_LegalMove_UpdatesStateAndWritesAuditRow()
    {
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.Created);

        var result = await this.service.TransitionSwarmAsync(
            swarmId,
            SwarmInstanceState.Planning,
            TransitionReasons.PhaseAdvanced,
            actor: "system");

        result.FromState.Should().Be(SwarmInstanceState.Created);
        result.ToState.Should().Be(SwarmInstanceState.Planning);
        result.Reason.Should().Be(TransitionReasons.PhaseAdvanced);

        await using var ctx = this.factory.CreateDbContext();
        var swarm = await ctx.Swarms.FindAsync(swarmId);
        swarm!.State.Should().Be(nameof(SwarmInstanceState.Planning));

        var row = await ctx.SwarmStateTransitions
            .SingleAsync(r => r.SwarmId == swarmId);
        row.FromState.Should().Be(nameof(SwarmInstanceState.Created));
        row.ToState.Should().Be(nameof(SwarmInstanceState.Planning));
        row.Reason.Should().Be(TransitionReasons.PhaseAdvanced);
        row.Actor.Should().Be("system");
    }

    [TestMethod]
    public async Task TransitionSwarmAsync_IllegalMove_ThrowsInvalidStateTransition()
    {
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.Created);

        Func<Task> act = () => this.service.TransitionSwarmAsync(
            swarmId,
            SwarmInstanceState.Complete,
            TransitionReasons.PhaseAdvanced);

        await act.Should().ThrowAsync<InvalidStateTransitionException>();

        await using var ctx = this.factory.CreateDbContext();
        (await ctx.SwarmStateTransitions.CountAsync()).Should().Be(0);
    }

    [TestMethod]
    public async Task TransitionSwarmAsync_FromTerminalState_ThrowsInvalidStateTransition()
    {
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.Complete);

        Func<Task> act = () => this.service.TransitionSwarmAsync(
            swarmId,
            SwarmInstanceState.Executing,
            TransitionReasons.UserContinue);

        await act.Should().ThrowAsync<InvalidStateTransitionException>();
    }

    [TestMethod]
    public async Task TransitionSwarmAsync_UnknownSwarm_ThrowsInvalidOperation()
    {
        Func<Task> act = () => this.service.TransitionSwarmAsync(
            Guid.NewGuid(),
            SwarmInstanceState.Planning,
            TransitionReasons.PhaseAdvanced);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [TestMethod]
    public async Task RecordSwarmAuditAsync_WritesAuditRow_WithoutChangingState()
    {
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.NeedsDiagnosis);

        var result = await this.service.RecordSwarmAuditAsync(
            swarmId,
            TransitionReasons.LockAcquired,
            actor: "alice");

        result.FromState.Should().Be(SwarmInstanceState.NeedsDiagnosis);
        result.ToState.Should().Be(SwarmInstanceState.NeedsDiagnosis);

        await using var ctx = this.factory.CreateDbContext();
        var swarm = await ctx.Swarms.FindAsync(swarmId);
        swarm!.State.Should().Be(nameof(SwarmInstanceState.NeedsDiagnosis));

        var row = await ctx.SwarmStateTransitions
            .SingleAsync(r => r.SwarmId == swarmId);
        row.FromState.Should().Be(row.ToState);
        row.Reason.Should().Be(TransitionReasons.LockAcquired);
        row.Actor.Should().Be("alice");
    }

    // ---- Task transitions ----

    [TestMethod]
    public async Task TransitionTaskAsync_FailedToPending_WithDelta1_BumpsRetryCount()
    {
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.AwaitingIntervention);
        var taskId = await this.SeedTaskAsync(swarmId, TaskState.Failed, retryCount: 0);

        var result = await this.service.TransitionTaskAsync(
            swarmId,
            taskId,
            TaskState.Pending,
            TransitionReasons.UserContinue,
            actor: "alice",
            retryCountDelta: 1);

        result.RetryCountAfter.Should().Be(1);
        result.FromState.Should().Be(TaskState.Failed);
        result.ToState.Should().Be(TaskState.Pending);

        await using var ctx = this.factory.CreateDbContext();
        var task = await ctx.Tasks
            .SingleAsync(t => t.SwarmId == swarmId && t.Id == taskId);
        task.State.Should().Be(nameof(TaskState.Pending));
        task.RetryCount.Should().Be(1);

        var row = await ctx.TaskStateTransitions
            .SingleAsync(r => r.TaskId == taskId);
        row.Reason.Should().Be(TransitionReasons.UserContinue);
        row.RetryCountAfter.Should().Be(1);
    }

    [TestMethod]
    public async Task TransitionTaskAsync_FailedToPending_WithDelta0_DoesNotBumpRetryCount()
    {
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.AwaitingIntervention);
        var taskId = await this.SeedTaskAsync(swarmId, TaskState.Failed, retryCount: 2);

        var result = await this.service.TransitionTaskAsync(
            swarmId,
            taskId,
            TaskState.Pending,
            TransitionReasons.LeaderRepairPlan,
            actor: "leader",
            retryCountDelta: 0);

        result.RetryCountAfter.Should().Be(2);

        await using var ctx = this.factory.CreateDbContext();
        var task = await ctx.Tasks
            .SingleAsync(t => t.SwarmId == swarmId && t.Id == taskId);
        task.RetryCount.Should().Be(2);
    }

    [TestMethod]
    public async Task TransitionTaskAsync_IllegalMove_Throws()
    {
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.Executing);
        var taskId = await this.SeedTaskAsync(swarmId, TaskState.Pending, retryCount: 0);

        Func<Task> act = () => this.service.TransitionTaskAsync(
            swarmId,
            taskId,
            TaskState.Completed,
            TransitionReasons.PhaseAdvanced);

        await act.Should().ThrowAsync<InvalidStateTransitionException>();
    }

    [TestMethod]
    public async Task TransitionTaskAsync_UnknownTask_ThrowsInvalidOperation()
    {
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.Executing);

        Func<Task> act = () => this.service.TransitionTaskAsync(
            swarmId,
            "missing-task",
            TaskState.Completed,
            TransitionReasons.PhaseAdvanced);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [TestMethod]
    public async Task TransitionTaskAsync_InProgressToCompleted_DoesNotBumpRetryCount()
    {
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.Executing);
        var taskId = await this.SeedTaskAsync(swarmId, TaskState.InProgress, retryCount: 0);

        var result = await this.service.TransitionTaskAsync(
            swarmId,
            taskId,
            TaskState.Completed,
            TransitionReasons.PhaseAdvanced);

        result.RetryCountAfter.Should().Be(0);
    }

    // ---- Failed-task dependent promotion (deadlock-on-failure regression guard) ----

    /// <summary>
    /// A task transitioning to Failed must strip its id from every dependent's
    /// blocked_by list and promote any newly-unblocked dependent from Blocked to
    /// Pending — the same contract Completed already honors. Without this, the
    /// swarm deadlocks because dependents stay Blocked forever.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [TestMethod]
    public async Task TransitionTaskAsync_FailedTask_PromotesSingleDependent()
    {
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.Executing);
        var failingId = await this.SeedTaskAsync(swarmId, TaskState.Pending, retryCount: 0);
        var dependentId = await this.SeedBlockedTaskAsync(swarmId, [failingId]);

        await this.service.TransitionTaskAsync(
            swarmId,
            failingId,
            TaskState.Failed,
            TransitionReasons.PhaseAdvanced);

        await using var ctx = this.factory.CreateDbContext();
        var dependent = await ctx.Tasks
            .SingleAsync(t => t.SwarmId == swarmId && t.Id == dependentId);
        dependent.State.Should().Be(nameof(TaskState.Pending));
        DeserializeBlockedBy(dependent.BlockedByJson).Should().BeEmpty();

        var auditRow = await ctx.TaskStateTransitions
            .SingleAsync(r => r.TaskId == dependentId
                && r.FromState == nameof(TaskState.Blocked)
                && r.ToState == nameof(TaskState.Pending));
        auditRow.Reason.Should().Be(TransitionReasons.PhaseAdvanced);

        this.emissionBroker.Verify(
            b => b.EmitTaskUpdatedAsync(swarmId, failingId, TaskState.Failed, It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        this.emissionBroker.Verify(
            b => b.EmitTaskUpdatedAsync(swarmId, dependentId, TaskState.Pending, It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// When a Failed task only resolves part of a dependent's blockers, the dependent
    /// must remain Blocked (no promotion, no Blocked->Pending audit row). Mirrors the
    /// existing Completed-side behavior and proves the fix is targeted, not blanket.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [TestMethod]
    public async Task TransitionTaskAsync_FailedTask_PartialDepsRemaining_StaysBlocked()
    {
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.Executing);
        var failingId = await this.SeedTaskAsync(swarmId, TaskState.Pending, retryCount: 0);
        var siblingId = await this.SeedTaskAsync(swarmId, TaskState.Pending, retryCount: 0);
        var dependentId = await this.SeedBlockedTaskAsync(swarmId, [failingId, siblingId]);

        await this.service.TransitionTaskAsync(
            swarmId,
            failingId,
            TaskState.Failed,
            TransitionReasons.PhaseAdvanced);

        await using var ctx = this.factory.CreateDbContext();
        var dependent = await ctx.Tasks
            .SingleAsync(t => t.SwarmId == swarmId && t.Id == dependentId);
        dependent.State.Should().Be(nameof(TaskState.Blocked));
        DeserializeBlockedBy(dependent.BlockedByJson).Should().BeEquivalentTo([siblingId]);

        var promotionRow = await ctx.TaskStateTransitions
            .Where(r => r.TaskId == dependentId
                && r.FromState == nameof(TaskState.Blocked)
                && r.ToState == nameof(TaskState.Pending))
            .ToListAsync();
        promotionRow.Should().BeEmpty("partial dep removal must not promote");
    }

    /// <summary>
    /// Cascading failures must unwind the dependency chain step by step: A→Failed
    /// promotes B (Blocked→Pending); B→Failed promotes C (Blocked→Pending). Without
    /// the fix, the second hop deadlocks because B's id stays in C's blocked_by.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [TestMethod]
    public async Task TransitionTaskAsync_FailedTask_CascadesThroughChain()
    {
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.Executing);
        var aId = await this.SeedTaskAsync(swarmId, TaskState.Pending, retryCount: 0);
        var bId = await this.SeedBlockedTaskAsync(swarmId, [aId]);
        var cId = await this.SeedBlockedTaskAsync(swarmId, [bId]);

        await this.service.TransitionTaskAsync(
            swarmId,
            aId,
            TaskState.Failed,
            TransitionReasons.PhaseAdvanced);

        await using (var ctxAfterA = this.factory.CreateDbContext())
        {
            var b = await ctxAfterA.Tasks.SingleAsync(t => t.Id == bId);
            b.State.Should().Be(nameof(TaskState.Pending));
            DeserializeBlockedBy(b.BlockedByJson).Should().BeEmpty();

            var c = await ctxAfterA.Tasks.SingleAsync(t => t.Id == cId);
            c.State.Should().Be(nameof(TaskState.Blocked));
            DeserializeBlockedBy(c.BlockedByJson).Should().BeEquivalentTo([bId]);
        }

        await this.service.TransitionTaskAsync(
            swarmId,
            bId,
            TaskState.Failed,
            TransitionReasons.PhaseAdvanced);

        await using var ctxAfterB = this.factory.CreateDbContext();
        var cFinal = await ctxAfterB.Tasks.SingleAsync(t => t.Id == cId);
        cFinal.State.Should().Be(nameof(TaskState.Pending));
        DeserializeBlockedBy(cFinal.BlockedByJson).Should().BeEmpty();
    }

    /// <summary>
    /// Locks the contract that dependents inheriting from a Failed predecessor see
    /// the empty/null upstream Result. This matches existing failure-propagation
    /// policy at SwarmOrchestrator.cs:650 / :715 and is intentional — a follow-up
    /// task reads its predecessors' results from the DB; if a predecessor failed,
    /// the dependent runs with whatever empty/null payload was persisted.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [TestMethod]
    public async Task TransitionTaskAsync_FailedTask_DependentSeesFailedUpstreamResult()
    {
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.Executing);
        var failingId = await this.SeedTaskAsync(swarmId, TaskState.Pending, retryCount: 0);
        var dependentId = await this.SeedBlockedTaskAsync(swarmId, [failingId]);

        await this.service.TransitionTaskAsync(
            swarmId,
            failingId,
            TaskState.Failed,
            TransitionReasons.PhaseAdvanced,
            result: null);

        await using var ctx = this.factory.CreateDbContext();
        var failing = await ctx.Tasks.SingleAsync(t => t.Id == failingId);
        failing.State.Should().Be(nameof(TaskState.Failed));
        (failing.Result is null || failing.Result.Length == 0).Should().BeTrue(
            "the contract is that a Failed predecessor's Result is unset");

        var dependent = await ctx.Tasks.SingleAsync(t => t.Id == dependentId);
        dependent.State.Should().Be(nameof(TaskState.Pending));
    }

    // ---- Helpers ----

    private async Task<Guid> SeedSwarmAsync(SwarmInstanceState state)
    {
        await using var ctx = this.factory.CreateDbContext();
        var swarm = new SwarmEntity
        {
            Id = Guid.NewGuid(),
            Goal = "test goal",
            State = state.ToString(),
        };
        ctx.Swarms.Add(swarm);
        await ctx.SaveChangesAsync();
        return swarm.Id;
    }

    private async Task<string> SeedTaskAsync(Guid swarmId, TaskState state, int retryCount)
    {
        await using var ctx = this.factory.CreateDbContext();
        var taskId = "t-" + Guid.NewGuid().ToString("N")[..8];
        var task = new TaskEntity
        {
            SwarmId = swarmId,
            Id = taskId,
            Subject = "test task",
            Description = "test description",
            WorkerRole = "worker",
            WorkerName = "worker-a",
            State = state.ToString(),
            RetryCount = retryCount,
        };
        ctx.Tasks.Add(task);
        await ctx.SaveChangesAsync();
        return taskId;
    }

    private async Task<string> SeedBlockedTaskAsync(Guid swarmId, IEnumerable<string> blockedBy)
    {
        await using var ctx = this.factory.CreateDbContext();
        var taskId = "t-" + Guid.NewGuid().ToString("N")[..8];
        var task = new TaskEntity
        {
            SwarmId = swarmId,
            Id = taskId,
            Subject = "blocked task",
            Description = "test description",
            WorkerRole = "worker",
            WorkerName = "worker-b",
            State = nameof(TaskState.Blocked),
            BlockedByJson = JsonSerializer.Serialize(blockedBy.ToList()),
        };
        ctx.Tasks.Add(task);
        await ctx.SaveChangesAsync();
        return taskId;
    }

    private static List<string> DeserializeBlockedBy(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        return JsonSerializer.Deserialize<List<string>>(json) ?? [];
    }
}
