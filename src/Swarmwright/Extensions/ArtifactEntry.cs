namespace Swarmwright.Extensions;

/// <summary>
/// A single artifact entry returned by the list-artifacts endpoint.
/// </summary>
/// <param name="Name">The file's leaf name.</param>
/// <param name="Path">The file's path relative to the swarm work directory, using forward slashes.</param>
/// <param name="Size">The file's size in bytes.</param>
internal sealed record ArtifactEntry(string Name, string Path, long Size);
