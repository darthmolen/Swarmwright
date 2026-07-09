using System.Diagnostics;
using Swarmwright.Configuration;
using Swarmwright.Core;
using Swarmwright.Database.Repositories;
using Swarmwright.Events;
using Swarmwright.Orchestration;
using Swarmwright.Services;
using Swarmwright.Telemetry;
using Swarmwright.Templates;
using Swarmwright.Tests.Hosting.StateMachine;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Swarmwright.Tests.Orchestration;

/// <summary>
/// Telemetry-specific tests for <see cref="SwarmOrchestrator"/>. Verifies
/// that the orchestrator emits a root <c>swarm.run</c> activity on the
/// <see cref="AgentTelemetry.SwarmActivitySourceName"/> source, tagged with
/// the canonical swarm identifier. Downstream hosts (and the
/// OpenTelemetry MCP server) rely on this tag to pivot queries by swarm.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SwarmOrchestratorTelemetryTests : IDisposable
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

    private static ActivitySamplingResult SampleAllData(
        ref ActivityCreationOptions<ActivityContext> options) =>
        ActivitySamplingResult.AllData;

    /// <summary>
    /// Verifies that <see cref="SwarmOrchestrator.RunAsync(Guid, string, CancellationToken)"/>
    /// starts a parent activity named <see cref="AgentTelemetry.SwarmRunActivityName"/>
    /// on the <see cref="AgentTelemetry.SwarmActivitySourceName"/> source tagged with
    /// the caller-supplied swarm identifier, even when the orchestrator itself fails
    /// downstream.
    /// </summary>
    [TestMethod]
    public async Task RunAsync_StartsSwarmRunActivity_TaggedWithSwarmId()
    {
        // Arrange — subscribe to the Swarm ActivitySource before starting the run
        // so we capture the span even if the orchestrator later throws.
        var stoppedActivities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == AgentTelemetry.SwarmActivitySourceName,
            Sample = SampleAllData,
            ActivityStopped = stoppedActivities.Add,
        };
        ActivitySource.AddActivityListener(listener);

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

        // Make the leader throw so RunAsync exits quickly after the activity
        // has been started — we only need the span, not a full run.
        this.mockLeaderClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Short-circuit for test."));

        var knownId = Guid.Parse("aaaaaaaa-1111-2222-3333-444444444444");

        // Act — we expect a throw; the span should still have stopped cleanly.
        Func<Task> act = async () => await orchestrator.RunAsync(knownId, "Observe me.");
        await act.Should().ThrowAsync<InvalidOperationException>();

        // Assert — exactly one swarm.run activity with swarm.id tag matching the input.
        var swarmRun = stoppedActivities.Should().ContainSingle(
            a => a.OperationName == AgentTelemetry.SwarmRunActivityName,
            "the orchestrator must start exactly one root swarm.run activity").Subject;

        swarmRun.Source.Name.Should().Be(
            AgentTelemetry.SwarmActivitySourceName,
            "the activity must originate on the Swarmwright swarm ActivitySource.");
        swarmRun.GetTagItem(AgentTelemetry.SwarmIdTagName).Should().Be(
            knownId.ToString(),
            "swarm.id tag lets the observability MCP tool pivot queries to a single swarm.");
    }

    /// <summary>
    /// Verifies that when a template is provided, the swarm-run activity
    /// carries the template key as a tag so queries can filter by template.
    /// </summary>
    [TestMethod]
    public async Task RunAsync_WhenTemplateProvided_TagsActivityWithTemplateKey()
    {
        // Arrange
        var stoppedActivities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == AgentTelemetry.SwarmActivitySourceName,
            Sample = SampleAllData,
            ActivityStopped = stoppedActivities.Add,
        };
        ActivitySource.AddActivityListener(listener);

        var template = new LoadedTemplate
        {
            Key = "deep-research",
        };

        var orchestrator = new SwarmOrchestrator(
            this.mockLeaderClient.Object,
            _ => this.mockWorkerClient.Object,
            this.eventBus,
            new Swarmwright.Events.AgUI.SwarmEventAdapter(),
            this.swarmService,
            new NoOpStateTransitionService(),
            this.options,
            template: template,
            workDirectory: Path.GetTempPath(),
            httpClient: this.httpClient,
            loggerFactory: NullLoggerFactory.Instance);

        this.mockLeaderClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Short-circuit for test."));

        // Act
        Func<Task> act = async () =>
            await orchestrator.RunAsync(Guid.NewGuid(), "Template-tagged goal.");
        await act.Should().ThrowAsync<InvalidOperationException>();

        // Assert
        var swarmRun = stoppedActivities.Should().ContainSingle(
            a => a.OperationName == AgentTelemetry.SwarmRunActivityName).Subject;
        swarmRun.GetTagItem(AgentTelemetry.SwarmTemplateTagName).Should().Be(
            "deep-research",
            "the template key tag lets queries scope to a swarm template.");
    }
}
