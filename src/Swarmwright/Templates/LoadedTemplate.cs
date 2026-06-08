namespace Swarmwright.Templates;

/// <summary>
/// Represents a fully loaded swarm template with leader, workers, and synthesis prompts.
/// </summary>
public class LoadedTemplate
{
    /// <summary>Gets or sets the unique template key.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Gets or sets the human-readable template name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the template description.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Gets or sets the goal template string with placeholders.</summary>
    public string GoalTemplate { get; set; } = string.Empty;

    /// <summary>Gets or sets the leader agent prompt template.</summary>
    public string LeaderPrompt { get; set; } = string.Empty;

    /// <summary>Gets the list of worker agent definitions.</summary>
    public List<AgentDefinition> Agents { get; } = [];

    /// <summary>Gets or sets the synthesis prompt template.</summary>
    public string SynthesisPrompt { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether QA review is enabled.</summary>
    public bool QaEnabled { get; set; }

    /// <summary>Gets or sets the maximum number of retries for the template.</summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>
    /// Gets or sets a value indicating whether the default tool set (read, write, web_fetch)
    /// is available to workers in this template. Defaults to <c>true</c>. Individual workers
    /// may override via the <c>allow_default_tools</c> frontmatter field.
    /// </summary>
    public bool AllowDefaultTools { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether skill script execution is allowed
    /// for workers in this template. Defaults to <c>false</c>. Controlled by the
    /// <c>allow_skill_scripts</c> field in <c>_template.yaml</c>.
    /// </summary>
    public bool AllowSkillScripts { get; set; }

    /// <summary>
    /// Gets or sets the system coordination preamble loaded from <c>system-prompt.md</c>
    /// (one level above the template directory). Prepended to all worker system prompts.
    /// Empty string if no preamble file exists.
    /// </summary>
    public string SystemPreamble { get; set; } = string.Empty;
}
