using Microsoft.Extensions.DependencyInjection;
using Swarmwright.Orchestration;

namespace Swarmwright.Hosting;

/// <summary>
/// Creates <see cref="ISwarmOrchestrator"/> instances for a single swarm
/// execution. Extracted from <c>SwarmDispatcherService.BuildOrchestrator</c>
/// so the same wiring is available to the rehydrator (which rebuilds an
/// orchestrator for an evicted swarm outside the dispatcher's lifecycle).
/// </summary>
public interface ISwarmOrchestratorFactory
{
    /// <summary>
    /// Builds a new orchestrator using services resolved from
    /// <paramref name="scope"/>. The scope must live for the duration of
    /// the swarm run — the caller owns its disposal.
    /// </summary>
    /// <param name="scope">The per-swarm DI scope.</param>
    /// <param name="execution">The swarm execution bundle the orchestrator drives.</param>
    /// <returns>A ready-to-run orchestrator bound to the execution.</returns>
    public ISwarmOrchestrator Build(IServiceScope scope, SwarmExecution execution);
}
