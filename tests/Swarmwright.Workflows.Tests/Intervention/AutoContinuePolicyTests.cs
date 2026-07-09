using Swarmwright.Models.Enums;
using Swarmwright.Workflows.Intervention;
using FluentAssertions;

namespace Swarmwright.Workflows.Tests.Intervention;

/// <summary>
/// Decision-table unit tests for <see cref="AutoContinuePolicy"/>. Covers the
/// retry-counter behavior on <c>AwaitingIntervention</c>, the
/// <c>AwaitingFeedback</c> bail, and the defensive throws on out-of-scope
/// states.
/// </summary>
[TestClass]
public sealed class AutoContinuePolicyTests
{
    [TestMethod]
    public async Task AwaitingIntervention_FirstAttempt_ReturnsSmartContinue()
    {
        var policy = new AutoContinuePolicy(maxRetries: 3);
        var context = new InterventionContext(
            Guid.NewGuid(),
            SwarmInstanceState.AwaitingIntervention,
            Attempt: 1,
            LastFailureReason: null);

        var decision = await policy.DecideAsync(context, CancellationToken.None);

        decision.Should().Be(InterventionDecision.SmartContinue);
    }

    [TestMethod]
    public async Task AwaitingIntervention_AtMaxRetries_StillReturnsSmartContinue()
    {
        var policy = new AutoContinuePolicy(maxRetries: 3);
        var context = new InterventionContext(
            Guid.NewGuid(),
            SwarmInstanceState.AwaitingIntervention,
            Attempt: 3,
            LastFailureReason: null);

        var decision = await policy.DecideAsync(context, CancellationToken.None);

        decision.Should().Be(InterventionDecision.SmartContinue);
    }

    [TestMethod]
    public async Task AwaitingIntervention_AfterMaxRetries_ReturnsBail()
    {
        var policy = new AutoContinuePolicy(maxRetries: 3);
        var context = new InterventionContext(
            Guid.NewGuid(),
            SwarmInstanceState.AwaitingIntervention,
            Attempt: 4,
            LastFailureReason: null);

        var decision = await policy.DecideAsync(context, CancellationToken.None);

        decision.Should().Be(InterventionDecision.Bail);
    }

    [TestMethod]
    public async Task AwaitingFeedback_AlwaysReturnsBail()
    {
        var policy = new AutoContinuePolicy(maxRetries: 3);
        var context = new InterventionContext(
            Guid.NewGuid(),
            SwarmInstanceState.AwaitingFeedback,
            Attempt: 1,
            LastFailureReason: null);

        var decision = await policy.DecideAsync(context, CancellationToken.None);

        decision.Should().Be(InterventionDecision.Bail);
    }

    [TestMethod]
    public async Task NeedsDiagnosis_ThrowsArgumentException()
    {
        var policy = new AutoContinuePolicy(maxRetries: 3);
        var context = new InterventionContext(
            Guid.NewGuid(),
            SwarmInstanceState.NeedsDiagnosis,
            Attempt: 1,
            LastFailureReason: null);

        Func<Task> act = () => policy.DecideAsync(context, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>(
            "the executor short-circuits NeedsDiagnosis before consulting the policy");
    }

    [TestMethod]
    public async Task TerminalState_ThrowsArgumentException()
    {
        var policy = new AutoContinuePolicy(maxRetries: 3);
        var context = new InterventionContext(
            Guid.NewGuid(),
            SwarmInstanceState.Complete,
            Attempt: 1,
            LastFailureReason: null);

        Func<Task> act = () => policy.DecideAsync(context, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>(
            "policies should never be consulted on terminal states");
    }

    [TestMethod]
    public void Constructor_RejectsZeroOrNegativeMaxRetries()
    {
        Action act = () => _ = new AutoContinuePolicy(maxRetries: 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
