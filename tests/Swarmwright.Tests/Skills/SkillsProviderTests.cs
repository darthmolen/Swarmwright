using System.ComponentModel;
using Swarmwright.Skills;
using FluentAssertions;
using Microsoft.Extensions.AI;

namespace Swarmwright.Tests.Skills;

/// <summary>
/// Tests for <see cref="SkillsProvider"/>.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SkillsProviderTests : IDisposable
{
    private readonly string testDir;

    /// <summary>
    /// Initializes a new instance of the <see cref="SkillsProviderTests"/> class.
    /// </summary>
    public SkillsProviderTests()
    {
        this.testDir = Path.Combine(Path.GetTempPath(), "skills-provider-tests-" + Guid.NewGuid().ToString("N"));
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
    /// The prompt fragment lists all skill names and descriptions.
    /// </summary>
    [TestMethod]
    public void GetPromptFragment_ListsAllSkillNamesAndDescriptions()
    {
        var skills = new List<SkillDefinition>
        {
            new("azure-architect", "Cloud architecture frameworks.", "Body A.", this.testDir),
            new("security-expert", "Security analysis.", "Body B.", this.testDir),
        };
        var provider = new SkillsProvider(skills, allowScripts: false);

        var fragment = provider.GetPromptFragment();

        fragment.Should().Contain("## Available Skills");
        fragment.Should().Contain("azure-architect");
        fragment.Should().Contain("Cloud architecture frameworks.");
        fragment.Should().Contain("security-expert");
        fragment.Should().Contain("Security analysis.");
    }

    /// <summary>
    /// With scripts disabled, only load_skill and read_skill_resource tools are returned.
    /// </summary>
    [TestMethod]
    public void GetTools_ReturnsLoadSkillAndReadResource()
    {
        var skills = new List<SkillDefinition>
        {
            new("test-skill", "A test skill.", "Body.", this.testDir),
        };
        var provider = new SkillsProvider(skills, allowScripts: false);

        var tools = provider.GetTools();

        tools.Select(t => t.Name).Should().Contain("load_skill");
        tools.Select(t => t.Name).Should().Contain("read_skill_resource");
    }

    /// <summary>
    /// When scripts are disabled, run_skill_script tool is not included.
    /// </summary>
    [TestMethod]
    public void GetTools_ExcludesRunSkillScript_WhenAllowScriptsFalse()
    {
        var skills = new List<SkillDefinition>
        {
            new("test-skill", "A test skill.", "Body.", this.testDir),
        };
        var provider = new SkillsProvider(skills, allowScripts: false);

        var tools = provider.GetTools();

        tools.Select(t => t.Name).Should().NotContain("run_skill_script");
    }

    /// <summary>
    /// When scripts are enabled, run_skill_script tool is included.
    /// </summary>
    [TestMethod]
    public void GetTools_IncludesRunSkillScript_WhenAllowScriptsTrue()
    {
        var skills = new List<SkillDefinition>
        {
            new("test-skill", "A test skill.", "Body.", this.testDir),
        };
        var provider = new SkillsProvider(skills, allowScripts: true);

        var tools = provider.GetTools();

        tools.Select(t => t.Name).Should().Contain("run_skill_script");
    }

    /// <summary>
    /// The run_skill_script description honestly signals the v1 diagnostic-only behavior so
    /// LLMs reading descriptions know not to expect real execution. If this test fails, either:
    /// (a) real execution has been implemented and the description should no longer say DIAGNOSTIC, or
    /// (b) the honest-description contract has regressed.
    /// </summary>
    [TestMethod]
    public void GetTools_RunSkillScriptDescription_SignalsDiagnosticOnlyInV1()
    {
        var skills = new List<SkillDefinition>
        {
            new("test-skill", "A test skill.", "Body.", this.testDir),
        };
        var provider = new SkillsProvider(skills, allowScripts: true);

        var tools = provider.GetTools();
        var runScript = tools.OfType<AIFunction>().First(t => t.Name == "run_skill_script");

        runScript.Description.Should().Contain("DIAGNOSTIC", "v1 description must flag that execution is not implemented");
        runScript.Description.Should().NotBe("Executes a script from a skill's scripts directory.", "old misleading description must not resurface");
    }

    /// <summary>
    /// The load_skill tool returns the skill body when invoked with a valid skill name.
    /// </summary>
    [TestMethod]
    public async Task LoadSkill_ReturnsSkillBody_WhenInvoked()
    {
        var skills = new List<SkillDefinition>
        {
            new("arch", "Architecture.", "Use Well-Architected Framework.", this.testDir),
        };
        var provider = new SkillsProvider(skills, allowScripts: false);

        var tools = provider.GetTools();
        var loadSkill = tools.OfType<AIFunction>().First(t => t.Name == "load_skill");

        var args = new AIFunctionArguments(new Dictionary<string, object?> { ["skillName"] = "arch" });
        var result = await loadSkill.InvokeAsync(args);

        result.Should().NotBeNull();
        result!.ToString().Should().Contain("Well-Architected Framework");
    }

    /// <summary>
    /// The load_skill tool returns an error message when invoked with an unknown skill name.
    /// </summary>
    [TestMethod]
    public async Task LoadSkill_ReturnsError_WhenSkillNotFound()
    {
        var skills = new List<SkillDefinition>
        {
            new("arch", "Architecture.", "Body.", this.testDir),
        };
        var provider = new SkillsProvider(skills, allowScripts: false);

        var tools = provider.GetTools();
        var loadSkill = tools.OfType<AIFunction>().First(t => t.Name == "load_skill");

        var args = new AIFunctionArguments(new Dictionary<string, object?> { ["skillName"] = "nonexistent" });
        var result = await loadSkill.InvokeAsync(args);

        result.Should().NotBeNull();
        result!.ToString().Should().Contain("Error");
        result.ToString().Should().Contain("nonexistent");
    }

    /// <summary>
    /// The read_skill_resource tool returns file content for a valid resource.
    /// </summary>
    [TestMethod]
    public async Task ReadSkillResource_ReturnsContent_WhenResourceExists()
    {
        var refsDir = Path.Combine(this.testDir, "references");
        Directory.CreateDirectory(refsDir);
        File.WriteAllText(Path.Combine(refsDir, "table.md"), "| Unit | Factor |\n| km | 1.609 |");

        var skills = new List<SkillDefinition>
        {
            new("converter", "Conversion.", "Body.", this.testDir),
        };
        var provider = new SkillsProvider(skills, allowScripts: false);

        var tools = provider.GetTools();
        var readResource = tools.OfType<AIFunction>().First(t => t.Name == "read_skill_resource");

        var args = new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["skillName"] = "converter",
            ["resourceName"] = "table.md",
        });
        var result = await readResource.InvokeAsync(args);

        result.Should().NotBeNull();
        result!.ToString().Should().Contain("1.609");
    }

    /// <summary>
    /// The read_skill_resource tool rejects path traversal attempts.
    /// </summary>
    [TestMethod]
    public async Task ReadSkillResource_RejectsPathTraversal()
    {
        var skills = new List<SkillDefinition>
        {
            new("test-skill", "Test.", "Body.", this.testDir),
        };
        var provider = new SkillsProvider(skills, allowScripts: false);

        var tools = provider.GetTools();
        var readResource = tools.OfType<AIFunction>().First(t => t.Name == "read_skill_resource");

        var args = new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["skillName"] = "test-skill",
            ["resourceName"] = "../../etc/passwd",
        });
        var result = await readResource.InvokeAsync(args);

        result.Should().NotBeNull();
        result!.ToString().Should().Contain("Error");
    }

    /// <summary>
    /// The run_skill_script tool rejects path traversal attempts.
    /// </summary>
    [TestMethod]
    public async Task RunSkillScript_RejectsPathTraversal()
    {
        var skills = new List<SkillDefinition>
        {
            new("test-skill", "Test.", "Body.", this.testDir),
        };
        var provider = new SkillsProvider(skills, allowScripts: true);

        var tools = provider.GetTools();
        var runScript = tools.OfType<AIFunction>().First(t => t.Name == "run_skill_script");

        var args = new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["skillName"] = "test-skill",
            ["scriptName"] = "../../etc/shadow",
            ["arguments"] = null,
        });
        var result = await runScript.InvokeAsync(args);

        result.Should().NotBeNull();
        result!.ToString().Should().Contain("Error");
    }

    /// <summary>
    /// Two calls to GetTools() return the same cached list reference.
    /// </summary>
    [TestMethod]
    public void GetTools_CachesResult_AcrossCalls()
    {
        var skills = new List<SkillDefinition>
        {
            new("test-skill", "Test.", "Body.", this.testDir),
        };
        var provider = new SkillsProvider(skills, allowScripts: false);

        var first = provider.GetTools();
        var second = provider.GetTools();

        second.Should().BeSameAs(first);
    }
}
