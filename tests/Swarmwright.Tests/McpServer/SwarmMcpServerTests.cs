using System.Text.Json;
using Swarmwright.Database.Models;
using Swarmwright.Database.Repositories;
using Swarmwright.Events;
using Swarmwright.Hosting;
using Swarmwright.McpServer;
using Swarmwright.Models.Enums;
using Swarmwright.Templates;
using FluentAssertions;
using Moq;

namespace Swarmwright.Tests.McpServer;

/// <summary>
/// Unit tests for <see cref="SwarmMcpServer"/>.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SwarmMcpServerTests
{
    private readonly Mock<ISwarmManager> mockSwarmManager = new();
    private readonly Mock<ISwarmRepository> mockRepository = new();
    private readonly Mock<ITemplateLoader> mockTemplateLoader = new();

    /// <summary>
    /// Verifies that GetActiveSwarmsAsync returns the active swarm list.
    /// </summary>
    [TestMethod]
    public async Task GetActiveSwarmsAsync_ReturnsActiveList()
    {
        // Arrange
        var executions = new List<SwarmExecution>
        {
            new() { SwarmId = Guid.NewGuid(), Goal = "Goal A", TemplateKey = "template-a", Cts = new CancellationTokenSource(), EventBus = new SwarmEventBus(), AgUiAdapter = new Swarmwright.Events.AgUI.SwarmEventAdapter(), WorkDirectory = Path.GetTempPath() },
            new() { SwarmId = Guid.NewGuid(), Goal = "Goal B", Cts = new CancellationTokenSource(), EventBus = new SwarmEventBus(), AgUiAdapter = new Swarmwright.Events.AgUI.SwarmEventAdapter(), WorkDirectory = Path.GetTempPath() },
        };

        this.mockSwarmManager.Setup(m => m.ListActiveSwarms()).Returns(executions);

        var server = this.CreateServer();

        // Act
        var result = await server.GetActiveSwarmsAsync();

        // Assert
        var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        root.GetProperty("swarms").GetArrayLength().Should().Be(2);
        root.GetProperty("swarms")[0].GetProperty("goal").GetString().Should().Be("Goal A");
        root.GetProperty("swarms")[1].GetProperty("goal").GetString().Should().Be("Goal B");
    }

    /// <summary>
    /// Verifies that GetSwarmStatusAsync returns the swarm status.
    /// </summary>
    [TestMethod]
    public async Task GetSwarmStatusAsync_ReturnsStatus()
    {
        // Arrange
        var swarmId = Guid.NewGuid();
        using var execution = new SwarmExecution
        {
            SwarmId = swarmId,
            Goal = "Build widgets",
            TemplateKey = "widget-template",
            Cts = new CancellationTokenSource(),
            EventBus = new SwarmEventBus(),
            AgUiAdapter = new Swarmwright.Events.AgUI.SwarmEventAdapter(),
            WorkDirectory = Path.GetTempPath(),
        };

        this.mockSwarmManager.Setup(m => m.GetSwarm(swarmId)).Returns(execution);
        this.mockRepository
            .Setup(r => r.GetSwarmAsync(swarmId))
            .ReturnsAsync(new Swarmwright.Database.Models.SwarmEntity
            {
                Id = swarmId,
                Goal = "Build widgets",
                State = "Executing",
            });

        var server = this.CreateServer();

        // Act
        var result = await server.GetSwarmStatusAsync(swarmId);

        // Assert
        var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        root.GetProperty("swarmId").GetString().Should().Be(swarmId.ToString());
        root.GetProperty("phase").GetString().Should().Be("Executing");
        root.GetProperty("goal").GetString().Should().Be("Build widgets");
        root.GetProperty("template").GetString().Should().Be("widget-template");
    }

    /// <summary>
    /// Verifies that GetSwarmStatusAsync returns an error for an unknown swarm ID.
    /// </summary>
    [TestMethod]
    public async Task GetSwarmStatusAsync_UnknownId_ReturnsError()
    {
        // Arrange
        var unknownId = Guid.NewGuid();
        this.mockSwarmManager.Setup(m => m.GetSwarm(unknownId)).Returns((SwarmExecution?)null);

        var server = this.CreateServer();

        // Act
        var result = await server.GetSwarmStatusAsync(unknownId);

        // Assert
        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("error").GetString().Should().Contain("not found");
    }

    /// <summary>
    /// Verifies that ListTasksAsync returns tasks from the repository.
    /// </summary>
    [TestMethod]
    public async Task ListTasksAsync_ReturnsTasks()
    {
        // Arrange
        var swarmId = Guid.NewGuid();
        var tasks = new List<TaskEntity>
        {
            new() { SwarmId = swarmId, Id = "T1", Subject = "Task One", State = "pending", WorkerName = "worker-a" },
            new() { SwarmId = swarmId, Id = "T2", Subject = "Task Two", State = "complete", WorkerName = "worker-b" },
        };

        this.mockRepository.Setup(r => r.GetTasksAsync(swarmId)).ReturnsAsync(tasks);

        var server = this.CreateServer();

        // Act
        var result = await server.ListTasksAsync(swarmId);

        // Assert
        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("tasks").GetArrayLength().Should().Be(2);
    }

    /// <summary>
    /// Verifies that ListTasksAsync filters by status when provided.
    /// </summary>
    [TestMethod]
    public async Task ListTasksAsync_FiltersByStatus()
    {
        // Arrange
        var swarmId = Guid.NewGuid();
        var tasks = new List<TaskEntity>
        {
            new() { SwarmId = swarmId, Id = "T1", Subject = "Task One", State = "pending", WorkerName = "worker-a" },
            new() { SwarmId = swarmId, Id = "T2", Subject = "Task Two", State = "complete", WorkerName = "worker-b" },
        };

        this.mockRepository.Setup(r => r.GetTasksAsync(swarmId)).ReturnsAsync(tasks);

        var server = this.CreateServer();

        // Act
        var result = await server.ListTasksAsync(swarmId, status: "pending");

        // Assert
        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("tasks").GetArrayLength().Should().Be(1);
        doc.RootElement.GetProperty("tasks")[0].GetProperty("id").GetString().Should().Be("T1");
    }

    /// <summary>
    /// Verifies that ListTasksAsync filters by worker when provided.
    /// </summary>
    [TestMethod]
    public async Task ListTasksAsync_FiltersByWorker()
    {
        // Arrange
        var swarmId = Guid.NewGuid();
        var tasks = new List<TaskEntity>
        {
            new() { SwarmId = swarmId, Id = "T1", Subject = "Task One", State = "pending", WorkerName = "worker-a" },
            new() { SwarmId = swarmId, Id = "T2", Subject = "Task Two", State = "complete", WorkerName = "worker-b" },
        };

        this.mockRepository.Setup(r => r.GetTasksAsync(swarmId)).ReturnsAsync(tasks);

        var server = this.CreateServer();

        // Act
        var result = await server.ListTasksAsync(swarmId, worker: "worker-b");

        // Assert
        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("tasks").GetArrayLength().Should().Be(1);
        doc.RootElement.GetProperty("tasks")[0].GetProperty("id").GetString().Should().Be("T2");
    }

    /// <summary>
    /// Verifies that ListAgentsAsync returns agents from the repository.
    /// </summary>
    [TestMethod]
    public async Task ListAgentsAsync_ReturnsAgents()
    {
        // Arrange
        var swarmId = Guid.NewGuid();
        var agents = new List<AgentEntity>
        {
            new() { SwarmId = swarmId, Name = "agent-a", Role = "researcher", Status = "idle" },
            new() { SwarmId = swarmId, Name = "agent-b", Role = "coder", Status = "working" },
        };

        this.mockRepository.Setup(r => r.GetAgentsAsync(swarmId)).ReturnsAsync(agents);

        var server = this.CreateServer();

        // Act
        var result = await server.ListAgentsAsync(swarmId);

        // Assert
        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("agents").GetArrayLength().Should().Be(2);
        doc.RootElement.GetProperty("agents")[0].GetProperty("name").GetString().Should().Be("agent-a");
    }

    /// <summary>
    /// Verifies that GetRecentEventsAsync returns events from the repository.
    /// </summary>
    [TestMethod]
    public async Task GetRecentEventsAsync_ReturnsEvents()
    {
        // Arrange
        var swarmId = Guid.NewGuid();
        var events = new List<EventEntity>
        {
            new() { SwarmId = swarmId, EventType = "task.started", DataJson = "{}" },
            new() { SwarmId = swarmId, EventType = "task.completed", DataJson = "{}" },
        };

        this.mockRepository.Setup(r => r.GetEventsAsync(swarmId, 50)).ReturnsAsync(events);

        var server = this.CreateServer();

        // Act
        var result = await server.GetRecentEventsAsync(swarmId);

        // Assert
        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("events").GetArrayLength().Should().Be(2);
        doc.RootElement.GetProperty("events")[0].GetProperty("eventType").GetString().Should().Be("task.started");
    }

    /// <summary>
    /// Verifies that GetSwarmTemplatesAsync returns the template list.
    /// </summary>
    [TestMethod]
    public async Task GetSwarmTemplatesAsync_ReturnsList()
    {
        // Arrange
        var keys = new List<string> { "code-review", "data-analysis" };
        this.mockTemplateLoader.Setup(t => t.ListAvailable()).Returns(keys);

        var server = this.CreateServer();

        // Act
        var result = await server.GetSwarmTemplatesAsync();

        // Assert
        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("templates").GetArrayLength().Should().Be(2);
        doc.RootElement.GetProperty("templates")[0].GetString().Should().Be("code-review");
        doc.RootElement.GetProperty("templates")[1].GetString().Should().Be("data-analysis");
    }

    /// <summary>
    /// Verifies that CreateSwarmAsync calls the manager and returns the swarm ID.
    /// </summary>
    [TestMethod]
    public async Task CreateSwarmAsync_StartsSwarm()
    {
        // Arrange
        var expectedId = Guid.NewGuid();
        this.mockSwarmManager
            .Setup(m => m.CreateSwarmAsync(
                "Build a thing",
                "my-template",
                It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .ReturnsAsync(expectedId);

        var server = this.CreateServer();

        // Act
        var result = await server.CreateSwarmAsync("Build a thing", "my-template");

        // Assert
        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("swarmId").GetString().Should().Be(expectedId.ToString());
        this.mockSwarmManager.Verify(
            m => m.CreateSwarmAsync("Build a thing", "my-template", It.IsAny<IReadOnlyDictionary<string, string>?>()),
            Times.Once);
    }

    /// <summary>
    /// Verifies that CreateSwarmAsync forwards an optional context bag to the manager.
    /// </summary>
    [TestMethod]
    public async Task CreateSwarmAsync_ForwardsContext_ToManager()
    {
        // Arrange
        IReadOnlyDictionary<string, string>? captured = null;
        this.mockSwarmManager
            .Setup(m => m.CreateSwarmAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .Callback<string, string?, IReadOnlyDictionary<string, string>?>((_, _, ctx) => captured = ctx)
            .ReturnsAsync(Guid.NewGuid());

        var server = this.CreateServer();
        var context = new Dictionary<string, string> { ["sourceRoot"] = "/clones/pr-9b" };

        // Act
        await server.CreateSwarmAsync("Build a thing", "my-template", context);

        // Assert
        captured.Should().NotBeNull();
        captured.Should().ContainKey("sourceRoot")
            .WhoseValue.Should().Be("/clones/pr-9b");
    }

    private Swarmwright.McpServer.SwarmMcpServer CreateServer()
    {
        return new Swarmwright.McpServer.SwarmMcpServer(
            this.mockSwarmManager.Object,
            this.mockRepository.Object,
            this.mockTemplateLoader.Object);
    }
}
