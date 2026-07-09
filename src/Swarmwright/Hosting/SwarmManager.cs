using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Swarmwright.Configuration;
using Swarmwright.Database.Repositories;
using Swarmwright.Events;
using Swarmwright.Hosting.StateMachine;
using Swarmwright.Models.Enums;

namespace Swarmwright.Hosting;

/// <summary>
/// Manages swarm executions: creates new swarms (writing to the dispatcher
/// channel), hands out references from an in-memory dictionary, signals
/// orchestrators, and — on demand — resurrects evicted swarms from the
/// repository and re-enqueues them for the dispatcher.
/// </summary>
public sealed partial class SwarmManager : ISwarmManager
{
    private readonly ChannelWriter<SwarmRequest> channelWriter;
    private readonly ConcurrentDictionary<Guid, SwarmExecution> activeSwarms;
    private readonly SwarmOptions options;
    private readonly ISwarmRepository repository;
    private readonly ISwarmObservationSink observationSink;
    private readonly ILogger<SwarmManager> logger;
    private readonly ConcurrentDictionary<Guid, Lazy<Task<SwarmExecution?>>> inflight = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="SwarmManager"/> class.
    /// </summary>
    /// <param name="channelWriter">The channel writer for dispatching swarm requests.</param>
    /// <param name="activeSwarms">The shared dictionary of active swarm executions.</param>
    /// <param name="options">The swarm configuration options.</param>
    /// <param name="repository">The swarm repository for evicted-swarm lookups.</param>
    /// <param name="observationSink">
    /// The observation sink that backs <c>WaitForCompletionAsync</c> /
    /// <c>WaitForStateChangeAsync</c> / <c>SwarmCompleted</c>. Production DI
    /// registers the singleton; the dispatcher and manager must share one
    /// instance or signals will be published to a sink the manager is not
    /// reading from.
    /// </param>
    /// <param name="logger">Diagnostic logger.</param>
    public SwarmManager(
        ChannelWriter<SwarmRequest> channelWriter,
        ConcurrentDictionary<Guid, SwarmExecution> activeSwarms,
        IOptions<SwarmOptions> options,
        ISwarmRepository repository,
        ISwarmObservationSink observationSink,
        ILogger<SwarmManager> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(observationSink);
        ArgumentNullException.ThrowIfNull(logger);
        this.channelWriter = channelWriter;
        this.activeSwarms = activeSwarms;
        this.options = options.Value;
        this.repository = repository;
        this.observationSink = observationSink;
        this.logger = logger;
    }

    /// <inheritdoc/>
    public event Func<Guid, SwarmExecution, Task> SwarmCompleted
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            this.observationSink.OnTerminal(value);
        }

        remove
        {
            // V1 does not support unsubscription — subscribers are intended
            // to be process-lifetime observers. The sink has no remove path.
        }
    }

    /// <inheritdoc/>
    public void RegisterCompletionWaiter(Guid swarmId) =>
        this.observationSink.RegisterCompletionWaiter(swarmId);

    /// <inheritdoc/>
    public Task<SwarmExecution> WaitForCompletionAsync(Guid swarmId, CancellationToken cancellationToken = default) =>
        this.observationSink.WaitForCompletionAsync(swarmId, cancellationToken);

    /// <inheritdoc/>
    public Task<SwarmInstanceState> WaitForStateChangeAsync(Guid swarmId, CancellationToken cancellationToken = default) =>
        this.observationSink.WaitForStateChangeAsync(swarmId, cancellationToken);

    /// <inheritdoc/>
    public async Task<Guid> CreateSwarmAsync(
        string goal,
        string? templateKey = null,
        IReadOnlyDictionary<string, string>? context = null)
    {
        var swarmId = Guid.NewGuid();
        var workDir = this.CreateSwarmWorkDirectory(swarmId);

        var execution = new SwarmExecution
        {
            SwarmId = swarmId,
            Goal = goal,
            TemplateKey = templateKey,
            Cts = new CancellationTokenSource(),
            EventBus = new SwarmEventBus(),
            AgUiAdapter = new Events.AgUI.SwarmEventAdapter(),
            WorkDirectory = workDir,
            Context = context ?? new Dictionary<string, string>(),
        };

        this.activeSwarms[swarmId] = execution;
        await this.channelWriter.WriteAsync(new SwarmRequest(swarmId, goal, templateKey)).ConfigureAwait(false);

        return swarmId;
    }

    /// <inheritdoc/>
    public SwarmExecution? GetSwarm(Guid swarmId)
    {
        this.activeSwarms.TryGetValue(swarmId, out var execution);
        return execution;
    }

    /// <inheritdoc/>
    public IReadOnlyList<SwarmExecution> ListActiveSwarms()
    {
        return this.activeSwarms.Values.ToList().AsReadOnly();
    }

    /// <inheritdoc/>
    public async Task CancelSwarmAsync(Guid swarmId)
    {
        if (this.activeSwarms.TryGetValue(swarmId, out var execution))
        {
            await execution.Cts.CancelAsync().ConfigureAwait(false);

            if (execution.Orchestrator is not null)
            {
                await execution.Orchestrator.CancelAsync().ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc/>
    public bool SignalContinue(Guid swarmId)
    {
        if (this.activeSwarms.TryGetValue(swarmId, out var execution))
        {
            execution.Orchestrator?.SignalContinue();
            return true;
        }

        return false;
    }

    /// <inheritdoc/>
    public bool SignalSkip(Guid swarmId)
    {
        if (this.activeSwarms.TryGetValue(swarmId, out var execution))
        {
            execution.Orchestrator?.SignalSkip();
            return true;
        }

        return false;
    }

    /// <inheritdoc/>
    public string? GetWorkDirectory(Guid swarmId)
    {
        if (this.activeSwarms.TryGetValue(swarmId, out var execution))
        {
            return execution.WorkDirectory;
        }

        var basePath = string.IsNullOrWhiteSpace(this.options.WorkBasePath)
            ? Path.Combine(Path.GetTempPath(), "swarm-work")
            : this.options.WorkBasePath;

        var constructedPath = Path.Combine(basePath, swarmId.ToString());
        return Directory.Exists(constructedPath) ? constructedPath : null;
    }

    /// <inheritdoc/>
    public async Task<SwarmExecution?> EnsureLiveAsync(Guid swarmId, CancellationToken cancellationToken = default)
    {
        if (this.activeSwarms.TryGetValue(swarmId, out var existing))
        {
            this.LogSwarmAlreadyLive(swarmId);
            return existing;
        }

        var lazy = this.inflight.GetOrAdd(
            swarmId,
            id => new Lazy<Task<SwarmExecution?>>(() => this.ResurrectAsync(id, cancellationToken)));

        try
        {
            return await lazy.Value.ConfigureAwait(false);
        }
        catch
        {
            this.inflight.TryRemove(swarmId, out _);
            throw;
        }
    }

    private async Task<SwarmExecution?> ResurrectAsync(Guid swarmId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var entity = await this.repository.GetSwarmAsync(swarmId).ConfigureAwait(false);
        if (entity is null)
        {
            this.LogSwarmNotFound(swarmId);
            return null;
        }

        if (Enum.TryParse<SwarmInstanceState>(entity.State, out var state)
            && SwarmStateGuards.IsTerminal(state))
        {
            this.LogSwarmTerminal(swarmId, entity.State);
            return null;
        }

        var workDir = this.GetWorkDirectory(swarmId)
            ?? Path.Combine(Path.GetTempPath(), "swarm-work", swarmId.ToString());

        var context = this.DeserializeContextOrEmpty(swarmId, entity.ContextJson);

        var execution = new SwarmExecution
        {
            SwarmId = swarmId,
            Goal = entity.Goal,
            TemplateKey = entity.TemplateKey,
            Cts = new CancellationTokenSource(),
            EventBus = new SwarmEventBus(),
            AgUiAdapter = new Events.AgUI.SwarmEventAdapter(),
            WorkDirectory = workDir,
            Context = context,
        };

        // TryAdd rather than indexer — another caller could have registered
        // between our initial TryGetValue and here. If it did, return theirs
        // and discard ours; no channel write happens in that branch.
        if (!this.activeSwarms.TryAdd(swarmId, execution))
        {
            execution.Dispose();
            return this.activeSwarms.TryGetValue(swarmId, out var winner) ? winner : null;
        }

        await this.channelWriter
            .WriteAsync(new SwarmRequest(swarmId, entity.Goal, entity.TemplateKey), cancellationToken)
            .ConfigureAwait(false);

        this.LogSwarmEnqueued(swarmId, entity.State);
        return execution;
    }

    private Dictionary<string, string> DeserializeContextOrEmpty(Guid swarmId, string? contextJson)
    {
        if (string.IsNullOrWhiteSpace(contextJson))
        {
            return new Dictionary<string, string>();
        }

        try
        {
            return JsonSerializer
                .Deserialize<Dictionary<string, string>>(contextJson, SwarmJsonOptions.Default)
                ?? new Dictionary<string, string>();
        }
        catch (JsonException ex)
        {
            this.LogContextDeserializeFailed(swarmId, ex);
            return new Dictionary<string, string>();
        }
    }

    private string CreateSwarmWorkDirectory(Guid swarmId)
    {
        var basePath = string.IsNullOrWhiteSpace(this.options.WorkBasePath)
            ? Path.Combine(Path.GetTempPath(), "swarm-work")
            : this.options.WorkBasePath;

        var workDir = Path.Combine(basePath, swarmId.ToString());
        Directory.CreateDirectory(workDir);
        return workDir;
    }

    [LoggerMessage(
        LogLevel.Information,
        "Manager: swarm {SwarmId} already live; returning existing execution.")]
    private partial void LogSwarmAlreadyLive(Guid swarmId);

    [LoggerMessage(
        LogLevel.Warning,
        "Manager: swarm {SwarmId} not found in repository.")]
    private partial void LogSwarmNotFound(Guid swarmId);

    [LoggerMessage(
        LogLevel.Information,
        "Manager: swarm {SwarmId} is terminal ({State}); not resurrecting.")]
    private partial void LogSwarmTerminal(Guid swarmId, string state);

    [LoggerMessage(
        LogLevel.Information,
        "Manager: swarm {SwarmId} resurrected from state {State}; enqueued on dispatcher channel.")]
    private partial void LogSwarmEnqueued(Guid swarmId, string state);

    [LoggerMessage(
        LogLevel.Warning,
        "Manager: swarm {SwarmId} has malformed context JSON; degrading to empty context.")]
    private partial void LogContextDeserializeFailed(Guid swarmId, Exception exception);
}
