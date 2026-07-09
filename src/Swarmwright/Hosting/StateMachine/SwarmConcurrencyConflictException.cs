using Microsoft.EntityFrameworkCore;

namespace Swarmwright.Hosting.StateMachine;

/// <summary>
/// Thrown by <c>SwarmDbContextExtensions.SaveOrThrowAsync</c> when a
/// <see cref="DbUpdateConcurrencyException"/> escapes — i.e. another writer
/// updated the same row between the read and the save and Postgres rejected
/// the write because the <c>xmin</c> token no longer matched. Callers map
/// this to HTTP <c>409 Conflict</c> with the stable code
/// <c>concurrency_conflict</c> so clients can refetch and retry.
/// </summary>
/// <remarks>
/// Distinct from <see cref="InvalidStateTransitionException"/>: a guard
/// rejection means "this transition is illegal, refresh state and re-evaluate";
/// a concurrency conflict means "you raced another writer, refetch and retry
/// the same intent".
/// </remarks>
public sealed class SwarmConcurrencyConflictException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SwarmConcurrencyConflictException"/> class.
    /// </summary>
    /// <param name="entityKind">A short label for the contended entity (<c>swarm</c> or <c>task</c>).</param>
    /// <param name="entityId">The contended entity's identifier (human readable).</param>
    /// <param name="inner">The underlying EF exception that triggered the mapping.</param>
    public SwarmConcurrencyConflictException(
        string entityKind,
        string entityId,
        DbUpdateConcurrencyException inner)
        : base($"Concurrent update to {entityKind} {entityId}; refetch and retry.", inner)
    {
        this.EntityKind = entityKind;
        this.EntityId = entityId;
    }

    /// <summary>Gets the contended entity kind (<c>swarm</c> or <c>task</c>).</summary>
    public string EntityKind { get; }

    /// <summary>Gets the contended entity identifier.</summary>
    public string EntityId { get; }
}
