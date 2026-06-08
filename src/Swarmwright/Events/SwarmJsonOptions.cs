using System.Text.Json;
using System.Text.Json.Serialization;

namespace Swarmwright.Events;

/// <summary>
/// Provides the canonical <see cref="JsonSerializerOptions"/> instance used by
/// every swarm JSON writer (state snapshots, SSE envelopes, AG-UI event
/// interceptor). Centralizing the options guarantees enums are serialized as
/// strings (for example, <c>"Pending"</c>) instead of their numeric values so
/// downstream consumers (the swarm frontend) can discriminate task state
/// without a magic-number mapping.
/// </summary>
internal static class SwarmJsonOptions
{
    /// <summary>
    /// The shared <see cref="JsonSerializerOptions"/> instance used across all
    /// swarm JSON writers. Configured with <see cref="JsonSerializerDefaults.Web"/>
    /// and a <see cref="JsonStringEnumConverter"/> so enum values serialize as
    /// their member names.
    /// </summary>
    public static readonly JsonSerializerOptions Default = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
}
