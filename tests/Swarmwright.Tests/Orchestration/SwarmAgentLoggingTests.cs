using Swarmwright.Models;
using Swarmwright.Orchestration;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Swarmwright.Tests.Orchestration;

/// <summary>
/// Unit tests for <see cref="SwarmAgent"/> logging integration.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SwarmAgentLoggingTests
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
    /// Verifies that the constructor accepts a logger without throwing.
    /// </summary>
    [TestMethod]
    public void Constructor_AcceptsLogger()
    {
        // Arrange & Act
        var act = () => new SwarmAgent(
            "test-agent",
            "developer",
            "Test Agent",
            "You are a helpful assistant.",
            new List<AITool>(),
            this.mockChatClient.Object,
            NullLogger<SwarmAgent>.Instance);

        // Assert
        act.Should().NotThrow();
    }

    /// <summary>
    /// Verifies that basic execution still works with a logger provided.
    /// </summary>
    [TestMethod]
    public async Task ExecuteTaskAsync_CompletesWithoutError()
    {
        // Arrange
        var agent = new SwarmAgent(
            "test-agent",
            "developer",
            "Test Agent",
            "You are a helpful assistant.",
            new List<AITool>(),
            this.mockChatClient.Object,
            NullLogger<SwarmAgent>.Instance);

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
    }
}
