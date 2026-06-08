using Swarmwright.Configuration;
using Swarmwright.Database;
using Swarmwright.Database.Models;
using Swarmwright.Database.Repositories;
using Swarmwright.Events;
using Swarmwright.Extensions;
using Swarmwright.Hosting;
using Swarmwright.Hosting.StateMachine;
using Swarmwright.Models.Enums;
using Swarmwright.Tests.Hosting.StateMachine;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Swarmwright.Tests.Extensions;

/// <summary>
/// Unit tests for <see cref="SwarmInterventionHandler"/> — the logic core
/// behind the <c>POST /api/swarm/{id}/continue</c>, <c>/skip</c>, <c>/cancel</c>,
/// <c>/smart-continue</c>, and <c>/lock</c> endpoints. The handler is
/// transport-agnostic: it returns <see cref="InterventionResult"/> which
/// the minimal-API binding translates into HTTP responses.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SwarmInterventionHandlerTests
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
        this.factory = new InMemoryDbContextFactory("Intervention_" + Guid.NewGuid());
        this.stateService = new StateTransitionService(this.factory, Mock.Of<ISwarmEmissionBroker>(), Mock.Of<ISwarmObservationSink>());
        this.repository = new SwarmRepository(this.factory);
        this.manager = new Mock<ISwarmManager>();
        this.options = new SwarmOptions { MaxTaskRetries = 1 };
        this.handler = new SwarmInterventionHandler(
            this.manager.Object,
            this.stateService,
            this.repository,
            Mock.Of<ILeaderRepairAdvisor>(),
            Options.Create(this.options),
            NullLogger<SwarmInterventionHandler>.Instance);
    }

    // ---- Continue ----

    [TestMethod]
    public async Task ContinueAsync_OnCompleteSwarm_Returns410Gone()
    {
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.Complete);

        var result = await this.handler.ContinueAsync(swarmId, actor: "alice");

        result.StatusCode.Should().Be(410);
    }

    [TestMethod]
    public async Task ContinueAsync_OnCreatedSwarm_Returns409Conflict()
    {
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.Created);

        var result = await this.handler.ContinueAsync(swarmId, actor: "alice");

        result.StatusCode.Should().Be(409);
    }

    [TestMethod]
    public async Task ContinueAsync_UnknownSwarm_Returns404()
    {
        var result = await this.handler.ContinueAsync(Guid.NewGuid(), actor: "alice");

        result.StatusCode.Should().Be(404);
    }

    [TestMethod]
    public async Task ContinueAsync_OnAwaitingInterventionWithEligibleFailed_Returns204AndTransitions()
    {
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.AwaitingIntervention);
        var taskId = await this.SeedTaskAsync(swarmId, TaskState.Failed, retryCount: 0);

        var result = await this.handler.ContinueAsync(swarmId, actor: "alice");

        result.StatusCode.Should().Be(204);
        this.manager.Verify(m => m.SignalContinue(swarmId), Times.Once);

        await using var ctx = this.factory.CreateDbContext();
        var swarm = await ctx.Swarms.FindAsync(swarmId);
        swarm!.State.Should().Be(nameof(SwarmInstanceState.Executing));
        var task = await ctx.Tasks.SingleAsync(t => t.SwarmId == swarmId && t.Id == taskId);
        task.State.Should().Be(nameof(TaskState.Pending));
        task.RetryCount.Should().Be(1);
    }

    [TestMethod]
    public async Task ContinueAsync_SuccessPath_CallsEnsureLiveAfterStateWrites()
    {
        // Fix A guard: the handler owns the orchestrator resurrection so the
        // orchestrator's LoadAsync always reads the post-handler DB state.
        // Previously the endpoint called EnsureLiveAsync BEFORE the handler,
        // which resurrected the orchestrator, loaded stale TaskBoard state,
        // and missed the orphan reset the handler was about to write. The
        // handler must call EnsureLiveAsync AFTER its state transitions
        // complete — tested here via a call-order tracker.
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.AwaitingIntervention);
        await this.SeedTaskAsync(swarmId, TaskState.Failed, retryCount: 0);

        var callOrder = new List<string>();

        // Wrap the real state service so we can record when its writes happen
        // relative to the manager's EnsureLiveAsync call.
        var recordingStateService = new RecordingStateTransitionService(this.stateService, callOrder);

        this.manager
            .Setup(m => m.EnsureLiveAsync(swarmId, It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("EnsureLiveAsync"))
            .ReturnsAsync((SwarmExecution?)null);

        var handler = new SwarmInterventionHandler(
            this.manager.Object,
            recordingStateService,
            this.repository,
            Mock.Of<ILeaderRepairAdvisor>(),
            Options.Create(this.options),
            NullLogger<SwarmInterventionHandler>.Instance);

        var result = await handler.ContinueAsync(swarmId, actor: "alice");

        result.StatusCode.Should().Be(204);

        this.manager.Verify(
            m => m.EnsureLiveAsync(swarmId, It.IsAny<CancellationToken>()),
            Times.Once,
            "handler must resurrect the orchestrator so its LoadAsync reads the post-handler state");

        var swarmTransitionIndex = callOrder.FindIndex(c => c == "TransitionSwarmAsync:Executing");
        var ensureLiveIndex = callOrder.IndexOf("EnsureLiveAsync");
        swarmTransitionIndex.Should().BeGreaterThanOrEqualTo(0, "the swarm-level transition must have fired");
        ensureLiveIndex.Should().BeGreaterThanOrEqualTo(0, "EnsureLiveAsync must have been called");
        ensureLiveIndex.Should().BeGreaterThan(
            swarmTransitionIndex,
            "EnsureLiveAsync must run AFTER the swarm-level state write so the resurrected orchestrator sees fresh state");
    }

    [TestMethod]
    public async Task ContinueAsync_OnAwaitingInterventionWithNoBudget_Returns409()
    {
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.AwaitingIntervention);
        await this.SeedTaskAsync(swarmId, TaskState.Failed, retryCount: 1);

        var result = await this.handler.ContinueAsync(swarmId, actor: "alice");

        result.StatusCode.Should().Be(409);
        this.manager.Verify(m => m.SignalContinue(It.IsAny<Guid>()), Times.Never);
    }

    [TestMethod]
    public async Task ContinueAsync_OnAwaitingInterventionWithOrphanInProgressOnly_ResetsOrphanToPendingWithOrphanResumeReason()
    {
        // Defense-in-depth Layer 2b: a swarm with one orphan InProgress task
        // and no Failed/Pending must still be continuable. Continue resets the
        // orphan to Pending via the orphan_resume reason (not user_continue)
        // and does NOT bump retry_count — the worker never got to run, so it's
        // not fair to charge retry budget.
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.AwaitingIntervention);
        var taskId = await this.SeedTaskAsync(swarmId, TaskState.InProgress, retryCount: 0);

        var result = await this.handler.ContinueAsync(swarmId, actor: "alice");

        result.StatusCode.Should().Be(204, "orphan InProgress is viable work for Continue");
        this.manager.Verify(m => m.SignalContinue(swarmId), Times.Once);

        await using var ctx = this.factory.CreateDbContext();
        var task = await ctx.Tasks.SingleAsync(t => t.SwarmId == swarmId && t.Id == taskId);
        task.State.Should().Be(nameof(TaskState.Pending));
        task.RetryCount.Should().Be(0, "orphan resets do not consume retry budget");

        var row = await ctx.TaskStateTransitions
            .SingleAsync(r => r.TaskId == taskId && r.Reason == TransitionReasons.OrphanResume);
        row.FromState.Should().Be(nameof(TaskState.InProgress));
        row.ToState.Should().Be(nameof(TaskState.Pending));
        row.Actor.Should().Be("alice");

        var swarm = await ctx.Swarms.FindAsync(swarmId);
        swarm!.State.Should().Be(nameof(SwarmInstanceState.Executing));
    }

    [TestMethod]
    public async Task ContinueAsync_OrphanAndFailedWithBudget_UsesDifferentReasonsPerTask()
    {
        // Both paths fire in the same Continue. Failed uses user_continue
        // (with retry_count bump); orphan uses orphan_resume (no bump).
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.AwaitingIntervention);
        var orphanId = await this.SeedTaskAsync(swarmId, TaskState.InProgress, retryCount: 0);
        var failedId = await this.SeedTaskAsync(swarmId, TaskState.Failed, retryCount: 0);

        var result = await this.handler.ContinueAsync(swarmId, actor: "alice");

        result.StatusCode.Should().Be(204);

        await using var ctx = this.factory.CreateDbContext();

        var orphanRow = await ctx.TaskStateTransitions
            .SingleAsync(r => r.TaskId == orphanId);
        orphanRow.Reason.Should().Be(TransitionReasons.OrphanResume);
        var orphan = await ctx.Tasks.SingleAsync(t => t.Id == orphanId);
        orphan.RetryCount.Should().Be(0, "orphan resume must not bump retry_count");

        var failedRow = await ctx.TaskStateTransitions
            .SingleAsync(r => r.TaskId == failedId);
        failedRow.Reason.Should().Be(TransitionReasons.UserContinue);
        var failedTask = await ctx.Tasks.SingleAsync(t => t.Id == failedId);
        failedTask.RetryCount.Should().Be(1, "user_continue bumps retry_count by 1");
    }

    [TestMethod]
    public async Task ContinueAsync_OnAwaitingInterventionWithViablePendingAndNoFailed_Returns204()
    {
        // Regression: the hung-swarm demo seed has no failed tasks but 1 Pending + some
        // Blocked. Continue must accept this — the orchestrator can resume by running the
        // Pending task — rather than rejecting with "no_retry_budget".
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.AwaitingIntervention);
        await this.SeedTaskAsync(swarmId, TaskState.Completed, retryCount: 0);
        await this.SeedTaskAsync(swarmId, TaskState.Pending, retryCount: 0);
        await this.SeedTaskAsync(swarmId, TaskState.Blocked, retryCount: 0);

        var result = await this.handler.ContinueAsync(swarmId, actor: "alice");

        result.StatusCode.Should().Be(204, "viable Pending work is a sufficient basis for Continue");
        this.manager.Verify(m => m.SignalContinue(swarmId), Times.Once);
    }

    // ---- Skip ----

    [TestMethod]
    public async Task SkipAsync_OnCompleteSwarm_Returns410()
    {
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.Complete);

        var result = await this.handler.SkipAsync(swarmId, actor: "alice");

        result.StatusCode.Should().Be(410);
    }

    [TestMethod]
    public async Task SkipAsync_OnCreatedSwarm_Returns409()
    {
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.Created);

        var result = await this.handler.SkipAsync(swarmId, actor: "alice");

        result.StatusCode.Should().Be(409);
    }

    [TestMethod]
    public async Task SkipAsync_OnAwaitingIntervention_Returns204AndTransitionsToSynthesizing()
    {
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.AwaitingIntervention);

        var result = await this.handler.SkipAsync(swarmId, actor: "alice");

        result.StatusCode.Should().Be(204);
        this.manager.Verify(m => m.SignalSkip(swarmId), Times.Once);

        await using var ctx = this.factory.CreateDbContext();
        var swarm = await ctx.Swarms.FindAsync(swarmId);
        swarm!.State.Should().Be(nameof(SwarmInstanceState.Synthesizing));
    }

    // ---- Cancel ----

    [TestMethod]
    public async Task CancelAsync_OnCompleteSwarm_Returns410()
    {
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.Complete);

        var result = await this.handler.CancelAsync(swarmId, actor: "alice");

        result.StatusCode.Should().Be(410);
    }

    [TestMethod]
    public async Task CancelAsync_OnExecutingSwarm_Returns204AndCancels()
    {
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.Executing);
        this.manager.Setup(m => m.CancelSwarmAsync(swarmId)).Returns(Task.CompletedTask);

        var result = await this.handler.CancelAsync(swarmId, actor: "alice");

        result.StatusCode.Should().Be(204);
        this.manager.Verify(m => m.CancelSwarmAsync(swarmId), Times.Once);

        await using var ctx = this.factory.CreateDbContext();
        var swarm = await ctx.Swarms.FindAsync(swarmId);
        swarm!.State.Should().Be(nameof(SwarmInstanceState.Cancelled));
    }

    // ---- MarkAsAwaitingIntervention (Manual Recover) ----

    [TestMethod]
    public async Task MarkAsAwaitingInterventionAsync_OnFailedSwarm_TransitionsToAwaitingInterventionWithCarriedNote()
    {
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.Failed);
        await this.SeedTransitionAsync(
            swarmId,
            fromState: SwarmInstanceState.Executing,
            toState: SwarmInstanceState.Failed,
            reason: TransitionReasons.RunFailed,
            note: "Recipient 'leader' is not registered.");

        var result = await this.handler.MarkAsAwaitingInterventionAsync(swarmId, actor: "dev");

        result.StatusCode.Should().Be(204);

        await using var ctx = this.factory.CreateDbContext();
        var swarm = await ctx.Swarms.FindAsync(swarmId);
        swarm!.State.Should().Be(nameof(SwarmInstanceState.AwaitingIntervention));

        var latest = ctx.SwarmStateTransitions
            .Where(t => t.SwarmId == swarmId)
            .OrderByDescending(t => t.CreatedAt)
            .First();
        latest.Reason.Should().Be(TransitionReasons.UserMarkForIntervention);
        latest.Actor.Should().Be("dev");
        latest.ToState.Should().Be(nameof(SwarmInstanceState.AwaitingIntervention));
        latest.Note.Should().StartWith("Recovered from:");
        latest.Note.Should().Contain("Recipient 'leader' is not registered.");
    }

    [TestMethod]
    public async Task MarkAsAwaitingInterventionAsync_OnFailedSwarm_DoesNotAutoResumeViaManager()
    {
        // Manual Recover is a pure state flip — the operator picks a recovery
        // strategy (Smart Continue / Force Synthesis / Continue / Cancel)
        // from the AwaitingIntervention UI. Auto-kicking the dispatcher here
        // bypasses operator intent and races phase-advance against
        // already-Completed tasks on recovered swarms.
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.Failed);

        var result = await this.handler.MarkAsAwaitingInterventionAsync(swarmId, actor: "dev");

        result.StatusCode.Should().Be(204);
        this.manager.Verify(
            m => m.EnsureLiveAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Manual Recover must not auto-resume; operator chooses the recovery action");
    }

    [TestMethod]
    public async Task MarkAsAwaitingInterventionAsync_OnCompleteSwarm_Returns409()
    {
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.Complete);

        var result = await this.handler.MarkAsAwaitingInterventionAsync(swarmId, actor: "dev");

        result.StatusCode.Should().Be(409, "Complete is an intentional terminal; Manual Recover must not reopen it");
    }

    [TestMethod]
    public async Task MarkAsAwaitingInterventionAsync_OnCancelledSwarm_Returns409()
    {
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.Cancelled);

        var result = await this.handler.MarkAsAwaitingInterventionAsync(swarmId, actor: "dev");

        result.StatusCode.Should().Be(409, "Cancelled is an intentional terminal; Manual Recover must not reopen it");
    }

    [TestMethod]
    public async Task MarkAsAwaitingInterventionAsync_OnExecutingSwarm_Returns409()
    {
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.Executing);

        var result = await this.handler.MarkAsAwaitingInterventionAsync(swarmId, actor: "dev");

        result.StatusCode.Should().Be(409, "Manual Recover is only valid from Failed");
    }

    [TestMethod]
    public async Task MarkAsAwaitingInterventionAsync_OnUnknownSwarm_Returns404()
    {
        var result = await this.handler.MarkAsAwaitingInterventionAsync(Guid.NewGuid(), actor: "dev");

        result.StatusCode.Should().Be(404);
    }

    // ---- Helpers ----

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

    private async Task SeedTransitionAsync(
        Guid swarmId,
        SwarmInstanceState fromState,
        SwarmInstanceState toState,
        string reason,
        string? note = null)
    {
        await using var ctx = this.factory.CreateDbContext();
        ctx.SwarmStateTransitions.Add(new SwarmStateTransition
        {
            SwarmId = swarmId,
            FromState = fromState.ToString(),
            ToState = toState.ToString(),
            Reason = reason,
            Actor = "system",
            Note = note,
            CreatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }

    private async Task<string> SeedTaskAsync(Guid swarmId, TaskState state, int retryCount)
    {
        await using var ctx = this.factory.CreateDbContext();
        var id = "t-" + Guid.NewGuid().ToString("N")[..8];
        var task = new TaskEntity
        {
            SwarmId = swarmId,
            Id = id,
            Subject = "s",
            Description = "d",
            WorkerRole = "r",
            WorkerName = "w",
            State = state.ToString(),
            RetryCount = retryCount,
        };
        ctx.Tasks.Add(task);
        await ctx.SaveChangesAsync();
        return id;
    }

    /// <summary>
    /// Decorates <see cref="IStateTransitionService"/> to record swarm-level
    /// transition calls into a shared timeline. Used by the Fix-A ordering
    /// test to prove <c>EnsureLiveAsync</c> runs AFTER the handler's writes.
    /// </summary>
    private sealed class RecordingStateTransitionService : IStateTransitionService
    {
        private readonly IStateTransitionService inner;
        private readonly List<string> timeline;

        public RecordingStateTransitionService(IStateTransitionService inner, List<string> timeline)
        {
            this.inner = inner;
            this.timeline = timeline;
        }

        public async Task<SwarmStateTransitionResult> TransitionSwarmAsync(
            Guid swarmId,
            SwarmInstanceState toState,
            string reason,
            string? actor = null,
            string? note = null,
            CancellationToken cancellationToken = default)
        {
            var result = await this.inner.TransitionSwarmAsync(swarmId, toState, reason, actor, note, cancellationToken)
                .ConfigureAwait(false);
            this.timeline.Add($"TransitionSwarmAsync:{toState}");
            return result;
        }

        public Task<TaskStateTransitionResult> TransitionTaskAsync(
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
            return this.inner.TransitionTaskAsync(swarmId, taskId, toState, reason, actor, retryCountDelta, note, result, cancellationToken);
        }

        public Task<SwarmStateTransitionResult> RecordSwarmAuditAsync(
            Guid swarmId,
            string reason,
            string? actor = null,
            string? note = null,
            CancellationToken cancellationToken = default)
        {
            return this.inner.RecordSwarmAuditAsync(swarmId, reason, actor, note, cancellationToken);
        }
    }
}
