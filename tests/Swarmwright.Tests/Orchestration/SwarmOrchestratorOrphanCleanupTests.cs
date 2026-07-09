using Swarmwright.Core;
using Swarmwright.Database.Repositories;
using Swarmwright.Events;
using Swarmwright.Events.AgUI;
using Swarmwright.Hosting.StateMachine;
using Swarmwright.Models;
using Swarmwright.Models.Enums;
using Swarmwright.Orchestration;
using Swarmwright.Services;
using Swarmwright.Tests.Hosting.StateMachine;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Moq;

namespace Swarmwright.Tests.Orchestration;

/// <summary>
/// Covers the orchestrator's orphan-cleanup contract introduced by the
/// <c>2026-04-24 orphan-in-progress-defense-in-depth</c> plan. When the swarm
/// exits abnormally (crash or user cancellation), every task still in
/// <see cref="TaskState.InProgress"/> in memory must be transitioned to
/// <see cref="TaskState.Failed"/> <b>before</b> the swarm-level terminal
/// transition is written. That way the audit trail shows the tasks cleaning
/// up first and the swarm flipping terminal last — matching how an operator
/// reads "what happened last."
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SwarmOrchestratorOrphanCleanupTests : IDisposable
{
    private static readonly string[] ExpectedInFlightIds = ["t-in-flight-1", "t-in-flight-2"];

    private InMemoryDbContextFactory dbFactory = null!;
    private SwarmRepository repository = null!;
    private SwarmService swarmService = null!;
    private NoOpStateTransitionService transitionService = null!;
    private SwarmEventBus eventBus = null!;
    private SwarmEventAdapter agUiAdapter = null!;
    private HttpClient httpClient = null!;
    private Guid currentSwarmId;

    [TestInitialize]
    public async Task TestInitialize()
    {
        this.dbFactory = new InMemoryDbContextFactory("OrphanCleanup_" + Guid.NewGuid());
        this.repository = new SwarmRepository(this.dbFactory);
        this.swarmService = new SwarmService(
            new InboxSystem(),
            new TeamRegistry(),
            this.repository);
        this.eventBus = new SwarmEventBus();
        this.agUiAdapter = new SwarmEventAdapter();
        this.transitionService = new NoOpStateTransitionService(this.agUiAdapter);
        this.httpClient = new HttpClient();
        this.currentSwarmId = Guid.NewGuid();
        await this.swarmService.CreateSwarmAsync(this.currentSwarmId, "test");
    }

    public void Dispose()
    {
        this.httpClient?.Dispose();
    }

    [TestMethod]
    public async Task FailInFlightTasksAsync_WithMultipleTaskStates_TransitionsOnlyInProgressTasksToFailed()
    {
        // Arrange — seed the task board with a mix of states. The helper must
        // transition ONLY the InProgress ones and leave everything else alone.
        var orchestrator = this.CreateOrchestrator();

        await this.SeedTaskAsync("t-completed", TaskState.Completed);
        await this.SeedTaskAsync("t-pending", TaskState.Pending);
        await this.SeedTaskAsync("t-in-flight-1", TaskState.InProgress);
        await this.SeedTaskAsync("t-in-flight-2", TaskState.InProgress);
        await this.SeedTaskAsync("t-failed", TaskState.Failed);
        await this.SeedTaskAsync("t-blocked", TaskState.Blocked);

        // Act — invoke the helper directly (via InternalsVisibleTo).
        await orchestrator.FailInFlightTasksAsync(
            this.currentSwarmId,
            TransitionReasons.RunFailed,
            note: "Swarm crashed mid-run; task was in flight",
            CancellationToken.None);

        // Assert — exactly the two in-flight tasks got transitioned, with
        // Failed as the target, run_failed as the reason, and retry delta 0.
        this.transitionService.TaskCalls.Should().HaveCount(
            2,
            "only the two InProgress tasks should have been touched");

        this.transitionService.TaskCalls.Should().OnlyContain(c =>
            c.SwarmId == this.currentSwarmId
            && c.ToState == TaskState.Failed
            && c.Reason == TransitionReasons.RunFailed
            && c.Actor == "system"
            && c.Delta == 0);

        this.transitionService.TaskCalls.Select(c => c.TaskId).Should().BeEquivalentTo(
            ExpectedInFlightIds);

        // And critically: no swarm-level transition happened from the helper.
        // The caller (the catch block) owns the swarm-level terminal write.
        this.transitionService.SwarmCalls.Should().BeEmpty(
            "FailInFlightTasksAsync is task-level only; callers own the swarm-level transition");
    }

    [TestMethod]
    public async Task FailInFlightTasksAsync_WithNoInProgressTasks_IsANoOp()
    {
        // Arrange — no InProgress tasks at all; the helper should see nothing to do.
        var orchestrator = this.CreateOrchestrator();
        await this.SeedTaskAsync("t-completed", TaskState.Completed);
        await this.SeedTaskAsync("t-failed", TaskState.Failed);

        // Act.
        await orchestrator.FailInFlightTasksAsync(
            this.currentSwarmId,
            TransitionReasons.RunFailed,
            note: null,
            CancellationToken.None);

        // Assert — zero task transitions.
        this.transitionService.TaskCalls.Should().BeEmpty(
            "no in-flight tasks means no cleanup transitions");
    }

    [TestMethod]
    public async Task FailInFlightTasksAsync_UsesPassedReasonAndNote_ForCancellationVsCrashDisambiguation()
    {
        // Arrange — the helper is called by both catch blocks. The crash catch
        // passes run_failed; the cancel catch passes user_cancel. The helper must
        // faithfully forward whichever reason the caller chose so the audit trail
        // tells the operator "this was a cancel, not a crash."
        var orchestrator = this.CreateOrchestrator();
        await this.SeedTaskAsync("t-in-flight", TaskState.InProgress);

        // Act — simulate the cancel-catch call site.
        await orchestrator.FailInFlightTasksAsync(
            this.currentSwarmId,
            TransitionReasons.UserCancel,
            note: "Swarm cancelled mid-run; worker did not complete",
            CancellationToken.None);

        // Assert.
        this.transitionService.TaskCalls.Should().ContainSingle()
            .Which.Reason.Should().Be(TransitionReasons.UserCancel);
    }

    private async Task SeedTaskAsync(string id, TaskState state)
    {
        // Insert directly into the repository so the post-cache-kill
        // GetTasksAsync read sees the seeded shape without going through
        // SwarmService.AddTaskAsync's Pending/Blocked auto-derivation.
        await using var ctx = this.dbFactory.CreateDbContext();
        ctx.Tasks.Add(new Swarmwright.Database.Models.TaskEntity
        {
            SwarmId = this.currentSwarmId,
            Id = id,
            Subject = id,
            Description = id,
            WorkerName = "w",
            WorkerRole = "r",
            State = state.ToString(),
            BlockedByJson = "[]",
        });
        await ctx.SaveChangesAsync();
    }

    private SwarmOrchestrator CreateOrchestrator()
    {
        return new SwarmOrchestrator(
            new Mock<IChatClient>().Object,
            _ => new Mock<IChatClient>().Object,
            this.eventBus,
            this.agUiAdapter,
            this.swarmService,
            this.transitionService,
            new Swarmwright.Configuration.SwarmOptions { MaxRounds = 1 },
            template: null,
            workDirectory: Path.GetTempPath(),
            httpClient: this.httpClient);
    }
}
