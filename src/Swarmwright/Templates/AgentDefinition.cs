namespace Swarmwright.Templates;

/// <summary>
/// Defines an agent parsed from a template markdown file.
/// </summary>
public class AgentDefinition
{
    /// <summary>Gets or sets the unique agent name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the human-readable display name.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Gets or sets the agent description.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Gets or sets the list of tools available to the agent, or <c>null</c> for all tools.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "Mutable DTO populated from YAML deserialization.")]
    public List<string>? Tools { get; set; }

    /// <summary>Gets or sets the prompt template body.</summary>
    public string PromptTemplate { get; set; } = string.Empty;

    /// <summary>Gets or sets the maximum number of concurrent instances.</summary>
    public int MaxInstances { get; set; } = 1;

    /// <summary>Gets or sets the maximum number of retries, or <c>null</c> for default.</summary>
    public int? MaxRetries { get; set; }

    /// <summary>Gets or sets the list of skills available to the agent.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "Mutable DTO populated from YAML deserialization.")]
    public List<string>? Skills { get; set; }

    /// <summary>Gets or sets a value indicating whether inference is enabled for this agent.</summary>
    public bool Infer { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the default tool set (read, write, web_fetch)
    /// is available to this agent. <c>null</c> means inherit from the template-level setting.
    /// </summary>
    public bool? AllowDefaultTools { get; set; }

    /// <summary>
    /// Gets or sets the list of MCP endpoint names whose tools this agent should load
    /// (e.g. <c>learn-microsoft</c>). Populated from the <c>mcp_endpoints</c> YAML
    /// frontmatter field. Null or empty means no MCP tools are loaded for this agent.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "Mutable DTO populated from YAML deserialization.")]
    public List<string>? McpEndpoints { get; set; }

    /// <summary>
    /// Gets or sets the list of custom tool names this agent is allowed to use.
    /// Populated from the <c>custom_tools</c> YAML frontmatter field. Null or empty
    /// means no custom tools are loaded for this agent.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "Mutable DTO populated from YAML deserialization.")]
    public List<string>? CustomTools { get; set; }
}
