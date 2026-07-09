using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Swarmwright.Events.AgUI;

/// <summary>
/// A delegating <see cref="IChatClient"/> that intercepts chat responses and emits
/// AG-UI protocol events for tool calls and text messages. Sits in the chat client
/// pipeline after <c>FunctionInvokingChatClient</c>.
/// </summary>
public sealed class AgUIEventInterceptor : IChatClient
{
    /// <summary>
    /// The shared <see cref="JsonSerializerOptions"/> used to serialize tool
    /// call arguments and results. Routed through <see cref="SwarmJsonOptions.Default"/>
    /// so enum values serialize as strings across every swarm JSON writer.
    /// Exposed as <c>internal</c> for verification by the test assembly via
    /// <c>InternalsVisibleTo</c>.
    /// </summary>
    internal static readonly JsonSerializerOptions JsonOptions = SwarmJsonOptions.Default;

    private readonly IChatClient inner;
    private readonly SwarmEventAdapter adapter;
    private readonly string agentName;

    /// <summary>
    /// Initializes a new instance of the <see cref="AgUIEventInterceptor"/> class.
    /// </summary>
    /// <param name="inner">The inner chat client to delegate to.</param>
    /// <param name="adapter">The event adapter to emit AG-UI events to.</param>
    /// <param name="agentName">The name of the agent using this client.</param>
    public AgUIEventInterceptor(IChatClient inner, SwarmEventAdapter adapter, string agentName)
    {
        this.inner = inner;
        this.adapter = adapter;
        this.agentName = agentName;
    }

    /// <inheritdoc/>
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = await this.inner.GetResponseAsync(messages, options, cancellationToken)
            .ConfigureAwait(false);

        foreach (var message in response.Messages)
        {
            await this.EmitEventsForMessageAsync(message).ConfigureAwait(false);
        }

        return response;
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return this.inner.GetStreamingResponseAsync(messages, options, cancellationToken);
    }

    /// <summary>
    /// Disposes this interceptor. The inner <see cref="IChatClient"/> is
    /// intentionally NOT disposed here — it is a shared singleton owned by the
    /// DI container. Disposing it on one request would break every subsequent
    /// swarm run and refinement call host-wide. The interceptor owns no
    /// disposable state of its own.
    /// </summary>
    public void Dispose()
    {
        // Intentionally no-op. See class-level XML comment.
    }

    /// <inheritdoc/>
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        return this.inner.GetService(serviceType, serviceKey);
    }

    private async Task EmitEventsForMessageAsync(ChatMessage message)
    {
        // One messageId per ChatMessage. Threaded through all content emitters
        // so that a message with both a tool call and text content surfaces as
        // a single assistant message in the AG-UI apply layer's messages[]
        // (parentMessageId on TOOL_CALL_START and messageId on TEXT_MESSAGE_*
        // point at the same id). Also satisfies ToolCallResultEventSchema,
        // which requires messageId (Zod rejects the event otherwise and
        // errors the entire SSE stream client-side).
        var messageId = Guid.NewGuid().ToString("N");

        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case FunctionCallContent funcCall:
                    await this.EmitToolCallEventsAsync(funcCall, messageId).ConfigureAwait(false);
                    break;

                case FunctionResultContent funcResult:
                    await this.EmitToolCallResultAsync(funcResult, messageId).ConfigureAwait(false);
                    break;

                case TextContent textContent when !string.IsNullOrEmpty(textContent.Text):
                    await this.EmitTextMessageEventsAsync(message.Role, textContent.Text, messageId)
                        .ConfigureAwait(false);
                    break;

                default:
                    // Other content types (DataContent, etc.) are not mapped to AG-UI events.
                    break;
            }
        }
    }

    private async Task EmitToolCallEventsAsync(FunctionCallContent funcCall, string parentMessageId)
    {
        var argsJson = funcCall.Arguments is not null
            ? JsonSerializer.Serialize(funcCall.Arguments, JsonOptions)
            : "{}";

        await this.adapter.EmitAsync(new ToolCallStartEvent
        {
            ToolCallId = funcCall.CallId ?? Guid.NewGuid().ToString("N"),
            ToolCallName = funcCall.Name,
            ParentMessageId = parentMessageId,
            AgentName = this.agentName,
        }).ConfigureAwait(false);

        await this.adapter.EmitAsync(new ToolCallArgsEvent
        {
            ToolCallId = funcCall.CallId ?? string.Empty,
            Delta = argsJson,
        }).ConfigureAwait(false);

        await this.adapter.EmitAsync(new ToolCallEndEvent
        {
            ToolCallId = funcCall.CallId ?? string.Empty,
        }).ConfigureAwait(false);
    }

    private async Task EmitToolCallResultAsync(FunctionResultContent funcResult, string messageId)
    {
        var resultContent = funcResult.Result switch
        {
            null => string.Empty,
            string s => s,
            JsonElement je => je.GetRawText(),
            _ => JsonSerializer.Serialize(funcResult.Result, JsonOptions),
        };

        await this.adapter.EmitAsync(new ToolCallResultEvent
        {
            MessageId = messageId,
            ToolCallId = funcResult.CallId ?? string.Empty,
            Content = resultContent,
            Role = "tool",
        }).ConfigureAwait(false);
    }

    private async Task EmitTextMessageEventsAsync(ChatRole role, string text, string messageId)
    {
        await this.adapter.EmitAsync(new TextMessageStartEvent
        {
            MessageId = messageId,
            Role = role.Value,
            AgentName = this.agentName,
        }).ConfigureAwait(false);

        await this.adapter.EmitAsync(new TextMessageContentEvent
        {
            MessageId = messageId,
            Delta = text,
        }).ConfigureAwait(false);

        await this.adapter.EmitAsync(new TextMessageEndEvent
        {
            MessageId = messageId,
        }).ConfigureAwait(false);
    }
}
