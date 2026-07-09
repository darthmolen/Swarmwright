using System.Collections.Concurrent;
using System.Threading.Channels;
using Swarmwright.Configuration;
using Swarmwright.Database.Repositories;
using Swarmwright.Events;
using Swarmwright.Events.AgUI;
using Swarmwright.Hosting;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Swarmwright.Tests.Hosting;

/// <summary>
/// Unit tests for <see cref="SwarmManager.GetWorkDirectory"/>.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SwarmManagerGetWorkDirectoryTests : IDisposable
{
    private readonly string testDir;
    private readonly ConcurrentDictionary<Guid, SwarmExecution> activeSwarms;
    private readonly SwarmManager manager;

    /// <summary>
    /// Initializes a new instance of the <see cref="SwarmManagerGetWorkDirectoryTests"/> class.
    /// </summary>
    public SwarmManagerGetWorkDirectoryTests()
    {
        this.testDir = Path.Combine(Path.GetTempPath(), "workdir-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.testDir);

        var channel = Channel.CreateUnbounded<SwarmRequest>();
        this.activeSwarms = new ConcurrentDictionary<Guid, SwarmExecution>();

        this.manager = new SwarmManager(
            channel.Writer,
            this.activeSwarms,
            Options.Create(new SwarmOptions { WorkBasePath = this.testDir }),
            Mock.Of<ISwarmRepository>(),
            Mock.Of<ISwarmObservationSink>(),
            NullLogger<SwarmManager>.Instance);
    }

    /// <summary>
    /// Disposes managed resources.
    /// </summary>
    public void Dispose()
    {
        if (Directory.Exists(this.testDir))
        {
            Directory.Delete(this.testDir, recursive: true);
        }
    }

    /// <summary>
    /// Verifies that GetWorkDirectory returns the in-memory execution's work directory.
    /// </summary>
    [TestMethod]
    public void GetWorkDirectory_WhenInMemory_ReturnsExecutionWorkDirectory()
    {
        // Arrange
        var swarmId = Guid.NewGuid();
        var expectedPath = Path.Combine(this.testDir, "custom-path");
        this.activeSwarms[swarmId] = new SwarmExecution
        {
            SwarmId = swarmId,
            Goal = "test",
            Cts = new CancellationTokenSource(),
            EventBus = new SwarmEventBus(),
            AgUiAdapter = new SwarmEventAdapter(),
            WorkDirectory = expectedPath,
        };

        // Act
        var result = this.manager.GetWorkDirectory(swarmId);

        // Assert
        result.Should().Be(expectedPath);
    }

    /// <summary>
    /// Verifies that GetWorkDirectory constructs path from options when swarm is evicted
    /// and the directory exists on disk.
    /// </summary>
    [TestMethod]
    public void GetWorkDirectory_WhenEvictedButDirExists_ReturnsConstructedPath()
    {
        // Arrange
        var swarmId = Guid.NewGuid();
        var expectedPath = Path.Combine(this.testDir, swarmId.ToString());
        Directory.CreateDirectory(expectedPath);

        // Act
        var result = this.manager.GetWorkDirectory(swarmId);

        // Assert
        result.Should().Be(expectedPath);
    }

    /// <summary>
    /// Verifies that GetWorkDirectory returns null when the directory does not exist.
    /// </summary>
    [TestMethod]
    public void GetWorkDirectory_WhenDirDoesNotExist_ReturnsNull()
    {
        // Arrange
        var swarmId = Guid.NewGuid();

        // Act
        var result = this.manager.GetWorkDirectory(swarmId);

        // Assert
        result.Should().BeNull();
    }
}
