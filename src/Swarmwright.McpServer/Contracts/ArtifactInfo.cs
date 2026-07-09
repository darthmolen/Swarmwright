namespace Swarmwright.McpServer.Contracts;

/// <summary>
/// File descriptor for a swarm work-directory artifact.
/// </summary>
/// <param name="Name">File name.</param>
/// <param name="Path">Path relative to the swarm's work directory (forward-slash separated).</param>
/// <param name="SizeBytes">File size in bytes.</param>
/// <param name="ModifiedAt">UTC last-modified timestamp.</param>
public sealed record ArtifactInfo(
    string Name,
    string Path,
    long SizeBytes,
    DateTime ModifiedAt);
