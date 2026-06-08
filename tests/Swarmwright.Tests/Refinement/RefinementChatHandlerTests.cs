using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Swarmwright.Configuration;
using Swarmwright.Database.Repositories;
using Swarmwright.Events;
using Swarmwright.Events.AgUI;
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
/// Unit tests for <see cref="RefinementChatHandler"/>.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class RefinementChatHandlerTests : IDisposable
{
    private readonly string testDir;
    private readonly ConcurrentDictionary<Guid, SwarmExecution> activeSwarms;
    private readonly SwarmManager manager;

    /// <summary>
    /// Initializes a new instance of the <see cref="RefinementChatHandlerTests"/> class.
    /// </summary>
    public RefinementChatHandlerTests()
    {
        this.testDir = Path.Combine(Path.GetTempPath(), "refinement-handler-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.testDir);

        var channel = Channel.CreateUnbounded<SwarmRequest>();
        this.activeSwarms = new ConcurrentDictionary<Guid, SwarmExecution>();

        this.manager = new SwarmManager(
            channel.Writer,
            this.activeSwarms,
            Options.Create(new SwarmOptions { WorkBasePath = this.testDir }),
            Mock.Of<ISwarmRepository>(),
            Mock.Of<ISwarmObservationSink>(),
            NullLogger<SwarmManager>.Instance);
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
    /// CopilotKit's AG-UI state manager correlates RUN_STARTED and RUN_FINISHED
    /// by <c>runId</c>. If the two events carry different run IDs the client
    /// never clears the in-flight flag and the chat input stays hidden.
    /// </summary>
    [TestMethod]
    public async Task HandleAsync_AgentRun_EmitsMatchingRunIdForStartAndFinish()
    {
        // Arrange — swarm work directory with minimal agents.json.
        var swarmId = Guid.NewGuid();
        var workDir = Path.Combine(this.testDir, swarmId.ToString());
        var chatDir = Path.Combine(workDir, ".chat");
        Directory.CreateDirectory(chatDir);

        var agentsJson = "[{\"name\":\"synthesis\",\"displayName\":\"Synthesis\",\"role\":\"synthesizer\",\"description\":\"t\"}]";
        await File.WriteAllTextAsync(Path.Combine(chatDir, "agents.json"), agentsJson);

        var chatResponse = new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]);
        using var chatClient = new FakeChatClient(chatResponse);
        var httpFactory = new StubHttpClientFactory();

        var handler = new RefinementChatHandler(
            this.manager,
            chatClient,
            httpFactory,
            NullLogger<RefinementChatHandler>.Instance);

        var request = new RefinementRequestDto
        {
            Method = "agent/run",
            Params = JsonDocument.Parse("{\"agentId\":\"synthesis\"}").RootElement,
            Body = JsonDocument.Parse("{\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}").RootElement,
        };

        var httpContext = new DefaultHttpContext();
        using var responseBody = new MemoryStream();
        httpContext.Response.Body = responseBody;

        // Act
        await handler.HandleAsync(swarmId, request, httpContext);

        // Assert
        responseBody.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(responseBody);
        var sse = await reader.ReadToEndAsync();

        var startedRunId = ExtractRunId(sse, "RUN_STARTED");
        var finishedRunId = ExtractRunId(sse, "RUN_FINISHED");

        startedRunId.Should().NotBeNullOrEmpty("RUN_STARTED must include a runId");
        finishedRunId.Should().NotBeNullOrEmpty("RUN_FINISHED must include a runId");
        finishedRunId.Should().Be(startedRunId, "AG-UI clients correlate finish/start by runId");
    }

    /// <summary>
    /// When the LLM call throws (e.g. Azure OpenAI RAI content filter), the
    /// handler must surface the failure to the user as an assistant TEXT_MESSAGE
    /// sequence. Prior to the fix the run produced <c>sseEvents=0</c> — the
    /// client received RUN_STARTED / RUN_FINISHED with nothing between them and
    /// the chat pane stayed blank.
    /// </summary>
    [TestMethod]
    public async Task HandleAsync_WhenLlmCallThrowsContentFilter_EmitsUserFacingAssistantMessage()
    {
        var swarmId = Guid.NewGuid();
        var workDir = Path.Combine(this.testDir, swarmId.ToString());
        var chatDir = Path.Combine(workDir, ".chat");
        Directory.CreateDirectory(chatDir);

        var agentsJson = "[{\"name\":\"synthesis\",\"displayName\":\"Synthesis\",\"role\":\"synthesizer\",\"description\":\"t\"}]";
        await File.WriteAllTextAsync(Path.Combine(chatDir, "agents.json"), agentsJson);

        // Chat client throws the same shape we saw in production:
        // HTTP 400 with "content_filter" in the message.
        using var chatClient = new ThrowingChatClient(
            new InvalidOperationException(
                "HTTP 400 (content_filter)\n"
                + "The response was filtered due to the prompt triggering Azure OpenAI's content management policy."));

        var request = new RefinementRequestDto
        {
            Method = "agent/run",
            Params = JsonDocument.Parse("{\"agentId\":\"synthesis\"}").RootElement,
            Body = JsonDocument.Parse("{\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}").RootElement,
        };

        var httpContext = new DefaultHttpContext();
        using var responseBody = new MemoryStream();
        httpContext.Response.Body = responseBody;

        var handler = new RefinementChatHandler(
            this.manager,
            chatClient,
            new StubHttpClientFactory(),
            NullLogger<RefinementChatHandler>.Instance);

        await handler.HandleAsync(swarmId, request, httpContext);

        responseBody.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(responseBody);
        var sse = await reader.ReadToEndAsync();

        sse.Should().Contain("\"type\":\"RUN_STARTED\"");
        sse.Should().Contain("\"type\":\"TEXT_MESSAGE_START\"", "user needs to see an assistant bubble when the call fails");
        sse.Should().Contain("\"type\":\"TEXT_MESSAGE_CONTENT\"");
        sse.Should().Contain("\"type\":\"TEXT_MESSAGE_END\"");
        sse.Should().Contain("\"type\":\"RUN_FINISHED\"");

        // The friendly copy must explain the content filter so the user knows to rephrase.
        sse.Should().Contain("content management policy");
    }

    /// <summary>
    /// Rate-limit failures (HTTP 429) must surface with their own copy
    /// rather than the generic fallback — the user should know to wait
    /// before retrying.
    /// </summary>
    [TestMethod]
    public async Task HandleAsync_WhenLlmCallThrowsRateLimit_EmitsRateLimitMessage()
    {
        var swarmId = Guid.NewGuid();
        var workDir = Path.Combine(this.testDir, swarmId.ToString());
        Directory.CreateDirectory(Path.Combine(workDir, ".chat"));

        using var chatClient = new ThrowingChatClient(
            new InvalidOperationException("HTTP 429 TooManyRequests: rate limit exceeded"));

        var request = new RefinementRequestDto
        {
            Method = "agent/run",
            Params = JsonDocument.Parse("{\"agentId\":\"synthesis\"}").RootElement,
            Body = JsonDocument.Parse("{\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}").RootElement,
        };

        var httpContext = new DefaultHttpContext();
        using var responseBody = new MemoryStream();
        httpContext.Response.Body = responseBody;

        var handler = new RefinementChatHandler(
            this.manager,
            chatClient,
            new StubHttpClientFactory(),
            NullLogger<RefinementChatHandler>.Instance);

        await handler.HandleAsync(swarmId, request, httpContext);

        responseBody.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(responseBody);
        var sse = await reader.ReadToEndAsync();

        sse.Should().Contain("\"type\":\"TEXT_MESSAGE_CONTENT\"");
        sse.Should().Contain("rate-limited");
    }

    /// <summary>
    /// The error message sent to the client must be a well-formed
    /// TEXT_MESSAGE_START / CONTENT / END triple sharing a single
    /// <c>messageId</c> — otherwise CopilotKit's AG-UI apply layer drops
    /// the content / end events with a warning and nothing renders.
    /// </summary>
    [TestMethod]
    public async Task HandleAsync_WhenLlmCallThrows_ErrorMessageIdIsConsistentAcrossStartContentEnd()
    {
        var swarmId = Guid.NewGuid();
        var workDir = Path.Combine(this.testDir, swarmId.ToString());
        Directory.CreateDirectory(Path.Combine(workDir, ".chat"));

        using var chatClient = new ThrowingChatClient(
            new InvalidOperationException("HTTP 400 (content_filter)"));

        var request = new RefinementRequestDto
        {
            Method = "agent/run",
            Params = JsonDocument.Parse("{\"agentId\":\"synthesis\"}").RootElement,
            Body = JsonDocument.Parse("{\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}").RootElement,
        };

        var httpContext = new DefaultHttpContext();
        using var responseBody = new MemoryStream();
        httpContext.Response.Body = responseBody;

        var handler = new RefinementChatHandler(
            this.manager,
            chatClient,
            new StubHttpClientFactory(),
            NullLogger<RefinementChatHandler>.Instance);

        await handler.HandleAsync(swarmId, request, httpContext);

        responseBody.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(responseBody);
        var sse = await reader.ReadToEndAsync();

        var startId = ExtractField(sse, "TEXT_MESSAGE_START", "messageId");
        var contentId = ExtractField(sse, "TEXT_MESSAGE_CONTENT", "messageId");
        var endId = ExtractField(sse, "TEXT_MESSAGE_END", "messageId");

        startId.Should().NotBeNullOrEmpty();
        contentId.Should().Be(startId, "AG-UI CONTENT events must target the active message");
        endId.Should().Be(startId, "AG-UI END event must close the active message by id");
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

        var key = "\"" + field + "\":\"";
        var keyIndex = segment.IndexOf(key, StringComparison.Ordinal);
        if (keyIndex < 0)
        {
            return null;
        }

        var valueStart = keyIndex + key.Length;
        var valueEnd = segment.IndexOf('"', valueStart);
        return valueEnd < 0 ? null : segment[valueStart..valueEnd];
    }

    private static string? ExtractRunId(string sseBody, string eventType)
    {
        // Each AG-UI event is a single "data: {...}\n\n" line. Find the JSON
        // payload whose "type" matches the requested event, then read its runId.
        var pattern = "\"type\":\"" + eventType + "\"";
        var typeIndex = sseBody.IndexOf(pattern, StringComparison.Ordinal);
        if (typeIndex < 0)
        {
            return null;
        }

        var lineEnd = sseBody.IndexOf('\n', typeIndex);
        var segment = lineEnd < 0 ? sseBody[typeIndex..] : sseBody[typeIndex..lineEnd];

        const string runIdKey = "\"runId\":\"";
        var runIdIndex = segment.IndexOf(runIdKey, StringComparison.Ordinal);
        if (runIdIndex < 0)
        {
            return null;
        }

        var valueStart = runIdIndex + runIdKey.Length;
        var valueEnd = segment.IndexOf('"', valueStart);
        return valueEnd < 0 ? null : segment[valueStart..valueEnd];
    }

    /// <summary>
    /// Minimal <see cref="IChatClient"/> returning a fixed response.
    /// </summary>
    private sealed class FakeChatClient : IChatClient
    {
        private readonly ChatResponse response;

        public FakeChatClient(ChatResponse response)
        {
            this.response = response;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(this.response);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public void Dispose()
        {
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return null;
        }
    }

    /// <summary>
    /// Minimal <see cref="IHttpClientFactory"/> that hands out a fresh client.
    /// The handler only needs this to build default tools; the tools are not
    /// invoked because the stub chat client returns a plain text response.
    /// </summary>
    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            return new HttpClient();
        }
    }

    /// <summary>
    /// Chat client that always throws, simulating LLM-side failures
    /// (content filter, rate limit, auth) for the error-surfacing tests.
    /// </summary>
    private sealed class ThrowingChatClient : IChatClient
    {
        private readonly Exception toThrow;

        public ThrowingChatClient(Exception toThrow)
        {
            this.toThrow = toThrow;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw this.toThrow;
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw this.toThrow;
        }

        public void Dispose()
        {
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return null;
        }
    }
}
