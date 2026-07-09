using System.Text.Json;
using System.Threading.Channels;
using Swarmwright.Events.AgUI;
using FluentAssertions;

namespace Swarmwright.Tests.Events.AgUI;

[TestClass]
public class SwarmEventAdapterTests
{
    // -----------------------------------------------------------------------
    // Construction and channel access
    // -----------------------------------------------------------------------

    [TestMethod]
    public void Constructor_Creates_Readable_Channel()
    {
        var adapter = new SwarmEventAdapter();
        adapter.Reader.Should().NotBeNull();
    }

    // -----------------------------------------------------------------------
    // Lifecycle events
    // -----------------------------------------------------------------------

    [TestMethod]
    public async Task EmitRunStartedAsync_Writes_RunStartedEvent_To_Channel()
    {
        var adapter = new SwarmEventAdapter();
        var swarmId = Guid.NewGuid();

        await adapter.EmitRunStartedAsync(swarmId, "Test goal");

        var evt = await ReadOneAsync(adapter);
        evt.Should().BeOfType<RunStartedEvent>();
        var started = (RunStartedEvent)evt;
        started.ThreadId.Should().Be(swarmId.ToString());
        started.RunId.Should().NotBeNullOrEmpty();
    }

    [TestMethod]
    public async Task EmitRunFinishedAsync_Writes_RunFinishedEvent_To_Channel()
    {
        var adapter = new SwarmEventAdapter();
        var swarmId = Guid.NewGuid();

        // Emit started first so the adapter captures the runId
        await adapter.EmitRunStartedAsync(swarmId, "goal");
        _ = await ReadOneAsync(adapter);

        await adapter.EmitRunFinishedAsync(swarmId);

        var evt = await ReadOneAsync(adapter);
        evt.Should().BeOfType<RunFinishedEvent>();
        var finished = (RunFinishedEvent)evt;
        finished.ThreadId.Should().Be(swarmId.ToString());
    }

    [TestMethod]
    public async Task EmitRunErrorAsync_Writes_RunErrorEvent_To_Channel()
    {
        var adapter = new SwarmEventAdapter();

        await adapter.EmitRunErrorAsync(Guid.NewGuid(), "TIMEOUT", "Planning timed out");

        var evt = await ReadOneAsync(adapter);
        evt.Should().BeOfType<RunErrorEvent>();
        var error = (RunErrorEvent)evt;
        error.Code.Should().Be("TIMEOUT");
        error.Message.Should().Be("Planning timed out");
    }

    // -----------------------------------------------------------------------
    // Step events (phase transitions)
    // -----------------------------------------------------------------------

    [TestMethod]
    public async Task EmitStepStartedAsync_Writes_StepStartedEvent()
    {
        var adapter = new SwarmEventAdapter();

        await adapter.EmitStepStartedAsync("Planning");

        var evt = await ReadOneAsync(adapter);
        evt.Should().BeOfType<StepStartedEvent>();
        ((StepStartedEvent)evt).StepName.Should().Be("Planning");
    }

    [TestMethod]
    public async Task EmitStepFinishedAsync_Writes_StepFinishedEvent()
    {
        var adapter = new SwarmEventAdapter();

        await adapter.EmitStepFinishedAsync("Executing");

        var evt = await ReadOneAsync(adapter);
        evt.Should().BeOfType<StepFinishedEvent>();
        ((StepFinishedEvent)evt).StepName.Should().Be("Executing");
    }

    // -----------------------------------------------------------------------
    // State events
    // -----------------------------------------------------------------------

    [TestMethod]
    public async Task EmitStateSnapshotAsync_Writes_StateSnapshotEvent()
    {
        var adapter = new SwarmEventAdapter();
        var snapshot = JsonSerializer.SerializeToElement(new { phase = "Executing", roundNumber = 1 });

        await adapter.EmitStateSnapshotAsync(snapshot);

        var evt = await ReadOneAsync(adapter);
        evt.Should().BeOfType<StateSnapshotEvent>();
        var snap = (StateSnapshotEvent)evt;
        snap.Snapshot!.Value.GetProperty("phase").GetString().Should().Be("Executing");
    }

    [TestMethod]
    public async Task EmitStateDeltaAsync_Writes_StateDeltaEvent()
    {
        var adapter = new SwarmEventAdapter();
        var patch = JsonSerializer.SerializeToElement(new[]
        {
            new { op = "replace", path = "/phase", value = "Synthesizing" },
        });

        await adapter.EmitStateDeltaAsync(patch);

        var evt = await ReadOneAsync(adapter);
        evt.Should().BeOfType<StateDeltaEvent>();
        var delta = (StateDeltaEvent)evt;
        delta.Delta!.Value.GetArrayLength().Should().Be(1);
    }

    // -----------------------------------------------------------------------
    // Custom domain events
    // -----------------------------------------------------------------------

    [TestMethod]
    public async Task EmitCustomAsync_Writes_SwarmCustomEvent()
    {
        var adapter = new SwarmEventAdapter();
        var value = JsonSerializer.SerializeToElement(new { taskId = "t1", status = "Completed" });

        await adapter.EmitCustomAsync("SWARM_TASK_UPDATED", value, "researcher");

        var evt = await ReadOneAsync(adapter);
        evt.Should().BeOfType<SwarmCustomEvent>();
        var custom = (SwarmCustomEvent)evt;
        custom.Name.Should().Be("SWARM_TASK_UPDATED");
        custom.AgentName.Should().Be("researcher");
        custom.Value.GetProperty("taskId").GetString().Should().Be("t1");
    }

    [TestMethod]
    public async Task EmitCustomAsync_Without_AgentName()
    {
        var adapter = new SwarmEventAdapter();
        var value = JsonSerializer.SerializeToElement(new { sender = "a", recipient = "b" });

        await adapter.EmitCustomAsync("SWARM_INBOX_MESSAGE", value);

        var evt = await ReadOneAsync(adapter);
        var custom = (SwarmCustomEvent)evt;
        custom.AgentName.Should().BeNull();
    }

    // -----------------------------------------------------------------------
    // Pass-through for interceptor events
    // -----------------------------------------------------------------------

    [TestMethod]
    public async Task EmitAsync_Passes_Through_Any_SwarmAgUIEvent()
    {
        var adapter = new SwarmEventAdapter();
        var toolStart = new ToolCallStartEvent
        {
            ToolCallId = "tc-001",
            ToolCallName = "task_update",
            AgentName = "worker",
        };

        await adapter.EmitAsync(toolStart);

        var evt = await ReadOneAsync(adapter);
        evt.Should().BeOfType<ToolCallStartEvent>();
        ((ToolCallStartEvent)evt).ToolCallId.Should().Be("tc-001");
    }

    // -----------------------------------------------------------------------
    // Multiple events accumulate in channel
    // -----------------------------------------------------------------------

    [TestMethod]
    public async Task Multiple_Events_Accumulate_In_Order()
    {
        var adapter = new SwarmEventAdapter();

        await adapter.EmitStepStartedAsync("Planning");
        await adapter.EmitStepFinishedAsync("Planning");
        await adapter.EmitStepStartedAsync("Executing");

        var e1 = await ReadOneAsync(adapter);
        var e2 = await ReadOneAsync(adapter);
        var e3 = await ReadOneAsync(adapter);

        e1.Should().BeOfType<StepStartedEvent>();
        e2.Should().BeOfType<StepFinishedEvent>();
        e3.Should().BeOfType<StepStartedEvent>();
        ((StepStartedEvent)e3).StepName.Should().Be("Executing");
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static async Task<SwarmAgUIEvent> ReadOneAsync(SwarmEventAdapter adapter)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        return await adapter.Reader.ReadAsync(cts.Token);
    }
}
