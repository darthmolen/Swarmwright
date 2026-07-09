namespace Swarmwright.McpServer.Contracts;

/// <summary>
/// Task descriptor returned by <c>list_tasks</c>.
/// </summary>
/// <param name="Id">The task identifier.</param>
/// <param name="Subject">Short title of the task.</param>
/// <param name="WorkerName">The assigned worker name.</param>
/// <param name="WorkerRole">The specialist role required.</param>
/// <param name="Status">Task status as a PascalCase string.</param>
/// <param name="CreatedAt">UTC task creation timestamp.</param>
/// <param name="UpdatedAt">UTC last-update timestamp.</param>
public sealed record TaskInfo(
    string Id,
    string Subject,
    string WorkerName,
    string WorkerRole,
    string Status,
    DateTime CreatedAt,
    DateTime UpdatedAt);
