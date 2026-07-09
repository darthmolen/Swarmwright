namespace Swarmwright.Events;

/// <summary>
/// In-memory publish-subscribe event bus for swarm coordination.
/// </summary>
public class SwarmEventBus : ISwarmEventBus
{
    private readonly List<Func<string, object?, Task>> handlers = [];
    private readonly Lock syncLock = new();

    /// <inheritdoc/>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Intentionally isolating subscriber exceptions to prevent one failing subscriber from blocking others.")]
    public async Task EmitAsync(string eventType, object? data = null)
    {
        Func<string, object?, Task>[] snapshot;
        lock (this.syncLock)
        {
            snapshot = [.. this.handlers];
        }

        foreach (var handler in snapshot)
        {
            try
            {
                await handler(eventType, data).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Isolate subscriber exceptions so other subscribers still receive the event.
            }
        }
    }

    /// <inheritdoc/>
    public void EmitSync(string eventType, object? data = null)
    {
        Task.Run(() => this.EmitAsync(eventType, data));
    }

    /// <inheritdoc/>
    public IDisposable Subscribe(Func<string, object?, Task> handler)
    {
        lock (this.syncLock)
        {
            this.handlers.Add(handler);
        }

        return new Subscription(this, handler);
    }

    private void Unsubscribe(Func<string, object?, Task> handler)
    {
        lock (this.syncLock)
        {
            this.handlers.Remove(handler);
        }
    }

    /// <summary>
    /// Represents a subscription that can be disposed to unsubscribe.
    /// </summary>
    private sealed class Subscription : IDisposable
    {
        private readonly SwarmEventBus bus;
        private readonly Func<string, object?, Task> handler;

        /// <summary>
        /// Initializes a new instance of the <see cref="Subscription"/> class.
        /// </summary>
        /// <param name="bus">The event bus to unsubscribe from.</param>
        /// <param name="handler">The handler to remove on disposal.</param>
        public Subscription(SwarmEventBus bus, Func<string, object?, Task> handler)
        {
            this.bus = bus;
            this.handler = handler;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            this.bus.Unsubscribe(this.handler);
        }
    }
}
