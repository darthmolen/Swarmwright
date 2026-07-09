using Swarmwright.Events;
using FluentAssertions;

namespace Swarmwright.Tests.Events;

[TestClass]
public class SwarmEventBusTests
{
    [TestMethod]
    public async Task EmitAsync_NotifiesAllSubscribers()
    {
        // Arrange
        var bus = new SwarmEventBus();
        var received1 = new List<string>();
        var received2 = new List<string>();

        bus.Subscribe((eventType, data) =>
        {
            received1.Add(eventType);
            return Task.CompletedTask;
        });

        bus.Subscribe((eventType, data) =>
        {
            received2.Add(eventType);
            return Task.CompletedTask;
        });

        // Act
        await bus.EmitAsync("test-event", new { Value = 42 });

        // Assert
        received1.Should().ContainSingle().Which.Should().Be("test-event");
        received2.Should().ContainSingle().Which.Should().Be("test-event");
    }

    [TestMethod]
    public void EmitSync_NotifiesAllSubscribers()
    {
        // Arrange
        var bus = new SwarmEventBus();
        var received1 = new List<string>();
        var received2 = new List<string>();

        bus.Subscribe((eventType, data) =>
        {
            received1.Add(eventType);
            return Task.CompletedTask;
        });

        bus.Subscribe((eventType, data) =>
        {
            received2.Add(eventType);
            return Task.CompletedTask;
        });

        // Act
        bus.EmitSync("sync-event", new { Value = 99 });

        // Assert - allow time for background tasks
        Thread.Sleep(200);
        received1.Should().ContainSingle().Which.Should().Be("sync-event");
        received2.Should().ContainSingle().Which.Should().Be("sync-event");
    }

    [TestMethod]
    public async Task Subscribe_ReturnsDisposable_Unsubscribe()
    {
        // Arrange
        var bus = new SwarmEventBus();
        var received = new List<string>();

        var subscription = bus.Subscribe((eventType, data) =>
        {
            received.Add(eventType);
            return Task.CompletedTask;
        });

        // Act
        subscription.Dispose();
        await bus.EmitAsync("after-dispose");

        // Assert
        received.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Emit_IsolatesSubscriberExceptions()
    {
        // Arrange
        var bus = new SwarmEventBus();
        var secondReceived = new List<string>();

        bus.Subscribe((eventType, data) =>
        {
            throw new InvalidOperationException("Subscriber failure");
        });

        bus.Subscribe((eventType, data) =>
        {
            secondReceived.Add(eventType);
            return Task.CompletedTask;
        });

        // Act
        await bus.EmitAsync("error-event");

        // Assert
        secondReceived.Should().ContainSingle().Which.Should().Be("error-event");
    }
}
