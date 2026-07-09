using System.ClientModel;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Azure.AI.OpenAI;
using Swarmwright.Configuration;
using Swarmwright.Database.Repositories;
using Swarmwright.Events;
using Swarmwright.Hosting;
using Swarmwright.Refinement;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Swarmwright.Tests.Refinement;

/// <summary>
/// Live-LLM integration tests that prove the model discovers and calls the
/// refinement tools against a real Azure OpenAI endpoint. Opt-in only —
/// requires <c>AZURE_OPENAI_ENDPOINT</c>, <c>AZURE_OPENAI_API_KEY</c>, and
/// <c>AZURE_OPENAI_DEPLOYMENT</c> environment variables. Skipped as
/// inconclusive when the vars are missing.
///
/// Run: <c>dotnet test --filter "TestCategory=LiveLlm"</c>.
/// </summary>
[TestClass]
[TestCategory("LiveLlm")]
public sealed class RefinementToolsLiveTests : IDisposable
{
    // Distinctive phrases embedded in fixture files. The model is asked a
    // question that can only be answered by reading the fixture via the new
    // tool — if the phrase appears in the model's reply, the tool was
    // discovered, called, and its payload was used.
    private const string ResearcherDriverPhrase = "You are the meticulous Iceland geography researcher.";
    private const string SkepticTranscriptPhrase = "the population estimate of 130,000 seems inflated by 15%";

    private string testDir = null!;
    private string workDir = null!;
    private Guid swarmId;
    private ConcurrentDictionary<Guid, SwarmExecution> activeSwarms = null!;
    private SwarmManager swarmManager = null!;
    private IChatClient chatClient = null!;

    /// <inheritdoc/>
    public void Dispose()
    {
        this.chatClient?.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Sets up a temp swarm workdir with canned <c>.chat/</c> fixture files and
    /// constructs the real Azure OpenAI-backed chat client. Calls
    /// <see cref="Assert.Inconclusive(string)"/> when env vars are missing so
    /// the test is skipped, not failed, on machines without credentials.
    /// </summary>
    [TestInitialize]
    public async Task TestInitialize()
    {
        var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        var apiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT");

        if (string.IsNullOrWhiteSpace(endpoint) ||
            string.IsNullOrWhiteSpace(apiKey) ||
            string.IsNullOrWhiteSpace(deployment))
        {
            Assert.Inconclusive(
                "Live-LLM tests require AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, AZURE_OPENAI_DEPLOYMENT.");
            return;
        }

        this.testDir = Path.Combine(Path.GetTempPath(), "refinement-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.testDir);

        this.swarmId = Guid.NewGuid();
        this.workDir = Path.Combine(this.testDir, this.swarmId.ToString());
        var chatDir = Path.Combine(this.workDir, ".chat");
        Directory.CreateDirectory(chatDir);

        // agents.json
        var agentsJson = """
            [
              { "name": "researcher", "displayName": "Iceland Researcher", "role": "researcher", "description": "Geography researcher for the Iceland team." },
              { "name": "skeptic", "displayName": "Skeptic", "role": "skeptic", "description": "Challenges the researcher's conclusions." }
            ]
            """;
        await File.WriteAllTextAsync(Path.Combine(chatDir, "agents.json"), agentsJson);

        // researcher.system.md — distinctive driver phrase
        await File.WriteAllTextAsync(
            Path.Combine(chatDir, "researcher.system.md"),
            ResearcherDriverPhrase + "\n\nYour job is to report precise demographic numbers for Reykjavik.");

        // researcher.jsonl — short canned transcript
        var researcherHistory = new[]
        {
            JsonSerializer.Serialize(new { role = "user", text = "What is the population of Reykjavik?" }),
            JsonSerializer.Serialize(new { role = "assistant", text = "Based on Statistics Iceland, Reykjavik's population is approximately 130,000." }),
        };
        await File.WriteAllLinesAsync(Path.Combine(chatDir, "researcher.jsonl"), researcherHistory);

        // skeptic.system.md
        await File.WriteAllTextAsync(
            Path.Combine(chatDir, "skeptic.system.md"),
            "You are the Skeptic. Challenge every demographic claim with statistical skepticism.");

        // skeptic.jsonl — distinctive conclusion phrase
        var skepticHistory = new[]
        {
            JsonSerializer.Serialize(new { role = "user", text = "Review the researcher's population claim for Reykjavik." }),
            JsonSerializer.Serialize(new { role = "assistant", text = "After cross-checking against sample variance, " + SkepticTranscriptPhrase + ". A tighter estimate would be closer to 113,000." }),
        };
        await File.WriteAllLinesAsync(Path.Combine(chatDir, "skeptic.jsonl"), skepticHistory);

        // SwarmManager with the test workdir.
        var channel = Channel.CreateUnbounded<SwarmRequest>();
        this.activeSwarms = new ConcurrentDictionary<Guid, SwarmExecution>();
        this.swarmManager = new SwarmManager(
            channel.Writer,
            this.activeSwarms,
            Options.Create(new SwarmOptions { WorkBasePath = this.testDir }),
            Mock.Of<ISwarmRepository>(),
            Mock.Of<ISwarmObservationSink>(),
            NullLogger<SwarmManager>.Instance);

        // Real chat client with function invocation (mirrors SwarmBuilderExtensions wiring).
        var azureClient = new AzureOpenAIClient(
            new Uri(endpoint),
            new ApiKeyCredential(apiKey));
        this.chatClient = azureClient.GetChatClient(deployment)
            .AsIChatClient()
            .AsBuilder()
            .UseFunctionInvocation()
            .Build();
    }

    /// <inheritdoc/>
    [TestCleanup]
    public void TestCleanup()
    {
        this.chatClient?.Dispose();
        if (Directory.Exists(this.testDir))
        {
            Directory.Delete(this.testDir, recursive: true);
        }
    }

    /// <summary>
    /// Asks the researcher agent "what were your original instructions?" — the
    /// only way to answer accurately is to call <c>read_driver_prompt</c>,
    /// which returns the distinctive <see cref="ResearcherDriverPhrase"/>.
    /// </summary>
    [TestMethod]
    public async Task ToolDiscovery_ModelCallsReadDriverPrompt_WhenAskedAboutOriginalInstructions()
    {
        var sse = await this.RunAgentAsync(
            agentId: "researcher",
            userMessage: "What were your original instructions for this swarm run? Quote them exactly.");

        sse.Should().Contain("\"type\":\"RUN_STARTED\"");
        sse.Should().Contain("\"type\":\"RUN_FINISHED\"");

        AssertMatchingRunIds(sse);
        ToolCallShouldHaveBeenMade(sse, "read_driver_prompt");
        AssertAssistantTextContains(sse, ResearcherDriverPhrase);
    }

    /// <summary>
    /// Asks the researcher agent about the skeptic's conclusion — requires
    /// calling <c>read_conversation_history</c> with <c>agentId="skeptic"</c>.
    /// </summary>
    [TestMethod]
    public async Task ToolDiscovery_ModelCallsReadConversationHistory_ForSiblingAgent()
    {
        var sse = await this.RunAgentAsync(
            agentId: "researcher",
            userMessage: "What did the skeptic agent conclude about our population estimate? Look at their conversation and quote the specific concern they raised.");

        AssertMatchingRunIds(sse);
        ToolCallShouldHaveBeenMade(sse, "read_conversation_history");

        // The assistant's final text must include content from the skeptic's
        // fixture transcript — that proves the model called the tool with the
        // correct sibling agentId and used the payload. The exact distinctive
        // phrase is built into skeptic.jsonl; if the model landed on the
        // paraphrased figure "113,000" it also had to read skeptic's history.
        AssertAssistantTextContainsAny(sse, "113,000", "inflated by 15%", "cross-check");
    }

    private async Task<string> RunAgentAsync(string agentId, string userMessage)
    {
        var handler = new RefinementChatHandler(
            this.swarmManager,
            this.chatClient,
            new SingleClientHttpFactory(),
            NullLogger<RefinementChatHandler>.Instance);

        var request = new RefinementRequestDto
        {
            Method = "agent/run",
            Params = JsonDocument.Parse($"{{\"agentId\":\"{agentId}\"}}").RootElement,
            Body = JsonDocument.Parse(
                $"{{\"messages\":[{{\"role\":\"user\",\"content\":{JsonSerializer.Serialize(userMessage)}}}]}}").RootElement,
        };

        var httpContext = new DefaultHttpContext();
        using var responseBody = new MemoryStream();
        httpContext.Response.Body = responseBody;

        await handler.HandleAsync(this.swarmId, request, httpContext);

        responseBody.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(responseBody);
        return await reader.ReadToEndAsync();
    }

    private static void AssertMatchingRunIds(string sse)
    {
        var startedRunId = ExtractField(sse, "RUN_STARTED", "runId");
        var finishedRunId = ExtractField(sse, "RUN_FINISHED", "runId");
        startedRunId.Should().NotBeNullOrEmpty();
        finishedRunId.Should().Be(startedRunId);
    }

    private static void ToolCallShouldHaveBeenMade(string sse, string toolCallName)
    {
        var pattern = "\"type\":\"TOOL_CALL_START\"";
        var searchFrom = 0;
        while (true)
        {
            var hit = sse.IndexOf(pattern, searchFrom, StringComparison.Ordinal);
            if (hit < 0)
            {
                break;
            }

            var lineEnd = sse.IndexOf('\n', hit);
            var segment = lineEnd < 0 ? sse[hit..] : sse[hit..lineEnd];
            if (segment.Contains("\"toolCallName\":\"" + toolCallName + "\"", StringComparison.Ordinal))
            {
                return;
            }

            searchFrom = hit + pattern.Length;
        }

        Assert.Fail($"Expected at least one TOOL_CALL_START for '{toolCallName}' in SSE stream.");
    }

    private static void AssertAssistantTextContainsAny(string sse, params string[] candidates)
    {
        var full = ConcatenateAssistantText(sse);
        candidates.Any(c => full.Contains(c, StringComparison.OrdinalIgnoreCase))
            .Should()
            .BeTrue(
                $"assistant reply should reference sibling fixture content. Expected any of [{string.Join(", ", candidates)}] in: {full}");
    }

    private static void AssertAssistantTextContains(string sse, string expectedSubstring)
    {
        var full = ConcatenateAssistantText(sse);
        full.Should().Contain(expectedSubstring, "assistant reply must reference fixture content that only the tool could supply");
    }

    private static string ConcatenateAssistantText(string sse)
    {
        // Gather all TEXT_MESSAGE_CONTENT deltas and join them — the assistant's
        // streamed reply is the concatenation of those deltas.
        var deltas = new List<string>();
        var pattern = "\"type\":\"TEXT_MESSAGE_CONTENT\"";
        var searchFrom = 0;
        while (true)
        {
            var hit = sse.IndexOf(pattern, searchFrom, StringComparison.Ordinal);
            if (hit < 0)
            {
                break;
            }

            var lineEnd = sse.IndexOf('\n', hit);
            var segment = lineEnd < 0 ? sse[hit..] : sse[hit..lineEnd];
            var delta = ExtractFieldFromSegment(segment, "delta");
            if (delta is not null)
            {
                deltas.Add(delta);
            }

            searchFrom = hit + pattern.Length;
        }

        return string.Concat(deltas);
    }

    private static string? ExtractField(string sseBody, string eventType, string field)
    {
        var pattern = "\"type\":\"" + eventType + "\"";
        var typeIndex = sseBody.IndexOf(pattern, StringComparison.Ordinal);
        if (typeIndex < 0)
        {
            return null;
        }

        var lineEnd = sseBody.IndexOf('\n', typeIndex);
        var segment = lineEnd < 0 ? sseBody[typeIndex..] : sseBody[typeIndex..lineEnd];
        return ExtractFieldFromSegment(segment, field);
    }

    private static string? ExtractFieldFromSegment(string segment, string field)
    {
        var key = "\"" + field + "\":\"";
        var keyIndex = segment.IndexOf(key, StringComparison.Ordinal);
        if (keyIndex < 0)
        {
            return null;
        }

        var valueStart = keyIndex + key.Length;

        // Walk forward to find the closing unescaped quote.
        var i = valueStart;
        while (i < segment.Length)
        {
            if (segment[i] == '\\' && i + 1 < segment.Length)
            {
                i += 2;
                continue;
            }

            if (segment[i] == '"')
            {
                return segment[valueStart..i]
                    .Replace("\\n", "\n", StringComparison.Ordinal)
                    .Replace("\\\"", "\"", StringComparison.Ordinal)
                    .Replace("\\\\", "\\", StringComparison.Ordinal);
            }

            i++;
        }

        return null;
    }

    /// <summary>
    /// Minimal <see cref="IHttpClientFactory"/> that hands out a fresh client.
    /// The refinement handler uses this for the default tool set; live tests
    /// never exercise <c>web_fetch</c>, so a bare client suffices.
    /// </summary>
    private sealed class SingleClientHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            return new HttpClient();
        }
    }
}
