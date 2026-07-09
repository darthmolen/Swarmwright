using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Swarmwright.Database;

/// <summary>
/// Hosted service that runs EF Core migrations on startup in Development/Testing environments.
/// </summary>
internal sealed partial class SwarmMigrationRunner : IHostedService
{
    private readonly IServiceProvider serviceProvider;
    private readonly ILogger<SwarmMigrationRunner> logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SwarmMigrationRunner"/> class.
    /// </summary>
    /// <param name="serviceProvider">The service provider.</param>
    /// <param name="logger">The logger instance.</param>
    public SwarmMigrationRunner(
        IServiceProvider serviceProvider,
        ILogger<SwarmMigrationRunner> logger)
    {
        this.serviceProvider = serviceProvider;
        this.logger = logger;
    }

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        this.LogRunningMigrations();

        var factory = this.serviceProvider.GetRequiredService<IDbContextFactory<SwarmDbContext>>();
        await using var context = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // The InMemory provider has no migrations and creates its store on first use; calling
        // MigrateAsync against it throws. Skip non-relational providers so an InMemory-backed
        // host (tests, demos) starts cleanly.
        if (!context.Database.IsRelational())
        {
            this.LogSkippingNonRelational();
            return;
        }

        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);

        this.LogMigrationsComplete();
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Running Swarm database migrations")]
    private partial void LogRunningMigrations();

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Information,
        Message = "Swarm database migrations completed successfully")]
    private partial void LogMigrationsComplete();

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Information,
        Message = "Skipping Swarm database migrations: the configured provider is not relational")]
    private partial void LogSkippingNonRelational();
}
