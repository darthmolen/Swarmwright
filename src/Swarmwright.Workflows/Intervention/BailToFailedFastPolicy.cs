using Swarmwright.Models.Enums;

namespace Swarmwright.Workflows.Intervention;

/// <summary>
/// Test-default <see cref="IInterventionPolicy"/>. Returns
/// <see cref="InterventionDecision.Bail"/> for every routed pause state. Use
/// in unit tests where a swarm parking on intervention is itself the failure
/// signal — no recovery, no retry budget. Production swarms ship with
/// <see cref="AutoContinuePolicy"/> instead.
/// </summary>
public sealed class BailToFailedFastPolicy : IInterventionPolicy
{
    /// <inheritdoc/>
    public Task<InterventionDecision> DecideAsync(
        InterventionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.State switch
        {
            SwarmInstanceState.AwaitingIntervention or SwarmInstanceState.AwaitingFeedback =>
                Task.FromResult(InterventionDecision.Bail),
            _ =>
                throw new ArgumentException(
                    $"BailToFailedFastPolicy is only valid for routed pause states (AwaitingIntervention / AwaitingFeedback); got {context.State}.",
                    nameof(context)),
        };
    }
}
