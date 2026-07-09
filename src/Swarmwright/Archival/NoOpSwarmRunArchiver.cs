using Microsoft.Extensions.Logging;

namespace Swarmwright.Archival;

/// <summary>
/// The <see cref="ISwarmRunArchiver"/> registered when archival is disabled.
/// Performs no upload — it logs the skip so a host that expected archival can
/// see why nothing was promoted.
/// </summary>
public sealed partial class NoOpSwarmRunArchiver : ISwarmRunArchiver
{
    private readonly ILogger<NoOpSwarmRunArchiver> logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="NoOpSwarmRunArchiver"/> class.
    /// </summary>
    /// <param name="loggerFactory">The logger factory.</param>
    public NoOpSwarmRunArchiver(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        this.logger = loggerFactory.CreateLogger<NoOpSwarmRunArchiver>();
    }

    /// <inheritdoc/>
    public Task ArchiveAsync(SwarmRunArchiveContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        this.LogArchivalDisabled(context.SwarmId);
        return Task.CompletedTask;
    }

    [LoggerMessage(LogLevel.Debug, "Archival disabled; skipping archive for swarm {SwarmId}.")]
    private partial void LogArchivalDisabled(Guid swarmId);
}
