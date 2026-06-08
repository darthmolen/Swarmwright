using Swarmwright.Database.Models;
using Swarmwright.Database.Repositories;
using Swarmwright.Events;
using Swarmwright.Hosting.StateMachine;
using Swarmwright.Models.Enums;
using Swarmwright.SelfHealing;
using Swarmwright.Tests.Hosting.StateMachine;
using FluentAssertions;
using Moq;

namespace Swarmwright.Tests.SelfHealing;

/// <summary>
/// Unit tests for <see cref="OrphanRecovery"/>. Post state-machine
/// refactor, recovery transitions non-terminal orphans to
/// <see cref="SwarmInstanceState.AwaitingIntervention"/> via
/// <see cref="IStateTransitionService"/>.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class OrphanRecoveryTests
{
    private readonly Mock<ISwarmRepository> mockRepository = new();
    private readonly Mock<ISwarmEventBus> mockEventBus = new();
    private readonly NoOpStateTransitionService transitionService = new();

    [TestMethod]
    public async Task RecoverAsync_TransitionsNonTerminalSwarmsToAwaitingIntervention()
    {
        var swarm1 = new SwarmEntity { Id = Guid.NewGuid(), State = nameof(SwarmInstanceState.Executing) };
        var swarm2 = new SwarmEntity { Id = Guid.NewGuid(), State = nameof(SwarmInstanceState.Planning) };
        this.mockRepository
            .Setup(r => r.ListSwarmsByStateAsync(It.IsAny<string[]>()))
            .ReturnsAsync(new List<SwarmEntity> { swarm1, swarm2 });
        this.mockEventBus
            .Setup(e => e.EmitAsync(It.IsAny<string>(), It.IsAny<object?>()))
            .Returns(Task.CompletedTask);

        var recovery = new OrphanRecovery(this.mockRepository.Object, this.transitionService, this.mockEventBus.Object);

        await recovery.RecoverAsync();

        this.transitionService.SwarmCalls.Should().HaveCount(2);
        this.transitionService.SwarmCalls.Should()
            .OnlyContain(c => c.ToState == SwarmInstanceState.AwaitingIntervention);
        this.transitionService.SwarmCalls.Should()
            .OnlyContain(c => c.Reason == TransitionReasons.TaskFailed);
    }

    [TestMethod]
    public async Task RecoverAsync_IgnoresTerminalSwarms()
    {
        this.mockRepository
            .Setup(r => r.ListSwarmsByStateAsync(It.IsAny<string[]>()))
            .ReturnsAsync(new List<SwarmEntity>());

        var recovery = new OrphanRecovery(this.mockRepository.Object, this.transitionService, this.mockEventBus.Object);

        await recovery.RecoverAsync();

        this.transitionService.SwarmCalls.Should().BeEmpty();
    }

    [TestMethod]
    public async Task RecoverAsync_EmitsOrphanRecoveredEvent()
    {
        var swarm = new SwarmEntity { Id = Guid.NewGuid(), State = nameof(SwarmInstanceState.Executing) };
        this.mockRepository
            .Setup(r => r.ListSwarmsByStateAsync(It.IsAny<string[]>()))
            .ReturnsAsync(new List<SwarmEntity> { swarm });
        this.mockEventBus
            .Setup(e => e.EmitAsync(It.IsAny<string>(), It.IsAny<object?>()))
            .Returns(Task.CompletedTask);

        var recovery = new OrphanRecovery(this.mockRepository.Object, this.transitionService, this.mockEventBus.Object);

        await recovery.RecoverAsync();

        this.mockEventBus.Verify(
            e => e.EmitAsync("swarm.orphan_recovered", It.IsAny<object?>()),
            Times.Once);
    }
}
