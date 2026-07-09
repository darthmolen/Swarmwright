using Swarmwright.Exceptions;
using FluentAssertions;

namespace Swarmwright.Tests.Exceptions;

/// <summary>
/// Unit tests for <see cref="SwarmWorkDirNotFoundException"/>.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SwarmWorkDirNotFoundExceptionTests
{
    /// <summary>
    /// Verifies that the exception carries the swarm ID and expected path.
    /// </summary>
    [TestMethod]
    public void Constructor_SetsSwarmIdAndExpectedPath()
    {
        // Arrange
        var swarmId = Guid.NewGuid();
        var expectedPath = "/tmp/swarm-work/" + swarmId;

        // Act
        var ex = new SwarmWorkDirNotFoundException(swarmId, expectedPath);

        // Assert
        ex.SwarmId.Should().Be(swarmId);
        ex.ExpectedPath.Should().Be(expectedPath);
        ex.Message.Should().Contain(swarmId.ToString());
        ex.Message.Should().Contain(expectedPath);
    }
}
