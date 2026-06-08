using Swarmwright.Templates;
using FluentAssertions;

namespace Swarmwright.Tests.Templates;

/// <summary>
/// Tests for <see cref="TemplateLoader"/> parsing of the <c>allow_skill_scripts</c> field.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class TemplateLoaderSkillScriptsTests : IDisposable
{
    private readonly string testDir;

    /// <summary>
    /// Initializes a new instance of the <see cref="TemplateLoaderSkillScriptsTests"/> class.
    /// </summary>
    public TemplateLoaderSkillScriptsTests()
    {
        this.testDir = Path.Combine(Path.GetTempPath(), "template-scripts-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.testDir);
    }

    /// <summary>
    /// Disposes managed resources.
    /// </summary>
    public void Dispose()
    {
        if (Directory.Exists(this.testDir))
        {
            Directory.Delete(this.testDir, recursive: true);
        }
    }

    /// <summary>
    /// When <c>allow_skill_scripts: true</c> is set in _template.yaml, the loaded
    /// template reflects that value.
    /// </summary>
    [TestMethod]
    public void Load_ParsesAllowSkillScriptsTrue()
    {
        this.CreateTemplate("scripts-on", allowSkillScripts: true);

        var loader = new TemplateLoader(this.testDir);

        var template = loader.Load("scripts-on");

        template.AllowSkillScripts.Should().BeTrue();
    }

    /// <summary>
    /// When <c>allow_skill_scripts</c> is not present in _template.yaml, the property
    /// defaults to false.
    /// </summary>
    [TestMethod]
    public void Load_DefaultsAllowSkillScriptsToFalse_WhenFieldMissing()
    {
        this.CreateTemplate("no-scripts-field", allowSkillScripts: null);

        var loader = new TemplateLoader(this.testDir);

        var template = loader.Load("no-scripts-field");

        template.AllowSkillScripts.Should().BeFalse();
    }

    private void CreateTemplate(string key, bool? allowSkillScripts)
    {
        var templateDir = Path.Combine(this.testDir, key);
        Directory.CreateDirectory(templateDir);

        var yaml = $"""
            key: {key}
            name: Test
            description: Test template
            goal_template: "do: it"
            """;

        if (allowSkillScripts.HasValue)
        {
            yaml += $"\nallow_skill_scripts: {allowSkillScripts.Value.ToString().ToLowerInvariant()}";
        }

        File.WriteAllText(Path.Combine(templateDir, "_template.yaml"), yaml);
        File.WriteAllText(Path.Combine(templateDir, "leader.md"), "# Leader");
        File.WriteAllText(Path.Combine(templateDir, "synthesis.md"), "# Synthesis");
    }
}
