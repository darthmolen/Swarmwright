using Swarmwright.Models;
using Swarmwright.Models.Enums;
using Swarmwright.Orchestration;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Moq;

namespace Swarmwright.Tests.Orchestration;

/// <summary>
/// Unit tests for <see cref="SwarmAgent"/>.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SwarmAgentTests
{
    private Mock<IChatClient> mockChatClient = null!;

    /// <summary>
    /// Initializes test dependencies before each test.
    /// </summary>
    [TestInitialize]
    public void TestInitialize()
    {
        this.mockChatClient = new Mock<IChatClient>();
    }

    /// <summary>
    /// Verifies that the Name property returns the value provided in the constructor.
    /// </summary>
    [TestMethod]
    public void Name_ReturnsConstructorValue()
    {
        // Arrange & Act
        var agent = this.CreateAgent(name: "test-agent");

        // Assert
        agent.Name.Should().Be("test-agent");
    }

    /// <summary>
    /// Verifies that ExecuteTaskAsync sends a prompt and returns the assistant response.
    /// </summary>
    [TestMethod]
    public async Task ExecuteTaskAsync_SendsPromptAndReturnsResponse()
    {
        // Arrange
        var agent = this.CreateAgent();
        var task = new SwarmTask
        {
            Id = "task-1",
            Subject = "Test Subject",
            Description = "Test Description",
        };

        var responseMessage = new ChatMessage(ChatRole.Assistant, "Task completed successfully.");
        var chatResponse = new ChatResponse(responseMessage);

        this.mockChatClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(chatResponse);

        // Act
        var result = await agent.ExecuteTaskAsync(task);

        // Assert
        result.FinalText.Should().Be("Task completed successfully.");
        this.mockChatClient.Verify(
            c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// Verifies that ResumeSessionAsync preserves existing conversation history.
    /// </summary>
    [TestMethod]
    public async Task ResumeSessionAsync_PreservesConversationHistory()
    {
        // Arrange
        var agent = this.CreateAgent();
        var task = new SwarmTask
        {
            Id = "task-1",
            Subject = "Test Subject",
            Description = "Test Description",
        };

        var firstResponse = new ChatMessage(ChatRole.Assistant, "First response.");
        var secondResponse = new ChatMessage(ChatRole.Assistant, "Second response.");

        this.mockChatClient
            .SetupSequence(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(firstResponse))
            .ReturnsAsync(new ChatResponse(secondResponse));

        // Act
        await agent.ExecuteTaskAsync(task);
        var historyAfterFirst = agent.ConversationHistory.Count;

        await agent.ResumeSessionAsync("Please continue.");
        var historyAfterResume = agent.ConversationHistory.Count;

        // Assert — history should grow (system + user + assistant + nudge + assistant)
        historyAfterResume.Should().BeGreaterThan(historyAfterFirst);
    }

    /// <summary>
    /// Verifies that when the worker successfully calls <c>task_update</c> with status
    /// <c>Completed</c>, the resulting <see cref="TaskExecutionResult"/> reflects the
    /// declared status and result.
    /// </summary>
    [TestMethod]
    public async Task ExecuteTaskAsync_WhenWorkerCallsTaskUpdateCompleted_ReturnsCompletedStatus()
    {
        // Arrange
        var agent = this.CreateAgent();
        var task = new SwarmTask
        {
            Id = "task-1",
            Subject = "Test Subject",
            Description = "Test Description",
        };

        var funcCall = new FunctionCallContent(
            "call-1",
            "task_update",
            new Dictionary<string, object?>
            {
                ["task_id"] = "task-1",
                ["status"] = "Completed",
                ["result"] = "All done with the report.",
            });
        var funcResult = new FunctionResultContent(
            "call-1",
            "{\"success\":true,\"taskId\":\"task-1\",\"status\":\"Completed\"}");

        var chatResponse = new ChatResponse(
        [
            new ChatMessage(ChatRole.Assistant, [funcCall]),
            new ChatMessage(ChatRole.Tool, [funcResult]),
            new ChatMessage(ChatRole.Assistant, "Task completed successfully."),
        ]);

        this.mockChatClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(chatResponse);

        // Act
        var result = await agent.ExecuteTaskAsync(task);

        // Assert
        result.WorkerDeclaredStatus.Should().Be(TaskState.Completed);
        result.WorkerDeclaredResult.Should().Be("All done with the report.");
    }

    /// <summary>
    /// Verifies that when the worker successfully calls <c>task_update</c> with status
    /// <c>Failed</c>, the resulting <see cref="TaskExecutionResult"/> reflects the failure.
    /// </summary>
    [TestMethod]
    public async Task ExecuteTaskAsync_WhenWorkerCallsTaskUpdateFailed_ReturnsFailedStatus()
    {
        // Arrange
        var agent = this.CreateAgent();
        var task = new SwarmTask
        {
            Id = "task-1",
            Subject = "Test Subject",
            Description = "Test Description",
        };

        var funcCall = new FunctionCallContent(
            "call-1",
            "task_update",
            new Dictionary<string, object?>
            {
                ["task_id"] = "task-1",
                ["status"] = "Failed",
                ["result"] = "Could not retrieve data.",
            });
        var funcResult = new FunctionResultContent(
            "call-1",
            "{\"success\":true,\"taskId\":\"task-1\",\"status\":\"Failed\"}");

        var chatResponse = new ChatResponse(
        [
            new ChatMessage(ChatRole.Assistant, [funcCall]),
            new ChatMessage(ChatRole.Tool, [funcResult]),
            new ChatMessage(ChatRole.Assistant, "I failed."),
        ]);

        this.mockChatClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(chatResponse);

        // Act
        var result = await agent.ExecuteTaskAsync(task);

        // Assert
        result.WorkerDeclaredStatus.Should().Be(TaskState.Failed);
        result.WorkerDeclaredResult.Should().Be("Could not retrieve data.");
    }

    /// <summary>
    /// Verifies that when the worker emits only text without calling <c>task_update</c>,
    /// the resulting <see cref="TaskExecutionResult.WorkerDeclaredStatus"/> is <see langword="null"/>.
    /// </summary>
    [TestMethod]
    public async Task ExecuteTaskAsync_WhenWorkerDoesNotCallTaskUpdate_ReturnsNullStatus()
    {
        // Arrange
        var agent = this.CreateAgent();
        var task = new SwarmTask
        {
            Id = "task-1",
            Subject = "Test Subject",
            Description = "Test Description",
        };

        var chatResponse = new ChatResponse(
            new ChatMessage(ChatRole.Assistant, "I am ready to begin."));

        this.mockChatClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(chatResponse);

        // Act
        var result = await agent.ExecuteTaskAsync(task);

        // Assert
        result.WorkerDeclaredStatus.Should().BeNull();
        result.WorkerDeclaredResult.Should().BeNull();
        result.FinalText.Should().Be("I am ready to begin.");
    }

    /// <summary>
    /// Verifies that when a <c>task_update</c> tool call returns an error JSON result,
    /// the declared status is ignored and <see cref="TaskExecutionResult.WorkerDeclaredStatus"/>
    /// remains <see langword="null"/>.
    /// </summary>
    [TestMethod]
    public async Task ExecuteTaskAsync_WhenTaskUpdateToolCallFailed_IgnoresDeclaredStatus()
    {
        // Arrange
        var agent = this.CreateAgent();
        var task = new SwarmTask
        {
            Id = "task-1",
            Subject = "Test Subject",
            Description = "Test Description",
        };

        var funcCall = new FunctionCallContent(
            "call-1",
            "task_update",
            new Dictionary<string, object?>
            {
                ["task_id"] = "task-1",
                ["status"] = "Completed",
                ["result"] = "Report complete.",
            });
        var funcResult = new FunctionResultContent(
            "call-1",
            "{\"error\":\"Task not found\"}");

        var chatResponse = new ChatResponse(
        [
            new ChatMessage(ChatRole.Assistant, [funcCall]),
            new ChatMessage(ChatRole.Tool, [funcResult]),
            new ChatMessage(ChatRole.Assistant, "Tried to update."),
        ]);

        this.mockChatClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(chatResponse);

        // Act
        var result = await agent.ExecuteTaskAsync(task);

        // Assert
        result.WorkerDeclaredStatus.Should().BeNull();
        result.WorkerDeclaredResult.Should().BeNull();
    }

    /// <summary>
    /// Verifies that when the worker calls <c>task_update</c> multiple times,
    /// the last successful invocation wins.
    /// </summary>
    [TestMethod]
    public async Task ExecuteTaskAsync_WhenWorkerCallsTaskUpdateMultipleTimes_UsesLastSuccessful()
    {
        // Arrange
        var agent = this.CreateAgent();
        var task = new SwarmTask
        {
            Id = "task-1",
            Subject = "Test Subject",
            Description = "Test Description",
        };

        var firstCall = new FunctionCallContent(
            "call-1",
            "task_update",
            new Dictionary<string, object?>
            {
                ["task_id"] = "task-1",
                ["status"] = "InProgress",
                ["result"] = "Starting.",
            });
        var firstResult = new FunctionResultContent(
            "call-1",
            "{\"success\":true,\"taskId\":\"task-1\",\"status\":\"InProgress\"}");

        var secondCall = new FunctionCallContent(
            "call-2",
            "task_update",
            new Dictionary<string, object?>
            {
                ["task_id"] = "task-1",
                ["status"] = "Completed",
                ["result"] = "All done.",
            });
        var secondResult = new FunctionResultContent(
            "call-2",
            "{\"success\":true,\"taskId\":\"task-1\",\"status\":\"Completed\"}");

        var chatResponse = new ChatResponse(
        [
            new ChatMessage(ChatRole.Assistant, [firstCall]),
            new ChatMessage(ChatRole.Tool, [firstResult]),
            new ChatMessage(ChatRole.Assistant, [secondCall]),
            new ChatMessage(ChatRole.Tool, [secondResult]),
            new ChatMessage(ChatRole.Assistant, "Finished."),
        ]);

        this.mockChatClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(chatResponse);

        // Act
        var result = await agent.ExecuteTaskAsync(task);

        // Assert
        result.WorkerDeclaredStatus.Should().Be(TaskState.Completed);
        result.WorkerDeclaredResult.Should().Be("All done.");
    }

    private SwarmAgent CreateAgent(
        string name = "worker-1",
        string role = "developer",
        string displayName = "Worker One",
        string systemPrompt = "You are a helpful assistant.")
    {
        return new SwarmAgent(
            name,
            role,
            displayName,
            systemPrompt,
            new List<AITool>(),
            this.mockChatClient.Object);
    }
}
