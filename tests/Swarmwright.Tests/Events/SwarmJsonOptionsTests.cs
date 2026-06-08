using System.Text.Json;
using Swarmwright.Events;
using Swarmwright.Events.AgUI;
using Swarmwright.Models;
using Swarmwright.Models.Enums;
using FluentAssertions;

namespace Swarmwright.Tests.Events;

/// <summary>
/// Regression tests for the shared <see cref="SwarmJsonOptions"/> used by every
/// swarm JSON writer. All writers must serialize enums as strings (for example,
/// <c>"Pending"</c>) rather than their numeric values so the frontend can
/// discriminate task state without a magic-number mapping.
/// </summary>
[TestClass]
public class SwarmJsonOptionsTests
{
    [TestMethod]
    public void SwarmJsonOptions_Default_SerializesEnumAsString()
    {
        // Arrange
        var payload = new { status = TaskState.Pending };

        // Act
        var json = JsonSerializer.Serialize(payload, SwarmJsonOptions.Default);

        // Assert
        json.Should().Contain("\"status\":\"Pending\"");
        json.Should().NotContain("\"status\":1");
    }

    [TestMethod]
    public void EmitPlanSnapshot_SerializesTaskStatusAsString()
    {
        // Arrange — mirror the shape built in SwarmOrchestrator.PlanAsync.
        var tasks = new[]
        {
            new SwarmTask
            {
                Id = "task-1",
                Subject = "Research the topic",
                WorkerName = "researcher",
                WorkerRole = "researcher",
                Status = TaskState.Pending,
            },
        };

        var snapshot = new
        {
            phase = "Spawning",
            roundNumber = 0,
            tasks,
            agents = Array.Empty<object>(),
            messages = Array.Empty<object>(),
        };

        // Act
        var element = JsonSerializer.SerializeToElement(snapshot, SwarmJsonOptions.Default);
        var json = element.GetRawText();

        // Assert
        json.Should().Contain("\"status\":\"Pending\"");
        json.Should().NotContain("\"status\":1");
    }

    [TestMethod]
    public void EmitSpawnSnapshot_SerializesTaskStatusAsString()
    {
        // Arrange — mirror the shape built in SwarmOrchestrator.SpawnAsync.
        var tasks = new[]
        {
            new SwarmTask
            {
                Id = "task-1",
                Subject = "Draft the outline",
                WorkerName = "writer",
                WorkerRole = "writer",
                Status = TaskState.InProgress,
            },
        };

        var agents = new[]
        {
            new { name = "writer", role = "writer", displayName = "Writer Agent" },
        };

        var snapshot = new
        {
            phase = "Executing",
            roundNumber = 0,
            tasks,
            agents,
            messages = Array.Empty<object>(),
        };

        // Act
        var element = JsonSerializer.SerializeToElement(snapshot, SwarmJsonOptions.Default);
        var json = element.GetRawText();

        // Assert
        json.Should().Contain("\"status\":\"InProgress\"");
        json.Should().NotContain("\"status\":2");
    }

    [TestMethod]
    public void SseEventWriter_WriteEnvelopeAsync_SerializesEnumAsString()
    {
        // Arrange
        var data = new { taskId = "task-1", status = TaskState.Completed };

        // Act
        var sse = SseEventWriter.FormatEvent("task.updated", data);

        // Assert
        sse.Should().Contain("\"status\":\"Completed\"");
        sse.Should().NotContain("\"status\":3");
    }

    [TestMethod]
    public void SseEventWriter_WriteEventAsync_SerializesEnumAsString()
    {
        // Arrange — a state snapshot event whose payload carries the task status.
        // The snapshot is pre-serialized through SwarmJsonOptions.Default so the
        // enum becomes a string, and the outer FormatAgUIEvent call must preserve
        // that string without re-encoding it as a number.
        var snapshot = JsonSerializer.SerializeToElement(
            new
            {
                tasks = new[]
                {
                    new { id = "task-1", status = TaskState.Failed },
                },
            },
            SwarmJsonOptions.Default);

        SwarmAgUIEvent evt = new StateSnapshotEvent
        {
            Snapshot = snapshot,
        };

        // Act
        var sse = SseEventWriter.FormatAgUIEvent(evt);

        // Assert
        sse.Should().Contain("\"status\":\"Failed\"");
        sse.Should().NotContain("\"status\":4");
    }

    [TestMethod]
    public void AgUIEventInterceptor_SerializesEnumAsString()
    {
        // Arrange — route a payload carrying an enum through the same
        // JsonSerializerOptions instance the interceptor uses. Once the
        // interceptor references SwarmJsonOptions.Default, both sides match.
        var payload = new { status = TaskState.Pending };

        // Act
        var json = JsonSerializer.Serialize(payload, AgUIEventInterceptor.JsonOptions);

        // Assert
        json.Should().Contain("\"status\":\"Pending\"");
        json.Should().NotContain("\"status\":1");
    }
}
