using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Swarmwright.Configuration;
using Swarmwright.Database.Models;
using Swarmwright.Database.Repositories;
using Swarmwright.Events;
using Swarmwright.Hosting.StateMachine;
using Swarmwright.Models.Enums;
using Swarmwright.Orchestration;

namespace Swarmwright.Hosting;

/// <summary>
/// A background service that reads swarm requests from a channel and dispatches them for execution.
/// </summary>
[SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Dispatcher must catch all exceptions to prevent service crash and log them.")]
public partial class SwarmDispatcherService : BackgroundService
{
    private readonly ChannelReader<SwarmRequest> channelReader;
    private readonly ConcurrentDictionary<Guid, SwarmExecution> activeSwarms;
    private readonly IServiceScopeFactory scopeFactory;
    private readonly ISwarmOrchestratorFactory orchestratorFactory;
    private readonly ISwarmObservationSink observationSink;
    private readonly SwarmOptions options;
    private readonly ILogger<SwarmDispatcherService> logger;
    private readonly ILoggerFactory loggerFactory;
    private readonly SemaphoreSlim concurrencyLimiter;

    /// <summary>
    /// Initializes a new instance of the <see cref="SwarmDispatcherService"/> class.
    /// </summary>
    /// <param name="channelReader">The channel reader for receiving swarm requests.</param>
    /// <param name="activeSwarms">The shared dictionary of active swarm executions.</param>
    /// <param name="scopeFactory">The service scope factory for creating per-swarm DI scopes.</param>
    /// <param name="orchestratorFactory">The factory that builds a per-swarm orchestrator from a DI scope.</param>
    /// <param name="observationSink">
    /// The observation sink that publishes terminal-completion signals. Production
    /// DI registers the singleton; the dispatcher and manager must share one
    /// instance or signals will be published to a sink the manager is not
    /// reading from.
    /// </param>
    /// <param name="options">The swarm configuration options.</param>
    /// <param name="loggerFactory">The logger factory for structured logging.</param>
    public SwarmDispatcherService(
        ChannelReader<SwarmRequest> channelReader,
        ConcurrentDictionary<Guid, SwarmExecution> activeSwarms,
        IServiceScopeFactory scopeFactory,
        ISwarmOrchestratorFactory orchestratorFactory,
        ISwarmObservationSink observationSink,
        IOptions<SwarmOptions> options,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(orchestratorFactory);
        ArgumentNullException.ThrowIfNull(observationSink);

        this.channelReader = channelReader;
        this.activeSwarms = activeSwarms;
        this.scopeFactory = scopeFactory;
        this.orchestratorFactory = orchestratorFactory;
        this.observationSink = observationSink;
        this.options = options.Value;
        this.loggerFactory = loggerFactory;
        this.logger = loggerFactory.CreateLogger<SwarmDispatcherService>();
        this.concurrencyLimiter = new SemaphoreSlim(this.options.MaxConcurrentSwarms);
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        this.LogDispatcherStarted(this.options.MaxConcurrentSwarms, this.options.MaxQueuedSwarms);

        var runningTasks = new ConcurrentDictionary<Guid, Task>();

        try
        {
            await foreach (var request in this.channelReader.ReadAllAsync(stoppingToken))
            {
                if (!this.activeSwarms.TryGetValue(request.SwarmId, out var execution)
                    || execution is null)
                {
                    this.LogSwarmNotFound(request.SwarmId);
                    continue;
                }

                var task = this.RunSwarmAsync(execution, stoppingToken);
                execution.RunningTask = task;
                runningTasks[request.SwarmId] = task;

                var swarmId = request.SwarmId;
                _ = task.ContinueWith(
                    _ => runningTasks.TryRemove(swarmId, out var _),
                    TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown — fall through to graceful await.
        }
        finally
        {
            this.LogDispatcherShuttingDown(runningTasks.Count);

            if (!runningTasks.IsEmpty)
            {
                try
                {
                    await Task.WhenAll(runningTasks.Values)
                        .WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    this.LogShutdownTimeout(runningTasks.Count);
                }
                catch (Exception ex)
                {
                    this.LogShutdownError(ex);
                }
            }
        }
    }

    private async Task RunSwarmAsync(SwarmExecution execution, CancellationToken hostToken)
    {
        await this.concurrencyLimiter.WaitAsync(hostToken).ConfigureAwait(false);
        IDisposable? fanInSubscription = null;
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            hostToken, execution.Cts.Token);
        using var scope = this.scopeFactory.CreateScope();

        try
        {
            var globalBus = scope.ServiceProvider.GetRequiredService<ISwarmEventBus>();
            fanInSubscription = execution.EventBus.Subscribe(globalBus.EmitAsync);

            var contextFactory = scope.ServiceProvider.GetService<IDbContextFactory<Database.SwarmDbContext>>();
            execution.EventLogger = new SwarmEventLogger(
                execution.EventBus,
                execution.SwarmId,
                contextFactory,
                this.loggerFactory.CreateLogger<SwarmEventLogger>());

            // Create the swarm row up front — before BuildOrchestrator — so
            // that a pre-Planning exception (template load error, DI
            // resolution error, etc.) has a row to attach a Failed state
            // transition to. SwarmService.CreateSwarmAsync is idempotent
            // against this: when the orchestrator later reaches its own
            // CreateSwarmAsync call, it sees the existing row and skips the
            // insert.
            var repository = scope.ServiceProvider.GetRequiredService<ISwarmRepository>();
            if (await repository.GetSwarmAsync(execution.SwarmId).ConfigureAwait(false) is null)
            {
                var contextJson = JsonSerializer.Serialize(execution.Context, SwarmJsonOptions.Default);
                await repository.CreateSwarmAsync(new SwarmEntity
                {
                    Id = execution.SwarmId,
                    Goal = execution.Goal,
                    TemplateKey = execution.TemplateKey,
                    ContextJson = contextJson,
                }).ConfigureAwait(false);
            }

            // Populate the scoped run-context holder before BuildOrchestrator so
            // custom tool providers resolved from this scope (scoped or transient)
            // observe the execution's id, work directory, and context. Resolve the
            // concrete type — the public ISwarmRunContext is getter-only.
            var runContext = scope.ServiceProvider.GetService<Tools.SwarmRunContext>();
            if (runContext is not null)
            {
                runContext.Initialize(execution.SwarmId, execution.WorkDirectory, execution.Context);
                this.LogRunContextPopulated(execution.SwarmId, execution.Context.Count);
            }

            var orchestrator = this.BuildOrchestrator(scope, execution);
            execution.Orchestrator = orchestrator;

            this.LogSwarmStarted(execution.SwarmId, execution.Goal);

            await orchestrator.RunAsync(execution.SwarmId, execution.Goal, linkedCts.Token).ConfigureAwait(false);

            execution.FinalState = SwarmInstanceState.Complete;
            this.LogSwarmCompleted(execution.SwarmId);
        }
        catch (OperationCanceledException)
        {
            execution.FinalState = SwarmInstanceState.Cancelled;
            this.LogSwarmCancelled(execution.SwarmId);
        }
        catch (Exception ex)
        {
            execution.FinalState = SwarmInstanceState.Failed;
            execution.FailureReason = ex.Message;
            this.LogSwarmFailed(execution.SwarmId, ex);

            // When the orchestrator fails before it can record its own
            // Failed transition (e.g., template-load error in BuildOrchestrator,
            // or any other pre-Planning error) the swarm would be stuck in
            // Created forever. Record the Failed transition from the
            // dispatcher's own catch so the state machine reflects reality.
            // The state service is idempotent on matching current state, so
            // it's a no-op if the orchestrator already recorded Failed.
            await this.TryRecordFailedTransitionAsync(
                scope.ServiceProvider,
                execution.SwarmId,
                ex).ConfigureAwait(false);
        }
        finally
        {
            execution.IsTerminal = true;

            // Publish terminal completion to the observation sink. The orchestrator's
            // success / cancel / failure paths populate FinalState (and FailureReason
            // when failed) on the execution before this finally runs. A pre-Planning
            // crash can bypass those paths; default to Failed so consumers still
            // observe a deterministic terminal value.
            execution.FinalState ??= SwarmInstanceState.Failed;

            // Publish the archival notification BEFORE signalling the terminal
            // sink. The notification is [ExecutionSchedule(Background)] so the
            // publish returns immediately and never delays the signal; archival
            // runs off-thread in SwarmRunCompletedNotificationConsumer. Resolved
            // per-swarm from the scope; absent in hosts without the mediator.
            await this.PublishRunCompletedAsync(scope.ServiceProvider, execution).ConfigureAwait(false);

            await this.observationSink
                .SignalTerminalAsync(execution.SwarmId, execution)
                .ConfigureAwait(false);

            this.concurrencyLimiter.Release();
            fanInSubscription?.Dispose();

            _ = Task.Delay(TimeSpan.FromMinutes(5), CancellationToken.None)
                .ContinueWith(
                    _ =>
                    {
                        this.activeSwarms.TryRemove(execution.SwarmId, out var _);
                        execution.Dispose();
                    },
                    TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Publishes a <see cref="SwarmRunCompletedNotification"/> for the terminal run via the
    /// <see cref="ISwarmNotificationPublisher"/>, when one is registered. The notification is
    /// enqueued to the background pipeline, so the publish returns immediately and the terminal
    /// observation signal is not delayed. Best-effort: a missing publisher or an enqueue failure
    /// is logged and swallowed so archival never breaks the terminal path.
    /// </summary>
    /// <param name="services">The per-swarm scope's service provider.</param>
    /// <param name="execution">The terminal swarm execution carrying the manifest fields.</param>
    /// <returns>A task representing the asynchronous publish.</returns>
    private async Task PublishRunCompletedAsync(IServiceProvider services, SwarmExecution execution)
    {
        try
        {
            var publisher = services.GetService<ISwarmNotificationPublisher>();
            if (publisher is null)
            {
                return;
            }

            var notification = new SwarmRunCompletedNotification
            {
                SwarmId = execution.SwarmId,
                WorkDirectory = execution.WorkDirectory,
                Goal = execution.Goal,
                TemplateKey = execution.TemplateKey,
                CreatedUtc = execution.CreatedAt,
                CompletedUtc = DateTime.UtcNow,
                FinalState = execution.FinalState ?? SwarmInstanceState.Failed,
                FailureReason = execution.FailureReason,
                Context = execution.Context,
            };

            await publisher.PublishAsync(notification, CancellationToken.None).ConfigureAwait(false);
            this.LogRunCompletedNotificationPublished(execution.SwarmId);
        }
        catch (Exception ex)
        {
            this.LogRunCompletedNotificationPublishFailed(execution.SwarmId, ex);
        }
    }

    /// <summary>
    /// Builds the <see cref="ISwarmOrchestrator"/> used to drive a single swarm execution.
    /// Exposed as <c>protected internal virtual</c> so tests can substitute a spy orchestrator
    /// without standing up the full swarm dependency graph. Delegates to the injected
    /// <see cref="ISwarmOrchestratorFactory"/> so the same wiring backs both the
    /// dispatcher's fresh runs and the rehydrator's evicted-swarm wake-ups.
    /// </summary>
    /// <param name="scope">The per-swarm dependency injection scope.</param>
    /// <param name="execution">The swarm execution tracking the run.</param>
    /// <returns>The orchestrator that executes the swarm lifecycle.</returns>
    protected internal virtual ISwarmOrchestrator BuildOrchestrator(IServiceScope scope, SwarmExecution execution)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(execution);
        return this.orchestratorFactory.Build(scope, execution);
    }

    /// <summary>
    /// Records a Failed swarm transition when the run path threw an
    /// exception the orchestrator could not record itself (typically a
    /// BuildOrchestrator failure that happens before the orchestrator
    /// exists). Before writing the swarm-level Failed row, walks the
    /// repository for any task still in <see cref="TaskState.InProgress"/>
    /// and transitions each to <see cref="TaskState.Failed"/> — orphan
    /// cleanup for the pre-orchestrator crash path (defense-in-depth
    /// Layer 1b). Swallows errors from either step so a cascading failure
    /// can't crash the dispatcher — the root exception is already logged
    /// above. <c>internal</c> so unit tests can drive the helper without
    /// standing up a live dispatcher loop.
    /// </summary>
    /// <param name="services">The per-swarm scope's service provider.</param>
    /// <param name="swarmId">The swarm identifier whose run failed.</param>
    /// <param name="rootException">The exception that triggered the catch; its message is forwarded to the audit row.</param>
    /// <returns>A task representing the asynchronous cleanup + transition.</returns>
    internal async Task TryRecordFailedTransitionAsync(
        IServiceProvider services,
        Guid swarmId,
        Exception rootException)
    {
        try
        {
            var stateService = services.GetService<IStateTransitionService>();
            if (stateService is null)
            {
                return;
            }

            // Layer 1b: fail orphan in-flight tasks FIRST so chronology in
            // the audit trail reads "tasks cleaned up, then swarm terminal."
            var repository = services.GetService<ISwarmRepository>();
            if (repository is not null)
            {
                await FailInFlightTasksViaRepositoryAsync(
                    repository,
                    stateService,
                    swarmId).ConfigureAwait(false);
            }

            await stateService.TransitionSwarmAsync(
                swarmId,
                SwarmInstanceState.Failed,
                TransitionReasons.RunFailed,
                actor: "system",
                note: rootException.Message,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.LogFailedTransitionRecordError(swarmId, ex);
        }
    }

    /// <summary>
    /// Walks the swarm's persisted tasks and transitions every
    /// <see cref="TaskState.InProgress"/> row to <see cref="TaskState.Failed"/>
    /// with <see cref="TransitionReasons.RunFailed"/>. Used by the dispatcher's
    /// outer catch when <c>BuildOrchestrator</c> threw before the orchestrator
    /// could walk its own in-memory task board. <c>retryCountDelta=0</c> — the
    /// workers never got to run, so it's not fair to charge retry budget.
    /// </summary>
    private static async Task FailInFlightTasksViaRepositoryAsync(
        ISwarmRepository repository,
        IStateTransitionService stateService,
        Guid swarmId)
    {
        List<TaskEntity>? tasks;
        try
        {
            tasks = await repository.GetTasksAsync(swarmId).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Cleanup path must not shadow the primary exception.
        catch
#pragma warning restore CA1031
        {
            return;
        }

        if (tasks is null)
        {
            return;
        }

        foreach (var task in tasks.Where(t => string.Equals(t.State, nameof(TaskState.InProgress), StringComparison.Ordinal)))
        {
            try
            {
                await stateService.TransitionTaskAsync(
                    swarmId,
                    task.Id,
                    TaskState.Failed,
                    TransitionReasons.RunFailed,
                    actor: "system",
                    retryCountDelta: 0,
                    note: "Swarm failed before orchestrator ran; task was left in-flight by an earlier run",
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Per-task failure must not stop other orphans from being cleaned up.
            catch
#pragma warning restore CA1031
            {
                // One stuck task doesn't prevent cleanup of the rest.
                // The root exception is already logged by the caller.
            }
        }
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        this.concurrencyLimiter.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    [LoggerMessage(LogLevel.Information, "Swarm dispatcher started (maxConcurrent={MaxConcurrent}, maxQueued={MaxQueued}).")]
    private partial void LogDispatcherStarted(int maxConcurrent, int maxQueued);

    [LoggerMessage(LogLevel.Information, "Swarm dispatcher shutting down with {ActiveCount} active swarms.")]
    private partial void LogDispatcherShuttingDown(int activeCount);

    [LoggerMessage(LogLevel.Warning, "Shutdown timeout reached with {RemainingCount} swarms still running.")]
    private partial void LogShutdownTimeout(int remainingCount);

    [LoggerMessage(LogLevel.Error, "Error during shutdown await.")]
    private partial void LogShutdownError(Exception exception);

    [LoggerMessage(LogLevel.Warning, "Swarm request received for unknown swarm ID {SwarmId}.")]
    private partial void LogSwarmNotFound(Guid swarmId);

    [LoggerMessage(LogLevel.Information, "Swarm {SwarmId} started: {Goal}.")]
    private partial void LogSwarmStarted(Guid swarmId, string goal);

    [LoggerMessage(LogLevel.Debug, "Swarm {SwarmId}: populated run context holder with {ContextCount} context entries.")]
    private partial void LogRunContextPopulated(Guid swarmId, int contextCount);

    [LoggerMessage(LogLevel.Information, "Swarm {SwarmId} completed.")]
    private partial void LogSwarmCompleted(Guid swarmId);

    [LoggerMessage(LogLevel.Information, "Swarm {SwarmId} cancelled.")]
    private partial void LogSwarmCancelled(Guid swarmId);

    [LoggerMessage(LogLevel.Error, "Swarm {SwarmId} failed.")]
    private partial void LogSwarmFailed(Guid swarmId, Exception exception);

    [LoggerMessage(LogLevel.Warning, "Swarm {SwarmId}: failed to record Failed state transition in dispatcher catch.")]
    private partial void LogFailedTransitionRecordError(Guid swarmId, Exception exception);

    [LoggerMessage(LogLevel.Information, "Swarm {SwarmId}: published run-completed archival notification.")]
    private partial void LogRunCompletedNotificationPublished(Guid swarmId);

    [LoggerMessage(LogLevel.Warning, "Swarm {SwarmId}: failed to publish run-completed archival notification; archival skipped, run unaffected.")]
    private partial void LogRunCompletedNotificationPublishFailed(Guid swarmId, Exception exception);
}
