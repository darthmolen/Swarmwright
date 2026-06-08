using Swarmwright.Events;
using Swarmwright.Events.AgUI;
using Swarmwright.Hosting;
using Swarmwright.Models.Enums;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace Swarmwright.Tests.Events;

/// <summary>
/// Covers the <see cref="SwarmEmissionBroker"/> contract: it resolves the
/// active <see cref="SwarmExecution"/> via <see cref="ISwarmManager.GetSwarm"/>
/// and emits <c>SWARM_TASK_UPDATED</c> on that execution's adapter. When the
/// swarm is not in the active dictionary (evicted, never-registered, or
/// already disposed) the broker logs a Warning and returns without throwing.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SwarmEmissionBrokerTests
{
    private Mock<ISwarmManager> manager = null!;
    private Mock<ILogger<SwarmEmissionBroker>> logger = null!;
    private SwarmEmissionBroker broker = null!;

    [TestInitialize]
    public void Initialize()
    {
        this.manager = new Mock<ISwarmManager>();
        this.logger = new Mock<ILogger<SwarmEmissionBroker>>();

        // LoggerMessage-generated partial methods short-circuit when
        // ILogger.IsEnabled returns false. Moq defaults to false, so
        // verification of the Warning would otherwise fail against code
        // that never reached the Log call.
        this.logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        this.broker = new SwarmEmissionBroker(this.manager.Object, this.logger.Object);
    }

    [TestMethod]
    public async Task EmitTaskUpdatedAsync_WhenSwarmActive_EmitsOnAdapter()
    {
        var swarmId = Guid.NewGuid();
        var adapter = new SwarmEventAdapter();
        using var execution = BuildExecution(swarmId, adapter);
        this.manager.Setup(m => m.GetSwarm(swarmId)).Returns(execution);

        await this.broker.EmitTaskUpdatedAsync(
            swarmId,
            taskId: "t-abc",
            status: TaskState.InProgress,
            agentName: "architect");

        var evt = await DrainOneAsync(adapter);
        evt.Should().NotBeNull("the broker must resolve the execution's adapter and emit on it");
        evt!.Name.Should().Be("SWARM_TASK_UPDATED");
        evt.AgentName.Should().Be("architect");
        evt.Value.GetProperty("taskId").GetString().Should().Be("t-abc");
        evt.Value.GetProperty("status").GetString().Should().Be(nameof(TaskState.InProgress));
        evt.Value.GetProperty("agent").GetString().Should().Be("architect");
    }

    [TestMethod]
    public async Task EmitTaskUpdatedAsync_WhenSwarmNotActive_LogsWarningAndDoesNotThrow()
    {
        var swarmId = Guid.NewGuid();
        this.manager.Setup(m => m.GetSwarm(swarmId)).Returns((SwarmExecution?)null);

        var act = async () => await this.broker.EmitTaskUpdatedAsync(
            swarmId,
            taskId: "t-abc",
            status: TaskState.InProgress,
            agentName: "architect");

        await act.Should().NotThrowAsync(
            "the broker must swallow missing-swarm lookups so a late/duplicate emission can't bring down the state-service transition");

        this.logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once,
            "surface the miss instead of dropping silently — that was the original smell");
    }

    [TestMethod]
    public async Task EmitTaskUpdatedAsync_PassesAgentNameToEventPayloadAndTopLevel()
    {
        var swarmId = Guid.NewGuid();
        var adapter = new SwarmEventAdapter();
        using var execution = BuildExecution(swarmId, adapter);
        this.manager.Setup(m => m.GetSwarm(swarmId)).Returns(execution);

        await this.broker.EmitTaskUpdatedAsync(
            swarmId,
            taskId: "t-xyz",
            status: TaskState.Completed,
            agentName: "cost-expert");

        var evt = await DrainOneAsync(adapter);
        evt.Should().NotBeNull();
        evt!.AgentName.Should().Be(
            "cost-expert",
            "the top-level AgentName lets the frontend filter events by agent without parsing the payload");
        evt.Value.GetProperty("agent").GetString().Should().Be(
            "cost-expert",
            "the payload agent field is the authoritative attribution for the Kanban card");
    }

    // ---- Helpers ----

    private static SwarmExecution BuildExecution(Guid swarmId, SwarmEventAdapter adapter)
    {
        return new SwarmExecution
        {
            SwarmId = swarmId,
            Goal = "test",
            Cts = new CancellationTokenSource(),
            EventBus = new SwarmEventBus(),
            AgUiAdapter = adapter,
            WorkDirectory = Path.GetTempPath(),
        };
    }

    private static async Task<SwarmCustomEvent?> DrainOneAsync(SwarmEventAdapter adapter)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        try
        {
            while (await adapter.Reader.WaitToReadAsync(cts.Token).ConfigureAwait(false))
            {
                while (adapter.Reader.TryRead(out var evt))
                {
                    if (evt is SwarmCustomEvent custom)
                    {
                        return custom;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // No event arrived before timeout.
        }

        return null;
    }
}
