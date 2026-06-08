using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Swarmwright.Events;
using Swarmwright.Events.AgUI;
using Swarmwright.Models.Enums;
using Swarmwright.Services;
using Swarmwright.Templates;

namespace Swarmwright.Tools;

/// <summary>
/// Creates <see cref="AITool"/> instances for worker agents in a swarm.
/// </summary>
public static class SwarmToolFactory
{
    // Routed through SwarmJsonOptions.Default so enum values serialize as their
    // PascalCase string form (e.g. "Completed") rather than raw integers.
    // Previously the task_list tool returned "status":3 to the LLM, which it had
    // no way to interpret — tracked as a follow-up from the prior plan's Batch 3.
    private static readonly JsonSerializerOptions JsonOptions = SwarmJsonOptions.Default;

    /// <summary>
    /// Creates the four coordination-only worker tools (legacy overload for callers
    /// that don't have a work directory or template context).
    /// </summary>
    /// <param name="agentName">The name of the agent these tools belong to.</param>
    /// <param name="swarmService">The swarm service for task and message operations.</param>
    /// <param name="eventBus">The event bus for emitting swarm events (legacy).</param>
    /// <param name="agUiAdapter">The AG-UI event adapter for typed domain events.</param>
    /// <returns>A list of 4 coordination AI tools for worker agents.</returns>
    public static IList<AITool> CreateWorkerTools(
        string agentName,
        ISwarmService swarmService,
        ISwarmEventBus eventBus,
        SwarmEventAdapter? agUiAdapter = null)
    {
        return CreateCoordinationTools(agentName, swarmService, eventBus, agUiAdapter);
    }

    /// <summary>
    /// Creates the worker tools with default-tool resolution and optional whitelist filtering.
    /// </summary>
    /// <param name="agentName">The name of the agent these tools belong to.</param>
    /// <param name="swarmService">The swarm service for task and message operations.</param>
    /// <param name="eventBus">The event bus for emitting swarm events (legacy).</param>
    /// <param name="agUiAdapter">The AG-UI event adapter for typed domain events.</param>
    /// <param name="workDirectory">The per-swarm work directory for file tools.</param>
    /// <param name="httpClient">The HTTP client used by <c>web_fetch</c>.</param>
    /// <param name="template">The loaded template providing the default <c>allow_default_tools</c> value.</param>
    /// <param name="agentDef">The agent definition with optional per-worker overrides.</param>
    /// <returns>The resolved list of AI tools for the worker.</returns>
    public static IList<AITool> CreateWorkerTools(
        string agentName,
        ISwarmService swarmService,
        ISwarmEventBus eventBus,
        SwarmEventAdapter? agUiAdapter,
        string workDirectory,
        HttpClient httpClient,
        LoadedTemplate? template,
        AgentDefinition? agentDef)
    {
        var coordinationTools = CreateCoordinationTools(agentName, swarmService, eventBus, agUiAdapter);

        // Resolve effective allow-default-tools (worker overrides template).
        var allowDefaults = agentDef?.AllowDefaultTools ?? template?.AllowDefaultTools ?? true;

        var allTools = new List<AITool>(coordinationTools);
        if (allowDefaults)
        {
            allTools.AddRange(DefaultToolFactory.CreateDefaultTools(workDirectory, httpClient));
        }

        // Apply explicit whitelist if the agent definition declares one.
        var whitelist = agentDef?.Tools;
        if (whitelist is { Count: > 0 })
        {
            var allowed = new HashSet<string>(whitelist, StringComparer.Ordinal);
            allTools = allTools.Where(t => allowed.Contains(t.Name)).ToList();
        }

        return allTools;
    }

    /// <summary>
    /// Async variant of the synchronous <c>CreateWorkerTools</c> that also loads tools from MCP
    /// endpoints declared on the agent via <see cref="AgentDefinition.McpEndpoints"/>.
    /// The caller supplies <paramref name="mcpToolLoader"/> — a delegate that looks up
    /// an MCP client by endpoint name and returns its tools. The loader is decoupled
    /// from <c>IMcpClientFactory</c> so tests can fake it without reaching into the SDK.
    /// </summary>
    /// <param name="agentName">The name of the agent these tools belong to.</param>
    /// <param name="swarmService">The swarm service for task and message operations.</param>
    /// <param name="eventBus">The event bus for emitting swarm events (legacy).</param>
    /// <param name="agUiAdapter">The AG-UI event adapter for typed domain events.</param>
    /// <param name="workDirectory">The per-swarm work directory for file tools.</param>
    /// <param name="httpClient">The HTTP client used by <c>web_fetch</c>.</param>
    /// <param name="template">The loaded template providing default settings.</param>
    /// <param name="agentDef">The agent definition with optional per-worker overrides.</param>
    /// <param name="mcpToolLoader">Loader that returns MCP tools for a named endpoint; null disables MCP loading.</param>
    /// <param name="cancellationToken">A cancellation token for the MCP client calls.</param>
    /// <returns>The resolved list of AI tools for the worker.</returns>
    public static async Task<IList<AITool>> CreateWorkerToolsAsync(
        string agentName,
        ISwarmService swarmService,
        ISwarmEventBus eventBus,
        SwarmEventAdapter? agUiAdapter,
        string workDirectory,
        HttpClient httpClient,
        LoadedTemplate? template,
        AgentDefinition? agentDef,
        Func<string, CancellationToken, Task<IReadOnlyList<AITool>>>? mcpToolLoader,
        CancellationToken cancellationToken = default)
    {
        var coordinationTools = CreateCoordinationTools(agentName, swarmService, eventBus, agUiAdapter);

        var allowDefaults = agentDef?.AllowDefaultTools ?? template?.AllowDefaultTools ?? true;

        var allTools = new List<AITool>(coordinationTools);
        if (allowDefaults)
        {
            allTools.AddRange(DefaultToolFactory.CreateDefaultTools(workDirectory, httpClient));
        }

        // Load MCP tools per endpoint the agent declares, if a loader is available.
        if (mcpToolLoader is not null && agentDef?.McpEndpoints is { Count: > 0 } endpoints)
        {
            foreach (var endpointName in endpoints)
            {
                var mcpTools = await mcpToolLoader(endpointName, cancellationToken).ConfigureAwait(false);
                allTools.AddRange(mcpTools);
            }
        }

        // Apply explicit whitelist AFTER MCP loading so it can filter MCP tools by name too.
        var whitelist = agentDef?.Tools;
        if (whitelist is { Count: > 0 })
        {
            var allowed = new HashSet<string>(whitelist, StringComparer.Ordinal);
            allTools = allTools.Where(t => allowed.Contains(t.Name)).ToList();
        }

        return allTools;
    }

    private static List<AITool> CreateCoordinationTools(
        string agentName,
        ISwarmService swarmService,
        ISwarmEventBus eventBus,
        SwarmEventAdapter? agUiAdapter)
    {
        return
        [
            CreateTaskUpdateTool(swarmService, eventBus),
            CreateInboxSendTool(agentName, swarmService, eventBus, agUiAdapter),
            CreateInboxReceiveTool(agentName, swarmService),
            CreateTaskListTool(agentName, swarmService),
        ];
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Tool functions must return error JSON to the LLM, not throw.")]
    private static AIFunction CreateTaskUpdateTool(
        ISwarmService swarmService,
        ISwarmEventBus eventBus)
    {
        _ = swarmService;
        _ = eventBus;
        return AIFunctionFactory.Create(
            (
                [Description("The unique identifier of the task to update.")] string task_id,
                [Description("The new status for the task (Pending, InProgress, Completed, Failed).")] string status,
                [Description("Result summary for completed tasks. Must be self-contained: include key findings, source URLs for any web-fetched data, MCP tool names and query parameters for any data-sourced claims, and flag any unsourced claims. This text is the ONLY input the synthesis phase receives from your work.")] string? result) =>
            {
                try
                {
                    _ = result;

                    // Forgiveness layer: normalize common LLM variants (snake_case,
                    // SCREAMING_SNAKE_CASE, "in progress") to PascalCase by stripping
                    // underscores and spaces before falling through to Enum.TryParse.
                    // The tool contract still advertises Pending/InProgress/Completed/Failed
                    // as canonical values; this just stops rejecting cosmetic variants.
                    var normalized = (status ?? string.Empty)
                        .Replace("_", string.Empty, StringComparison.Ordinal)
                        .Replace(" ", string.Empty, StringComparison.Ordinal);

                    if (!Enum.TryParse<TaskState>(normalized, ignoreCase: true, out var parsedStatus))
                    {
                        // Preserve the ORIGINAL status in the error so diagnostics show
                        // what the LLM actually sent, not our normalized form.
                        return JsonSerializer.Serialize(new { error = $"Invalid status: {status}" }, JsonOptions);
                    }

                    // F01.3: the tool no longer writes anywhere. The
                    // orchestrator's SwarmAgent parses the FunctionCallContent
                    // out of the worker's response, propagates the declared
                    // status via TaskExecutionResult.WorkerDeclaredStatus,
                    // and TerminateTaskAsync writes the task's DB state via
                    // IStateTransitionService.TransitionTaskAsync — which
                    // is also where SWARM_TASK_UPDATED is emitted (once,
                    // post-DB-commit). Calling the state service here as
                    // well would race the orchestrator's terminal write
                    // and produce a guard rejection on the second call.
                    return JsonSerializer.Serialize(new { success = true, taskId = task_id, status = parsedStatus.ToString() }, JsonOptions);
                }
                catch (Exception ex)
                {
                    return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
                }
            },
            "task_update",
            "Signals completion of a task to the swarm. The result field is the ONLY channel through which your work reaches the synthesis phase — it must be self-contained with sources, not just conclusions. The orchestrator persists the declared status and result after your conversation turn returns.");
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Tool functions must return error JSON to the LLM, not throw.")]
    private static AIFunction CreateInboxSendTool(
        string agentName,
        ISwarmService swarmService,
        ISwarmEventBus eventBus,
        SwarmEventAdapter? agUiAdapter)
    {
        _ = eventBus;
        return AIFunctionFactory.Create(
            async (
                [Description("The name of the recipient agent.")] string to,
                [Description("The message content to send.")] string message) =>
            {
                try
                {
                    await swarmService.SendMessageAsync(agentName, to, message);
                    if (agUiAdapter is not null)
                    {
                        await agUiAdapter.EmitCustomAsync(
                            "SWARM_INBOX_MESSAGE",
                            JsonSerializer.SerializeToElement(new { sender = agentName, recipient = to, content = message }));
                    }

                    return JsonSerializer.Serialize(new { success = true, to, from = agentName }, JsonOptions);
                }
                catch (Exception ex)
                {
                    return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
                }
            },
            "inbox_send",
            "Sends a message to another agent in the swarm.");
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Tool functions must return error JSON to the LLM, not throw.")]
    private static AIFunction CreateInboxReceiveTool(
        string agentName,
        ISwarmService swarmService)
    {
        return AIFunctionFactory.Create(
            async () =>
            {
                try
                {
                    var messages = await swarmService.InboxSystem.ReceiveAsync(agentName);
                    return JsonSerializer.Serialize(messages, JsonOptions);
                }
                catch (Exception ex)
                {
                    return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
                }
            },
            "inbox_receive",
            "Receives and removes all messages from the agent's inbox.");
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Tool functions must return error JSON to the LLM, not throw.")]
    private static AIFunction CreateTaskListTool(string agentName, ISwarmService swarmService)
    {
        return AIFunctionFactory.Create(
            async ([Description("Filter by worker name. Use 'all' to see all tasks, or omit to see your own tasks.")] string? owner) =>
            {
                try
                {
                    // Default to this agent's tasks; "all" shows everything
                    var effectiveOwner = owner?.Equals("all", StringComparison.OrdinalIgnoreCase) == true
                        ? null
                        : owner ?? agentName;
                    var tasks = await swarmService.GetTasksAsync(effectiveOwner);
                    return JsonSerializer.Serialize(tasks, JsonOptions);
                }
                catch (Exception ex)
                {
                    return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
                }
            },
            "task_list",
            $"Lists tasks on the swarm task board. By default shows your tasks (worker_name='{agentName}'). Pass owner='all' to see all tasks.");
    }
}
