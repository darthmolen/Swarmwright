using Swarmwright.Recommendation;

namespace Swarmwright.Database.Models;

/// <summary>
/// API response DTO for <c>GET /api/swarm/{id}</c>. Frozen shape consumed by the
/// admin UI hydration and external agents via MCP; field order and casing here
/// are contractually significant. Serializes through <c>SwarmJsonOptions.Default</c>
/// (camelCase via <see cref="System.Text.Json.JsonSerializerDefaults.Web"/>).
/// </summary>
/// <param name="SwarmId">The swarm identifier.</param>
/// <param name="Goal">The user-supplied goal text.</param>
/// <param name="TemplateKey">The template key the swarm was instantiated from.</param>
/// <param name="Phase">Legacy alias for <see cref="State"/>; preserved for frontend parity.</param>
/// <param name="State">The canonical persisted state (PascalCase <see cref="Swarmwright.Models.Enums.SwarmInstanceState"/>).</param>
/// <param name="IsRunning"><see langword="true"/> when the in-memory execution is still live.</param>
/// <param name="LockedBy">The actor currently holding the diagnose lock, or <see langword="null"/>.</param>
/// <param name="LockedAt">UTC timestamp of the lock acquisition, or <see langword="null"/>.</param>
/// <param name="CreatedAt">UTC creation timestamp.</param>
/// <param name="CompletedAt">UTC completion timestamp, or <see langword="null"/> when not finished.</param>
/// <param name="Recommendation">
/// Server-computed opinion about the right recovery action. Populated only when the
/// swarm is in an actionable non-terminal state
/// (<see cref="Swarmwright.Models.Enums.SwarmInstanceState.AwaitingIntervention"/> or
/// <see cref="Swarmwright.Models.Enums.SwarmInstanceState.NeedsDiagnosis"/>); otherwise <see langword="null"/>.
/// </param>
public sealed record SwarmMetadataResponse(
    Guid SwarmId,
    string Goal,
    string? TemplateKey,
    string Phase,
    string State,
    bool IsRunning,
    string? LockedBy,
    DateTime? LockedAt,
    DateTime CreatedAt,
    DateTime? CompletedAt,
    SwarmContinueRecommendation? Recommendation);
