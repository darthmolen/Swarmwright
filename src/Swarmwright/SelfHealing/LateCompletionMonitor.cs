using Swarmwright.Database.Repositories;
using Swarmwright.Events;
using Swarmwright.Models.Enums;

namespace Swarmwright.SelfHealing;

/// <summary>
/// Periodically checks for tasks that timed out but may have completed late.
/// </summary>
public class LateCompletionMonitor : IDisposable
{
    /// <summary>
    /// The states considered active for monitoring purposes.
    /// </summary>
    private static readonly string[] ActiveStates =
    [
        nameof(SwarmInstanceState.Executing),
    ];

    private readonly ISwarmRepository repository;
    private readonly ISwarmEventBus eventBus;
    private Timer? timer;
    private bool disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="LateCompletionMonitor"/> class.
    /// </summary>
    /// <param name="repository">The swarm repository for persistence operations.</param>
    /// <param name="eventBus">The event bus for emitting late completion events.</param>
    /// <param name="checkIntervalSeconds">The interval in seconds between checks.</param>
    public LateCompletionMonitor(
        ISwarmRepository repository,
        ISwarmEventBus eventBus,
        int checkIntervalSeconds = 60)
    {
        this.repository = repository;
        this.eventBus = eventBus;
        var interval = TimeSpan.FromSeconds(checkIntervalSeconds);
        this.timer = new Timer(_ => { }, null, Timeout.InfiniteTimeSpan, interval);
    }

    /// <summary>
    /// Checks for tasks that were timed out but have since completed.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        var activeSwarms = await this.repository.ListSwarmsByStateAsync(ActiveStates).ConfigureAwait(false);

        foreach (var swarm in activeSwarms)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var tasks = await this.repository.GetTasksAsync(swarm.Id).ConfigureAwait(false);

            foreach (var task in tasks)
            {
                if (string.Equals(task.State, nameof(TaskState.Completed), StringComparison.Ordinal))
                {
                    await this.eventBus.EmitAsync(
                        "task.late_completed",
                        new { SwarmId = swarm.Id, TaskId = task.Id }).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>
    /// Releases all resources used by the <see cref="LateCompletionMonitor"/>.
    /// </summary>
    public void Dispose()
    {
        this.Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases the unmanaged resources and optionally releases the managed resources.
    /// </summary>
    /// <param name="disposing">True to release both managed and unmanaged resources.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (!this.disposed)
        {
            if (disposing)
            {
                this.timer?.Dispose();
                this.timer = null;
            }

            this.disposed = true;
        }
    }
}
