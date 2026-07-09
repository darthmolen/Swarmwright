using Swarmwright.Core;
using Swarmwright.Models;
using Swarmwright.Models.Enums;
using FluentAssertions;

namespace Swarmwright.Tests.Core;

[TestClass]
public class TeamRegistryTests
{
    [TestMethod]
    public async Task RegisterAsync_StoresAgent()
    {
        // Arrange
        var registry = new TeamRegistry();
        var agent = new AgentInfo { Name = "agent-1", Role = "Researcher" };

        // Act
        await registry.RegisterAsync(agent);
        var result = await registry.GetAgentAsync("agent-1");

        // Assert
        result.Should().NotBeNull();
        result!.Name.Should().Be("agent-1");
        result.Role.Should().Be("Researcher");
    }

    [TestMethod]
    public async Task GetAllAsync_ReturnsAll()
    {
        // Arrange
        var registry = new TeamRegistry();
        await registry.RegisterAsync(new AgentInfo { Name = "a1" });
        await registry.RegisterAsync(new AgentInfo { Name = "a2" });
        await registry.RegisterAsync(new AgentInfo { Name = "a3" });

        // Act
        var all = await registry.GetAllAsync();

        // Assert
        all.Should().HaveCount(3);
    }

    [TestMethod]
    public async Task UpdateStatusAsync_ChangesStatus()
    {
        // Arrange
        var registry = new TeamRegistry();
        var agent = new AgentInfo { Name = "worker", Status = AgentStatus.Idle };
        await registry.RegisterAsync(agent);

        // Act
        await registry.UpdateStatusAsync("worker", AgentStatus.Working);
        var result = await registry.GetAgentAsync("worker");

        // Assert
        result.Should().NotBeNull();
        result!.Status.Should().Be(AgentStatus.Working);
    }

    [TestMethod]
    public async Task GetAgentAsync_UnknownAgent_ReturnsNull()
    {
        // Arrange
        var registry = new TeamRegistry();

        // Act
        var result = await registry.GetAgentAsync("does-not-exist");

        // Assert
        result.Should().BeNull();
    }
}
