using Swarmwright.Templates;
using FluentAssertions;

namespace Swarmwright.Tests.Templates;

/// <summary>
/// Tests for <see cref="TemplateLoader"/> parsing of the <c>custom_tools</c>
/// frontmatter field into <see cref="AgentDefinition.CustomTools"/>.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class TemplateLoaderCustomToolsTests : IDisposable
{
    private readonly string testDir;

    /// <summary>
    /// Initializes a new instance of the <see cref="TemplateLoaderCustomToolsTests"/> class.
    /// </summary>
    public TemplateLoaderCustomToolsTests()
    {
        this.testDir = Path.Combine(Path.GetTempPath(), "template-customtools-tests-" + Guid.NewGuid().ToString("N"));
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
    /// A worker with <c>custom_tools</c> frontmatter has that list reflected
    /// in <see cref="AgentDefinition.CustomTools"/>.
    /// </summary>
    [TestMethod]
    public void Load_ParsesCustomToolsList()
    {
        var templateDir = Path.Combine(this.testDir, "custom-tools-template");
        Directory.CreateDirectory(templateDir);

        const string templateYaml = """
            key: custom-tools-template
            name: CT
            description: Test
            goal_template: "do it"
            """;

        const string workerMd = """
            ---
            name: db-analyst
            displayName: DB Analyst
            description: Queries the DB
            custom_tools:
              - query_db
              - run_report
            ---

            # Worker
            """;

        File.WriteAllText(Path.Combine(templateDir, "_template.yaml"), templateYaml);
        File.WriteAllText(Path.Combine(templateDir, "leader.md"), "# Leader");
        File.WriteAllText(Path.Combine(templateDir, "synthesis.md"), "# Synthesis");
        File.WriteAllText(Path.Combine(templateDir, "worker-db-analyst.md"), workerMd);

        var loader = new TemplateLoader(this.testDir);

        var template = loader.Load("custom-tools-template");

        var worker = template.Agents.Single(a => a.Name == "db-analyst");
        worker.CustomTools.Should().NotBeNull();
        worker.CustomTools.Should().BeEquivalentTo("query_db", "run_report");
    }

    /// <summary>
    /// A worker with no <c>custom_tools</c> frontmatter has a null or empty
    /// <see cref="AgentDefinition.CustomTools"/> property.
    /// </summary>
    [TestMethod]
    public void Load_CustomToolsNullWhenFieldMissing()
    {
        var templateDir = Path.Combine(this.testDir, "no-custom-tools");
        Directory.CreateDirectory(templateDir);

        const string templateYaml = """
            key: no-custom-tools
            name: NC
            description: Test
            goal_template: "do it"
            """;

        const string workerMd = """
            ---
            name: plain
            displayName: Plain
            description: No custom tools
            ---

            # Worker
            """;

        File.WriteAllText(Path.Combine(templateDir, "_template.yaml"), templateYaml);
        File.WriteAllText(Path.Combine(templateDir, "leader.md"), "# Leader");
        File.WriteAllText(Path.Combine(templateDir, "synthesis.md"), "# Synthesis");
        File.WriteAllText(Path.Combine(templateDir, "worker-plain.md"), workerMd);

        var loader = new TemplateLoader(this.testDir);

        var template = loader.Load("no-custom-tools");

        var worker = template.Agents.Single(a => a.Name == "plain");
        (worker.CustomTools is null || worker.CustomTools.Count == 0).Should().BeTrue();
    }
}
