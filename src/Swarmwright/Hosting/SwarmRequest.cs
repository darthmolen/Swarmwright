namespace Swarmwright.Hosting;

/// <summary>
/// Represents a request to create and run a swarm, dispatched via channel.
/// </summary>
/// <param name="SwarmId">The unique identifier for the swarm.</param>
/// <param name="Goal">The user-provided goal for the swarm.</param>
/// <param name="TemplateKey">The optional template key used to configure the swarm.</param>
public record SwarmRequest(Guid SwarmId, string Goal, string? TemplateKey);
