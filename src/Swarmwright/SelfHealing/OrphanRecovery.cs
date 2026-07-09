using Swarmwright.Database.Repositories;
using Swarmwright.Events;
using Swarmwright.Hosting.StateMachine;
using Swarmwright.Models.Enums;

namespace Swarmwright.SelfHealing;

/// <summary>
/// Recovers orphaned swarms that were in non-terminal states when the
/// process crashed. Every such swarm is transitioned to
/// <see cref="SwarmInstanceState.AwaitingIntervention"/> via the state
/// transition service so the admin can recover via the normal 4-button
/// surface.
/// </summary>
public class OrphanRecovery
{
    /// <summary>
    /// The set of non-terminal states that indicate a swarm needs recovery.
    /// </summary>
    private static readonly string[] NonTerminalStates =
    [
        nameof(SwarmInstanceState.Created),
        nameof(SwarmInstanceState.Planning),
        nameof(SwarmInstanceState.Spawning),
        nameof(SwarmInstanceState.Executing),
        nameof(SwarmInstanceState.Synthesizing),
    ];

    private readonly ISwarmRepository repository;
    private readonly IStateTransitionService stateTransitionService;
    private readonly ISwarmEventBus eventBus;

    /// <summary>
    /// Initializes a new instance of the <see cref="OrphanRecovery"/> class.
    /// </summary>
    /// <param name="repository">The swarm repository for loading orphan candidates.</param>
    /// <param name="stateTransitionService">The single write surface for the recovery transition.</param>
    /// <param name="eventBus">The event bus for emitting recovery events.</param>
    public OrphanRecovery(
        ISwarmRepository repository,
        IStateTransitionService stateTransitionService,
        ISwarmEventBus eventBus)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(stateTransitionService);
        ArgumentNullException.ThrowIfNull(eventBus);
        this.repository = repository;
        this.stateTransitionService = stateTransitionService;
        this.eventBus = eventBus;
    }

    /// <summary>
    /// Recovers all swarms that are in non-terminal states by transitioning
    /// them to <see cref="SwarmInstanceState.AwaitingIntervention"/>.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task RecoverAsync(CancellationToken cancellationToken = default)
    {
        var orphans = await this.repository.ListSwarmsByStateAsync(NonTerminalStates).ConfigureAwait(false);

        foreach (var swarm in orphans)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var previousState = swarm.State;

            try
            {
                await this.stateTransitionService.TransitionSwarmAsync(
                    swarm.Id,
                    SwarmInstanceState.AwaitingIntervention,
                    TransitionReasons.TaskFailed,
                    actor: "orphan-recovery",
                    note: $"Recovered from orphaned state '{previousState}' after process restart.",
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidStateTransitionException)
            {
                // Another process already recovered this swarm — nothing to do.
                continue;
            }

            await this.eventBus.EmitAsync(
                "swarm.orphan_recovered",
                new { SwarmId = swarm.Id, PreviousState = previousState }).ConfigureAwait(false);
        }
    }
}
