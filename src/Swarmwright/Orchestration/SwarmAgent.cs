using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Swarmwright.Models.Enums;

namespace Swarmwright.Orchestration;

/// <summary>
/// Wraps an <see cref="IChatClient"/> and manages the AI conversation loop for a single worker agent.
/// </summary>
public partial class SwarmAgent
{
    private readonly string systemPrompt;
    private readonly IList<AITool> tools;
    private readonly IChatClient chatClient;
    /*NOTE: CircuitBreaker removed. FunctionInvokingChatClient handles tool invocation internally.
    Re-integrate via FunctionInvokingChatClient.MaximumIterationsPerRequest when needed.*/
    private readonly ILogger<SwarmAgent> logger;
    private bool systemMessageAdded;

    /// <summary>
    /// Initializes a new instance of the <see cref="SwarmAgent"/> class.
    /// </summary>
    /// <param name="name">The unique name of the agent.</param>
    /// <param name="role">The specialist role of the agent.</param>
    /// <param name="displayName">The human-readable display name.</param>
    /// <param name="systemPrompt">The system prompt that defines agent behavior.</param>
    /// <param name="tools">The list of AI tools available to the agent.</param>
    /// <param name="chatClient">The chat client used for AI communication.</param>
    /// <param name="logger">An optional logger for structured logging.</param>
    /// <param name="systemPromptCore">The driver prompt minus swarm-coordination mandates, for refinement-chat snapshotting.</param>
    public SwarmAgent(
        string name,
        string role,
        string displayName,
        string systemPrompt,
        IList<AITool> tools,
        IChatClient chatClient,
        ILogger<SwarmAgent>? logger = null,
        string? systemPromptCore = null)
    {
        this.Name = name;
        this.Role = role;
        this.DisplayName = displayName;
        this.systemPrompt = systemPrompt;
        this.SystemPromptCore = systemPromptCore;
        this.tools = tools;
        this.chatClient = chatClient;
        this.logger = logger ?? NullLogger<SwarmAgent>.Instance;
    }

    /// <summary>
    /// Gets the unique name of the agent.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the specialist role of the agent.
    /// </summary>
    public string Role { get; }

    /// <summary>
    /// Gets the human-readable display name.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// Gets the conversation history for the agent session.
    /// </summary>
    public List<ChatMessage> ConversationHistory { get; } = [];

    /// <summary>
    /// Gets the driver prompt that shaped this agent's execution-time behavior,
    /// minus swarm-coordination tool mandates. Persisted to
    /// <c>.chat/{name}.system.md</c> for later use by refinement chat.
    /// </summary>
    public string? SystemPromptCore { get; }

    /// <summary>
    /// Executes a task by building a prompt and running the AI conversation loop.
    /// </summary>
    /// <param name="task">The swarm task to execute.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>
    /// A <see cref="TaskExecutionResult"/> describing the worker's final text output
    /// and whether the worker explicitly declared a terminal status via <c>task_update</c>.
    /// </returns>
    public async Task<TaskExecutionResult> ExecuteTaskAsync(
        Models.SwarmTask task,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        this.LogTaskStarted(task.Subject, this.Name);

        var taskPrompt = $"Task: {task.Subject}\n\nDescription: {task.Description}";

        this.EnsureSystemMessage();
        this.ConversationHistory.Add(new ChatMessage(ChatRole.User, taskPrompt));

        var result = await this.RunConversationLoopAsync(cancellationToken).ConfigureAwait(false);

        this.LogTaskCompleted(task.Subject, this.Name);

        return result;
    }

    /// <summary>
    /// Resumes an existing session with a nudge message.
    /// </summary>
    /// <param name="nudgeMessage">The nudge message to append to the conversation.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>
    /// A <see cref="TaskExecutionResult"/> describing the worker's final text output
    /// and whether the worker explicitly declared a terminal status via <c>task_update</c>
    /// during the resumed turn.
    /// </returns>
    public async Task<TaskExecutionResult> ResumeSessionAsync(
        string nudgeMessage,
        CancellationToken cancellationToken = default)
    {
        this.ConversationHistory.Add(new ChatMessage(ChatRole.User, nudgeMessage));

        return await this.RunConversationLoopAsync(cancellationToken).ConfigureAwait(false);
    }

    private void EnsureSystemMessage()
    {
        if (!this.systemMessageAdded)
        {
            this.ConversationHistory.Insert(0, new ChatMessage(ChatRole.System, this.systemPrompt));
            this.systemMessageAdded = true;
        }
    }

    private async Task<TaskExecutionResult> RunConversationLoopAsync(CancellationToken cancellationToken)
    {
        // FunctionInvokingChatClient handles tool invocation automatically:
        // it intercepts tool calls, invokes AIFunctions, sends results back to the model,
        // and returns the final text response. We just call GetResponseAsync once.
        var chatOptions = new ChatOptions
        {
            Tools = this.tools,
        };

        var response = await this.chatClient.GetResponseAsync(
            this.ConversationHistory,
            chatOptions,
            cancellationToken).ConfigureAwait(false);

        this.LogLlmResponse(this.Name);
        this.LogWorkerResponseDetail(this.Name, response.Messages.Count);

        // Log what the worker did — tool calls and final text
        foreach (var msg in response.Messages)
        {
            foreach (var content in msg.Contents)
            {
                if (content is FunctionCallContent fc)
                {
                    this.LogToolInvocation(fc.Name, this.Name);
                }
                else if (content is FunctionResultContent fr)
                {
                    var resultText = fr.Result?.ToString() ?? "(null)";
                    this.LogToolResult(this.Name, resultText.Length > 200 ? resultText[..200] + "..." : resultText);
                }
            }

            if (!string.IsNullOrEmpty(msg.Text))
            {
                var preview = msg.Text.Length > 300 ? msg.Text[..300] + "..." : msg.Text;
                this.LogWorkerOutput(this.Name, msg.Role.Value, preview);
            }
        }

        // Add all response messages (including intermediate tool call/result) to history.
        foreach (var message in response.Messages)
        {
            this.ConversationHistory.Add(message);
        }

        var finalText = response.Messages
            .LastOrDefault(m => !string.IsNullOrEmpty(m.Text))?.Text ?? string.Empty;

        var (declaredStatus, declaredResult) = TryExtractTaskUpdate(response.Messages);

        if (declaredStatus is null)
        {
            this.LogWorkerDidNotSignalCompletion(this.Name);
        }

        return new TaskExecutionResult(finalText, declaredStatus, declaredResult);
    }

    /// <summary>
    /// Walks the response messages and pairs <see cref="FunctionCallContent"/> entries
    /// for <c>task_update</c> with their corresponding <see cref="FunctionResultContent"/>
    /// by <c>CallId</c>. Returns the last successful match in message order.
    /// </summary>
    /// <param name="messages">The messages returned by the chat client.</param>
    /// <returns>
    /// A tuple containing the declared status and optional result text from the last
    /// successful <c>task_update</c> invocation, or <c>(null, null)</c> if none.
    /// </returns>
    private static (TaskState? Status, string? Result) TryExtractTaskUpdate(
        IList<ChatMessage> messages)
    {
        // Pass 1: collect every task_update FunctionCallContent keyed by CallId, parsing
        // the status/result arguments out of FunctionCallContent.Arguments as we go.
        var pendingCalls = new Dictionary<string, (TaskState Status, string? Result)>(StringComparer.Ordinal);

        foreach (var message in messages)
        {
            foreach (var content in message.Contents)
            {
                if (content is not FunctionCallContent fc)
                {
                    continue;
                }

                if (!string.Equals(fc.Name, "task_update", StringComparison.Ordinal))
                {
                    continue;
                }

                if (string.IsNullOrEmpty(fc.CallId))
                {
                    continue;
                }

                if (!TryParseTaskUpdateArguments(fc.Arguments, out var status, out var resultArg))
                {
                    continue;
                }

                pendingCalls[fc.CallId] = (status, resultArg);
            }
        }

        if (pendingCalls.Count == 0)
        {
            return (null, null);
        }

        // Pass 2: walk messages again and match FunctionResultContent entries by CallId.
        // Track the last successful match in message order.
        TaskState? lastStatus = null;
        string? lastResult = null;

        foreach (var message in messages)
        {
            foreach (var content in message.Contents)
            {
                if (content is not FunctionResultContent fr)
                {
                    continue;
                }

                if (string.IsNullOrEmpty(fr.CallId))
                {
                    continue;
                }

                if (!pendingCalls.TryGetValue(fr.CallId, out var pending))
                {
                    continue;
                }

                if (!IsToolResultSuccessful(fr.Result))
                {
                    continue;
                }

                lastStatus = pending.Status;
                lastResult = pending.Result;
            }
        }

        return (lastStatus, lastResult);
    }

    /// <summary>
    /// Parses the <c>status</c> and optional <c>result</c> arguments from a
    /// <see cref="FunctionCallContent.Arguments"/> dictionary.
    /// </summary>
    /// <param name="arguments">The arguments dictionary, possibly <see langword="null"/>.</param>
    /// <param name="status">When this method returns <see langword="true"/>, the parsed status.</param>
    /// <param name="result">When this method returns <see langword="true"/>, the optional result text.</param>
    /// <returns><see langword="true"/> if a valid status argument was found; otherwise <see langword="false"/>.</returns>
    private static bool TryParseTaskUpdateArguments(
        IDictionary<string, object?>? arguments,
        out TaskState status,
        out string? result)
    {
        status = default;
        result = null;

        if (arguments is null)
        {
            return false;
        }

        if (!arguments.TryGetValue("status", out var statusObj) || statusObj is null)
        {
            return false;
        }

        var statusText = statusObj switch
        {
            string s => s,
            JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
            _ => statusObj.ToString(),
        };

        if (!Enum.TryParse(statusText, ignoreCase: true, out status))
        {
            return false;
        }

        if (arguments.TryGetValue("result", out var resultObj) && resultObj is not null)
        {
            result = resultObj switch
            {
                string s => s,
                JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
                _ => resultObj.ToString(),
            };
        }

        return true;
    }

    /// <summary>
    /// Determines whether a tool <see cref="FunctionResultContent.Result"/> payload
    /// represents a successful invocation. The contract is that the swarm tools return
    /// either <c>{"success":true,...}</c> or <c>{"error":"..."}</c>.
    /// </summary>
    /// <param name="toolResult">The raw tool result object.</param>
    /// <returns><see langword="true"/> if the result JSON contains a <c>success</c> field with value <c>true</c>.</returns>
    /// <remarks>
    /// <para>
    /// The real <c>FunctionInvokingChatClient</c> pipeline wraps a tool's string
    /// return value in a <see cref="JsonElement"/> whose <see cref="JsonValueKind"/>
    /// is <see cref="JsonValueKind.String"/>. Calling <see cref="JsonElement.GetRawText"/>
    /// on such an element returns the JSON-encoded string literal (escaped quotes
    /// included) rather than the inner object JSON, which then fails to parse as
    /// an object. We must call <see cref="JsonElement.GetString"/> for
    /// <see cref="JsonValueKind.String"/> elements to unwrap to the raw inner text.
    /// </para>
    /// <para>
    /// We also handle <see cref="System.Text.Json.Nodes.JsonNode"/>, dictionaries,
    /// and plain strings defensively so unit tests that construct tool results by
    /// hand keep working alongside the production FIC path.
    /// </para>
    /// </remarks>
    private static bool IsToolResultSuccessful(object? toolResult)
    {
        if (toolResult is null)
        {
            return false;
        }

        var raw = UnwrapToolResultToJson(toolResult);

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (document.RootElement.TryGetProperty("error", out _))
            {
                return false;
            }

            return document.RootElement.TryGetProperty("success", out var successProp)
                && successProp.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Unwraps a tool result object (as delivered via <see cref="FunctionResultContent.Result"/>)
    /// into the raw JSON text the helper needs to parse. Handles strings,
    /// <see cref="JsonElement"/> (with string-kind unwrapping), <see cref="System.Text.Json.Nodes.JsonNode"/>,
    /// and dictionaries.
    /// </summary>
    /// <param name="toolResult">The non-null tool result object.</param>
    /// <returns>The raw JSON text, or <see langword="null"/> if unwrapping failed.</returns>
    private static string? UnwrapToolResultToJson(object toolResult)
    {
        return toolResult switch
        {
            string s => s,

            // FIC wraps string-returning tool results as JsonElement with
            // ValueKind == String. GetRawText() would return the *escaped* JSON
            // string literal; GetString() returns the inner raw text. Fall back
            // to GetRawText() for object/array kinds so callers still get real JSON.
            JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
            JsonElement je => je.GetRawText(),

            System.Text.Json.Nodes.JsonNode jn => jn.GetValueKind() == JsonValueKind.String
                ? jn.GetValue<string>()
                : jn.ToJsonString(),

            IDictionary<string, object?> dict => JsonSerializer.Serialize(dict),

            _ => toolResult.ToString(),
        };
    }

    [LoggerMessage(EventId = 200, Level = LogLevel.Information, Message = "Task started: '{TaskSubject}' by agent {AgentName}.")]
    private partial void LogTaskStarted(string taskSubject, string agentName);

    [LoggerMessage(EventId = 201, Level = LogLevel.Information, Message = "Task completed: '{TaskSubject}' by agent {AgentName}.")]
    private partial void LogTaskCompleted(string taskSubject, string agentName);

    [LoggerMessage(EventId = 202, Level = LogLevel.Debug, Message = "Tool invocation: {ToolName} by agent {AgentName}.")]
    private partial void LogToolInvocation(string toolName, string agentName);

    [LoggerMessage(EventId = 203, Level = LogLevel.Warning, Message = "Tool failure: {ToolName} by agent {AgentName}.")]
    private partial void LogToolFailure(string toolName, string agentName, Exception exception);

    [LoggerMessage(EventId = 204, Level = LogLevel.Error, Message = "Circuit breaker tripped for agent {AgentName} after {Failures} consecutive failures.")]
    private partial void LogCircuitBreakerTripped(string agentName, int failures);

    [LoggerMessage(EventId = 205, Level = LogLevel.Debug, Message = "LLM response received for agent {AgentName}.")]
    private partial void LogLlmResponse(string agentName);

    [LoggerMessage(EventId = 206, Level = LogLevel.Information, Message = "Worker {AgentName} response: {MessageCount} messages in conversation.")]
    private partial void LogWorkerResponseDetail(string agentName, int messageCount);

    [LoggerMessage(EventId = 207, Level = LogLevel.Debug, Message = "Worker {AgentName} tool result: {Result}")]
    private partial void LogToolResult(string agentName, string result);

    [LoggerMessage(EventId = 208, Level = LogLevel.Information, Message = "Worker {AgentName} output [{Role}]: {Preview}")]
    private partial void LogWorkerOutput(string agentName, string role, string preview);

    [LoggerMessage(EventId = 209, Level = LogLevel.Warning, Message = "Worker {AgentName} did not signal task completion via task_update; task will be marked failed.")]
    private partial void LogWorkerDidNotSignalCompletion(string agentName);
}
