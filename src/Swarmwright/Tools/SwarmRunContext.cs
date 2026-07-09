namespace Swarmwright.Tools;

/// <summary>
/// Scoped, mutable holder backing <see cref="ISwarmRunContext"/>. Registered as a
/// scoped service alongside the interface so both resolve to the same per-swarm
/// instance. The public <see cref="ISwarmRunContext"/> surface is getter-only;
/// only the dispatcher — holding the concrete type — populates it via
/// <see cref="Initialize"/> before the orchestrator (and therefore any custom tool
/// provider) is built.
/// </summary>
internal sealed class SwarmRunContext : ISwarmRunContext
{
    /// <inheritdoc/>
    public Guid SwarmId { get; private set; }

    /// <inheritdoc/>
    public string WorkDirectory { get; private set; } = string.Empty;

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, string> Context { get; private set; }
        = new Dictionary<string, string>();

    /// <summary>
    /// Populates the holder for the swarm run. Called once by the dispatcher
    /// before the orchestrator is built.
    /// </summary>
    /// <param name="swarmId">The swarm identifier this context serves.</param>
    /// <param name="workDirectory">The per-swarm work directory.</param>
    /// <param name="context">The free-form key/value metadata for the run.</param>
    internal void Initialize(Guid swarmId, string workDirectory, IReadOnlyDictionary<string, string> context)
    {
        ArgumentNullException.ThrowIfNull(context);
        this.SwarmId = swarmId;
        this.WorkDirectory = workDirectory;
        this.Context = context;
    }
}
