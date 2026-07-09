namespace Swarmwright.Extensions;

/// <summary>
/// Transport-agnostic return type from swarm intervention endpoint handlers.
/// Minimal API endpoint bindings translate this into <c>Results.StatusCode</c>
/// / <c>Results.Json</c> before writing the HTTP response; unit tests inspect
/// the record directly without spinning up a test server.
/// </summary>
/// <param name="StatusCode">The HTTP status code to return.</param>
/// <param name="Body">An optional response body (serialized as JSON).</param>
public sealed record InterventionResult(int StatusCode, object? Body = null)
{
    /// <summary>Standard 204 No Content.</summary>
    /// <returns>A new <see cref="InterventionResult"/>.</returns>
    public static InterventionResult NoContent() => new(204);

    /// <summary>Standard 200 OK with a JSON body.</summary>
    /// <param name="body">The response payload.</param>
    /// <returns>A new <see cref="InterventionResult"/>.</returns>
    public static InterventionResult Ok(object body) => new(200, body);

    /// <summary>Standard 404 Not Found.</summary>
    /// <param name="code">A short machine-readable code (e.g. <c>swarm_not_found</c>).</param>
    /// <param name="message">A human-readable message.</param>
    /// <returns>A new <see cref="InterventionResult"/>.</returns>
    public static InterventionResult NotFound(string code, string message) =>
        new(404, new { code, message });

    /// <summary>Standard 403 Forbidden.</summary>
    /// <param name="code">A short machine-readable code.</param>
    /// <param name="message">A human-readable message.</param>
    /// <returns>A new <see cref="InterventionResult"/>.</returns>
    public static InterventionResult Forbidden(string code, string message) =>
        new(403, new { code, message });

    /// <summary>
    /// Standard 409 Conflict for a state-machine guard rejection.
    /// </summary>
    /// <param name="code">A short machine-readable code (e.g. <c>invalid_transition</c>).</param>
    /// <param name="body">An optional extra payload; when <see langword="null"/> the body is just <c>{ code }</c>.</param>
    /// <returns>A new <see cref="InterventionResult"/>.</returns>
    public static InterventionResult Conflict(string code, object? body = null) =>
        new(409, body ?? new { code });

    /// <summary>
    /// Standard 410 Gone when a terminal swarm is targeted by a mutator.
    /// </summary>
    /// <param name="state">The terminal state the swarm is currently in.</param>
    /// <returns>A new <see cref="InterventionResult"/>.</returns>
    public static InterventionResult Gone(string state) =>
        new(410, new { code = "terminal_state", state });

    /// <summary>
    /// Standard 423 Locked when a diagnose lock is held by someone other than the caller.
    /// </summary>
    /// <param name="lockedBy">The identity currently holding the lock.</param>
    /// <param name="lockedAt">The wall-clock time the lock was acquired.</param>
    /// <returns>A new <see cref="InterventionResult"/>.</returns>
    public static InterventionResult Locked(string lockedBy, DateTime lockedAt) =>
        new(423, new { code = "locked", lockedBy, lockedAt });
}
