using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.AI;

namespace Swarmwright.Skills;

/// <summary>
/// Default <see cref="ISkillsProvider"/> implementation that builds progressive-disclosure
/// tools via <see cref="AIFunctionFactory"/>.
/// </summary>
public sealed class SkillsProvider : ISkillsProvider
{
    private readonly bool allowScripts;
    private readonly Dictionary<string, SkillDefinition> skillsByName;
    private readonly Lazy<IReadOnlyList<AITool>> cachedTools;
    private readonly Lazy<string> cachedPromptFragment;

    /// <summary>
    /// Initializes a new instance of the <see cref="SkillsProvider"/> class.
    /// </summary>
    /// <param name="skills">The resolved skill definitions.</param>
    /// <param name="allowScripts">Whether script execution is allowed.</param>
    public SkillsProvider(IReadOnlyList<SkillDefinition> skills, bool allowScripts)
    {
        this.Skills = skills;
        this.allowScripts = allowScripts;
        this.skillsByName = skills.ToDictionary(s => s.Name, StringComparer.Ordinal);
        this.cachedTools = new Lazy<IReadOnlyList<AITool>>(this.BuildTools);
        this.cachedPromptFragment = new Lazy<string>(this.BuildPromptFragment);
    }

    /// <inheritdoc/>
    public IReadOnlyList<SkillDefinition> Skills { get; }

    /// <inheritdoc/>
    public string GetPromptFragment() => this.cachedPromptFragment.Value;

    /// <inheritdoc/>
    public IReadOnlyList<AITool> GetTools() => this.cachedTools.Value;

    private string BuildPromptFragment()
    {
        if (this.Skills.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.AppendLine("## Available Skills");
        sb.AppendLine();
        sb.AppendLine("You have access to the following skills. Use `load_skill` to load a skill's full instructions when needed.");
        sb.AppendLine();

        foreach (var skill in this.Skills)
        {
            sb.Append("- **").Append(skill.Name).Append("**: ").AppendLine(skill.Description);
        }

        return sb.ToString().TrimEnd();
    }

    private List<AITool> BuildTools()
    {
        if (this.Skills.Count == 0)
        {
            return [];
        }

        var tools = new List<AITool>
        {
            this.CreateLoadSkillTool(),
            this.CreateReadSkillResourceTool(),
        };

        if (this.allowScripts)
        {
            tools.Add(this.CreateRunSkillScriptTool());
        }

        return tools;
    }

    private AIFunction CreateLoadSkillTool()
    {
        return AIFunctionFactory.Create(
            (
                [Description("The name of the skill to load.")] string skillName) =>
            {
                if (this.skillsByName.TryGetValue(skillName, out var skill))
                {
                    return skill.Body;
                }

                return $"Error: skill '{skillName}' not found. Available skills: {string.Join(", ", this.skillsByName.Keys)}";
            },
            "load_skill",
            "Loads the full instructions for a skill by name. Use this to get detailed guidance before applying a skill's framework.");
    }

    private AIFunction CreateReadSkillResourceTool()
    {
        return AIFunctionFactory.Create(
            (
                [Description("The name of the skill whose resource to read.")] string skillName,
                [Description("The resource file name within the skill's references directory.")] string resourceName) =>
            {
                if (!this.skillsByName.TryGetValue(skillName, out var skill))
                {
                    return $"Error: skill '{skillName}' not found.";
                }

                var resourcePath = Path.Combine(skill.DirectoryPath, "references", resourceName);
                var normalized = Path.GetFullPath(resourcePath);
                var skillDirNormalized = Path.GetFullPath(skill.DirectoryPath) + Path.DirectorySeparatorChar;

                if (!normalized.StartsWith(skillDirNormalized, StringComparison.Ordinal))
                {
                    return "Error: resource path is outside the skill directory.";
                }

                if (!File.Exists(normalized))
                {
                    return $"Error: resource '{resourceName}' not found in skill '{skillName}'.";
                }

                return File.ReadAllText(normalized);
            },
            "read_skill_resource",
            "Reads a reference file from a skill's references directory.");
    }

    private AIFunction CreateRunSkillScriptTool()
    {
        return AIFunctionFactory.Create(
            (
                [Description("The name of the skill whose script to run.")] string skillName,
                [Description("The script file name within the skill's scripts directory.")] string scriptName,
                [Description("Optional arguments to pass to the script.")] string? arguments) =>
            {
                if (!this.skillsByName.TryGetValue(skillName, out var skill))
                {
                    return $"Error: skill '{skillName}' not found.";
                }

                var scriptPath = Path.Combine(skill.DirectoryPath, "scripts", scriptName);
                var normalized = Path.GetFullPath(scriptPath);
                var skillDirNormalized = Path.GetFullPath(skill.DirectoryPath) + Path.DirectorySeparatorChar;

                if (!normalized.StartsWith(skillDirNormalized, StringComparison.Ordinal))
                {
                    return "Error: script path is outside the skill directory.";
                }

                if (!File.Exists(normalized))
                {
                    return $"Error: script '{scriptName}' not found in skill '{skillName}'.";
                }

                return $"[NOT EXECUTED — v1 diagnostic only] Resolved script path: {normalized}. Arguments: {arguments ?? "(none)"}. In-process execution is deferred to the SDK-backed provider (see planning/backlog/SWARM-SKILLS-SDK-PROVIDER.md).";
            },
            "run_skill_script",
            "[v1 DIAGNOSTIC ONLY] Validates the resolved script path but does NOT execute the script. Real execution lands when the SDK-backed provider ships. Do not call this tool expecting computation — use it only to confirm the script file is reachable.");
    }
}
