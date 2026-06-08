using Microsoft.EntityFrameworkCore;
using Swarmwright.Hosting.StateMachine;

namespace Swarmwright.Database;

/// <summary>
/// Centralized save helpers for <see cref="SwarmDbContext"/>. Wraps
/// <see cref="DbContext.SaveChangesAsync(CancellationToken)"/> and translates
/// <see cref="DbUpdateConcurrencyException"/> (raised when the
/// <c>xmin</c> concurrency token mismatches) into a typed
/// <see cref="SwarmConcurrencyConflictException"/> so callers can map it to a
/// 409 with a stable error code.
/// </summary>
public static class SwarmDbContextExtensions
{
    /// <summary>
    /// Gets or Sets the Test-only hook fired after change-tracking but before
    /// <see cref="DbContext.SaveChangesAsync(CancellationToken)"/>. Used by
    /// <c>ConcurrentContinueTests</c> to deterministically interleave two
    /// callers against the same row so the second loses on <c>xmin</c>.
    /// Production code never sets this; tests reset it in
    /// <c>[TestCleanup]</c>.
    /// </summary>
    internal static Func<Task>? OnBeforeSaveForTesting { get; set; }

    /// <summary>
    /// Saves all pending changes, mapping a Postgres concurrency-token
    /// mismatch (<c>xmin</c>) to <see cref="SwarmConcurrencyConflictException"/>.
    /// </summary>
    /// <param name="context">The swarm db context.</param>
    /// <param name="entityKind">A short label for the entity being saved
    /// (<c>swarm</c>, <c>task</c>) — surfaced in the error code body.</param>
    /// <param name="entityId">The entity's stable identifier (human readable).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the save has succeeded.</returns>
    public static async Task SaveOrThrowAsync(
        this SwarmDbContext context,
        string entityKind,
        string entityId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrEmpty(entityKind);
        ArgumentException.ThrowIfNullOrEmpty(entityId);

        var hook = OnBeforeSaveForTesting;
        if (hook is not null)
        {
            await hook().ConfigureAwait(false);
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new SwarmConcurrencyConflictException(entityKind, entityId, ex);
        }
    }
}
