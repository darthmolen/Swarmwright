namespace Swarmwright.Refinement;

/// <summary>
/// Agent metadata returned by the AG-UI info endpoint for CopilotKit agent discovery.
/// </summary>
public sealed class RefinementAgentInfo
{
    /// <summary>
    /// Gets or sets the agent's unique name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the agent's description shown in the CopilotKit UI.
    /// </summary>
    public string Description { get; set; } = string.Empty;
}
