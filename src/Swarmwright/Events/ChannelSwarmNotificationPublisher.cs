using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Swarmwright.Events;

/// <summary>
/// Default <see cref="ISwarmNotificationPublisher"/> that writes notifications to a bounded
/// in-process channel drained by <see cref="SwarmNotificationBackgroundService"/>. The dispatch
/// closure resolves <see cref="ISwarmNotificationHandler{TNotification}"/> instances from the
/// per-notification scope and invokes each under its own try/catch so one handler's failure does
/// not affect the others.
/// </summary>
internal sealed class ChannelSwarmNotificationPublisher : ISwarmNotificationPublisher
{
    private readonly ChannelWriter<SwarmNotificationEnvelope> writer;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelSwarmNotificationPublisher"/> class.
    /// </summary>
    /// <param name="writer">The channel writer notifications are enqueued to.</param>
    public ChannelSwarmNotificationPublisher(ChannelWriter<SwarmNotificationEnvelope> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        this.writer = writer;
    }

    /// <inheritdoc/>
    public ValueTask PublishAsync<TNotification>(TNotification notification, CancellationToken cancellationToken)
        where TNotification : notnull
    {
        var envelope = new SwarmNotificationEnvelope
        {
            NotificationType = typeof(TNotification).Name,
            DispatchAsync = async (serviceProvider, ct) =>
            {
                var logger = serviceProvider.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("Swarmwright.Events.SwarmNotifications");

                foreach (var handler in serviceProvider.GetServices<ISwarmNotificationHandler<TNotification>>())
                {
                    try
                    {
                        await handler.HandleAsync(notification, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException and not StackOverflowException)
                    {
                        SwarmNotificationLog.HandlerFailed(logger, handler.GetType().Name, typeof(TNotification).Name, ex);
                    }
                }
            },
        };

        return this.writer.WriteAsync(envelope, cancellationToken);
    }
}
