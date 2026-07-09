using System.Text.Json;
using Swarmwright.Configuration;
using Swarmwright.Database.Models;
using Swarmwright.Database.Repositories;
using Swarmwright.Models.Enums;
using Swarmwright.Recommendation;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;

namespace Swarmwright.Tests.Recommendation;

/// <summary>
/// Rule-table unit tests for <see cref="DeterministicSwarmContinueProvider"/>. Every
/// row in the recommendation switch table has at least one test. Adding a new rule =
/// adding a method + a test here.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class DeterministicSwarmContinueProviderTests
{
    private const int DefaultMaxRetries = 1;

    private Mock<ISwarmRepository> repository = null!;
    private DeterministicSwarmContinueProvider provider = null!;

    [TestInitialize]
    public void Initialize()
    {
        this.repository = new Mock<ISwarmRepository>(MockBehavior.Strict);
        this.provider = new DeterministicSwarmContinueProvider(
            this.repository.Object,
            Options.Create(new SwarmOptions { MaxTaskRetries = DefaultMaxRetries }));
    }

    // ----- Non-actionable states ---------------------------------------------------

    [TestMethod]
    public async Task Running_StateExecuting_ReturnsNull()
    {
        var swarmId = Guid.NewGuid();
        this.SetupSwarmState(swarmId, SwarmInstanceState.Executing);
        this.SetupTasks(swarmId, []);

        var rec = await this.provider.GetRecommendationAsync(swarmId);

        rec.Should().BeNull("running swarms have no recovery action");
    }

    [TestMethod]
    public async Task Complete_StateComplete_ReturnsNull()
    {
        var swarmId = Guid.NewGuid();
        this.SetupSwarmState(swarmId, SwarmInstanceState.Complete);
        this.SetupTasks(swarmId, []);

        var rec = await this.provider.GetRecommendationAsync(swarmId);

        rec.Should().BeNull("complete swarms have no recovery action");
    }

    [TestMethod]
    public async Task Missing_SwarmDoesNotExist_ReturnsNull()
    {
        var swarmId = Guid.NewGuid();
        this.repository.Setup(r => r.GetSwarmAsync(swarmId)).ReturnsAsync((SwarmEntity?)null);

        var rec = await this.provider.GetRecommendationAsync(swarmId);

        rec.Should().BeNull("missing swarms have no recovery action");
    }

    // ----- Actionable rules ---------------------------------------------------------

    [TestMethod]
    public async Task Continue_FailedTasksAllHaveRetryBudget_RecommendsContinue()
    {
        var swarmId = Guid.NewGuid();
        this.SetupSwarmState(swarmId, SwarmInstanceState.AwaitingIntervention);
        this.SetupTasks(swarmId, [
            Task("failed-1", TaskState.Failed, retryCount: 0),
            Task("failed-2", TaskState.Failed, retryCount: 0),
            Task("blocked-1", TaskState.Blocked),
        ]);

        var rec = await this.provider.GetRecommendationAsync(swarmId);

        rec.Should().NotBeNull();
        rec!.RecommendedAction.Should().Be("continue");
        rec.Rationale.Should().Contain("retry budget");
        rec.ValidActions.Should().BeEquivalentTo("continue", "smart-continue", "force-synthesis", "cancel");
    }

    [TestMethod]
    public async Task SmartContinue_FailedTasksAllRetryExhausted_RecommendsSmartContinue()
    {
        var swarmId = Guid.NewGuid();
        this.SetupSwarmState(swarmId, SwarmInstanceState.AwaitingIntervention);
        this.SetupTasks(swarmId, [
            Task("f1", TaskState.Failed, retryCount: DefaultMaxRetries),
            Task("f2", TaskState.Failed, retryCount: DefaultMaxRetries),
        ]);

        var rec = await this.provider.GetRecommendationAsync(swarmId);

        rec!.RecommendedAction.Should().Be("smart-continue");
        rec.Rationale.Should().Contain("exhausted");
    }

    [TestMethod]
    public async Task Continue_FailedTasksMixedBudget_RecommendsContinue()
    {
        var swarmId = Guid.NewGuid();
        this.SetupSwarmState(swarmId, SwarmInstanceState.AwaitingIntervention);
        this.SetupTasks(swarmId, [
            Task("f-has-budget", TaskState.Failed, retryCount: 0),
            Task("f-exhausted", TaskState.Failed, retryCount: DefaultMaxRetries),
        ]);

        var rec = await this.provider.GetRecommendationAsync(swarmId);

        rec!.RecommendedAction.Should().Be("continue");
        rec.Rationale.Should().Contain("retry", "continue retries the budgeted ones first");
        rec.Rationale.Should().Contain("Smart Continue", "caller should know smart-continue may follow");
    }

    [TestMethod]
    public async Task Continue_NoFailuresAndViablePending_RecommendsContinue()
    {
        var swarmId = Guid.NewGuid();
        this.SetupSwarmState(swarmId, SwarmInstanceState.AwaitingIntervention);
        this.SetupTasks(swarmId, [
            Task("done-1", TaskState.Completed),
            Task("pending-1", TaskState.Pending),
            Task("blocked-1", TaskState.Blocked),
        ]);

        var rec = await this.provider.GetRecommendationAsync(swarmId);

        rec!.RecommendedAction.Should().Be("continue", "this is the demo bug — no failures, 1 viable pending must recommend continue");
        rec.Rationale.ToLowerInvariant().Should().Contain(
            "no failures",
            "operator needs to see why continue is safe");
    }

    [TestMethod]
    public async Task SmartContinue_NoFailuresAllBlockedWithNoViablePending_RecommendsSmartContinue()
    {
        var swarmId = Guid.NewGuid();
        this.SetupSwarmState(swarmId, SwarmInstanceState.AwaitingIntervention);
        this.SetupTasks(swarmId, [
            Task("done", TaskState.Completed),
            Task("blocked-1", TaskState.Blocked),
            Task("blocked-2", TaskState.Blocked),
        ]);

        var rec = await this.provider.GetRecommendationAsync(swarmId);

        rec!.RecommendedAction.Should().Be("smart-continue", "stuck chain requires leader intervention");
        rec.Rationale.ToLowerInvariant().Should().Contain("blocked");
    }

    [TestMethod]
    public async Task ForceSynthesis_OnlyCompletedTasks_RecommendsForceSynthesis()
    {
        var swarmId = Guid.NewGuid();
        this.SetupSwarmState(swarmId, SwarmInstanceState.AwaitingIntervention);
        this.SetupTasks(swarmId, [
            Task("done-1", TaskState.Completed),
            Task("done-2", TaskState.Completed),
        ]);

        var rec = await this.provider.GetRecommendationAsync(swarmId);

        // When failed-exhausted tasks exist, Smart Continue wins because the leader can
        // reset them back to Pending. Force Synthesis is reserved for "nothing to rescue" —
        // i.e., only Completed tasks remain.
        rec!.RecommendedAction.Should().Be(
            "force-synthesis",
            "no failures to rescue, no open work — synthesize the partial report");
    }

    [TestMethod]
    public async Task Continue_OrphanInProgressOnly_RecommendsContinueWithOrphanRationale()
    {
        // Defense-in-depth Layer 3: a swarm with only orphan InProgress tasks
        // (no Failed, no Pending, no Blocked) must recommend Continue so the
        // operator clicks the right button on first glance.
        //
        // NOTE: this test doubles as the force-synthesis-guard regression guard.
        // If a future change reverts the §3a patch and the force-synthesis
        // early-return catches the orphan-only case, recommendedAction comes
        // back as "force-synthesis" and this Be("continue") assertion fails.
        // Do not split this into a separate negative test — the positive
        // assertion already proves the negative behaviour by construction.
        var swarmId = Guid.NewGuid();
        this.SetupSwarmState(swarmId, SwarmInstanceState.AwaitingIntervention);
        this.SetupTasks(swarmId, [
            Task("orphan-1", TaskState.InProgress),
        ]);

        var rec = await this.provider.GetRecommendationAsync(swarmId);

        rec!.RecommendedAction.Should().Be(
            "continue",
            "orphan InProgress is deterministically resumable — Continue resets and retries");
        rec.Rationale.ToLowerInvariant().Should().Contain(
            "orphan",
            "rationale must name the orphan scenario so the operator understands the one-click fix");
    }

    [TestMethod]
    public async Task Continue_OrphanInProgressPlusFailedWithBudget_RecommendsContinueWithFailedRationale()
    {
        // Precedence: Failed-with-budget is a more actionable signal than an
        // orphan, so the Failed rule's rationale wins. Both conditions map to
        // "continue" so the button is the same either way; it's the rationale
        // the operator reads that differs.
        var swarmId = Guid.NewGuid();
        this.SetupSwarmState(swarmId, SwarmInstanceState.AwaitingIntervention);
        this.SetupTasks(swarmId, [
            Task("orphan", TaskState.InProgress),
            Task("failed-has-budget", TaskState.Failed, retryCount: 0),
        ]);

        var rec = await this.provider.GetRecommendationAsync(swarmId);

        rec!.RecommendedAction.Should().Be("continue");
        rec.Rationale.ToLowerInvariant().Should().Contain(
            "retry budget",
            "Failed-with-budget is the primary signal when both coexist");
    }

    [TestMethod]
    public async Task NeedsDiagnosis_StateNeedsDiagnosis_ReturnsRecommendation()
    {
        var swarmId = Guid.NewGuid();
        this.SetupSwarmState(swarmId, SwarmInstanceState.NeedsDiagnosis);
        this.SetupTasks(swarmId, [
            Task("f-exhausted", TaskState.Failed, retryCount: DefaultMaxRetries),
        ]);

        var rec = await this.provider.GetRecommendationAsync(swarmId);

        rec.Should().NotBeNull("NeedsDiagnosis is an actionable state");
    }

    // ----- Helpers ------------------------------------------------------------------

    private void SetupSwarmState(Guid swarmId, SwarmInstanceState state)
    {
        this.repository
            .Setup(r => r.GetSwarmAsync(swarmId))
            .ReturnsAsync(new SwarmEntity
            {
                Id = swarmId,
                Goal = "test",
                State = state.ToString(),
            });
    }

    private void SetupTasks(Guid swarmId, IReadOnlyList<TaskEntity> tasks)
    {
        this.repository
            .Setup(r => r.GetTasksAsync(swarmId))
            .ReturnsAsync(tasks.ToList());
    }

    private static TaskEntity Task(
        string id,
        TaskState state,
        int retryCount = 0,
        IReadOnlyList<string>? blockedBy = null)
    {
        return new TaskEntity
        {
            Id = id,
            Subject = id,
            Description = id,
            WorkerRole = "r",
            WorkerName = id,
            State = state.ToString(),
            RetryCount = retryCount,
            BlockedByJson = JsonSerializer.Serialize(blockedBy ?? []),
        };
    }
}
