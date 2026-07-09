namespace Swarmwright.Events;

/// <summary>
/// Defines a publish-subscribe event bus for swarm coordination.
/// </summary>
public interface ISwarmEventBus
{
    /// <summary>
    /// Emits an event asynchronously, notifying all subscribers.
    /// </summary>
    /// <param name="eventType">The type identifier for the event.</param>
    /// <param name="data">Optional event data payload.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public Task EmitAsync(string eventType, object? data = null);

    /// <summary>
    /// Emits an event synchronously using fire-and-forget semantics.
    /// </summary>
    /// <param name="eventType">The type identifier for the event.</param>
    /// <param name="data">Optional event data payload.</param>
    public void EmitSync(string eventType, object? data = null);

    /// <summary>
    /// Subscribes a handler to all events on the bus.
    /// </summary>
    /// <param name="handler">The async handler invoked for each event.</param>
    /// <returns>An <see cref="IDisposable"/> that removes the subscription when disposed.</returns>
    public IDisposable Subscribe(Func<string, object?, Task> handler);
}
