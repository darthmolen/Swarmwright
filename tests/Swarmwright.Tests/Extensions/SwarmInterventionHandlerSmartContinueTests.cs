using Swarmwright.Database;
using Swarmwright.Database.Models;
using Swarmwright.Database.Repositories;
using Swarmwright.Events;
using Swarmwright.Extensions;
using Swarmwright.Hosting;
using Swarmwright.Hosting.StateMachine;
using Swarmwright.Models;
using Swarmwright.Models.Enums;
using Swarmwright.Configuration;
using Swarmwright.Tests.Hosting.StateMachine;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Swarmwright.Tests.Extensions;

/// <summary>
/// Tests that exercise the leader-driven Smart Continue path end-to-end.
/// The leader is represented by a scripted <see cref="ILeaderRepairAdvisor"/>
/// so these tests stay deterministic and fast.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SwarmInterventionHandlerSmartContinueTests
{
    private InMemoryDbContextFactory factory = null!;
    private IStateTransitionService stateService = null!;
    private ISwarmRepository repository = null!;
    private Mock<ISwarmManager> manager = null!;

    [TestInitialize]
    public void Initialize()
    {
        this.factory = new InMemoryDbContextFactory("SmartContinue_" + Guid.NewGuid());
        this.stateService = new StateTransitionService(this.factory, Mock.Of<ISwarmEmissionBroker>(), Mock.Of<ISwarmObservationSink>());
        this.repository = new SwarmRepository(this.factory);
        this.manager = new Mock<ISwarmManager>();
    }

    [TestMethod]
    public async Task SmartContinueAsync_WithResetTaskPlan_FlipsTaskToPendingWithoutBumpingRetry()
    {
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.AwaitingIntervention);
        var taskId = await this.SeedTaskAsync(swarmId, TaskState.Failed, retryCount: 2);
        var advisor = new FakeLeaderRepairAdvisor(new RepairPlan(
            ResetTaskIds: [taskId],
            AddTasks: [],
            AbandonTaskIds: [],
            Note: "Leader decided to retry this task after reading the output."));

        var result = await this.BuildHandler(advisor).SmartContinueAsync(swarmId, actor: "alice");

        result.StatusCode.Should().Be(204);

        await using var ctx = this.factory.CreateDbContext();
        var task = await ctx.Tasks.SingleAsync(t => t.SwarmId == swarmId && t.Id == taskId);
        task.State.Should().Be(nameof(TaskState.Pending));
        task.RetryCount.Should().Be(2, "leader_repair_plan must not consume retry budget");

        var row = await ctx.TaskStateTransitions
            .SingleAsync(r => r.TaskId == taskId && r.Reason == TransitionReasons.LeaderRepairPlan);
        row.Actor.Should().Be("alice");
        row.Note.Should().Contain("Leader decided");

        var swarm = await ctx.Swarms.FindAsync(swarmId);
        swarm!.State.Should().Be(nameof(SwarmInstanceState.Executing));
    }

    [TestMethod]
    public async Task SmartContinueAsync_WithAddTasksPlan_SeedsNewTasks()
    {
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.AwaitingIntervention);
        var advisor = new FakeLeaderRepairAdvisor(new RepairPlan(
            ResetTaskIds: [],
            AddTasks:
            [
                new RepairTaskSpec(
                    Subject: "Re-run audit with extra data",
                    Description: "The cost-expert needs a third data source.",
                    WorkerRole: "cost-expert",
                    WorkerName: "cost-expert-1",
                    BlockedBy: null),
            ],
            AbandonTaskIds: [],
            Note: "Added a supplemental audit task."));

        var result = await this.BuildHandler(advisor).SmartContinueAsync(swarmId, actor: "alice");

        result.StatusCode.Should().Be(204);

        await using var ctx = this.factory.CreateDbContext();
        var added = await ctx.Tasks
            .SingleAsync(t => t.SwarmId == swarmId && t.Subject == "Re-run audit with extra data");
        added.State.Should().Be(nameof(TaskState.Pending));
        added.WorkerName.Should().Be("cost-expert-1");
    }

    [TestMethod]
    public async Task SmartContinueAsync_WithBlockedByAddedTask_InitialStateIsBlocked()
    {
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.AwaitingIntervention);
        var advisor = new FakeLeaderRepairAdvisor(new RepairPlan(
            ResetTaskIds: [],
            AddTasks:
            [
                new RepairTaskSpec(
                    Subject: "Follow-up",
                    Description: "Wait for preceding result.",
                    WorkerRole: "cost-expert",
                    WorkerName: "cost-expert-1",
                    BlockedBy: ["task-a"]),
            ],
            AbandonTaskIds: [],
            Note: null));

        var result = await this.BuildHandler(advisor).SmartContinueAsync(swarmId, actor: "alice");

        result.StatusCode.Should().Be(204);

        await using var ctx = this.factory.CreateDbContext();
        var added = await ctx.Tasks.SingleAsync(t => t.SwarmId == swarmId && t.Subject == "Follow-up");
        added.State.Should().Be(nameof(TaskState.Blocked));
        added.BlockedByJson.Should().Contain("task-a");
    }

    [TestMethod]
    public async Task SmartContinueAsync_OnZeroFailedTasksWithViableWork_ShortCircuitsToExecutingWithoutCallingAdvisor()
    {
        // Regression: the hung-swarm demo scenario had architect+security Completed,
        // cost-expert Pending, 5x iac-* Blocked — and zero failed tasks. Before the fix
        // SmartContinue still invoked the advisor, which returned null (nothing to repair)
        // and the handler folded to 409 "Leader did not produce a repair plan." The fix
        // short-circuits: if there's nothing to repair but there IS viable work, transition
        // the swarm to Executing directly.
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.AwaitingIntervention);
        await this.SeedTaskAsync(swarmId, TaskState.Completed, retryCount: 0);
        await this.SeedTaskAsync(swarmId, TaskState.Pending, retryCount: 0);
        await this.SeedTaskAsync(swarmId, TaskState.Blocked, retryCount: 0);
        var advisor = new FakeLeaderRepairAdvisor(null);

        var result = await this.BuildHandler(advisor).SmartContinueAsync(swarmId, actor: "alice");

        result.StatusCode.Should().Be(204);
        advisor.CallCount.Should().Be(
            0,
            "with zero failed tasks the handler must short-circuit — calling the advisor with nothing to repair only produces spurious 409s");

        await using var ctx = this.factory.CreateDbContext();
        var swarm = await ctx.Swarms.FindAsync(swarmId);
        swarm!.State.Should().Be(nameof(SwarmInstanceState.Executing));

        var row = await ctx.SwarmStateTransitions
            .SingleAsync(r => r.SwarmId == swarmId && r.Reason == TransitionReasons.UserSmartContinueNoFailures);
        row.ToState.Should().Be(nameof(SwarmInstanceState.Executing));
        row.Actor.Should().Be("alice");

        this.manager.Verify(m => m.SignalContinue(swarmId), Times.Once);
    }

    [TestMethod]
    public async Task SmartContinueAsync_WhenAdvisorReturnsNull_Returns409()
    {
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.AwaitingIntervention);
        var advisor = new FakeLeaderRepairAdvisor(null);

        var result = await this.BuildHandler(advisor).SmartContinueAsync(swarmId, actor: "alice");

        result.StatusCode.Should().Be(409);

        await using var ctx = this.factory.CreateDbContext();
        var swarm = await ctx.Swarms.FindAsync(swarmId);
        swarm!.State.Should().Be(
            nameof(SwarmInstanceState.AwaitingIntervention),
            "a failed repair must not transition the swarm");
    }

    [TestMethod]
    public async Task SmartContinueAsync_WhenPlanIsEmpty_Returns409()
    {
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.AwaitingIntervention);
        var advisor = new FakeLeaderRepairAdvisor(new RepairPlan(
            ResetTaskIds: [],
            AddTasks: [],
            AbandonTaskIds: [],
            Note: "No-op."));

        var result = await this.BuildHandler(advisor).SmartContinueAsync(swarmId, actor: "alice");

        result.StatusCode.Should().Be(409);
    }

    /// <summary>
    /// Verifies that a rejected Smart Continue (empty plan) emits a
    /// Warning log containing the reason code. Without this, the endpoint
    /// returns 409 silently — the server log gives no hint of which guard
    /// fired, making failed recoveries undiagnosable in production.
    /// </summary>
    /// <returns>A task representing the asynchronous test operation.</returns>
    [TestMethod]
    public async Task SmartContinueAsync_WhenPlanIsEmpty_LogsWarningWithReasonCode()
    {
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.AwaitingIntervention);
        var advisor = new FakeLeaderRepairAdvisor(new RepairPlan(
            ResetTaskIds: [],
            AddTasks: [],
            AbandonTaskIds: [],
            Note: "No-op."));
        var logger = new Mock<ILogger<SwarmInterventionHandler>>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        var result = await this.BuildHandler(advisor, logger.Object).SmartContinueAsync(swarmId, actor: "alice");

        result.StatusCode.Should().Be(409);
        logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, _) => o.ToString()!.Contains("repair_empty", StringComparison.Ordinal)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once,
            "empty repair plans must be logged so operators can tell why the 409 happened");
    }

    /// <summary>
    /// Verifies that a 423 Locked rejection is surfaced as a Warning
    /// carrying the current lock holder. This is the prime candidate for
    /// the "immediate failure after rehydration" scenario and must be
    /// diagnosable from logs alone.
    /// </summary>
    /// <returns>A task representing the asynchronous test operation.</returns>
    [TestMethod]
    public async Task SmartContinueAsync_WhenLockedByAnotherUser_LogsWarningWithLockHolder()
    {
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.AwaitingIntervention);
        await using (var ctx = this.factory.CreateDbContext())
        {
            var swarm = await ctx.Swarms.FindAsync(swarmId);
            swarm!.LockedBy = "bob";
            swarm.LockedAt = DateTime.UtcNow.AddMinutes(-3);
            await ctx.SaveChangesAsync();
        }

        var advisor = new FakeLeaderRepairAdvisor(new RepairPlan(["t"], [], [], null));
        var logger = new Mock<ILogger<SwarmInterventionHandler>>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        var result = await this.BuildHandler(advisor, logger.Object).SmartContinueAsync(swarmId, actor: "alice");

        result.StatusCode.Should().Be(423);
        advisor.CallCount.Should().Be(0, "lock rejection must short-circuit before the LLM");
        logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, _) => o.ToString()!.Contains("bob", StringComparison.Ordinal)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once,
            "lock-rejection logs must name the current holder so admins know who to coordinate with");
    }

    [TestMethod]
    public async Task SmartContinueAsync_OnCompleteSwarm_Returns410WithoutCallingAdvisor()
    {
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.Complete);
        var advisor = new FakeLeaderRepairAdvisor(new RepairPlan(["t"], [], [], null));

        var result = await this.BuildHandler(advisor).SmartContinueAsync(swarmId, actor: "alice");

        result.StatusCode.Should().Be(410);
        advisor.CallCount.Should().Be(0, "terminal swarms must short-circuit before the LLM call");
    }

    // ---- Helpers ----

    private async Task<Guid> SeedSwarmAsync(SwarmInstanceState state, string? templateKey = null)
    {
        await using var ctx = this.factory.CreateDbContext();
        var swarm = new SwarmEntity
        {
            Id = Guid.NewGuid(),
            Goal = "test",
            State = state.ToString(),
            TemplateKey = templateKey,
        };
        ctx.Swarms.Add(swarm);
        await ctx.SaveChangesAsync();
        return swarm.Id;
    }

    private async Task<string> SeedTaskAsync(Guid swarmId, TaskState state, int retryCount)
    {
        await using var ctx = this.factory.CreateDbContext();
        var id = "t-" + Guid.NewGuid().ToString("N")[..8];
        ctx.Tasks.Add(new TaskEntity
        {
            SwarmId = swarmId,
            Id = id,
            Subject = "Seeded",
            Description = "d",
            WorkerRole = "r",
            WorkerName = "w",
            State = state.ToString(),
            RetryCount = retryCount,
        });
        await ctx.SaveChangesAsync();
        return id;
    }

    private SwarmInterventionHandler BuildHandler(
        ILeaderRepairAdvisor advisor,
        ILogger<SwarmInterventionHandler>? logger = null)
    {
        return new SwarmInterventionHandler(
            this.manager.Object,
            this.stateService,
            this.repository,
            advisor,
            Options.Create(new SwarmOptions()),
            logger ?? NullLogger<SwarmInterventionHandler>.Instance);
    }

    private sealed class FakeLeaderRepairAdvisor : ILeaderRepairAdvisor
    {
        private readonly RepairPlan? plan;

        public FakeLeaderRepairAdvisor(RepairPlan? plan)
        {
            this.plan = plan;
        }

        public int CallCount { get; private set; }

        public Task<RepairPlan?> RequestRepairAsync(
            Guid swarmId,
            IReadOnlyList<TaskEntity> failedTasks,
            string? templateKey,
            CancellationToken cancellationToken)
        {
            this.CallCount++;
            return Task.FromResult(this.plan);
        }
    }
}
