using System.Text.Json;
using System.Text.Json.Serialization;

namespace Swarmwright.Events.AgUI;

/// <summary>
/// Base class for AG-UI protocol events. Produces the same JSON wire format
/// as the AG-UI specification without depending on the SDK's internal types.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RunStartedEvent), "RUN_STARTED")]
[JsonDerivedType(typeof(RunFinishedEvent), "RUN_FINISHED")]
[JsonDerivedType(typeof(RunErrorEvent), "RUN_ERROR")]
[JsonDerivedType(typeof(StepStartedEvent), "STEP_STARTED")]
[JsonDerivedType(typeof(StepFinishedEvent), "STEP_FINISHED")]
[JsonDerivedType(typeof(StateSnapshotEvent), "STATE_SNAPSHOT")]
[JsonDerivedType(typeof(StateDeltaEvent), "STATE_DELTA")]
[JsonDerivedType(typeof(TextMessageStartEvent), "TEXT_MESSAGE_START")]
[JsonDerivedType(typeof(TextMessageContentEvent), "TEXT_MESSAGE_CONTENT")]
[JsonDerivedType(typeof(TextMessageEndEvent), "TEXT_MESSAGE_END")]
[JsonDerivedType(typeof(ToolCallStartEvent), "TOOL_CALL_START")]
[JsonDerivedType(typeof(ToolCallArgsEvent), "TOOL_CALL_ARGS")]
[JsonDerivedType(typeof(ToolCallEndEvent), "TOOL_CALL_END")]
[JsonDerivedType(typeof(ToolCallResultEvent), "TOOL_CALL_RESULT")]
[JsonDerivedType(typeof(SwarmCustomEvent), "SWARM_CUSTOM")]
public abstract class SwarmAgUIEvent
{
}

// -----------------------------------------------------------------------
// Lifecycle events
// -----------------------------------------------------------------------

/// <summary>
/// Emitted when a swarm run begins processing.
/// </summary>
#pragma warning disable CA1708 // Identifiers should differ by more than case
#pragma warning disable SA1402 // File may only contain a single type
public sealed class RunStartedEvent : SwarmAgUIEvent
{
    /// <summary>
    /// Gets or sets the conversation thread identifier (swarm ID).
    /// </summary>
    [JsonPropertyName("threadId")]
    public string ThreadId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the unique run identifier.
    /// </summary>
    [JsonPropertyName("runId")]
    public string RunId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the user-supplied goal that initiated this run.
    /// </summary>
    [JsonPropertyName("goal")]
    public string Goal { get; set; } = string.Empty;
}

/// <summary>
/// Emitted upon successful swarm completion.
/// </summary>
public sealed class RunFinishedEvent : SwarmAgUIEvent
{
    /// <summary>
    /// Gets or sets the conversation thread identifier.
    /// </summary>
    [JsonPropertyName("threadId")]
    public string ThreadId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the unique run identifier.
    /// </summary>
    [JsonPropertyName("runId")]
    public string RunId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the optional result payload.
    /// </summary>
    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Result { get; set; }
}

/// <summary>
/// Emitted when a swarm encounters an unrecoverable error.
/// </summary>
public sealed class RunErrorEvent : SwarmAgUIEvent
{
    /// <summary>
    /// Gets or sets the error description.
    /// </summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the optional error code.
    /// </summary>
    [JsonPropertyName("code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Code { get; set; }
}

// -----------------------------------------------------------------------
// Step events (phase transitions)
// -----------------------------------------------------------------------

/// <summary>
/// Emitted when a swarm phase begins.
/// </summary>
public sealed class StepStartedEvent : SwarmAgUIEvent
{
    /// <summary>
    /// Gets or sets the phase name.
    /// </summary>
    [JsonPropertyName("stepName")]
    public string StepName { get; set; } = string.Empty;
}

/// <summary>
/// Emitted when a swarm phase completes.
/// </summary>
public sealed class StepFinishedEvent : SwarmAgUIEvent
{
    /// <summary>
    /// Gets or sets the phase name.
    /// </summary>
    [JsonPropertyName("stepName")]
    public string StepName { get; set; } = string.Empty;
}

// -----------------------------------------------------------------------
// Text message events
// -----------------------------------------------------------------------

/// <summary>
/// Emitted to initialize an incoming text message.
/// </summary>
public sealed class TextMessageStartEvent : SwarmAgUIEvent
{
    /// <summary>
    /// Gets or sets the unique message identifier.
    /// </summary>
    [JsonPropertyName("messageId")]
    public string MessageId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the sender role.
    /// </summary>
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the agent name (swarm extension, not in standard AG-UI).
    /// </summary>
    [JsonPropertyName("agentName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AgentName { get; set; }
}

/// <summary>
/// Emitted repeatedly as text content streams.
/// </summary>
public sealed class TextMessageContentEvent : SwarmAgUIEvent
{
    /// <summary>
    /// Gets or sets the message identifier referencing the start event.
    /// </summary>
    [JsonPropertyName("messageId")]
    public string MessageId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the text content chunk.
    /// </summary>
    [JsonPropertyName("delta")]
    public string Delta { get; set; } = string.Empty;
}

/// <summary>
/// Emitted when a text message transmission completes.
/// </summary>
public sealed class TextMessageEndEvent : SwarmAgUIEvent
{
    /// <summary>
    /// Gets or sets the message identifier referencing the start event.
    /// </summary>
    [JsonPropertyName("messageId")]
    public string MessageId { get; set; } = string.Empty;
}

// -----------------------------------------------------------------------
// Tool call events
// -----------------------------------------------------------------------

/// <summary>
/// Emitted when an agent invokes a tool.
/// </summary>
public sealed class ToolCallStartEvent : SwarmAgUIEvent
{
    /// <summary>
    /// Gets or sets the unique tool call identifier.
    /// </summary>
    [JsonPropertyName("toolCallId")]
    public string ToolCallId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the tool name being invoked.
    /// </summary>
    [JsonPropertyName("toolCallName")]
    public string ToolCallName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the optional parent message identifier.
    /// </summary>
    [JsonPropertyName("parentMessageId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentMessageId { get; set; }

    /// <summary>
    /// Gets or sets the agent name (swarm extension, not in standard AG-UI).
    /// </summary>
    [JsonPropertyName("agentName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AgentName { get; set; }
}

/// <summary>
/// Emitted repeatedly as tool call arguments stream.
/// </summary>
public sealed class ToolCallArgsEvent : SwarmAgUIEvent
{
    /// <summary>
    /// Gets or sets the tool call identifier referencing the start event.
    /// </summary>
    [JsonPropertyName("toolCallId")]
    public string ToolCallId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the argument data chunk (often a JSON fragment).
    /// </summary>
    [JsonPropertyName("delta")]
    public string Delta { get; set; } = string.Empty;
}

/// <summary>
/// Emitted when tool argument transmission completes.
/// </summary>
public sealed class ToolCallEndEvent : SwarmAgUIEvent
{
    /// <summary>
    /// Gets or sets the tool call identifier referencing the start event.
    /// </summary>
    [JsonPropertyName("toolCallId")]
    public string ToolCallId { get; set; } = string.Empty;
}

/// <summary>
/// Emitted after tool execution completes with a result.
/// </summary>
public sealed class ToolCallResultEvent : SwarmAgUIEvent
{
    /// <summary>
    /// Gets or sets the optional conversation message identifier.
    /// </summary>
    [JsonPropertyName("messageId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MessageId { get; set; }

    /// <summary>
    /// Gets or sets the tool call identifier referencing the start event.
    /// </summary>
    [JsonPropertyName("toolCallId")]
    public string ToolCallId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the tool execution result content.
    /// </summary>
    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the optional role (typically "tool").
    /// </summary>
    [JsonPropertyName("role")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Role { get; set; }
}

// -----------------------------------------------------------------------
// State management events
// -----------------------------------------------------------------------

/// <summary>
/// Emitted to provide a complete state representation for hydration.
/// </summary>
public sealed class StateSnapshotEvent : SwarmAgUIEvent
{
    /// <summary>
    /// Gets or sets the complete state snapshot as a JSON element.
    /// </summary>
    [JsonPropertyName("snapshot")]
    public JsonElement? Snapshot { get; set; }
}

/// <summary>
/// Emitted for incremental state changes using RFC 6902 JSON Patch operations.
/// </summary>
public sealed class StateDeltaEvent : SwarmAgUIEvent
{
    /// <summary>
    /// Gets or sets the JSON Patch operations array.
    /// </summary>
    [JsonPropertyName("delta")]
    public JsonElement? Delta { get; set; }
}

// -----------------------------------------------------------------------
// Custom swarm events
// -----------------------------------------------------------------------

/// <summary>
/// Emitted for swarm-specific domain events not covered by standard AG-UI types.
/// </summary>
public sealed class SwarmCustomEvent : SwarmAgUIEvent
{
    /// <summary>
    /// Gets or sets the custom event name (e.g. SWARM_TASK_CREATED).
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the event payload as a JSON element.
    /// </summary>
    [JsonPropertyName("value")]
    public JsonElement Value { get; set; }

    /// <summary>
    /// Gets or sets the optional agent name associated with this event.
    /// </summary>
    [JsonPropertyName("agentName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AgentName { get; set; }
}
#pragma warning restore CA1708 // Identifiers should differ by more than case
#pragma warning restore SA1402 // File may only contain a single type
