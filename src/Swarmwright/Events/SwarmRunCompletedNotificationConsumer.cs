using Microsoft.Extensions.Logging;
using Swarmwright.Archival;

namespace Swarmwright.Events;

/// <summary>
/// Background handler that delegates a completed-run archive to
/// <see cref="ISwarmRunArchiver"/>. Best-effort: a failure is logged and
/// swallowed so archival never fails the run. The notification pipeline runs it off the
/// dispatcher's terminal-signal thread in <see cref="SwarmNotificationBackgroundService"/>.
/// </summary>
public sealed partial class SwarmRunCompletedNotificationConsumer
    : ISwarmNotificationHandler<SwarmRunCompletedNotification>
{
    private readonly ISwarmRunArchiver archiver;
    private readonly ILogger<SwarmRunCompletedNotificationConsumer> logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SwarmRunCompletedNotificationConsumer"/> class.
    /// </summary>
    /// <param name="archiver">The run archiver to delegate to.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    public SwarmRunCompletedNotificationConsumer(
        ISwarmRunArchiver archiver,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(archiver);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        this.archiver = archiver;
        this.logger = loggerFactory.CreateLogger<SwarmRunCompletedNotificationConsumer>();
    }

    /// <inheritdoc/>
    public async Task HandleAsync(
        SwarmRunCompletedNotification notification,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);

        var context = new SwarmRunArchiveContext(
            notification.SwarmId,
            notification.WorkDirectory,
            notification.Goal,
            notification.TemplateKey,
            notification.CreatedUtc,
            notification.CompletedUtc,
            notification.FinalState,
            notification.FailureReason,
            notification.Context);

        try
        {
            this.LogArchiveStarting(notification.SwarmId, notification.WorkDirectory);
            await this.archiver.ArchiveAsync(context, cancellationToken).ConfigureAwait(false);
            this.LogArchiveCompleted(notification.SwarmId);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            this.LogArchiveFailed(notification.SwarmId, ex);
        }
    }

    [LoggerMessage(LogLevel.Information, "Starting archival for swarm {SwarmId} from work directory {WorkDirectory}.")]
    private partial void LogArchiveStarting(Guid swarmId, string workDirectory);

    [LoggerMessage(LogLevel.Information, "Completed archival for swarm {SwarmId}.")]
    private partial void LogArchiveCompleted(Guid swarmId);

    [LoggerMessage(LogLevel.Error, "Archival failed for swarm {SwarmId}; the run is unaffected.")]
    private partial void LogArchiveFailed(Guid swarmId, Exception exception);
}
