using Swarmwright.Templates;
using FluentAssertions;

namespace Swarmwright.Tests.Templates;

/// <summary>
/// Tests for <see cref="TemplateLoader"/> parsing of the <c>mcp_endpoints</c> frontmatter
/// field into <see cref="AgentDefinition.McpEndpoints"/>.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class TemplateLoaderMcpEndpointsTests : IDisposable
{
    private readonly string testDir;

    /// <summary>
    /// Initializes a new instance of the <see cref="TemplateLoaderMcpEndpointsTests"/> class.
    /// </summary>
    public TemplateLoaderMcpEndpointsTests()
    {
        this.testDir = Path.Combine(Path.GetTempPath(), "template-mcp-tests-" + Guid.NewGuid().ToString("N"));
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
    /// A worker with <c>mcp_endpoints</c> frontmatter must have that list reflected
    /// in its parsed <see cref="AgentDefinition.McpEndpoints"/>.
    /// </summary>
    [TestMethod]
    public void Load_WhenAgentDeclaresMcpEndpoints_PopulatesAgentDefinition()
    {
        var templateDir = Path.Combine(this.testDir, "mcp-test-template");
        Directory.CreateDirectory(templateDir);

        const string templateYaml = """
            key: mcp-test-template
            name: MCP Test Template
            description: For testing
            goal_template: "do: {user_input}"
            """;

        const string workerMd = """
            ---
            name: researcher
            displayName: Researcher
            description: Researches Microsoft stuff
            mcp_endpoints:
              - learn-microsoft
              - another-server
            ---

            # Researcher

            You are a researcher.
            """;

        File.WriteAllText(Path.Combine(templateDir, "_template.yaml"), templateYaml);
        File.WriteAllText(Path.Combine(templateDir, "leader.md"), "# Leader\n\nYou are the leader.");
        File.WriteAllText(Path.Combine(templateDir, "synthesis.md"), "# Synthesis\n\nYou synthesize.");
        File.WriteAllText(Path.Combine(templateDir, "worker-researcher.md"), workerMd);

        var loader = new TemplateLoader(this.testDir);

        var template = loader.Load("mcp-test-template");

        var researcher = template.Agents.Single(a => a.Name == "researcher");
        researcher.McpEndpoints.Should().NotBeNull();
        researcher.McpEndpoints.Should().BeEquivalentTo("learn-microsoft", "another-server");
    }

    /// <summary>
    /// A worker with no <c>mcp_endpoints</c> frontmatter must have
    /// <see cref="AgentDefinition.McpEndpoints"/> as null or empty.
    /// </summary>
    [TestMethod]
    public void Load_WhenAgentHasNoMcpEndpoints_LeavesPropertyNullOrEmpty()
    {
        var templateDir = Path.Combine(this.testDir, "plain-template");
        Directory.CreateDirectory(templateDir);

        const string templateYaml = """
            key: plain-template
            name: Plain
            description: No MCP
            goal_template: "do: {user_input}"
            """;

        const string workerMd = """
            ---
            name: plain
            displayName: Plain Worker
            description: No MCP needed
            ---

            # Plain
            """;

        File.WriteAllText(Path.Combine(templateDir, "_template.yaml"), templateYaml);
        File.WriteAllText(Path.Combine(templateDir, "leader.md"), "# Leader");
        File.WriteAllText(Path.Combine(templateDir, "synthesis.md"), "# Synthesis");
        File.WriteAllText(Path.Combine(templateDir, "worker-plain.md"), workerMd);

        var loader = new TemplateLoader(this.testDir);

        var template = loader.Load("plain-template");

        var worker = template.Agents.Single(a => a.Name == "plain");
        (worker.McpEndpoints is null || worker.McpEndpoints.Count == 0).Should().BeTrue();
    }
}
