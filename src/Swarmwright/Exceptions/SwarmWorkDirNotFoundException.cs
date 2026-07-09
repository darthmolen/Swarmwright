namespace Swarmwright.Exceptions;

/// <summary>
/// Thrown when a swarm's work directory cannot be found on disk. Typed exception
/// for NuGet consumers to differentiate from application-level 404 errors.
/// </summary>
public sealed class SwarmWorkDirNotFoundException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SwarmWorkDirNotFoundException"/> class.
    /// </summary>
    /// <param name="swarmId">The swarm identifier whose work directory was not found.</param>
    /// <param name="expectedPath">The path that was expected to exist.</param>
    public SwarmWorkDirNotFoundException(Guid swarmId, string expectedPath)
        : base($"Swarm work directory not found for swarm {swarmId} at path '{expectedPath}'.")
    {
        this.SwarmId = swarmId;
        this.ExpectedPath = expectedPath;
    }

    /// <summary>
    /// Gets the swarm identifier whose work directory was not found.
    /// </summary>
    public Guid SwarmId { get; }

    /// <summary>
    /// Gets the path that was expected to exist.
    /// </summary>
    public string ExpectedPath { get; }
}
