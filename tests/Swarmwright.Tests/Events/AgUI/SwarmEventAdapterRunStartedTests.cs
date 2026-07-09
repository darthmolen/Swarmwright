using Swarmwright.Events.AgUI;
using FluentAssertions;

namespace Swarmwright.Tests.Events.AgUI;

/// <summary>
/// Verifies that <see cref="SwarmEventAdapter.EmitRunStartedAsync"/> carries the
/// user-supplied goal on the emitted <see cref="RunStartedEvent"/> — the adapter
/// previously discarded the goal via <c>_ = goal;</c>, so downstream consumers
/// (AG-UI frontends, session persistence) had no way to render what the swarm
/// was actually trying to do.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SwarmEventAdapterRunStartedTests
{
    /// <summary>
    /// The emitted RunStartedEvent exposes the goal string.
    /// </summary>
    [TestMethod]
    public async Task EmitRunStartedAsync_IncludesGoalInPayload()
    {
        var adapter = new SwarmEventAdapter();
        var swarmId = Guid.NewGuid();

        await adapter.EmitRunStartedAsync(swarmId, "research the merits of bluegrass");

        adapter.Reader.TryRead(out var evt).Should().BeTrue();
        var runStarted = evt.Should().BeOfType<RunStartedEvent>().Subject;
        runStarted.ThreadId.Should().Be(swarmId.ToString());
        runStarted.Goal.Should().Be("research the merits of bluegrass");
    }
}
