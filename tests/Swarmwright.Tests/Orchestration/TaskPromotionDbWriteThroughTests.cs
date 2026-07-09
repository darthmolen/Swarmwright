using System.Text.Json;
using Swarmwright.Core;
using Swarmwright.Database;
using Swarmwright.Database.Models;
using Swarmwright.Database.Repositories;
using Swarmwright.Events;
using Swarmwright.Hosting.StateMachine;
using Swarmwright.Models;
using Swarmwright.Models.Enums;
using Swarmwright.Services;
using Swarmwright.Tests.Hosting.StateMachine;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using Swarmwright.Hosting;

namespace Swarmwright.Tests.Orchestration;

/// <summary>
/// Covers the Blocked-&gt;Pending promotion seam in
/// <see cref="IStateTransitionService"/>: the promotion must write through to
/// <see cref="TaskEntity.State"/> and the <c>blocked_by_json</c> column,
/// otherwise the next round's Pending-&gt;InProgress transition reads a stale
/// DB state of Blocked and the state guard rejects <c>Blocked -&gt; InProgress</c>.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class TaskPromotionDbWriteThroughTests : IDisposable
{
    private InMemoryDbContextFactory factory = null!;
    private StateTransitionService stateService = null!;
    private ISwarmRepository repository = null!;
    private InboxSystem inboxSystem = null!;
    private SwarmService swarmService = null!;
    private Guid swarmId;

    [TestInitialize]
    public async Task Initialize()
    {
        this.factory = new InMemoryDbContextFactory("TaskPromotion_" + Guid.NewGuid());
        this.stateService = new StateTransitionService(this.factory, Mock.Of<ISwarmEmissionBroker>(), Mock.Of<ISwarmObservationSink>());
        this.repository = new SwarmRepository(this.factory);
        this.inboxSystem = new InboxSystem();

        this.swarmId = Guid.NewGuid();

        this.swarmService = new SwarmService(
            this.inboxSystem,
            new TeamRegistry(),
            this.repository);

        // CreateSwarmAsync owns both the repo insert and the in-memory state setup.
        await this.swarmService.CreateSwarmAsync(this.swarmId, "test");

        // Seeding happens while the swarm is in Executing, since we assert
        // on task state transitions that presuppose the swarm is running.
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
    public async Task CompletingTaskA_PromotesDependentTaskB_InBothMemoryAndDatabase()
    {
        var aId = await this.SeedTaskAsync("A", blockedBy: []);
        var bId = await this.SeedTaskAsync("B", blockedBy: [aId]);

        // F01.3 ceremony: every transition goes through the state service.
        // The Pending->InProgress→Completed sequence on A walks A through
        // its lifecycle; the Completed call internally strips A from B's
        // blocked_by list and promotes B from Blocked to Pending in the
        // same DB transaction.
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

        // The next round will try Pending->InProgress on B. That call must
        // succeed — the DB must already reflect B's Blocked->Pending
        // promotion after A completes. Without the write-through fix, the
        // state service sees DB state=Blocked and the guard rejects.
        await this.stateService
            .Invoking(s => s.TransitionTaskAsync(
                this.swarmId,
                bId,
                TaskState.InProgress,
                TransitionReasons.PhaseAdvanced,
                actor: "system"))
            .Should()
            .NotThrowAsync<InvalidStateTransitionException>(
                "completing A must promote B from Blocked to Pending in the DB, "
                + "otherwise the next-round Pending->InProgress transition fails the guard");

        await using var ctx = this.factory.CreateDbContext();
        var b = await ctx.Tasks.SingleAsync(t => t.Id == bId);
        b.State.Should().Be(
            nameof(TaskState.InProgress),
            "after the full ceremony B should end up InProgress (Blocked->Pending->InProgress)");

        BlockedByOf(b).Should().BeEmpty(
            "A was removed from B's blocked_by when A completed; DB must reflect that");

        var promotion = await ctx.TaskStateTransitions
            .Where(r => r.TaskId == bId && r.FromState == nameof(TaskState.Blocked)
                && r.ToState == nameof(TaskState.Pending))
            .SingleAsync();
        promotion.Reason.Should().Be(
            TransitionReasons.PhaseAdvanced,
            "auto-promotion after a dep completes is a normal orchestration advance");
    }

    [TestMethod]
    public async Task CompletingTaskA_WithDependentStillBlockedOnOther_StripsACorrectlyInDatabase()
    {
        var aId = await this.SeedTaskAsync("A", blockedBy: []);
        var otherId = await this.SeedTaskAsync("other", blockedBy: []);
        var cId = await this.SeedTaskAsync("C", blockedBy: [aId, otherId]);

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

        await using var ctx = this.factory.CreateDbContext();
        var c = await ctx.Tasks.SingleAsync(t => t.Id == cId);
        c.State.Should().Be(
            nameof(TaskState.Blocked),
            "C still depends on 'other', it must not flip to Pending yet");
        BlockedByOf(c).Should().BeEquivalentTo(
            [otherId],
            "A was stripped from C.blocked_by in the DB; the remaining dep stays");
    }

    // ---- Helpers ----

    private static List<string> BlockedByOf(TaskEntity t) =>
        JsonSerializer.Deserialize<List<string>>(t.BlockedByJson ?? "[]") ?? [];

    private async Task<string> SeedTaskAsync(string subject, IReadOnlyList<string> blockedBy)
    {
        var id = "t-" + Guid.NewGuid().ToString("N")[..8];
        var task = new SwarmTask
        {
            Id = id,
            Subject = subject,
            Description = subject,
            WorkerRole = "r",
            WorkerName = subject,
        };
        foreach (var dep in blockedBy)
        {
            task.BlockedBy.Add(dep);
        }

        await this.swarmService.AddTaskAsync(task);
        return id;
    }
}
