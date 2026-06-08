using System.Text.Json;
using Swarmwright.Core;
using Swarmwright.Database.Mapping;
using Swarmwright.Database.Models;
using Swarmwright.Database.Repositories;
using Swarmwright.Hosting.StateMachine;
using Swarmwright.Models;
using Swarmwright.Models.Enums;

namespace Swarmwright.Services;

/// <summary>
/// Coordinates the runtime-only swarm collaborators (InboxSystem,
/// TeamRegistry, agents dictionary held by the orchestrator) and exposes
/// a read surface over <see cref="ISwarmRepository"/>. Every task read goes
/// straight to the repository, every task write goes through
/// <see cref="IStateTransitionService.TransitionTaskAsync"/>.
/// </summary>
public sealed class SwarmService : ISwarmService
{
    private readonly ISwarmRepository repository;

    /// <summary>
    /// Initializes a new instance of the <see cref="SwarmService"/> class.
    /// </summary>
    /// <param name="inboxSystem">The inbox system for agent messaging.</param>
    /// <param name="teamRegistry">The team registry for tracking agents.</param>
    /// <param name="repository">The repository — sole storage for swarm tasks.</param>
    public SwarmService(
        IInboxSystem inboxSystem,
        ITeamRegistry teamRegistry,
        ISwarmRepository repository)
    {
        this.InboxSystem = inboxSystem;
        this.TeamRegistry = teamRegistry;
        this.repository = repository;
        this.State = new SwarmState();
    }

    /// <inheritdoc/>
    public Guid SwarmId => this.State.SwarmId;

    /// <inheritdoc/>
    public IInboxSystem InboxSystem { get; }

    /// <inheritdoc/>
    public ITeamRegistry TeamRegistry { get; }

    /// <inheritdoc/>
    public SwarmState State { get; private set; }

    /// <inheritdoc/>
    public async Task CreateSwarmAsync(Guid swarmId, string goal, string? templateKey = null)
    {
        this.State = new SwarmState
        {
            SwarmId = swarmId,
            Goal = goal,
            TemplateKey = templateKey,
            State = SwarmInstanceState.Created,
            RoundNumber = 0,
        };

        // Idempotent against the dispatcher's pre-BuildOrchestrator insert:
        // when the row already exists (dispatcher created it up front so a
        // BuildOrchestrator failure can still be recorded as Failed), skip
        // the insert. Without this guard EF would throw on duplicate Id.
        var existing = await this.repository.GetSwarmAsync(swarmId).ConfigureAwait(false);
        if (existing is not null)
        {
            return;
        }

        await this.repository.CreateSwarmAsync(new SwarmEntity
        {
            Id = swarmId,
            Goal = goal,
            TemplateKey = templateKey,
        }).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task UpdateRoundAsync(int round)
    {
        this.State.RoundNumber = round;
        await this.repository.UpdateRoundAsync(this.SwarmId, round).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task AddTaskAsync(SwarmTask task)
    {
        ArgumentNullException.ThrowIfNull(task);

        // Derive the initial status from BlockedBy count. Used to live in
        // TaskBoard.AddTaskAsync; now that the cache is gone the heuristic
        // stays here so the persisted row reflects Blocked vs Pending
        // correctly. Terminal/active inbound statuses (Completed, Failed,
        // InProgress, AwaitingFeedback) are preserved — those callers are
        // hydrating an already-known shape, not planning a fresh task.
        var isTerminalOrActive = task.Status is TaskState.Completed
            or TaskState.Failed
            or TaskState.InProgress
            or TaskState.AwaitingFeedback;

        if (!isTerminalOrActive)
        {
            task.Status = task.BlockedBy.Count > 0
                ? TaskState.Blocked
                : TaskState.Pending;
        }

        await this.repository.CreateTaskAsync(new TaskEntity
        {
            SwarmId = this.SwarmId,
            Id = task.Id,
            Subject = task.Subject,
            Description = task.Description,
            WorkerRole = task.WorkerRole,
            WorkerName = task.WorkerName,
            State = task.Status.ToString(),
            BlockedByJson = JsonSerializer.Serialize(task.BlockedBy),
        }).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SwarmTask>> GetTasksAsync(string? workerName = null)
    {
        // XXX TODO(2026-04-25): If orchestrator round cadence becomes DB-bound,
        // re-introduce a read-through cache here, invalidated by ISwarmEmissionBroker
        // events. Do NOT bring back UpdateTaskStatusAsync — keep IStateTransitionService
        // as the sole writer.
        var entities = await this.repository.GetTasksAsync(this.SwarmId).ConfigureAwait(false)
            ?? [];
        var mapped = entities
            .Select(SwarmTaskMapper.FromEntity)
            .Where(t => workerName is null || t.WorkerName == workerName)
            .ToList()
            .AsReadOnly();
        return mapped;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SwarmTask>> GetRunnableTasksAsync(string? workerName = null)
    {
        var entities = await this.repository.GetRunnableTasksAsync(this.SwarmId).ConfigureAwait(false)
            ?? [];
        var mapped = entities
            .Select(SwarmTaskMapper.FromEntity)
            .Where(t => workerName is null || t.WorkerName == workerName)
            .ToList()
            .AsReadOnly();
        return mapped;
    }

    /// <inheritdoc/>
    public async Task RegisterAgentAsync(AgentInfo agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        await this.TeamRegistry.RegisterAsync(agent).ConfigureAwait(false);
        this.InboxSystem.RegisterAgent(agent.Name);
        await this.repository.RegisterAgentAsync(new AgentEntity
        {
            SwarmId = this.SwarmId,
            Name = agent.Name,
            Role = agent.Role,
            DisplayName = agent.DisplayName,
            Status = agent.Status.ToString().ToLowerInvariant(),
        }).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task SendMessageAsync(string sender, string recipient, string content)
    {
        await this.InboxSystem.SendAsync(sender, recipient, content).ConfigureAwait(false);
        await this.repository.SaveMessageAsync(new MessageEntity
        {
            SwarmId = this.SwarmId,
            Sender = sender,
            Recipient = recipient,
            Content = content,
        }).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task SaveFileAsync(string path, long sizeBytes)
    {
        await this.repository.SaveFileAsync(new FileEntity
        {
            SwarmId = this.SwarmId,
            Path = path,
            SizeBytes = sizeBytes,
        }).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<bool> IsRetryBudgetExhaustedAsync(int maxRetries)
    {
        var tasks = await this.repository.GetTasksAsync(this.SwarmId).ConfigureAwait(false);
        var failed = tasks
            .Where(t => string.Equals(t.State, nameof(SwarmInstanceState.Failed), StringComparison.Ordinal)
                     || string.Equals(t.State, "Failed", StringComparison.Ordinal))
            .ToList();

        if (failed.Count == 0)
        {
            // Nothing Failed to retry — budget is irrelevant. Treat as "not
            // exhausted" so the orchestrator keeps its normal flow (the
            // orchestrator shouldn't have suspended in the first place, but
            // if it did we don't escalate on an empty failure set).
            return false;
        }

        return failed.All(t => t.RetryCount >= maxRetries);
    }

    /// <inheritdoc/>
    public async Task<SwarmInstanceState?> GetPersistedStateAsync(Guid swarmId)
    {
        var entity = await this.repository.GetSwarmAsync(swarmId).ConfigureAwait(false);
        if (entity is null)
        {
            return null;
        }

        return Enum.TryParse<SwarmInstanceState>(entity.State, ignoreCase: false, out var parsed)
            ? parsed
            : SwarmInstanceState.Created;
    }

    /// <inheritdoc/>
    public async Task LoadAsync(Guid swarmId)
    {
        // After the F01.3 cache kill, LoadAsync is strictly runtime-state
        // restoration. It refreshes the in-memory SwarmState header (so
        // subsequent UpdateRoundAsync / SwarmId-driven reads address the
        // right swarm), repopulates the TeamRegistry + InboxSystem (the
        // bits that ARE process-local — agent rosters and message
        // queues), and replays persisted messages. Tasks are no longer
        // hydrated into a cache; every consumer that needs the task list
        // calls GetTasksAsync, which reads through to the repository.
        var (swarm, _, agents, messages) = await this.repository
            .LoadSwarmStateAsync(swarmId).ConfigureAwait(false);

        if (swarm is null)
        {
            throw new InvalidOperationException($"Swarm {swarmId} not found in repository.");
        }

        await this.InboxSystem.ClearAsync().ConfigureAwait(false);
        await this.TeamRegistry.ClearAsync().ConfigureAwait(false);

        // Restore swarm state. Phase is sourced from the new State column
        // (the state-machine single source of truth). During the migration
        // window some rows may have an empty State — fall back to Created.
        var parsedState = Enum.TryParse<SwarmInstanceState>(swarm.State, ignoreCase: false, out var stateEnum)
            ? stateEnum
            : SwarmInstanceState.Created;

        this.State = new SwarmState
        {
            SwarmId = swarm.Id,
            Goal = swarm.Goal,
            TemplateKey = swarm.TemplateKey,
            State = parsedState,
            RoundNumber = swarm.CurrentRound,
        };

        // Restore agents.
        foreach (var agentEntity in agents)
        {
            var agentInfo = new AgentInfo
            {
                Name = agentEntity.Name,
                Role = agentEntity.Role,
                DisplayName = agentEntity.DisplayName,
                Status = Enum.Parse<AgentStatus>(agentEntity.Status, ignoreCase: true),
                TasksCompleted = agentEntity.TasksCompleted,
            };

            await this.TeamRegistry.RegisterAsync(agentInfo).ConfigureAwait(false);
            this.InboxSystem.RegisterAgent(agentInfo.Name);
        }

        // Register the well-known 'leader' inbox before replay. Worker
        // messages routinely target `leader`, and in a fresh-run flow it
        // gets registered inside SwarmOrchestrator.SpawnAsync — a phase
        // the resume-aware RunAsync skips for swarms already past
        // Spawning. Without this line the first replayed worker→leader
        // message throws "Recipient 'leader' is not registered" and the
        // resume fails before ExecuteAsync ever gets control.
        this.InboxSystem.RegisterAgent("leader");

        // Replay messages for crash recovery so agents have their inbox state.
        foreach (var msg in messages)
        {
            await this.InboxSystem.SendAsync(msg.Sender, msg.Recipient, msg.Content)
                .ConfigureAwait(false);
        }
    }
}
