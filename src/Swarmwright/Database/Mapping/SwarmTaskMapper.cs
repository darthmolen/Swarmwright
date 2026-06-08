using System.Text.Json;
using Swarmwright.Database.Models;
using Swarmwright.Models;
using Swarmwright.Models.Enums;

namespace Swarmwright.Database.Mapping;

/// <summary>
/// Maps a persisted <see cref="TaskEntity"/> row into the
/// <see cref="SwarmTask"/> domain object. The same domain shape is emitted
/// by <c>SwarmOrchestrator</c>'s live <c>STATE_SNAPSHOT</c> path, so the
/// REST <c>/tasks</c> endpoint and the rehydration path produce identical
/// wire payloads — the frontend does not need to handle two shapes.
/// </summary>
public static class SwarmTaskMapper
{
    /// <summary>
    /// Converts a <see cref="TaskEntity"/> into its <see cref="SwarmTask"/>
    /// form, parsing the persisted <c>State</c> string into a
    /// <see cref="TaskState"/> enum and the JSON-encoded <c>BlockedByJson</c>
    /// column into a populated <see cref="SwarmTask.BlockedBy"/> list.
    /// </summary>
    /// <param name="entity">The persisted row.</param>
    /// <returns>The hydrated domain task.</returns>
    public static SwarmTask FromEntity(TaskEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var task = new SwarmTask
        {
            SwarmId = entity.SwarmId,
            Id = entity.Id,
            Subject = entity.Subject,
            Description = entity.Description,
            WorkerRole = entity.WorkerRole,
            WorkerName = entity.WorkerName,
            Status = Enum.Parse<TaskState>(entity.State, ignoreCase: true),
            Result = entity.Result,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt,
        };

        var blockedBy = JsonSerializer.Deserialize<List<string>>(entity.BlockedByJson) ?? [];
        foreach (var dep in blockedBy)
        {
            task.BlockedBy.Add(dep);
        }

        return task;
    }
}
