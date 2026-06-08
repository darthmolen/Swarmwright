namespace Swarmwright.Archival;

/// <summary>
/// Promotes a completed swarm-run work directory to durable storage. The
/// storage abstraction the archival notification consumer delegates to so the
/// blob logic stays unit-testable and swappable.
/// </summary>
public interface ISwarmRunArchiver
{
    /// <summary>
    /// Archives the run described by <paramref name="context"/> — copies its
    /// work-directory tree to durable storage and writes the run manifest.
    /// </summary>
    /// <param name="context">The terminal-run metadata and work-directory pointer.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous archive.</returns>
    public Task ArchiveAsync(SwarmRunArchiveContext context, CancellationToken cancellationToken);
}
