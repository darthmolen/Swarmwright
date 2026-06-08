using System.Text.Json;
using Swarmwright.Database.Models;
using Swarmwright.Events;
using Swarmwright.Hosting.StateMachine;
using Swarmwright.Models;
using Swarmwright.Models.Enums;
using Swarmwright.Orchestration;
using Swarmwright.Tests.Hosting.StateMachine;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using Swarmwright.Hosting;

namespace Swarmwright.Tests.Composition;

/// <summary>
/// Composition guard that exercises both the duplicate-<c>BlockedByIndices</c> bug
/// (Bug 1) and the failed-task-deadlock bug (Bug 2) together through the production
/// code path: <see cref="SwarmOrchestrator.BuildBlockedByList"/> wires the persisted
/// <c>BlockedByJson</c> column, then <see cref="StateTransitionService"/> drives the
/// task transitions via EF InMemory. If either fix regresses, this test deadlocks.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class DuplicateAndFailedDeadlockTests
{
    private InMemoryDbContextFactory factory = null!;
    private StateTransitionService service = null!;

    /// <summary>
    /// Initializes the EF InMemory factory and the state-transition service before
    /// each test.
    /// </summary>
    [TestInitialize]
    public void Initialize()
    {
        this.factory = new InMemoryDbContextFactory("CompositionDeadlock_" + Guid.NewGuid());
        this.service = new StateTransitionService(this.factory, Mock.Of<ISwarmEmissionBroker>(), Mock.Of<ISwarmObservationSink>());
    }

    /// <summary>
    /// End-to-end composition: a gate task with duplicate <c>BlockedByIndices</c>
    /// (<c>[0, 0, 1]</c>) and a downstream task blocked on the gate. After
    /// task-0 → Completed, task-1 → Completed, gate → Failed, no task may remain
    /// Blocked. This proves duplicates don't survive the write boundary AND that a
    /// Failed gate unblocks its dependents.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [TestMethod]
    public async Task DuplicateIndices_PlusFailedMidChain_AllDepsResolveThroughStateTransitionService()
    {
        var swarmId = await this.SeedSwarmAsync();

        // Two leaf upstream tasks.
        var task0Id = await this.SeedTaskAsync(swarmId, "task-0", TaskState.Pending, blockedBy: []);
        var task1Id = await this.SeedTaskAsync(swarmId, "task-1", TaskState.Pending, blockedBy: []);

        // Gate task — uses BuildBlockedByList with duplicate indices, simulating the
        // production planning path. The orchestrator's wiring loop runs at plan time;
        // we replicate it here so that the persisted BlockedByJson reflects what
        // the bug-fixed orchestrator would write.
        var gatePlan = new TaskPlan { Subject = "gate", Description = "g", WorkerName = "w" };
        gatePlan.BlockedByIndices.Add(0);
        gatePlan.BlockedByIndices.Add(0);
        gatePlan.BlockedByIndices.Add(1);
        var taskIdsSoFar = new List<string> { task0Id, task1Id, "task-gate" };
        var gateBlockedBy = SwarmOrchestrator.BuildBlockedByList(gatePlan, taskIdsSoFar);
        var gateId = await this.SeedTaskAsync(swarmId, "task-gate", TaskState.Blocked, blockedBy: gateBlockedBy);

        // Downstream task — blocked only on the gate.
        var downstreamId = await this.SeedTaskAsync(
            swarmId,
            "task-3",
            TaskState.Blocked,
            blockedBy: [gateId]);

        // Drive the production transition path. Tasks must move through InProgress
        // before Completed (Pending→Completed is not a legal transition).
        await this.service.TransitionTaskAsync(swarmId, task0Id, TaskState.InProgress, TransitionReasons.PhaseAdvanced);
        await this.service.TransitionTaskAsync(swarmId, task0Id, TaskState.Completed, TransitionReasons.PhaseAdvanced);
        await this.service.TransitionTaskAsync(swarmId, task1Id, TaskState.InProgress, TransitionReasons.PhaseAdvanced);
        await this.service.TransitionTaskAsync(swarmId, task1Id, TaskState.Completed, TransitionReasons.PhaseAdvanced);

        await using (var ctxBeforeFail = this.factory.CreateDbContext())
        {
            var gateBefore = await ctxBeforeFail.Tasks.SingleAsync(t => t.Id == gateId);
            gateBefore.State.Should().Be(
                nameof(TaskState.Pending),
                "gate must promote to Pending after both upstream tasks complete (duplicates would leave a zombie blocker here)");
            DeserializeBlockedBy(gateBefore.BlockedByJson).Should().BeEmpty();
        }

        // Now fail the gate. Without the Bug 2 fix, the downstream stays Blocked forever.
        await this.service.TransitionTaskAsync(swarmId, gateId, TaskState.Failed, TransitionReasons.PhaseAdvanced);

        await using var ctx = this.factory.CreateDbContext();
        var allTasks = await ctx.Tasks.Where(t => t.SwarmId == swarmId).ToListAsync();

        allTasks.Should().NotContain(
            t => t.State == nameof(TaskState.Blocked),
            "no task may remain Blocked after every upstream resolves to a terminal state");

        var downstream = allTasks.Single(t => t.Id == downstreamId);
        downstream.State.Should().Be(nameof(TaskState.Pending));
        DeserializeBlockedBy(downstream.BlockedByJson).Should().BeEmpty();
    }

    private async Task<Guid> SeedSwarmAsync()
    {
        await using var ctx = this.factory.CreateDbContext();
        var swarm = new SwarmEntity
        {
            Id = Guid.NewGuid(),
            Goal = "composition test",
            State = nameof(SwarmInstanceState.Executing),
        };
        ctx.Swarms.Add(swarm);
        await ctx.SaveChangesAsync();
        return swarm.Id;
    }

    private async Task<string> SeedTaskAsync(
        Guid swarmId,
        string subject,
        TaskState state,
        IReadOnlyList<string> blockedBy)
    {
        await using var ctx = this.factory.CreateDbContext();
        var taskId = "t-" + Guid.NewGuid().ToString("N")[..8];
        var task = new TaskEntity
        {
            SwarmId = swarmId,
            Id = taskId,
            Subject = subject,
            Description = "test description",
            WorkerRole = "worker",
            WorkerName = "worker-a",
            State = state.ToString(),
            BlockedByJson = JsonSerializer.Serialize(blockedBy),
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
