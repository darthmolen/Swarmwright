using Swarmwright.Models.Enums;
using Swarmwright.Workflows.Intervention;
using FluentAssertions;

namespace Swarmwright.Workflows.Tests.Intervention;

/// <summary>
/// Decision-table unit tests for <see cref="BailToFailedFastPolicy"/>. The
/// test policy is intentionally trivial — every routed pause state bails —
/// so the surface area is small.
/// </summary>
[TestClass]
public sealed class BailToFailedFastPolicyTests
{
    [TestMethod]
    public async Task AwaitingIntervention_ReturnsBail()
    {
        var policy = new BailToFailedFastPolicy();
        var context = new InterventionContext(
            Guid.NewGuid(),
            SwarmInstanceState.AwaitingIntervention,
            Attempt: 1,
            LastFailureReason: null);

        var decision = await policy.DecideAsync(context, CancellationToken.None);

        decision.Should().Be(InterventionDecision.Bail);
    }

    [TestMethod]
    public async Task AwaitingFeedback_ReturnsBail()
    {
        var policy = new BailToFailedFastPolicy();
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
        var policy = new BailToFailedFastPolicy();
        var context = new InterventionContext(
            Guid.NewGuid(),
            SwarmInstanceState.NeedsDiagnosis,
            Attempt: 1,
            LastFailureReason: null);

        Func<Task> act = () => policy.DecideAsync(context, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }
}
