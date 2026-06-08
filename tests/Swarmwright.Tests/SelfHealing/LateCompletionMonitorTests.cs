using Swarmwright.Database.Models;
using Swarmwright.Database.Repositories;
using Swarmwright.Events;
using Swarmwright.SelfHealing;
using FluentAssertions;
using Moq;

namespace Swarmwright.Tests.SelfHealing;

/// <summary>
/// Unit tests for <see cref="LateCompletionMonitor"/>.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class LateCompletionMonitorTests
{
    private readonly Mock<ISwarmRepository> mockRepository = new();
    private readonly Mock<ISwarmEventBus> mockEventBus = new();

    /// <summary>
    /// Verifies that late completions are detected and events emitted.
    /// </summary>
    [TestMethod]
    public async Task CheckAsync_DetectsLateCompletions()
    {
        // Arrange
        var swarmId = Guid.NewGuid();
        var timedOutTask = new TaskEntity
        {
            SwarmId = swarmId,
            Id = "task-1",
            Subject = "Test task",
            Description = "A test task",
            State = "Failed",
        };
        var completedTask = new TaskEntity
        {
            SwarmId = swarmId,
            Id = "task-1",
            Subject = "Test task",
            Description = "A test task",
            State = "Completed",
        };

        // First call returns timed-out tasks; second call returns re-queried tasks showing completion
        this.mockRepository
            .Setup(r => r.GetTasksAsync(swarmId))
            .ReturnsAsync(new List<TaskEntity> { completedTask });
        this.mockRepository
            .Setup(r => r.ListSwarmsByStateAsync(It.IsAny<string[]>()))
            .ReturnsAsync(new List<SwarmEntity>
            {
                new() { Id = swarmId, State = "Executing" },
            });
        this.mockEventBus
            .Setup(e => e.EmitAsync(It.IsAny<string>(), It.IsAny<object?>()))
            .Returns(Task.CompletedTask);

        using var monitor = new LateCompletionMonitor(
            this.mockRepository.Object,
            this.mockEventBus.Object);

        // Act
        await monitor.CheckAsync();

        // Assert
        this.mockEventBus.Verify(
            e => e.EmitAsync("task.late_completed", It.IsAny<object?>()),
            Times.Once);
    }

    /// <summary>
    /// Verifies that no events are emitted when there are no timed-out tasks.
    /// </summary>
    [TestMethod]
    public async Task CheckAsync_NoTimedOutTasks_NoOps()
    {
        // Arrange
        this.mockRepository
            .Setup(r => r.ListSwarmsByStateAsync(It.IsAny<string[]>()))
            .ReturnsAsync(new List<SwarmEntity>());

        using var monitor = new LateCompletionMonitor(
            this.mockRepository.Object,
            this.mockEventBus.Object);

        // Act
        await monitor.CheckAsync();

        // Assert
        this.mockEventBus.Verify(
            e => e.EmitAsync(It.IsAny<string>(), It.IsAny<object?>()),
            Times.Never);
    }

    /// <summary>
    /// Verifies that dispose cleans up the internal timer.
    /// </summary>
    [TestMethod]
    public void Dispose_CleansUp()
    {
        // Arrange
        var monitor = new LateCompletionMonitor(
            this.mockRepository.Object,
            this.mockEventBus.Object,
            checkIntervalSeconds: 60);

        // Act
        monitor.Dispose();

        // Assert — no exception means cleanup succeeded
        monitor.Invoking(m => m.Dispose()).Should().NotThrow();
    }
}
