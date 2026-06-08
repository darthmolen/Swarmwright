using System.Text.Json;
using Swarmwright.Core;
using Swarmwright.Events;
using Swarmwright.Models;
using Swarmwright.Models.Enums;
using Swarmwright.Services;
using Swarmwright.Templates;
using Swarmwright.Tools;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Moq;

namespace Swarmwright.Tests.Tools;

/// <summary>
/// Unit tests for <see cref="SwarmToolFactory"/>.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SwarmToolFactoryTests
{
    private Mock<ISwarmService> mockSwarmService = null!;
    private Mock<ISwarmEventBus> mockEventBus = null!;

    /// <summary>
    /// Initializes mocks before each test.
    /// </summary>
    [TestInitialize]
    public void TestInitialize()
    {
        this.mockSwarmService = new Mock<ISwarmService>();
        this.mockEventBus = new Mock<ISwarmEventBus>();
    }

    /// <summary>
    /// Verifies that CreateWorkerTools returns exactly 4 tools.
    /// </summary>
    [TestMethod]
    public void CreateWorkerTools_Returns4Tools()
    {
        // Act
        var tools = SwarmToolFactory.CreateWorkerTools(
            "worker_1",
            this.mockSwarmService.Object,
            this.mockEventBus.Object);

        // Assert
        tools.Should().HaveCount(4);
    }

    /// <summary>
    /// Verifies that the task_update tool returns a successful JSON envelope
    /// echoing the canonical status. F01.3: the tool no longer mutates state
    /// — its only job is to give the worker LLM a structured surface to
    /// declare completion; the orchestrator parses the FunctionCallContent
    /// and writes the DB row via IStateTransitionService post-conversation.
    /// </summary>
    [TestMethod]
    public async Task TaskUpdate_ReturnsSuccessJsonForValidInput()
    {
        // Arrange
        var tools = SwarmToolFactory.CreateWorkerTools(
            "worker_1",
            this.mockSwarmService.Object,
            this.mockEventBus.Object);

        var taskUpdateTool = (AIFunction)tools.First(t => t.Name == "task_update");
        var args = new AIFunctionArguments
        {
            ["task_id"] = "task-1",
            ["status"] = "Completed",
            ["result"] = "done",
        };

        // Act
        var result = await taskUpdateTool.InvokeAsync(args);

        // Assert
        var json = result?.ToString() ?? string.Empty;
        json.Should().Contain("\"success\":true");
        json.Should().Contain("\"taskId\":\"task-1\"");
        json.Should().Contain("\"status\":\"Completed\"");
    }

    /// <summary>
    /// Verifies that inbox_send calls the service with the closure-bound sender name.
    /// </summary>
    [TestMethod]
    public async Task InboxSend_CallsServiceSendMessage()
    {
        // Arrange
        this.mockSwarmService
            .Setup(s => s.SendMessageAsync("worker_1", "worker_2", "hello"))
            .Returns(Task.CompletedTask);
        this.mockEventBus
            .Setup(e => e.EmitAsync("inbox.message", It.IsAny<object?>()))
            .Returns(Task.CompletedTask);

        var tools = SwarmToolFactory.CreateWorkerTools(
            "worker_1",
            this.mockSwarmService.Object,
            this.mockEventBus.Object);

        var inboxSendTool = (AIFunction)tools.First(t => t.Name == "inbox_send");
        var args = new AIFunctionArguments
        {
            ["to"] = "worker_2",
            ["message"] = "hello",
        };

        // Act
        await inboxSendTool.InvokeAsync(args);

        // Assert
        this.mockSwarmService.Verify(
            s => s.SendMessageAsync("worker_1", "worker_2", "hello"),
            Times.Once);
    }

    /// <summary>
    /// Verifies that inbox_receive returns a JSON array of messages.
    /// </summary>
    [TestMethod]
    public async Task InboxReceive_ReturnsJsonMessages()
    {
        // Arrange
        var messages = new List<InboxMessage>
        {
            new() { Sender = "leader", Recipient = "worker_1", Content = "start" },
        };

        var mockInbox = new Mock<IInboxSystem>();
        mockInbox
            .Setup(i => i.ReceiveAsync("worker_1"))
            .ReturnsAsync(messages);
        this.mockSwarmService
            .Setup(s => s.InboxSystem)
            .Returns(mockInbox.Object);

        var tools = SwarmToolFactory.CreateWorkerTools(
            "worker_1",
            this.mockSwarmService.Object,
            this.mockEventBus.Object);

        var inboxReceiveTool = (AIFunction)tools.First(t => t.Name == "inbox_receive");

        // Act
        var result = await inboxReceiveTool.InvokeAsync(new AIFunctionArguments());

        // Assert
        var json = result?.ToString() ?? string.Empty;
        json.Should().Contain("leader");
        json.Should().Contain("start");
    }

    /// <summary>
    /// Verifies that task_list returns a JSON array of tasks.
    /// </summary>
    [TestMethod]
    public async Task TaskList_ReturnsFilteredTasks()
    {
        // Arrange
        var tasks = new List<SwarmTask>
        {
            new() { Id = "task-1", Subject = "Do work", WorkerName = "worker_1" },
        };

        this.mockSwarmService
            .Setup(s => s.GetTasksAsync("worker_1"))
            .ReturnsAsync(tasks);

        var tools = SwarmToolFactory.CreateWorkerTools(
            "worker_1",
            this.mockSwarmService.Object,
            this.mockEventBus.Object);

        var taskListTool = (AIFunction)tools.First(t => t.Name == "task_list");
        var args = new AIFunctionArguments
        {
            ["owner"] = "worker_1",
        };

        // Act
        var result = await taskListTool.InvokeAsync(args);

        // Assert
        var json = result?.ToString() ?? string.Empty;
        json.Should().Contain("task-1");
        json.Should().Contain("Do work");
    }

    /// <summary>
    /// Verifies that task_list serializes <see cref="TaskState"/> as its
    /// PascalCase string representation (e.g. <c>"Completed"</c>) rather than a
    /// raw integer. Previously the tool used a local <c>JsonOptions</c> with no
    /// enum converter, causing the LLM to see opaque integers that it had no
    /// way to reason about.
    /// </summary>
    [TestMethod]
    public async Task TaskList_SerializesStatusAsPascalCaseString()
    {
        // Arrange — one task per status so we exercise the full enum surface.
        var tasks = new List<SwarmTask>
        {
            new() { Id = "task-completed", Subject = "Done", WorkerName = "worker_1", Status = TaskState.Completed },
            new() { Id = "task-in-progress", Subject = "Working", WorkerName = "worker_1", Status = TaskState.InProgress },
            new() { Id = "task-pending", Subject = "Next", WorkerName = "worker_1", Status = TaskState.Pending },
        };

        this.mockSwarmService
            .Setup(s => s.GetTasksAsync("worker_1"))
            .ReturnsAsync(tasks);

        var tools = SwarmToolFactory.CreateWorkerTools(
            "worker_1",
            this.mockSwarmService.Object,
            this.mockEventBus.Object);

        var taskListTool = (AIFunction)tools.First(t => t.Name == "task_list");
        var args = new AIFunctionArguments { ["owner"] = "worker_1" };

        // Act
        var result = await taskListTool.InvokeAsync(args);

        // Assert — PascalCase string form for every enum value.
        var json = result?.ToString() ?? string.Empty;
        json.Should().Contain("\"status\":\"Completed\"");
        json.Should().Contain("\"status\":\"InProgress\"");
        json.Should().Contain("\"status\":\"Pending\"");

        // And crucially NOT any raw integer or lowercase form.
        json.Should().NotContain("\"status\":3");
        json.Should().NotContain("\"status\":2");
        json.Should().NotContain("\"status\":1");
        json.Should().NotContain("\"status\":\"completed\"");
        json.Should().NotContain("\"status\":\"inprogress\"");
    }

    // -----------------------------------------------------------------------
    // Whitelist resolution — default tools opt-in/opt-out
    // -----------------------------------------------------------------------

    [TestMethod]
    public void CreateWorkerTools_WithDefaultsAllowed_AddsReadWriteFetch()
    {
        var workDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            using var handler = new NoOpHandler();
            using var http = new HttpClient(handler);
            var template = new LoadedTemplate { AllowDefaultTools = true };
            var agentDef = new AgentDefinition { Name = "worker_1", AllowDefaultTools = null };

            var tools = SwarmToolFactory.CreateWorkerTools(
                "worker_1",
                this.mockSwarmService.Object,
                this.mockEventBus.Object,
                agUiAdapter: null,
                workDirectory: workDir,
                httpClient: http,
                template: template,
                agentDef: agentDef);

            tools.Select(t => t.Name).Should().BeEquivalentTo(
                ["task_update", "inbox_send", "inbox_receive", "task_list", "read", "write", "web_fetch"]);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [TestMethod]
    public void CreateWorkerTools_TemplateDisallowsDefaults_ReturnsCoordinationOnly()
    {
        using var handler = new NoOpHandler();
        using var http = new HttpClient(handler);
        var template = new LoadedTemplate { AllowDefaultTools = false };
        var agentDef = new AgentDefinition { Name = "worker_1", AllowDefaultTools = null };

        var tools = SwarmToolFactory.CreateWorkerTools(
            "worker_1",
            this.mockSwarmService.Object,
            this.mockEventBus.Object,
            agUiAdapter: null,
            workDirectory: Path.GetTempPath(),
            httpClient: http,
            template: template,
            agentDef: agentDef);

        tools.Select(t => t.Name).Should().BeEquivalentTo(
            ["task_update", "inbox_send", "inbox_receive", "task_list"]);
    }

    [TestMethod]
    public void CreateWorkerTools_WorkerOverridesTemplate_ToFalse()
    {
        using var handler = new NoOpHandler();
        using var http = new HttpClient(handler);
        var template = new LoadedTemplate { AllowDefaultTools = true };
        var agentDef = new AgentDefinition { Name = "worker_1", AllowDefaultTools = false };

        var tools = SwarmToolFactory.CreateWorkerTools(
            "worker_1",
            this.mockSwarmService.Object,
            this.mockEventBus.Object,
            agUiAdapter: null,
            workDirectory: Path.GetTempPath(),
            httpClient: http,
            template: template,
            agentDef: agentDef);

        tools.Should().HaveCount(4);
    }

    [TestMethod]
    public void CreateWorkerTools_WorkerOverridesTemplate_ToTrue()
    {
        var workDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            using var handler = new NoOpHandler();
            using var http = new HttpClient(handler);
            var template = new LoadedTemplate { AllowDefaultTools = false };
            var agentDef = new AgentDefinition { Name = "worker_1", AllowDefaultTools = true };

            var tools = SwarmToolFactory.CreateWorkerTools(
                "worker_1",
                this.mockSwarmService.Object,
                this.mockEventBus.Object,
                agUiAdapter: null,
                workDirectory: workDir,
                httpClient: http,
                template: template,
                agentDef: agentDef);

            tools.Should().HaveCount(7);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [TestMethod]
    public void CreateWorkerTools_ExplicitToolsList_FiltersToWhitelist()
    {
        var workDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            using var handler = new NoOpHandler();
            using var http = new HttpClient(handler);
            var template = new LoadedTemplate { AllowDefaultTools = true };
            var agentDef = new AgentDefinition
            {
                Name = "worker_1",
                Tools = ["task_update", "inbox_send", "write"], // explicit whitelist, no read/web_fetch
            };

            var tools = SwarmToolFactory.CreateWorkerTools(
                "worker_1",
                this.mockSwarmService.Object,
                this.mockEventBus.Object,
                agUiAdapter: null,
                workDirectory: workDir,
                httpClient: http,
                template: template,
                agentDef: agentDef);

            tools.Select(t => t.Name).Should().BeEquivalentTo(["task_update", "inbox_send", "write"]);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [TestMethod]
    public void CreateWorkerTools_LegacyOverload_StillReturnsFourTools()
    {
        // The 3-arg overload (used by older callers and existing tests) must keep working.
        var tools = SwarmToolFactory.CreateWorkerTools(
            "worker_1",
            this.mockSwarmService.Object,
            this.mockEventBus.Object);

        tools.Should().HaveCount(4);
        tools.Select(t => t.Name).Should().BeEquivalentTo(
            ["task_update", "inbox_send", "inbox_receive", "task_list"]);
    }

    // -----------------------------------------------------------------------
    // Bug C — task_update status normalization (snake_case, space-separated)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Verifies that task_update accepts lowercase snake_case "in_progress" and
    /// normalizes it to the <see cref="TaskState.InProgress"/> enum value in
    /// the returned JSON envelope.
    /// </summary>
    [TestMethod]
    public async Task TaskUpdate_AcceptsSnakeCase_InProgress()
    {
        // Arrange
        var tools = SwarmToolFactory.CreateWorkerTools(
            "worker_1",
            this.mockSwarmService.Object,
            this.mockEventBus.Object);

        var taskUpdateTool = (AIFunction)tools.First(t => t.Name == "task_update");
        var args = new AIFunctionArguments
        {
            ["task_id"] = "task-1",
            ["status"] = "in_progress",
            ["result"] = null,
        };

        // Act
        var result = await taskUpdateTool.InvokeAsync(args);

        // Assert
        var json = result?.ToString() ?? string.Empty;
        json.Should().Contain("\"status\":\"InProgress\"");
        json.Should().NotContain("\"error\"");
    }

    /// <summary>
    /// Verifies that task_update accepts SCREAMING_SNAKE_CASE "IN_PROGRESS".
    /// </summary>
    [TestMethod]
    public async Task TaskUpdate_AcceptsScreamingSnakeCase_IN_PROGRESS()
    {
        // Arrange
        var tools = SwarmToolFactory.CreateWorkerTools(
            "worker_1",
            this.mockSwarmService.Object,
            this.mockEventBus.Object);

        var taskUpdateTool = (AIFunction)tools.First(t => t.Name == "task_update");
        var args = new AIFunctionArguments
        {
            ["task_id"] = "task-1",
            ["status"] = "IN_PROGRESS",
            ["result"] = null,
        };

        // Act
        var result = await taskUpdateTool.InvokeAsync(args);

        // Assert
        var json = result?.ToString() ?? string.Empty;
        json.Should().Contain("\"status\":\"InProgress\"");
        json.Should().NotContain("\"error\"");
    }

    /// <summary>
    /// Verifies that task_update accepts space-separated "in progress".
    /// </summary>
    [TestMethod]
    public async Task TaskUpdate_AcceptsSpaceSeparated()
    {
        // Arrange
        var tools = SwarmToolFactory.CreateWorkerTools(
            "worker_1",
            this.mockSwarmService.Object,
            this.mockEventBus.Object);

        var taskUpdateTool = (AIFunction)tools.First(t => t.Name == "task_update");
        var args = new AIFunctionArguments
        {
            ["task_id"] = "task-1",
            ["status"] = "in progress",
            ["result"] = null,
        };

        // Act
        var result = await taskUpdateTool.InvokeAsync(args);

        // Assert
        var json = result?.ToString() ?? string.Empty;
        json.Should().Contain("\"status\":\"InProgress\"");
        json.Should().NotContain("\"error\"");
    }

    /// <summary>
    /// Regression guard: task_update continues to accept canonical PascalCase values.
    /// </summary>
    /// <param name="status">The PascalCase status string under test.</param>
    [TestMethod]
    [DataRow("InProgress")]
    [DataRow("Completed")]
    [DataRow("Failed")]
    [DataRow("Pending")]
    public async Task TaskUpdate_StillAcceptsPascalCase(string status)
    {
        // Arrange
        var tools = SwarmToolFactory.CreateWorkerTools(
            "worker_1",
            this.mockSwarmService.Object,
            this.mockEventBus.Object);

        var taskUpdateTool = (AIFunction)tools.First(t => t.Name == "task_update");
        var args = new AIFunctionArguments
        {
            ["task_id"] = "task-1",
            ["status"] = status,
            ["result"] = null,
        };

        // Act
        var result = await taskUpdateTool.InvokeAsync(args);

        // Assert
        var json = result?.ToString() ?? string.Empty;
        json.Should().Contain($"\"status\":\"{status}\"");
        json.Should().NotContain("\"error\"");
    }

    /// <summary>
    /// Verifies that an invalid status returns an error JSON that preserves the
    /// original (un-normalized) value sent by the LLM, so diagnostics remain useful.
    /// </summary>
    [TestMethod]
    public async Task TaskUpdate_InvalidStatus_ReturnsErrorWithOriginalValue()
    {
        // Arrange
        var tools = SwarmToolFactory.CreateWorkerTools(
            "worker_1",
            this.mockSwarmService.Object,
            this.mockEventBus.Object);

        var taskUpdateTool = (AIFunction)tools.First(t => t.Name == "task_update");
        var args = new AIFunctionArguments
        {
            ["task_id"] = "task-1",
            ["status"] = "bogus",
            ["result"] = null,
        };

        // Act
        var result = await taskUpdateTool.InvokeAsync(args);

        // Assert
        var json = result?.ToString() ?? string.Empty;
        json.Should().Contain("\"error\":\"Invalid status: bogus\"");
    }

    /// <summary>
    /// Verifies that a null status returns an error JSON without throwing
    /// <see cref="NullReferenceException"/>.
    /// </summary>
    [TestMethod]
    public async Task TaskUpdate_NullStatus_ReturnsErrorWithoutCrashing()
    {
        // Arrange
        var tools = SwarmToolFactory.CreateWorkerTools(
            "worker_1",
            this.mockSwarmService.Object,
            this.mockEventBus.Object);

        var taskUpdateTool = (AIFunction)tools.First(t => t.Name == "task_update");
        var args = new AIFunctionArguments
        {
            ["task_id"] = "task-1",
            ["status"] = null,
            ["result"] = null,
        };

        // Act
        Func<Task> act = async () => await taskUpdateTool.InvokeAsync(args);

        // Assert — must not throw and must produce an error JSON.
        await act.Should().NotThrowAsync();
        var result = await taskUpdateTool.InvokeAsync(args);
        var json = result?.ToString() ?? string.Empty;
        json.Should().Contain("\"error\"");
    }

    /// <summary>No-op HTTP handler used by whitelist tests; never actually invoked.</summary>
    private sealed class NoOpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}
