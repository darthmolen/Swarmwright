using System.Text.Json;
using Swarmwright.Refinement;
using FluentAssertions;
using Microsoft.Extensions.AI;

namespace Swarmwright.Tests.Refinement;

/// <summary>
/// Unit tests for <see cref="RefinementToolFactory"/>.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class RefinementToolFactoryTests : IDisposable
{
    private const string CurrentAgent = "researcher";
    private const string SiblingAgent = "skeptic";

    private string workDir = null!;
    private string chatDir = null!;

    /// <inheritdoc/>
    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Creates a unique temp work directory with a <c>.chat</c> subfolder before each test.
    /// </summary>
    [TestInitialize]
    public void TestInitialize()
    {
        this.workDir = Path.Combine(Path.GetTempPath(), "refinement-tool-tests-" + Guid.NewGuid().ToString("N"));
        this.chatDir = Path.Combine(this.workDir, ".chat");
        Directory.CreateDirectory(this.chatDir);
    }

    /// <summary>
    /// Cleans up the work directory after each test.
    /// </summary>
    [TestCleanup]
    public void TestCleanup()
    {
        if (Directory.Exists(this.workDir))
        {
            Directory.Delete(this.workDir, recursive: true);
        }
    }

    // -----------------------------------------------------------------------
    // CreateRefinementTools — composition
    // -----------------------------------------------------------------------

    [TestMethod]
    public void CreateRefinementTools_Returns_Two_Tools()
    {
        var tools = RefinementToolFactory.CreateRefinementTools(this.workDir, CurrentAgent);

        tools.Should().HaveCount(2);
        tools.Select(t => t.Name).Should().BeEquivalentTo(["read_conversation_history", "read_driver_prompt"]);
    }

    // -----------------------------------------------------------------------
    // read_conversation_history
    // -----------------------------------------------------------------------

    [TestMethod]
    public async Task ReadConversationHistory_WhenEmptyAgentId_ReturnsCurrentAgentsHistory()
    {
        await this.WriteHistoryAsync(CurrentAgent, ("user", "hi"), ("assistant", "hello"));
        await this.WriteHistoryAsync(SiblingAgent, ("user", "other"), ("assistant", "different"));

        var tool = this.GetTool("read_conversation_history");
        var result = await tool.InvokeAsync(new AIFunctionArguments
        {
            ["agentId"] = string.Empty,
            ["limit"] = 0,
        });

        var root = ParseResult(result);
        root.GetProperty("agentId").GetString().Should().Be(CurrentAgent);
        root.GetProperty("messageCount").GetInt32().Should().Be(2);
        var messages = root.GetProperty("messages");
        messages[0].GetProperty("text").GetString().Should().Be("hi");
        messages[1].GetProperty("text").GetString().Should().Be("hello");
    }

    [TestMethod]
    public async Task ReadConversationHistory_WhenExplicitAgentId_ReturnsThatAgentsHistory()
    {
        await this.WriteHistoryAsync(CurrentAgent, ("user", "mine"));
        await this.WriteHistoryAsync(SiblingAgent, ("user", "question"), ("assistant", "skeptic answer"));

        var tool = this.GetTool("read_conversation_history");
        var result = await tool.InvokeAsync(new AIFunctionArguments
        {
            ["agentId"] = SiblingAgent,
            ["limit"] = 0,
        });

        var root = ParseResult(result);
        root.GetProperty("agentId").GetString().Should().Be(SiblingAgent);
        root.GetProperty("messageCount").GetInt32().Should().Be(2);
        root.GetProperty("messages")[1].GetProperty("text").GetString().Should().Be("skeptic answer");
    }

    [TestMethod]
    public async Task ReadConversationHistory_WithLimit_TrimsToLastN()
    {
        await this.WriteHistoryAsync(
            CurrentAgent,
            ("user", "one"),
            ("assistant", "two"),
            ("user", "three"),
            ("assistant", "four"),
            ("user", "five"));

        var tool = this.GetTool("read_conversation_history");
        var result = await tool.InvokeAsync(new AIFunctionArguments
        {
            ["agentId"] = string.Empty,
            ["limit"] = 2,
        });

        var root = ParseResult(result);
        root.GetProperty("messageCount").GetInt32().Should().Be(2);
        var messages = root.GetProperty("messages");
        messages[0].GetProperty("text").GetString().Should().Be("four");
        messages[1].GetProperty("text").GetString().Should().Be("five");
    }

    [TestMethod]
    public async Task ReadConversationHistory_WhenFileMissing_ReturnsEmptyMessages()
    {
        var tool = this.GetTool("read_conversation_history");
        var result = await tool.InvokeAsync(new AIFunctionArguments
        {
            ["agentId"] = "does_not_exist",
            ["limit"] = 0,
        });

        var root = ParseResult(result);
        root.GetProperty("agentId").GetString().Should().Be("does_not_exist");
        root.GetProperty("messageCount").GetInt32().Should().Be(0);
        root.GetProperty("messages").GetArrayLength().Should().Be(0);
    }

    [TestMethod]
    public async Task ReadConversationHistory_WhenAgentIdEscapesWorkdir_ReturnsError()
    {
        var tool = this.GetTool("read_conversation_history");
        var result = await tool.InvokeAsync(new AIFunctionArguments
        {
            ["agentId"] = "../../../etc/passwd",
            ["limit"] = 0,
        });

        var root = ParseResult(result);
        root.TryGetProperty("error", out var err).Should().BeTrue();
        err.GetString().Should().Contain("Invalid agentId");
    }

    [TestMethod]
    public async Task ReadConversationHistory_WhenAgentIdIsAbsolutePath_ReturnsError()
    {
        var tool = this.GetTool("read_conversation_history");
        var absolutePath = OperatingSystem.IsWindows() ? @"C:\Windows\System32" : "/etc/passwd";
        var result = await tool.InvokeAsync(new AIFunctionArguments
        {
            ["agentId"] = absolutePath,
            ["limit"] = 0,
        });

        var root = ParseResult(result);
        root.TryGetProperty("error", out _).Should().BeTrue();
    }

    // -----------------------------------------------------------------------
    // read_driver_prompt
    // -----------------------------------------------------------------------

    [TestMethod]
    public async Task ReadDriverPrompt_WhenEmptyAgentId_ReturnsCurrentAgentsPrompt()
    {
        await File.WriteAllTextAsync(
            Path.Combine(this.chatDir, $"{CurrentAgent}.system.md"),
            "You are the Iceland researcher. Be precise.");

        var tool = this.GetTool("read_driver_prompt");
        var result = await tool.InvokeAsync(new AIFunctionArguments
        {
            ["agentId"] = string.Empty,
        });

        var root = ParseResult(result);
        root.GetProperty("agentId").GetString().Should().Be(CurrentAgent);
        root.GetProperty("content").GetString().Should().Contain("Iceland researcher");
    }

    [TestMethod]
    public async Task ReadDriverPrompt_WhenExplicitAgentId_ReturnsThatAgentsPrompt()
    {
        await File.WriteAllTextAsync(
            Path.Combine(this.chatDir, $"{SiblingAgent}.system.md"),
            "You challenge every claim with skepticism.");

        var tool = this.GetTool("read_driver_prompt");
        var result = await tool.InvokeAsync(new AIFunctionArguments
        {
            ["agentId"] = SiblingAgent,
        });

        var root = ParseResult(result);
        root.GetProperty("agentId").GetString().Should().Be(SiblingAgent);
        root.GetProperty("content").GetString().Should().Contain("challenge every claim");
    }

    [TestMethod]
    public async Task ReadDriverPrompt_WhenFileMissing_ReturnsError()
    {
        var tool = this.GetTool("read_driver_prompt");
        var result = await tool.InvokeAsync(new AIFunctionArguments
        {
            ["agentId"] = "missing_agent",
        });

        var root = ParseResult(result);
        root.TryGetProperty("error", out var err).Should().BeTrue();
        err.GetString().Should().Contain("No driver prompt snapshot");
    }

    [TestMethod]
    public async Task ReadDriverPrompt_WhenAgentIdEscapesWorkdir_ReturnsError()
    {
        var tool = this.GetTool("read_driver_prompt");
        var result = await tool.InvokeAsync(new AIFunctionArguments
        {
            ["agentId"] = "../../etc/passwd",
        });

        var root = ParseResult(result);
        root.TryGetProperty("error", out _).Should().BeTrue();
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private AIFunction GetTool(string name)
    {
        return (AIFunction)RefinementToolFactory.CreateRefinementTools(this.workDir, CurrentAgent)
            .First(t => t.Name == name);
    }

    private async Task WriteHistoryAsync(string agentName, params (string Role, string Text)[] messages)
    {
        var lines = messages.Select(m =>
            JsonSerializer.Serialize(new { role = m.Role, text = m.Text }));
        var path = Path.Combine(this.chatDir, $"{agentName}.jsonl");
        await File.WriteAllLinesAsync(path, lines);
    }

    private static JsonElement ParseResult(object? result)
    {
        using var doc = JsonDocument.Parse(result?.ToString() ?? "{}");
        return doc.RootElement.Clone();
    }
}
