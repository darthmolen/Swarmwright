using Swarmwright.Database;
using Swarmwright.Events;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Swarmwright.Tests.Events;

/// <summary>
/// Unit tests for <see cref="SwarmEventLogger"/>.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SwarmEventLoggerTests
{
    /// <summary>
    /// Verifies that the logger records events published on the event bus.
    /// </summary>
    [TestMethod]
    public async Task OnEvent_RecordsEvent()
    {
        // Arrange
        Func<string, object?, Task>? capturedHandler = null;
        var mockBus = new Mock<ISwarmEventBus>();
        mockBus
            .Setup(b => b.Subscribe(It.IsAny<Func<string, object?, Task>>()))
            .Callback<Func<string, object?, Task>>(h => capturedHandler = h)
            .Returns(Mock.Of<IDisposable>());

        using var logger = new SwarmEventLogger(mockBus.Object);

        // Act — simulate an event
        capturedHandler.Should().NotBeNull("Subscribe should have been called");
        await capturedHandler!("task.completed", new { TaskId = "t-1" });

        // Assert
        var events = logger.GetEvents();
        events.Should().HaveCount(1);
        events[0].EventType.Should().Be("task.completed");
        events[0].Data.Should().NotBeNull();
    }

    /// <summary>
    /// Verifies that exceptions in event handling do not propagate.
    /// </summary>
    [TestMethod]
    public async Task OnEvent_HandlesExceptionsGracefully()
    {
        // Arrange
        Func<string, object?, Task>? capturedHandler = null;
        var mockBus = new Mock<ISwarmEventBus>();
        mockBus
            .Setup(b => b.Subscribe(It.IsAny<Func<string, object?, Task>>()))
            .Callback<Func<string, object?, Task>>(h => capturedHandler = h)
            .Returns(Mock.Of<IDisposable>());

        // Use a callback that will be invoked by the logger to simulate failure
        using var logger = new SwarmEventLogger(mockBus.Object);
        capturedHandler.Should().NotBeNull();

        // Act — pass null data to exercise edge case; should not throw
        Func<Task> act = () => capturedHandler!(null!, null);
        await act.Should().NotThrowAsync();

        // Assert — the event should still be recorded
        var events = logger.GetEvents();
        events.Should().HaveCount(1);
    }

    /// <summary>
    /// Verifies that disposing the logger unsubscribes from the event bus.
    /// </summary>
    [TestMethod]
    public void Dispose_Unsubscribes()
    {
        // Arrange
        var mockSubscription = new Mock<IDisposable>();
        var mockBus = new Mock<ISwarmEventBus>();
        mockBus
            .Setup(b => b.Subscribe(It.IsAny<Func<string, object?, Task>>()))
            .Returns(mockSubscription.Object);

        var logger = new SwarmEventLogger(mockBus.Object);

        // Act
        logger.Dispose();

        // Assert
        mockSubscription.Verify(s => s.Dispose(), Times.Once);
    }

    /// <summary>
    /// Verifies that when a database context factory is provided, emitted
    /// events are persisted as <c>EventEntity</c> rows with the correct
    /// <c>SwarmId</c>, <c>EventType</c>, and non-empty <c>DataJson</c>.
    /// </summary>
    [TestMethod]
    public async Task SwarmEventLogger_WithDbContext_PersistsEventToDatabase()
    {
        // Arrange
        var swarmId = Guid.NewGuid();
        Func<string, object?, Task>? capturedHandler = null;
        var mockBus = new Mock<ISwarmEventBus>();
        mockBus
            .Setup(b => b.Subscribe(It.IsAny<Func<string, object?, Task>>()))
            .Callback<Func<string, object?, Task>>(h => capturedHandler = h)
            .Returns(Mock.Of<IDisposable>());

        var dbOptions = new DbContextOptionsBuilder<SwarmDbContext>()
            .UseInMemoryDatabase("EventLogger_Persist_" + Guid.NewGuid())
            .Options;

        var mockFactory = new Mock<IDbContextFactory<SwarmDbContext>>();
        mockFactory
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new SwarmDbContext(dbOptions));

        using var logger = new SwarmEventLogger(
            mockBus.Object,
            swarmId,
            mockFactory.Object,
            NullLogger<SwarmEventLogger>.Instance);

        capturedHandler.Should().NotBeNull("Subscribe should have been called");

        // Act
        await capturedHandler!("task.completed", new { TaskId = "t-1" });

        // Assert — verify the row exists in the database
        await using var verifyContext = new SwarmDbContext(dbOptions);
        var rows = await verifyContext.Events.ToListAsync();
        rows.Should().HaveCount(1);
        rows[0].SwarmId.Should().Be(swarmId);
        rows[0].EventType.Should().Be("task.completed");
        rows[0].DataJson.Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// Verifies that when the database write throws, no exception propagates
    /// and the in-memory event list still contains the event.
    /// </summary>
    [TestMethod]
    public async Task SwarmEventLogger_WhenDbWriteFails_DoesNotThrow()
    {
        // Arrange
        var swarmId = Guid.NewGuid();
        Func<string, object?, Task>? capturedHandler = null;
        var mockBus = new Mock<ISwarmEventBus>();
        mockBus
            .Setup(b => b.Subscribe(It.IsAny<Func<string, object?, Task>>()))
            .Callback<Func<string, object?, Task>>(h => capturedHandler = h)
            .Returns(Mock.Of<IDisposable>());

        var mockFactory = new Mock<IDbContextFactory<SwarmDbContext>>();
        mockFactory
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("DB unavailable"));

        using var eventLogger = new SwarmEventLogger(
            mockBus.Object,
            swarmId,
            mockFactory.Object,
            NullLogger<SwarmEventLogger>.Instance);

        capturedHandler.Should().NotBeNull("Subscribe should have been called");

        // Act — should not throw
        Func<Task> act = () => capturedHandler!("worker.started", new { Worker = "w-1" });
        await act.Should().NotThrowAsync();

        // Assert — in-memory path still works
        var events = eventLogger.GetEvents();
        events.Should().HaveCount(1);
        events[0].EventType.Should().Be("worker.started");
    }
}
