using System.Text.Json;
using Swarmwright.Events;
using Swarmwright.Hosting;
using FluentAssertions;

namespace Swarmwright.Tests.Hosting;

[TestClass]
public class SwarmExecutionSerializationTests
{
    [TestMethod]
    public void SwarmExecution_CannotBeDirectlySerialized()
    {
        var execution = new SwarmExecution
        {
            SwarmId = Guid.NewGuid(),
            Goal = "Test goal",
            Cts = new CancellationTokenSource(),
            EventBus = new SwarmEventBus(),
            AgUiAdapter = new Swarmwright.Events.AgUI.SwarmEventAdapter(),
            WorkDirectory = Path.Combine(Path.GetTempPath(), "swarm-test-" + Guid.NewGuid().ToString("N")),
        };

        // SwarmExecution contains CancellationTokenSource which has IntPtr — not serializable
        var act = () => JsonSerializer.Serialize(execution);
        act.Should().Throw<NotSupportedException>();

        execution.Dispose();
    }

    [TestMethod]
    public void SwarmExecution_DtoProjection_IsSerializable()
    {
        var execution = new SwarmExecution
        {
            SwarmId = Guid.NewGuid(),
            Goal = "Test goal",
            TemplateKey = "deep-research",
            Cts = new CancellationTokenSource(),
            EventBus = new SwarmEventBus(),
            AgUiAdapter = new Swarmwright.Events.AgUI.SwarmEventAdapter(),
            WorkDirectory = Path.Combine(Path.GetTempPath(), "swarm-test-" + Guid.NewGuid().ToString("N")),
        };

        // The DTO projection used by endpoints should serialize fine.
        // Phase no longer lives on SwarmExecution — the /get endpoint reads
        // it from the DB via SwarmEntity.Phase/State instead.
        var dto = new
        {
            swarmId = execution.SwarmId,
            goal = execution.Goal,
            templateKey = execution.TemplateKey,
            isRunning = execution.IsRunning,
            isTerminal = execution.IsTerminal,
        };

        var json = JsonSerializer.Serialize(dto);
        json.Should().Contain("Test goal");
        json.Should().Contain("deep-research");

        execution.Dispose();
    }
}
