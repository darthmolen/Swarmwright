namespace Swarmwright.Models;

/// <summary>
/// Represents a message in the swarm agent inbox system.
/// </summary>
public class InboxMessage
{
    /// <summary>Gets or sets the unique message identifier.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Gets or sets the name of the sending agent.</summary>
    public string Sender { get; set; } = string.Empty;

    /// <summary>Gets or sets the name of the recipient agent.</summary>
    public string Recipient { get; set; } = string.Empty;

    /// <summary>Gets or sets the message content.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>Gets or sets the message timestamp.</summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
