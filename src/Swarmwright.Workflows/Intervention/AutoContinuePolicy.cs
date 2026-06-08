using Swarmwright.Models.Enums;

namespace Swarmwright.Workflows.Intervention;

/// <summary>
/// Production-default <see cref="IInterventionPolicy"/>. Behavior:
/// <list type="bullet">
///   <item><c>AwaitingIntervention</c>: returns <see cref="InterventionDecision.SmartContinue"/> while the executor's attempt counter is at or below <see cref="MaxRetries"/>; bails after.</item>
///   <item><c>AwaitingFeedback</c>: returns <see cref="InterventionDecision.Bail"/> immediately — production automation cannot wait for a human.</item>
/// </list>
/// <c>NeedsDiagnosis</c> is hardcoded to bail at the executor and never reaches
/// this policy; the policy throws if asked about it.
/// </summary>
public sealed class AutoContinuePolicy : IInterventionPolicy
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AutoContinuePolicy"/> class.
    /// </summary>
    /// <param name="maxRetries">Maximum number of <c>SmartContinue</c> attempts before bailing on <c>AwaitingIntervention</c>. Defaults to 3.</param>
    public AutoContinuePolicy(int maxRetries = 3)
    {
        if (maxRetries < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxRetries),
                maxRetries,
                "maxRetries must be at least 1.");
        }

        this.MaxRetries = maxRetries;
    }

    /// <summary>Gets the configured retry budget.</summary>
    public int MaxRetries { get; }

    /// <inheritdoc/>
    public Task<InterventionDecision> DecideAsync(
        InterventionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.State switch
        {
            SwarmInstanceState.AwaitingIntervention =>
                Task.FromResult(context.Attempt <= this.MaxRetries
                    ? InterventionDecision.SmartContinue
                    : InterventionDecision.Bail),
            SwarmInstanceState.AwaitingFeedback =>
                Task.FromResult(InterventionDecision.Bail),
            _ =>
                throw new ArgumentException(
                    $"AutoContinuePolicy is only valid for routed pause states (AwaitingIntervention / AwaitingFeedback); got {context.State}.",
                    nameof(context)),
        };
    }
}
