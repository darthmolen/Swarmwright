using Microsoft.AspNetCore.Http;

namespace Swarmwright.Extensions;

/// <summary>
/// Resolves the "actor" string recorded on swarm state transitions and locks.
/// Prefers an authenticated principal's name; falls back to the
/// <c>X-Swarm-Actor</c> header for unauthenticated dev hosts.
/// </summary>
public static class SwarmActorResolver
{
    /// <summary>
    /// Name of the header used to carry an actor identity when the host is
    /// unauthenticated. Must be set explicitly by the caller.
    /// </summary>
    public const string ActorHeader = "X-Swarm-Actor";

    /// <summary>
    /// Resolves an actor string suitable for writing to
    /// <c>SwarmStateTransition.Actor</c> / <c>SwarmEntity.LockedBy</c>.
    /// </summary>
    /// <param name="httpContext">The current request context.</param>
    /// <returns>The resolved actor name, or <see langword="null"/> when no identity is available.</returns>
    public static string? Resolve(HttpContext? httpContext)
    {
        if (httpContext is null)
        {
            return null;
        }

        var principal = httpContext.User?.Identity?.Name;
        if (!string.IsNullOrWhiteSpace(principal))
        {
            return principal;
        }

        if (httpContext.Request.Headers.TryGetValue(ActorHeader, out var values) && values.Count > 0)
        {
            var header = values[0];
            if (!string.IsNullOrWhiteSpace(header))
            {
                return header;
            }
        }

        return null;
    }
}
