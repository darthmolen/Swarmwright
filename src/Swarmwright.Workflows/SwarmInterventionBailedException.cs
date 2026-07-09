namespace Swarmwright.Workflows;

/// <summary>
/// Thrown by <c>SwarmExecutor&lt;TOutput&gt;</c> when the swarm reaches a
/// non-recoverable terminal outcome from the executor's perspective:
/// <list type="bullet">
///   <item>The underlying swarm reached <c>SwarmInstanceState.Failed</c>.</item>
///   <item>An <see cref="Intervention.IInterventionPolicy"/> returned <c>Bail</c>.</item>
///   <item>The swarm transitioned to <c>NeedsDiagnosis</c> (recovery budget exhausted; v1 hardcoded bail).</item>
///   <item>A resume target was unrecoverable (<c>EnsureLiveAsync</c> returned <see langword="null"/>).</item>
/// </list>
/// Cancellation paths surface as <see cref="OperationCanceledException"/>
/// instead, so callers must distinguish caller-cancel from
/// swarm-declared-failure.
/// </summary>
public sealed class SwarmInterventionBailedException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SwarmInterventionBailedException"/> class.
    /// </summary>
    public SwarmInterventionBailedException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SwarmInterventionBailedException"/> class.
    /// </summary>
    /// <param name="message">A description of the bail outcome.</param>
    public SwarmInterventionBailedException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SwarmInterventionBailedException"/> class.
    /// </summary>
    /// <param name="message">A description of the bail outcome.</param>
    /// <param name="innerException">The exception that triggered the bail, when available.</param>
    public SwarmInterventionBailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
