using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Swarmwright.Configuration;
using Swarmwright.Events;
using Swarmwright.Events.AgUI;
using Swarmwright.Hosting.StateMachine;
using Swarmwright.Models;
using Swarmwright.Models.Enums;
using Swarmwright.Services;
using Swarmwright.Skills;
using Swarmwright.Telemetry;
using Swarmwright.Templates;
using Swarmwright.Tools;

namespace Swarmwright.Orchestration;

/// <summary>
/// Orchestrates the full swarm lifecycle: planning, spawning, execution, and synthesis.
/// </summary>
[SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Orchestrator catches exceptions to update swarm state and re-throw.")]
public partial class SwarmOrchestrator : ISwarmOrchestrator
{
    /// <summary>
    /// The canonical inbox name registered for the swarm leader. Workers
    /// address the leader exclusively through this name via <c>inbox_send</c>.
    /// </summary>
    public const string LeaderInboxName = "leader";

    private static readonly ActivitySource ActivitySource = new(AgentTelemetry.SwarmActivitySourceName);

    private readonly IChatClient leaderChatClient;
    private readonly Func<string, IChatClient> workerChatClientFactory;
    private readonly ISwarmEventBus eventBus;
    private readonly SwarmEventAdapter agUiAdapter;
    private readonly ISwarmService swarmService;
    private readonly IStateTransitionService stateTransitionService;
    private readonly SwarmOptions options;
    private readonly LoadedTemplate? template;
    private readonly ILogger<SwarmOrchestrator> logger;
    private readonly ILoggerFactory loggerFactory;
    private readonly string workDirectory;
    private readonly HttpClient httpClient;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<AITool>>>? mcpToolLoader;
    private readonly string? templatesDirectory;
    private readonly List<ICustomToolProvider> customToolProviders;
    private readonly Dictionary<string, SwarmAgent> agents = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> agentFlushLocks = new();
    private CancellationTokenSource? cancellationTokenSource;
    private TaskCompletionSource? suspendSignal;
    private bool skipToSynthesis;
    private List<ChatMessage>? synthesisHistory;

    /// <summary>
    /// Initializes a new instance of the <see cref="SwarmOrchestrator"/> class.
    /// </summary>
    /// <param name="leaderChatClient">The chat client for the leader agent.</param>
    /// <param name="workerChatClientFactory">
    /// A factory invoked once per worker when the orchestrator spawns that worker's
    /// <see cref="SwarmAgent"/>. The input is the worker's name (<c>worker.WorkerName</c>);
    /// the output is the <see cref="IChatClient"/> that worker will use — typically an
    /// <c>AgUIEventInterceptor</c> wrapping the shared inner client so per-worker tool
    /// call events reach the AG-UI event adapter scoped to that agent name.
    /// </param>
    /// <param name="eventBus">The event bus for swarm coordination (legacy, kept for backward compatibility).</param>
    /// <param name="agUiAdapter">The AG-UI event adapter for typed event emission.</param>
    /// <param name="swarmService">The swarm service for task and agent management.</param>
    /// <param name="stateTransitionService">The single write surface for swarm and task state changes and their audit rows.</param>
    /// <param name="options">The swarm configuration options.</param>
    /// <param name="template">An optional loaded template for the swarm.</param>
    /// <param name="workDirectory">The per-swarm work directory for file tools.</param>
    /// <param name="httpClient">The HTTP client used by the <c>web_fetch</c> default tool.</param>
    /// <param name="loggerFactory">An optional logger factory for structured logging.</param>
    /// <param name="mcpToolLoader">Optional loader for MCP tools. When non-null, agents that
    /// declare <c>mcp_endpoints</c> in their template have their MCP tools loaded in addition
    /// to coordination + default tools. Null means no MCP (the default for non-MCP deployments).</param>
    /// <param name="templatesDirectory">The root templates directory for skill resolution. Null disables skills.</param>
    /// <param name="customToolProviders">
    /// Optional enumerable of <c>ICustomToolProvider</c> instances resolved from DI. Workers that
    /// declare <c>custom_tools: [...]</c> in frontmatter receive matching tools from these providers.
    /// Null means no custom tools are available.
    /// </param>
    public SwarmOrchestrator(
        IChatClient leaderChatClient,
        Func<string, IChatClient> workerChatClientFactory,
        ISwarmEventBus eventBus,
        SwarmEventAdapter agUiAdapter,
        ISwarmService swarmService,
        IStateTransitionService stateTransitionService,
        SwarmOptions options,
        LoadedTemplate? template,
        string workDirectory,
        HttpClient httpClient,
        ILoggerFactory? loggerFactory = null,
        Func<string, CancellationToken, Task<IReadOnlyList<AITool>>>? mcpToolLoader = null,
        string? templatesDirectory = null,
        IEnumerable<ICustomToolProvider>? customToolProviders = null)
    {
        this.leaderChatClient = leaderChatClient;
        this.workerChatClientFactory = workerChatClientFactory;
        this.eventBus = eventBus;
        this.agUiAdapter = agUiAdapter;
        this.swarmService = swarmService;
        this.stateTransitionService = stateTransitionService;
        this.options = options;
        this.template = template;
        this.workDirectory = workDirectory;
        this.httpClient = httpClient;
        this.mcpToolLoader = mcpToolLoader;
        this.templatesDirectory = templatesDirectory;
        this.customToolProviders = customToolProviders?.ToList() ?? [];

        var factory = loggerFactory ?? NullLoggerFactory.Instance;
        this.logger = factory.CreateLogger<SwarmOrchestrator>();
        this.loggerFactory = factory;
    }

    /// <summary>
    /// Gets the set of worker names currently registered in the
    /// orchestrator-local agent dictionary. Exposed for tests that need to
    /// verify the resume path rebuilt agents without reaching into
    /// <see cref="ExecuteAsync"/>'s dispatch loop.
    /// </summary>
    internal IReadOnlyCollection<string> AgentNames => this.agents.Keys.ToList();

    /// <inheritdoc/>
    public Guid SwarmId { get; private set; }

    /// <inheritdoc/>
    public bool IsCancelled { get; private set; }

    /// <inheritdoc/>
    public async Task<string> RunAsync(Guid swarmId, string goal, CancellationToken cancellationToken = default)
    {
        this.cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = this.cancellationTokenSource.Token;

        // Open the root orchestration span so every Agent Framework child
        // span emitted during planning/spawning/execution/synthesis is parented
        // to a single swarm.run trace the observability MCP tools can pivot on.
        using var activity = ActivitySource.StartActivity(
            AgentTelemetry.SwarmRunActivityName,
            ActivityKind.Internal);
        activity?.SetTag(AgentTelemetry.SwarmIdTagName, swarmId.ToString());
        if (this.template?.Key is { Length: > 0 } templateKey)
        {
            activity?.SetTag(AgentTelemetry.SwarmTemplateTagName, templateKey);
        }

        try
        {
            // Adopt the dispatcher-supplied canonical swarm id so every event,
            // log entry, and database row shares a single identifier end-to-end.
            this.SwarmId = swarmId;
            await this.swarmService.CreateSwarmAsync(this.SwarmId, goal, this.template?.Key).ConfigureAwait(false);
            await this.agUiAdapter.EmitRunStartedAsync(this.SwarmId, goal).ConfigureAwait(false);

            // Inspect persisted state to decide whether this is a fresh run
            // or a resume. A fresh run (or a bare test-mock repo with no
            // configured GetSwarmAsync) returns null and we treat it as
            // Created. A resume returns the phase we left off at, and we
            // skip the earlier phases to avoid re-planning (duplicate tasks)
            // or re-spawning (duplicate agents) and to satisfy the
            // state-machine guard that rejects backward transitions.
            var persistedState = await this.swarmService
                .GetPersistedStateAsync(this.SwarmId)
                .ConfigureAwait(false) ?? SwarmInstanceState.Created;

            if (SwarmStateGuards.IsTerminal(persistedState))
            {
                // Nothing to do; the caller should have filtered this
                // upstream, but be defensive.
                await this.agUiAdapter.EmitRunFinishedAsync(this.SwarmId).ConfigureAwait(false);
                return string.Empty;
            }

            // When resuming past Planning, reload caches from persistence
            // so TeamRegistry / InboxSystem reflect reality.
            // Also rebuild the orchestrator-local agent dict — SpawnAsync
            // is skipped on resume, and ExecuteAsync dispatches by
            // looking up this.agents[workerName]. Without this, Execute
            // silently skips every task and the swarm "completes" with
            // zero LLM calls.
            var resuming = persistedState is not SwarmInstanceState.Created
                and not SwarmInstanceState.Planning;
            if (resuming)
            {
                await this.swarmService.LoadAsync(this.SwarmId).ConfigureAwait(false);

                var persistedTasks = await this.swarmService.GetTasksAsync().ConfigureAwait(false);
                var resumeWorkers = persistedTasks
                    .Select(t => (t.WorkerName, t.WorkerRole))
                    .DistinctBy(w => w.WorkerName);
                await this.RebuildWorkerAgentsAsync(resumeWorkers, persist: false, token).ConfigureAwait(false);
            }

            // Planning phase — only for Created/Planning.
            if (persistedState is SwarmInstanceState.Created or SwarmInstanceState.Planning)
            {
                await this.PlanAsync(goal, token).ConfigureAwait(false);
            }

            token.ThrowIfCancellationRequested();

            // Spawning phase — for Created/Planning/Spawning.
            if (persistedState is SwarmInstanceState.Created
                or SwarmInstanceState.Planning
                or SwarmInstanceState.Spawning)
            {
                await this.SpawnAsync(token).ConfigureAwait(false);
            }

            token.ThrowIfCancellationRequested();

            // Execution phase — for everything up to (but not including) Synthesizing.
            if (persistedState is not SwarmInstanceState.Synthesizing)
            {
                await this.ExecuteAsync(token).ConfigureAwait(false);
            }

            token.ThrowIfCancellationRequested();

            // Synthesis phase — always.
            var report = await this.SynthesizeAsync(token).ConfigureAwait(false);

            await this.stateTransitionService.TransitionSwarmAsync(
                this.SwarmId,
                SwarmInstanceState.Complete,
                TransitionReasons.RunCompleted,
                actor: "system",
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
            await this.agUiAdapter.EmitStepFinishedAsync(nameof(SwarmInstanceState.Synthesizing)).ConfigureAwait(false);
            await this.agUiAdapter.EmitRunFinishedAsync(this.SwarmId).ConfigureAwait(false);
            this.LogSwarmComplete(this.SwarmId);

            return report;
        }
        catch (OperationCanceledException) when (this.IsCancelled)
        {
            // Clean up orphan in-flight tasks BEFORE the swarm-level terminal
            // transition so the audit trail shows tasks cleaning up first,
            // swarm flipping terminal last (defense-in-depth § Layer 1a).
            await this.FailInFlightTasksAsync(
                this.SwarmId,
                TransitionReasons.UserCancel,
                note: "Swarm cancelled mid-run; worker did not complete",
                CancellationToken.None).ConfigureAwait(false);

            await this.stateTransitionService.TransitionSwarmAsync(
                this.SwarmId,
                SwarmInstanceState.Cancelled,
                TransitionReasons.UserCancel,
                actor: "system",
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
            await this.agUiAdapter.EmitRunErrorAsync(this.SwarmId, "CANCELLED", "Swarm was cancelled").ConfigureAwait(false);
            this.LogSwarmCancelled(this.SwarmId);
            throw;
        }
        catch (Exception ex)
        {
            // Clean up orphan in-flight tasks BEFORE the swarm-level Failed
            // transition (defense-in-depth § Layer 1a). Without this the
            // Completed demo bug from 2026-04-23 left cost-expert stuck at
            // InProgress indefinitely because the catch only wrote the
            // swarm-level row.
            await this.FailInFlightTasksAsync(
                this.SwarmId,
                TransitionReasons.RunFailed,
                note: "Swarm crashed mid-run; task was in flight",
                CancellationToken.None).ConfigureAwait(false);

            await this.stateTransitionService.TransitionSwarmAsync(
                this.SwarmId,
                SwarmInstanceState.Failed,
                TransitionReasons.RunFailed,
                actor: "system",
                note: ex.Message,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
            await this.agUiAdapter.EmitRunErrorAsync(this.SwarmId, "SWARM_FAILED", ex.Message).ConfigureAwait(false);
            this.LogSwarmFailed(this.SwarmId, ex);
            throw;
        }
        finally
        {
            // Persist conversation histories for refinement chat on every
            // exit path (success, cancel, crash). Per-task flushes inside
            // TerminateTaskAsync already captured individual agents, but
            // synthesis history and agents.json are only written here.
            // PersistConversationHistoriesAsync swallows its own exceptions
            // so a flush failure cannot shadow the original crash or
            // bubble out of finally.
            await this.PersistConversationHistoriesAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public Task CancelAsync()
    {
        this.IsCancelled = true;
        this.cancellationTokenSource?.Cancel();
        this.suspendSignal?.TrySetResult();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public void SignalContinue()
    {
        this.suspendSignal?.TrySetResult();
    }

    /// <inheritdoc/>
    public void SignalSkip()
    {
        this.skipToSynthesis = true;
        this.suspendSignal?.TrySetResult();
    }

    private async Task PlanAsync(string goal, CancellationToken cancellationToken)
    {
        this.LogPhaseChanged(nameof(SwarmInstanceState.Planning), this.SwarmId);
        await this.stateTransitionService.TransitionSwarmAsync(
            this.SwarmId,
            SwarmInstanceState.Planning,
            TransitionReasons.PhaseAdvanced,
            actor: "system",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await this.agUiAdapter.EmitStepStartedAsync(nameof(SwarmInstanceState.Planning)).ConfigureAwait(false);
        await this.agUiAdapter.EmitStateDeltaAsync(
            JsonSerializer.SerializeToElement(new[] { new { op = "replace", path = "/phase", value = nameof(SwarmInstanceState.Planning) } })).ConfigureAwait(false);

        var (planTool, planSource) = LeaderToolFactory.CreatePlanTool();

        var goalMessage = GoalTemplateExpander.Expand(this.template?.GoalTemplate, goal);

        List<ChatMessage> messages =
        [
            new(ChatRole.System, PromptBuilder.ForPlanning(this.template)),
            new(ChatRole.User, goalMessage),
        ];

        var chatOptions = new ChatOptions
        {
            Tools = [planTool],
        };

        // Log the full prompt being sent
        var systemPrompt = messages[0].Text ?? "(null)";
        this.LogLlmPromptSent("Planning", "system", systemPrompt);
        this.LogLlmPromptSent("Planning", "user", goalMessage);
        this.LogLlmRequest(nameof(SwarmInstanceState.Planning), "leader");
        var response = await this.leaderChatClient.GetResponseAsync(
            messages,
            chatOptions,
            cancellationToken).ConfigureAwait(false);
        this.LogLlmResponse("Planning", "leader", response.Messages.Count);

        // Log what the LLM actually returned — text AND tool calls
        foreach (var msg in response.Messages)
        {
            this.LogLlmMessageContent("Planning", msg.Role.Value, msg.Text ?? "(no text)");
            foreach (var content in msg.Contents)
            {
                if (content is FunctionCallContent fc)
                {
                    var args = JsonSerializer.Serialize(fc.Arguments);
                    this.LogLlmToolCall("Planning", fc.Name, args);
                }
                else
                {
                    var typeName = content.GetType().Name;
                    var value = content.ToString() ?? "(null)";
                    this.LogLlmContentItem("Planning", typeName, value);
                }
            }
        }

        // Await the plan from the TCS (with timeout to prevent infinite hang if model didn't call create_plan).
        if (planSource.Task.IsCompleted)
        {
            this.LogPlanToolCalled();
        }
        else
        {
            this.LogPlanToolNotCalled();
        }

        var plan = await planSource.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);

        // Convert BlockedByIndices to task IDs and add tasks to swarm service.
        var taskIds = new List<string>();
        foreach (var taskPlan in plan.Tasks)
        {
            var swarmTask = new SwarmTask
            {
                Id = Guid.NewGuid().ToString("N"),
                Subject = taskPlan.Subject,
                Description = taskPlan.Description,
                WorkerRole = taskPlan.WorkerRole,
                WorkerName = taskPlan.WorkerName,
            };

            taskIds.Add(swarmTask.Id);
            foreach (var id in BuildBlockedByList(taskPlan, taskIds))
            {
                swarmTask.BlockedBy.Add(id);
            }

            await this.swarmService.AddTaskAsync(swarmTask).ConfigureAwait(false);
        }

        this.LogPlanCreated(plan.Tasks.Count, this.SwarmId);
        await this.agUiAdapter.EmitStepFinishedAsync(nameof(SwarmInstanceState.Planning)).ConfigureAwait(false);

        // Emit state snapshot with all tasks after planning
        var planTasks = await this.swarmService.GetTasksAsync().ConfigureAwait(false);
        var planSnapshot = JsonSerializer.SerializeToElement(
            new
            {
                phase = nameof(SwarmInstanceState.Spawning),
                roundNumber = 0,
                tasks = planTasks,
                agents = Array.Empty<object>(),
                messages = Array.Empty<object>(),
            },
            SwarmJsonOptions.Default);
        await this.agUiAdapter.EmitStateSnapshotAsync(planSnapshot).ConfigureAwait(false);
    }

    private async Task SpawnAsync(CancellationToken cancellationToken)
    {
        this.LogPhaseChanged(nameof(SwarmInstanceState.Spawning), this.SwarmId);
        await this.stateTransitionService.TransitionSwarmAsync(
            this.SwarmId,
            SwarmInstanceState.Spawning,
            TransitionReasons.PhaseAdvanced,
            actor: "system",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await this.agUiAdapter.EmitStepStartedAsync(nameof(SwarmInstanceState.Spawning)).ConfigureAwait(false);

        // Register the canonical leader inbox before any worker so workers can
        // address the leader via `inbox_send` with the well-known name
        // (SwarmOrchestrator.LeaderInboxName). Without this, worker calls to
        // `inbox_send("leader", ...)` would throw "Recipient 'leader' is not registered".
        this.swarmService.InboxSystem.RegisterAgent(LeaderInboxName);
        this.LogLeaderInboxRegistered(LeaderInboxName);

        var tasks = await this.swarmService.GetTasksAsync().ConfigureAwait(false);
        var uniqueWorkers = tasks
            .Select(t => (t.WorkerName, t.WorkerRole))
            .DistinctBy(w => w.WorkerName)
            .ToList();

        await this.RebuildWorkerAgentsAsync(uniqueWorkers, persist: true, cancellationToken).ConfigureAwait(false);

        await this.agUiAdapter.EmitStepFinishedAsync(nameof(SwarmInstanceState.Spawning)).ConfigureAwait(false);

        // Emit state snapshot with all tasks + agents after spawning
        var spawnTasks = await this.swarmService.GetTasksAsync().ConfigureAwait(false);
        var agentList = this.agents.Values.Select(a => new { name = a.Name, role = a.Role, displayName = a.DisplayName }).ToArray();
        var spawnSnapshot = JsonSerializer.SerializeToElement(
            new
            {
                phase = nameof(SwarmInstanceState.Executing),
                roundNumber = 0,
                tasks = spawnTasks,
                agents = agentList,
                messages = Array.Empty<object>(),
            },
            SwarmJsonOptions.Default);
        await this.agUiAdapter.EmitStateSnapshotAsync(spawnSnapshot).ConfigureAwait(false);
    }

    /// <summary>
    /// Constructs a <see cref="SwarmAgent"/> for each unique worker in
    /// <paramref name="workers"/> and stores it in the orchestrator-local
    /// agent dictionary. Shared by <see cref="SpawnAsync"/> (fresh runs,
    /// <paramref name="persist"/> = true — also writes the DB row and
    /// emits the UI event) and the resume branch in <see cref="RunAsync"/>
    /// (evicted-swarm wake-ups, <paramref name="persist"/> = false —
    /// agents already exist in the DB and were restored into TeamRegistry
    /// by <see cref="ISwarmService.LoadAsync"/>, so we just need
    /// the in-memory <see cref="SwarmAgent"/> objects so
    /// <see cref="ExecuteAsync"/> can dispatch tasks).
    /// </summary>
    /// <param name="workers">The unique worker-name / worker-role pairs to rebuild.</param>
    /// <param name="persist">When <see langword="true"/>, also writes the agent row via <see cref="ISwarmService.RegisterAgentAsync"/> and emits <c>SWARM_AGENT_SPAWNED</c>. When <see langword="false"/>, rebuilds in-memory only.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task representing the asynchronous work.</returns>
    internal async Task RebuildWorkerAgentsAsync(
        IEnumerable<(string WorkerName, string WorkerRole)> workers,
        bool persist = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workers);

        foreach (var worker in workers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chatClient = this.workerChatClientFactory(worker.WorkerName);
            var agentDef = this.template?.Agents.FirstOrDefault(a => a.Name == worker.WorkerName);
            var workerTools = await SwarmToolFactory.CreateWorkerToolsAsync(
                worker.WorkerName,
                this.swarmService,
                this.eventBus,
                this.agUiAdapter,
                this.workDirectory,
                this.httpClient,
                this.template,
                agentDef,
                this.mcpToolLoader,
                cancellationToken).ConfigureAwait(false);

            // Resolve skills for this worker.
            ISkillsProvider skillsProvider = NullSkillsProvider.Instance;
            if (this.templatesDirectory is not null && this.template is not null && agentDef?.Skills is { Count: > 0 })
            {
                var skillLoader = new FileSkillLoader(this.templatesDirectory, this.loggerFactory);
                var skills = skillLoader.LoadForWorker(this.template.Key, agentDef.Skills);
                if (skills.Count > 0)
                {
                    skillsProvider = new SkillsProvider(skills, this.template.AllowSkillScripts);
                }
            }

            foreach (var skillTool in skillsProvider.GetTools())
            {
                workerTools.Add(skillTool);
            }

            // Resolve custom tools from registered providers, filtered by the worker's custom_tools allowlist.
            if (agentDef?.CustomTools is { Count: > 0 } allowedCustomToolNames && this.customToolProviders.Count > 0)
            {
                var allowed = new HashSet<string>(allowedCustomToolNames, StringComparer.Ordinal);
                var matched = new HashSet<string>(StringComparer.Ordinal);

                foreach (var provider in this.customToolProviders)
                {
                    foreach (var tool in provider.GetTools())
                    {
                        if (allowed.Contains(tool.Name))
                        {
                            workerTools.Add(tool);
                            matched.Add(tool.Name);
                        }
                    }
                }

                foreach (var requested in allowed)
                {
                    if (!matched.Contains(requested))
                    {
                        this.LogCustomToolNotFound(requested, worker.WorkerName);
                    }
                }
            }

            var displayName = agentDef?.DisplayName ?? worker.WorkerName;
            var skillsFragment = skillsProvider.GetPromptFragment();
            var systemPrompt = PromptBuilder.ForWorker(
                this.template?.SystemPreamble,
                this.workDirectory,
                displayName,
                worker.WorkerRole,
                agentDef?.PromptTemplate,
                skillsFragment);
            var systemPromptCore = PromptBuilder.ForWorkerCore(
                this.template?.SystemPreamble,
                this.workDirectory,
                displayName,
                worker.WorkerRole,
                agentDef?.PromptTemplate);

            var agent = new SwarmAgent(
                worker.WorkerName,
                worker.WorkerRole,
                displayName,
                systemPrompt,
                workerTools,
                chatClient,
                this.loggerFactory.CreateLogger<SwarmAgent>(),
                systemPromptCore);

            this.agents[worker.WorkerName] = agent;
            this.LogAgentSpawned(worker.WorkerName, worker.WorkerRole);

            if (persist)
            {
                var agentInfo = new AgentInfo
                {
                    Name = worker.WorkerName,
                    Role = worker.WorkerRole,
                    DisplayName = displayName,
                    Status = AgentStatus.Idle,
                };
                await this.swarmService.RegisterAgentAsync(agentInfo).ConfigureAwait(false);
                await this.agUiAdapter.EmitCustomAsync(
                    "SWARM_AGENT_SPAWNED",
                    JsonSerializer.SerializeToElement(
                        new { name = worker.WorkerName, role = worker.WorkerRole, displayName },
                        SwarmJsonOptions.Default)).ConfigureAwait(false);
            }
        }
    }

    private async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        this.LogPhaseChanged(nameof(SwarmInstanceState.Executing), this.SwarmId);
        await this.stateTransitionService.TransitionSwarmAsync(
            this.SwarmId,
            SwarmInstanceState.Executing,
            TransitionReasons.PhaseAdvanced,
            actor: "system",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await this.agUiAdapter.EmitStepStartedAsync(nameof(SwarmInstanceState.Executing)).ConfigureAwait(false);

        for (var round = 1; round <= this.options.MaxRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (this.skipToSynthesis)
            {
                break;
            }

            await this.swarmService.UpdateRoundAsync(round).ConfigureAwait(false);
            await this.agUiAdapter.EmitStateDeltaAsync(
                JsonSerializer.SerializeToElement(new[] { new { op = "replace", path = "/roundNumber", value = round } })).ConfigureAwait(false);

            var runnableTasks = await this.swarmService.GetRunnableTasksAsync().ConfigureAwait(false);
            this.LogRoundStarted(round, runnableTasks.Count);
            if (runnableTasks.Count == 0)
            {
                // Check if all tasks are done.
                var allTasks = await this.swarmService.GetTasksAsync().ConfigureAwait(false);
                var allComplete = allTasks.All(t =>
                    t.Status is TaskState.Completed or TaskState.Failed);

                if (allComplete)
                {
                    break;
                }

                // Some tasks are still blocked — enter suspend wait.
                var remainingTasks = allTasks.Count(t =>
                    t.Status is not TaskState.Completed and not TaskState.Failed);
                this.LogSuspendWait(remainingTasks);
                await this.EnterSuspendWaitAsync(cancellationToken).ConfigureAwait(false);

                if (this.IsCancelled || this.skipToSynthesis)
                {
                    break;
                }

                continue;
            }

            // Group tasks by worker, respecting maxInstances.
            var tasksByWorker = runnableTasks
                .GroupBy(t => t.WorkerName)
                .ToList();

            var workerTasks = new List<Task>();
            foreach (var workerGroup in tasksByWorker)
            {
                var workerName = workerGroup.Key;
                if (!this.agents.TryGetValue(workerName, out var agent))
                {
                    continue;
                }

                var agentDef = this.template?.Agents.FirstOrDefault(a => a.Name == workerName);
                var maxInstances = agentDef?.MaxInstances ?? 1;
                var tasksToRun = workerGroup
                    .Where(t => t.Status == TaskState.Pending)
                    .Take(maxInstances)
                    .ToList();

                foreach (var swarmTask in tasksToRun)
                {
                    // F01.3: state service is the sole writer. The legacy
                    // dual-write to swarmService.UpdateTaskStatusAsync is
                    // gone — there's no replicated cache to keep in sync.
                    await this.stateTransitionService.TransitionTaskAsync(
                        this.SwarmId,
                        swarmTask.Id,
                        TaskState.InProgress,
                        TransitionReasons.PhaseAdvanced,
                        actor: "system",
                        cancellationToken: cancellationToken).ConfigureAwait(false);

                    workerTasks.Add(this.ExecuteWorkerTaskAsync(agent, swarmTask, cancellationToken));
                }
            }

            await Task.WhenAll(workerTasks).ConfigureAwait(false);
            this.LogRoundCompleted(round);

            // Check if all tasks are done after this round.
            var allTasksAfterRound = await this.swarmService.GetTasksAsync().ConfigureAwait(false);
            var allDone = allTasksAfterRound.All(t =>
                t.Status is TaskState.Completed or TaskState.Failed);

            if (allDone)
            {
                break;
            }
        }
    }

    internal async Task ExecuteWorkerTaskAsync(
        SwarmAgent agent,
        SwarmTask swarmTask,
        CancellationToken cancellationToken)
    {
        try
        {
            var execution = await agent.ExecuteTaskAsync(swarmTask, cancellationToken).ConfigureAwait(false);

            if (execution.WorkerDeclaredStatus == TaskState.Completed)
            {
                this.LogTaskCompleted(swarmTask.Id, agent.Name);
                await this.TerminateTaskAsync(
                    agent,
                    swarmTask,
                    TaskState.Completed,
                    result: execution.WorkerDeclaredResult ?? execution.FinalText,
                    transitionReason: TransitionReasons.PhaseAdvanced,
                    transitionNote: null,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else if (execution.WorkerDeclaredStatus == TaskState.Failed)
            {
                this.LogTaskFailed(swarmTask.Id, agent.Name, execution.WorkerDeclaredResult ?? "Worker reported failure.");
                await this.TerminateTaskAsync(
                    agent,
                    swarmTask,
                    TaskState.Failed,
                    result: execution.WorkerDeclaredResult ?? execution.FinalText,
                    transitionReason: TransitionReasons.TaskFailed,
                    transitionNote: execution.WorkerDeclaredResult,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else
            {
                this.LogWorkerDidNotSignalTaskUpdate(swarmTask.Id, agent.Name);
                await this.TerminateTaskAsync(
                    agent,
                    swarmTask,
                    TaskState.Failed,
                    result: "Worker did not call task_update; one-shot execution without completion signal.",
                    transitionReason: TransitionReasons.TaskFailed,
                    transitionNote: "Worker did not call task_update.",
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            this.LogTaskFailed(swarmTask.Id, agent.Name, ex.Message);
            await this.TerminateTaskAsync(
                agent,
                swarmTask,
                TaskState.Failed,
                result: ex.Message,
                transitionReason: TransitionReasons.TaskFailed,
                transitionNote: ex.Message,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Translates a leader-supplied <see cref="TaskPlan.BlockedByIndices"/> list into
    /// the concrete <see cref="SwarmTask.BlockedBy"/> task-id list that gets persisted
    /// to <c>BlockedByJson</c>. The bounds check <c>&lt; taskIdsSoFar.Count - 1</c> is
    /// intentional — the current task's id has already been appended to
    /// <paramref name="taskIdsSoFar"/> immediately before this call, so the <c>-1</c>
    /// excludes self-blocking.
    /// </summary>
    /// <param name="taskPlan">The plan entry whose dependencies are being wired.</param>
    /// <param name="taskIdsSoFar">The running list of task ids in plan order, including the current task as the last element.</param>
    /// <returns>The dependency task-id list to attach to the current task.</returns>
    internal static IReadOnlyList<string> BuildBlockedByList(
        TaskPlan taskPlan,
        IReadOnlyList<string> taskIdsSoFar)
    {
        ArgumentNullException.ThrowIfNull(taskPlan);
        ArgumentNullException.ThrowIfNull(taskIdsSoFar);

        var result = new List<string>();
        foreach (var blockedIndex in taskPlan.BlockedByIndices.Distinct())
        {
            if (blockedIndex >= 0 && blockedIndex < taskIdsSoFar.Count - 1)
            {
                result.Add(taskIdsSoFar[blockedIndex]);
            }
        }

        return result;
    }

    /// <summary>
    /// Re-reads swarm state (agents, messages) from the database into the
    /// in-memory caches. Used by the suspend-wake path so that a recovery
    /// action the intervention handler wrote while the orchestrator was asleep
    /// is visible to the next round's <c>GetRunnableTasksAsync</c>.
    /// </summary>
    /// <remarks>
    /// Without this, an already-running orchestrator that is signalled out of
    /// suspend wait re-enters the round loop reading stale in-memory state —
    /// so an orphan reset (InProgress → Pending) written by
    /// <c>ContinueAsync</c> never surfaces as a runnable task and the swarm
    /// drops back into suspend immediately.
    /// <see cref="ISwarmService.LoadAsync"/> clears and repopulates
    /// the in-memory caches from the repository, which is the same code path
    /// the initial resume branch of <c>RunAsync</c> uses.
    /// </remarks>
    /// <returns>A task representing the asynchronous reload.</returns>
    internal Task ReloadFromDatabaseAsync()
    {
        return this.swarmService.LoadAsync(this.SwarmId);
    }

    /// <summary>
    /// Walks the task board and transitions every task currently in
    /// <see cref="TaskState.InProgress"/> to <see cref="TaskState.Failed"/>.
    /// Called from the crash and cancellation catch blocks in
    /// <see cref="RunAsync(Guid, string, CancellationToken)"/> so that an
    /// orchestrator that exits abnormally does not leave orphan in-flight rows
    /// in the database — those rows would otherwise confuse the recommendation
    /// surface on the next recovery action.
    /// </summary>
    /// <remarks>
    /// Task-level only. The caller (the catch block) owns writing the
    /// swarm-level terminal transition, and must call this helper FIRST so
    /// that <c>swarm_state_transitions</c> shows the swarm going terminal
    /// after its tasks finished cleaning up — matching how an operator reads
    /// "what happened last." <c>retryCountDelta</c> is always zero: the
    /// worker did not get a chance to run to completion, so it is not fair
    /// to charge retry budget for a crash or cancellation.
    /// </remarks>
    /// <param name="swarmId">The owning swarm identifier.</param>
    /// <param name="reason">The <see cref="TransitionReasons"/> string — typically <see cref="TransitionReasons.RunFailed"/> for crashes or <see cref="TransitionReasons.UserCancel"/> for cancellations.</param>
    /// <param name="note">Operator-readable note forwarded to the audit row; distinguishes crash vs cancel in the trail.</param>
    /// <param name="cancellationToken">Cancellation token forwarded to the state service.</param>
    /// <returns>A task representing the asynchronous cleanup.</returns>
    internal async Task FailInFlightTasksAsync(
        Guid swarmId,
        string reason,
        string? note,
        CancellationToken cancellationToken)
    {
        // Defensive against a swarm service that hasn't populated the task
        // board yet — happens when the orchestrator crashes before planning
        // completes (tests do this by throwing from CreateSwarmAsync).
        IReadOnlyList<SwarmTask>? tasks;
        try
        {
            tasks = await this.swarmService.GetTasksAsync().ConfigureAwait(false);
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

        foreach (var task in tasks.Where(t => t.Status == TaskState.InProgress))
        {
            await this.stateTransitionService.TransitionTaskAsync(
                swarmId,
                task.Id,
                TaskState.Failed,
                reason,
                actor: "system",
                retryCountDelta: 0,
                note: note,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Transitions a task to its terminal state and flushes the agent's
    /// conversation artifacts so diagnostic traces are visible even when
    /// the swarm crashes before synthesis. Called from every terminal
    /// site in <see cref="ExecuteWorkerTaskAsync"/>. Serializes flushes
    /// per-agent via <see cref="agentFlushLocks"/> so concurrent callers
    /// produce newer-wins semantics without tmp-file collisions. Flush
    /// failures are logged via <see cref="LogConversationHistoryPersistFailed"/>
    /// and swallowed so the caller's primary transition contract is
    /// preserved.
    /// </summary>
    /// <param name="agent">The agent whose task just terminated.</param>
    /// <param name="swarmTask">The task being transitioned.</param>
    /// <param name="newState">The terminal state to record (Completed or Failed).</param>
    /// <param name="result">The task's final result text.</param>
    /// <param name="transitionReason">The <see cref="TransitionReasons"/> value for the audit row.</param>
    /// <param name="transitionNote">An optional note explaining the transition.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task TerminateTaskAsync(
        SwarmAgent agent,
        SwarmTask swarmTask,
        TaskState newState,
        string result,
        string transitionReason,
        string? transitionNote,
        CancellationToken cancellationToken)
    {
        // F01.3: state service is the sole writer. Result text rides
        // along on the same call so the worker's final report lands on
        // the DB row in one transaction; no replicated cache to keep in
        // sync.
        await this.stateTransitionService.TransitionTaskAsync(
            this.SwarmId,
            swarmTask.Id,
            newState,
            transitionReason,
            actor: "system",
            note: transitionNote,
            result: result,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var semaphore = this.agentFlushLocks.GetOrAdd(agent.Name, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await this.PersistAgentConversationAsync(agent).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.LogConversationHistoryPersistFailed(this.SwarmId, ex);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task EnterSuspendWaitAsync(CancellationToken cancellationToken)
    {
        await this.stateTransitionService.TransitionSwarmAsync(
            this.SwarmId,
            SwarmInstanceState.AwaitingIntervention,
            TransitionReasons.TaskFailed,
            actor: "system",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await this.agUiAdapter.EmitStateDeltaAsync(
            JsonSerializer.SerializeToElement(new[] { new { op = "replace", path = "/phase", value = nameof(SwarmInstanceState.AwaitingIntervention) } })).ConfigureAwait(false);

        // Budget-exhaustion escalation: if every Failed task has already
        // burned through its MaxTaskRetries budget, flip to NeedsDiagnosis
        // so the /continue endpoint returns 409 and the UI disables the
        // Continue button (Smart Continue remains the only recovery path).
        var budgetExhausted = await this.swarmService
            .IsRetryBudgetExhaustedAsync(this.options.MaxTaskRetries)
            .ConfigureAwait(false);
        if (budgetExhausted)
        {
            await this.stateTransitionService.TransitionSwarmAsync(
                this.SwarmId,
                SwarmInstanceState.NeedsDiagnosis,
                TransitionReasons.BudgetExhausted,
                actor: "system",
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        this.suspendSignal = new TaskCompletionSource();

        using var timeoutCts = new CancellationTokenSource(
            TimeSpan.FromSeconds(this.options.SuspendTimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);

        try
        {
            await this.suspendSignal.Task.WaitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Suspend timeout — skip to synthesis.
            this.skipToSynthesis = true;
        }

        // Resume execution phase if not skipping.
        if (!this.skipToSynthesis && !this.IsCancelled)
        {
            // Defense-in-depth Fix B: re-read agent/message state from the
            // DB before the next round. While the orchestrator was asleep the
            // intervention handler may have written transitions (orphan resets,
            // Failed → Pending retries, added or abandoned tasks). The in-memory
            // caches are stale from the initial LoadAsync at RunAsync startup;
            // without this refresh, in-memory state mismatches the DB and the
            // suspend-wake path drops back into suspend immediately.
            await this.ReloadFromDatabaseAsync().ConfigureAwait(false);

            await this.stateTransitionService.TransitionSwarmAsync(
                this.SwarmId,
                SwarmInstanceState.Executing,
                TransitionReasons.UserContinue,
                actor: "system",
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string> SynthesizeAsync(CancellationToken cancellationToken)
    {
        this.LogPhaseChanged(nameof(SwarmInstanceState.Synthesizing), this.SwarmId);
        await this.stateTransitionService.TransitionSwarmAsync(
            this.SwarmId,
            SwarmInstanceState.Synthesizing,
            TransitionReasons.PhaseAdvanced,
            actor: "system",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await this.agUiAdapter.EmitStepFinishedAsync(nameof(SwarmInstanceState.Executing)).ConfigureAwait(false);
        await this.agUiAdapter.EmitStepStartedAsync(nameof(SwarmInstanceState.Synthesizing)).ConfigureAwait(false);

        var allTasks = await this.swarmService.GetTasksAsync().ConfigureAwait(false);
        var taskResults = allTasks
            .Where(t => t.Status == TaskState.Completed)
            .Select(t => $"Task: {t.Subject}\nResult: {t.Result}")
            .ToList();

        var synthesisPrompt = this.template?.SynthesisPrompt
            ?? "You are the swarm leader. Synthesize the following task results into a comprehensive report using the submit_report tool.";

        var resultsBlock = string.Join("\n\n", taskResults);

        var (reportTool, reportSource) = LeaderToolFactory.CreateReportTool();

        List<ChatMessage> messages =
        [
            new(ChatRole.System, synthesisPrompt),
            new(ChatRole.User, $"Task results:\n\n{resultsBlock}"),
        ];

        var chatOptions = new ChatOptions
        {
            Tools = [reportTool],
        };

        this.LogLlmRequest(nameof(SwarmInstanceState.Synthesizing), "leader");
        await this.leaderChatClient.GetResponseAsync(
            messages,
            chatOptions,
            cancellationToken).ConfigureAwait(false);

        this.synthesisHistory = messages;

        var report = await reportSource.Task.ConfigureAwait(false);

        // Persist the synthesis report to the swarm work directory as a standard
        // artifact. This guarantees every successful swarm run produces at least
        // one file (the report itself) retrievable via GET /api/swarm/{id}/artifacts
        // without depending on whether individual workers happened to call the
        // write tool. Best-effort: a write failure is logged but does not fail
        // the phase — the report is still in the return value.
        try
        {
            if (!Directory.Exists(this.workDirectory))
            {
                Directory.CreateDirectory(this.workDirectory);
            }

            var reportPath = Path.Combine(this.workDirectory, "synthesis-report.md");
            await File.WriteAllTextAsync(reportPath, report, cancellationToken).ConfigureAwait(false);
            this.LogSynthesisReportWritten(reportPath, this.SwarmId);
        }
        catch (IOException ex)
        {
            this.LogSynthesisReportWriteFailed(this.SwarmId, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            this.LogSynthesisReportWriteFailed(this.SwarmId, ex);
        }

        this.LogSynthesisComplete(this.SwarmId);
        return report;
    }

    /// <summary>
    /// Persists every agent's conversation history, the synthesis history,
    /// and agent metadata to disk so the refinement chat can load them
    /// after swarm eviction. Best-effort: failures are logged but do not
    /// fail the swarm. Called at end-of-run as a "last word" flush; the
    /// per-task terminal flush now keeps artifacts fresh mid-run via
    /// <see cref="PersistAgentConversationAsync(SwarmAgent)"/>.
    /// </summary>
    internal async Task PersistConversationHistoriesAsync()
    {
        try
        {
            foreach (var agent in this.agents.Values)
            {
                await this.PersistAgentConversationAsync(agent).ConfigureAwait(false);
            }

            await this.PersistSynthesisConversationAsync().ConfigureAwait(false);

            var chatDir = Path.Combine(this.workDirectory, ".chat");
            Directory.CreateDirectory(chatDir);
            var agentMetadata = this.agents.Values
                .Select(a => new { name = a.Name, role = a.Role, displayName = a.DisplayName })
                .ToList();
            var metadataJson = JsonSerializer.Serialize(agentMetadata, SwarmJsonOptions.Default);
            await File.WriteAllTextAsync(Path.Combine(chatDir, "agents.json"), metadataJson).ConfigureAwait(false);

            await this.PersistTaskOutputsIndexAsync().ConfigureAwait(false);

            this.LogConversationHistoriesPersisted(this.agents.Count, this.SwarmId);
        }
        catch (Exception ex)
        {
            this.LogConversationHistoryPersistFailed(this.SwarmId, ex);
        }
    }

    /// <summary>
    /// Persists a single agent's JSONL conversation history and the
    /// driver-prompt snapshot (<c>.chat/{name}.system.md</c>). Safe to call
    /// mid-run from the per-task terminal flush. The
    /// <see cref="ConversationHistorySerializer"/> provides per-call
    /// atomicity via write-temp + rename.
    /// </summary>
    /// <param name="agent">The agent whose conversation history to persist.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    internal async Task PersistAgentConversationAsync(SwarmAgent agent)
    {
        var chatDir = Path.Combine(this.workDirectory, ".chat");
        Directory.CreateDirectory(chatDir);

        var agentPath = Path.Combine(chatDir, $"{agent.Name}.jsonl");
        await ConversationHistorySerializer.SerializeAsync(agentPath, agent.ConversationHistory).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(agent.SystemPromptCore))
        {
            var systemPath = Path.Combine(chatDir, $"{agent.Name}.system.md");
            await File.WriteAllTextAsync(systemPath, agent.SystemPromptCore).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Persists the synthesis conversation history and its driver-prompt
    /// snapshot. No-op when <see cref="synthesisHistory"/> is null (swarm
    /// failed before synthesis ran).
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task PersistSynthesisConversationAsync()
    {
        if (this.synthesisHistory == null)
        {
            return;
        }

        var chatDir = Path.Combine(this.workDirectory, ".chat");
        Directory.CreateDirectory(chatDir);

        var synthesisPath = Path.Combine(chatDir, "synthesis.jsonl");
        await ConversationHistorySerializer.SerializeAsync(synthesisPath, this.synthesisHistory).ConfigureAwait(false);

        if (this.synthesisHistory.Count > 0 && this.synthesisHistory[0].Role == ChatRole.System)
        {
            var synthesisSystemPath = Path.Combine(chatDir, "synthesis.system.md");
            await File.WriteAllTextAsync(synthesisSystemPath, this.synthesisHistory[0].Text).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Writes the agent-attributed output index (<c>task-outputs.json</c>) to
    /// the swarm work directory. Each completed task contributes a
    /// (input, output, label-join-key) triple — the verbatim <c>result</c>,
    /// the producing <c>workerRole</c> (lens) and <c>workerName</c> (instance),
    /// and artifact pointers — that survives synthesis dedup and is produced on
    /// every terminal path (including Failed). Called from the all-paths
    /// end-of-run flush so it is bundled into the tree the archiver promotes.
    /// </summary>
    /// <returns>A task representing the asynchronous write.</returns>
    private async Task PersistTaskOutputsIndexAsync()
    {
        var allTasks = await this.swarmService.GetTasksAsync().ConfigureAwait(false);
        var completed = allTasks.Where(t => t.Status == TaskState.Completed).ToList();

        var entries = new List<TaskOutputEntry>(completed.Count);
        foreach (var task in completed)
        {
            entries.Add(new TaskOutputEntry(
                task.Id,
                task.WorkerName,
                task.WorkerRole,
                task.Subject,
                task.Result,
                task.UpdatedAt,
                this.BuildTaskArtifacts(task.WorkerName)));
        }

        var index = new TaskOutputsIndex(
            this.SwarmId.ToString(),
            this.template?.Key,
            DateTime.UtcNow,
            entries);

        var indexJson = JsonSerializer.Serialize(index, SwarmJsonOptions.Default);
        var indexPath = Path.Combine(this.workDirectory, "task-outputs.json");
        await File.WriteAllTextAsync(indexPath, indexJson).ConfigureAwait(false);

        this.LogTaskOutputsIndexWritten(entries.Count, this.SwarmId);
    }

    /// <summary>
    /// Builds the artifact pointer block for a single task's producing worker:
    /// the relative transcript and system-prompt paths, plus the SHA-256 of the
    /// persisted system prompt for prompt-version / drift detection. The hash is
    /// <c>null</c> when the system-prompt snapshot is absent.
    /// </summary>
    /// <param name="workerName">The producing worker's instance name.</param>
    /// <returns>The artifact pointer block.</returns>
    private TaskArtifacts BuildTaskArtifacts(string workerName)
    {
        var transcript = $".chat/{workerName}.jsonl";
        var systemPrompt = $".chat/{workerName}.system.md";
        var systemPromptPath = Path.Combine(this.workDirectory, ".chat", $"{workerName}.system.md");

        string? hash = null;
        if (File.Exists(systemPromptPath))
        {
            var bytes = File.ReadAllBytes(systemPromptPath);
            var digest = System.Security.Cryptography.SHA256.HashData(bytes);
            hash = "sha256:" + Convert.ToHexStringLower(digest);
        }

        return new TaskArtifacts(transcript, systemPrompt, hash);
    }

    [LoggerMessage(EventId = 100, Level = LogLevel.Information, Message = "Phase changed to {Phase} for swarm {SwarmId}.")]
    private partial void LogPhaseChanged(string phase, Guid swarmId);

    [LoggerMessage(EventId = 101, Level = LogLevel.Information, Message = "Plan created with {TaskCount} tasks for swarm {SwarmId}.")]
    private partial void LogPlanCreated(int taskCount, Guid swarmId);

    [LoggerMessage(EventId = 102, Level = LogLevel.Information, Message = "Agent spawned: {AgentName} ({Role}).")]
    private partial void LogAgentSpawned(string agentName, string role);

    [LoggerMessage(EventId = 126, Level = LogLevel.Warning, Message = "Custom tool '{ToolName}' requested by worker '{WorkerName}' is not supplied by any registered ICustomToolProvider.")]
    private partial void LogCustomToolNotFound(string toolName, string workerName);

    [LoggerMessage(EventId = 103, Level = LogLevel.Information, Message = "Round {Round} started with {RunnableCount} runnable tasks.")]
    private partial void LogRoundStarted(int round, int runnableCount);

    [LoggerMessage(EventId = 104, Level = LogLevel.Information, Message = "Round {Round} completed.")]
    private partial void LogRoundCompleted(int round);

    [LoggerMessage(EventId = 105, Level = LogLevel.Debug, Message = "Task {TaskId} completed by {Worker}.")]
    private partial void LogTaskCompleted(string taskId, string worker);

    [LoggerMessage(EventId = 106, Level = LogLevel.Warning, Message = "Task {TaskId} failed for {Worker}: {Error}.")]
    private partial void LogTaskFailed(string taskId, string worker, string error);

    [LoggerMessage(EventId = 107, Level = LogLevel.Information, Message = "Swarm {SwarmId} completed successfully.")]
    private partial void LogSwarmComplete(Guid swarmId);

    [LoggerMessage(EventId = 108, Level = LogLevel.Error, Message = "Swarm {SwarmId} failed.")]
    private partial void LogSwarmFailed(Guid swarmId, Exception exception);

    [LoggerMessage(EventId = 109, Level = LogLevel.Information, Message = "Swarm {SwarmId} cancelled.")]
    private partial void LogSwarmCancelled(Guid swarmId);

    [LoggerMessage(EventId = 110, Level = LogLevel.Information, Message = "Suspend wait started with {RemainingTasks} remaining tasks.")]
    private partial void LogSuspendWait(int remainingTasks);

    [LoggerMessage(EventId = 111, Level = LogLevel.Debug, Message = "Sending LLM request for {Phase} phase, agent: {AgentName}.")]
    private partial void LogLlmRequest(string phase, string agentName);

    [LoggerMessage(EventId = 113, Level = LogLevel.Information, Message = "LLM response received for {Phase} phase, agent: {AgentName}. Messages: {MessageCount}")]
    private partial void LogLlmResponse(string phase, string agentName, int messageCount);

    [LoggerMessage(EventId = 114, Level = LogLevel.Information, Message = "create_plan tool was called by the LLM — plan captured")]
    private partial void LogPlanToolCalled();

    [LoggerMessage(EventId = 115, Level = LogLevel.Warning, Message = "create_plan tool was NOT called by the LLM — waiting for TCS (10s timeout)")]
    private partial void LogPlanToolNotCalled();

    [LoggerMessage(EventId = 116, Level = LogLevel.Information, Message = "LLM message [{Phase}] role={Role}: {Content}")]
    private partial void LogLlmMessageContent(string phase, string role, string content);

    [LoggerMessage(EventId = 117, Level = LogLevel.Information, Message = "LLM tool call [{Phase}] function={FunctionName} args={Arguments}")]
    private partial void LogLlmToolCall(string phase, string functionName, string arguments);

    [LoggerMessage(EventId = 118, Level = LogLevel.Debug, Message = "LLM content item [{Phase}] type={ContentType}: {Value}")]
    private partial void LogLlmContentItem(string phase, string contentType, string value);

    [LoggerMessage(EventId = 119, Level = LogLevel.Debug, Message = "Prompt sent [{Phase}] role={Role}: {Content}")]
    private partial void LogLlmPromptSent(string phase, string role, string content);

    [LoggerMessage(EventId = 112, Level = LogLevel.Information, Message = "Synthesis phase complete for swarm {SwarmId}.")]
    private partial void LogSynthesisComplete(Guid swarmId);

    [LoggerMessage(EventId = 120, Level = LogLevel.Warning, Message = "Task {TaskId} from worker {Worker} did not call task_update; marking Failed.")]
    private partial void LogWorkerDidNotSignalTaskUpdate(string taskId, string worker);

    [LoggerMessage(EventId = 121, Level = LogLevel.Debug, Message = "Registered canonical leader inbox '{InboxName}' before spawning workers.")]
    private partial void LogLeaderInboxRegistered(string inboxName);

    [LoggerMessage(EventId = 122, Level = LogLevel.Information, Message = "Synthesis report written to {ReportPath} for swarm {SwarmId}.")]
    private partial void LogSynthesisReportWritten(string reportPath, Guid swarmId);

    [LoggerMessage(EventId = 123, Level = LogLevel.Warning, Message = "Failed to persist synthesis report for swarm {SwarmId}; the report is still returned in-memory.")]
    private partial void LogSynthesisReportWriteFailed(Guid swarmId, Exception exception);

    [LoggerMessage(EventId = 124, Level = LogLevel.Information, Message = "Conversation histories persisted for {AgentCount} agents in swarm {SwarmId}.")]
    private partial void LogConversationHistoriesPersisted(int agentCount, Guid swarmId);

    [LoggerMessage(EventId = 125, Level = LogLevel.Warning, Message = "Failed to persist conversation histories for swarm {SwarmId}; refinement chat may be unavailable.")]
    private partial void LogConversationHistoryPersistFailed(Guid swarmId, Exception exception);

    [LoggerMessage(EventId = 127, Level = LogLevel.Information, Message = "Task-outputs index written with {EntryCount} entries for swarm {SwarmId}.")]
    private partial void LogTaskOutputsIndexWritten(int entryCount, Guid swarmId);
}
