namespace Swarmwright.McpServer.Contracts;

/// <summary>
/// Descriptor for a swarm template available on disk.
/// </summary>
/// <param name="Key">The template key (directory name).</param>
/// <param name="Name">The human-readable template name.</param>
/// <param name="Description">The template description.</param>
public sealed record SwarmTemplateInfo(
    string Key,
    string Name,
    string Description);
