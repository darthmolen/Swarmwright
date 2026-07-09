using Swarmwright.Core;
using Swarmwright.Database.Models;
using Swarmwright.Database.Repositories;
using Swarmwright.Models;
using Swarmwright.Models.Enums;
using Swarmwright.Services;
using FluentAssertions;
using Moq;

namespace Swarmwright.Tests.Services;

/// <summary>
/// Unit tests for <see cref="SwarmService"/>.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SwarmServiceTests
{
    private Mock<IInboxSystem> mockInbox = null!;
    private Mock<ITeamRegistry> mockRegistry = null!;
    private Mock<ISwarmRepository> mockRepo = null!;
    private SwarmService sut = null!;

    /// <summary>
    /// Initializes mocks and the system under test before each test.
    /// </summary>
    [TestInitialize]
    public void TestInitialize()
    {
        this.mockInbox = new Mock<IInboxSystem>();
        this.mockRegistry = new Mock<ITeamRegistry>();
        this.mockRepo = new Mock<ISwarmRepository>();
        this.sut = new SwarmService(
            this.mockInbox.Object,
            this.mockRegistry.Object,
            this.mockRepo.Object);
    }

    /// <summary>
    /// Verifies that CreateSwarmAsync sets the SwarmId property.
    /// </summary>
    [TestMethod]
    public async Task CreateSwarmAsync_SetsSwarmId()
    {
        // Arrange
        var swarmId = Guid.NewGuid();

        // Act
        await this.sut.CreateSwarmAsync(swarmId, "Test goal");

        // Assert
        this.sut.SwarmId.Should().Be(swarmId);
    }

    /// <summary>
    /// Verifies that CreateSwarmAsync persists to the repository.
    /// </summary>
    [TestMethod]
    public async Task CreateSwarmAsync_PersistsToRepository()
    {
        // Arrange
        var swarmId = Guid.NewGuid();

        // Act
        await this.sut.CreateSwarmAsync(swarmId, "Test goal", "tpl-1");

        // Assert
        this.mockRepo.Verify(
            r => r.CreateSwarmAsync(It.Is<SwarmEntity>(e =>
                e.Id == swarmId && e.Goal == "Test goal" && e.TemplateKey == "tpl-1")),
            Times.Once);
    }

    /// <summary>
    /// Verifies that AddTaskAsync persists the task to the repository.
    /// F01.3: the in-memory TaskBoard is no longer the storage path; the
    /// repository row is the only durable surface.
    /// </summary>
    [TestMethod]
    public async Task AddTaskAsync_PersistsToRepository()
    {
        // Arrange
        var swarmId = Guid.NewGuid();
        await this.sut.CreateSwarmAsync(swarmId, "goal");
        var task = new SwarmTask
        {
            Id = "task-1",
            Subject = "Task One",
            Description = "Do work",
            WorkerRole = "coder",
            WorkerName = "worker_1",
        };

        // Act
        await this.sut.AddTaskAsync(task);

        // Assert — Status is persisted in PascalCase enum form (e.g. "Pending"),
        // matching TaskState.ToString(). The old lowercase form is gone.
        this.mockRepo.Verify(
            r => r.CreateTaskAsync(It.Is<TaskEntity>(e =>
                e.SwarmId == swarmId &&
                e.Id == "task-1" &&
                e.Subject == "Task One" &&
                e.WorkerRole == "coder" &&
                e.State == "Pending")),
            Times.Once);
    }

    /// <summary>
    /// Verifies that AddTaskAsync derives Blocked status when the task has
    /// dependencies. The heuristic used to live in TaskBoard.AddTaskAsync;
    /// after the F01.3 cache kill it lives inline in SwarmService so the
    /// persisted row reflects Blocked vs Pending correctly.
    /// </summary>
    [TestMethod]
    public async Task AddTaskAsync_TaskWithBlockers_PersistsAsBlocked()
    {
        // Arrange
        var swarmId = Guid.NewGuid();
        await this.sut.CreateSwarmAsync(swarmId, "goal");
        var task = new SwarmTask
        {
            Id = "task-2",
            Subject = "Dependent",
            Description = "Waits on task-1",
            WorkerRole = "coder",
            WorkerName = "worker_1",
        };
        task.BlockedBy.Add("task-1");

        // Act
        await this.sut.AddTaskAsync(task);

        // Assert
        this.mockRepo.Verify(
            r => r.CreateTaskAsync(It.Is<TaskEntity>(e =>
                e.Id == "task-2" && e.State == "Blocked")),
            Times.Once);
    }

    /// <summary>
    /// Verifies that SendMessageAsync delegates to the InboxSystem.
    /// </summary>
    [TestMethod]
    public async Task SendMessageAsync_DelegatesToInboxSystem()
    {
        // Arrange
        var sender = "agent-a";
        var recipient = "agent-b";
        var content = "Hello";

        // Act
        await this.sut.SendMessageAsync(sender, recipient, content);

        // Assert
        this.mockInbox.Verify(
            inbox => inbox.SendAsync(sender, recipient, content),
            Times.Once);
    }

    /// <summary>
    /// Verifies that RegisterAgentAsync delegates to the TeamRegistry.
    /// </summary>
    [TestMethod]
    public async Task RegisterAgentAsync_DelegatesToTeamRegistry()
    {
        // Arrange
        var agent = new AgentInfo { Name = "worker-1", Role = "coder" };

        // Act
        await this.sut.RegisterAgentAsync(agent);

        // Assert
        this.mockRegistry.Verify(r => r.RegisterAsync(agent), Times.Once);
    }

    /// <summary>
    /// Verifies that GetRunnableTasksAsync reads from the repository,
    /// not the in-memory TaskBoard (F01.3 cache kill).
    /// </summary>
    [TestMethod]
    public async Task GetRunnableTasksAsync_ReadsFromRepository()
    {
        // Arrange
        var swarmId = Guid.NewGuid();
        await this.sut.CreateSwarmAsync(swarmId, "goal");
        var entities = new List<TaskEntity>
        {
            new()
            {
                SwarmId = swarmId,
                Id = "task-1",
                Subject = "S",
                Description = "D",
                WorkerName = "w",
                WorkerRole = "r",
                State = nameof(TaskState.Pending),
                BlockedByJson = "[]",
            },
        };
        this.mockRepo
            .Setup(r => r.GetRunnableTasksAsync(swarmId))
            .ReturnsAsync(entities);

        // Act
        var result = await this.sut.GetRunnableTasksAsync();

        // Assert
        result.Should().ContainSingle().Which.Id.Should().Be("task-1");
        this.mockRepo.Verify(r => r.GetRunnableTasksAsync(swarmId), Times.Once);
    }

    /// <summary>
    /// Verifies that GetRunnableTasksAsync filters the repository result
    /// by worker name when one is supplied.
    /// </summary>
    [TestMethod]
    public async Task GetRunnableTasksAsync_FiltersByWorkerName()
    {
        // Arrange
        var swarmId = Guid.NewGuid();
        await this.sut.CreateSwarmAsync(swarmId, "goal");
        var entities = new List<TaskEntity>
        {
            new()
            {
                SwarmId = swarmId,
                Id = "task-1",
                Subject = "S",
                Description = "D",
                WorkerName = "alpha",
                WorkerRole = "r",
                State = nameof(TaskState.Pending),
                BlockedByJson = "[]",
            },
            new()
            {
                SwarmId = swarmId,
                Id = "task-2",
                Subject = "S",
                Description = "D",
                WorkerName = "beta",
                WorkerRole = "r",
                State = nameof(TaskState.Pending),
                BlockedByJson = "[]",
            },
        };
        this.mockRepo
            .Setup(r => r.GetRunnableTasksAsync(swarmId))
            .ReturnsAsync(entities);

        // Act
        var result = await this.sut.GetRunnableTasksAsync("beta");

        // Assert
        result.Should().ContainSingle().Which.WorkerName.Should().Be("beta");
    }

    /// <summary>
    /// Verifies that GetTasksAsync reads from the repository, not the
    /// in-memory TaskBoard (F01.3 cache kill).
    /// </summary>
    [TestMethod]
    public async Task GetTasksAsync_ReadsFromRepository()
    {
        // Arrange
        var swarmId = Guid.NewGuid();
        await this.sut.CreateSwarmAsync(swarmId, "goal");
        var entities = new List<TaskEntity>
        {
            new()
            {
                SwarmId = swarmId,
                Id = "task-1",
                Subject = "S",
                Description = "D",
                WorkerName = "worker-1",
                WorkerRole = "r",
                State = nameof(TaskState.Pending),
                BlockedByJson = "[]",
            },
            new()
            {
                SwarmId = swarmId,
                Id = "task-2",
                Subject = "S",
                Description = "D",
                WorkerName = "worker-2",
                WorkerRole = "r",
                State = nameof(TaskState.Completed),
                BlockedByJson = "[]",
            },
        };
        this.mockRepo
            .Setup(r => r.GetTasksAsync(swarmId))
            .ReturnsAsync(entities);

        // Act
        var result = await this.sut.GetTasksAsync("worker-1");

        // Assert
        result.Should().ContainSingle().Which.Id.Should().Be("task-1");
        this.mockRepo.Verify(r => r.GetTasksAsync(swarmId), Times.Once);
    }

    /// <summary>
    /// Verifies that UpdateRoundAsync updates the round number.
    /// </summary>
    [TestMethod]
    public async Task UpdateRoundAsync_UpdatesRoundNumber()
    {
        // Arrange
        await this.sut.CreateSwarmAsync(Guid.NewGuid(), "goal");

        // Act
        await this.sut.UpdateRoundAsync(3);

        // Assert
        this.sut.State.RoundNumber.Should().Be(3);
    }

    /// <summary>
    /// Verifies that UpdateRoundAsync persists to the repository.
    /// </summary>
    [TestMethod]
    public async Task UpdateRoundAsync_PersistsToRepository()
    {
        // Arrange
        var swarmId = Guid.NewGuid();
        await this.sut.CreateSwarmAsync(swarmId, "goal");

        // Act
        await this.sut.UpdateRoundAsync(3);

        // Assert
        this.mockRepo.Verify(r => r.UpdateRoundAsync(swarmId, 3), Times.Once);
    }

    /// <summary>
    /// Verifies that RegisterAgentAsync also registers the agent in the InboxSystem.
    /// </summary>
    [TestMethod]
    public async Task RegisterAgentAsync_AlsoRegistersInInboxSystem()
    {
        // Arrange
        var agent = new AgentInfo { Name = "worker-1", Role = "coder" };

        // Act
        await this.sut.RegisterAgentAsync(agent);

        // Assert
        this.mockInbox.Verify(inbox => inbox.RegisterAgent(agent.Name), Times.Once);
    }

    /// <summary>
    /// Verifies that RegisterAgentAsync persists to the repository.
    /// </summary>
    [TestMethod]
    public async Task RegisterAgentAsync_PersistsToRepository()
    {
        // Arrange
        var swarmId = Guid.NewGuid();
        await this.sut.CreateSwarmAsync(swarmId, "goal");
        var agent = new AgentInfo { Name = "worker-1", Role = "coder", DisplayName = "Worker 1" };

        // Act
        await this.sut.RegisterAgentAsync(agent);

        // Assert
        this.mockRepo.Verify(
            r => r.RegisterAgentAsync(It.Is<AgentEntity>(e =>
                e.SwarmId == swarmId &&
                e.Name == "worker-1" &&
                e.Role == "coder" &&
                e.DisplayName == "Worker 1")),
            Times.Once);
    }

    /// <summary>
    /// Verifies that CreateSwarmAsync sets the goal on the state.
    /// </summary>
    [TestMethod]
    public async Task CreateSwarmAsync_SetsGoalAndTemplateKey()
    {
        // Arrange
        var swarmId = Guid.NewGuid();

        // Act
        await this.sut.CreateSwarmAsync(swarmId, "Build something", "template-1");

        // Assert
        this.sut.State.Goal.Should().Be("Build something");
        this.sut.State.TemplateKey.Should().Be("template-1");
    }

    /// <summary>
    /// Verifies that SendMessageAsync persists to the repository.
    /// </summary>
    [TestMethod]
    public async Task SendMessageAsync_PersistsToRepository()
    {
        // Arrange
        var swarmId = Guid.NewGuid();
        await this.sut.CreateSwarmAsync(swarmId, "goal");

        // Act
        await this.sut.SendMessageAsync("agent-a", "agent-b", "Hello");

        // Assert
        this.mockRepo.Verify(
            r => r.SaveMessageAsync(It.Is<MessageEntity>(e =>
                e.SwarmId == swarmId &&
                e.Sender == "agent-a" &&
                e.Recipient == "agent-b" &&
                e.Content == "Hello")),
            Times.Once);
    }

    /// <summary>
    /// Verifies that SaveFileAsync persists to the repository.
    /// </summary>
    [TestMethod]
    public async Task SaveFileAsync_PersistsToRepository()
    {
        // Arrange
        var swarmId = Guid.NewGuid();
        await this.sut.CreateSwarmAsync(swarmId, "goal");

        // Act
        await this.sut.SaveFileAsync("/output/report.txt", 1024);

        // Assert
        this.mockRepo.Verify(
            r => r.SaveFileAsync(It.Is<FileEntity>(e =>
                e.SwarmId == swarmId &&
                e.Path == "/output/report.txt" &&
                e.SizeBytes == 1024)),
            Times.Once);
    }

    /// <summary>
    /// Verifies that LoadAsync restores the swarm state header (id, goal,
    /// state, round number) from the repository. F01.3: tasks are no
    /// longer hydrated into a cache; they're read through to GetTasksAsync
    /// at call time.
    /// </summary>
    [TestMethod]
    public async Task LoadAsync_RestoresSwarmStateHeader()
    {
        // Arrange
        var swarmId = Guid.NewGuid();
        var swarmEntity = new SwarmEntity
        {
            Id = swarmId,
            Goal = "Loaded goal",
            State = "Executing",
            CurrentRound = 2,
        };

        this.mockRepo
            .Setup(r => r.LoadSwarmStateAsync(swarmId))
            .ReturnsAsync((swarmEntity, new List<TaskEntity>(), new List<AgentEntity>(), new List<MessageEntity>()));

        // Act
        await this.sut.LoadAsync(swarmId);

        // Assert
        this.sut.State.SwarmId.Should().Be(swarmId);
        this.sut.State.Goal.Should().Be("Loaded goal");
        this.sut.State.State.Should().Be(SwarmInstanceState.Executing);
        this.sut.State.RoundNumber.Should().Be(2);
    }

    /// <summary>
    /// Verifies that LoadAsync throws when the swarm is not found.
    /// </summary>
    [TestMethod]
    public async Task LoadAsync_ThrowsWhenSwarmNotFound()
    {
        // Arrange
        var swarmId = Guid.NewGuid();
        this.mockRepo
            .Setup(r => r.LoadSwarmStateAsync(swarmId))
            .ReturnsAsync((null, new List<TaskEntity>(), new List<AgentEntity>(), new List<MessageEntity>()));

        // Act
        var act = () => this.sut.LoadAsync(swarmId);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"Swarm {swarmId} not found*");
    }

    /// <summary>
    /// Verifies that <see cref="SwarmService.LoadAsync"/> registers the
    /// well-known <c>leader</c> inbox before replaying messages. Before
    /// this fix, replay of a worker-to-leader message after
    /// <see cref="SwarmService.LoadAsync"/> cleared the inbox cache
    /// threw because <c>leader</c> is only registered inside
    /// <c>SwarmOrchestrator.SpawnAsync</c> — which the resume-aware
    /// <c>RunAsync</c> skips when the swarm was already past Spawning.
    /// </summary>
    /// <returns>A task representing the asynchronous test operation.</returns>
    [TestMethod]
    public async Task LoadAsync_WithMessageToLeader_RegistersLeaderInboxBeforeReplay()
    {
        // Arrange: a swarm with one persisted worker→leader message.
        var swarmId = Guid.NewGuid();
        var swarmEntity = new SwarmEntity
        {
            Id = swarmId,
            Goal = "goal",
            State = nameof(SwarmInstanceState.Executing),
        };
        var messages = new List<MessageEntity>
        {
            new()
            {
                SwarmId = swarmId,
                Sender = "worker-1",
                Recipient = "leader",
                Content = "I'm done.",
            },
        };

        this.mockRepo
            .Setup(r => r.LoadSwarmStateAsync(swarmId))
            .ReturnsAsync((swarmEntity, new List<TaskEntity>(), new List<AgentEntity>(), messages));

        // Act
        await this.sut.LoadAsync(swarmId);

        // Assert: LoadAsync must register the 'leader' inbox so the
        // subsequent message replay can route worker→leader traffic.
        this.mockInbox.Verify(
            i => i.RegisterAgent("leader"),
            Times.AtLeastOnce,
            "LoadAsync must register the well-known 'leader' inbox before replaying worker-to-leader messages");
    }

    /// <summary>
    /// Verifies that LoadAsync restores agents and registers them in the inbox.
    /// </summary>
    [TestMethod]
    public async Task LoadAsync_RestoresAgentsAndInbox()
    {
        // Arrange
        var swarmId = Guid.NewGuid();
        var swarmEntity = new SwarmEntity
        {
            Id = swarmId,
            Goal = "goal",
            State = "Created",
        };
        var agentEntities = new List<AgentEntity>
        {
            new()
            {
                SwarmId = swarmId,
                Name = "worker-1",
                Role = "coder",
                DisplayName = "Worker 1",
                Status = "idle",
            },
        };

        this.mockRepo
            .Setup(r => r.LoadSwarmStateAsync(swarmId))
            .ReturnsAsync((swarmEntity, new List<TaskEntity>(), agentEntities, new List<MessageEntity>()));

        // Act
        await this.sut.LoadAsync(swarmId);

        // Assert
        this.mockRegistry.Verify(
            r => r.RegisterAsync(It.Is<AgentInfo>(a => a.Name == "worker-1")),
            Times.Once);
        this.mockInbox.Verify(i => i.RegisterAgent("worker-1"), Times.Once);
    }
}
