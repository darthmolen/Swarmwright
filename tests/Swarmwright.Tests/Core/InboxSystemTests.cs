using Swarmwright.Core;
using FluentAssertions;

namespace Swarmwright.Tests.Core;

[TestClass]
public class InboxSystemTests
{
    private static readonly string[] ExcludeAgentBArray = ["AgentB"];

    [TestMethod]
    public async Task RegisterAgent_CreatesEmptyInbox()
    {
        // Arrange
        var inbox = new InboxSystem();

        // Act
        inbox.RegisterAgent("AgentA");
        var messages = await inbox.PeekAsync("AgentA");

        // Assert
        messages.Should().BeEmpty();
    }

    [TestMethod]
    public async Task SendAsync_DeliversMessageToRecipient()
    {
        // Arrange
        var inbox = new InboxSystem();
        inbox.RegisterAgent("AgentA");
        inbox.RegisterAgent("AgentB");

        // Act
        await inbox.SendAsync("AgentA", "AgentB", "Hello from A");
        var messages = await inbox.ReceiveAsync("AgentB");

        // Assert
        messages.Should().HaveCount(1);
        messages[0].Sender.Should().Be("AgentA");
        messages[0].Recipient.Should().Be("AgentB");
        messages[0].Content.Should().Be("Hello from A");
    }

    [TestMethod]
    public async Task ReceiveAsync_DestructiveRead_ClearsMessages()
    {
        // Arrange
        var inbox = new InboxSystem();
        inbox.RegisterAgent("AgentA");
        inbox.RegisterAgent("AgentB");
        await inbox.SendAsync("AgentA", "AgentB", "Message 1");
        await inbox.SendAsync("AgentA", "AgentB", "Message 2");

        // Act
        var firstReceive = await inbox.ReceiveAsync("AgentB");
        var secondReceive = await inbox.ReceiveAsync("AgentB");

        // Assert
        firstReceive.Should().HaveCount(2);
        secondReceive.Should().BeEmpty();
    }

    [TestMethod]
    public async Task ReceiveAsync_EmptyInbox_ReturnsEmpty()
    {
        // Arrange
        var inbox = new InboxSystem();
        inbox.RegisterAgent("AgentA");

        // Act
        var messages = await inbox.ReceiveAsync("AgentA");

        // Assert
        messages.Should().BeEmpty();
    }

    [TestMethod]
    public async Task PeekAsync_NonDestructive_PreservesMessages()
    {
        // Arrange
        var inbox = new InboxSystem();
        inbox.RegisterAgent("AgentA");
        inbox.RegisterAgent("AgentB");
        await inbox.SendAsync("AgentA", "AgentB", "Hello");

        // Act
        var firstPeek = await inbox.PeekAsync("AgentB");
        var secondPeek = await inbox.PeekAsync("AgentB");

        // Assert
        firstPeek.Should().HaveCount(1);
        secondPeek.Should().HaveCount(1);
        firstPeek[0].Content.Should().Be("Hello");
        secondPeek[0].Content.Should().Be("Hello");
    }

    [TestMethod]
    public async Task BroadcastAsync_SendsToAllExceptSender()
    {
        // Arrange
        var inbox = new InboxSystem();
        inbox.RegisterAgent("AgentA");
        inbox.RegisterAgent("AgentB");
        inbox.RegisterAgent("AgentC");

        // Act
        await inbox.BroadcastAsync("AgentA", "Broadcast message");
        var messagesA = await inbox.ReceiveAsync("AgentA");
        var messagesB = await inbox.ReceiveAsync("AgentB");
        var messagesC = await inbox.ReceiveAsync("AgentC");

        // Assert
        messagesA.Should().BeEmpty();
        messagesB.Should().HaveCount(1);
        messagesB[0].Content.Should().Be("Broadcast message");
        messagesC.Should().HaveCount(1);
        messagesC[0].Content.Should().Be("Broadcast message");
    }

    [TestMethod]
    public async Task BroadcastAsync_WithExclusions_SkipsExcluded()
    {
        // Arrange
        var inbox = new InboxSystem();
        inbox.RegisterAgent("AgentA");
        inbox.RegisterAgent("AgentB");
        inbox.RegisterAgent("AgentC");

        // Act
        await inbox.BroadcastAsync("AgentA", "Broadcast message", exclude: ExcludeAgentBArray);
        var messagesB = await inbox.ReceiveAsync("AgentB");
        var messagesC = await inbox.ReceiveAsync("AgentC");

        // Assert
        messagesB.Should().BeEmpty();
        messagesC.Should().HaveCount(1);
        messagesC[0].Content.Should().Be("Broadcast message");
    }

    [TestMethod]
    public async Task SendAsync_UnregisteredRecipient_ThrowsInvalidOperationException()
    {
        // Arrange
        var inbox = new InboxSystem();
        inbox.RegisterAgent("AgentA");

        // Act
        Func<Task> act = async () => await inbox.SendAsync("AgentA", "UnregisteredAgent", "Hello");

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
