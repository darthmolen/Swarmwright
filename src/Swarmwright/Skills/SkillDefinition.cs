namespace Swarmwright.Skills;

/// <summary>
/// Immutable record representing a parsed SKILL.md file.
/// </summary>
/// <param name="Name">The skill name from frontmatter.</param>
/// <param name="Description">The skill description from frontmatter.</param>
/// <param name="Body">The markdown body below the frontmatter fence.</param>
/// <param name="DirectoryPath">The absolute path to the skill folder.</param>
public sealed record SkillDefinition(
    string Name,
    string Description,
    string Body,
    string DirectoryPath);
