using Swarmwright.Models;

namespace Swarmwright.Core;

/// <summary>
/// Defines the contract for a swarm agent inbox messaging system.
/// </summary>
public interface IInboxSystem
{
    /// <summary>
    /// Registers an agent and creates an empty inbox for it.
    /// </summary>
    /// <param name="agentName">The name of the agent to register.</param>
    public void RegisterAgent(string agentName);

    /// <summary>
    /// Sends a message from one agent to another.
    /// </summary>
    /// <param name="sender">The name of the sending agent.</param>
    /// <param name="recipient">The name of the recipient agent.</param>
    /// <param name="content">The message content.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task SendAsync(string sender, string recipient, string content);

    /// <summary>
    /// Receives and removes all messages from the specified agent's inbox.
    /// </summary>
    /// <param name="agentName">The name of the agent whose messages to receive.</param>
    /// <returns>A read-only list of inbox messages.</returns>
    public Task<IReadOnlyList<InboxMessage>> ReceiveAsync(string agentName);

    /// <summary>
    /// Peeks at all messages in the specified agent's inbox without removing them.
    /// </summary>
    /// <param name="agentName">The name of the agent whose messages to peek at.</param>
    /// <returns>A read-only list of inbox messages.</returns>
    public Task<IReadOnlyList<InboxMessage>> PeekAsync(string agentName);

    /// <summary>
    /// Broadcasts a message to all registered agents except the sender and any excluded agents.
    /// </summary>
    /// <param name="sender">The name of the sending agent.</param>
    /// <param name="content">The message content.</param>
    /// <param name="exclude">Optional collection of agent names to exclude from the broadcast.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task BroadcastAsync(string sender, string content, IEnumerable<string>? exclude = null);

    /// <summary>
    /// Clears all messages from all agent inboxes.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task ClearAsync();
}
