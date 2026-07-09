using Microsoft.Extensions.AI;

namespace Swarmwright.Skills;

/// <summary>
/// Provides skill-related prompt fragments and progressive-disclosure tools for a worker agent.
/// </summary>
public interface ISkillsProvider
{
    /// <summary>Gets the resolved skill definitions.</summary>
    public IReadOnlyList<SkillDefinition> Skills { get; }

    /// <summary>
    /// Returns the markdown prompt fragment listing available skills.
    /// Empty string when no skills are loaded.
    /// </summary>
    /// <returns>The skills prompt fragment.</returns>
    public string GetPromptFragment();

    /// <summary>
    /// Returns the progressive-disclosure AI tools (load_skill, read_skill_resource,
    /// and optionally run_skill_script). Empty list when no skills are loaded.
    /// </summary>
    /// <returns>The skill tools.</returns>
    public IReadOnlyList<AITool> GetTools();
}
