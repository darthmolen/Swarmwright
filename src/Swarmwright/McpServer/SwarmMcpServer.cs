using System.Text.Json;
using Swarmwright.Database.Models;
using Swarmwright.Database.Repositories;
using Swarmwright.Hosting;
using Swarmwright.Templates;

namespace Swarmwright.McpServer;

/// <summary>
/// Provides MCP tool handler methods for swarm operations.
/// </summary>
public class SwarmMcpServer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly ISwarmManager swarmManager;
    private readonly ISwarmRepository repository;
    private readonly ITemplateLoader templateLoader;

    /// <summary>
    /// Initializes a new instance of the <see cref="SwarmMcpServer"/> class.
    /// </summary>
    /// <param name="swarmManager">The swarm manager for lifecycle operations.</param>
    /// <param name="repository">The swarm repository for data access.</param>
    /// <param name="templateLoader">The template loader for swarm templates.</param>
    public SwarmMcpServer(
        ISwarmManager swarmManager,
        ISwarmRepository repository,
        ITemplateLoader templateLoader)
    {
        this.swarmManager = swarmManager;
        this.repository = repository;
        this.templateLoader = templateLoader;
    }

    /// <summary>
    /// Gets all active swarm instances with their IDs, goals, and phases.
    /// </summary>
    /// <returns>A JSON string containing the active swarms list.</returns>
    public async Task<string> GetActiveSwarmsAsync()
    {
        var instances = this.swarmManager.ListActiveSwarms();
        var swarms = new List<object>(instances.Count);
        foreach (var i in instances)
        {
            var entity = await this.repository.GetSwarmAsync(i.SwarmId).ConfigureAwait(false);
            swarms.Add(new
            {
                swarmId = i.SwarmId.ToString(),
                goal = i.Goal,
                phase = entity?.State ?? "Running",
                template = i.TemplateKey,
            });
        }

        return JsonSerializer.Serialize(new { swarms }, JsonOptions);
    }

    /// <summary>
    /// Gets the status of a specific swarm instance.
    /// </summary>
    /// <param name="swarmId">The unique identifier of the swarm.</param>
    /// <returns>A JSON string containing the swarm status.</returns>
    public async Task<string> GetSwarmStatusAsync(Guid swarmId)
    {
        var instance = this.swarmManager.GetSwarm(swarmId);
        if (instance is null)
        {
            return JsonSerializer.Serialize(
                new { error = $"Swarm {swarmId} not found." },
                JsonOptions);
        }

        var entity = await this.repository.GetSwarmAsync(swarmId).ConfigureAwait(false);
        return JsonSerializer.Serialize(
            new
            {
                swarmId = instance.SwarmId.ToString(),
                phase = entity?.State ?? "Running",
                goal = instance.Goal,
                template = instance.TemplateKey,
                isRunning = instance.IsRunning,
            },
            JsonOptions);
    }

    /// <summary>
    /// Lists tasks for a swarm, optionally filtered by status or worker.
    /// </summary>
    /// <param name="swarmId">The unique identifier of the swarm.</param>
    /// <param name="status">The optional status filter.</param>
    /// <param name="worker">The optional worker name filter.</param>
    /// <returns>A JSON string containing the filtered task list.</returns>
    public async Task<string> ListTasksAsync(Guid swarmId, string? status = null, string? worker = null)
    {
        var tasks = await this.repository.GetTasksAsync(swarmId);

        IEnumerable<TaskEntity> filtered = tasks;
        if (!string.IsNullOrEmpty(status))
        {
            filtered = filtered.Where(t => string.Equals(t.State, status, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrEmpty(worker))
        {
            filtered = filtered.Where(t => string.Equals(t.WorkerName, worker, StringComparison.OrdinalIgnoreCase));
        }

        var items = filtered.Select(t => new
        {
            id = t.Id,
            subject = t.Subject,
            status = t.State,
            workerName = t.WorkerName,
            workerRole = t.WorkerRole,
        });

        return JsonSerializer.Serialize(new { tasks = items }, JsonOptions);
    }

    /// <summary>
    /// Gets detailed information about a single task.
    /// </summary>
    /// <param name="swarmId">The unique identifier of the swarm.</param>
    /// <param name="taskId">The task identifier.</param>
    /// <returns>A JSON string containing the task detail.</returns>
    public async Task<string> GetTaskDetailAsync(Guid swarmId, string taskId)
    {
        var tasks = await this.repository.GetTasksAsync(swarmId);
        var task = tasks.FirstOrDefault(t => string.Equals(t.Id, taskId, StringComparison.OrdinalIgnoreCase));

        if (task is null)
        {
            return JsonSerializer.Serialize(
                new { error = $"Task {taskId} not found in swarm {swarmId}." },
                JsonOptions);
        }

        return JsonSerializer.Serialize(
            new
            {
                id = task.Id,
                subject = task.Subject,
                description = task.Description,
                status = task.State,
                workerName = task.WorkerName,
                workerRole = task.WorkerRole,
                result = task.Result,
                blockedByJson = task.BlockedByJson,
            },
            JsonOptions);
    }

    /// <summary>
    /// Lists all agents registered for a swarm.
    /// </summary>
    /// <param name="swarmId">The unique identifier of the swarm.</param>
    /// <returns>A JSON string containing the agent list.</returns>
    public async Task<string> ListAgentsAsync(Guid swarmId)
    {
        var agents = await this.repository.GetAgentsAsync(swarmId);
        var items = agents.Select(a => new
        {
            name = a.Name,
            role = a.Role,
            displayName = a.DisplayName,
            status = a.Status,
            tasksCompleted = a.TasksCompleted,
        });

        return JsonSerializer.Serialize(new { agents = items }, JsonOptions);
    }

    /// <summary>
    /// Gets recent events for a swarm.
    /// </summary>
    /// <param name="swarmId">The unique identifier of the swarm.</param>
    /// <param name="count">The maximum number of events to return.</param>
    /// <returns>A JSON string containing the event list.</returns>
    public async Task<string> GetRecentEventsAsync(Guid swarmId, int count = 50)
    {
        var events = await this.repository.GetEventsAsync(swarmId, count);
        var items = events.Select(e => new
        {
            eventType = e.EventType,
            dataJson = e.DataJson,
            createdAt = e.CreatedAt.ToString("O"),
        });

        return JsonSerializer.Serialize(new { events = items }, JsonOptions);
    }

    /// <summary>
    /// Gets a token-efficient summary of a swarm including task status counts.
    /// </summary>
    /// <param name="swarmId">The unique identifier of the swarm.</param>
    /// <returns>A JSON string containing the swarm summary.</returns>
    public async Task<string> GetSwarmSummaryAsync(Guid swarmId)
    {
        var instance = this.swarmManager.GetSwarm(swarmId);
        if (instance is null)
        {
            return JsonSerializer.Serialize(
                new { error = $"Swarm {swarmId} not found." },
                JsonOptions);
        }

        var tasks = await this.repository.GetTasksAsync(swarmId);
        var statusCounts = tasks
            .GroupBy(t => t.State)
            .ToDictionary(g => g.Key, g => g.Count());

        var entity = await this.repository.GetSwarmAsync(swarmId).ConfigureAwait(false);
        return JsonSerializer.Serialize(
            new
            {
                swarmId = instance.SwarmId.ToString(),
                goal = instance.Goal,
                phase = entity?.State ?? "Running",
                template = instance.TemplateKey,
                taskCounts = statusCounts,
                totalTasks = tasks.Count,
            },
            JsonOptions);
    }

    /// <summary>
    /// Placeholder for resuming a suspended agent. Returns a success message.
    /// </summary>
    /// <param name="swarmId">The unique identifier of the swarm.</param>
    /// <param name="agentName">The name of the agent to resume.</param>
    /// <param name="nudge">An optional nudge message for the agent.</param>
    /// <returns>A JSON string containing the result message.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Placeholder - will use instance data when orchestrator access is added.")]
    public Task<string> ResumeAgentAsync(Guid swarmId, string agentName, string? nudge = null)
    {
        var result = JsonSerializer.Serialize(
            new
            {
                message = $"Resume signal queued for agent '{agentName}' in swarm {swarmId}.",
                nudge,
            },
            JsonOptions);
        return Task.FromResult(result);
    }

    /// <summary>
    /// Gets all available swarm template keys.
    /// </summary>
    /// <returns>A JSON string containing the template key list.</returns>
    public Task<string> GetSwarmTemplatesAsync()
    {
        var keys = this.templateLoader.ListAvailable();
        var result = JsonSerializer.Serialize(new { templates = keys }, JsonOptions);
        return Task.FromResult(result);
    }

    /// <summary>
    /// Creates a new swarm with the specified goal and optional template.
    /// </summary>
    /// <param name="goal">The user-provided goal for the swarm.</param>
    /// <param name="template">The optional template key.</param>
    /// <param name="context">
    /// Optional free-form key/value context exposed to scoped custom tool
    /// providers via <c>ISwarmRunContext</c> and persisted across evict/resume.
    /// </param>
    /// <returns>A JSON string containing the created swarm ID.</returns>
    public async Task<string> CreateSwarmAsync(
        string goal,
        string? template = null,
        IReadOnlyDictionary<string, string>? context = null)
    {
        var swarmId = await this.swarmManager.CreateSwarmAsync(goal, template, context);
        return JsonSerializer.Serialize(
            new { swarmId = swarmId.ToString(), message = "Swarm created and started." },
            JsonOptions);
    }
}
