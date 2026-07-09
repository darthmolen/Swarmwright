namespace Swarmwright.McpServer.Contracts;

/// <summary>
/// Lightweight descriptor for an active swarm execution.
/// </summary>
/// <param name="SwarmId">The unique swarm identifier.</param>
/// <param name="Goal">The user-supplied goal string.</param>
/// <param name="TemplateKey">The template key used to create the swarm, if any.</param>
/// <param name="Phase">The current lifecycle phase as a PascalCase string.</param>
/// <param name="CreatedAt">UTC timestamp at which the swarm was created.</param>
/// <param name="IsRunning">A value indicating whether the swarm's running task is still active.</param>
public sealed record SwarmInfo(
    Guid SwarmId,
    string Goal,
    string? TemplateKey,
    string Phase,
    DateTime CreatedAt,
    bool IsRunning);
