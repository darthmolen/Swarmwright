using Swarmwright.Skills;
using FluentAssertions;

namespace Swarmwright.Tests.Skills;

/// <summary>
/// Tests for <see cref="FileSkillLoader"/> discovery and parsing of SKILL.md files.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class FileSkillLoaderTests : IDisposable
{
    private readonly string testDir;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileSkillLoaderTests"/> class.
    /// </summary>
    public FileSkillLoaderTests()
    {
        this.testDir = Path.Combine(Path.GetTempPath(), "skill-loader-tests-" + Guid.NewGuid().ToString("N"));
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
    /// When requested skills list is null, the loader returns an empty list.
    /// </summary>
    [TestMethod]
    public void LoadForWorker_ReturnsEmpty_WhenRequestedSkillsIsNull()
    {
        var loader = new FileSkillLoader(this.testDir);

        var result = loader.LoadForWorker("some-template", null);

        result.Should().BeEmpty();
    }

    /// <summary>
    /// When requested skills list is empty, the loader returns an empty list.
    /// </summary>
    [TestMethod]
    public void LoadForWorker_ReturnsEmpty_WhenRequestedSkillsIsEmpty()
    {
        var loader = new FileSkillLoader(this.testDir);

        var result = loader.LoadForWorker("some-template", []);

        result.Should().BeEmpty();
    }

    /// <summary>
    /// Skills are loaded from the template-local skills directory.
    /// </summary>
    [TestMethod]
    public void LoadForWorker_LoadsFromTemplateLocalDirectory()
    {
        this.CreateSkillFile(
            "my-template/skills/azure-architect",
            "azure-architect",
            "Cloud architecture frameworks.",
            "Use Well-Architected Framework principles.");

        var loader = new FileSkillLoader(this.testDir);

        var result = loader.LoadForWorker("my-template", ["azure-architect"]);

        result.Should().HaveCount(1);
        var skill = result[0];
        skill.Name.Should().Be("azure-architect");
        skill.Description.Should().Be("Cloud architecture frameworks.");
        skill.Body.Should().Contain("Well-Architected Framework");
    }

    /// <summary>
    /// When a skill is not found in the template-local directory, the loader
    /// falls back to the shared skills directory.
    /// </summary>
    [TestMethod]
    public void LoadForWorker_FallsBackToSharedDirectory()
    {
        this.CreateSkillFile(
            "skills/common-skill",
            "common-skill",
            "A shared skill.",
            "Shared instructions.");

        // Create template dir without the skill.
        var templateDir = Path.Combine(this.testDir, "my-template");
        Directory.CreateDirectory(templateDir);
        File.WriteAllText(Path.Combine(templateDir, "_template.yaml"), "key: my-template\nname: Test\ndescription: test\ngoal_template: do");

        var loader = new FileSkillLoader(this.testDir);

        var result = loader.LoadForWorker("my-template", ["common-skill"]);

        result.Should().HaveCount(1);
        result[0].Name.Should().Be("common-skill");
        result[0].Description.Should().Be("A shared skill.");
    }

    /// <summary>
    /// Template-local skills take precedence over shared skills with the same name.
    /// </summary>
    [TestMethod]
    public void LoadForWorker_PrefersTemplateLocalOverShared()
    {
        this.CreateSkillFile(
            "my-template/skills/overlap",
            "overlap",
            "Template-local version.",
            "Local body.");

        this.CreateSkillFile(
            "skills/overlap",
            "overlap",
            "Shared version.",
            "Shared body.");

        var loader = new FileSkillLoader(this.testDir);

        var result = loader.LoadForWorker("my-template", ["overlap"]);

        result.Should().HaveCount(1);
        result[0].Description.Should().Be("Template-local version.");
        result[0].Body.Should().Contain("Local body");
    }

    /// <summary>
    /// Unknown skill names are skipped with a warning rather than throwing.
    /// </summary>
    [TestMethod]
    public void LoadForWorker_SkipsUnknownSkill()
    {
        this.CreateSkillFile(
            "my-template/skills/real-skill",
            "real-skill",
            "A real skill.",
            "Real instructions.");

        var loader = new FileSkillLoader(this.testDir);

        var result = loader.LoadForWorker("my-template", ["real-skill", "nonexistent-skill"]);

        result.Should().HaveCount(1);
        result[0].Name.Should().Be("real-skill");
    }

    /// <summary>
    /// The loader correctly parses YAML frontmatter into Name, Description, and Body.
    /// </summary>
    [TestMethod]
    public void LoadForWorker_ParsesFrontmatter_NameDescriptionBody()
    {
        var body = """
            # Azure Architect Skill

            ## Decision Framework

            1. Evaluate requirements
            2. Apply Well-Architected principles
            3. Recommend architecture
            """;

        this.CreateSkillFile(
            "my-template/skills/arch",
            "arch",
            "Architecture decision framework.",
            body);

        var loader = new FileSkillLoader(this.testDir);

        var result = loader.LoadForWorker("my-template", ["arch"]);

        result.Should().HaveCount(1);
        var skill = result[0];
        skill.Name.Should().Be("arch");
        skill.Description.Should().Be("Architecture decision framework.");
        skill.Body.Should().Contain("# Azure Architect Skill");
        skill.Body.Should().Contain("Well-Architected principles");
        skill.DirectoryPath.Should().EndWith(Path.Combine("my-template", "skills", "arch"));
    }

    private void CreateSkillFile(string relativeDirPath, string name, string description, string body)
    {
        var dir = Path.Combine(this.testDir, relativeDirPath);
        Directory.CreateDirectory(dir);
        var content = $"""
            ---
            name: {name}
            description: {description}
            ---

            {body}
            """;
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), content);
    }
}
