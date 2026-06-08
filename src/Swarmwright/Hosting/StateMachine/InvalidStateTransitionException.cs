namespace Swarmwright.Hosting.StateMachine;

/// <summary>
/// Thrown when a requested state transition is not legal per
/// <see cref="SwarmStateGuards"/>. Callers translate this to an HTTP
/// <c>409 Conflict</c> response.
/// </summary>
public sealed class InvalidStateTransitionException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="InvalidStateTransitionException"/> class.
    /// </summary>
    /// <param name="entityKind">The kind of entity (<c>swarm</c> or <c>task</c>).</param>
    /// <param name="entityId">A stable identifier for the rejected entity (human readable).</param>
    /// <param name="fromState">The current state, as a string.</param>
    /// <param name="toState">The requested state, as a string.</param>
    /// <param name="reason">The supplied transition reason.</param>
    public InvalidStateTransitionException(
        string entityKind,
        string entityId,
        string fromState,
        string toState,
        string reason)
        : base($"Invalid {entityKind} state transition for {entityId}: {fromState} -> {toState} (reason: {reason}).")
    {
        this.EntityKind = entityKind;
        this.EntityId = entityId;
        this.FromState = fromState;
        this.ToState = toState;
        this.Reason = reason;
    }

    /// <summary>Gets the entity kind (<c>swarm</c> or <c>task</c>).</summary>
    public string EntityKind { get; }

    /// <summary>Gets the entity identifier.</summary>
    public string EntityId { get; }

    /// <summary>Gets the current state at the time of rejection.</summary>
    public string FromState { get; }

    /// <summary>Gets the state that was requested.</summary>
    public string ToState { get; }

    /// <summary>Gets the supplied transition reason.</summary>
    public string Reason { get; }
}
