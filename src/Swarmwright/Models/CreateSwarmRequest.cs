namespace Swarmwright.Models;

/// <summary>
/// Request model for creating a new swarm.
/// </summary>
/// <param name="Goal">The user-provided goal for the swarm.</param>
/// <param name="TemplateKey">The optional template key used to configure the swarm.</param>
/// <param name="Context">
/// Optional free-form key/value context exposed to scoped custom tool providers
/// via <c>ISwarmRunContext</c> and persisted across evict/resume.
/// </param>
public record CreateSwarmRequest(
    string Goal,
    string? TemplateKey,
    Dictionary<string, string>? Context = null);
