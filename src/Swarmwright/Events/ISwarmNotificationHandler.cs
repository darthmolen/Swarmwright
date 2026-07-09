namespace Swarmwright.Events;

/// <summary>
/// Handles a swarm notification of type <typeparamref name="TNotification"/> off the dispatcher
/// thread. Register handlers in DI as an enumerable; the background pipeline invokes every
/// registered handler for a published notification, isolating each from the others' failures.
/// </summary>
/// <typeparam name="TNotification">The notification type handled.</typeparam>
public interface ISwarmNotificationHandler<in TNotification>
{
    /// <summary>
    /// Handles the published notification.
    /// </summary>
    /// <param name="notification">The notification instance.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task that completes when handling finishes.</returns>
    public Task HandleAsync(TNotification notification, CancellationToken cancellationToken);
}
