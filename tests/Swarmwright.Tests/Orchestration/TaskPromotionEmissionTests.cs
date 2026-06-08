using System.Text.Json;
using Swarmwright.Configuration;
using Swarmwright.Core;
using Swarmwright.Database.Models;
using Swarmwright.Database.Repositories;
using Swarmwright.Events;
using Swarmwright.Extensions;
using Swarmwright.Hosting;
using Swarmwright.Hosting.StateMachine;
using Swarmwright.Models;
using Swarmwright.Models.Enums;
using Swarmwright.Services;
using Swarmwright.Tests.Hosting.StateMachine;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Swarmwright.Tests.Orchestration;

/// <summary>
/// Composition tests: every path that leads to a task state transition
/// ultimately lands on <see cref="ISwarmEmissionBroker.EmitTaskUpdatedAsync"/>.
/// Callers no longer thread an adapter — the state service does it once,
/// via the broker, for every successful DB commit.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class TaskPromotionEmissionTests : IDisposable
{
    private InMemoryDbContextFactory factory = null!;
    private Mock<ISwarmEmissionBroker> broker = null!;
    private StateTransitionService stateService = null!;
    private SwarmRepository repository = null!;
    private InboxSystem inboxSystem = null!;
    private SwarmService swarmService = null!;
    private Guid swarmId;

    [TestInitialize]
    public async Task Initialize()
    {
        this.factory = new InMemoryDbContextFactory("TaskPromotionEmit_" + Guid.NewGuid());
        this.broker = new Mock<ISwarmEmissionBroker>();
        this.stateService = new StateTransitionService(this.factory, this.broker.Object, Mock.Of<ISwarmObservationSink>());
        this.repository = new SwarmRepository(this.factory);
        this.inboxSystem = new InboxSystem();

        this.swarmId = Guid.NewGuid();

        this.swarmService = new SwarmService(
            this.inboxSystem,
            new TeamRegistry(),
            this.repository);

        await this.swarmService.CreateSwarmAsync(this.swarmId, "test");

        await this.stateService.TransitionSwarmAsync(
            this.swarmId,
            SwarmInstanceState.Planning,
            TransitionReasons.PhaseAdvanced,
            actor: "system");
        await this.stateService.TransitionSwarmAsync(
            this.swarmId,
            SwarmInstanceState.Spawning,
            TransitionReasons.PhaseAdvanced,
            actor: "system");
        await this.stateService.TransitionSwarmAsync(
            this.swarmId,
            SwarmInstanceState.Executing,
            TransitionReasons.PhaseAdvanced,
            actor: "system");
    }

    [TestCleanup]
    public void Cleanup() => this.Dispose();

    public void Dispose()
    {
        // No owned disposable resources after the TaskBoard removal.
    }

    [TestMethod]
    public async Task CompletingTaskA_InvokesBrokerForDependentBPendingPromotion()
    {
        var aId = await this.SeedTaskAsync("A", blockedBy: []);
        var bId = await this.SeedTaskAsync("B", blockedBy: [aId]);

        // F01.3 ceremony — single-write through IStateTransitionService:
        //   1. A: Pending -> InProgress
        //   2. A: InProgress -> Completed (the state service strips A from
        //      every dependent's blocked_by list, promotes any newly empty
        //      list from Blocked to Pending in the same DB transaction,
        //      and emits SWARM_TASK_UPDATED for each promoted dependent)
        await this.stateService.TransitionTaskAsync(
            this.swarmId,
            aId,
            TaskState.InProgress,
            TransitionReasons.PhaseAdvanced,
            actor: "system");

        await this.stateService.TransitionTaskAsync(
            this.swarmId,
            aId,
            TaskState.Completed,
            TransitionReasons.PhaseAdvanced,
            actor: "system",
            result: "done");

        // A: Pending -> InProgress
        this.broker.Verify(
            b => b.EmitTaskUpdatedAsync(
                this.swarmId,
                aId,
                TaskState.InProgress,
                "A",
                It.IsAny<CancellationToken>()),
            Times.Once);

        // B: Blocked -> Pending (dep promotion via SwarmService)
        this.broker.Verify(
            b => b.EmitTaskUpdatedAsync(
                this.swarmId,
                bId,
                TaskState.Pending,
                "B",
                It.IsAny<CancellationToken>()),
            Times.Once,
            "dep promotion must emit exactly once — this is the bug that kicked off the emission refactor");

        // A: InProgress -> Completed
        this.broker.Verify(
            b => b.EmitTaskUpdatedAsync(
                this.swarmId,
                aId,
                TaskState.Completed,
                "A",
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [TestMethod]
    public async Task InterventionHandler_SmartContinueReset_InvokesBrokerForResetTaskPending()
    {
        var handlerSwarmId = Guid.NewGuid();
        await using (var ctx = this.factory.CreateDbContext())
        {
            ctx.Swarms.Add(new SwarmEntity
            {
                Id = handlerSwarmId,
                Goal = "handler test",
                State = SwarmInstanceState.AwaitingIntervention.ToString(),
            });
            await ctx.SaveChangesAsync();
        }

        var failedId = "t-" + Guid.NewGuid().ToString("N")[..8];
        await using (var ctx = this.factory.CreateDbContext())
        {
            ctx.Tasks.Add(new TaskEntity
            {
                SwarmId = handlerSwarmId,
                Id = failedId,
                Subject = "failed task",
                Description = "d",
                WorkerRole = "worker",
                WorkerName = "architect",
                State = nameof(TaskState.Failed),
            });
            await ctx.SaveChangesAsync();
        }

        var scriptedAdvisor = new ScriptedAdvisor(new RepairPlan(
            ResetTaskIds: [failedId],
            AddTasks: [],
            AbandonTaskIds: [],
            Note: "retry"));

        var manager = new Mock<ISwarmManager>();

        var handler = new SwarmInterventionHandler(
            manager.Object,
            this.stateService,
            this.repository,
            scriptedAdvisor,
            Options.Create(new SwarmOptions()),
            NullLogger<SwarmInterventionHandler>.Instance);
        var result = await handler.SmartContinueAsync(handlerSwarmId, actor: "alice");

        result.StatusCode.Should().Be(204);

        this.broker.Verify(
            b => b.EmitTaskUpdatedAsync(
                handlerSwarmId,
                failedId,
                TaskState.Pending,
                "architect",
                It.IsAny<CancellationToken>()),
            Times.Once,
            "user-driven Failed->Pending must still emit — the broker is invoked regardless of which call path wrote the transition");
    }

    // ---- Helpers ----

    private async Task<string> SeedTaskAsync(string subject, IReadOnlyList<string> blockedBy)
    {
        var task = new SwarmTask
        {
            Id = "t-" + Guid.NewGuid().ToString("N")[..8],
            Subject = subject,
            Description = subject,
            WorkerRole = "worker",
            WorkerName = subject,
        };
        foreach (var dep in blockedBy)
        {
            task.BlockedBy.Add(dep);
        }

        await this.swarmService.AddTaskAsync(task).ConfigureAwait(false);
        return task.Id;
    }

    private sealed class ScriptedAdvisor : ILeaderRepairAdvisor
    {
        private readonly RepairPlan? plan;

        public ScriptedAdvisor(RepairPlan? plan)
        {
            this.plan = plan;
        }

        public Task<RepairPlan?> RequestRepairAsync(
            Guid swarmId,
            IReadOnlyList<TaskEntity> failedTasks,
            string? templateKey,
            CancellationToken cancellationToken) => Task.FromResult(this.plan);
    }
}
