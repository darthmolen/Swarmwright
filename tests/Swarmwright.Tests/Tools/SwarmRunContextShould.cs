using Swarmwright.Tools;
using FluentAssertions;

namespace Swarmwright.Tests.Tools;

/// <summary>
/// Unit tests for <see cref="SwarmRunContext"/> — the scoped holder that
/// exposes the per-swarm <see cref="ISwarmRunContext"/> to custom tool
/// providers.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SwarmRunContextShould
{
    /// <summary>
    /// Verifies that <c>Initialize</c> populates the read-only getters exposed
    /// through <see cref="ISwarmRunContext"/>.
    /// </summary>
    [TestMethod]
    public void ExposeValuesPassedToInitialize()
    {
        // Arrange
        var swarmId = Guid.NewGuid();
        var workDir = Path.Combine(Path.GetTempPath(), swarmId.ToString());
        var context = new Dictionary<string, string>
        {
            ["sourceRoot"] = "/clones/pr-42",
        };

        var holder = new SwarmRunContext();

        // Act
        holder.Initialize(swarmId, workDir, context);

        // Assert — the holder satisfies the public read-only contract and
        // surfaces the values passed to Initialize.
        holder.Should().BeAssignableTo<ISwarmRunContext>();
        holder.SwarmId.Should().Be(swarmId);
        holder.WorkDirectory.Should().Be(workDir);
        holder.Context.Should().ContainKey("sourceRoot")
            .WhoseValue.Should().Be("/clones/pr-42");
    }
}
