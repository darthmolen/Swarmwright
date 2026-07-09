namespace Swarmwright.Events;

/// <summary>
/// A type-erased unit of work on the notification channel. The dispatch closure captures the
/// strongly-typed notification and the generic handler resolution at publish time, so the
/// background service can run it without reflection over open generics.
/// </summary>
internal sealed class SwarmNotificationEnvelope
{
    /// <summary>Gets the notification type name, for diagnostics.</summary>
    public required string NotificationType { get; init; }

    /// <summary>Gets the closure that resolves and invokes the registered handlers for this notification.</summary>
    public required Func<IServiceProvider, CancellationToken, Task> DispatchAsync { get; init; }
}
