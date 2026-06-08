using Swarmwright.Events;
using Swarmwright.Events.AgUI;
using FluentAssertions;

namespace Swarmwright.Tests.Events;

[TestClass]
public class SseEventWriterTests
{
    [TestMethod]
    public void FormatEvent_ProducesValidSseFormat()
    {
        // Arrange
        var eventType = "task.completed";
        var data = new { taskId = 1 };

        // Act
        var result = SseEventWriter.FormatEvent(eventType, data);

        // Assert
        result.Should().StartWith("data: ");
        result.Should().Contain("\"type\":\"task.completed\"");
        result.Should().EndWith("\n\n");
    }

    [TestMethod]
    public void FormatEvent_SerializesDataAsJson()
    {
        // Arrange
        var data = new { agentName = "researcher", status = "done" };

        // Act
        var result = SseEventWriter.FormatEvent("agent.status", data);

        // Assert
        result.Should().Contain("\"type\":\"agent.status\"");
        result.Should().Contain("\"agentName\":\"researcher\"");
        result.Should().Contain("\"status\":\"done\"");
    }

    [TestMethod]
    public void FormatEvent_HandlesNullData()
    {
        // Act
        var result = SseEventWriter.FormatEvent("heartbeat", null);

        // Assert
        result.Should().Be("data: {\"type\":\"heartbeat\",\"data\":null}\n\n");
    }

    [TestMethod]
    public void FormatHeartbeat_ProducesComment()
    {
        // Act
        var result = SseEventWriter.FormatHeartbeat();

        // Assert
        result.Should().Be(":\n\n");
    }

    // -----------------------------------------------------------------------
    // AG-UI event formatting
    // -----------------------------------------------------------------------

    [TestMethod]
    public void FormatAgUIEvent_ProducesValidSseFormat()
    {
        // Arrange
        var evt = new RunStartedEvent
        {
            ThreadId = "thread-1",
            RunId = "run-1",
        };

        // Act
        var result = SseEventWriter.FormatAgUIEvent(evt);

        // Assert
        result.Should().StartWith("data: ");
        result.Should().EndWith("\n\n");
        result.Should().Contain("\"type\":\"RUN_STARTED\"");
        result.Should().Contain("\"threadId\":\"thread-1\"");
    }

    [TestMethod]
    public void FormatAgUIEvent_Preserves_Polymorphic_Type_Discriminator()
    {
        // Arrange
        SwarmAgUIEvent evt = new ToolCallStartEvent
        {
            ToolCallId = "tc-1",
            ToolCallName = "task_update",
            AgentName = "worker",
        };

        // Act
        var result = SseEventWriter.FormatAgUIEvent(evt);

        // Assert — polymorphic serialization must include derived properties
        result.Should().Contain("\"type\":\"TOOL_CALL_START\"");
        result.Should().Contain("\"toolCallId\":\"tc-1\"");
        result.Should().Contain("\"toolCallName\":\"task_update\"");
        result.Should().Contain("\"agentName\":\"worker\"");
    }

    [TestMethod]
    public void FormatAgUIEvent_Omits_Null_Optional_Fields()
    {
        // Arrange
        var evt = new ToolCallStartEvent
        {
            ToolCallId = "tc-2",
            ToolCallName = "inbox_send",
        };

        // Act
        var result = SseEventWriter.FormatAgUIEvent(evt);

        // Assert — parentMessageId and agentName are null, should not appear
        result.Should().NotContain("parentMessageId");
        result.Should().NotContain("agentName");
    }
}
