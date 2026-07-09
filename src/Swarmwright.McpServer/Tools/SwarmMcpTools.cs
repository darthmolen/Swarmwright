using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Swarmwright.Database.Models;
using Swarmwright.Database.Repositories;
using Swarmwright.Extensions;
using Swarmwright.Hosting;
using Swarmwright.McpServer.Configuration;
using Swarmwright.McpServer.Contracts;
using Swarmwright.Recommendation;
using Swarmwright.Templates;
using Swarmwright.Tools;

namespace Swarmwright.McpServer.Tools;

/// <summary>
/// MCP tool surface that exposes swarm operations to external AI agents.
/// All tool names are snake_case to match the Python reference implementation.
/// </summary>
[McpServerToolType]
public sealed class SwarmMcpTools
{
    private readonly ISwarmManager swarmManager;
    private readonly ISwarmRepository repository;
    private readonly ITemplateLoader templateLoader;
    private readonly ISwarmInterventionHandler interventionHandler;
    private readonly IRecommendedSwarmContinueProvider recommendationProvider;
    private readonly SwarmMcpOptions options;

    /// <summary>
    /// Initializes a new instance of the <see cref="SwarmMcpTools"/> class.
    /// </summary>
    /// <param name="swarmManager">The swarm lifecycle manager.</param>
    /// <param name="repository">The swarm persistence repository.</param>
    /// <param name="templateLoader">The template loader.</param>
    /// <param name="interventionHandler">The recovery-action handler — the same logic behind the REST <c>/continue</c> / <c>/smart-continue</c> / <c>/skip</c> / <c>/cancel</c> endpoints.</param>
    /// <param name="recommendationProvider">The server-side recommendation provider surfaced in summaries and dedicated lookups.</param>
    /// <param name="options">The Swarm MCP options.</param>
    public SwarmMcpTools(
        ISwarmManager swarmManager,
        ISwarmRepository repository,
        ITemplateLoader templateLoader,
        ISwarmInterventionHandler interventionHandler,
        IRecommendedSwarmContinueProvider recommendationProvider,
        IOptions<SwarmMcpOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.swarmManager = swarmManager;
        this.repository = repository;
        this.templateLoader = templateLoader;
        this.interventionHandler = interventionHandler;
        this.recommendationProvider = recommendationProvider;
        this.options = options.Value;
    }

    /// <summary>
    /// Lists all swarms currently tracked by the in-memory manager.
    /// </summary>
    /// <returns>The list of active swarms.</returns>
    [McpServerTool(Name = "get_active_swarms", ReadOnly = true, Idempotent = true, UseStructuredContent = true)]
    [Description("Lists all swarms currently running or recently completed in the in-memory swarm manager.")]
    public async Task<IReadOnlyList<SwarmInfo>> GetActiveSwarmsAsync()
    {
        var executions = this.swarmManager.ListActiveSwarms();
        var results = new List<SwarmInfo>(executions.Count);
        foreach (var exec in executions)
        {
            var entity = await this.repository.GetSwarmAsync(exec.SwarmId).ConfigureAwait(false);
            results.Add(new SwarmInfo(
                SwarmId: exec.SwarmId,
                Goal: exec.Goal,
                TemplateKey: exec.TemplateKey,
                Phase: entity?.State ?? "Running",
                CreatedAt: DateTime.SpecifyKind(exec.CreatedAt, DateTimeKind.Utc),
                IsRunning: exec.IsRunning));
        }

        return results;
    }

    /// <summary>
    /// Gets the current status of a specific swarm including agent and task counts.
    /// </summary>
    /// <param name="swarmId">The unique identifier of the swarm to inspect.</param>
    /// <returns>A status snapshot for the swarm.</returns>
    [McpServerTool(Name = "get_swarm_status", ReadOnly = true, Idempotent = true, UseStructuredContent = true)]
    [Description("Gets the current phase, task counts by status, and agent count for a specific swarm.")]
    public async Task<SwarmStatus> GetSwarmStatusAsync(
        [Description("The unique identifier of the swarm.")] Guid swarmId)
    {
        var execution = this.swarmManager.GetSwarm(swarmId);
        var isRunning = execution?.IsRunning ?? false;

        var tasks = await this.repository.GetTasksAsync(swarmId).ConfigureAwait(false);
        var agents = await this.repository.GetAgentsAsync(swarmId).ConfigureAwait(false);
        var entity = await this.repository.GetSwarmAsync(swarmId).ConfigureAwait(false);
        var phase = entity?.State ?? "Unknown";

        if (execution is null && tasks.Count == 0 && agents.Count == 0 && entity is null)
        {
            throw new McpException($"Swarm {swarmId} not found.");
        }

        var counts = tasks
            .GroupBy(t => t.State, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => NormalizeStatus(g.Key), g => g.Count(), StringComparer.Ordinal);

        return new SwarmStatus(
            SwarmId: swarmId,
            Phase: phase,
            IsRunning: isRunning,
            AgentCount: agents.Count,
            TaskCountsByStatus: counts);
    }

    /// <summary>
    /// Lists tasks for a swarm, optionally filtered by status and/or worker name.
    /// </summary>
    /// <param name="swarmId">The swarm identifier.</param>
    /// <param name="status">Optional task status filter (case-insensitive, e.g., <c>Pending</c>, <c>Completed</c>).</param>
    /// <param name="worker">Optional worker name filter (case-insensitive).</param>
    /// <returns>The filtered task list.</returns>
    [McpServerTool(Name = "list_tasks", ReadOnly = true, Idempotent = true, UseStructuredContent = true)]
    [Description("Lists tasks for a swarm with optional status and worker filters.")]
    public async Task<IReadOnlyList<TaskInfo>> ListTasksAsync(
        [Description("The unique identifier of the swarm.")] Guid swarmId,
        [Description("Optional status filter (e.g., Pending, InProgress, Completed, Failed, Timeout, Blocked).")] string? status = null,
        [Description("Optional worker name filter.")] string? worker = null)
    {
        var tasks = await this.repository.GetTasksAsync(swarmId).ConfigureAwait(false);

        IEnumerable<TaskEntity> filtered = tasks;
        if (!string.IsNullOrEmpty(status))
        {
            filtered = filtered.Where(t => string.Equals(t.State, status, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrEmpty(worker))
        {
            filtered = filtered.Where(t => string.Equals(t.WorkerName, worker, StringComparison.OrdinalIgnoreCase));
        }

        return filtered
            .Select(t => new TaskInfo(
                Id: t.Id,
                Subject: t.Subject,
                WorkerName: t.WorkerName,
                WorkerRole: t.WorkerRole,
                Status: NormalizeStatus(t.State),
                CreatedAt: DateTime.SpecifyKind(t.CreatedAt, DateTimeKind.Utc),
                UpdatedAt: DateTime.SpecifyKind(t.UpdatedAt, DateTimeKind.Utc)))
            .ToList();
    }

    /// <summary>
    /// Lists all agents registered for a swarm.
    /// </summary>
    /// <param name="swarmId">The swarm identifier.</param>
    /// <returns>The list of agents.</returns>
    [McpServerTool(Name = "list_agents", ReadOnly = true, Idempotent = true, UseStructuredContent = true)]
    [Description("Lists all agents registered for a swarm with their roles and task completion counts.")]
    public async Task<IReadOnlyList<AgentSummary>> ListAgentsAsync(
        [Description("The unique identifier of the swarm.")] Guid swarmId)
    {
        var agents = await this.repository.GetAgentsAsync(swarmId).ConfigureAwait(false);
        return agents
            .Select(a => new AgentSummary(
                Name: a.Name,
                DisplayName: a.DisplayName,
                Role: a.Role,
                Status: a.Status,
                TasksCompleted: a.TasksCompleted))
            .ToList();
    }

    /// <summary>
    /// Lists all available swarm templates on disk.
    /// </summary>
    /// <returns>The list of templates.</returns>
    [McpServerTool(Name = "get_swarm_templates", ReadOnly = true, Idempotent = true, UseStructuredContent = true)]
    [Description("Lists all available swarm templates configured on the server.")]
    public IReadOnlyList<SwarmTemplateInfo> GetSwarmTemplates()
    {
        var templates = this.templateLoader.LoadAll();
        return templates
            .Select(t => new SwarmTemplateInfo(
                Key: t.Key,
                Name: t.Name,
                Description: t.Description))
            .ToList();
    }

    /// <summary>
    /// Lists files in a swarm's work directory.
    /// </summary>
    /// <param name="swarmId">The swarm identifier.</param>
    /// <returns>The artifact list.</returns>
    [McpServerTool(Name = "list_artifacts", ReadOnly = true, Idempotent = true, UseStructuredContent = true)]
    [Description("Lists files produced in a swarm's work directory.")]
    public IReadOnlyList<ArtifactInfo> ListArtifacts(
        [Description("The unique identifier of the swarm.")] Guid swarmId)
    {
        var workDir = this.swarmManager.GetWorkDirectory(swarmId);
        if (workDir is null || !Directory.Exists(workDir))
        {
            throw new McpException($"Work directory for swarm {swarmId} not found.");
        }

        var results = new List<ArtifactInfo>();
        foreach (var fullPath in Directory.EnumerateFiles(workDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(workDir, fullPath);
            if (!PathSecurity.TryResolveSafePath(workDir, relative, out var resolved))
            {
                continue;
            }

            var info = new FileInfo(resolved);
            if (!info.Exists)
            {
                continue;
            }

            var normalized = relative.Replace('\\', '/');
            results.Add(new ArtifactInfo(
                Name: Path.GetFileName(normalized),
                Path: normalized,
                SizeBytes: info.Length,
                ModifiedAt: DateTime.SpecifyKind(info.LastWriteTimeUtc, DateTimeKind.Utc)));
        }

        return results;
    }

    /// <summary>
    /// Reads the text contents of a file in a swarm's work directory.
    /// Rejects absolute paths and parent-traversal attempts.
    /// </summary>
    /// <param name="swarmId">The swarm identifier.</param>
    /// <param name="path">Relative path within the work directory.</param>
    /// <returns>The file contents.</returns>
    [McpServerTool(Name = "read_artifact", ReadOnly = true, Idempotent = true, UseStructuredContent = true)]
    [Description("Reads the text contents of a file in a swarm's work directory. Paths must be relative and stay within the swarm's work directory.")]
    public async Task<ArtifactContent> ReadArtifactAsync(
        [Description("The unique identifier of the swarm.")] Guid swarmId,
        [Description("Path relative to the swarm's work directory.")] string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new McpException("Path must not be empty.");
        }

        var workDir = this.swarmManager.GetWorkDirectory(swarmId);
        if (workDir is null || !Directory.Exists(workDir))
        {
            throw new McpException($"Work directory for swarm {swarmId} not found.");
        }

        if (!PathSecurity.TryResolveSafePath(workDir, path, out var resolved))
        {
            throw new McpException($"Invalid artifact path: '{path}'.");
        }

        if (!File.Exists(resolved))
        {
            throw new McpException($"Artifact not found: '{path}'.");
        }

        var info = new FileInfo(resolved);
        var max = this.options.MaxArtifactBytes;
        var truncated = info.Length > max;
        string content;
        if (truncated)
        {
            using var stream = File.OpenRead(resolved);
            var buffer = new byte[max];
            var read = await stream.ReadAsync(buffer.AsMemory(0, max)).ConfigureAwait(false);
            content = Encoding.UTF8.GetString(buffer, 0, read)
                + $"\n\n[... truncated; full size {info.Length} bytes, returned {read} bytes ...]";
        }
        else
        {
            content = await File.ReadAllTextAsync(resolved).ConfigureAwait(false);
        }

        return new ArtifactContent(
            Path: path.Replace('\\', '/'),
            Content: content,
            SizeBytes: info.Length,
            Truncated: truncated);
    }

    /// <summary>
    /// Creates a new swarm with the given goal and optional template, and enqueues it for execution.
    /// </summary>
    /// <param name="goal">The goal for the swarm.</param>
    /// <param name="templateKey">Optional template key to initialize the swarm with.</param>
    /// <param name="context">Optional free-form key/value metadata exposed to custom tools.</param>
    /// <returns>The identifier of the newly created swarm.</returns>
    [McpServerTool(Name = "create_swarm", Destructive = false, UseStructuredContent = true)]
    [Description("Creates a new swarm with the given goal and optional template, and enqueues it for execution.")]
    public async Task<SwarmCreatedResult> CreateSwarmAsync(
        [Description("The goal for the swarm to pursue.")] string goal,
        [Description("Optional template key (use get_swarm_templates to discover valid keys).")] string? templateKey = null,
        [Description("Optional free-form key/value metadata for the run, exposed to custom tools via the run context.")] Dictionary<string, string>? context = null)
    {
        if (string.IsNullOrWhiteSpace(goal))
        {
            throw new McpException("Goal must not be empty.");
        }

        var swarmId = await this.swarmManager.CreateSwarmAsync(goal, templateKey, context).ConfigureAwait(false);
        return new SwarmCreatedResult(swarmId, "starting");
    }

    /// <summary>
    /// Cancels a running swarm.
    /// </summary>
    /// <param name="swarmId">The swarm identifier.</param>
    /// <returns>A success result.</returns>
    [McpServerTool(Name = "cancel_swarm", Destructive = true, UseStructuredContent = true)]
    [Description("Cancels a running swarm, transitioning it to the Cancelled phase.")]
    public async Task<OperationResult> CancelSwarmAsync(
        [Description("The unique identifier of the swarm to cancel.")] Guid swarmId)
    {
        await this.swarmManager.CancelSwarmAsync(swarmId).ConfigureAwait(false);
        return new OperationResult(true, $"Swarm {swarmId} cancellation signal sent.");
    }

    /// <summary>
    /// Deterministic resume of a suspended swarm. Calls the same handler as the REST
    /// <c>POST /api/swarm/{id}/continue</c> endpoint: guards terminal state, releases any
    /// lock the caller holds, flips Failed-with-budget tasks back to Pending (bumping
    /// <c>retry_count</c>), transitions the swarm to <c>Executing</c>, and signals the
    /// orchestrator loop. Accepts both "at least one failed task has retry budget" AND
    /// "at least one task is already Pending". Does <b>not</b> invoke the leader.
    /// </summary>
    /// <param name="swarmId">The swarm identifier.</param>
    /// <returns>A structured result describing the attempt including post-op phase and recommendation.</returns>
    [McpServerTool(Name = "continue_swarm", UseStructuredContent = true)]
    [Description("Deterministic resume: retries failed tasks that still have retry budget and/or picks up viable Pending work. Does not invoke the leader LLM. Prefer this over signal_continue for any external-agent or operator-driven recovery.")]
    public async Task<RecoveryActionResult> ContinueSwarmAsync(
        [Description("The unique identifier of the swarm to continue.")] Guid swarmId)
    {
        var result = await this.interventionHandler
            .ContinueAsync(swarmId, actor: "mcp")
            .ConfigureAwait(false);
        return await this.BuildRecoveryResultAsync(swarmId, "continue", result).ConfigureAwait(false);
    }

    /// <summary>
    /// Leader-driven recovery. Calls the same handler as the REST
    /// <c>POST /api/swarm/{id}/smart-continue</c>: invokes the leader's
    /// <c>repair_plan_after_failure</c> tool to produce a reset/add/abandon plan and
    /// applies it, or — when there are zero failed tasks but viable open work —
    /// short-circuits straight to <c>Executing</c> with reason
    /// <c>user_smart_continue_no_failures</c>.
    /// </summary>
    /// <param name="swarmId">The swarm identifier.</param>
    /// <returns>A structured result including post-op phase and recommendation.</returns>
    [McpServerTool(Name = "smart_continue_swarm", UseStructuredContent = true)]
    [Description("Leader-driven recovery: invokes the leader LLM with the current failure context to produce a reset/add/abandon plan. Use when deterministic Continue cannot unblock the swarm (all failures retry-exhausted, dependency chain dead). Short-circuits to plain Executing when there are zero failures but viable open work.")]
    public async Task<RecoveryActionResult> SmartContinueSwarmAsync(
        [Description("The unique identifier of the swarm.")] Guid swarmId)
    {
        var result = await this.interventionHandler
            .SmartContinueAsync(swarmId, actor: "mcp")
            .ConfigureAwait(false);
        return await this.BuildRecoveryResultAsync(swarmId, "smart-continue", result).ConfigureAwait(false);
    }

    /// <summary>
    /// Force Synthesis. Calls the same handler as the REST
    /// <c>POST /api/swarm/{id}/skip</c>: abandons remaining open work and jumps the
    /// swarm into the Synthesizing phase so the synthesis agent can produce a report
    /// from whatever Completed tasks exist.
    /// </summary>
    /// <param name="swarmId">The swarm identifier.</param>
    /// <returns>A structured result including post-op phase.</returns>
    [McpServerTool(Name = "force_synthesis_swarm", Destructive = true, UseStructuredContent = true)]
    [Description("Force Synthesis: abandon remaining open work and run the synthesis agent against whatever is Completed. Terminal-ish — use when nothing is left to rescue.")]
    public async Task<RecoveryActionResult> ForceSynthesisSwarmAsync(
        [Description("The unique identifier of the swarm.")] Guid swarmId)
    {
        var result = await this.interventionHandler
            .SkipAsync(swarmId, actor: "mcp")
            .ConfigureAwait(false);
        return await this.BuildRecoveryResultAsync(swarmId, "force-synthesis", result).ConfigureAwait(false);
    }

    /// <summary>
    /// Flips a <c>Failed</c> swarm to <c>AwaitingIntervention</c> so the recovery
    /// buttons become actionable. Calls the same handler as the REST
    /// <c>POST /api/swarm/{id}/mark-as-awaiting-intervention</c> endpoint. This is a
    /// pure state flip — the orchestrator stays asleep until the caller picks a
    /// recovery action.
    /// </summary>
    /// <param name="swarmId">The swarm identifier.</param>
    /// <returns>A structured result including post-op phase and recommendation.</returns>
    [McpServerTool(Name = "mark_swarm_awaiting_intervention", UseStructuredContent = true)]
    [Description("Flips a Failed swarm to AwaitingIntervention so recovery actions (continue_swarm / smart_continue_swarm / force_synthesis_swarm) become legal. Does not resume execution on its own.")]
    public async Task<RecoveryActionResult> MarkSwarmAwaitingInterventionAsync(
        [Description("The unique identifier of the swarm.")] Guid swarmId)
    {
        var result = await this.interventionHandler
            .MarkAsAwaitingInterventionAsync(swarmId, actor: "mcp")
            .ConfigureAwait(false);
        return await this.BuildRecoveryResultAsync(swarmId, "mark-awaiting-intervention", result).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the server's opinion about the right recovery action for a swarm
    /// currently in an actionable non-terminal state. Mirrors the <c>recommendation</c>
    /// field returned by <c>get_swarm_summary</c>; provided as a dedicated tool so
    /// external agents can query just the opinion without pulling a full summary.
    /// </summary>
    /// <param name="swarmId">The swarm identifier.</param>
    /// <returns>The recommendation, or <see langword="null"/> when the swarm is not in an actionable state.</returns>
    [McpServerTool(Name = "get_swarm_recommendation", ReadOnly = true, Idempotent = true, UseStructuredContent = true)]
    [Description("Returns the server's opinion about the right recovery action (continue / smart-continue / force-synthesis / cancel) along with valid alternatives and rationale. Null when the swarm is not in an actionable non-terminal state.")]
    public async Task<SwarmContinueRecommendation?> GetSwarmRecommendationAsync(
        [Description("The unique identifier of the swarm.")] Guid swarmId)
    {
        return await this.recommendationProvider
            .GetRecommendationAsync(swarmId)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Low-level dispatch primitive — wakes an orchestrator blocked in
    /// <c>EnterSuspendWaitAsync</c>. Does NOT write state transitions, does NOT
    /// consume retry budget, and does NOT go through the intervention handler's
    /// guards. External agents and operators should prefer <c>continue_swarm</c>
    /// which composes this signal with the full audited state change.
    /// </summary>
    /// <param name="swarmId">The swarm identifier.</param>
    /// <returns>A success result.</returns>
    [McpServerTool(Name = "signal_continue", UseStructuredContent = true)]
    [Description("LOW-LEVEL: wakes a suspended orchestrator without writing any state transition. Almost always you want continue_swarm instead — it performs the audited state change, retry-budget bookkeeping, and lock handling that this signal skips.")]
    public OperationResult SignalContinue(
        [Description("The unique identifier of the swarm to resume.")] Guid swarmId)
    {
        this.swarmManager.SignalContinue(swarmId);
        return new OperationResult(true, $"Continue signal sent to swarm {swarmId}.");
    }

    /// <summary>
    /// Low-level dispatch primitive — signals the orchestrator to skip remaining
    /// execution rounds. Prefer <c>force_synthesis_swarm</c> for the audited
    /// state transition.
    /// </summary>
    /// <param name="swarmId">The swarm identifier.</param>
    /// <returns>A success result.</returns>
    [McpServerTool(Name = "signal_skip", UseStructuredContent = true)]
    [Description("LOW-LEVEL: signals the orchestrator to stop executing rounds. Almost always you want force_synthesis_swarm instead — it performs the audited state transition to Synthesizing.")]
    public OperationResult SignalSkip(
        [Description("The unique identifier of the swarm.")] Guid swarmId)
    {
        this.swarmManager.SignalSkip(swarmId);
        return new OperationResult(true, $"Skip signal sent to swarm {swarmId}.");
    }

    /// <summary>
    /// Returns a token-efficient, phase-aware summary of a swarm suitable for quick orientation.
    /// </summary>
    /// <param name="swarmId">The swarm identifier.</param>
    /// <returns>The swarm summary.</returns>
    [McpServerTool(Name = "get_swarm_summary", ReadOnly = true, Idempotent = true, UseStructuredContent = true)]
    [Description("Returns a token-efficient, phase-aware summary of a swarm suitable for quick orientation before drilling into tasks, agents, or artifacts.")]
    public async Task<SwarmSummary> GetSwarmSummaryAsync(
        [Description("The unique identifier of the swarm.")] Guid swarmId)
    {
        return await this.BuildSummaryAsync(swarmId).ConfigureAwait(false);
    }

    /// <summary>
    /// Blocks server-side until the swarm makes observable progress (phase change,
    /// a new task completion, a terminal phase, or the swarm is evicted) or until
    /// <paramref name="timeoutSeconds"/> elapses. Returns the fresh summary. Call
    /// repeatedly in a loop to stream progress without burning tokens on polling turns.
    /// </summary>
    /// <param name="swarmId">The swarm identifier.</param>
    /// <param name="timeoutSeconds">Maximum seconds to wait (clamped to 1..60; default 15).</param>
    /// <returns>The swarm summary at return time.</returns>
    [McpServerTool(Name = "wait_for_swarm_progress", ReadOnly = true, UseStructuredContent = true)]
    [Description("Blocks until the swarm makes observable progress (phase change, task completion, or terminal state) or the timeout elapses. Returns the fresh summary. Use this in a loop instead of repeatedly calling get_swarm_summary to avoid burning tokens on busy-poll turns.")]
    public async Task<SwarmSummary> WaitForSwarmProgressAsync(
        [Description("The unique identifier of the swarm.")] Guid swarmId,
        [Description("Maximum seconds to wait for progress before returning the current summary. Clamped to 1..60. Default 15.")] int timeoutSeconds = 15)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 60));
        var deadline = DateTime.UtcNow + timeout;

        var startingFingerprint = await this.ComputeFingerprintAsync(swarmId).ConfigureAwait(false);
        if (startingFingerprint is null)
        {
            throw new McpException($"Swarm {swarmId} not found.");
        }

        if (IsTerminal(startingFingerprint.Value.Phase))
        {
            return await this.BuildSummaryAsync(swarmId).ConfigureAwait(false);
        }

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);

            var current = await this.ComputeFingerprintAsync(swarmId).ConfigureAwait(false);

            // Execution evicted from the manager (normal 5-min post-terminal eviction) — stop waiting.
            if (current is null)
            {
                break;
            }

            if (current != startingFingerprint || IsTerminal(current.Value.Phase))
            {
                break;
            }
        }

        return await this.BuildSummaryAsync(swarmId).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the token-efficient summary shared by <c>get_swarm_summary</c> and
    /// <c>wait_for_swarm_progress</c>.
    /// </summary>
    private async Task<SwarmSummary> BuildSummaryAsync(Guid swarmId)
    {
        var execution = this.swarmManager.GetSwarm(swarmId);
        var tasks = await this.repository.GetTasksAsync(swarmId).ConfigureAwait(false);
        var entity = await this.repository.GetSwarmAsync(swarmId).ConfigureAwait(false);

        if (execution is null && tasks.Count == 0 && entity is null)
        {
            throw new McpException($"Swarm {swarmId} not found.");
        }

        var phase = entity?.State ?? "Unknown";
        var goal = execution?.Goal ?? entity?.Goal ?? string.Empty;
        var templateKey = execution?.TemplateKey ?? entity?.TemplateKey;
        var isRunning = execution?.IsRunning ?? false;

        var completedCount = tasks.Count(t => string.Equals(t.State, "Completed", StringComparison.OrdinalIgnoreCase));
        var failedCount = tasks.Count(t =>
            string.Equals(t.State, "Failed", StringComparison.OrdinalIgnoreCase));
        var ratio = tasks.Count == 0 ? 0.0 : (double)completedCount / tasks.Count;

        var headline = phase switch
        {
            "Starting" => "Swarm is initializing.",
            "Qa" => "Leader is conducting Q&A with the user.",
            "Planning" => "Leader is decomposing the goal into tasks.",
            "Spawning" => "Worker agents are being created.",
            "Executing" => $"Workers executing — {completedCount}/{tasks.Count} tasks complete.",
            "Synthesizing" => "Leader is synthesizing results.",
            "Complete" => $"Swarm complete — {completedCount}/{tasks.Count} tasks finished successfully.",
            "Suspended" => "Swarm suspended awaiting user action.",
            "Cancelled" => "Swarm was cancelled.",
            "Failed" => "Swarm failed during execution.",
            _ => $"Swarm in phase {phase}.",
        };

        string? primaryArtifact = null;
        var workDir = this.swarmManager.GetWorkDirectory(swarmId);
        if (workDir is not null
            && (phase is "Complete" or "Synthesizing")
            && PathSecurity.TryResolveSafePath(workDir, "synthesis-report.md", out var resolved)
            && File.Exists(resolved))
        {
            primaryArtifact = "synthesis-report.md";
        }

        var recommendation = await this.recommendationProvider
            .GetRecommendationAsync(swarmId)
            .ConfigureAwait(false);

        return new SwarmSummary(
            SwarmId: swarmId,
            Goal: goal,
            TemplateKey: templateKey,
            Phase: phase,
            IsRunning: isRunning,
            Headline: headline,
            TaskCompletionRatio: ratio,
            TotalTasks: tasks.Count,
            CompletedTasks: completedCount,
            FailedTasks: failedCount,
            PrimaryArtifactPath: primaryArtifact,
            Recommendation: recommendation);
    }

    /// <summary>
    /// Computes a cheap comparable fingerprint used by <c>wait_for_swarm_progress</c>
    /// to detect observable changes without rebuilding the whole summary each tick.
    /// Returns null if the swarm is not present in either the in-memory manager or the repository.
    /// </summary>
    private async Task<(string Phase, int TotalTasks, int CompletedTasks)?> ComputeFingerprintAsync(Guid swarmId)
    {
        var execution = this.swarmManager.GetSwarm(swarmId);
        var tasks = await this.repository.GetTasksAsync(swarmId).ConfigureAwait(false);
        var entity = await this.repository.GetSwarmAsync(swarmId).ConfigureAwait(false);

        if (execution is null && tasks.Count == 0 && entity is null)
        {
            return null;
        }

        var phase = entity?.State ?? "Unknown";
        var completed = tasks.Count(t => string.Equals(t.State, "Completed", StringComparison.OrdinalIgnoreCase));
        return (phase, tasks.Count, completed);
    }

    private static bool IsTerminal(string phase) =>
        phase is "Complete" or "Failed" or "Cancelled";

    /// <summary>
    /// Translates an <see cref="InterventionResult"/> from the shared handler into the
    /// MCP-facing <see cref="RecoveryActionResult"/>: extracts the canonical error code
    /// and message from the body, queries the current phase (so the caller sees where
    /// the swarm actually landed), and attaches a fresh recommendation so the caller
    /// can decide the next action in-turn.
    /// </summary>
    private async Task<RecoveryActionResult> BuildRecoveryResultAsync(
        Guid swarmId,
        string action,
        InterventionResult result)
    {
        string? code = null;
        string message;

        if (result.StatusCode == 204)
        {
            message = $"{action} accepted.";
        }
        else
        {
            (code, message) = ExtractCodeAndMessage(result.Body);
        }

        var entity = await this.repository.GetSwarmAsync(swarmId).ConfigureAwait(false);
        var currentPhase = entity?.State ?? "Unknown";

        var recommendation = await this.recommendationProvider
            .GetRecommendationAsync(swarmId)
            .ConfigureAwait(false);

        return new RecoveryActionResult(
            Ok: result.StatusCode == 204,
            StatusCode: result.StatusCode,
            Action: action,
            Code: code,
            Message: message,
            CurrentPhase: currentPhase,
            Recommendation: recommendation);
    }

    /// <summary>
    /// Peels <c>code</c> and <c>message</c> out of the anonymous/record body that
    /// <see cref="InterventionResult.Conflict"/> / <see cref="InterventionResult.NotFound"/> /
    /// etc. attach to their rejections. Best-effort — returns sensible defaults when
    /// the body shape does not match.
    /// </summary>
    private static (string? Code, string Message) ExtractCodeAndMessage(object? body)
    {
        if (body is null)
        {
            return (null, "Action rejected without detail.");
        }

        try
        {
            var element = body is JsonElement je
                ? je
                : JsonSerializer.SerializeToElement(body);

            string? code = null;
            string? message = null;

            if (element.ValueKind == JsonValueKind.Object)
            {
                if (element.TryGetProperty("code", out var codeElement)
                    && codeElement.ValueKind == JsonValueKind.String)
                {
                    code = codeElement.GetString();
                }

                if (element.TryGetProperty("message", out var messageElement)
                    && messageElement.ValueKind == JsonValueKind.String)
                {
                    message = messageElement.GetString();
                }
            }

            return (code, message ?? code ?? "Action rejected.");
        }
#pragma warning disable CA1031 // Body shape is best-effort; surface a generic fallback rather than bubbling.
        catch
#pragma warning restore CA1031
        {
            return (null, "Action rejected (body could not be parsed).");
        }
    }

    /// <summary>
    /// Normalizes task status strings to PascalCase so callers get a consistent surface
    /// regardless of whether the repository returns <c>"completed"</c> or <c>"Completed"</c>.
    /// </summary>
    private static string NormalizeStatus(string status)
    {
        if (string.IsNullOrEmpty(status))
        {
            return status;
        }

        return char.IsUpper(status[0])
            ? status
            : char.ToUpperInvariant(status[0]) + status[1..];
    }
}
