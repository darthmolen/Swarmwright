using Swarmwright.Configuration;
using Swarmwright.Events;
using Swarmwright.Events.AgUI;
using Swarmwright.Orchestration;
using Swarmwright.Services;
using Swarmwright.Tests.Hosting.StateMachine;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Moq;

namespace Swarmwright.Tests.Orchestration;

/// <summary>
/// Fix B coverage: when an already-running orchestrator is signalled out of
/// suspend wait by an external recovery action, its next round must see the
/// post-handler DB state. Otherwise the in-memory TaskBoard stays frozen at
/// the state captured during the initial <c>LoadAsync</c>, and the orphan
/// reset (or any task-state change the handler wrote while the orchestrator
/// was asleep) never reaches <c>GetRunnableTasksAsync</c>. The orchestrator
/// re-reads task state from the database on suspend-wake to close that
/// window — this test verifies the contract at the reload seam.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SwarmOrchestratorReloadOnResumeTests : IDisposable
{
    private Mock<ISwarmService> mockSwarmService = null!;
    private NoOpStateTransitionService transitionService = null!;
    private SwarmEventBus eventBus = null!;
    private SwarmEventAdapter agUiAdapter = null!;
    private HttpClient httpClient = null!;

    [TestInitialize]
    public void TestInitialize()
    {
        this.mockSwarmService = new Mock<ISwarmService>();
        this.eventBus = new SwarmEventBus();
        this.agUiAdapter = new SwarmEventAdapter();
        this.transitionService = new NoOpStateTransitionService(this.agUiAdapter);
        this.httpClient = new HttpClient();
    }

    public void Dispose()
    {
        this.httpClient?.Dispose();
    }

    [TestMethod]
    public async Task ReloadFromDatabaseAsync_InvokesSwarmServiceLoadAsync()
    {
        this.mockSwarmService
            .Setup(s => s.LoadAsync(It.IsAny<Guid>()))
            .Returns(Task.CompletedTask)
            .Verifiable();

        var orchestrator = this.CreateOrchestrator();

        await orchestrator.ReloadFromDatabaseAsync();

        this.mockSwarmService.Verify(
            s => s.LoadAsync(It.IsAny<Guid>()),
            Times.Once,
            "the reload seam must delegate to ISwarmService.LoadAsync so in-memory caches are refreshed from the DB");
    }

    private SwarmOrchestrator CreateOrchestrator()
    {
        return new SwarmOrchestrator(
            new Mock<IChatClient>().Object,
            _ => new Mock<IChatClient>().Object,
            this.eventBus,
            this.agUiAdapter,
            this.mockSwarmService.Object,
            this.transitionService,
            new SwarmOptions { MaxRounds = 1 },
            template: null,
            workDirectory: Path.GetTempPath(),
            httpClient: this.httpClient);
    }
}
