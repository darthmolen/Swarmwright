using System.Text.Json;

namespace Swarmwright.Refinement;

/// <summary>
/// DTO for the single-endpoint AG-UI protocol used by CopilotKit.
/// Dispatches on <see cref="Method"/> to route info, agent/run, and agent/stop requests.
/// </summary>
public sealed class RefinementRequestDto
{
    /// <summary>
    /// Gets or sets the method name (e.g. "info", "agent/run", "agent/stop").
    /// </summary>
    public string Method { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the optional parameters (e.g. agentId).
    /// </summary>
    public JsonElement? Params { get; set; }

    /// <summary>
    /// Gets or sets the optional body (e.g. messages, threadId).
    /// </summary>
    public JsonElement? Body { get; set; }
}
