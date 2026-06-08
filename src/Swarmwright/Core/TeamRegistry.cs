using System.Collections.Concurrent;

using Swarmwright.Models;
using Swarmwright.Models.Enums;

namespace Swarmwright.Core;

/// <summary>
/// In-memory registry for tracking agents within a swarm team.
/// </summary>
public class TeamRegistry : ITeamRegistry
{
    private readonly ConcurrentDictionary<string, AgentInfo> agents = new();

    /// <inheritdoc/>
    public Task RegisterAsync(AgentInfo agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        this.agents[agent.Name] = agent;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<AgentInfo?> GetAgentAsync(string name)
    {
        this.agents.TryGetValue(name, out var agent);
        return Task.FromResult(agent);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<AgentInfo>> GetAllAsync()
    {
        IReadOnlyList<AgentInfo> result = this.agents.Values.ToList().AsReadOnly();
        return Task.FromResult(result);
    }

    /// <inheritdoc/>
    public Task UpdateStatusAsync(string name, AgentStatus status)
    {
        if (this.agents.TryGetValue(name, out var agent))
        {
            agent.Status = status;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task ClearAsync()
    {
        this.agents.Clear();
        return Task.CompletedTask;
    }
}
