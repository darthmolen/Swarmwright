using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Swarmwright.Events;
using Swarmwright.Events.AgUI;
using Swarmwright.Exceptions;
using Swarmwright.Hosting;
using Swarmwright.Templates;
using Swarmwright.Tools;

namespace Swarmwright.Refinement;

/// <summary>
/// Handles AG-UI single-endpoint protocol requests for refinement chat.
/// Dispatches on the <c>method</c> field to info, agent/run, and agent/stop handlers.
/// </summary>
[SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Handler must return errors to the SSE stream, not throw.")]
public sealed partial class RefinementChatHandler
{
    private readonly ISwarmManager swarmManager;
    private readonly IChatClient chatClient;
    private readonly IHttpClientFactory httpClientFactory;
    private readonly ILogger<RefinementChatHandler> logger;
    private CancellationTokenSource? activeRunCts;

    /// <summary>
    /// Initializes a new instance of the <see cref="RefinementChatHandler"/> class.
    /// </summary>
    /// <param name="swarmManager">The swarm manager for work directory resolution.</param>
    /// <param name="chatClient">The chat client for LLM calls.</param>
    /// <param name="httpClientFactory">The HTTP client factory for creating the web_fetch tool client.</param>
    /// <param name="logger">The logger instance.</param>
    public RefinementChatHandler(
        ISwarmManager swarmManager,
        IChatClient chatClient,
        IHttpClientFactory httpClientFactory,
        ILogger<RefinementChatHandler> logger)
    {
        this.swarmManager = swarmManager;
        this.chatClient = chatClient;
        this.httpClientFactory = httpClientFactory;
        this.logger = logger;
    }

    /// <summary>
    /// Dispatches the request to the appropriate handler based on the method field.
    /// </summary>
    /// <param name="swarmId">The swarm identifier.</param>
    /// <param name="request">The parsed request DTO.</param>
    /// <param name="httpContext">The HTTP context for response writing.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task HandleAsync(Guid swarmId, RefinementRequestDto request, HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(httpContext);

        this.LogRefinementRequest(swarmId, request.Method ?? "<null>");

        switch (request.Method)
        {
            case "info":
                await this.HandleInfoAsync(swarmId, httpContext).ConfigureAwait(false);
                break;

            case "agent/connect":
                await this.HandleAgentConnectAsync(swarmId, httpContext).ConfigureAwait(false);
                break;

            case "agent/run":
                await this.HandleAgentRunAsync(swarmId, request, httpContext).ConfigureAwait(false);
                break;

            case "agent/stop":
                this.HandleAgentStop();
                httpContext.Response.StatusCode = 200;
                break;

            default:
                this.LogRefinementUnknownMethod(swarmId, request.Method ?? "<null>");
                httpContext.Response.StatusCode = 400;
                await httpContext.Response.WriteAsJsonAsync(
                    new { error = $"Unknown method: {request.Method}" }).ConfigureAwait(false);
                break;
        }
    }

    private async Task HandleInfoAsync(Guid swarmId, HttpContext httpContext)
    {
        var workDir = this.swarmManager.GetWorkDirectory(swarmId);
        if (workDir is null)
        {
            var basePath = ResolveBasePath(swarmId);
            this.LogRefinementWorkDirNotFound(swarmId, basePath);
            throw new SwarmWorkDirNotFoundException(swarmId, basePath);
        }

        var agentsJsonPath = Path.Combine(workDir, ".chat", "agents.json");
        if (!File.Exists(agentsJsonPath))
        {
            this.LogRefinementChatUnavailable(swarmId, agentsJsonPath);
            await httpContext.Response.WriteAsJsonAsync(new
            {
                agents = new Dictionary<string, RefinementAgentInfo>(),
                chatAvailable = false,
            }).ConfigureAwait(false);
            return;
        }

        var json = await File.ReadAllTextAsync(agentsJsonPath).ConfigureAwait(false);
        var agentList = JsonSerializer.Deserialize<List<RefinementAgentInfo>>(json, SwarmJsonOptions.Default)
            ?? [];

        var agentsDict = new Dictionary<string, RefinementAgentInfo>();

        // Always include synthesis.
        agentsDict["synthesis"] = new RefinementAgentInfo
        {
            Name = "synthesis",
            Description = "Synthesis agent - discuss the final report",
        };

        foreach (var agent in agentList)
        {
            agentsDict[agent.Name] = agent;
        }

        this.LogRefinementInfoReturned(swarmId, agentsDict.Count);

        await httpContext.Response.WriteAsJsonAsync(new
        {
            agents = agentsDict,
            mode = "sse",
        }).ConfigureAwait(false);
    }

    private async Task HandleAgentConnectAsync(Guid swarmId, HttpContext httpContext)
    {
        // agent/connect reconnects to an existing SSE stream for a running agent.
        // Our refinement chat is stateless per-request (we don't maintain active runs
        // across requests), so return an empty SSE stream to signal "no active run to
        // reconnect to." CopilotKit accepts this and then calls agent/run when the user
        // actually sends a message.
        this.LogRefinementAgentConnect(swarmId);
        httpContext.Response.ContentType = "text/event-stream";
        httpContext.Response.Headers.CacheControl = "no-cache";
        httpContext.Response.Headers.Connection = "keep-alive";
        await httpContext.Response.Body.FlushAsync(httpContext.RequestAborted).ConfigureAwait(false);
    }

    private async Task HandleAgentRunAsync(Guid swarmId, RefinementRequestDto request, HttpContext httpContext)
    {
        var workDir = this.swarmManager.GetWorkDirectory(swarmId);
        if (workDir is null)
        {
            var basePath = ResolveBasePath(swarmId);
            this.LogRefinementWorkDirNotFound(swarmId, basePath);
            throw new SwarmWorkDirNotFoundException(swarmId, basePath);
        }

        // Extract agentId from params.
        var agentId = "synthesis";
        if (request.Params.HasValue)
        {
            if (request.Params.Value.TryGetProperty("agentId", out var agentIdProp))
            {
                agentId = agentIdProp.GetString() ?? "synthesis";
            }
        }

        this.LogRefinementAgentRunStarted(swarmId, agentId);

        var chatDir = Path.Combine(workDir, ".chat");

        // Load agent metadata for system prompt.
        var agentsJsonPath = Path.Combine(chatDir, "agents.json");
        var displayName = agentId;
        var role = "specialist";
        var siblingNames = new List<string>();
        if (File.Exists(agentsJsonPath))
        {
            var metaJson = await File.ReadAllTextAsync(agentsJsonPath).ConfigureAwait(false);
            var agents = JsonSerializer.Deserialize<List<JsonElement>>(metaJson, SwarmJsonOptions.Default) ?? [];
            foreach (var agent in agents)
            {
                if (!agent.TryGetProperty("name", out var nameProp))
                {
                    continue;
                }

                var name = nameProp.GetString();
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                siblingNames.Add(name);

                if (name == agentId)
                {
                    displayName = agent.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? agentId : agentId;
                    role = agent.TryGetProperty("role", out var r) ? r.GetString() ?? "specialist" : "specialist";
                }
            }
        }

        // Build refinement system prompt, telling the model its own internal
        // name and the full roster so it uses correct ids with refinement tools.
        var systemPrompt = PromptBuilder.ForRefinement(
            displayName,
            role,
            originalSystemPrompt: null,
            currentAgentName: agentId,
            siblingAgentNames: siblingNames);

        // Start each refinement turn with a clean history: just the system
        // prompt. The agent fetches execution context on demand via
        // read_conversation_history / read_driver_prompt; the user-side
        // conversation arrives via request.Body.messages below.
        var history = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
        };

        // Merge new messages from CopilotKit.
        if (request.Body.HasValue && request.Body.Value.TryGetProperty("messages", out var messagesProp))
        {
            foreach (var msgEl in messagesProp.EnumerateArray())
            {
                var msgRole = msgEl.TryGetProperty("role", out var rp) ? rp.GetString() ?? "user" : "user";
                var msgText = msgEl.TryGetProperty("content", out var cp) ? cp.GetString() ?? string.Empty : string.Empty;

                // Only add user messages that aren't already in history.
                if (msgRole == "user" && !history.Any(h => h.Role == ChatRole.User && h.Text == msgText))
                {
                    history.Add(new ChatMessage(new ChatRole(msgRole), msgText));
                }
            }
        }

        // Build tools: file read/write + web_fetch, plus refinement-only tools
        // for pulling the agent's (or a sibling's) transcript and driver prompt.
        var httpClient = this.httpClientFactory.CreateClient("swarm-default-tools");
        var tools = DefaultToolFactory.CreateDefaultTools(workDir, httpClient)
            .Concat(RefinementToolFactory.CreateRefinementTools(workDir, agentId))
            .ToList();

        var chatOptions = new ChatOptions { Tools = tools };

        // Set up SSE response.
        httpContext.Response.ContentType = "text/event-stream";
        httpContext.Response.Headers.CacheControl = "no-cache";
        httpContext.Response.Headers.Connection = "keep-alive";

        // Create adapter for AG-UI events.
        var adapter = new SwarmEventAdapter();
        using var interceptor = new AgUIEventInterceptor(this.chatClient, adapter, agentId);

        this.activeRunCts = CancellationTokenSource.CreateLinkedTokenSource(httpContext.RequestAborted);
        var ct = this.activeRunCts.Token;

        // CopilotKit correlates RUN_STARTED/RUN_FINISHED by runId; reuse the same value.
        var runId = Guid.NewGuid().ToString("N");

        this.LogRefinementRunContext(swarmId, agentId, runId, history.Count, tools.Count);

        // Emit RUN_STARTED.
        var runStarted = SseEventWriter.FormatAgUIEvent(new RunStartedEvent
        {
            ThreadId = swarmId.ToString(),
            RunId = runId,
        });
        await httpContext.Response.WriteAsync(runStarted, ct).ConfigureAwait(false);
        await httpContext.Response.Body.FlushAsync(ct).ConfigureAwait(false);

        var startTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        var eventCount = 0;

        try
        {
            this.LogRefinementLlmCallStart(swarmId, agentId);

            // Start LLM call in background, read events from adapter.
            var llmTask = Task.Run(
                async () =>
                {
                    try
                    {
                        await interceptor.GetResponseAsync(history, chatOptions, ct).ConfigureAwait(false);
                        var elapsedMs = (int)System.Diagnostics.Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                        this.LogRefinementLlmCallEnd(swarmId, agentId, elapsedMs);
                    }
                    catch (Exception llmEx) when (llmEx is not OperationCanceledException)
                    {
                        this.LogRefinementLlmCallFailed(swarmId, agentId, llmEx);
                        throw;
                    }
                    finally
                    {
                        adapter.Complete();
                    }
                },
                ct);

            // Stream events to SSE.
            try
            {
                await foreach (var evt in adapter.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    eventCount++;
                    var (evtType, evtDetail) = DescribeSseEvent(evt);
                    this.LogRefinementSseEvent(swarmId, agentId, evtType, evtDetail);
                    var message = SseEventWriter.FormatAgUIEvent(evt);
                    await httpContext.Response.WriteAsync(message, ct).ConfigureAwait(false);
                    await httpContext.Response.Body.FlushAsync(ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Client disconnected or stop requested.
                this.LogRefinementRunCancelled(swarmId, agentId, eventCount);
            }

            await llmTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation.
            this.LogRefinementRunCancelled(swarmId, agentId, eventCount);
        }
        catch (Exception ex)
        {
            this.LogRefinementChatError(swarmId, agentId, ex);
            await EmitErrorTextMessageAsync(httpContext.Response, agentId, ex).ConfigureAwait(false);
        }

        // Emit RUN_FINISHED.
        try
        {
            var runFinished = SseEventWriter.FormatAgUIEvent(new RunFinishedEvent
            {
                ThreadId = swarmId.ToString(),
                RunId = runId,
            });
            await httpContext.Response.WriteAsync(runFinished, CancellationToken.None).ConfigureAwait(false);
            await httpContext.Response.Body.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Client may have disconnected.
        }

        var totalElapsedMs = (int)System.Diagnostics.Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        this.LogRefinementRunFinished(swarmId, agentId, runId, totalElapsedMs, eventCount);

        // No server-side persistence for refinement turns: CopilotKit keeps
        // the UI-side conversation and replays it on every agent/run. The
        // persisted .chat/{agentId}.jsonl remains the authoritative
        // execution transcript that the read_conversation_history tool
        // surfaces on demand.
    }

    private void HandleAgentStop()
    {
        this.LogRefinementAgentStop();
        this.activeRunCts?.Cancel();
    }

    /// <summary>
    /// Produces a compact type + detail pair for an outbound AG-UI event so
    /// the <c>Refinement SSE event</c> debug log includes useful context
    /// (tool names, arg snippets, message-text previews) rather than just
    /// the event class name.
    /// </summary>
    private static (string Type, string Detail) DescribeSseEvent(SwarmAgUIEvent evt)
    {
        const int previewMax = 120;
        return evt switch
        {
            RunStartedEvent rs => ("RUN_STARTED", $"threadId={rs.ThreadId} runId={rs.RunId}"),
            RunFinishedEvent rf => ("RUN_FINISHED", $"threadId={rf.ThreadId} runId={rf.RunId}"),
            RunErrorEvent re => ("RUN_ERROR", $"message={Preview(re.Message, previewMax)}"),
            ToolCallStartEvent tcs => ("TOOL_CALL_START", $"toolCallId={tcs.ToolCallId} tool={tcs.ToolCallName} agent={tcs.AgentName ?? "(none)"}"),
            ToolCallArgsEvent tca => ("TOOL_CALL_ARGS", $"toolCallId={tca.ToolCallId} delta={Preview(tca.Delta, previewMax)}"),
            ToolCallEndEvent tce => ("TOOL_CALL_END", $"toolCallId={tce.ToolCallId}"),
            ToolCallResultEvent tcr => ("TOOL_CALL_RESULT", $"toolCallId={tcr.ToolCallId} content={Preview(tcr.Content, previewMax)}"),
            TextMessageStartEvent tms => ("TEXT_MESSAGE_START", $"messageId={tms.MessageId} role={tms.Role} agent={tms.AgentName ?? "(none)"}"),
            TextMessageContentEvent tmc => ("TEXT_MESSAGE_CONTENT", $"messageId={tmc.MessageId} delta={Preview(tmc.Delta, previewMax)}"),
            TextMessageEndEvent tme => ("TEXT_MESSAGE_END", $"messageId={tme.MessageId}"),
            _ => (evt.GetType().Name, string.Empty),
        };

        static string Preview(string? value, int max)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "(empty)";
            }

            var oneLine = value.Replace('\n', ' ').Replace('\r', ' ');
            return oneLine.Length <= max ? oneLine : oneLine[..max] + "…";
        }
    }

    private static string ResolveBasePath(Guid swarmId)
    {
        // Best-effort path for the exception message.
        return Path.Combine(Path.GetTempPath(), "swarm-work", swarmId.ToString());
    }

    /// <summary>
    /// Maps an exception from the refinement LLM pipeline to a user-facing
    /// message the chat UI can display. Keeps the technical detail out of the
    /// user's face while still distinguishing common recoverable cases
    /// (content filter, rate limit) from a generic failure.
    /// </summary>
    private static string GetFriendlyErrorMessage(Exception ex)
    {
        var message = ex.Message ?? string.Empty;

        if (message.Contains("content_filter", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("content management policy", StringComparison.OrdinalIgnoreCase))
        {
            return "⚠️ Your message was blocked by Azure OpenAI's content management policy. "
                + "This usually happens when the prompt, the agent's original execution context, or your "
                + "question contains material the safety filter flags (e.g., policy-sensitive language). "
                + "Please rephrase your question and try again.";
        }

        if (message.Contains("429", StringComparison.Ordinal) ||
            message.Contains("TooManyRequests", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
        {
            return "⚠️ The model is currently rate-limited. Please wait a moment and try again.";
        }

        if (message.Contains("401", StringComparison.Ordinal) ||
            message.Contains("403", StringComparison.Ordinal) ||
            message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            return "⚠️ The refinement service could not authenticate to the model. Ask an admin to check the API key configuration.";
        }

        return "⚠️ Something went wrong while processing your request. Please try rephrasing your question.";
    }

    /// <summary>
    /// Writes a synthetic <c>TEXT_MESSAGE_*</c> sequence directly to the SSE
    /// response so the chat UI renders an assistant bubble explaining the
    /// error. Called from the top-level catch after the adapter has been
    /// drained or aborted, before <c>RUN_FINISHED</c> is emitted.
    /// </summary>
    private static async Task EmitErrorTextMessageAsync(
        HttpResponse response,
        string agentName,
        Exception ex)
    {
        var friendly = GetFriendlyErrorMessage(ex);
        var messageId = Guid.NewGuid().ToString("N");

        var start = SseEventWriter.FormatAgUIEvent(new TextMessageStartEvent
        {
            MessageId = messageId,
            Role = ChatRole.Assistant.Value,
            AgentName = agentName,
        });
        var content = SseEventWriter.FormatAgUIEvent(new TextMessageContentEvent
        {
            MessageId = messageId,
            Delta = friendly,
        });
        var end = SseEventWriter.FormatAgUIEvent(new TextMessageEndEvent
        {
            MessageId = messageId,
        });

        try
        {
            await response.WriteAsync(start, CancellationToken.None).ConfigureAwait(false);
            await response.WriteAsync(content, CancellationToken.None).ConfigureAwait(false);
            await response.WriteAsync(end, CancellationToken.None).ConfigureAwait(false);
            await response.Body.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Client already gone — best effort, let RUN_FINISHED still attempt below.
        }
    }

    [LoggerMessage(EventId = 200, Level = LogLevel.Error, Message = "Refinement chat error for swarm {SwarmId}, agent {AgentId}.")]
    private partial void LogRefinementChatError(Guid swarmId, string agentId, Exception exception);

    [LoggerMessage(EventId = 202, Level = LogLevel.Information, Message = "Refinement request received for swarm {SwarmId}: method={Method}.")]
    private partial void LogRefinementRequest(Guid swarmId, string method);

    [LoggerMessage(EventId = 203, Level = LogLevel.Warning, Message = "Refinement unknown method for swarm {SwarmId}: {Method}.")]
    private partial void LogRefinementUnknownMethod(Guid swarmId, string method);

    [LoggerMessage(EventId = 204, Level = LogLevel.Warning, Message = "Refinement work directory not found for swarm {SwarmId} at {ExpectedPath}.")]
    private partial void LogRefinementWorkDirNotFound(Guid swarmId, string expectedPath);

    [LoggerMessage(EventId = 205, Level = LogLevel.Information, Message = "Refinement chat unavailable for swarm {SwarmId}: .chat/agents.json not found at {Path}.")]
    private partial void LogRefinementChatUnavailable(Guid swarmId, string path);

    [LoggerMessage(EventId = 206, Level = LogLevel.Information, Message = "Refinement info returned for swarm {SwarmId}: {AgentCount} agents.")]
    private partial void LogRefinementInfoReturned(Guid swarmId, int agentCount);

    [LoggerMessage(EventId = 207, Level = LogLevel.Information, Message = "Refinement agent/connect (empty SSE) for swarm {SwarmId}.")]
    private partial void LogRefinementAgentConnect(Guid swarmId);

    [LoggerMessage(EventId = 208, Level = LogLevel.Information, Message = "Refinement agent/run started for swarm {SwarmId}, agent {AgentId}.")]
    private partial void LogRefinementAgentRunStarted(Guid swarmId, string agentId);

    [LoggerMessage(EventId = 209, Level = LogLevel.Information, Message = "Refinement agent/stop requested.")]
    private partial void LogRefinementAgentStop();

    [LoggerMessage(EventId = 210, Level = LogLevel.Information, Message = "Refinement run context for swarm {SwarmId}, agent {AgentId}: runId={RunId}, messageCount={MessageCount}, toolCount={ToolCount}.")]
    private partial void LogRefinementRunContext(Guid swarmId, string agentId, string runId, int messageCount, int toolCount);

    [LoggerMessage(EventId = 211, Level = LogLevel.Information, Message = "Refinement LLM call starting for swarm {SwarmId}, agent {AgentId}.")]
    private partial void LogRefinementLlmCallStart(Guid swarmId, string agentId);

    [LoggerMessage(EventId = 212, Level = LogLevel.Information, Message = "Refinement LLM call completed for swarm {SwarmId}, agent {AgentId} in {ElapsedMs}ms.")]
    private partial void LogRefinementLlmCallEnd(Guid swarmId, string agentId, int elapsedMs);

    [LoggerMessage(EventId = 213, Level = LogLevel.Error, Message = "Refinement LLM call failed for swarm {SwarmId}, agent {AgentId}.")]
    private partial void LogRefinementLlmCallFailed(Guid swarmId, string agentId, Exception exception);

    [LoggerMessage(EventId = 214, Level = LogLevel.Debug, Message = "Refinement SSE event for swarm {SwarmId}, agent {AgentId}: {EventType} {EventDetail}.")]
    private partial void LogRefinementSseEvent(Guid swarmId, string agentId, string eventType, string eventDetail);

    [LoggerMessage(EventId = 215, Level = LogLevel.Warning, Message = "Refinement run cancelled for swarm {SwarmId}, agent {AgentId} after {EventCount} SSE events.")]
    private partial void LogRefinementRunCancelled(Guid swarmId, string agentId, int eventCount);

    [LoggerMessage(EventId = 216, Level = LogLevel.Information, Message = "Refinement run finished for swarm {SwarmId}, agent {AgentId}: runId={RunId}, elapsedMs={ElapsedMs}, sseEvents={EventCount}.")]
    private partial void LogRefinementRunFinished(Guid swarmId, string agentId, string runId, int elapsedMs, int eventCount);
}
