using Swarmwright.Configuration;
using Swarmwright.Core;
using Swarmwright.Database.Repositories;
using Swarmwright.Events;
using Swarmwright.Models.Enums;
using Swarmwright.Orchestration;
using Swarmwright.Services;
using Swarmwright.Tests.Hosting.StateMachine;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Swarmwright.Tests.Orchestration;

/// <summary>
/// Logging-specific tests for <see cref="SwarmOrchestrator"/>.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SwarmOrchestratorLoggingTests : IDisposable
{
    private Mock<IChatClient> mockLeaderClient = null!;
    private Mock<IChatClient> mockWorkerClient = null!;
    private SwarmService swarmService = null!;
    private SwarmEventBus eventBus = null!;
    private SwarmOptions options = null!;
    private HttpClient httpClient = null!;

    /// <summary>
    /// Initializes test dependencies before each test.
    /// </summary>
    [TestInitialize]
    public void TestInitialize()
    {
        this.mockLeaderClient = new Mock<IChatClient>();
        this.mockWorkerClient = new Mock<IChatClient>();
        this.swarmService = new SwarmService(
            new InboxSystem(),
            new TeamRegistry(),
            new Mock<ISwarmRepository>().Object);
        this.eventBus = new SwarmEventBus();
        this.httpClient = new HttpClient();
        this.options = new SwarmOptions
        {
            MaxRounds = 3,
            SuspendTimeoutSeconds = 5,
        };
    }

    /// <summary>
    /// Disposes managed resources.
    /// </summary>
    public void Dispose()
    {
        this.httpClient?.Dispose();
    }

    /// <summary>
    /// Verifies that the constructor accepts an ILoggerFactory without throwing.
    /// </summary>
    [TestMethod]
    public void Constructor_AcceptsLoggerFactory()
    {
        // Arrange & Act
        var orchestrator = new SwarmOrchestrator(
            this.mockLeaderClient.Object,
            _ => this.mockWorkerClient.Object,
            this.eventBus,
            new Swarmwright.Events.AgUI.SwarmEventAdapter(),
            this.swarmService,
            new NoOpStateTransitionService(),
            this.options,
            template: null,
            workDirectory: Path.GetTempPath(),
            httpClient: this.httpClient,
            loggerFactory: NullLoggerFactory.Instance);

        // Assert
        orchestrator.Should().NotBeNull();
    }

    /// <summary>
    /// Verifies that when an unhandled exception occurs, the phase is set to Failed.
    /// </summary>
    [TestMethod]
    public async Task RunAsync_OnUnhandledException_SetsFailedPhase()
    {
        // Arrange
        var transitionService = new NoOpStateTransitionService();
        var orchestrator = new SwarmOrchestrator(
            this.mockLeaderClient.Object,
            _ => this.mockWorkerClient.Object,
            this.eventBus,
            new Swarmwright.Events.AgUI.SwarmEventAdapter(),
            this.swarmService,
            transitionService,
            this.options,
            template: null,
            workDirectory: Path.GetTempPath(),
            httpClient: this.httpClient,
            loggerFactory: NullLoggerFactory.Instance);

        this.mockLeaderClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("LLM call failed."));

        // Act
        Func<Task> act = async () => await orchestrator.RunAsync(Guid.NewGuid(), "Some goal.");

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("LLM call failed.");
        transitionService.SwarmCalls.Should()
            .Contain(c => c.ToState == Swarmwright.Models.Enums.SwarmInstanceState.Failed);
    }
}
