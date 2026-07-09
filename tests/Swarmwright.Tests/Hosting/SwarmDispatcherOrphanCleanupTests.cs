using System.Collections.Concurrent;
using System.Threading.Channels;
using Swarmwright.Configuration;
using Swarmwright.Database.Models;
using Swarmwright.Database.Repositories;
using Swarmwright.Hosting;
using Swarmwright.Hosting.StateMachine;
using Swarmwright.Models.Enums;
using Swarmwright.Tests.Hosting.StateMachine;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Swarmwright.Tests.Hosting;

/// <summary>
/// Layer 1b coverage for the orphan-InProgress defense-in-depth plan. When
/// <c>BuildOrchestrator</c> throws before the orchestrator can run (template
/// load error, DI resolution error), the dispatcher's outer catch is the last
/// line of defense — any tasks already in <see cref="TaskState.InProgress"/>
/// from an earlier run must be transitioned to <see cref="TaskState.Failed"/>
/// before the swarm-level Failed so the recommendation surface sees the
/// correct board on the next recovery action.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SwarmDispatcherOrphanCleanupTests
{
    [TestMethod]
    public async Task TryRecordFailedTransitionAsync_WithInProgressTasksInRepo_FailsOrphansBeforeSwarmLevelFailed()
    {
        // Arrange — a repository that reports one InProgress + one Completed task,
        // and a transition service that records calls. The dispatcher helper must
        // visit the InProgress task and leave the Completed one alone.
        var swarmId = Guid.Parse("aaaaaaaa-cccc-1111-2222-bbbbbbbbbbbb");

        var repo = new Mock<ISwarmRepository>();
        repo.Setup(r => r.GetTasksAsync(swarmId)).ReturnsAsync(new List<TaskEntity>
        {
            new() { SwarmId = swarmId, Id = "t-orphan", State = nameof(TaskState.InProgress) },
            new() { SwarmId = swarmId, Id = "t-done", State = nameof(TaskState.Completed) },
        });

        var transitionService = new NoOpStateTransitionService();

        var services = new ServiceCollection();
        services.AddSingleton(repo.Object);
        services.AddSingleton<IStateTransitionService>(transitionService);
        using var provider = services.BuildServiceProvider();

        using var dispatcher = CreateDispatcher();

        // Act.
        await dispatcher.TryRecordFailedTransitionAsync(
            provider,
            swarmId,
            new InvalidOperationException("template load failed"));

        // Assert — exactly the orphan got a Failed task transition, with run_failed.
        transitionService.TaskCalls.Should().ContainSingle()
            .Which.Should().Match<(Guid SwarmId, string TaskId, TaskState ToState, string Reason, string? Actor, int Delta)>(c =>
                c.SwarmId == swarmId
                && c.TaskId == "t-orphan"
                && c.ToState == TaskState.Failed
                && c.Reason == TransitionReasons.RunFailed
                && c.Actor == "system"
                && c.Delta == 0);

        // AND the swarm-level transition happened — chronology guaranteed by
        // insertion order into the recorded lists (task first, swarm second).
        transitionService.SwarmCalls.Should().ContainSingle()
            .Which.ToState.Should().Be(SwarmInstanceState.Failed);
    }

    [TestMethod]
    public async Task TryRecordFailedTransitionAsync_WithNoTasksInRepo_StillWritesSwarmLevelFailed()
    {
        // Arrange — a fresh swarm that failed before tasks existed. Helper
        // should not query-then-crash; it just writes the swarm-level row.
        var swarmId = Guid.NewGuid();

        var repo = new Mock<ISwarmRepository>();
        repo.Setup(r => r.GetTasksAsync(swarmId)).ReturnsAsync(new List<TaskEntity>());

        var transitionService = new NoOpStateTransitionService();

        var services = new ServiceCollection();
        services.AddSingleton(repo.Object);
        services.AddSingleton<IStateTransitionService>(transitionService);
        using var provider = services.BuildServiceProvider();

        using var dispatcher = CreateDispatcher();

        // Act.
        await dispatcher.TryRecordFailedTransitionAsync(
            provider,
            swarmId,
            new InvalidOperationException("boom"));

        // Assert.
        transitionService.TaskCalls.Should().BeEmpty();
        transitionService.SwarmCalls.Should().ContainSingle()
            .Which.ToState.Should().Be(SwarmInstanceState.Failed);
    }

    [TestMethod]
    public async Task TryRecordFailedTransitionAsync_WithNoStateServiceInScope_IsANoOp()
    {
        // Arrange — old pre-state-machine deployments don't have IStateTransitionService
        // wired. Helper must return quietly, not throw.
        var services = new ServiceCollection();
        using var provider = services.BuildServiceProvider();

        using var dispatcher = CreateDispatcher();

        // Act.
        var act = async () => await dispatcher.TryRecordFailedTransitionAsync(
            provider,
            Guid.NewGuid(),
            new InvalidOperationException("boom"));

        // Assert.
        await act.Should().NotThrowAsync(
            "a missing state service must be a no-op, not a secondary crash");
    }

    private static SwarmDispatcherService CreateDispatcher()
    {
        var channel = Channel.CreateUnbounded<SwarmRequest>();
        var activeSwarms = new ConcurrentDictionary<Guid, SwarmExecution>();
        var scopeFactory = new Mock<IServiceScopeFactory>();
        var options = Options.Create(new SwarmOptions());
        var loggerFactory = NullLoggerFactory.Instance;
        var orchestratorFactory = new SwarmOrchestratorFactory(options, loggerFactory);

        return new SwarmDispatcherService(
            channel.Reader,
            activeSwarms,
            scopeFactory.Object,
            orchestratorFactory,
            Mock.Of<ISwarmObservationSink>(),
            options,
            loggerFactory);
    }
}
