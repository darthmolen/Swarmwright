namespace Swarmwright.McpServer.Contracts;

/// <summary>
/// Generic success-or-failure result for write operations that have no richer payload.
/// </summary>
/// <param name="Ok">A value indicating whether the operation succeeded.</param>
/// <param name="Message">Optional human-readable explanation.</param>
public sealed record OperationResult(bool Ok, string? Message);
