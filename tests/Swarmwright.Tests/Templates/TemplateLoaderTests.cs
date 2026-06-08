using Swarmwright.Templates;
using FluentAssertions;

namespace Swarmwright.Tests.Templates;

[TestClass]
public class TemplateLoaderTests
{
    private static string TestDataPath => Path.Combine(
        AppContext.BaseDirectory,
        "TestData",
        "templates");

    [TestMethod]
    public void ParseFrontmatter_SplitsYamlFromBody()
    {
        // Arrange
        var content = "---\nname: test-agent\ndescription: A test agent\n---\n\n# Test Agent\n\nYou are a test agent.";

        // Act
        var (yaml, body) = TemplateLoader.ParseFrontmatter(content);

        // Assert
        yaml.Should().ContainKey("name");
        yaml["name"].Should().Be("test-agent");
        yaml["description"].Should().Be("A test agent");
        body.Trim().Should().Contain("# Test Agent");
    }

    [TestMethod]
    public void Load_DiscoversWorkerFiles()
    {
        // Arrange
        var loader = new TemplateLoader(TestDataPath);

        // Act
        var template = loader.Load("deep-research");

        // Assert
        template.Agents.Should().HaveCount(3);
    }

    [TestMethod]
    public void Load_ParsesAgentDefinition_WithToolsAndSkills()
    {
        // Arrange
        var loader = new TemplateLoader(TestDataPath);

        // Act
        var template = loader.Load("deep-research");
        var primaryResearcher = template.Agents.First(a => a.Name == "primary-researcher");

        // Assert
        primaryResearcher.Tools.Should().NotBeNull();
        primaryResearcher.Tools.Should().HaveCount(4);
        primaryResearcher.Tools.Should().Contain("task_update");
        primaryResearcher.Infer.Should().BeFalse();
    }

    [TestMethod]
    public void Load_MissingTemplateYaml_Throws()
    {
        // Arrange
        var emptyDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(emptyDir);

        try
        {
            var loader = new TemplateLoader(emptyDir);

            // Act
            var act = () => loader.Load("nonexistent");

            // Assert
            act.Should().Throw<DirectoryNotFoundException>();
        }
        finally
        {
            Directory.Delete(emptyDir, true);
        }
    }

    [TestMethod]
    public void ListAvailable_ReturnsAllTemplateKeys()
    {
        // Arrange
        var loader = new TemplateLoader(TestDataPath);

        // Act
        var keys = loader.ListAvailable();

        // Assert
        keys.Should().Contain("deep-research");
    }

    // -----------------------------------------------------------------------
    // allow_default_tools — template-level + per-worker
    // -----------------------------------------------------------------------

    [TestMethod]
    public void Load_DefaultsAllowDefaultToolsToTrue_WhenNotSpecified()
    {
        var loader = new TemplateLoader(TestDataPath);

        var template = loader.Load("deep-research");

        template.AllowDefaultTools.Should().BeTrue();
    }

    [TestMethod]
    public void Load_ParsesAllowDefaultTools_FromTemplateYaml()
    {
        // Arrange — write a custom template with allow_default_tools: false
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var templateDir = Path.Combine(tempDir, "no-tools");
        Directory.CreateDirectory(templateDir);
        File.WriteAllText(
            Path.Combine(templateDir, "_template.yaml"),
            "key: no-tools\nname: No Tools\ndescription: x\ngoal_template: \"do {user_input}\"\nallow_default_tools: false\n");
        File.WriteAllText(Path.Combine(templateDir, "leader.md"), "Leader prompt body.");

        try
        {
            var loader = new TemplateLoader(tempDir);

            var template = loader.Load("no-tools");

            template.AllowDefaultTools.Should().BeFalse();
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public void Load_ParsesAllowDefaultTools_FromWorkerFrontmatter()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var templateDir = Path.Combine(tempDir, "mixed");
        Directory.CreateDirectory(templateDir);
        File.WriteAllText(
            Path.Combine(templateDir, "_template.yaml"),
            "key: mixed\nname: Mixed\ndescription: x\ngoal_template: \"{user_input}\"\nallow_default_tools: true\n");
        File.WriteAllText(Path.Combine(templateDir, "leader.md"), "Leader.");
        File.WriteAllText(
            Path.Combine(templateDir, "worker-restricted.md"),
            "---\nname: restricted\ndisplayName: Restricted\ndescription: A locked-down worker\nallow_default_tools: false\n---\n\nBody.");

        try
        {
            var loader = new TemplateLoader(tempDir);

            var template = loader.Load("mixed");
            var worker = template.Agents.First(a => a.Name == "restricted");

            worker.AllowDefaultTools.Should().BeFalse();
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public void Load_WorkerAllowDefaultTools_NullByDefault()
    {
        // The deep-research workers don't set allow_default_tools, so it should be null
        // (which means inherit from template).
        var loader = new TemplateLoader(TestDataPath);

        var template = loader.Load("deep-research");
        var primaryResearcher = template.Agents.First(a => a.Name == "primary-researcher");

        primaryResearcher.AllowDefaultTools.Should().BeNull();
    }

    // -----------------------------------------------------------------------
    // system-prompt.md — shared preamble loading
    // -----------------------------------------------------------------------

    [TestMethod]
    public void Load_LoadsSystemPreamble_WhenSystemPromptMdExists()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var templateDir = Path.Combine(tempDir, "with-preamble");
        Directory.CreateDirectory(templateDir);
        File.WriteAllText(
            Path.Combine(templateDir, "_template.yaml"),
            "key: with-preamble\nname: With Preamble\ndescription: x\ngoal_template: \"{user_input}\"\n");
        File.WriteAllText(Path.Combine(templateDir, "leader.md"), "Leader.");
        File.WriteAllText(
            Path.Combine(tempDir, "system-prompt.md"),
            "---\nname: system-prompt\n---\n\n## System Coordination Protocol\n\nFollow these rules.");

        try
        {
            var loader = new TemplateLoader(tempDir);

            var template = loader.Load("with-preamble");

            template.SystemPreamble.Should().Contain("System Coordination Protocol");
            template.SystemPreamble.Should().Contain("Follow these rules.");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public void Load_SystemPreamble_EmptyWhenSystemPromptMdMissing()
    {
        var loader = new TemplateLoader(TestDataPath);

        var template = loader.Load("deep-research");

        // No system-prompt.md in the test fixture
        template.SystemPreamble.Should().BeEmpty();
    }

    // -----------------------------------------------------------------------
    // Template-key input validation (T2.2 — path traversal guard)
    // -----------------------------------------------------------------------

    [TestMethod]
    public void Load_RejectsTraversalTemplateKey()
    {
        var loader = new TemplateLoader(TestDataPath);

        var act = () => loader.Load("../evil");

        act.Should().Throw<ArgumentException>()
            .WithMessage("*template*")
            .Which.ParamName.Should().Be("templateKey");
    }

    [TestMethod]
    public void Load_RejectsAbsoluteWindowsPathTemplateKey()
    {
        var loader = new TemplateLoader(TestDataPath);

        var act = () => loader.Load("C:\\Windows");

        act.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("templateKey");
    }

    [TestMethod]
    public void Load_RejectsAbsoluteUnixPathTemplateKey()
    {
        var loader = new TemplateLoader(TestDataPath);

        var act = () => loader.Load("/etc");

        act.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("templateKey");
    }

    [TestMethod]
    public void Load_RejectsTemplateKeyWithPathSeparator()
    {
        var loader = new TemplateLoader(TestDataPath);

        var act = () => loader.Load("foo/bar");

        act.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("templateKey");
    }

    [TestMethod]
    public void Load_AcceptsValidTemplateKey()
    {
        var loader = new TemplateLoader(TestDataPath);

        var act = () => loader.Load("deep-research");

        act.Should().NotThrow();
    }
}
