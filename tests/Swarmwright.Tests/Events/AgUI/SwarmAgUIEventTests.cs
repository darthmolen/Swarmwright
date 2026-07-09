using System.Text.Json;
using Swarmwright.Events.AgUI;
using FluentAssertions;

namespace Swarmwright.Tests.Events.AgUI;

[TestClass]
public class SwarmAgUIEventTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // -----------------------------------------------------------------------
    // Lifecycle events
    // -----------------------------------------------------------------------

    [TestMethod]
    public void RunStartedEvent_Serializes_To_AgUI_WireFormat()
    {
        // Arrange
        var evt = new RunStartedEvent
        {
            ThreadId = "thread-abc",
            RunId = "run-123",
        };

        // Act
        var json = JsonSerializer.Serialize<SwarmAgUIEvent>(evt, JsonOptions);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Assert — must match AG-UI wire format exactly
        root.GetProperty("type").GetString().Should().Be("RUN_STARTED");
        root.GetProperty("threadId").GetString().Should().Be("thread-abc");
        root.GetProperty("runId").GetString().Should().Be("run-123");
    }

    [TestMethod]
    public void RunFinishedEvent_Serializes_To_AgUI_WireFormat()
    {
        var evt = new RunFinishedEvent
        {
            ThreadId = "thread-abc",
            RunId = "run-123",
        };

        var json = JsonSerializer.Serialize<SwarmAgUIEvent>(evt, JsonOptions);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("type").GetString().Should().Be("RUN_FINISHED");
        root.GetProperty("threadId").GetString().Should().Be("thread-abc");
        root.GetProperty("runId").GetString().Should().Be("run-123");
    }

    [TestMethod]
    public void RunFinishedEvent_Result_Is_Optional()
    {
        var evt = new RunFinishedEvent
        {
            ThreadId = "t",
            RunId = "r",
        };

        var json = JsonSerializer.Serialize<SwarmAgUIEvent>(evt, JsonOptions);

        // result should be absent or null
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("result", out var resultProp))
        {
            resultProp.ValueKind.Should().Be(JsonValueKind.Null);
        }
    }

    [TestMethod]
    public void RunErrorEvent_Serializes_To_AgUI_WireFormat()
    {
        var evt = new RunErrorEvent
        {
            Message = "Something failed",
            Code = "SWARM_TIMEOUT",
        };

        var json = JsonSerializer.Serialize<SwarmAgUIEvent>(evt, JsonOptions);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("type").GetString().Should().Be("RUN_ERROR");
        root.GetProperty("message").GetString().Should().Be("Something failed");
        root.GetProperty("code").GetString().Should().Be("SWARM_TIMEOUT");
    }

    // -----------------------------------------------------------------------
    // Step events (phase transitions)
    // -----------------------------------------------------------------------

    [TestMethod]
    public void StepStartedEvent_Serializes_To_AgUI_WireFormat()
    {
        var evt = new StepStartedEvent { StepName = "Planning" };

        var json = JsonSerializer.Serialize<SwarmAgUIEvent>(evt, JsonOptions);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("type").GetString().Should().Be("STEP_STARTED");
        root.GetProperty("stepName").GetString().Should().Be("Planning");
    }

    [TestMethod]
    public void StepFinishedEvent_Serializes_To_AgUI_WireFormat()
    {
        var evt = new StepFinishedEvent { StepName = "Executing" };

        var json = JsonSerializer.Serialize<SwarmAgUIEvent>(evt, JsonOptions);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("type").GetString().Should().Be("STEP_FINISHED");
        root.GetProperty("stepName").GetString().Should().Be("Executing");
    }

    // -----------------------------------------------------------------------
    // Text message events
    // -----------------------------------------------------------------------

    [TestMethod]
    public void TextMessageStartEvent_Serializes_To_AgUI_WireFormat()
    {
        var evt = new TextMessageStartEvent
        {
            MessageId = "msg-001",
            Role = "assistant",
        };

        var json = JsonSerializer.Serialize<SwarmAgUIEvent>(evt, JsonOptions);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("type").GetString().Should().Be("TEXT_MESSAGE_START");
        root.GetProperty("messageId").GetString().Should().Be("msg-001");
        root.GetProperty("role").GetString().Should().Be("assistant");
    }

    [TestMethod]
    public void TextMessageStartEvent_Includes_AgentName_Extension()
    {
        var evt = new TextMessageStartEvent
        {
            MessageId = "msg-001",
            Role = "assistant",
            AgentName = "researcher",
        };

        var json = JsonSerializer.Serialize<SwarmAgUIEvent>(evt, JsonOptions);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("agentName").GetString().Should().Be("researcher");
    }

    [TestMethod]
    public void TextMessageContentEvent_Serializes_To_AgUI_WireFormat()
    {
        var evt = new TextMessageContentEvent
        {
            MessageId = "msg-001",
            Delta = "Hello world",
        };

        var json = JsonSerializer.Serialize<SwarmAgUIEvent>(evt, JsonOptions);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("type").GetString().Should().Be("TEXT_MESSAGE_CONTENT");
        root.GetProperty("messageId").GetString().Should().Be("msg-001");
        root.GetProperty("delta").GetString().Should().Be("Hello world");
    }

    [TestMethod]
    public void TextMessageEndEvent_Serializes_To_AgUI_WireFormat()
    {
        var evt = new TextMessageEndEvent { MessageId = "msg-001" };

        var json = JsonSerializer.Serialize<SwarmAgUIEvent>(evt, JsonOptions);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("type").GetString().Should().Be("TEXT_MESSAGE_END");
        root.GetProperty("messageId").GetString().Should().Be("msg-001");
    }

    // -----------------------------------------------------------------------
    // Tool call events
    // -----------------------------------------------------------------------

    [TestMethod]
    public void ToolCallStartEvent_Serializes_To_AgUI_WireFormat()
    {
        var evt = new ToolCallStartEvent
        {
            ToolCallId = "tc-001",
            ToolCallName = "task_update",
            AgentName = "researcher",
        };

        var json = JsonSerializer.Serialize<SwarmAgUIEvent>(evt, JsonOptions);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("type").GetString().Should().Be("TOOL_CALL_START");
        root.GetProperty("toolCallId").GetString().Should().Be("tc-001");
        root.GetProperty("toolCallName").GetString().Should().Be("task_update");
        root.GetProperty("agentName").GetString().Should().Be("researcher");
    }

    [TestMethod]
    public void ToolCallStartEvent_ParentMessageId_Is_Optional()
    {
        var evt = new ToolCallStartEvent
        {
            ToolCallId = "tc-001",
            ToolCallName = "task_update",
        };

        var json = JsonSerializer.Serialize<SwarmAgUIEvent>(evt, JsonOptions);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // parentMessageId should be absent or null
        if (root.TryGetProperty("parentMessageId", out var prop))
        {
            prop.ValueKind.Should().Be(JsonValueKind.Null);
        }
    }

    [TestMethod]
    public void ToolCallArgsEvent_Serializes_To_AgUI_WireFormat()
    {
        var evt = new ToolCallArgsEvent
        {
            ToolCallId = "tc-001",
            Delta = "{\"task_id\":\"abc\",\"status\":\"Completed\"}",
        };

        var json = JsonSerializer.Serialize<SwarmAgUIEvent>(evt, JsonOptions);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("type").GetString().Should().Be("TOOL_CALL_ARGS");
        root.GetProperty("toolCallId").GetString().Should().Be("tc-001");
        root.GetProperty("delta").GetString().Should().Contain("task_id");
    }

    [TestMethod]
    public void ToolCallEndEvent_Serializes_To_AgUI_WireFormat()
    {
        var evt = new ToolCallEndEvent { ToolCallId = "tc-001" };

        var json = JsonSerializer.Serialize<SwarmAgUIEvent>(evt, JsonOptions);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("type").GetString().Should().Be("TOOL_CALL_END");
        root.GetProperty("toolCallId").GetString().Should().Be("tc-001");
    }

    [TestMethod]
    public void ToolCallResultEvent_Serializes_To_AgUI_WireFormat()
    {
        var evt = new ToolCallResultEvent
        {
            ToolCallId = "tc-001",
            Content = "{\"success\":true}",
            Role = "tool",
        };

        var json = JsonSerializer.Serialize<SwarmAgUIEvent>(evt, JsonOptions);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("type").GetString().Should().Be("TOOL_CALL_RESULT");
        root.GetProperty("toolCallId").GetString().Should().Be("tc-001");
        root.GetProperty("content").GetString().Should().Be("{\"success\":true}");
        root.GetProperty("role").GetString().Should().Be("tool");
    }

    // -----------------------------------------------------------------------
    // State management events
    // -----------------------------------------------------------------------

    [TestMethod]
    public void StateSnapshotEvent_Serializes_To_AgUI_WireFormat()
    {
        var snapshotJson = JsonSerializer.SerializeToElement(new
        {
            phase = "Executing",
            roundNumber = 2,
            tasks = new[] { new { id = "t1", status = "Completed" } },
        });

        var evt = new StateSnapshotEvent { Snapshot = snapshotJson };

        var json = JsonSerializer.Serialize<SwarmAgUIEvent>(evt, JsonOptions);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("type").GetString().Should().Be("STATE_SNAPSHOT");
        root.GetProperty("snapshot").GetProperty("phase").GetString().Should().Be("Executing");
        root.GetProperty("snapshot").GetProperty("roundNumber").GetInt32().Should().Be(2);
    }

    [TestMethod]
    public void StateDeltaEvent_Serializes_JsonPatch_Array()
    {
        var patchJson = JsonSerializer.SerializeToElement(new object[]
        {
            new { op = "replace", path = "/phase", value = (object)"Synthesizing" },
            new { op = "replace", path = "/roundNumber", value = (object)3 },
        });

        var evt = new StateDeltaEvent { Delta = patchJson };

        var json = JsonSerializer.Serialize<SwarmAgUIEvent>(evt, JsonOptions);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("type").GetString().Should().Be("STATE_DELTA");
        root.GetProperty("delta").GetArrayLength().Should().Be(2);
        root.GetProperty("delta")[0].GetProperty("op").GetString().Should().Be("replace");
    }

    // -----------------------------------------------------------------------
    // Custom swarm events
    // -----------------------------------------------------------------------

    [TestMethod]
    public void SwarmCustomEvent_Serializes_With_Name_And_Value()
    {
        var valueJson = JsonSerializer.SerializeToElement(new
        {
            taskId = "abc",
            status = "Completed",
            agent = "researcher",
        });

        var evt = new SwarmCustomEvent
        {
            Name = "SWARM_TASK_UPDATED",
            Value = valueJson,
            AgentName = "researcher",
        };

        var json = JsonSerializer.Serialize<SwarmAgUIEvent>(evt, JsonOptions);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("type").GetString().Should().Be("SWARM_CUSTOM");
        root.GetProperty("name").GetString().Should().Be("SWARM_TASK_UPDATED");
        root.GetProperty("value").GetProperty("taskId").GetString().Should().Be("abc");
        root.GetProperty("agentName").GetString().Should().Be("researcher");
    }

    [TestMethod]
    public void SwarmCustomEvent_AgentName_Is_Optional()
    {
        var valueJson = JsonSerializer.SerializeToElement(new { sender = "a", recipient = "b" });
        var evt = new SwarmCustomEvent
        {
            Name = "SWARM_INBOX_MESSAGE",
            Value = valueJson,
        };

        var json = JsonSerializer.Serialize<SwarmAgUIEvent>(evt, JsonOptions);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("type").GetString().Should().Be("SWARM_CUSTOM");
        root.GetProperty("name").GetString().Should().Be("SWARM_INBOX_MESSAGE");
        if (root.TryGetProperty("agentName", out var agentProp))
        {
            agentProp.ValueKind.Should().Be(JsonValueKind.Null);
        }
    }

    // -----------------------------------------------------------------------
    // Polymorphic serialization — base type round-trips correctly
    // -----------------------------------------------------------------------

    [TestMethod]
    public void Polymorphic_Serialization_Preserves_Derived_Properties()
    {
        SwarmAgUIEvent evt = new ToolCallStartEvent
        {
            ToolCallId = "tc-poly",
            ToolCallName = "inbox_send",
        };

        // Serialize as base type — must include derived properties
        var json = JsonSerializer.Serialize(evt, JsonOptions);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("type").GetString().Should().Be("TOOL_CALL_START");
        root.GetProperty("toolCallId").GetString().Should().Be("tc-poly");
        root.GetProperty("toolCallName").GetString().Should().Be("inbox_send");
    }
}
