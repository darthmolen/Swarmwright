using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Swarmwright.Events;

/// <summary>
/// Drains the swarm notification channel and dispatches each notification to its registered
/// handlers in a fresh DI scope. On graceful shutdown it best-effort drains notifications already
/// queued. Each notification is dispatched under its own try/catch so a faulty handler never tears
/// down the drain loop. Replaces the CSAT Mediate background consumer host.
/// </summary>
internal sealed partial class SwarmNotificationBackgroundService : BackgroundService
{
    private readonly ChannelReader<SwarmNotificationEnvelope> reader;
    private readonly IServiceScopeFactory scopeFactory;
    private readonly ILogger<SwarmNotificationBackgroundService> logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SwarmNotificationBackgroundService"/> class.
    /// </summary>
    /// <param name="reader">The channel reader to drain.</param>
    /// <param name="scopeFactory">The scope factory used to resolve scoped handlers per notification.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    public SwarmNotificationBackgroundService(
        ChannelReader<SwarmNotificationEnvelope> reader,
        IServiceScopeFactory scopeFactory,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        this.reader = reader;
        this.scopeFactory = scopeFactory;
        this.logger = loggerFactory.CreateLogger<SwarmNotificationBackgroundService>();
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var envelope in this.reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await this.DispatchAsync(envelope, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown requested; fall through to drain whatever is already queued.
        }

        while (this.reader.TryRead(out var envelope))
        {
            await this.DispatchAsync(envelope, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task DispatchAsync(SwarmNotificationEnvelope envelope, CancellationToken cancellationToken)
    {
        using var scope = this.scopeFactory.CreateScope();
        try
        {
            await envelope.DispatchAsync(scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException and not StackOverflowException)
        {
            this.LogDispatchFailed(envelope.NotificationType, ex);
        }
    }

    [LoggerMessage(LogLevel.Error, "Dispatching swarm notification {NotificationType} failed.")]
    private partial void LogDispatchFailed(string notificationType, Exception exception);
}
