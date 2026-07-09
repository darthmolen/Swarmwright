using System.Text.Json;
using Microsoft.Extensions.Logging;
using Swarmwright.Hosting;
using Swarmwright.Models.Enums;

namespace Swarmwright.Events;

/// <summary>
/// Default <see cref="ISwarmEmissionBroker"/> implementation. Resolves the
/// target swarm via <see cref="ISwarmManager.GetSwarm"/> and forwards
/// <c>SWARM_TASK_UPDATED</c> to that swarm's <see cref="AgUI.SwarmEventAdapter"/>.
/// </summary>
internal sealed partial class SwarmEmissionBroker : ISwarmEmissionBroker
{
    private readonly ISwarmManager manager;
    private readonly ILogger<SwarmEmissionBroker> logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SwarmEmissionBroker"/> class.
    /// </summary>
    /// <param name="manager">The swarm manager that owns the active-swarms dictionary.</param>
    /// <param name="logger">The logger used for missing-swarm warnings.</param>
    public SwarmEmissionBroker(ISwarmManager manager, ILogger<SwarmEmissionBroker> logger)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(logger);
        this.manager = manager;
        this.logger = logger;
    }

    /// <inheritdoc/>
    public async Task EmitTaskUpdatedAsync(
        Guid swarmId,
        string taskId,
        TaskState status,
        string? agentName,
        CancellationToken cancellationToken = default)
    {
        var execution = this.manager.GetSwarm(swarmId);
        if (execution is null)
        {
            this.LogSwarmNotActive(swarmId, taskId, status.ToString());
            return;
        }

        await execution.AgUiAdapter.EmitCustomAsync(
            "SWARM_TASK_UPDATED",
            JsonSerializer.SerializeToElement(new
            {
                taskId,
                status = status.ToString(),
                agent = agentName,
            }),
            agentName).ConfigureAwait(false);
    }

    [LoggerMessage(LogLevel.Warning, "Cannot emit SWARM_TASK_UPDATED for swarm {SwarmId} task {TaskId} ({Status}): swarm not in active dictionary (evicted or never registered).")]
    private partial void LogSwarmNotActive(Guid swarmId, string taskId, string status);
}
