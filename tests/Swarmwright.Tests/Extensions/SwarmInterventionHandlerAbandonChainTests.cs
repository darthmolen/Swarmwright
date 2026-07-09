using System.Text.Json;
using Swarmwright.Configuration;
using Swarmwright.Database;
using Swarmwright.Database.Models;
using Swarmwright.Database.Repositories;
using Swarmwright.Extensions;
using Swarmwright.Hosting;
using Swarmwright.Hosting.StateMachine;
using Swarmwright.Models;
using Swarmwright.Models.Enums;
using Swarmwright.Tests.Hosting.StateMachine;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Swarmwright.Events;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Swarmwright.Tests.Extensions;

/// <summary>
/// Covers the blocked_by chain-stripping behavior that Smart Continue
/// applies when the leader abandons one or more failed tasks: any
/// surviving task that depended on an abandoned id must have that id
/// pruned from its <c>blocked_by_json</c>, and must flip from Blocked
/// to Pending when the prune empties the dependency list.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SwarmInterventionHandlerAbandonChainTests
{
    private InMemoryDbContextFactory factory = null!;
    private IStateTransitionService stateService = null!;
    private ISwarmRepository repository = null!;
    private Mock<ISwarmManager> manager = null!;

    [TestInitialize]
    public void Initialize()
    {
        this.factory = new InMemoryDbContextFactory("AbandonChain_" + Guid.NewGuid());
        this.stateService = new StateTransitionService(this.factory, Mock.Of<ISwarmEmissionBroker>(), Mock.Of<ISwarmObservationSink>());
        this.repository = new SwarmRepository(this.factory);
        this.manager = new Mock<ISwarmManager>();
    }

    [TestMethod]
    public async Task SmartContinue_WhenSurvivingTaskBlockedByAbandonedTask_StripsAbandonedIdFromBlockedBy()
    {
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.AwaitingIntervention);
        await this.SeedTaskAsync(swarmId, "a", TaskState.Failed, blockedBy: []);
        await this.SeedTaskAsync(swarmId, "b", TaskState.Blocked, blockedBy: ["a"]);
        await this.SeedTaskAsync(swarmId, "x", TaskState.Pending, blockedBy: []);
        await this.SeedTaskAsync(swarmId, "c", TaskState.Blocked, blockedBy: ["a", "x"]);

        var advisor = new FakeLeaderRepairAdvisor(new RepairPlan(
            ResetTaskIds: [],
            AddTasks: [],
            AbandonTaskIds: ["a"],
            Note: "Upstream source gone; skip."));

        var result = await this.BuildHandler(advisor).SmartContinueAsync(swarmId, actor: "alice");

        result.StatusCode.Should().Be(204);

        await using var ctx = this.factory.CreateDbContext();
        var b = await ctx.Tasks.SingleAsync(t => t.SwarmId == swarmId && t.Id == "b");
        BlockedByOf(b).Should().BeEmpty("abandoned dep a was stripped and b had no other deps");
        b.State.Should().Be(nameof(TaskState.Pending), "empty deps after strip must promote b to Pending");

        var c = await ctx.Tasks.SingleAsync(t => t.SwarmId == swarmId && t.Id == "c");
        BlockedByOf(c).Should().BeEquivalentTo(["x"], "strip removes only abandoned ids, leaves valid deps intact");
        c.State.Should().Be(nameof(TaskState.Blocked), "c still waits on x so it stays Blocked");
    }

    [TestMethod]
    public async Task SmartContinue_AbandonedTaskRemainsFailed()
    {
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.AwaitingIntervention);
        await this.SeedTaskAsync(swarmId, "a", TaskState.Failed, retryCount: 3, blockedBy: []);
        await this.SeedTaskAsync(swarmId, "b", TaskState.Blocked, blockedBy: ["a"]);

        var advisor = new FakeLeaderRepairAdvisor(new RepairPlan(
            ResetTaskIds: [],
            AddTasks: [],
            AbandonTaskIds: ["a"],
            Note: "abandon"));

        var result = await this.BuildHandler(advisor).SmartContinueAsync(swarmId, actor: "alice");

        result.StatusCode.Should().Be(204);

        await using var ctx = this.factory.CreateDbContext();
        var a = await ctx.Tasks.SingleAsync(t => t.SwarmId == swarmId && t.Id == "a");
        a.State.Should().Be(nameof(TaskState.Failed), "abandoned tasks stay terminal");
        a.RetryCount.Should().Be(3, "abandoning must not touch retry_count");
    }

    [TestMethod]
    public async Task SmartContinue_WhenNoAbandonedTasks_DoesNotModifyBlockedByChains()
    {
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.AwaitingIntervention);
        await this.SeedTaskAsync(swarmId, "a", TaskState.Failed, blockedBy: []);
        await this.SeedTaskAsync(swarmId, "b", TaskState.Blocked, blockedBy: ["a"]);

        var advisor = new FakeLeaderRepairAdvisor(new RepairPlan(
            ResetTaskIds: ["a"],
            AddTasks: [],
            AbandonTaskIds: [],
            Note: "retry a"));

        var result = await this.BuildHandler(advisor).SmartContinueAsync(swarmId, actor: "alice");

        result.StatusCode.Should().Be(204);

        await using var ctx = this.factory.CreateDbContext();
        var b = await ctx.Tasks.SingleAsync(t => t.SwarmId == swarmId && t.Id == "b");
        BlockedByOf(b).Should().BeEquivalentTo(["a"], "no abandoned ids means no strip pass");
        b.State.Should().Be(nameof(TaskState.Blocked), "b remains Blocked while a is not yet Completed");
    }

    // ---- Helpers ----

    private SwarmInterventionHandler BuildHandler(ILeaderRepairAdvisor advisor)
    {
        return new SwarmInterventionHandler(
            this.manager.Object,
            this.stateService,
            this.repository,
            advisor,
            Options.Create(new SwarmOptions()),
            NullLogger<SwarmInterventionHandler>.Instance);
    }

    private static List<string> BlockedByOf(TaskEntity t) =>
        JsonSerializer.Deserialize<List<string>>(t.BlockedByJson ?? "[]") ?? [];

    private async Task<Guid> SeedSwarmAsync(SwarmInstanceState state)
    {
        await using var ctx = this.factory.CreateDbContext();
        var swarm = new SwarmEntity
        {
            Id = Guid.NewGuid(),
            Goal = "test",
            State = state.ToString(),
        };
        ctx.Swarms.Add(swarm);
        await ctx.SaveChangesAsync();
        return swarm.Id;
    }

    private async Task SeedTaskAsync(
        Guid swarmId,
        string id,
        TaskState state,
        IReadOnlyList<string> blockedBy,
        int retryCount = 0)
    {
        await using var ctx = this.factory.CreateDbContext();
        ctx.Tasks.Add(new TaskEntity
        {
            SwarmId = swarmId,
            Id = id,
            Subject = id,
            Description = id,
            WorkerRole = "r",
            WorkerName = "w",
            State = state.ToString(),
            BlockedByJson = JsonSerializer.Serialize(blockedBy),
            RetryCount = retryCount,
        });
        await ctx.SaveChangesAsync();
    }

    private sealed class FakeLeaderRepairAdvisor : ILeaderRepairAdvisor
    {
        private readonly RepairPlan? plan;

        public FakeLeaderRepairAdvisor(RepairPlan? plan)
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
