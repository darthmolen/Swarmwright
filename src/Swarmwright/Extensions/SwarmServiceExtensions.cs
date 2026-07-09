using System.Collections.Concurrent;
using System.Reflection;
using System.Threading.Channels;
using Azure.Storage.Blobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Swarmwright.Archival;
using Swarmwright.Configuration;
using Swarmwright.Core;
using Swarmwright.Database;
using Swarmwright.Database.Repositories;
using Swarmwright.Events;
using Swarmwright.Hosting;
using Swarmwright.Hosting.StateMachine;
using Swarmwright.Recommendation;
using Swarmwright.Templates;
using Swarmwright.Tools;

namespace Swarmwright.Extensions;

/// <summary>
/// Extension methods for registering Swarm domain services.
/// </summary>
public static class SwarmServiceExtensions
{
    /// <summary>
    /// Registers Swarm domain services (database, repositories, core services, hosting).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration.</param>
    /// <param name="hostingEnvironment">The hosting environment.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddSwarmDomain(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment hostingEnvironment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(hostingEnvironment);

        // 1. Register configuration
        services.Configure<SwarmOptions>(configuration.GetSection(SwarmOptions.SectionName));

        // 2. Register database context (Postgres/SQLite/InMemory based on config)
        var dbProvider = configuration["Swarm:Database:Provider"] ?? "InMemory";
        var connectionString = configuration["Swarm:Database:ConnectionString"]
            ?? "Data Source=swarm.db";

        // Register a DbContextFactory (not a scoped DbContext). Every repository call
        // creates a fresh short-lived context from the factory, so concurrent callers —
        // including parallel swarm workers — never share a single DbContext instance and
        // cannot trip EF Core's ConcurrencyDetector (see Bug E regression tests).
        if (dbProvider.Equals("PostgreSQL", StringComparison.OrdinalIgnoreCase))
        {
            services.AddDbContextFactory<SwarmDbContext>(options =>
                options.UseNpgsql(
                    connectionString,
                    npgsql => npgsql.MigrationsAssembly("Swarmwright.Database.Postgres")));
        }
        else if (dbProvider.Equals("SQLite", StringComparison.OrdinalIgnoreCase))
        {
            services.AddDbContextFactory<SwarmDbContext>(options =>
                options.UseSqlite(
                    connectionString,
                    sqlite => sqlite.MigrationsAssembly("Swarmwright.Database.Sqlite")));
        }
        else if (dbProvider.Equals("InMemory", StringComparison.OrdinalIgnoreCase))
        {
            services.AddDbContextFactory<SwarmDbContext>(options =>
                options.UseInMemoryDatabase("SwarmTest"));
        }
        else
        {
            throw new InvalidOperationException(
                $"Unsupported database provider: {dbProvider}. Use 'PostgreSQL', 'SQLite', or 'InMemory'.");
        }

        // 2b. Register migration runner for Development/Testing environments
        if (hostingEnvironment.EnvironmentName is "Development" or "Testing")
        {
            services.AddHostedService<SwarmMigrationRunner>();
        }

        // 3. Register repositories. Repository is stateless and delegates to
        // IDbContextFactory<SwarmDbContext> for per-call contexts, so singleton
        // lifetime is safe and lets the rehydrator (also singleton) inject it
        // without a captive-dependency violation.
        services.AddSingleton<ISwarmRepository, SwarmRepository>();

        // 3b. Register state-machine transition service. Same rationale —
        // stateless + factory-backed — so singleton is safe.
        services.AddSingleton<IStateTransitionService, StateTransitionService>();

        // 3b'. Register the observation sink that backs ISwarmManager's
        // WaitForCompletionAsync / WaitForStateChangeAsync / SwarmCompleted
        // surface. Singleton: holds in-memory TCS dictionaries shared by the
        // dispatcher (publisher), state-transition service (publisher), and
        // manager (consumer-facing surface). Internal type — registered behind
        // its internal interface so external consumers cannot resolve it
        // directly.
        services.AddSingleton<ISwarmObservationSink, SwarmObservationSink>();

        // 3c. Register the orchestrator factory that constructs a SwarmOrchestrator
        // for a given scope + execution. Shared by SwarmDispatcherService for
        // both fresh runs and resurrected-swarm wake-ups (the manager enqueues
        // onto the dispatcher channel; the dispatcher builds the orchestrator).
        services.AddSingleton<ISwarmOrchestratorFactory, SwarmOrchestratorFactory>();

        // 3d. Register the leader-repair advisor. Wraps the shared leader
        // IChatClient + ITemplateLoader so the /smart-continue endpoint can
        // invoke the leader with the repair_plan_after_failure tool and
        // apply the result.
        services.AddSingleton<ILeaderRepairAdvisor, DefaultLeaderRepairAdvisor>();

        // 3f. Register the intervention handler — the logic behind /continue,
        // /smart-continue, /skip, /cancel, /lock. Scoped to match the
        // DbContext lifetime owned by ISwarmRepository.
        services.AddScoped<ISwarmInterventionHandler, SwarmInterventionHandler>();

        // 3f'. Register the per-swarm run-context holder. Scoped so each
        // per-swarm DI scope gets its own instance; the dispatcher populates
        // the concrete SwarmRunContext before BuildOrchestrator, and the
        // public ISwarmRunContext forwards to the same instance so scoped (or
        // transient) custom tool providers resolved from that scope observe
        // the populated values.
        services.AddScoped<SwarmRunContext>();
        services.AddScoped<ISwarmRunContext>(sp => sp.GetRequiredService<SwarmRunContext>());

        // 3g. Register the server-side recovery recommendation provider.
        // Pure-function over (swarm state, tasks, retry budget) — recomputed
        // at every read; no caching. Singleton lifetime matches ISwarmRepository.
        services.AddSingleton<IRecommendedSwarmContinueProvider, DeterministicSwarmContinueProvider>();

        // 4. Register template loader.
        // Templates are shipped via NuGet content files into the app's bin folder
        // (see Swarmwright.Templates.*). Resolve a relative configured
        // path against AppContext.BaseDirectory rather than Environment.CurrentDirectory
        // so the loader finds them regardless of where the host was launched from.
        var configuredTemplatesDir = configuration["Swarm:TemplatesDirectory"] ?? "templates";
        var templatesDir = Path.IsPathRooted(configuredTemplatesDir)
            ? configuredTemplatesDir
            : Path.Combine(AppContext.BaseDirectory, configuredTemplatesDir);
        services.AddSingleton<ITemplateLoader>(sp =>
            new TemplateLoader(templatesDir, sp.GetService<ILoggerFactory>()?.CreateLogger<TemplateLoader>()));

        // 5. Register core services as transient (new per swarm instance)
        services.AddTransient<IInboxSystem, InboxSystem>();
        services.AddTransient<ITeamRegistry, TeamRegistry>();
        services.AddSingleton<ISwarmEventBus, SwarmEventBus>();

        // 6. Register channel, shared dictionary, SwarmManager, and dispatcher
        var swarmOptions = configuration.GetSection(SwarmOptions.SectionName).Get<SwarmOptions>() ?? new SwarmOptions();

        var channel = Channel.CreateBounded<SwarmRequest>(swarmOptions.MaxQueuedSwarms);
        services.AddSingleton(channel.Reader);
        services.AddSingleton(channel.Writer);
        services.AddSingleton<ConcurrentDictionary<Guid, SwarmExecution>>();
        services.AddSingleton<ISwarmManager, SwarmManager>();

        // 6a. Event-emission broker. Singleton; resolves a swarm's AG-UI
        // adapter via ISwarmManager.GetSwarm at emit time so the state
        // transition service can fire SWARM_TASK_UPDATED without threading
        // the adapter through every caller.
        services.AddSingleton<ISwarmEmissionBroker, SwarmEmissionBroker>();

        services.AddHostedService<SwarmDispatcherService>();

        // 7. Register named HttpClient used by the default web_fetch tool
        services.AddHttpClient("swarm-default-tools", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Add("User-Agent", "Swarmwright-Swarm-Agent/1.0");
        });

        // 9. Register swarm-run archival. The archiver is injected into the
        // background notification consumer (not the dispatcher). When archival
        // is disabled, a no-op archiver is registered so resolution always
        // succeeds. The consumer is discovered by the mediator so it runs on
        // the Background schedule the notification declares.
        services.AddSwarmRunArchival(configuration, hostingEnvironment);

        return services;
    }

    /// <summary>
    /// Registers swarm-run archival: the <see cref="ISwarmRunArchiver"/> (no-op
    /// when disabled, Blob otherwise) and the background notification consumer
    /// that delegates to it. The consumer is discovered by the mediator so it
    /// runs on the Background schedule the notification declares.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration.</param>
    /// <param name="hostingEnvironment">The hosting environment.</param>
    /// <returns>The service collection for chaining.</returns>
    private static IServiceCollection AddSwarmRunArchival(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment hostingEnvironment)
    {
        var archivalSection = configuration.GetSection($"{SwarmOptions.SectionName}:Archival");
        services.Configure<SwarmArchivalOptions>(archivalSection);
        var archivalOptions = archivalSection.Get<SwarmArchivalOptions>() ?? new SwarmArchivalOptions();

        if (archivalOptions.Enabled && archivalOptions.ContainerUri is not null)
        {
            var containerUri = archivalOptions.ContainerUri;
            var environmentName = hostingEnvironment.EnvironmentName;
            services.AddSingleton<ISwarmRunArchiver>(sp =>
            {
                var credential = SwarmArchiverCredentialFactory.Create(environmentName, archivalOptions);
                var containerClient = new BlobContainerClient(containerUri, credential);
                var sink = new BlobArchiveSink(containerClient);
                var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
                return new BlobSwarmRunArchiver(sink, loggerFactory);
            });
        }
        else
        {
            services.AddSingleton<ISwarmRunArchiver, NoOpSwarmRunArchiver>();
        }

        // In-process notification pipeline. A bounded channel decouples
        // the dispatcher's terminal-signal path from off-thread archival; the background service
        // drains it and dispatches to registered handlers. FullMode.Wait applies back-pressure
        // rather than dropping completion notifications.
        var notificationChannel = Channel.CreateBounded<SwarmNotificationEnvelope>(
            new BoundedChannelOptions(1024)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
            });
        services.TryAddSingleton(notificationChannel.Reader);
        services.TryAddSingleton(notificationChannel.Writer);
        services.TryAddSingleton<ISwarmNotificationPublisher, ChannelSwarmNotificationPublisher>();
        services.AddHostedService<SwarmNotificationBackgroundService>();

        // Register the background run-completed archival handler. TryAddEnumerable keeps this
        // idempotent across repeated AddSwarm* calls.
        services.TryAddEnumerable(
            ServiceDescriptor.Transient<ISwarmNotificationHandler<SwarmRunCompletedNotification>, SwarmRunCompletedNotificationConsumer>());

        return services;
    }

    /// <summary>
    /// Scans loaded assemblies for concrete <see cref="ICustomToolProvider"/> implementations
    /// and registers each as a <see cref="ICustomToolProvider"/> service. The DI lifetime is
    /// taken from a <see cref="SwarmToolProviderAttribute"/> on the implementation class, or
    /// defaults to <see cref="ServiceLifetime.Transient"/> when the attribute is absent.
    /// Types the consumer has already registered manually are skipped (manual registration wins).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection DiscoverCustomToolProviders(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var providerType = typeof(ICustomToolProvider);

        var discovered = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(SafeGetTypes)
            .Where(t => t.IsClass
                && !t.IsAbstract
                && !t.IsGenericTypeDefinition
                && providerType.IsAssignableFrom(t));

        foreach (var type in discovered)
        {
            // Skip if consumer already registered this type manually (any lifetime).
            if (services.Any(sd => sd.ServiceType == providerType && sd.ImplementationType == type))
            {
                continue;
            }

            var lifetime = type.GetCustomAttribute<SwarmToolProviderAttribute>()?.Lifetime
                ?? ServiceLifetime.Transient;

            services.Add(new ServiceDescriptor(providerType, type, lifetime));
        }

        return services;
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null).Cast<Type>();
        }
    }
}
