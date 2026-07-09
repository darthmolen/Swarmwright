using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Swarmwright.Database;
using Swarmwright.Database.Models;

namespace Swarmwright.Events;

/// <summary>
/// Subscribes to the swarm event bus and accumulates events for later retrieval.
/// When a database context factory is provided, each event is also persisted to
/// the <c>swarm_events</c> table so that <c>GET /api/swarm/{id}/events</c> can
/// return a durable event history.
/// </summary>
public sealed class SwarmEventLogger : IDisposable
{
    private readonly List<SwarmEventRecord> events = [];
    private readonly IDisposable subscription;
    private readonly Guid swarmId;
    private readonly IDbContextFactory<SwarmDbContext>? contextFactory;
    private readonly ILogger logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SwarmEventLogger"/> class.
    /// </summary>
    /// <param name="eventBus">The event bus to subscribe to.</param>
    /// <param name="swarmId">The optional swarm identifier used when persisting events.</param>
    /// <param name="contextFactory">The optional database context factory for event persistence.</param>
    /// <param name="logger">The optional logger for diagnostics.</param>
    public SwarmEventLogger(
        ISwarmEventBus eventBus,
        Guid? swarmId = null,
        IDbContextFactory<SwarmDbContext>? contextFactory = null,
        ILogger<SwarmEventLogger>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(eventBus);
        this.swarmId = swarmId ?? Guid.Empty;
        this.contextFactory = contextFactory;
        this.logger = logger ?? NullLogger<SwarmEventLogger>.Instance;
        this.subscription = eventBus.Subscribe(this.OnEventAsync);
    }

    /// <summary>
    /// Gets a snapshot of all recorded events.
    /// </summary>
    /// <returns>A read-only list of recorded events.</returns>
    public IReadOnlyList<SwarmEventRecord> GetEvents()
    {
        return this.events.AsReadOnly();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        this.subscription.Dispose();
    }

    private async Task OnEventAsync(string eventType, object? data)
    {
        this.events.Add(new SwarmEventRecord
        {
            EventType = eventType ?? string.Empty,
            Data = data,
            Timestamp = DateTime.UtcNow,
        });

        if (this.contextFactory is not null)
        {
            try
            {
                await using var context = await this.contextFactory.CreateDbContextAsync().ConfigureAwait(false);
                context.Events.Add(new EventEntity
                {
                    SwarmId = this.swarmId,
                    EventType = eventType ?? string.Empty,
                    DataJson = JsonSerializer.Serialize(data, SwarmJsonOptions.Default),
                    CreatedAt = DateTime.UtcNow,
                });

                await context.SaveChangesAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                SwarmEventPersistenceLogger.LogPersistenceFailed(this.logger, ex);
            }
        }
    }
}
