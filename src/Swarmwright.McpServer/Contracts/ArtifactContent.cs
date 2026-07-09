namespace Swarmwright.McpServer.Contracts;

/// <summary>
/// Content payload returned by <c>read_artifact</c>.
/// </summary>
/// <param name="Path">Path relative to the swarm's work directory (forward-slash separated).</param>
/// <param name="Content">Text contents of the file. Truncated if the file exceeded the configured size limit.</param>
/// <param name="SizeBytes">Total file size in bytes on disk.</param>
/// <param name="Truncated">A value indicating whether the returned content was truncated.</param>
public sealed record ArtifactContent(
    string Path,
    string Content,
    long SizeBytes,
    bool Truncated);
