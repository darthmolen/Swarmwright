using Swarmwright.Core;
using Swarmwright.Events;
using Swarmwright.Models;
using Swarmwright.Models.Enums;
using Swarmwright.Orchestration;
using Swarmwright.Services;
using Swarmwright.Tools;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Moq;

namespace Swarmwright.Tests.Orchestration;

/// <summary>
/// Integration tests that exercise <see cref="SwarmAgent"/> through the real
/// <c>FunctionInvokingChatClient</c> pipeline. The pipeline assembles
/// <see cref="FunctionCallContent"/> and <see cref="FunctionResultContent"/>
/// using the same serialization shapes as production, which differs from the
/// hand-constructed instances used by the unit tests in
/// <see cref="SwarmAgentTests"/>. In particular, FIC wraps string-returning
/// tool results as <see cref="System.Text.Json.JsonElement"/> values with
/// <see cref="System.Text.Json.JsonValueKind.String"/> kind, which requires
/// the helper to call <c>GetString()</c> rather than <c>GetRawText()</c>.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public sealed class SwarmAgentFunctionInvokingIntegrationTests
{
    private Mock<IChatClient> mockInnerClient = null!;
    private Mock<ISwarmService> mockSwarmService = null!;
    private Mock<ISwarmEventBus> mockEventBus = null!;

    /// <summary>
    /// Initializes test dependencies before each test.
    /// </summary>
    [TestInitialize]
    public void TestInitialize()
    {
        this.mockInnerClient = new Mock<IChatClient>();
        this.mockSwarmService = new Mock<ISwarmService>();
        this.mockEventBus = new Mock<ISwarmEventBus>();
    }

    /// <summary>
    /// Verifies that when the canned inner chat client emits a successful
    /// <c>task_update(Completed)</c> through the real <c>UseFunctionInvocation</c>
    /// pipeline, the resulting <see cref="TaskExecutionResult"/> reflects the declared
    /// <see cref="TaskState.Completed"/> status.
    /// </summary>
    [TestMethod]
    public async Task ExecuteTaskAsync_ThroughFunctionInvokingPipeline_WithSuccessfulTaskUpdate_ReturnsCompleted()
    {
        // Arrange
        var tools = SwarmToolFactory.CreateWorkerTools(
            "worker-1",
            this.mockSwarmService.Object,
            this.mockEventBus.Object);

        var firstCall = new FunctionCallContent(
            "call-1",
            "task_update",
            new Dictionary<string, object?>
            {
                ["task_id"] = "task-1",
                ["status"] = "Completed",
                ["result"] = "done",
            });

        // FIC issues a call, then invokes the tool, then calls GetResponseAsync AGAIN
        // expecting a final message. The pipeline loops until no more tool calls.
        this.mockInnerClient
            .SetupSequence(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, [firstCall])))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Task completed successfully.")));

        var pipeline = this.mockInnerClient.Object
            .AsBuilder()
            .UseFunctionInvocation()
            .Build();

        var agent = new SwarmAgent(
            "worker-1",
            "Developer",
            "Worker One",
            "You are a worker.",
            tools,
            pipeline);

        var task = new SwarmTask
        {
            Id = "task-1",
            Subject = "Test Subject",
            Description = "Test Description",
        };

        // Act
        var result = await agent.ExecuteTaskAsync(task);

        // Assert — F01.3: the tool no longer mutates state. The agent
        // exposes the worker's declared status via TaskExecutionResult
        // and the orchestrator persists it post-conversation through
        // IStateTransitionService.
        result.WorkerDeclaredStatus.Should().Be(TaskState.Completed);
        result.WorkerDeclaredResult.Should().Be("done");
    }

    /// <summary>
    /// Verifies that when the canned inner chat client emits two successful
    /// <c>task_update</c> calls through the real pipeline, the helper returns
    /// the last successful match (Completed) rather than the first (InProgress).
    /// </summary>
    [TestMethod]
    public async Task ExecuteTaskAsync_ThroughFunctionInvokingPipeline_WhenMultipleTaskUpdates_UsesLastSuccessful()
    {
        // Arrange
        var tools = SwarmToolFactory.CreateWorkerTools(
            "worker-1",
            this.mockSwarmService.Object,
            this.mockEventBus.Object);

        var firstCall = new FunctionCallContent(
            "call-1",
            "task_update",
            new Dictionary<string, object?>
            {
                ["task_id"] = "task-1",
                ["status"] = "InProgress",
                ["result"] = "starting",
            });

        var secondCall = new FunctionCallContent(
            "call-2",
            "task_update",
            new Dictionary<string, object?>
            {
                ["task_id"] = "task-1",
                ["status"] = "Completed",
                ["result"] = "all done",
            });

        // Turn 1: InProgress call. Turn 2: Completed call. Turn 3: final text.
        this.mockInnerClient
            .SetupSequence(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, [firstCall])))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, [secondCall])))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Finished.")));

        var pipeline = this.mockInnerClient.Object
            .AsBuilder()
            .UseFunctionInvocation()
            .Build();

        var agent = new SwarmAgent(
            "worker-1",
            "Developer",
            "Worker One",
            "You are a worker.",
            tools,
            pipeline);

        var task = new SwarmTask
        {
            Id = "task-1",
            Subject = "Test Subject",
            Description = "Test Description",
        };

        // Act
        var result = await agent.ExecuteTaskAsync(task);

        // Assert
        result.WorkerDeclaredStatus.Should().Be(TaskState.Completed);
        result.WorkerDeclaredResult.Should().Be("all done");
    }

    /// <summary>
    /// Verifies that when the canned inner chat client emits a <c>task_update</c>
    /// with an invalid status through the real pipeline, the real tool returns an
    /// error result and the helper reports <see langword="null"/> declared status.
    /// </summary>
    [TestMethod]
    public async Task ExecuteTaskAsync_ThroughFunctionInvokingPipeline_WhenAllCallsFail_ReturnsNull()
    {
        // Arrange
        var tools = SwarmToolFactory.CreateWorkerTools(
            "worker-1",
            this.mockSwarmService.Object,
            this.mockEventBus.Object);

        var firstCall = new FunctionCallContent(
            "call-1",
            "task_update",
            new Dictionary<string, object?>
            {
                ["task_id"] = "task-1",
                ["status"] = "totally_bogus",
                ["result"] = "nope",
            });

        this.mockInnerClient
            .SetupSequence(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, [firstCall])))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Gave up.")));

        var pipeline = this.mockInnerClient.Object
            .AsBuilder()
            .UseFunctionInvocation()
            .Build();

        var agent = new SwarmAgent(
            "worker-1",
            "Developer",
            "Worker One",
            "You are a worker.",
            tools,
            pipeline);

        var task = new SwarmTask
        {
            Id = "task-1",
            Subject = "Test Subject",
            Description = "Test Description",
        };

        // Act
        var result = await agent.ExecuteTaskAsync(task);

        // Assert
        result.WorkerDeclaredStatus.Should().BeNull();
        result.WorkerDeclaredResult.Should().BeNull();
    }
}
