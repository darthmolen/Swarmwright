using System.Text.Json;
using Swarmwright.Events.AgUI;

namespace Swarmwright.Events;

/// <summary>
/// Provides helper methods for formatting Server-Sent Events (SSE) messages.
/// </summary>
public static class SseEventWriter
{
    /// <summary>
    /// Formats an event as an SSE message string.
    /// </summary>
    /// <param name="eventType">The event type identifier.</param>
    /// <param name="data">The optional data payload to serialize as JSON.</param>
    /// <returns>A properly formatted SSE event string.</returns>
    public static string FormatEvent(string eventType, object? data)
    {
        var envelope = new { type = eventType, data };
        var json = JsonSerializer.Serialize(envelope, SwarmJsonOptions.Default);
        return $"data: {json}\n\n";
    }

    /// <summary>
    /// Formats a typed AG-UI event as an SSE message string.
    /// </summary>
    /// <param name="evt">The AG-UI event to serialize.</param>
    /// <returns>A properly formatted SSE event string.</returns>
    public static string FormatAgUIEvent(SwarmAgUIEvent evt)
    {
        var json = JsonSerializer.Serialize(evt, SwarmJsonOptions.Default);
        return $"data: {json}\n\n";
    }

    /// <summary>
    /// Formats an SSE heartbeat comment to keep the connection alive.
    /// </summary>
    /// <returns>A properly formatted SSE comment string.</returns>
    public static string FormatHeartbeat() => ":\n\n";
}
