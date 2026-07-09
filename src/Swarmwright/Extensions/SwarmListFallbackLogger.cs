using Microsoft.Extensions.Logging;

namespace Swarmwright.Extensions;

/// <summary>
/// High-performance <see cref="LoggerMessage"/> helper for the
/// <c>GET /api/swarm/</c> and <c>GET /api/swarm/{id}</c> repository fallback
/// paths. Invoked when a repository lookup throws (for example, in deployments
/// where the swarm database has not been configured) so the in-memory path
/// can still be served.
/// </summary>
internal static partial class SwarmListFallbackLogger
{
    /// <summary>
    /// Logs that the historical swarm query failed and the endpoint is falling
    /// back to the in-memory active list only.
    /// </summary>
    /// <param name="logger">The logger used to emit the warning.</param>
    /// <param name="exception">The repository exception that triggered the fallback.</param>
    [LoggerMessage(
        EventId = 130,
        Level = LogLevel.Warning,
        Message = "GET /api/swarm/ could not read historical swarms from the repository; falling back to active-only.")]
    public static partial void LogRepositoryUnavailable(ILogger logger, Exception exception);

    /// <summary>
    /// Logs that the database fallback for <c>GET /api/swarm/{id}</c> failed.
    /// The endpoint preserves the old behavior (return 404 as if the swarm did
    /// not exist) when the repository throws, so this warning is the only
    /// indication that a DB-backed hydration attempt was lost.
    /// </summary>
    /// <param name="logger">The logger used to emit the warning.</param>
    /// <param name="swarmId">The swarm identifier that was being fetched.</param>
    /// <param name="exception">The repository exception that triggered the fallback.</param>
    [LoggerMessage(
        EventId = 131,
        Level = LogLevel.Warning,
        Message = "GET /api/swarm/{SwarmId} could not read the swarm entity from the repository; returning 404.")]
    public static partial void LogMetadataRepositoryUnavailable(ILogger logger, Guid swarmId, Exception exception);
}
