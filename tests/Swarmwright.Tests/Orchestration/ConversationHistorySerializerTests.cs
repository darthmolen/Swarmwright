using System.Text.Json;
using Swarmwright.Orchestration;
using FluentAssertions;
using Microsoft.Extensions.AI;

namespace Swarmwright.Tests.Orchestration;

/// <summary>
/// Unit tests for <see cref="ConversationHistorySerializer"/>.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class ConversationHistorySerializerTests : IDisposable
{
    private readonly string testDir;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConversationHistorySerializerTests"/> class.
    /// </summary>
    public ConversationHistorySerializerTests()
    {
        this.testDir = Path.Combine(Path.GetTempPath(), "serializer-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.testDir);
    }

    /// <summary>
    /// Disposes managed resources.
    /// </summary>
    public void Dispose()
    {
        if (Directory.Exists(this.testDir))
        {
            Directory.Delete(this.testDir, recursive: true);
        }
    }

    /// <summary>
    /// Verifies that SerializeAsync writes one JSONL line per message with role and text fields.
    /// </summary>
    [TestMethod]
    public async Task SerializeAsync_WritesJsonlWithRoleAndText()
    {
        // Arrange
        var filePath = Path.Combine(this.testDir, "history.jsonl");
        var history = new List<ChatMessage>
        {
            new(ChatRole.System, "You are a helpful assistant."),
            new(ChatRole.User, "Hello."),
            new(ChatRole.Assistant, "Hi there."),
        };

        // Act
        await ConversationHistorySerializer.SerializeAsync(filePath, history);

        // Assert
        var lines = await File.ReadAllLinesAsync(filePath);
        lines.Should().HaveCount(3);
        lines[0].Should().Contain("\"role\":\"system\"");
        lines[0].Should().Contain("\"text\":\"You are a helpful assistant.\"");
        lines[1].Should().Contain("\"role\":\"user\"");
        lines[2].Should().Contain("\"role\":\"assistant\"");
    }

    /// <summary>
    /// Verifies that DeserializeAsync round-trips messages correctly.
    /// </summary>
    [TestMethod]
    public async Task DeserializeAsync_RoundTripsMessages()
    {
        // Arrange
        var filePath = Path.Combine(this.testDir, "roundtrip.jsonl");
        var history = new List<ChatMessage>
        {
            new(ChatRole.System, "System prompt."),
            new(ChatRole.User, "User message."),
            new(ChatRole.Assistant, "Assistant reply."),
        };

        await ConversationHistorySerializer.SerializeAsync(filePath, history);

        // Act
        var result = await ConversationHistorySerializer.DeserializeAsync(filePath);

        // Assert
        result.Should().HaveCount(3);
        result[0].Role.Should().Be(ChatRole.System);
        result[0].Text.Should().Be("System prompt.");
        result[1].Role.Should().Be(ChatRole.User);
        result[1].Text.Should().Be("User message.");
        result[2].Role.Should().Be(ChatRole.Assistant);
        result[2].Text.Should().Be("Assistant reply.");
    }

    /// <summary>
    /// Verifies that FunctionCallContent in an assistant message is persisted as a
    /// "toolCalls" array alongside the text.
    /// </summary>
    [TestMethod]
    public async Task SerializeAsync_AssistantToolCalls_PersistedAsToolCallsArray()
    {
        // Arrange
        var filePath = Path.Combine(this.testDir, "toolcalls.jsonl");
        var assistantMessage = new ChatMessage(
            ChatRole.Assistant,
            [
                new TextContent("Calling task_update"),
                new FunctionCallContent(
                    "call-1",
                    "task_update",
                    new Dictionary<string, object?> { ["task_id"] = "abc", ["status"] = "InProgress" }),
            ]);

        // Act
        await ConversationHistorySerializer.SerializeAsync(
            filePath,
            new List<ChatMessage> { assistantMessage });

        // Assert
        var line = (await File.ReadAllLinesAsync(filePath))[0];
        using var doc = JsonDocument.Parse(line);
        doc.RootElement.GetProperty("text").GetString().Should().Be("Calling task_update");
        var toolCalls = doc.RootElement.GetProperty("toolCalls");
        toolCalls.GetArrayLength().Should().Be(1);
        toolCalls[0].GetProperty("callId").GetString().Should().Be("call-1");
        toolCalls[0].GetProperty("name").GetString().Should().Be("task_update");
        toolCalls[0].GetProperty("args").GetProperty("task_id").GetString().Should().Be("abc");
        toolCalls[0].GetProperty("args").GetProperty("status").GetString().Should().Be("InProgress");
    }

    /// <summary>
    /// Verifies that FunctionResultContent in a tool message is persisted as "toolCallId" and "result".
    /// </summary>
    [TestMethod]
    public async Task SerializeAsync_ToolResult_PersistedAsToolCallIdAndResult()
    {
        // Arrange
        var filePath = Path.Combine(this.testDir, "toolresult.jsonl");
        var toolMessage = new ChatMessage(
            ChatRole.Tool,
            [new FunctionResultContent("call-1", "{\"success\":true}")]);

        // Act
        await ConversationHistorySerializer.SerializeAsync(
            filePath,
            new List<ChatMessage> { toolMessage });

        // Assert
        var line = (await File.ReadAllLinesAsync(filePath))[0];
        using var doc = JsonDocument.Parse(line);
        doc.RootElement.GetProperty("role").GetString().Should().Be("tool");
        doc.RootElement.GetProperty("toolCallId").GetString().Should().Be("call-1");
        doc.RootElement.GetProperty("result").GetString().Should().Be("{\"success\":true}");
    }

    /// <summary>
    /// Verifies that a plain text message (no function content) omits toolCalls/toolCallId/result fields.
    /// </summary>
    [TestMethod]
    public async Task SerializeAsync_PlainTextMessage_DoesNotIncludeToolCallFields()
    {
        // Arrange
        var filePath = Path.Combine(this.testDir, "plain.jsonl");

        // Act
        await ConversationHistorySerializer.SerializeAsync(
            filePath,
            new List<ChatMessage> { new(ChatRole.User, "hello") });

        // Assert
        var line = (await File.ReadAllLinesAsync(filePath))[0];
        using var doc = JsonDocument.Parse(line);
        doc.RootElement.TryGetProperty("toolCalls", out _).Should().BeFalse();
        doc.RootElement.TryGetProperty("toolCallId", out _).Should().BeFalse();
        doc.RootElement.TryGetProperty("result", out _).Should().BeFalse();
    }

    /// <summary>
    /// Verifies that an empty history produces an empty file.
    /// </summary>
    [TestMethod]
    public async Task SerializeAsync_EmptyHistory_WritesEmptyFile()
    {
        // Arrange
        var filePath = Path.Combine(this.testDir, "empty.jsonl");
        var history = new List<ChatMessage>();

        // Act
        await ConversationHistorySerializer.SerializeAsync(filePath, history);

        // Assert
        var content = await File.ReadAllTextAsync(filePath);
        content.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies that histories exceeding 200 messages are truncated to the last 200.
    /// </summary>
    [TestMethod]
    public async Task SerializeAsync_TruncatesAtMaxMessages()
    {
        // Arrange
        var filePath = Path.Combine(this.testDir, "truncated.jsonl");
        var history = new List<ChatMessage>();
        for (var i = 0; i < 250; i++)
        {
            history.Add(new ChatMessage(ChatRole.User, $"Message {i}"));
        }

        // Act
        await ConversationHistorySerializer.SerializeAsync(filePath, history);

        // Assert
        var lines = await File.ReadAllLinesAsync(filePath);
        lines.Should().HaveCount(ConversationHistorySerializer.MaxMessagesPerAgent);
        lines[0].Should().Contain("Message 50");
    }

    /// <summary>
    /// Verifies that sequential writes to the same path produce a
    /// last-writer-wins result with no corruption or interleaving.
    /// Concurrent writes are not the serializer's contract — the
    /// orchestrator's per-agent semaphore serializes callers, so the
    /// serializer only guarantees atomicity per call, not mutual
    /// exclusion across calls. This test covers the sequential
    /// contract that the serializer is responsible for.
    /// </summary>
    [TestMethod]
    public async Task SerializeAsync_SequentialWrites_LastWriterWins()
    {
        // Arrange
        var filePath = Path.Combine(this.testDir, "sequential.jsonl");
        var history1 = new List<ChatMessage> { new(ChatRole.User, "From writer 1.") };
        var history2 = new List<ChatMessage> { new(ChatRole.User, "From writer 2.") };

        // Act
        await ConversationHistorySerializer.SerializeAsync(filePath, history1);
        await ConversationHistorySerializer.SerializeAsync(filePath, history2);

        // Assert
        var lines = await File.ReadAllLinesAsync(filePath);
        lines.Should().HaveCount(1, "the last writer wins, producing exactly one line.");
        lines[0].Should().Contain("From writer 2.");
    }

    /// <summary>
    /// Verifies that DeserializeAsync returns an empty list for a non-existent file.
    /// </summary>
    [TestMethod]
    public async Task DeserializeAsync_NonExistentFile_ReturnsEmptyList()
    {
        // Arrange
        var filePath = Path.Combine(this.testDir, "does-not-exist.jsonl");

        // Act
        var result = await ConversationHistorySerializer.DeserializeAsync(filePath);

        // Assert
        result.Should().BeEmpty();
    }
}
