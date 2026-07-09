using Swarmwright.Events;
using Swarmwright.Events.AgUI;
using Swarmwright.Extensions;
using Swarmwright.Hosting;
using Swarmwright.Models.Enums;
using Swarmwright.Workflows.Intervention;
using FluentAssertions;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Swarmwright.Workflows.Tests;

/// <summary>
/// Unit tests for <see cref="SwarmExecutor{TOutput}"/>'s core dispatch loop.
/// Drives the executor against a fake <see cref="ISwarmManager"/> that lets
/// the test resolve <c>WaitForCompletionAsync</c> and
/// <c>WaitForStateChangeAsync</c> on demand, then asserts the executor's
/// reactions: mapper invocation, intervention-handler dispatch, cancel
/// cascade, and typed exception throws.
/// </summary>
[TestClass]
public sealed class SwarmExecutorTests
{
    [TestMethod]
    public async Task ExecuteCoreAsync_DispatchesAndMapsResult_OnHappyPath()
    {
        var swarmId = Guid.NewGuid();
        using var execution = NewExecution(swarmId, SwarmInstanceState.Complete);
        var harness = new Harness(swarmId);
        harness.CompletionTcs.SetResult(execution);

        var executor = harness.Build();

        var result = await executor.RunAsync(SwarmInvocationInput.New("test goal"), CancellationToken.None);

        result.Should().Be("mapped:" + execution.WorkDirectory);
        harness.SwarmManager.Verify(
            m => m.CreateSwarmAsync("test goal", null, It.IsAny<IReadOnlyDictionary<string, string>?>()),
            Times.Once);
        harness.SwarmManager.Verify(m => m.RegisterCompletionWaiter(swarmId), Times.Once);
    }

    [TestMethod]
    public async Task ExecuteCoreAsync_ForwardsInputContext_ToCreateSwarmAsync()
    {
        var swarmId = Guid.NewGuid();
        using var execution = NewExecution(swarmId, SwarmInstanceState.Complete);
        var harness = new Harness(swarmId);
        harness.CompletionTcs.SetResult(execution);

        IReadOnlyDictionary<string, string>? captured = null;
        harness.SwarmManager
            .Setup(m => m.CreateSwarmAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .Callback<string, string?, IReadOnlyDictionary<string, string>?>(
                (_, _, ctx) => captured = ctx)
            .ReturnsAsync(swarmId);

        var context = new Dictionary<string, string>
        {
            ["sourceRoot"] = "/clones/pr-99",
        };

        var executor = harness.Build();

        await executor.RunAsync(
            SwarmInvocationInput.New("test goal", templateKey: null, context: context),
            CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.Should().ContainKey("sourceRoot")
            .WhoseValue.Should().Be("/clones/pr-99");
    }

    [TestMethod]
    public async Task ExecuteCoreAsync_ThrowsBailedException_OnFailedTerminalState()
    {
        var swarmId = Guid.NewGuid();
        using var execution = NewExecution(swarmId, SwarmInstanceState.Failed);
        execution.FailureReason = "boom";
        var harness = new Harness(swarmId);
        harness.CompletionTcs.SetResult(execution);

        var executor = harness.Build();

        Func<Task> act = () => executor.RunAsync(SwarmInvocationInput.New("g"), CancellationToken.None);

        (await act.Should().ThrowAsync<SwarmInterventionBailedException>())
            .Which.Message.Should().Contain("boom");
    }

    [TestMethod]
    public async Task ExecuteCoreAsync_PassesResumeSwarmId_ToEnsureLiveAsync()
    {
        var resumeId = Guid.NewGuid();
        using var execution = NewExecution(resumeId, SwarmInstanceState.Complete);
        var harness = new Harness(resumeId);
        harness.SwarmManager
            .Setup(m => m.EnsureLiveAsync(resumeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(execution);
        harness.CompletionTcs.SetResult(execution);

        var executor = harness.Build();

        await executor.RunAsync(SwarmInvocationInput.Resume(resumeId), CancellationToken.None);

        harness.SwarmManager.Verify(
            m => m.CreateSwarmAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>()),
            Times.Never);
        harness.SwarmManager.Verify(m => m.EnsureLiveAsync(resumeId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task ExecuteCoreAsync_ThrowsBailed_WhenEnsureLiveAsyncReturnsNull()
    {
        var resumeId = Guid.NewGuid();
        var harness = new Harness(resumeId);
        harness.SwarmManager
            .Setup(m => m.EnsureLiveAsync(resumeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((SwarmExecution?)null);

        var executor = harness.Build();

        Func<Task> act = () => executor.RunAsync(SwarmInvocationInput.Resume(resumeId), CancellationToken.None);

        await act.Should().ThrowAsync<SwarmInterventionBailedException>();
    }

    [TestMethod]
    public async Task ExecuteCoreAsync_HardcodesBail_OnNeedsDiagnosis()
    {
        var swarmId = Guid.NewGuid();
        var harness = new Harness(swarmId);
        harness.CurrentStateChangeTcs.SetResult(SwarmInstanceState.NeedsDiagnosis);

        var policyCalls = 0;
        harness.PolicyOverride = new DelegatePolicy((_, _) =>
        {
            policyCalls++;
            return Task.FromResult(InterventionDecision.SmartContinue);
        });

        var executor = harness.Build();

        Func<Task> act = () => executor.RunAsync(SwarmInvocationInput.New("g"), CancellationToken.None);

        await act.Should().ThrowAsync<SwarmInterventionBailedException>(
            "NeedsDiagnosis must short-circuit before consulting the policy");
        policyCalls.Should().Be(0, "the executor must hardcode NeedsDiagnosis bail");
    }

    [TestMethod]
    public async Task ExecuteCoreAsync_RoutesAwaitingIntervention_ThroughPolicyAndHandler()
    {
        var swarmId = Guid.NewGuid();
        using var execution = NewExecution(swarmId, SwarmInstanceState.Complete);
        var harness = new Harness(swarmId);

        // First state-change resolves with AwaitingIntervention; after that the
        // executor re-arms; the next state-change resolves with Complete via
        // the completion channel.
        harness.CurrentStateChangeTcs.SetResult(SwarmInstanceState.AwaitingIntervention);
        harness.NextStateChangeBehavior = () => new TaskCompletionSource<SwarmInstanceState>().Task; // never completes
        harness.PolicyOverride = new DelegatePolicy((_, _) => Task.FromResult(InterventionDecision.SmartContinue));
        harness.OnSmartContinue = () => harness.CompletionTcs.SetResult(execution);

        var executor = harness.Build();

        var result = await executor.RunAsync(SwarmInvocationInput.New("g"), CancellationToken.None);

        result.Should().StartWith("mapped:");
        harness.InterventionHandler.Verify(
            h => h.SmartContinueAsync(swarmId, "swarm-executor", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [TestMethod]
    public async Task ExecuteCoreAsync_RoutesAwaitingFeedback_ContinueDecision_ToManagerSignalContinue()
    {
        var swarmId = Guid.NewGuid();
        using var execution = NewExecution(swarmId, SwarmInstanceState.Complete);
        var harness = new Harness(swarmId);
        harness.CurrentStateChangeTcs.SetResult(SwarmInstanceState.AwaitingFeedback);
        harness.NextStateChangeBehavior = () => new TaskCompletionSource<SwarmInstanceState>().Task;
        harness.PolicyOverride = new DelegatePolicy((_, _) => Task.FromResult(InterventionDecision.Continue));
        harness.SwarmManager.Setup(m => m.SignalContinue(swarmId)).Returns(true)
            .Callback(() => harness.CompletionTcs.SetResult(execution));

        var executor = harness.Build();

        await executor.RunAsync(SwarmInvocationInput.New("g"), CancellationToken.None);

        harness.SwarmManager.Verify(m => m.SignalContinue(swarmId), Times.Once);
        harness.InterventionHandler.Verify(
            h => h.ContinueAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "AwaitingFeedback Continue must route via the manager, not the intervention handler");
    }

    [TestMethod]
    public async Task ExecuteCoreAsync_ThrowsBailed_WhenPolicyDecidesBail()
    {
        var swarmId = Guid.NewGuid();
        var harness = new Harness(swarmId);
        harness.CurrentStateChangeTcs.SetResult(SwarmInstanceState.AwaitingIntervention);
        harness.PolicyOverride = new DelegatePolicy((_, _) => Task.FromResult(InterventionDecision.Bail));

        var executor = harness.Build();

        Func<Task> act = () => executor.RunAsync(SwarmInvocationInput.New("g"), CancellationToken.None);

        await act.Should().ThrowAsync<SwarmInterventionBailedException>();
    }

    [TestMethod]
    public async Task ExecuteCoreAsync_PropagatesCancellation_ToCancelSwarmAsync()
    {
        var swarmId = Guid.NewGuid();
        var harness = new Harness(swarmId)
        {
            // Both wait channels never resolve; cancellation must still surface.
            NextStateChangeBehavior = () => new TaskCompletionSource<SwarmInstanceState>().Task,
        };
        using var cts = new CancellationTokenSource();

        var executor = harness.Build();

        var task = executor.RunAsync(SwarmInvocationInput.New("g"), cts.Token);
        cts.Cancel();

        Func<Task> act = () => task;
        await act.Should().ThrowAsync<OperationCanceledException>();
        harness.SwarmManager.Verify(m => m.CancelSwarmAsync(swarmId), Times.Once);
    }

    /// <summary>
    /// When the configured timeout elapses while
    /// the caller's token is still healthy, the executor must cascade-cancel
    /// the swarm and throw <see cref="SwarmInterventionBailedException"/>
    /// with a "timed out" message — distinct from caller-driven cancellation
    /// which surfaces as <see cref="OperationCanceledException"/>.
    /// </summary>
    [TestMethod]
    [Timeout(15_000)]
    public async Task ExecuteCoreAsync_TimeoutElapses_CascadesCancelAndThrowsBailed()
    {
        var swarmId = Guid.NewGuid();
        var harness = new Harness(swarmId)
        {
            // Neither wait channel ever resolves on its own; only the timeout can break the loop.
            NextStateChangeBehavior = () => new TaskCompletionSource<SwarmInstanceState>().Task,
            TimeoutSecondsOverride = 1,
        };

        var executor = harness.Build();

        Func<Task> act = () => executor.RunAsync(SwarmInvocationInput.New("g"), CancellationToken.None);

        var ex = (await act.Should().ThrowAsync<SwarmInterventionBailedException>()).Which;
        ex.Message.Should().Contain("timed out", "operators read this to know it was a deadline, not a swarm failure");
        harness.SwarmManager.Verify(
            m => m.CancelSwarmAsync(swarmId),
            Times.Once,
            "cascade-cancel must run so the orphan swarm doesn't keep working past the deadline");
    }

    /// <summary>
    /// If the caller cancels their own token before the timeout fires,
    /// the existing caller-cancel path wins: <see cref="OperationCanceledException"/>
    /// (not the timeout bail exception).
    /// </summary>
    [TestMethod]
    [Timeout(15_000)]
    public async Task ExecuteCoreAsync_CallerCancel_TakesPrecedenceOverTimeout()
    {
        var swarmId = Guid.NewGuid();
        var harness = new Harness(swarmId)
        {
            NextStateChangeBehavior = () => new TaskCompletionSource<SwarmInstanceState>().Task,
            TimeoutSecondsOverride = 30,
        };
        using var cts = new CancellationTokenSource();

        var executor = harness.Build();
        var task = executor.RunAsync(SwarmInvocationInput.New("g"), cts.Token);
        cts.Cancel();

        Func<Task> act = () => task;
        await act.Should().ThrowAsync<OperationCanceledException>(
            "caller cancel must win over the (much later) timeout deadline");
    }

    private static SwarmExecution NewExecution(Guid swarmId, SwarmInstanceState finalState)
    {
        return new SwarmExecution
        {
            SwarmId = swarmId,
            Goal = "test",
            Cts = new CancellationTokenSource(),
            EventBus = new SwarmEventBus(),
            AgUiAdapter = new SwarmEventAdapter(),
            WorkDirectory = Path.GetTempPath(),
            FinalState = finalState,
        };
    }

    /// <summary>
    /// Harness wiring a fake <see cref="ISwarmManager"/>, intervention handler,
    /// policy, and result mapper for <see cref="SwarmExecutor{TOutput}"/> tests.
    /// </summary>
    private sealed class Harness
    {
        public Harness(Guid swarmId)
        {
            this.SwarmId = swarmId;
            this.SwarmManager = new Mock<ISwarmManager>();
            this.InterventionHandler = new Mock<ISwarmInterventionHandler>();
            this.CompletionTcs = new TaskCompletionSource<SwarmExecution>(TaskCreationOptions.RunContinuationsAsynchronously);
            this.CurrentStateChangeTcs = new TaskCompletionSource<SwarmInstanceState>(TaskCreationOptions.RunContinuationsAsynchronously);

            this.SwarmManager
                .Setup(m => m.CreateSwarmAsync(
                    It.IsAny<string>(),
                    It.IsAny<string?>(),
                    It.IsAny<IReadOnlyDictionary<string, string>?>()))
                .ReturnsAsync(swarmId);
            this.SwarmManager
                .Setup(m => m.RegisterCompletionWaiter(swarmId));
            this.SwarmManager
                .Setup(m => m.WaitForCompletionAsync(swarmId, It.IsAny<CancellationToken>()))
                .Returns((Guid _, CancellationToken ct) => this.CompletionTcs.Task.WaitAsync(ct));
            this.SwarmManager
                .Setup(m => m.WaitForStateChangeAsync(swarmId, It.IsAny<CancellationToken>()))
                .Returns((Guid _, CancellationToken ct) =>
                {
                    if (!this.firstStateChangeTaken)
                    {
                        this.firstStateChangeTaken = true;
                        return this.CurrentStateChangeTcs.Task.WaitAsync(ct);
                    }

                    var next = this.NextStateChangeBehavior?.Invoke()
                        ?? new TaskCompletionSource<SwarmInstanceState>().Task;
                    return next.WaitAsync(ct);
                });
            this.InterventionHandler
                .Setup(h => h.SmartContinueAsync(swarmId, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .Returns(() =>
                {
                    this.OnSmartContinue?.Invoke();
                    return Task.FromResult(new InterventionResult(StatusCode: 200, Body: "ok"));
                });
            this.SwarmManager
                .Setup(m => m.CancelSwarmAsync(swarmId))
                .Returns(Task.CompletedTask);
        }

        public Guid SwarmId { get; }

        public Mock<ISwarmManager> SwarmManager { get; }

        public Mock<ISwarmInterventionHandler> InterventionHandler { get; }

        public TaskCompletionSource<SwarmExecution> CompletionTcs { get; }

        public TaskCompletionSource<SwarmInstanceState> CurrentStateChangeTcs { get; }

        public Func<Task<SwarmInstanceState>>? NextStateChangeBehavior { get; set; }

        public Action? OnSmartContinue { get; set; }

        public IInterventionPolicy? PolicyOverride { get; set; }

        public int? TimeoutSecondsOverride { get; set; }

        public TestableSwarmExecutor Build()
        {
            var policy = this.PolicyOverride ?? new BailToFailedFastPolicy();
            return new TestableSwarmExecutor(
                "test-executor",
                NullLogger<SwarmExecutor<string>>.Instance,
                this.SwarmManager.Object,
                this.InterventionHandler.Object,
                policy,
                (workDir, _) => Task.FromResult("mapped:" + workDir),
                this.TimeoutSecondsOverride ?? 300);
        }

        private bool firstStateChangeTaken;
    }

    /// <summary>
    /// Subclass that drives the executor's dispatch loop directly via
    /// <see cref="SwarmExecutor{TOutput}.HandleAsync"/>, skipping the workflow-context plumbing.
    /// </summary>
    private sealed class TestableSwarmExecutor : SwarmExecutor<string>
    {
        public TestableSwarmExecutor(
            string id,
            Microsoft.Extensions.Logging.ILogger<SwarmExecutor<string>> logger,
            ISwarmManager swarmManager,
            ISwarmInterventionHandler interventionHandler,
            IInterventionPolicy policy,
            Func<string, CancellationToken, Task<string>> resultMapper,
            int timeoutSeconds = 300)
            : base(id, logger, swarmManager, interventionHandler, policy, resultMapper, timeoutSeconds)
        {
        }

        public Task<string> RunAsync(SwarmInvocationInput input, CancellationToken cancellationToken) =>
            this.HandleAsync(input, Mock.Of<IWorkflowContext>(), cancellationToken).AsTask();
    }

    /// <summary>Inline-delegate policy used by tests to drive specific decisions.</summary>
    private sealed class DelegatePolicy : IInterventionPolicy
    {
        private readonly Func<InterventionContext, CancellationToken, Task<InterventionDecision>> decide;

        public DelegatePolicy(Func<InterventionContext, CancellationToken, Task<InterventionDecision>> decide)
        {
            this.decide = decide;
        }

        public Task<InterventionDecision> DecideAsync(InterventionContext context, CancellationToken cancellationToken) =>
            this.decide(context, cancellationToken);
    }
}
