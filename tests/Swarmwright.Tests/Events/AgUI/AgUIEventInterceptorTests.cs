using System.Text.Json;
using Swarmwright.Events.AgUI;
using FluentAssertions;
using Microsoft.Extensions.AI;

namespace Swarmwright.Tests.Events.AgUI;

[TestClass]
public class AgUIEventInterceptorTests
{
    // -----------------------------------------------------------------------
    // Text content -> TEXT_MESSAGE_* events
    // -----------------------------------------------------------------------

    [TestMethod]
    public async Task GetResponseAsync_TextContent_Emits_TextMessage_Sequence()
    {
        // Arrange: inner client returns a response with text content
        var adapter = new SwarmEventAdapter();
        var innerResponse = new ChatResponse(
            [new ChatMessage(ChatRole.Assistant, "Hello from the agent")])
        {
            ModelId = "test-model",
        };
        using var inner = new FakeChatClient(innerResponse);
        using var interceptor = new AgUIEventInterceptor(inner, adapter, "researcher");

        // Act
        var result = await interceptor.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Hi")]);

        // Assert — response passes through unchanged
        result.Messages.Should().HaveCount(1);
        result.Messages[0].Text.Should().Be("Hello from the agent");

        // Assert — TEXT_MESSAGE_START, CONTENT, END emitted
        var events = await DrainEventsAsync(adapter, 3);
        events[0].Should().BeOfType<TextMessageStartEvent>();
        var start = (TextMessageStartEvent)events[0];
        start.Role.Should().Be("assistant");
        start.AgentName.Should().Be("researcher");

        events[1].Should().BeOfType<TextMessageContentEvent>();
        ((TextMessageContentEvent)events[1]).Delta.Should().Be("Hello from the agent");

        events[2].Should().BeOfType<TextMessageEndEvent>();
        ((TextMessageEndEvent)events[2]).MessageId.Should().Be(start.MessageId);
    }

    // -----------------------------------------------------------------------
    // FunctionCallContent -> TOOL_CALL_START + ARGS + END
    // -----------------------------------------------------------------------

    [TestMethod]
    public async Task GetResponseAsync_FunctionCallContent_Emits_ToolCall_Events()
    {
        var adapter = new SwarmEventAdapter();
        var funcCall = new FunctionCallContent(
            "call-123",
            "task_update",
            new Dictionary<string, object?> { ["taskId"] = "t1", ["status"] = "Completed" });
        var msg = new ChatMessage(ChatRole.Assistant, [funcCall]);
        var innerResponse = new ChatResponse([msg]);
        using var inner = new FakeChatClient(innerResponse);
        using var interceptor = new AgUIEventInterceptor(inner, adapter, "worker");

        await interceptor.GetResponseAsync([new ChatMessage(ChatRole.User, "do work")]);

        var events = await DrainEventsAsync(adapter, 3);
        events[0].Should().BeOfType<ToolCallStartEvent>();
        var toolStart = (ToolCallStartEvent)events[0];
        toolStart.ToolCallId.Should().Be("call-123");
        toolStart.ToolCallName.Should().Be("task_update");
        toolStart.AgentName.Should().Be("worker");

        events[1].Should().BeOfType<ToolCallArgsEvent>();
        var argsEvt = (ToolCallArgsEvent)events[1];
        argsEvt.ToolCallId.Should().Be("call-123");
        argsEvt.Delta.Should().Contain("taskId");

        events[2].Should().BeOfType<ToolCallEndEvent>();
        ((ToolCallEndEvent)events[2]).ToolCallId.Should().Be("call-123");
    }

    // -----------------------------------------------------------------------
    // FunctionResultContent -> TOOL_CALL_RESULT
    // -----------------------------------------------------------------------

    [TestMethod]
    public async Task GetResponseAsync_FunctionResultContent_Emits_ToolCallResult()
    {
        var adapter = new SwarmEventAdapter();
        var funcResult = new FunctionResultContent("call-456", "{\"success\":true}");
        var msg = new ChatMessage(ChatRole.Tool, [funcResult]);
        var innerResponse = new ChatResponse([msg]);
        using var inner = new FakeChatClient(innerResponse);
        using var interceptor = new AgUIEventInterceptor(inner, adapter, "worker");

        await interceptor.GetResponseAsync([new ChatMessage(ChatRole.User, "do work")]);

        var events = await DrainEventsAsync(adapter, 1);
        events[0].Should().BeOfType<ToolCallResultEvent>();
        var result = (ToolCallResultEvent)events[0];
        result.ToolCallId.Should().Be("call-456");
        result.Content.Should().Contain("success");
    }

    // -----------------------------------------------------------------------
    // MessageId correlation — AG-UI clients validate TOOL_CALL_RESULT.messageId
    // as a required string (Zod rejects events without it and errors the whole
    // SSE stream, leaving the UI blank). Tool calls also need parentMessageId
    // to bind to their assistant message in the client's messages[] tree.
    // -----------------------------------------------------------------------

    [TestMethod]
    public async Task GetResponseAsync_FunctionResultContent_EmitsToolCallResultWithMessageId()
    {
        var adapter = new SwarmEventAdapter();
        var funcResult = new FunctionResultContent("call-777", "{\"ok\":true}");
        var msg = new ChatMessage(ChatRole.Tool, [funcResult]);
        using var inner = new FakeChatClient(new ChatResponse([msg]));
        using var interceptor = new AgUIEventInterceptor(inner, adapter, "worker");

        await interceptor.GetResponseAsync([new ChatMessage(ChatRole.User, "run")]);

        var events = await DrainEventsAsync(adapter, 1);
        var result = (ToolCallResultEvent)events[0];
        result.MessageId.Should().NotBeNullOrEmpty(
            "AG-UI ToolCallResultEventSchema requires messageId; missing it errors the client SSE stream and leaves the UI blank");
        result.ToolCallId.Should().Be("call-777");
    }

    [TestMethod]
    public async Task GetResponseAsync_FunctionCallContent_EmitsToolCallStartWithParentMessageId()
    {
        var adapter = new SwarmEventAdapter();
        var funcCall = new FunctionCallContent(
            "call-888",
            "read_driver_prompt",
            new Dictionary<string, object?> { ["agentId"] = "skeptic" });
        var msg = new ChatMessage(ChatRole.Assistant, [funcCall]);
        using var inner = new FakeChatClient(new ChatResponse([msg]));
        using var interceptor = new AgUIEventInterceptor(inner, adapter, "researcher");

        await interceptor.GetResponseAsync([new ChatMessage(ChatRole.User, "ask")]);

        var events = await DrainEventsAsync(adapter, 3);
        var toolStart = (ToolCallStartEvent)events[0];
        toolStart.ParentMessageId.Should().NotBeNullOrEmpty(
            "AG-UI apply layer uses parentMessageId to bind a tool call to its assistant message in messages[]");
    }

    [TestMethod]
    public async Task GetResponseAsync_MessageWithTextAndToolCall_SharesMessageIdAcrossEvents()
    {
        var adapter = new SwarmEventAdapter();
        var funcCall = new FunctionCallContent(
            "call-999",
            "read_conversation_history",
            new Dictionary<string, object?> { ["agentId"] = "primary_researcher" });
        var textContent = new TextContent("Looking up the transcript now.");
        var msg = new ChatMessage(ChatRole.Assistant, [funcCall, textContent]);
        using var inner = new FakeChatClient(new ChatResponse([msg]));
        using var interceptor = new AgUIEventInterceptor(inner, adapter, "primary_researcher");

        await interceptor.GetResponseAsync([new ChatMessage(ChatRole.User, "ask")]);

        // TOOL_CALL_START, ARGS, END, TEXT_MESSAGE_START, CONTENT, END
        var events = await DrainEventsAsync(adapter, 6);
        var toolStart = (ToolCallStartEvent)events[0];
        var textStart = (TextMessageStartEvent)events[3];

        textStart.MessageId.Should().Be(
            toolStart.ParentMessageId,
            "text + tool-call content within one ChatMessage must share an id so the AG-UI apply layer renders them as one assistant message with both toolCalls and content");
    }

    // -----------------------------------------------------------------------
    // Mixed content — multiple messages with different content types
    // -----------------------------------------------------------------------

    [TestMethod]
    public async Task GetResponseAsync_Mixed_Content_Emits_All_Event_Types()
    {
        var adapter = new SwarmEventAdapter();
        var funcCall = new FunctionCallContent(
            "call-1",
            "inbox_send",
            new Dictionary<string, object?> { ["to"] = "leader" });
        var funcResult = new FunctionResultContent("call-1", "{\"sent\":true}");
        var textMsg = new ChatMessage(ChatRole.Assistant, "Done with task");

        var innerResponse = new ChatResponse(
        [
            new ChatMessage(ChatRole.Assistant, [funcCall]),
            new ChatMessage(ChatRole.Tool, [funcResult]),
            textMsg,
        ]);
        using var inner = new FakeChatClient(innerResponse);
        using var interceptor = new AgUIEventInterceptor(inner, adapter, "agent");

        await interceptor.GetResponseAsync([new ChatMessage(ChatRole.User, "work")]);

        // Expect: TOOL_CALL_START, ARGS, END, TOOL_CALL_RESULT, TEXT_START, TEXT_CONTENT, TEXT_END
        var events = await DrainEventsAsync(adapter, 7);
        events[0].Should().BeOfType<ToolCallStartEvent>();
        events[1].Should().BeOfType<ToolCallArgsEvent>();
        events[2].Should().BeOfType<ToolCallEndEvent>();
        events[3].Should().BeOfType<ToolCallResultEvent>();
        events[4].Should().BeOfType<TextMessageStartEvent>();
        events[5].Should().BeOfType<TextMessageContentEvent>();
        events[6].Should().BeOfType<TextMessageEndEvent>();
    }

    // -----------------------------------------------------------------------
    // Empty response — no events emitted
    // -----------------------------------------------------------------------

    [TestMethod]
    public async Task GetResponseAsync_Empty_Response_Emits_No_Events()
    {
        var adapter = new SwarmEventAdapter();
        var innerResponse = new ChatResponse([]);
        using var inner = new FakeChatClient(innerResponse);
        using var interceptor = new AgUIEventInterceptor(inner, adapter, "agent");

        await interceptor.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        adapter.Reader.TryRead(out _).Should().BeFalse();
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static async Task<List<SwarmAgUIEvent>> DrainEventsAsync(
        SwarmEventAdapter adapter,
        int expectedCount)
    {
        var events = new List<SwarmAgUIEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        for (var i = 0; i < expectedCount; i++)
        {
            events.Add(await adapter.Reader.ReadAsync(cts.Token));
        }

        return events;
    }

    /// <summary>
    /// Minimal fake IChatClient that returns a fixed response.
    /// </summary>
    private sealed class FakeChatClient : IChatClient
    {
        private readonly ChatResponse response;

        public FakeChatClient(ChatResponse response)
        {
            this.response = response;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(this.response);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public void Dispose()
        {
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return null;
        }
    }
}
