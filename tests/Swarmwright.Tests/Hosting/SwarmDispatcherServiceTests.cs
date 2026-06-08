using System.Collections.Concurrent;
using System.Reflection;
using System.Threading.Channels;
using Swarmwright.Configuration;
using Swarmwright.Core;
using Swarmwright.Database.Models;
using Swarmwright.Database.Repositories;
using Swarmwright.Events;
using Swarmwright.Events.AgUI;
using Swarmwright.Hosting;
using Swarmwright.Models.Enums;
using Swarmwright.Orchestration;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Swarmwright.Tests.Hosting;

/// <summary>
/// Unit tests for <see cref="SwarmDispatcherService"/>.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SwarmDispatcherServiceTests
{
    /// <summary>
    /// Verifies that the constructor does not throw with valid dependencies.
    /// </summary>
    [TestMethod]
    public void Constructor_DoesNotThrow()
    {
        // Arrange
        var channel = Channel.CreateUnbounded<SwarmRequest>();
        var activeSwarms = new ConcurrentDictionary<Guid, SwarmExecution>();
        var scopeFactory = new Mock<IServiceScopeFactory>();
        var options = Options.Create(new SwarmOptions());
        var loggerFactory = NullLoggerFactory.Instance;
        var orchestratorFactory = new Swarmwright.Hosting.SwarmOrchestratorFactory(
            options, loggerFactory);

        // Act
        var act = () => new SwarmDispatcherService(
            channel.Reader,
            activeSwarms,
            scopeFactory.Object,
            orchestratorFactory,
            Mock.Of<ISwarmObservationSink>(),
            options,
            loggerFactory);

        // Assert
        act.Should().NotThrow();
    }

    /// <summary>
    /// Verifies that when the dispatcher pulls a swarm request off the channel and runs it,
    /// the swarm identifier it passes to <see cref="ISwarmOrchestrator.RunAsync(Guid, string, CancellationToken)"/>
    /// is exactly the id stored on the <see cref="SwarmExecution"/> the dispatcher created.
    /// This guards against the canonical-id regression where the dispatcher generated one id
    /// and the orchestrator generated another, preventing events from reaching subscribers.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSwarm_PassesExecutionIdToOrchestrator()
    {
        // Arrange — build a minimal DI scope that can resolve ISwarmEventBus,
        // which the dispatcher pulls from the scope before invoking the orchestrator.
        var channel = Channel.CreateUnbounded<SwarmRequest>();
        var activeSwarms = new ConcurrentDictionary<Guid, SwarmExecution>();
        var scopeFactory = new Mock<IServiceScopeFactory>();
        var scope = new Mock<IServiceScope>();
        var scopeProvider = new Mock<IServiceProvider>();
        scopeProvider
            .Setup(p => p.GetService(typeof(ISwarmEventBus)))
            .Returns(new SwarmEventBus());
        var mockRepo = new Mock<ISwarmRepository>();
        mockRepo.Setup(r => r.GetSwarmAsync(It.IsAny<Guid>())).ReturnsAsync((SwarmEntity?)null);
        mockRepo.Setup(r => r.CreateSwarmAsync(It.IsAny<SwarmEntity>())).Returns(Task.CompletedTask);
        scopeProvider
            .Setup(p => p.GetService(typeof(ISwarmRepository)))
            .Returns(mockRepo.Object);
        scope.SetupGet(s => s.ServiceProvider).Returns(scopeProvider.Object);
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);
        var options = Options.Create(new SwarmOptions { MaxConcurrentSwarms = 1, MaxQueuedSwarms = 1 });
        var loggerFactory = NullLoggerFactory.Instance;

        var recordingOrchestrator = new RecordingOrchestrator();

        var swarmId = Guid.Parse("77777777-6666-5555-4444-333333333333");
        var execution = new SwarmExecution
        {
            SwarmId = swarmId,
            Goal = "Canonical id goal.",
            Cts = new CancellationTokenSource(),
            EventBus = new SwarmEventBus(),
            AgUiAdapter = new SwarmEventAdapter(),
            WorkDirectory = Path.GetTempPath(),
        };
        activeSwarms[swarmId] = execution;

        using var service = new TestSwarmDispatcherService(
            channel.Reader,
            activeSwarms,
            scopeFactory.Object,
            options,
            loggerFactory,
            recordingOrchestrator);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Act — start the background service, publish one request, wait for it to run, then shut down.
        await service.StartAsync(cts.Token);
        await channel.Writer.WriteAsync(new SwarmRequest(swarmId, execution.Goal, null), cts.Token);

        // Wait for the recording orchestrator to observe the RunAsync call.
        await recordingOrchestrator.RunAsyncInvoked.Task.WaitAsync(cts.Token);

        channel.Writer.Complete();
        await service.StopAsync(cts.Token);

        // Assert
        recordingOrchestrator.ReceivedSwarmIds.Should().ContainSingle(
            "the dispatcher must invoke RunAsync exactly once for a single queued swarm.");
        recordingOrchestrator.ReceivedSwarmIds[0].Should().Be(
            swarmId,
            "the dispatcher must hand the SwarmExecution.SwarmId to ISwarmOrchestrator.RunAsync.");
    }

    /// <summary>
    /// Verifies that <see cref="SwarmDispatcherService.BuildOrchestrator"/> wires every worker's
    /// <see cref="IChatClient"/> through an <see cref="AgUIEventInterceptor"/>. This guards Bug F:
    /// previously the dispatcher handed the raw singleton client to every worker, so
    /// per-worker AG-UI tool call events were never emitted.
    /// </summary>
    [TestMethod]
    public void BuildOrchestrator_WorkerChatClientIsAgUIEventInterceptor()
    {
        // Arrange — build a real ServiceCollection scope carrying just the services
        // BuildOrchestrator asks for. No template key, so ITemplateLoader is not required.
        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(new Mock<IChatClient>().Object);
        services.AddSingleton<IInboxSystem>(new Mock<IInboxSystem>().Object);
        services.AddSingleton<ITeamRegistry>(new Mock<ITeamRegistry>().Object);
        services.AddSingleton<ISwarmRepository>(new Mock<ISwarmRepository>().Object);
        services.AddSingleton<Swarmwright.Hosting.StateMachine.IStateTransitionService>(
            new Swarmwright.Tests.Hosting.StateMachine.NoOpStateTransitionService());
        services.AddHttpClient("swarm-default-tools");

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var channel = Channel.CreateUnbounded<SwarmRequest>();
        var activeSwarms = new ConcurrentDictionary<Guid, SwarmExecution>();
        var scopeFactory = new Mock<IServiceScopeFactory>();
        var options = Options.Create(new SwarmOptions());
        var loggerFactory = NullLoggerFactory.Instance;
        var orchestratorFactory = new Swarmwright.Hosting.SwarmOrchestratorFactory(
            options, loggerFactory);

        using var service = new SwarmDispatcherService(
            channel.Reader,
            activeSwarms,
            scopeFactory.Object,
            orchestratorFactory,
            Mock.Of<ISwarmObservationSink>(),
            options,
            loggerFactory);

        using var execution = new SwarmExecution
        {
            SwarmId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            Goal = "Factory wiring test.",
            Cts = new CancellationTokenSource(),
            EventBus = new SwarmEventBus(),
            AgUiAdapter = new SwarmEventAdapter(),
            WorkDirectory = Path.GetTempPath(),
        };

        // Act — invoke BuildOrchestrator directly, then pull the worker factory out of
        // SwarmOrchestrator via reflection and invoke it with a sample agent name.
        var orchestrator = service.BuildOrchestrator(scope, execution);
        orchestrator.Should().BeOfType<SwarmOrchestrator>(
            "the production BuildOrchestrator should return a real SwarmOrchestrator.");

        var factoryField = typeof(SwarmOrchestrator).GetField(
            "workerChatClientFactory",
            BindingFlags.Instance | BindingFlags.NonPublic);
        factoryField.Should().NotBeNull(
            "SwarmOrchestrator must expose the worker chat-client factory for inspection.");

        var factory = (Func<string, IChatClient>)factoryField!.GetValue(orchestrator)!;
        factory.Should().NotBeNull();

        var workerClient = factory("test-worker");

        // Assert — the dispatcher must wrap the singleton client in an AgUIEventInterceptor
        // keyed to the worker's agent name so AG-UI tool call events flow per worker.
        workerClient.Should().BeOfType<AgUIEventInterceptor>(
            "SwarmDispatcherService.BuildOrchestrator must wrap the worker chat client in "
            + "an AgUIEventInterceptor so per-worker AG-UI tool call events are emitted.");
    }

    /// <summary>
    /// Verifies that ExecuteAsync handles graceful shutdown without throwing.
    /// </summary>
    [TestMethod]
    public async Task ExecuteAsync_GracefulShutdown_HandlesCleanly()
    {
        // Arrange
        var channel = Channel.CreateUnbounded<SwarmRequest>();
        var activeSwarms = new ConcurrentDictionary<Guid, SwarmExecution>();
        var scopeFactory = new Mock<IServiceScopeFactory>();
        var options = Options.Create(new SwarmOptions());
        var loggerFactory = NullLoggerFactory.Instance;
        var orchestratorFactory = new Swarmwright.Hosting.SwarmOrchestratorFactory(
            options, loggerFactory);

        using var service = new SwarmDispatcherService(
            channel.Reader,
            activeSwarms,
            scopeFactory.Object,
            orchestratorFactory,
            Mock.Of<ISwarmObservationSink>(),
            options,
            loggerFactory);

        using var cts = new CancellationTokenSource();

        // Act — cancel immediately to trigger graceful shutdown
        cts.Cancel();
        var act = async () => await service.StartAsync(cts.Token);

        // Assert
        await act.Should().NotThrowAsync();
    }

    /// <summary>
    /// Verifies that for each terminal path (Complete, Cancelled, Failed) the
    /// dispatcher publishes exactly one <see cref="SwarmRunCompletedNotification"/>
    /// via <see cref="ISwarmNotificationPublisher"/> carrying the correct terminal metadata.
    /// The fake publisher returning immediately also evidences the publish is
    /// non-blocking.
    /// </summary>
    /// <param name="outcome">The terminal outcome the orchestrator simulates.</param>
    /// <param name="expectedState">The expected <see cref="SwarmInstanceState"/> on the notification.</param>
    /// <returns>A task representing the asynchronous test.</returns>
    [TestMethod]
    [DataRow(TerminalOutcome.Complete, SwarmInstanceState.Complete)]
    [DataRow(TerminalOutcome.Cancelled, SwarmInstanceState.Cancelled)]
    [DataRow(TerminalOutcome.Failed, SwarmInstanceState.Failed)]
    public async Task PublishesExactlyOneCompletedNotificationPerTerminalPath(
        TerminalOutcome outcome,
        SwarmInstanceState expectedState)
    {
        // Arrange
        var channel = Channel.CreateUnbounded<SwarmRequest>();
        var activeSwarms = new ConcurrentDictionary<Guid, SwarmExecution>();
        var fakePublisher = new RecordingNotificationPublisher();
        var scopeFactory = new Mock<IServiceScopeFactory>();
        var scope = new Mock<IServiceScope>();
        var scopeProvider = new Mock<IServiceProvider>();
        scopeProvider.Setup(p => p.GetService(typeof(ISwarmEventBus))).Returns(new SwarmEventBus());
        var mockRepo = new Mock<ISwarmRepository>();
        mockRepo.Setup(r => r.GetSwarmAsync(It.IsAny<Guid>())).ReturnsAsync((SwarmEntity?)null);
        mockRepo.Setup(r => r.CreateSwarmAsync(It.IsAny<SwarmEntity>())).Returns(Task.CompletedTask);
        scopeProvider.Setup(p => p.GetService(typeof(ISwarmRepository))).Returns(mockRepo.Object);
        scopeProvider.Setup(p => p.GetService(typeof(ISwarmNotificationPublisher))).Returns(fakePublisher);
        scope.SetupGet(s => s.ServiceProvider).Returns(scopeProvider.Object);
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        var options = Options.Create(new SwarmOptions { MaxConcurrentSwarms = 1, MaxQueuedSwarms = 1 });
        var loggerFactory = NullLoggerFactory.Instance;
        var orchestrator = new TerminalOutcomeOrchestrator(outcome);

        var swarmId = Guid.Parse("12121212-3434-5656-7878-909090909090");
        var execution = new SwarmExecution
        {
            SwarmId = swarmId,
            Goal = "Terminal-path goal.",
            TemplateKey = "code-review",
            Cts = new CancellationTokenSource(),
            EventBus = new SwarmEventBus(),
            AgUiAdapter = new SwarmEventAdapter(),
            WorkDirectory = Path.GetTempPath(),
        };
        activeSwarms[swarmId] = execution;

        using var service = new TestSwarmDispatcherService(
            channel.Reader,
            activeSwarms,
            scopeFactory.Object,
            options,
            loggerFactory,
            orchestrator);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Act
        await service.StartAsync(cts.Token);
        await channel.Writer.WriteAsync(new SwarmRequest(swarmId, execution.Goal, null), cts.Token);
        await orchestrator.RunAsyncInvoked.Task.WaitAsync(cts.Token);
        channel.Writer.Complete();
        await service.StopAsync(cts.Token);

        // Assert — exactly one notification, with the right terminal metadata.
        fakePublisher.Published.Should().ContainSingle(
            "the dispatcher must publish exactly one SwarmRunCompletedNotification per terminal path.");
        var notification = fakePublisher.Published[0];
        notification.SwarmId.Should().Be(swarmId);
        notification.Goal.Should().Be("Terminal-path goal.");
        notification.TemplateKey.Should().Be("code-review");
        notification.FinalState.Should().Be(expectedState);
        if (expectedState == SwarmInstanceState.Failed)
        {
            notification.FailureReason.Should().NotBeNullOrEmpty(
                "a Failed run must carry its failure reason.");
        }
    }

    /// <summary>
    /// The terminal outcome a <see cref="TerminalOutcomeOrchestrator"/> simulates.
    /// </summary>
    public enum TerminalOutcome
    {
        /// <summary>The run completes successfully.</summary>
        Complete,

        /// <summary>The run is cancelled (throws <see cref="OperationCanceledException"/>).</summary>
        Cancelled,

        /// <summary>The run fails (throws a non-cancellation exception).</summary>
        Failed,
    }

    /// <summary>
    /// Records every notification published through
    /// <see cref="ISwarmNotificationPublisher.PublishAsync{TNotification}(TNotification, CancellationToken)"/>.
    /// Returns immediately so the test also evidences the publish is non-blocking.
    /// </summary>
    private sealed class RecordingNotificationPublisher : ISwarmNotificationPublisher
    {
        public List<SwarmRunCompletedNotification> Published { get; } = new();

        public ValueTask PublishAsync<TNotification>(TNotification notification, CancellationToken cancellationToken)
            where TNotification : notnull
        {
            if (notification is SwarmRunCompletedNotification completed)
            {
                this.Published.Add(completed);
            }

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Spy orchestrator that drives a chosen terminal outcome so the dispatcher's
    /// success / cancel / catch arms each run.
    /// </summary>
    private sealed class TerminalOutcomeOrchestrator : ISwarmOrchestrator
    {
        private readonly TerminalOutcome outcome;

        public TerminalOutcomeOrchestrator(TerminalOutcome outcome)
        {
            this.outcome = outcome;
        }

        public TaskCompletionSource RunAsyncInvoked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Guid SwarmId { get; private set; }

        public SwarmInstanceState Phase { get; private set; }

        public bool IsCancelled { get; private set; }

        public Task<string> RunAsync(Guid swarmId, string goal, CancellationToken cancellationToken = default)
        {
            this.SwarmId = swarmId;
            this.RunAsyncInvoked.TrySetResult();

            return this.outcome switch
            {
                TerminalOutcome.Cancelled => throw new OperationCanceledException("Simulated cancellation."),
                TerminalOutcome.Failed => throw new InvalidOperationException("Simulated failure."),
                TerminalOutcome.Complete => Task.FromResult("done"),
                _ => Task.FromResult("done"),
            };
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

    /// <summary>
    /// Test double <see cref="SwarmDispatcherService"/> that overrides
    /// <see cref="SwarmDispatcherService.BuildOrchestrator"/> to return a pre-built spy
    /// orchestrator instead of resolving one from the scope's service provider. This lets
    /// us drive the dispatcher's channel pipeline without standing up the full swarm DI graph.
    /// </summary>
    private sealed class TestSwarmDispatcherService : SwarmDispatcherService
    {
        private readonly ISwarmOrchestrator stubOrchestrator;

        /// <summary>
        /// Initializes a new instance of the <see cref="TestSwarmDispatcherService"/> class.
        /// </summary>
        /// <param name="channelReader">The channel reader for receiving swarm requests.</param>
        /// <param name="activeSwarms">The shared dictionary of active swarm executions.</param>
        /// <param name="scopeFactory">The service scope factory for creating per-swarm DI scopes.</param>
        /// <param name="options">The swarm configuration options.</param>
        /// <param name="loggerFactory">The logger factory for structured logging.</param>
        /// <param name="stubOrchestrator">The stub orchestrator to return from <see cref="BuildOrchestrator"/>.</param>
        public TestSwarmDispatcherService(
            ChannelReader<SwarmRequest> channelReader,
            ConcurrentDictionary<Guid, SwarmExecution> activeSwarms,
            IServiceScopeFactory scopeFactory,
            IOptions<SwarmOptions> options,
            Microsoft.Extensions.Logging.ILoggerFactory loggerFactory,
            ISwarmOrchestrator stubOrchestrator)
            : base(channelReader, activeSwarms, scopeFactory, new Swarmwright.Hosting.SwarmOrchestratorFactory(options, loggerFactory), Mock.Of<ISwarmObservationSink>(), options, loggerFactory)
        {
            this.stubOrchestrator = stubOrchestrator;
        }

        /// <inheritdoc/>
        protected internal override ISwarmOrchestrator BuildOrchestrator(IServiceScope scope, SwarmExecution execution)
        {
            return this.stubOrchestrator;
        }
    }

    /// <summary>
    /// Spy <see cref="ISwarmOrchestrator"/> that records the swarm identifier supplied to
    /// <see cref="RunAsync(Guid, string, CancellationToken)"/> and signals a
    /// <see cref="TaskCompletionSource"/> the test awaits before asserting.
    /// </summary>
    private sealed class RecordingOrchestrator : ISwarmOrchestrator
    {
        /// <summary>Gets the task that completes on the first RunAsync invocation.</summary>
        public TaskCompletionSource RunAsyncInvoked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Gets the ordered list of swarm identifiers passed to RunAsync.</summary>
        public List<Guid> ReceivedSwarmIds { get; } = new();

        /// <inheritdoc/>
        public Guid SwarmId { get; private set; }

        /// <inheritdoc/>
        public SwarmInstanceState Phase { get; private set; }

        /// <inheritdoc/>
        public bool IsCancelled { get; private set; }

        /// <inheritdoc/>
        public Task<string> RunAsync(Guid swarmId, string goal, CancellationToken cancellationToken = default)
        {
            this.SwarmId = swarmId;
            this.ReceivedSwarmIds.Add(swarmId);
            this.RunAsyncInvoked.TrySetResult();
            return Task.FromResult("done");
        }

        /// <inheritdoc/>
        public Task CancelAsync()
        {
            this.IsCancelled = true;
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public void SignalContinue()
        {
        }

        /// <inheritdoc/>
        public void SignalSkip()
        {
        }
    }
}
