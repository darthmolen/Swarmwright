using Swarmwright.Database.Models;
using Swarmwright.Events;
using Swarmwright.Hosting.StateMachine;
using Swarmwright.Models.Enums;
using FluentAssertions;
using Moq;
using Swarmwright.Hosting;

namespace Swarmwright.Tests.Hosting.StateMachine;

/// <summary>
/// Covers the emission contract around
/// <see cref="IStateTransitionService.TransitionTaskAsync"/>: every
/// successful task transition unconditionally calls
/// <see cref="ISwarmEmissionBroker.EmitTaskUpdatedAsync"/>. A rejected
/// transition (guard fails) never commits and therefore never emits.
/// There is no opt-out — callers that do not care about emission
/// supply a no-op broker, not a null one. That was the code smell the
/// refactor removed.
/// </summary>
[TestClass]
public sealed class TaskStateEmissionTests
{
    private InMemoryDbContextFactory factory = null!;
    private Mock<ISwarmEmissionBroker> broker = null!;
    private StateTransitionService service = null!;

    [TestInitialize]
    public void Initialize()
    {
        this.factory = new InMemoryDbContextFactory("TaskStateEmission_" + Guid.NewGuid());
        this.broker = new Mock<ISwarmEmissionBroker>();
        this.service = new StateTransitionService(this.factory, this.broker.Object, Mock.Of<ISwarmObservationSink>());
    }

    [TestMethod]
    public async Task TransitionTaskAsync_AlwaysCallsBroker_NoOptOut()
    {
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.Executing);
        var taskId = await this.SeedTaskAsync(swarmId, TaskState.Pending, workerName: "architect");

        await this.service.TransitionTaskAsync(
            swarmId,
            taskId,
            TaskState.InProgress,
            TransitionReasons.PhaseAdvanced,
            actor: "system");

        this.broker.Verify(
            b => b.EmitTaskUpdatedAsync(
                swarmId,
                taskId,
                TaskState.InProgress,
                "architect",
                It.IsAny<CancellationToken>()),
            Times.Once,
            "every task transition must emit exactly once — the null-adapter opt-out the old design allowed was the smell we removed");
    }

    [TestMethod]
    public async Task TransitionTaskAsync_WhenGuardRejects_DoesNotCallBroker()
    {
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.Executing);
        var taskId = await this.SeedTaskAsync(swarmId, TaskState.Blocked, workerName: "architect");

        var act = () => this.service.TransitionTaskAsync(
            swarmId,
            taskId,
            TaskState.InProgress,
            TransitionReasons.PhaseAdvanced,
            actor: "system");

        await act.Should().ThrowAsync<InvalidStateTransitionException>();

        this.broker.Verify(
            b => b.EmitTaskUpdatedAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<TaskState>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Never,
            "rejected transitions never commit a DB write, so they must never emit either");
    }

    [TestMethod]
    public async Task TransitionTaskAsync_PassesAgentNameFromTaskEntity()
    {
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.Executing);
        var taskId = await this.SeedTaskAsync(swarmId, TaskState.Pending, workerName: "cost-expert");

        await this.service.TransitionTaskAsync(
            swarmId,
            taskId,
            TaskState.InProgress,
            TransitionReasons.PhaseAdvanced,
            actor: "system");

        this.broker.Verify(
            b => b.EmitTaskUpdatedAsync(
                swarmId,
                taskId,
                TaskState.InProgress,
                "cost-expert",
                It.IsAny<CancellationToken>()),
            Times.Once,
            "the agent field on the emitted event must come from TaskEntity.WorkerName so the frontend can attribute the update");
    }

    [TestMethod]
    public async Task TransitionTaskAsync_WhenBrokerThrows_DoesNotRollBackPersistedTransition()
    {
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.Executing);
        var taskId = await this.SeedTaskAsync(swarmId, TaskState.Pending, workerName: "architect");

        this.broker
            .Setup(b => b.EmitTaskUpdatedAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<TaskState>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("simulated channel failure"));

        await this.service.TransitionTaskAsync(
            swarmId,
            taskId,
            TaskState.InProgress,
            TransitionReasons.PhaseAdvanced,
            actor: "system");

        await using var ctx = this.factory.CreateDbContext();
        var task = await ctx.Tasks.FindAsync(swarmId, taskId);
        task!.State.Should().Be(
            nameof(TaskState.InProgress),
            "emission failure must not undo the persisted DB transition — the contract is fire-and-forget after commit");
    }

    // ---- Helpers ----

    private async Task<Guid> SeedSwarmAsync(SwarmInstanceState state)
    {
        await using var ctx = this.factory.CreateDbContext();
        var swarm = new SwarmEntity
        {
            Id = Guid.NewGuid(),
            Goal = "test goal",
            State = state.ToString(),
        };
        ctx.Swarms.Add(swarm);
        await ctx.SaveChangesAsync();
        return swarm.Id;
    }

    private async Task<string> SeedTaskAsync(Guid swarmId, TaskState state, string workerName)
    {
        await using var ctx = this.factory.CreateDbContext();
        var taskId = "t-" + Guid.NewGuid().ToString("N")[..8];
        var task = new TaskEntity
        {
            SwarmId = swarmId,
            Id = taskId,
            Subject = "test task",
            Description = "test description",
            WorkerRole = "worker",
            WorkerName = workerName,
            State = state.ToString(),
        };
        ctx.Tasks.Add(task);
        await ctx.SaveChangesAsync();
        return taskId;
    }
}
