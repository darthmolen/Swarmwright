using System.Collections.Concurrent;
using System.Threading.Channels;
using Swarmwright.Configuration;
using Swarmwright.Database.Repositories;
using Swarmwright.Events;
using Swarmwright.Hosting;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Swarmwright.Tests.Hosting;

/// <summary>
/// Unit tests for <see cref="SwarmManager"/>.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SwarmManagerTests
{
    private readonly Channel<SwarmRequest> channel = Channel.CreateUnbounded<SwarmRequest>();
    private readonly ConcurrentDictionary<Guid, SwarmExecution> activeSwarms = new();
    private string workBase = null!;

    /// <summary>Creates a unique temp work base for each test.</summary>
    [TestInitialize]
    public void Setup()
    {
        this.workBase = Path.Combine(Path.GetTempPath(), "swarm-mgr-test-" + Guid.NewGuid().ToString("N"));
    }

    /// <summary>Cleans up the per-test work base.</summary>
    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(this.workBase))
        {
            Directory.Delete(this.workBase, recursive: true);
        }
    }

    /// <summary>
    /// Verifies that CreateSwarmAsync returns a valid GUID.
    /// </summary>
    [TestMethod]
    public async Task CreateSwarmAsync_ReturnsGuid()
    {
        // Arrange
        var manager = this.CreateManager();

        // Act
        var swarmId = await manager.CreateSwarmAsync("Build a widget");

        // Assert
        swarmId.Should().NotBeEmpty();
    }

    /// <summary>
    /// Verifies that CreateSwarmAsync writes a request to the channel.
    /// </summary>
    [TestMethod]
    public async Task CreateSwarmAsync_WritesToChannel()
    {
        // Arrange
        var manager = this.CreateManager();

        // Act
        var swarmId = await manager.CreateSwarmAsync("Build a widget", "deep-research");

        // Assert
        this.channel.Reader.TryRead(out var request).Should().BeTrue();
        request!.SwarmId.Should().Be(swarmId);
        request.Goal.Should().Be("Build a widget");
        request.TemplateKey.Should().Be("deep-research");
    }

    /// <summary>
    /// Verifies that GetSwarm returns the execution for a known swarm ID.
    /// </summary>
    [TestMethod]
    public async Task GetSwarm_ReturnsExecution()
    {
        // Arrange
        var manager = this.CreateManager();
        var swarmId = await manager.CreateSwarmAsync("Build a widget");

        // Act
        var execution = manager.GetSwarm(swarmId);

        // Assert
        execution.Should().NotBeNull();
        execution!.SwarmId.Should().Be(swarmId);
        execution.Goal.Should().Be("Build a widget");
    }

    /// <summary>
    /// Verifies that GetSwarm returns null for an unknown swarm ID.
    /// </summary>
    [TestMethod]
    public void GetSwarm_UnknownId_ReturnsNull()
    {
        // Arrange
        var manager = this.CreateManager();

        // Act
        var execution = manager.GetSwarm(Guid.NewGuid());

        // Assert
        execution.Should().BeNull();
    }

    /// <summary>
    /// Verifies that ListActiveSwarms returns all active swarm executions.
    /// </summary>
    [TestMethod]
    public async Task ListActiveSwarms_ReturnsAll()
    {
        // Arrange
        var manager = this.CreateManager();
        await manager.CreateSwarmAsync("Goal 1");
        await manager.CreateSwarmAsync("Goal 2");

        // Act
        var swarms = manager.ListActiveSwarms();

        // Assert
        swarms.Should().HaveCount(2);
    }

    /// <summary>
    /// Verifies that CancelSwarmAsync cancels the execution CTS.
    /// </summary>
    [TestMethod]
    public async Task CancelSwarmAsync_CancelsExecution()
    {
        // Arrange
        var manager = this.CreateManager();
        var swarmId = await manager.CreateSwarmAsync("Build a widget");
        var execution = manager.GetSwarm(swarmId);

        // Act
        await manager.CancelSwarmAsync(swarmId);

        // Assert
        execution!.Cts.IsCancellationRequested.Should().BeTrue();
    }

    // -----------------------------------------------------------------------
    // Phase 2 — per-swarm work directory
    // -----------------------------------------------------------------------

    [TestMethod]
    public async Task CreateSwarmAsync_CreatesWorkDirectory()
    {
        var manager = this.CreateManager();

        var swarmId = await manager.CreateSwarmAsync("Build a widget");

        var expectedPath = Path.Combine(this.workBase, swarmId.ToString());
        Directory.Exists(expectedPath).Should().BeTrue();
    }

    [TestMethod]
    public async Task CreateSwarmAsync_PopulatesWorkDirectoryOnExecution()
    {
        var manager = this.CreateManager();

        var swarmId = await manager.CreateSwarmAsync("Build a widget");
        var execution = manager.GetSwarm(swarmId);

        execution!.WorkDirectory.Should().EndWith(swarmId.ToString());
        execution.WorkDirectory.Should().StartWith(this.workBase);
    }

    [TestMethod]
    public async Task CreateSwarmAsync_DefaultsWorkBasePath_WhenEmpty()
    {
        var options = Options.Create(new SwarmOptions { WorkBasePath = string.Empty });
        var manager = new SwarmManager(this.channel.Writer, this.activeSwarms, options, Mock.Of<ISwarmRepository>(), Mock.Of<ISwarmObservationSink>(), NullLogger<SwarmManager>.Instance);

        var swarmId = await manager.CreateSwarmAsync("Build a widget");
        var execution = manager.GetSwarm(swarmId);

        try
        {
            // Default falls back to a sub-directory under the OS temp path.
            execution!.WorkDirectory.Should().Contain("swarm-work");
            Directory.Exists(execution.WorkDirectory).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(execution!.WorkDirectory))
            {
                Directory.Delete(execution.WorkDirectory, recursive: true);
            }
        }
    }

    /// <summary>
    /// Verifies that a context bag supplied to CreateSwarmAsync is recorded on
    /// the tracked execution.
    /// </summary>
    [TestMethod]
    public async Task CreateSwarmAsync_RecordsContextOnExecution()
    {
        var manager = this.CreateManager();
        var context = new Dictionary<string, string>
        {
            ["sourceRoot"] = "/clones/pr-7",
        };

        var swarmId = await manager.CreateSwarmAsync("Build a widget", templateKey: null, context: context);
        var execution = manager.GetSwarm(swarmId);

        execution!.Context.Should().ContainKey("sourceRoot")
            .WhoseValue.Should().Be("/clones/pr-7");
    }

    /// <summary>
    /// Verifies that omitting the context bag yields an empty (non-null)
    /// context on the execution.
    /// </summary>
    [TestMethod]
    public async Task CreateSwarmAsync_DefaultsToEmptyContext_WhenOmitted()
    {
        var manager = this.CreateManager();

        var swarmId = await manager.CreateSwarmAsync("Build a widget");
        var execution = manager.GetSwarm(swarmId);

        execution!.Context.Should().NotBeNull();
        execution.Context.Should().BeEmpty();
    }

    private SwarmManager CreateManager()
    {
        var options = Options.Create(new SwarmOptions { WorkBasePath = this.workBase });
        return new SwarmManager(this.channel.Writer, this.activeSwarms, options, Mock.Of<ISwarmRepository>(), Mock.Of<ISwarmObservationSink>(), NullLogger<SwarmManager>.Instance);
    }
}
