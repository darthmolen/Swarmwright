using Microsoft.Extensions.Logging;

namespace Swarmwright.Events;

/// <summary>
/// High-performance log messages for the swarm notification pipeline. Kept separate so the
/// dispatch closure in <see cref="ChannelSwarmNotificationPublisher"/> can log against a logger
/// resolved from the per-notification scope.
/// </summary>
internal static partial class SwarmNotificationLog
{
    [LoggerMessage(LogLevel.Error, "Swarm notification handler {Handler} failed for {Notification}; the run is unaffected.")]
    public static partial void HandlerFailed(ILogger logger, string handler, string notification, Exception exception);
}
