using Swarmwright.Core;
using Swarmwright.Database.Repositories;
using Swarmwright.Events;
using Swarmwright.Services;
using Swarmwright.Templates;
using Swarmwright.Tools;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Moq;

namespace Swarmwright.Tests.Tools;

/// <summary>
/// Tests that <see cref="SwarmToolFactory.CreateWorkerToolsAsync"/> loads MCP tools
/// via the supplied loader delegate when the agent declares <c>McpEndpoints</c>.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SwarmToolFactoryMcpTests : IDisposable
{
    private readonly SwarmService swarmService;
    private readonly SwarmEventBus eventBus;
    private readonly HttpClient httpClient;
    private readonly string workDir;

    /// <summary>
    /// Initializes a new instance of the <see cref="SwarmToolFactoryMcpTests"/> class.
    /// </summary>
    public SwarmToolFactoryMcpTests()
    {
        this.swarmService = new SwarmService(
            new InboxSystem(),
            new TeamRegistry(),
            new Mock<ISwarmRepository>().Object);
        this.eventBus = new SwarmEventBus();
        this.httpClient = new HttpClient();
        this.workDir = Path.Combine(Path.GetTempPath(), "swarm-tool-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.workDir);
    }

    /// <summary>
    /// Disposes managed resources.
    /// </summary>
    public void Dispose()
    {
        this.httpClient?.Dispose();
        if (Directory.Exists(this.workDir))
        {
            Directory.Delete(this.workDir, recursive: true);
        }
    }

    /// <summary>
    /// When the loader supplies MCP tools for an agent's declared endpoints,
    /// those tools must appear in the returned tool list.
    /// </summary>
    [TestMethod]
    public async Task CreateWorkerToolsAsync_WhenAgentHasMcpEndpoints_LoadsToolsFromLoader()
    {
        var fakeMcpTool = AIFunctionFactory.Create(
            ([System.ComponentModel.Description("a query")] string query) => $"result for {query}",
            "microsoft_docs_search",
            "searches Microsoft Learn");

        static IReadOnlyList<AITool> One(AITool t) => new List<AITool> { t };
        var calls = new List<string>();
        Task<IReadOnlyList<AITool>> Loader(string endpointName, CancellationToken ct)
        {
            calls.Add(endpointName);
            return Task.FromResult(One(fakeMcpTool));
        }

        var agentDef = new AgentDefinition
        {
            Name = "researcher",
            McpEndpoints = new List<string> { "learn-microsoft" },
        };

        var tools = await SwarmToolFactory.CreateWorkerToolsAsync(
            "researcher",
            this.swarmService,
            this.eventBus,
            agUiAdapter: null,
            this.workDir,
            this.httpClient,
            template: null,
            agentDef,
            Loader);

        calls.Should().ContainSingle().Which.Should().Be("learn-microsoft");
        tools.Should().Contain(t => t.Name == "microsoft_docs_search");
    }

    /// <summary>
    /// When mcpToolLoader is null, MCP tools are not loaded even if the agent declares endpoints.
    /// </summary>
    [TestMethod]
    public async Task CreateWorkerToolsAsync_WhenLoaderIsNull_DoesNotAddMcpTools()
    {
        var agentDef = new AgentDefinition
        {
            Name = "researcher",
            McpEndpoints = new List<string> { "learn-microsoft" },
        };

        var tools = await SwarmToolFactory.CreateWorkerToolsAsync(
            "researcher",
            this.swarmService,
            this.eventBus,
            agUiAdapter: null,
            this.workDir,
            this.httpClient,
            template: null,
            agentDef,
            mcpToolLoader: null);

        tools.Should().NotContain(t => t.Name == "microsoft_docs_search");
        // Coordination tools and default tools should still be present.
        tools.Should().Contain(t => t.Name == "task_update");
        tools.Should().Contain(t => t.Name == "read");
    }

    /// <summary>
    /// A whitelist on <see cref="AgentDefinition.Tools"/> must also filter MCP tools.
    /// </summary>
    [TestMethod]
    public async Task CreateWorkerToolsAsync_WhitelistFiltersMcpTools()
    {
        var included = AIFunctionFactory.Create(
            ([System.ComponentModel.Description("q")] string q) => $"{q}",
            "microsoft_docs_search",
            "kept");
        var excluded = AIFunctionFactory.Create(
            ([System.ComponentModel.Description("q")] string q) => $"{q}",
            "microsoft_docs_fetch",
            "dropped");

        Task<IReadOnlyList<AITool>> Loader(string endpointName, CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyList<AITool>>(new List<AITool> { included, excluded });
        }

        var agentDef = new AgentDefinition
        {
            Name = "researcher",
            McpEndpoints = new List<string> { "learn-microsoft" },
            Tools = new List<string> { "task_update", "microsoft_docs_search" },
        };

        var tools = await SwarmToolFactory.CreateWorkerToolsAsync(
            "researcher",
            this.swarmService,
            this.eventBus,
            agUiAdapter: null,
            this.workDir,
            this.httpClient,
            template: null,
            agentDef,
            Loader);

        tools.Should().Contain(t => t.Name == "microsoft_docs_search");
        tools.Should().NotContain(t => t.Name == "microsoft_docs_fetch");
    }
}
