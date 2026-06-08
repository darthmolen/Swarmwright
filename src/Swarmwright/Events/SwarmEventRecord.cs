namespace Swarmwright.Events;

/// <summary>
/// Represents a recorded swarm event with its type, data, and timestamp.
/// </summary>
public sealed class SwarmEventRecord
{
    /// <summary>Gets or sets the event type identifier.</summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional event data payload.</summary>
    public object? Data { get; set; }

    /// <summary>Gets or sets the UTC timestamp when the event was recorded.</summary>
    public DateTime Timestamp { get; set; }
}
