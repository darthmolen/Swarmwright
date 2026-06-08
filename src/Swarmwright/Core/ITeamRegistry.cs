using Swarmwright.Models;
using Swarmwright.Models.Enums;

namespace Swarmwright.Core;

/// <summary>
/// Defines a registry for tracking agents within a swarm team.
/// </summary>
public interface ITeamRegistry
{
    /// <summary>
    /// Registers an agent in the team registry.
    /// </summary>
    /// <param name="agent">The agent information to register.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public Task RegisterAsync(AgentInfo agent);

    /// <summary>
    /// Gets an agent by name.
    /// </summary>
    /// <param name="name">The unique agent name.</param>
    /// <returns>The agent information, or <c>null</c> if not found.</returns>
    public Task<AgentInfo?> GetAgentAsync(string name);

    /// <summary>
    /// Gets all registered agents.
    /// </summary>
    /// <returns>A read-only list of all registered agents.</returns>
    public Task<IReadOnlyList<AgentInfo>> GetAllAsync();

    /// <summary>
    /// Updates the status of a registered agent.
    /// </summary>
    /// <param name="name">The unique agent name.</param>
    /// <param name="status">The new status to assign.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public Task UpdateStatusAsync(string name, AgentStatus status);

    /// <summary>
    /// Removes all agents from the registry.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public Task ClearAsync();
}
