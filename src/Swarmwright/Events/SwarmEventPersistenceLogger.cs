using Microsoft.Extensions.Logging;

namespace Swarmwright.Events;

/// <summary>
/// High-performance <see cref="LoggerMessage"/> helper for swarm event
/// persistence failures. Invoked when writing an <c>EventEntity</c> to
/// the database throws so that the in-memory event path is unaffected.
/// </summary>
internal static partial class SwarmEventPersistenceLogger
{
    /// <summary>
    /// Logs that persisting a swarm event to the database failed.
    /// </summary>
    /// <param name="logger">The logger used to emit the warning.</param>
    /// <param name="exception">The exception that caused the persistence failure.</param>
    [LoggerMessage(
        EventId = 132,
        Level = LogLevel.Warning,
        Message = "Failed to persist swarm event to the database; in-memory event log is unaffected.")]
    public static partial void LogPersistenceFailed(ILogger logger, Exception exception);
}
