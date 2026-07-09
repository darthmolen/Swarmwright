using System.Collections.Concurrent;
using System.Threading.Channels;
using Swarmwright.Configuration;
using Swarmwright.Database.Models;
using Swarmwright.Database.Repositories;
using Swarmwright.Events;
using Swarmwright.Events.AgUI;
using Swarmwright.Hosting;
using Swarmwright.Models.Enums;
using Swarmwright.Orchestration;
using Swarmwright.Tools;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Swarmwright.Tests.Hosting;

/// <summary>
/// Verifies that <see cref="SwarmDispatcherService"/> populates the scoped
/// <see cref="ISwarmRunContext"/> holder before building the orchestrator, so a
/// custom tool provider resolved from the per-swarm scope — whether registered
/// scoped or transient — observes the execution's SwarmId / WorkDirectory /
/// Context.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SwarmDispatcherRunContextTests
{
    /// <summary>
    /// A scoped custom tool provider sees the populated run context.
    /// </summary>
    [TestMethod]
    public async Task ScopedProvider_ObservesPopulatedRunContext()
    {
        await this.RunProviderObservationAsync(ServiceLifetime.Scoped);
    }

    /// <summary>
    /// A transient custom tool provider (the discovery default) also sees the
    /// populated run context because both lifetimes resolve the holder from the
    /// per-swarm scope.
    /// </summary>
    [TestMethod]
    public async Task TransientProvider_ObservesPopulatedRunContext()
    {
        await this.RunProviderObservationAsync(ServiceLifetime.Transient);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Used with this. qualifier per SA1101.")]
    private async Task RunProviderObservationAsync(ServiceLifetime lifetime)
    {
        // Arrange — a real DI scope carrying the services RunSwarmAsync resolves,
        // plus the scoped run-context holder and an observing custom tool provider.
        var observed = new ObservedContext();
        IServiceCollection services = new ServiceCollection();
        services.AddSingleton<ISwarmEventBus>(new SwarmEventBus());
        var repo = new Mock<ISwarmRepository>();
        repo.Setup(r => r.GetSwarmAsync(It.IsAny<Guid>())).ReturnsAsync((SwarmEntity?)null);
        repo.Setup(r => r.CreateSwarmAsync(It.IsAny<SwarmEntity>())).Returns(Task.CompletedTask);
        services.AddSingleton(repo.Object);
        services.AddSingleton(observed);
        services.AddScoped<SwarmRunContext>();
        services.AddScoped<ISwarmRunContext>(sp => sp.GetRequiredService<SwarmRunContext>());
        services.Add(new ServiceDescriptor(typeof(ICustomToolProvider), typeof(ObservingToolProvider), lifetime));

        using var provider = services.BuildServiceProvider();

        var channel = Channel.CreateUnbounded<SwarmRequest>();
        var activeSwarms = new ConcurrentDictionary<Guid, SwarmExecution>();
        var options = Options.Create(new SwarmOptions { MaxConcurrentSwarms = 1, MaxQueuedSwarms = 1 });
        var loggerFactory = NullLoggerFactory.Instance;

        var swarmId = Guid.Parse("12340000-0000-0000-0000-000000000001");
        var workDir = Path.Combine(Path.GetTempPath(), swarmId.ToString());
        using var execution = new SwarmExecution
        {
            SwarmId = swarmId,
            Goal = "observe context",
            Cts = new CancellationTokenSource(),
            EventBus = new SwarmEventBus(),
            AgUiAdapter = new SwarmEventAdapter(),
            WorkDirectory = workDir,
            Context = new Dictionary<string, string> { ["sourceRoot"] = "/clones/pr-8" },
        };
        activeSwarms[swarmId] = execution;

        using var service = new ContextObservingDispatcher(
            channel.Reader,
            activeSwarms,
            new SingleScopeFactory(provider),
            options,
            loggerFactory);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Act
        await service.StartAsync(cts.Token);
        await channel.Writer.WriteAsync(new SwarmRequest(swarmId, execution.Goal, null), cts.Token);
        await observed.Observed.Task.WaitAsync(cts.Token);
        channel.Writer.Complete();
        await service.StopAsync(cts.Token);

        // Assert — the provider saw the execution's values at BuildOrchestrator time.
        observed.SwarmId.Should().Be(swarmId);
        observed.WorkDirectory.Should().Be(workDir);
        observed.Context.Should().ContainKey("sourceRoot")
            .WhoseValue.Should().Be("/clones/pr-8");
    }

    /// <summary>Captures what an injected provider observed about the run context.</summary>
    private sealed class ObservedContext
    {
        public TaskCompletionSource Observed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Guid SwarmId { get; set; }

        public string WorkDirectory { get; set; } = string.Empty;

        public IReadOnlyDictionary<string, string> Context { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>
    /// Custom tool provider that records the injected <see cref="ISwarmRunContext"/>
    /// values when it is constructed.
    /// </summary>
    private sealed class ObservingToolProvider : ICustomToolProvider
    {
        public ObservingToolProvider(ISwarmRunContext runContext, ObservedContext observed)
        {
            observed.SwarmId = runContext.SwarmId;
            observed.WorkDirectory = runContext.WorkDirectory;
            observed.Context = runContext.Context;
            observed.Observed.TrySetResult();
        }

        public IReadOnlyList<AITool> GetTools() => [];
    }

    /// <summary>
    /// Dispatcher test double whose <see cref="BuildOrchestrator"/> resolves the
    /// scope's custom tool providers (forcing construction so they read the holder)
    /// then returns a no-op orchestrator.
    /// </summary>
    private sealed class ContextObservingDispatcher : SwarmDispatcherService
    {
        public ContextObservingDispatcher(
            ChannelReader<SwarmRequest> channelReader,
            ConcurrentDictionary<Guid, SwarmExecution> activeSwarms,
            IServiceScopeFactory scopeFactory,
            IOptions<SwarmOptions> options,
            Microsoft.Extensions.Logging.ILoggerFactory loggerFactory)
            : base(channelReader, activeSwarms, scopeFactory, new SwarmOrchestratorFactory(options, loggerFactory), Mock.Of<ISwarmObservationSink>(), options, loggerFactory)
        {
        }

        protected internal override ISwarmOrchestrator BuildOrchestrator(IServiceScope scope, SwarmExecution execution)
        {
            // Mirror the factory: resolve providers so they construct and read the holder.
            _ = scope.ServiceProvider.GetServices<ICustomToolProvider>().ToList();
            return new NoOpOrchestrator();
        }
    }

    /// <summary>Hands out a single shared scope so the test can register services on it.</summary>
    private sealed class SingleScopeFactory : IServiceScopeFactory
    {
        private readonly IServiceProvider rootProvider;

        public SingleScopeFactory(IServiceProvider rootProvider)
        {
            this.rootProvider = rootProvider;
        }

        public IServiceScope CreateScope() => this.rootProvider.CreateScope();
    }

    /// <summary>No-op orchestrator returned by the test dispatcher.</summary>
    private sealed class NoOpOrchestrator : ISwarmOrchestrator
    {
        public Guid SwarmId { get; private set; }

        public SwarmInstanceState Phase { get; private set; }

        public bool IsCancelled { get; private set; }

        public Task<string> RunAsync(Guid swarmId, string goal, CancellationToken cancellationToken = default)
        {
            this.SwarmId = swarmId;
            return Task.FromResult("done");
        }

        public Task CancelAsync()
        {
            this.IsCancelled = true;
            return Task.CompletedTask;
        }

        public void SignalContinue()
        {
        }

        public void SignalSkip()
        {
        }
    }
}
