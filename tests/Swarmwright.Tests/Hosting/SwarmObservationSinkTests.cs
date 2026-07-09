using Swarmwright.Events;
using Swarmwright.Events.AgUI;
using Swarmwright.Hosting;
using Swarmwright.Models.Enums;
using FluentAssertions;

namespace Swarmwright.Tests.Hosting;

/// <summary>
/// Unit tests for <see cref="SwarmObservationSink"/>. Covers the two
/// observation channels (terminal-completion and state-change) and the
/// fail-fast guard against duplicate completion waiters.
/// </summary>
[TestClass]
public sealed class SwarmObservationSinkTests
{
    private SwarmObservationSink sink = null!;

    [TestInitialize]
    public void Initialize()
    {
        this.sink = new SwarmObservationSink();
    }

    [TestMethod]
    public async Task WaitForCompletionAsync_ReturnsExecution_WhenSignalTerminalCalled()
    {
        var swarmId = Guid.NewGuid();
        using var execution = NewExecution(swarmId);
        execution.FinalState = SwarmInstanceState.Complete;

        this.sink.RegisterCompletionWaiter(swarmId);
        var waitTask = this.sink.WaitForCompletionAsync(swarmId, CancellationToken.None);

        await this.sink.SignalTerminalAsync(swarmId, execution);

        var result = await waitTask;
        result.Should().BeSameAs(execution);
        result.FinalState.Should().Be(SwarmInstanceState.Complete);
    }

    [TestMethod]
    public async Task WaitForCompletionAsync_ResolvesImmediately_WhenSwarmAlreadyTerminalBeforeWait()
    {
        // Closes the fast-completion race: SignalTerminalAsync arrives before the
        // executor calls WaitForCompletionAsync. The wait must observe the prior
        // terminal signal rather than hanging.
        var swarmId = Guid.NewGuid();
        using var execution = NewExecution(swarmId);
        execution.FinalState = SwarmInstanceState.Complete;

        this.sink.RegisterCompletionWaiter(swarmId);
        await this.sink.SignalTerminalAsync(swarmId, execution);

        var waitTask = this.sink.WaitForCompletionAsync(swarmId, CancellationToken.None);

        waitTask.IsCompletedSuccessfully.Should().BeTrue(
            "the wait must short-circuit when the swarm is already terminal");
        (await waitTask).Should().BeSameAs(execution);
    }

    [TestMethod]
    public async Task WaitForCompletionAsync_Throws_WhenCancellationRequested()
    {
        var swarmId = Guid.NewGuid();
        using var cts = new CancellationTokenSource();

        this.sink.RegisterCompletionWaiter(swarmId);
        var waitTask = this.sink.WaitForCompletionAsync(swarmId, cts.Token);

        cts.Cancel();

        Func<Task> act = () => waitTask;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [TestMethod]
    public async Task WaitForCompletionAsync_ResolvesWithCancelledState_WhenSwarmCancelsItself()
    {
        // Distinct from cancellation-token cancel: the swarm itself reaches Cancelled.
        // Consumers must distinguish these two paths in their error handling.
        var swarmId = Guid.NewGuid();
        using var execution = NewExecution(swarmId);
        execution.FinalState = SwarmInstanceState.Cancelled;

        this.sink.RegisterCompletionWaiter(swarmId);
        var waitTask = this.sink.WaitForCompletionAsync(swarmId, CancellationToken.None);

        await this.sink.SignalTerminalAsync(swarmId, execution);

        var result = await waitTask;
        result.FinalState.Should().Be(SwarmInstanceState.Cancelled);
    }

    [TestMethod]
    public void RegisterCompletionWaiter_Throws_WhenSecondWaiterRegistersForSameSwarm()
    {
        var swarmId = Guid.NewGuid();
        this.sink.RegisterCompletionWaiter(swarmId);

        Action act = () => this.sink.RegisterCompletionWaiter(swarmId);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage(
                "*SwarmCompleted*",
                "tail observers should subscribe to the manager's SwarmCompleted event");
    }

    [TestMethod]
    public async Task OnTerminal_FiresEachRegisteredCallback_OnceForTerminalTransition()
    {
        var swarmId = Guid.NewGuid();
        using var execution = NewExecution(swarmId);
        execution.FinalState = SwarmInstanceState.Complete;

        var firstCount = 0;
        var secondCount = 0;
        this.sink.OnTerminal((_, _) =>
        {
            firstCount++;
            return Task.CompletedTask;
        });
        this.sink.OnTerminal((_, _) =>
        {
            secondCount++;
            return Task.CompletedTask;
        });

        this.sink.RegisterCompletionWaiter(swarmId);
        await this.sink.SignalTerminalAsync(swarmId, execution);

        firstCount.Should().Be(1);
        secondCount.Should().Be(1);
    }

    [TestMethod]
    public async Task OnTerminal_LogsAndSwallows_WhenSubscriberThrows()
    {
        var swarmId = Guid.NewGuid();
        using var execution = NewExecution(swarmId);
        execution.FinalState = SwarmInstanceState.Complete;

        this.sink.OnTerminal((_, _) => throw new InvalidOperationException("first dies"));
        var secondFired = false;
        this.sink.OnTerminal((_, _) =>
        {
            secondFired = true;
            return Task.CompletedTask;
        });

        this.sink.RegisterCompletionWaiter(swarmId);

        // Direct call (no lambda capture) avoids CA2025 around the using-declared execution.
        // Reaching the next line at all proves SignalTerminalAsync did not propagate the throw.
        await this.sink.SignalTerminalAsync(swarmId, execution);

        secondFired.Should().BeTrue("subscribers after a thrower must still fire");
    }

    [TestMethod]
    public async Task SignalTerminalAsync_PopulatesFailureReason_OnFailedTransition()
    {
        var swarmId = Guid.NewGuid();
        using var execution = NewExecution(swarmId);
        execution.FinalState = SwarmInstanceState.Failed;
        execution.FailureReason = "boom";

        this.sink.RegisterCompletionWaiter(swarmId);
        var waitTask = this.sink.WaitForCompletionAsync(swarmId, CancellationToken.None);

        await this.sink.SignalTerminalAsync(swarmId, execution);

        var result = await waitTask;
        result.FinalState.Should().Be(SwarmInstanceState.Failed);
        result.FailureReason.Should().Be("boom");
    }

    [TestMethod]
    public async Task WaitForStateChangeAsync_ResolvesWithNextState_WhenSignalStateChangeCalled()
    {
        var swarmId = Guid.NewGuid();
        var waitTask = this.sink.WaitForStateChangeAsync(swarmId, CancellationToken.None);

        await this.sink.SignalStateChangeAsync(swarmId, SwarmInstanceState.AwaitingIntervention);

        var result = await waitTask;
        result.Should().Be(SwarmInstanceState.AwaitingIntervention);
    }

    [TestMethod]
    public async Task WaitForStateChangeAsync_DropsSignal_WhenNoWaiterEnqueued()
    {
        // Signals that arrive while no caller is awaiting are not buffered.
        // Documents v1 behavior: missed transient state between two awaits is OK.
        var swarmId = Guid.NewGuid();

        await this.sink.SignalStateChangeAsync(swarmId, SwarmInstanceState.Executing);

        // The next waiter must observe the next signal, not the dropped one.
        var waitTask = this.sink.WaitForStateChangeAsync(swarmId, CancellationToken.None);
        await this.sink.SignalStateChangeAsync(swarmId, SwarmInstanceState.AwaitingIntervention);

        var result = await waitTask;
        result.Should().Be(SwarmInstanceState.AwaitingIntervention);
    }

    [TestMethod]
    public async Task WaitForStateChangeAsync_Throws_WhenCancellationRequested()
    {
        var swarmId = Guid.NewGuid();
        using var cts = new CancellationTokenSource();

        var waitTask = this.sink.WaitForStateChangeAsync(swarmId, cts.Token);
        cts.Cancel();

        Func<Task> act = () => waitTask;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [TestMethod]
    public async Task WaitForStateChangeAsync_IsolatesPerSwarm_DifferentIdsDoNotCrossWake()
    {
        var swarmA = Guid.NewGuid();
        var swarmB = Guid.NewGuid();

        var waitA = this.sink.WaitForStateChangeAsync(swarmA, CancellationToken.None);
        var waitB = this.sink.WaitForStateChangeAsync(swarmB, CancellationToken.None);

        await this.sink.SignalStateChangeAsync(swarmA, SwarmInstanceState.Planning);

        waitA.IsCompletedSuccessfully.Should().BeTrue();
        waitB.IsCompleted.Should().BeFalse("swarmA's signal must not wake swarmB's waiter");
        (await waitA).Should().Be(SwarmInstanceState.Planning);

        await this.sink.SignalStateChangeAsync(swarmB, SwarmInstanceState.Spawning);
        (await waitB).Should().Be(SwarmInstanceState.Spawning);
    }

    private static SwarmExecution NewExecution(Guid swarmId)
    {
        return new SwarmExecution
        {
            SwarmId = swarmId,
            Goal = "test",
            Cts = new CancellationTokenSource(),
            EventBus = new SwarmEventBus(),
            AgUiAdapter = new SwarmEventAdapter(),
            WorkDirectory = Path.GetTempPath(),
        };
    }
}
