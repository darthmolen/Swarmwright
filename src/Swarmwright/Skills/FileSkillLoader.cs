using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Swarmwright.Templates;

namespace Swarmwright.Skills;

/// <summary>
/// Discovers and parses SKILL.md files from template-local and shared skill directories.
/// </summary>
public sealed partial class FileSkillLoader
{
    private readonly string templatesDirectory;
    private readonly ILogger<FileSkillLoader> logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileSkillLoader"/> class.
    /// </summary>
    /// <param name="templatesDirectory">The root templates directory.</param>
    /// <param name="loggerFactory">Optional logger factory.</param>
    public FileSkillLoader(string templatesDirectory, ILoggerFactory? loggerFactory = null)
    {
        this.templatesDirectory = templatesDirectory;
        this.logger = loggerFactory?.CreateLogger<FileSkillLoader>() ?? NullLogger<FileSkillLoader>.Instance;
    }

    /// <summary>
    /// Skill names come from worker frontmatter authored by template packagers. We
    /// reject anything that isn't a simple identifier to prevent Path.Combine from
    /// being tricked by separators or traversal sequences (e.g. <c>"../../etc"</c>).
    /// </summary>
    /// <returns>The compiled regex matching valid skill names.</returns>
    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_-]*$")]
    private static partial Regex ValidSkillNameRegex();

    /// <summary>
    /// Loads skill definitions for a worker, resolving from template-local then shared directories.
    /// </summary>
    /// <param name="templateKey">The template key identifying the template subdirectory.</param>
    /// <param name="requestedSkillNames">The skill names declared in worker frontmatter, or null.</param>
    /// <returns>The resolved skill definitions.</returns>
    public IReadOnlyList<SkillDefinition> LoadForWorker(
        string templateKey,
        IReadOnlyList<string>? requestedSkillNames)
    {
        if (requestedSkillNames is null or { Count: 0 })
        {
            return [];
        }

        var skills = new List<SkillDefinition>();
        foreach (var skillName in requestedSkillNames)
        {
            if (!ValidSkillNameRegex().IsMatch(skillName))
            {
                this.LogSkillNameInvalid(skillName, templateKey);
                continue;
            }

            var skill = this.TryLoadSkill(templateKey, skillName);
            if (skill is not null)
            {
                skills.Add(skill);
            }
            else
            {
                this.LogSkillNotFound(skillName, templateKey);
            }
        }

        return skills;
    }

    private SkillDefinition? TryLoadSkill(string templateKey, string skillName)
    {
        // Template-local first.
        var localPath = Path.Combine(this.templatesDirectory, templateKey, "skills", skillName, "SKILL.md");
        if (File.Exists(localPath))
        {
            return ParseSkillFile(localPath, skillName);
        }

        // Shared fallback.
        var sharedPath = Path.Combine(this.templatesDirectory, "skills", skillName, "SKILL.md");
        if (File.Exists(sharedPath))
        {
            return ParseSkillFile(sharedPath, skillName);
        }

        return null;
    }

    private static SkillDefinition ParseSkillFile(string filePath, string skillName)
    {
        var content = File.ReadAllText(filePath);
        var (yaml, body) = TemplateLoader.ParseFrontmatter(content);

        var name = yaml.TryGetValue("name", out var n) && n is not null
            ? n.ToString() ?? skillName
            : skillName;

        var description = yaml.TryGetValue("description", out var d) && d is not null
            ? d.ToString() ?? string.Empty
            : string.Empty;

        var directoryPath = Path.GetDirectoryName(filePath) ?? string.Empty;

        return new SkillDefinition(name, description, body.Trim(), directoryPath);
    }

    [LoggerMessage(
        EventId = 100,
        Level = LogLevel.Warning,
        Message = "Skill '{SkillName}' not found for template '{TemplateKey}' in template-local or shared directories.")]
    private partial void LogSkillNotFound(string skillName, string templateKey);

    [LoggerMessage(
        EventId = 101,
        Level = LogLevel.Warning,
        Message = "Skill name '{SkillName}' rejected for template '{TemplateKey}': must match ^[a-zA-Z0-9][a-zA-Z0-9_-]*$ (no path separators, no traversal sequences).")]
    private partial void LogSkillNameInvalid(string skillName, string templateKey);
}
