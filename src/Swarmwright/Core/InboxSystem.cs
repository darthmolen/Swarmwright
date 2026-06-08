using System.Collections.Concurrent;
using Swarmwright.Models;

namespace Swarmwright.Core;

/// <summary>
/// Provides an in-memory inbox messaging system for swarm agents.
/// </summary>
public class InboxSystem : IInboxSystem
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<InboxMessage>> inboxes = new();

    /// <inheritdoc/>
    public void RegisterAgent(string agentName)
    {
        this.inboxes.TryAdd(agentName, new ConcurrentQueue<InboxMessage>());
    }

    /// <inheritdoc/>
    public Task SendAsync(string sender, string recipient, string content)
    {
        if (!this.inboxes.TryGetValue(recipient, out var queue))
        {
            throw new InvalidOperationException($"Recipient '{recipient}' is not registered.");
        }

        var message = new InboxMessage
        {
            Sender = sender,
            Recipient = recipient,
            Content = content,
        };

        queue.Enqueue(message);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<InboxMessage>> ReceiveAsync(string agentName)
    {
        if (!this.inboxes.TryGetValue(agentName, out var queue))
        {
            return Task.FromResult<IReadOnlyList<InboxMessage>>(Array.Empty<InboxMessage>());
        }

        var messages = new List<InboxMessage>();
        while (queue.TryDequeue(out var message))
        {
            messages.Add(message);
        }

        return Task.FromResult<IReadOnlyList<InboxMessage>>(messages);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<InboxMessage>> PeekAsync(string agentName)
    {
        if (!this.inboxes.TryGetValue(agentName, out var queue))
        {
            return Task.FromResult<IReadOnlyList<InboxMessage>>(Array.Empty<InboxMessage>());
        }

        return Task.FromResult<IReadOnlyList<InboxMessage>>(queue.ToArray());
    }

    /// <inheritdoc/>
    public async Task BroadcastAsync(string sender, string content, IEnumerable<string>? exclude = null)
    {
        var excludeSet = exclude != null
            ? new HashSet<string>(exclude)
            : [];

        foreach (var agentName in this.inboxes.Keys)
        {
            if (agentName != sender && !excludeSet.Contains(agentName))
            {
                await this.SendAsync(sender, agentName, content);
            }
        }
    }

    /// <inheritdoc/>
    public Task ClearAsync()
    {
        foreach (var queue in this.inboxes.Values)
        {
            queue.Clear();
        }

        return Task.CompletedTask;
    }
}
