namespace Swarmwright.Tools;

/// <summary>
/// Read-only per-swarm run context exposed to scoped <see cref="ICustomToolProvider"/>
/// implementations. A custom tool provider can inject this to discover which swarm
/// it is serving (<see cref="SwarmId"/>), where that swarm's work directory lives
/// (<see cref="WorkDirectory"/>), and any free-form key/value metadata
/// (<see cref="Context"/>) supplied at swarm-creation time. The dispatcher populates
/// the concrete holder before building the orchestrator, so a provider resolved from
/// the per-swarm scope observes the values for that run.
/// </summary>
public interface ISwarmRunContext
{
    /// <summary>Gets the unique identifier of the swarm this context serves.</summary>
    public Guid SwarmId { get; }

    /// <summary>Gets the per-swarm work directory where agents read and write file artifacts.</summary>
    public string WorkDirectory { get; }

    /// <summary>Gets the free-form key/value metadata supplied at swarm creation.</summary>
    public IReadOnlyDictionary<string, string> Context { get; }
}
