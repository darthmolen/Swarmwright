using Swarmwright.Templates;
using FluentAssertions;

namespace Swarmwright.Tests.Templates;

/// <summary>
/// Tests for the <c>microsoft-deep-research</c> template — verifies it loads
/// with the expected agents and MCP endpoints wired up.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class MicrosoftDeepResearchTemplateTests
{
    private static string TestDataPath => Path.Combine(
        AppContext.BaseDirectory,
        "TestData",
        "templates");

    /// <summary>
    /// The template loads with 4 workers and the correct MCP endpoint wiring per worker.
    /// </summary>
    [TestMethod]
    public void Load_Template_HasFourWorkersWithCorrectMcpEndpoints()
    {
        var loader = new TemplateLoader(TestDataPath);

        var template = loader.Load("microsoft-deep-research");

        template.Agents.Should().HaveCount(4);
        template.Agents.Select(a => a.Name).Should().BeEquivalentTo(
            "microsoft_researcher", "skeptic", "licensing_analyst", "azure_integration_specialist");

        // Three of four workers use the learn-microsoft MCP endpoint.
        var researcher = template.Agents.Single(a => a.Name == "microsoft_researcher");
        researcher.McpEndpoints.Should().Contain("learn-microsoft");

        var licensingAnalyst = template.Agents.Single(a => a.Name == "licensing_analyst");
        licensingAnalyst.McpEndpoints.Should().Contain("learn-microsoft");

        var integrationSpecialist = template.Agents.Single(a => a.Name == "azure_integration_specialist");
        integrationSpecialist.McpEndpoints.Should().Contain("learn-microsoft");

        // Skeptic is source-agnostic — no MCP endpoints.
        var skeptic = template.Agents.Single(a => a.Name == "skeptic");
        (skeptic.McpEndpoints is null || skeptic.McpEndpoints.Count == 0).Should().BeTrue();
    }
}
