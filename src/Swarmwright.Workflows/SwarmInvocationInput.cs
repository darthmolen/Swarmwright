namespace Swarmwright.Workflows;

/// <summary>
/// Input record consumed by <c>SwarmExecutor&lt;TOutput&gt;</c>. Captures
/// either a fresh-dispatch goal+template or a resume target identifier.
/// Use the static factories <see cref="New(string, string?, System.Collections.Generic.IReadOnlyDictionary{string, string})"/> and
/// <see cref="Resume(System.Guid)"/> rather than the positional ctor —
/// they make the create vs. resume intent explicit at the call site.
/// </summary>
/// <param name="Goal">The user-facing goal text. Required for fresh dispatches; ignored on resume.</param>
/// <param name="TemplateKey">Optional template key for fresh dispatches; ignored on resume.</param>
/// <param name="ResumeSwarmId">When set, the executor calls <c>EnsureLiveAsync</c> instead of <c>CreateSwarmAsync</c>.</param>
/// <param name="Context">
/// Optional free-form key/value context forwarded to <c>CreateSwarmAsync</c> on
/// fresh dispatches; ignored on resume (the persisted context rehydrates instead).
/// </param>
public sealed record SwarmInvocationInput(
    string Goal,
    string? TemplateKey,
    System.Guid? ResumeSwarmId,
    System.Collections.Generic.IReadOnlyDictionary<string, string>? Context = null)
{
    /// <summary>
    /// Creates an input for a fresh swarm dispatch.
    /// </summary>
    /// <param name="goal">The user-facing goal text.</param>
    /// <param name="templateKey">Optional template key.</param>
    /// <param name="context">Optional free-form key/value context for the run.</param>
    /// <returns>A new <see cref="SwarmInvocationInput"/> with no resume target.</returns>
    public static SwarmInvocationInput New(
        string goal,
        string? templateKey = null,
        System.Collections.Generic.IReadOnlyDictionary<string, string>? context = null) =>
        new(goal, templateKey, ResumeSwarmId: null, Context: context);

    /// <summary>
    /// Creates an input for resuming an existing swarm via
    /// <c>EnsureLiveAsync</c>. The goal and template key are ignored on the
    /// resume path.
    /// </summary>
    /// <param name="swarmId">The id of the swarm to resume.</param>
    /// <returns>A new <see cref="SwarmInvocationInput"/> targeting <paramref name="swarmId"/>.</returns>
    public static SwarmInvocationInput Resume(System.Guid swarmId) =>
        new(Goal: string.Empty, TemplateKey: null, ResumeSwarmId: swarmId);
}
