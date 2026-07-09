using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Swarmwright.Templates;

/// <summary>
/// Loads swarm team templates from the file system using YAML and markdown frontmatter.
/// </summary>
public partial class TemplateLoader : ITemplateLoader
{
    private readonly ILogger<TemplateLoader> logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TemplateLoader"/> class.
    /// </summary>
    /// <param name="templatesDirectory">The root directory containing template subdirectories.</param>
    /// <param name="logger">Optional logger instance.</param>
    public TemplateLoader(string templatesDirectory, ILogger<TemplateLoader>? logger = null)
    {
        this.TemplatesDirectory = templatesDirectory;
        this.logger = logger ?? NullLogger<TemplateLoader>.Instance;

        var fullPath = Path.GetFullPath(this.TemplatesDirectory);
        var exists = Directory.Exists(fullPath);
        this.LogTemplateDirectoryResolved(this.TemplatesDirectory, fullPath, exists);
    }

    /// <inheritdoc/>
    public string TemplatesDirectory { get; }

    /// <summary>
    /// Parses frontmatter from a markdown file, splitting YAML metadata from body content.
    /// Intentionally <c>internal</c> — reused by <c>FileSkillLoader</c> within this assembly
    /// and by tests via <c>InternalsVisibleTo</c>. Not part of the public API surface.
    /// </summary>
    /// <param name="content">The raw markdown content with optional YAML frontmatter.</param>
    /// <returns>A tuple of the parsed YAML dictionary and the remaining body text.</returns>
    internal static (Dictionary<string, object> Yaml, string Body) ParseFrontmatter(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var normalizedContent = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!normalizedContent.StartsWith("---", StringComparison.Ordinal))
        {
            return (new Dictionary<string, object>(), normalizedContent);
        }

        var endIndex = normalizedContent.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (endIndex < 0)
        {
            return (new Dictionary<string, object>(), normalizedContent);
        }

        var yamlSection = normalizedContent[4..endIndex];
        var body = normalizedContent[(endIndex + 4)..];

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        var yaml = deserializer.Deserialize<Dictionary<string, object>>(yamlSection)
            ?? new Dictionary<string, object>();

        return (yaml, body);
    }

    /// <summary>
    /// Validates that a template key is a safe identifier (alphanumeric plus
    /// underscore and dash). Rejecting anything else at this boundary prevents
    /// Path.Combine from being tricked by separators or traversal sequences
    /// (e.g. <c>"../evil"</c>) into loading files outside the templates directory.
    /// </summary>
    /// <returns>The compiled regex.</returns>
    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex ValidTemplateKeyRegex();

    /// <inheritdoc/>
    public LoadedTemplate Load(string templateKey)
    {
        ArgumentNullException.ThrowIfNull(templateKey);

        if (!ValidTemplateKeyRegex().IsMatch(templateKey))
        {
            throw new ArgumentException(
                $"Invalid template key '{templateKey}': must match ^[A-Za-z0-9_-]+$ (no path separators or traversal sequences).",
                nameof(templateKey));
        }

        var templatesRoot = Path.GetFullPath(this.TemplatesDirectory) + Path.DirectorySeparatorChar;
        var templateDir = Path.GetFullPath(Path.Combine(this.TemplatesDirectory, templateKey));

        if (!templateDir.StartsWith(templatesRoot, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Template key '{templateKey}' resolves outside the templates directory.",
                nameof(templateKey));
        }

        this.LogLoadingTemplate(templateKey, templateDir);
        if (!Directory.Exists(templateDir))
        {
            throw new DirectoryNotFoundException($"Template directory not found: {templateDir}");
        }

        var templateYamlPath = Path.Combine(templateDir, "_template.yaml");
        if (!File.Exists(templateYamlPath))
        {
            throw new FileNotFoundException($"Template YAML not found: {templateYamlPath}");
        }

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        var templateYaml = deserializer.Deserialize<Dictionary<string, object>>(
            File.ReadAllText(templateYamlPath));

        var template = new LoadedTemplate
        {
            Key = GetStringValue(templateYaml, "key"),
            Name = GetStringValue(templateYaml, "name"),
            Description = GetStringValue(templateYaml, "description"),
            GoalTemplate = GetStringValue(templateYaml, "goal_template"),
            AllowDefaultTools = GetBoolValue(templateYaml, "allow_default_tools", defaultValue: true),
            AllowSkillScripts = GetBoolValue(templateYaml, "allow_skill_scripts", defaultValue: false),
        };

        // Load shared system preamble (one level above the template directory).
        var systemPromptPath = Path.Combine(this.TemplatesDirectory, "system-prompt.md");
        if (File.Exists(systemPromptPath))
        {
            var (_, preambleBody) = ParseFrontmatter(File.ReadAllText(systemPromptPath));
            template.SystemPreamble = preambleBody.Trim();
        }

        // Load leader prompt.
        var leaderPath = Path.Combine(templateDir, "leader.md");
        if (File.Exists(leaderPath))
        {
            var (_, leaderBody) = ParseFrontmatter(File.ReadAllText(leaderPath));
            template.LeaderPrompt = leaderBody.Trim();
        }

        // Load worker agents.
        var workerFiles = Directory.GetFiles(templateDir, "worker-*.md");
        foreach (var workerFile in workerFiles)
        {
            var agentDef = ParseAgentFile(workerFile);
            template.Agents.Add(agentDef);
        }

        // Load synthesis prompt.
        var synthesisPath = Path.Combine(templateDir, "synthesis.md");
        if (File.Exists(synthesisPath))
        {
            var (_, synthesisBody) = ParseFrontmatter(File.ReadAllText(synthesisPath));
            template.SynthesisPrompt = synthesisBody.Trim();
        }

        return template;
    }

    /// <inheritdoc/>
    public IReadOnlyList<LoadedTemplate> LoadAll()
    {
        if (!Directory.Exists(this.TemplatesDirectory))
        {
            return [];
        }

        var templates = new List<LoadedTemplate>();
        foreach (var key in this.ListAvailable())
        {
            templates.Add(this.Load(key));
        }

        return templates.AsReadOnly();
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> ListAvailable()
    {
        var fullPath = Path.GetFullPath(this.TemplatesDirectory);
        if (!Directory.Exists(fullPath))
        {
            this.LogTemplateDirectoryNotFound(this.TemplatesDirectory, fullPath);
            return [];
        }

        var keys = new List<string>();
        foreach (var dir in Directory.GetDirectories(fullPath))
        {
            var templateYaml = Path.Combine(dir, "_template.yaml");
            if (File.Exists(templateYaml))
            {
                keys.Add(Path.GetFileName(dir));
            }
        }

        var keyNames = keys.ToArray();
        this.LogTemplatesDiscovered(keys.Count, fullPath, keyNames);
        return keys.AsReadOnly();
    }

    private static string GetStringValue(Dictionary<string, object> dict, string key)
    {
        if (dict.TryGetValue(key, out var value) && value is not null)
        {
            return value.ToString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static bool GetBoolValue(Dictionary<string, object> dict, string key, bool defaultValue)
    {
        if (!dict.TryGetValue(key, out var value) || value is null)
        {
            return defaultValue;
        }

        if (value is bool b)
        {
            return b;
        }

        if (value is string s && bool.TryParse(s, out var parsed))
        {
            return parsed;
        }

        return defaultValue;
    }

    private static bool? GetNullableBoolValue(Dictionary<string, object> dict, string key)
    {
        if (!dict.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        if (value is bool b)
        {
            return b;
        }

        if (value is string s && bool.TryParse(s, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static List<string>? GetStringList(Dictionary<string, object> dict, string key)
    {
        if (!dict.TryGetValue(key, out var value))
        {
            return null;
        }

        if (value is List<object> list)
        {
            return list.Select(item => item?.ToString() ?? string.Empty).ToList();
        }

        return null;
    }

    private static AgentDefinition ParseAgentFile(string filePath)
    {
        var content = File.ReadAllText(filePath);
        var (yaml, body) = ParseFrontmatter(content);

        var agent = new AgentDefinition
        {
            Name = GetStringValue(yaml, "name"),
            DisplayName = GetStringValue(yaml, "displayName"),
            Description = GetStringValue(yaml, "description"),
            Tools = GetStringList(yaml, "tools"),
            PromptTemplate = body.Trim(),
        };

        // Parse infer flag (defaults to true).
        if (yaml.TryGetValue("infer", out var inferValue))
        {
            if (inferValue is bool inferBool)
            {
                agent.Infer = inferBool;
            }
            else if (inferValue is string inferStr)
            {
                agent.Infer = !string.Equals(inferStr, "false", StringComparison.OrdinalIgnoreCase);
            }
        }

        // Parse skills list.
        agent.Skills = GetStringList(yaml, "skills");

        // Parse allow_default_tools (null = inherit from template).
        agent.AllowDefaultTools = GetNullableBoolValue(yaml, "allow_default_tools");

        // Parse mcp_endpoints — list of MCP endpoint names whose tools this agent loads.
        agent.McpEndpoints = GetStringList(yaml, "mcp_endpoints");

        // Parse custom_tools — list of custom tool names this agent is allowed to use.
        agent.CustomTools = GetStringList(yaml, "custom_tools");

        return agent;
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Template directory resolved: configured='{ConfiguredPath}', full='{FullPath}', exists={Exists}")]
    private partial void LogTemplateDirectoryResolved(string configuredPath, string fullPath, bool exists);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "Template directory not found: configured='{ConfiguredPath}', resolved='{FullPath}'")]
    private partial void LogTemplateDirectoryNotFound(string configuredPath, string fullPath);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Information,
        Message = "Discovered {Count} template(s) in '{Directory}': {Keys}")]
    private partial void LogTemplatesDiscovered(int count, string directory, string[] keys);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Debug,
        Message = "Loading template '{TemplateKey}' from '{Directory}'")]
    private partial void LogLoadingTemplate(string templateKey, string directory);
}
