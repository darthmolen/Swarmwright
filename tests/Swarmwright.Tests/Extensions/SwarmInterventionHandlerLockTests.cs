using Swarmwright.Configuration;
using Swarmwright.Database;
using Swarmwright.Database.Models;
using Swarmwright.Database.Repositories;
using Swarmwright.Events;
using Swarmwright.Extensions;
using Swarmwright.Hosting;
using Swarmwright.Hosting.StateMachine;
using Swarmwright.Models;
using Swarmwright.Models.Enums;
using Swarmwright.Tests.Hosting.StateMachine;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Swarmwright.Tests.Extensions;

/// <summary>
/// Lock-subsystem tests for <see cref="SwarmInterventionHandler"/>:
/// acquire, release, steal, stale-holder rejection, and the cross-cutting
/// 423 Locked response other mutators emit when a caller is not the holder.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SwarmInterventionHandlerLockTests
{
    private InMemoryDbContextFactory factory = null!;
    private IStateTransitionService stateService = null!;
    private ISwarmRepository repository = null!;
    private Mock<ISwarmManager> manager = null!;
    private SwarmOptions options = null!;
    private SwarmInterventionHandler handler = null!;

    [TestInitialize]
    public void Initialize()
    {
        this.factory = new InMemoryDbContextFactory("Lock_" + Guid.NewGuid());
        this.stateService = new StateTransitionService(this.factory, Mock.Of<ISwarmEmissionBroker>(), Mock.Of<ISwarmObservationSink>());
        this.repository = new SwarmRepository(this.factory);
        this.manager = new Mock<ISwarmManager>();
        this.options = new SwarmOptions { MaxTaskRetries = 1 };
        this.handler = this.BuildHandler(
            new StubAdvisor(new RepairPlan(ResetTaskIds: ["stub"], AddTasks: [], AbandonTaskIds: [], Note: "stub plan")));
    }

    // ---- Lock POST ----

    [TestMethod]
    public async Task LockAsync_WhenUnlocked_Sets_lockedBy_AndWritesAuditRow()
    {
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.AwaitingIntervention);

        var result = await this.handler.LockAsync(swarmId, actor: "alice", steal: false);

        result.StatusCode.Should().Be(200);

        await using var ctx = this.factory.CreateDbContext();
        var swarm = await ctx.Swarms.FindAsync(swarmId);
        swarm!.LockedBy.Should().Be("alice");
        swarm.LockedAt.Should().NotBeNull();

        var auditRow = await ctx.SwarmStateTransitions
            .SingleAsync(r => r.SwarmId == swarmId && r.Reason == TransitionReasons.LockAcquired);
        auditRow.Actor.Should().Be("alice");
        auditRow.FromState.Should().Be(auditRow.ToState);
    }

    [TestMethod]
    public async Task LockAsync_WhenHeldBySameCaller_IsIdempotent_ReturnsOk()
    {
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.AwaitingIntervention);
        await this.handler.LockAsync(swarmId, actor: "alice", steal: false);

        var result = await this.handler.LockAsync(swarmId, actor: "alice", steal: false);

        result.StatusCode.Should().Be(200);

        await using var ctx = this.factory.CreateDbContext();
        var swarm = await ctx.Swarms.FindAsync(swarmId);
        swarm!.LockedBy.Should().Be("alice");
    }

    [TestMethod]
    public async Task LockAsync_WhenHeldByOther_Returns423()
    {
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.AwaitingIntervention);
        await this.handler.LockAsync(swarmId, actor: "alice", steal: false);

        var result = await this.handler.LockAsync(swarmId, actor: "bob", steal: false);

        result.StatusCode.Should().Be(423);
    }

    [TestMethod]
    public async Task LockAsync_WithStealTrue_OverridesLockAndWritesStolenRow()
    {
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.AwaitingIntervention);
        await this.handler.LockAsync(swarmId, actor: "alice", steal: false);

        var result = await this.handler.LockAsync(swarmId, actor: "bob", steal: true);

        result.StatusCode.Should().Be(200);

        await using var ctx = this.factory.CreateDbContext();
        var swarm = await ctx.Swarms.FindAsync(swarmId);
        swarm!.LockedBy.Should().Be("bob");

        var stolenRow = await ctx.SwarmStateTransitions
            .SingleAsync(r => r.SwarmId == swarmId && r.Reason == TransitionReasons.LockStolen);
        stolenRow.Actor.Should().Be("bob");
    }

    [TestMethod]
    public async Task LockAsync_OnTerminalSwarm_Returns410()
    {
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.Complete);

        var result = await this.handler.LockAsync(swarmId, actor: "alice", steal: false);

        result.StatusCode.Should().Be(410);
    }

    // ---- Lock DELETE ----

    [TestMethod]
    public async Task UnlockAsync_AsHolder_Clears_lockedBy_AndWritesReleasedRow()
    {
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.AwaitingIntervention);
        await this.handler.LockAsync(swarmId, actor: "alice", steal: false);

        var result = await this.handler.UnlockAsync(swarmId, actor: "alice");

        result.StatusCode.Should().Be(204);

        await using var ctx = this.factory.CreateDbContext();
        var swarm = await ctx.Swarms.FindAsync(swarmId);
        swarm!.LockedBy.Should().BeNull();

        (await ctx.SwarmStateTransitions.AnyAsync(r => r.Reason == TransitionReasons.LockReleased))
            .Should().BeTrue();
    }

    [TestMethod]
    public async Task UnlockAsync_AsNonHolder_Returns403()
    {
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.AwaitingIntervention);
        await this.handler.LockAsync(swarmId, actor: "alice", steal: false);

        var result = await this.handler.UnlockAsync(swarmId, actor: "bob");

        result.StatusCode.Should().Be(403);
    }

    [TestMethod]
    public async Task UnlockAsync_WhenNotLocked_ReturnsNoContent()
    {
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.AwaitingIntervention);

        var result = await this.handler.UnlockAsync(swarmId, actor: "alice");

        result.StatusCode.Should().Be(204);
    }

    // ---- Lock-mismatch on state mutators ----

    [TestMethod]
    public async Task ContinueAsync_WhenLockedByOther_Returns423()
    {
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.AwaitingIntervention);
        await this.SeedTaskAsync(swarmId, TaskState.Failed, 0);
        await this.handler.LockAsync(swarmId, actor: "alice", steal: false);

        var result = await this.handler.ContinueAsync(swarmId, actor: "bob");

        result.StatusCode.Should().Be(423);
    }

    [TestMethod]
    public async Task ContinueAsync_WhenLockedBySelf_SucceedsAndReleasesLock()
    {
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.AwaitingIntervention);
        await this.SeedTaskAsync(swarmId, TaskState.Failed, 0);
        await this.handler.LockAsync(swarmId, actor: "alice", steal: false);

        var result = await this.handler.ContinueAsync(swarmId, actor: "alice");

        result.StatusCode.Should().Be(204);

        await using var ctx = this.factory.CreateDbContext();
        var swarm = await ctx.Swarms.FindAsync(swarmId);
        swarm!.LockedBy.Should().BeNull();
    }

    // ---- Smart Continue stub ----

    [TestMethod]
    public async Task SmartContinueAsync_OnCompleteSwarm_Returns410()
    {
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.Complete);

        var result = await this.handler.SmartContinueAsync(swarmId, actor: "alice");

        result.StatusCode.Should().Be(410);
    }

    [TestMethod]
    public async Task SmartContinueAsync_OnCreatedSwarm_Returns409()
    {
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.Created);

        var result = await this.handler.SmartContinueAsync(swarmId, actor: "alice");

        result.StatusCode.Should().Be(409);
    }

    [TestMethod]
    public async Task SmartContinueAsync_OnAwaitingIntervention_TransitionsExecutingWithSmartReason()
    {
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.AwaitingIntervention);

        var result = await this.handler.SmartContinueAsync(swarmId, actor: "alice");

        result.StatusCode.Should().Be(204);

        await using var ctx = this.factory.CreateDbContext();
        var swarm = await ctx.Swarms.FindAsync(swarmId);
        swarm!.State.Should().Be(nameof(SwarmInstanceState.Executing));
        var row = await ctx.SwarmStateTransitions
            .SingleAsync(r => r.SwarmId == swarmId && r.Reason == TransitionReasons.UserSmartContinue);
        row.Actor.Should().Be("alice");
    }

    // ---- Helpers ----

    private SwarmInterventionHandler BuildHandler(ILeaderRepairAdvisor advisor)
    {
        return new SwarmInterventionHandler(
            this.manager.Object,
            this.stateService,
            this.repository,
            advisor,
            Options.Create(this.options),
            NullLogger<SwarmInterventionHandler>.Instance);
    }

    private async Task<Guid> SeedSwarmAsync(SwarmInstanceState state)
    {
        await using var ctx = this.factory.CreateDbContext();
        var swarm = new SwarmEntity
        {
            Id = Guid.NewGuid(),
            Goal = "t",
            State = state.ToString(),
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
            Subject = "s",
            Description = "d",
            WorkerRole = "r",
            WorkerName = "w",
            State = state.ToString(),
            RetryCount = retryCount,
        });
        await ctx.SaveChangesAsync();
        return id;
    }

    private sealed class StubAdvisor : ILeaderRepairAdvisor
    {
        private readonly RepairPlan plan;

        public StubAdvisor(RepairPlan plan)
        {
            this.plan = plan;
        }

        public System.Threading.Tasks.Task<RepairPlan?> RequestRepairAsync(
            Guid swarmId,
            IReadOnlyList<TaskEntity> failedTasks,
            string? templateKey,
            CancellationToken cancellationToken) => System.Threading.Tasks.Task.FromResult<RepairPlan?>(this.plan);
    }
}
