namespace Swarmwright.Events;

/// <summary>
/// Publishes a swarm notification to the in-process background pipeline. The publish call returns
/// as soon as the notification is enqueued (bounded channel, back-pressure on a full queue); the
/// registered <see cref="ISwarmNotificationHandler{TNotification}"/> handlers run off-thread in
/// <c>SwarmNotificationBackgroundService</c>. Replaces the CSAT Mediate publish seam.
/// </summary>
public interface ISwarmNotificationPublisher
{
    /// <summary>
    /// Enqueues a notification for background dispatch to its registered handlers.
    /// </summary>
    /// <typeparam name="TNotification">The notification type.</typeparam>
    /// <param name="notification">The notification instance.</param>
    /// <param name="cancellationToken">A cancellation token observed while enqueueing.</param>
    /// <returns>A task that completes once the notification is enqueued.</returns>
    public ValueTask PublishAsync<TNotification>(TNotification notification, CancellationToken cancellationToken)
        where TNotification : notnull;
}
