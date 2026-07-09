using System.Collections.Concurrent;
using Swarmwright.Configuration;
using Swarmwright.Core;
using Swarmwright.Database.Repositories;
using Swarmwright.Events;
using Swarmwright.Events.AgUI;
using Swarmwright.Models;
using Swarmwright.Models.Enums;
using Swarmwright.Orchestration;
using Swarmwright.Services;
using Swarmwright.Tests.Hosting.StateMachine;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Moq;
using Swarmwright.Hosting;

namespace Swarmwright.Tests.Orchestration;

/// <summary>
/// Tests for per-task artifact flushing (<c>.chat/{agent}.jsonl</c> and
/// siblings). Covers the serializer's atomic-rename invariant and the
/// orchestrator helpers that flush a single agent's conversation
/// history independent of swarm completion.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SwarmOrchestratorArtifactFlushTests : IDisposable
{
    private readonly string testDir;
    private readonly List<IDisposable> disposables = [];
    private Swarmwright.Hosting.StateMachine.StateTransitionService? stateService;

    /// <summary>
    /// Initializes a new instance of the <see cref="SwarmOrchestratorArtifactFlushTests"/> class.
    /// </summary>
    public SwarmOrchestratorArtifactFlushTests()
    {
        this.testDir = Path.Combine(Path.GetTempPath(), "artifact-flush-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.testDir);
    }

    /// <summary>
    /// Disposes managed resources.
    /// </summary>
    public void Dispose()
    {
        foreach (var disposable in this.disposables)
        {
            disposable.Dispose();
        }

        if (Directory.Exists(this.testDir))
        {
            Directory.Delete(this.testDir, recursive: true);
        }
    }

    /// <summary>
    /// Verifies that <see cref="ConversationHistorySerializer.SerializeAsync"/>
    /// leaves no per-call <c>.tmp</c> staging files behind after a
    /// successful write. The atomic-rename implementation moves its
    /// unique per-call staging file into place; an orphan <c>.tmp</c>
    /// sibling after success would indicate the move step was skipped
    /// or the staging file was leaked.
    /// </summary>
    [TestMethod]
    public async Task SerializeAsync_LeavesNoTmpFilesBehindOnSuccess()
    {
        // Arrange
        var filePath = Path.Combine(this.testDir, "no-orphan.jsonl");
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "First message."),
            new(ChatRole.Assistant, "Second message."),
        };

        // Act
        await ConversationHistorySerializer.SerializeAsync(filePath, history);

        // Assert
        File.Exists(filePath).Should().BeTrue("the final file must exist after SerializeAsync returns.");
        var lines = await File.ReadAllLinesAsync(filePath);
        lines.Should().HaveCount(2, "both messages should be written.");
        lines[0].Should().Contain("First message.");
        lines[1].Should().Contain("Second message.");

        var orphanTmps = Directory.GetFiles(this.testDir, "no-orphan.jsonl.*.tmp");
        orphanTmps.Should().BeEmpty(
            "a successful atomic rename must move the per-call staging file into place, leaving no orphaned .tmp files.");
    }

    /// <summary>
    /// Verifies that <c>PersistAgentConversationAsync</c> writes a single
    /// agent's JSONL history and system-prompt snapshot to <c>.chat/</c>
    /// without requiring the full swarm to complete. This is the primitive
    /// that the per-task-terminal flush will call, independent of the
    /// monolithic end-of-run flush.
    /// </summary>
    [TestMethod]
    public async Task PersistAgentConversationAsync_WritesJsonlAndSystemPrompt()
    {
        // Arrange
        var orchestrator = this.CreateOrchestrator();
        var agent = new SwarmAgent(
            name: "worker-a",
            role: "writer",
            displayName: "Writer A",
            systemPrompt: "Full system prompt with coordination mandates.",
            tools: [],
            chatClient: new Mock<IChatClient>().Object,
            logger: null,
            systemPromptCore: "Core driver prompt only.");
        agent.ConversationHistory.Add(new ChatMessage(ChatRole.User, "Hello."));
        agent.ConversationHistory.Add(new ChatMessage(ChatRole.Assistant, "Hi there."));

        // Act
        await orchestrator.PersistAgentConversationAsync(agent);

        // Assert
        var jsonlPath = Path.Combine(this.testDir, ".chat", "worker-a.jsonl");
        var systemPath = Path.Combine(this.testDir, ".chat", "worker-a.system.md");

        File.Exists(jsonlPath).Should().BeTrue(".chat/{agent}.jsonl must be written per-agent.");
        File.Exists(systemPath).Should().BeTrue(".chat/{agent}.system.md must capture the driver prompt snapshot.");

        var jsonlLines = await File.ReadAllLinesAsync(jsonlPath);
        jsonlLines.Should().HaveCount(2);
        jsonlLines[0].Should().Contain("Hello.");
        jsonlLines[1].Should().Contain("Hi there.");

        var systemContent = await File.ReadAllTextAsync(systemPath);
        systemContent.Should().Be("Core driver prompt only.");
    }

    /// <summary>
    /// Verifies that when a worker declares <see cref="TaskState.Completed"/>,
    /// the per-task terminal flush writes <c>.chat/{agent}.jsonl</c> inside
    /// <c>ExecuteWorkerTaskAsync</c> — independent of swarm synthesis.
    /// </summary>
    [TestMethod]
    public async Task TaskCompletion_FlushesAgentConversation_BeforeSwarmEnds()
    {
        // Arrange
        var swarmId = Guid.NewGuid();
        var task = NewTask(swarmId, "task-1", "worker-a", "writer");
        var orchestrator = this.CreateOrchestrator(swarmId, out var swarmService);
        await swarmService.AddTaskAsync(task);

        var agent = CreateAgent("worker-a", WorkerReturns(TaskState.Completed, "All done."));

        // Act
        await orchestrator.ExecuteWorkerTaskAsync(agent, task, CancellationToken.None);

        // Assert
        var jsonlPath = Path.Combine(this.testDir, ".chat", "worker-a.jsonl");
        File.Exists(jsonlPath).Should().BeTrue(
            "Completed branch of ExecuteWorkerTaskAsync must flush the agent's conversation.");
    }

    /// <summary>
    /// Verifies that when a worker declares <see cref="TaskState.Failed"/>,
    /// the per-task terminal flush writes <c>.chat/{agent}.jsonl</c>.
    /// </summary>
    [TestMethod]
    public async Task TaskFailure_FlushesAgentConversation_BeforeSwarmEnds()
    {
        // Arrange
        var swarmId = Guid.NewGuid();
        var task = NewTask(swarmId, "task-1", "worker-b", "writer");
        var orchestrator = this.CreateOrchestrator(swarmId, out var swarmService);
        await swarmService.AddTaskAsync(task);

        var agent = CreateAgent("worker-b", WorkerReturns(TaskState.Failed, "Could not do it."));

        // Act
        await orchestrator.ExecuteWorkerTaskAsync(agent, task, CancellationToken.None);

        // Assert
        var jsonlPath = Path.Combine(this.testDir, ".chat", "worker-b.jsonl");
        File.Exists(jsonlPath).Should().BeTrue(
            "Failed branch of ExecuteWorkerTaskAsync must flush the agent's conversation.");
    }

    /// <summary>
    /// Verifies the <c>else</c> branch: when the worker returns without
    /// calling <c>task_update</c>, the orchestrator transitions the task
    /// to Failed AND flushes the conversation history.
    /// </summary>
    [TestMethod]
    public async Task WorkerDidNotSignalTaskUpdate_FlushesAgentConversation()
    {
        // Arrange
        var swarmId = Guid.NewGuid();
        var task = NewTask(swarmId, "task-1", "worker-c", "writer");
        var orchestrator = this.CreateOrchestrator(swarmId, out var swarmService);
        await swarmService.AddTaskAsync(task);

        var agent = CreateAgent("worker-c", WorkerReturnsPlainText("Just some text, no task_update."));

        // Act
        await orchestrator.ExecuteWorkerTaskAsync(agent, task, CancellationToken.None);

        // Assert
        var jsonlPath = Path.Combine(this.testDir, ".chat", "worker-c.jsonl");
        File.Exists(jsonlPath).Should().BeTrue(
            "else branch must flush on the worker-did-not-signal path.");
    }

    /// <summary>
    /// Verifies that when the worker throws during execution, the
    /// orchestrator's catch-all path still transitions the task to
    /// Failed. Even if the flush itself throws (because the destination
    /// is blocked — here simulated by pre-creating a directory at the
    /// target jsonl path so the atomic-rename's File.Move fails), the
    /// worker's original exception must not propagate out and the
    /// task's Failed transition must still be recorded. The flush
    /// failure is swallowed via the existing logger.
    /// </summary>
    [TestMethod]
    public async Task WorkerThrows_FlushesAgentConversation_AndSwallowsFlushErrors()
    {
        // Arrange
        var swarmId = Guid.NewGuid();
        var task = NewTask(swarmId, "task-1", "worker-d", "writer");
        var capture = new CapturingLoggerProvider();
        this.disposables.Add(capture);
        var orchestrator = this.CreateOrchestrator(swarmId, capture, out var swarmService);
        await swarmService.AddTaskAsync(task);

        // Pre-create a directory at the target jsonl path so the flush's
        // File.Move fails with IOException, exercising the swallow path.
        var chatDir = Path.Combine(this.testDir, ".chat");
        Directory.CreateDirectory(chatDir);
        Directory.CreateDirectory(Path.Combine(chatDir, "worker-d.jsonl"));

        var chatMock = new Mock<IChatClient>();
        chatMock
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Simulated worker explosion."));

        var agent = CreateAgent("worker-d", chatMock);

        // Act
        Func<Task> act = () => orchestrator.ExecuteWorkerTaskAsync(agent, task, CancellationToken.None);

        // Assert — catch path wires flush (proven by flush-failure log), and
        // both the worker exception and the flush exception are swallowed.
        await act.Should().NotThrowAsync(
            "the catch block must swallow both the worker exception and any flush exception.");
        var updated = await swarmService.GetTasksAsync();
        updated.Should().ContainSingle(t => t.Id == "task-1")
            .Which.Status.Should().Be(TaskState.Failed, "the worker's crash path must still record Failed.");
        capture.Entries.Should().Contain(
            e => e.Category.Contains("SwarmOrchestrator", StringComparison.Ordinal)
                && e.Message.Contains("persist", StringComparison.OrdinalIgnoreCase),
            "LogConversationHistoryPersistFailed must fire when the flush's File.Move fails.");
    }

    /// <summary>
    /// Verifies that two concurrent terminals for the same agent produce
    /// a valid JSONL file whose final line matches the larger (newer)
    /// history. The per-agent semaphore guarantees newer-wins ordering
    /// so readers never observe a stale snapshot overwriting a fresher one.
    /// </summary>
    [TestMethod]
    public async Task TwoConcurrentTaskCompletions_SameAgent_ProducesValidJsonl_NewerWins()
    {
        // Arrange
        var swarmId = Guid.NewGuid();
        var taskA = NewTask(swarmId, "task-A", "worker-e", "writer");
        var taskB = NewTask(swarmId, "task-B", "worker-e", "writer");
        var orchestrator = this.CreateOrchestrator(swarmId, out var swarmService);
        await swarmService.AddTaskAsync(taskA);
        await swarmService.AddTaskAsync(taskB);

        var agent = CreateAgent("worker-e", WorkerReturns(TaskState.Completed, "Done."));

        // Act — run both terminals concurrently.
        await Task.WhenAll(
            orchestrator.ExecuteWorkerTaskAsync(agent, taskA, CancellationToken.None),
            orchestrator.ExecuteWorkerTaskAsync(agent, taskB, CancellationToken.None));

        // Assert
        var jsonlPath = Path.Combine(this.testDir, ".chat", "worker-e.jsonl");
        File.Exists(jsonlPath).Should().BeTrue();

        var lines = await File.ReadAllLinesAsync(jsonlPath);
        lines.Should().NotBeEmpty("the concurrent flushes must produce a non-empty, valid JSONL file.");
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            Action parse = () => System.Text.Json.JsonDocument.Parse(line).Dispose();
            parse.Should().NotThrow($"every line must be valid JSON: {line}");
        }

        // Newer-wins: the file's contents must match the agent's full
        // conversation history after both tasks have terminated (append-only
        // invariant guarantees the later flush is a superset of the earlier).
        lines.Length.Should().Be(
            agent.ConversationHistory.Count,
            "with newer-wins, the persisted file line count equals the final in-memory history count.");
    }

    /// <summary>
    /// Verifies that when <see cref="SwarmOrchestrator.RunAsync"/> throws
    /// before the happy-path end-of-run flush line, the same flush still
    /// runs via the finally block. Writing on success-or-failure of the
    /// swarm (not just success) guarantees <c>.chat/agents.json</c> and
    /// any accumulated synthesis history reach disk even when the swarm
    /// crashes or is cancelled mid-flight.
    /// </summary>
    [TestMethod]
    public async Task RunAsync_ThrowsMidFlight_EndOfRunFlushStillWritesAgentsJson()
    {
        // Arrange: a swarm service whose CreateSwarmAsync throws forces
        // RunAsync straight into its catch-all path before any agents are
        // spawned. The finally's end-of-run flush must still run.
        var mockSwarmService = new Mock<ISwarmService>();
        mockSwarmService
            .Setup(s => s.CreateSwarmAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>()))
            .ThrowsAsync(new InvalidOperationException("Simulated swarm creation failure."));

        var eventBus = new SwarmEventBus();
        var agUiAdapter = new SwarmEventAdapter();
        var transitionService = new NoOpStateTransitionService(agUiAdapter);
        var options = new SwarmOptions { MaxRounds = 1, SuspendTimeoutSeconds = 5 };
        var httpClient = new HttpClient();
        this.disposables.Add(httpClient);

        var orchestrator = new SwarmOrchestrator(
            new Mock<IChatClient>().Object,
            _ => new Mock<IChatClient>().Object,
            eventBus,
            agUiAdapter,
            mockSwarmService.Object,
            transitionService,
            options,
            template: null,
            workDirectory: this.testDir,
            httpClient: httpClient);

        // Act
        Func<Task> act = () => orchestrator.RunAsync(Guid.NewGuid(), "goal");

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        var agentsJson = Path.Combine(this.testDir, ".chat", "agents.json");
        File.Exists(agentsJson).Should().BeTrue(
            "finally block must run the end-of-run flush even when the happy path aborts.");
    }

    /// <summary>
    /// Verifies the end-of-run flush emits an agent-attributed
    /// <c>task-outputs.json</c> index. Given ≥2 completed tasks with distinct
    /// <c>WorkerName</c>/<c>WorkerRole</c>, every entry must carry the join
    /// keys (<c>taskId</c>, <c>workerName</c>, <c>workerRole</c>), the
    /// <c>subject</c>, the <c>result</c>, and a <c>completedUtc</c> equal to
    /// the task's <c>UpdatedAt</c>.
    /// </summary>
    [TestMethod]
    public async Task EndOfRunFlush_EmitsAgentAttributedTaskOutputsIndex()
    {
        // Arrange
        var swarmId = Guid.NewGuid();
        var orchestrator = this.CreateOrchestrator(swarmId, out var swarmService);

        await this.SeedCompletedTaskAsync(
            swarmService, swarmId, "task-A", "security-reviewer-1", "security-reviewer", "Audit the auth flow.", "No injection found.");
        await this.SeedCompletedTaskAsync(
            swarmService, swarmId, "task-B", "performance-reviewer-1", "performance-reviewer", "Profile the hot path.", "Allocations within budget.");

        var taskA = (await swarmService.GetTasksAsync()).Single(t => t.Id == "task-A");

        // Act
        await orchestrator.PersistConversationHistoriesAsync();

        // Assert
        var indexPath = Path.Combine(this.testDir, "task-outputs.json");
        File.Exists(indexPath).Should().BeTrue("the flush must emit the agent-attributed output index.");

        using var doc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(indexPath));
        var tasks = doc.RootElement.GetProperty("tasks");
        tasks.GetArrayLength().Should().Be(2, "both completed tasks must be indexed.");

        var entryA = tasks.EnumerateArray().Single(e => e.GetProperty("taskId").GetString() == "task-A");
        entryA.GetProperty("workerName").GetString().Should().Be("security-reviewer-1");
        entryA.GetProperty("workerRole").GetString().Should().Be("security-reviewer");
        entryA.GetProperty("subject").GetString().Should().Be("Audit the auth flow.");
        entryA.GetProperty("result").GetString().Should().Be("No injection found.");
        entryA.GetProperty("completedUtc").GetDateTime().Should().BeCloseTo(taskA.UpdatedAt, TimeSpan.FromSeconds(1));

        var entryB = tasks.EnumerateArray().Single(e => e.GetProperty("taskId").GetString() == "task-B");
        entryB.GetProperty("workerName").GetString().Should().Be("performance-reviewer-1");
        entryB.GetProperty("workerRole").GetString().Should().Be("performance-reviewer");
    }

    /// <summary>
    /// Verifies the index records the task <c>result</c> verbatim — a large,
    /// structured-JSON result must round-trip byte-for-byte through the index
    /// with no summarization or truncation.
    /// </summary>
    [TestMethod]
    public async Task TaskOutputsIndex_RecordsResultVerbatim()
    {
        // Arrange
        var swarmId = Guid.NewGuid();
        var orchestrator = this.CreateOrchestrator(swarmId, out var swarmService);

        var structuredResult = System.Text.Json.JsonSerializer.Serialize(new
        {
            findings = Enumerable.Range(0, 50)
                .Select(i => new { id = i, detail = $"Finding {i} with \"quotes\", commas, and \nnewlines." })
                .ToList(),
            summary = "A long structured payload that must not be summarized or truncated.",
        });

        await this.SeedCompletedTaskAsync(
            swarmService, swarmId, "task-J", "json-reviewer-1", "json-reviewer", "Emit structured findings.", structuredResult);

        // Act
        await orchestrator.PersistConversationHistoriesAsync();

        // Assert
        var indexPath = Path.Combine(this.testDir, "task-outputs.json");
        using var doc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(indexPath));
        var entry = doc.RootElement.GetProperty("tasks").EnumerateArray()
            .Single(e => e.GetProperty("taskId").GetString() == "task-J");
        entry.GetProperty("result").GetString().Should().Be(
            structuredResult,
            "the result must round-trip byte-for-byte — never summarized or truncated.");
    }

    /// <summary>
    /// Verifies each index entry pins its producing agent's prompt via a
    /// SHA-256 hash of the persisted <c>.chat/{worker}.system.md</c>, and that
    /// the <c>transcript</c>/<c>systemPrompt</c> artifact paths resolve to
    /// existing files in the work directory.
    /// </summary>
    [TestMethod]
    public async Task TaskOutputsIndex_PinsSystemPromptHashAndArtifactPaths()
    {
        // Arrange
        var swarmId = Guid.NewGuid();
        var orchestrator = this.CreateOrchestrator(swarmId, out var swarmService);

        await this.SeedCompletedTaskAsync(
            swarmService, swarmId, "task-H", "hash-reviewer-1", "hash-reviewer", "Review hashing.", "Looks good.");

        // Pre-persist the producing agent's artifacts so the index's hash and
        // path pointers have real files to resolve against (the flush persists
        // in-memory agents; a seeded task has no live agent, so we stage them).
        var chatDir = Path.Combine(this.testDir, ".chat");
        Directory.CreateDirectory(chatDir);
        await File.WriteAllTextAsync(Path.Combine(chatDir, "hash-reviewer-1.jsonl"), "{\"role\":\"user\"}\n");
        var promptContent = "Driver prompt for the hash reviewer lens.";
        await File.WriteAllTextAsync(Path.Combine(chatDir, "hash-reviewer-1.system.md"), promptContent);

        var expectedHash = "sha256:" + Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(promptContent)));

        // Act
        await orchestrator.PersistConversationHistoriesAsync();

        // Assert
        var indexPath = Path.Combine(this.testDir, "task-outputs.json");
        using var doc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(indexPath));
        var artifacts = doc.RootElement.GetProperty("tasks").EnumerateArray()
            .Single(e => e.GetProperty("taskId").GetString() == "task-H")
            .GetProperty("artifacts");

        artifacts.GetProperty("systemPromptHash").GetString().Should().Be(
            expectedHash, "the hash must be the SHA-256 of the persisted system prompt.");

        var transcript = artifacts.GetProperty("transcript").GetString()!;
        var systemPrompt = artifacts.GetProperty("systemPrompt").GetString()!;
        transcript.Should().Be(".chat/hash-reviewer-1.jsonl");
        systemPrompt.Should().Be(".chat/hash-reviewer-1.system.md");
        File.Exists(Path.Combine(this.testDir, transcript)).Should().BeTrue("the transcript pointer must resolve.");
        File.Exists(Path.Combine(this.testDir, systemPrompt)).Should().BeTrue("the system-prompt pointer must resolve.");
    }

    /// <summary>
    /// Verifies the index is emitted even when the run reaches the Failed
    /// terminal path. The end-of-run flush runs in <c>RunAsync</c>'s
    /// <c>finally</c> on every exit, so a crash during planning must still
    /// leave <c>task-outputs.json</c> on disk for the already-completed tasks.
    /// </summary>
    [TestMethod]
    public async Task TaskOutputsIndex_IsWrittenOnFailedRun()
    {
        // Arrange — seed a completed task, then build an orchestrator whose
        // leader client throws during planning so RunAsync lands in its
        // Failed catch and runs the finally flush.
        var swarmId = Guid.NewGuid();
        var dbFactory = new InMemoryDbContextFactory("ArtifactFlush_" + Guid.NewGuid());
        var repository = new SwarmRepository(dbFactory);
        var swarmService = new SwarmService(new InboxSystem(), new TeamRegistry(), repository);
        var localStateService = new Swarmwright.Hosting.StateMachine.StateTransitionService(
            dbFactory,
            new NullEmissionBroker(),
            Mock.Of<ISwarmObservationSink>());
        this.stateService = localStateService;

        await this.SeedCompletedTaskAsync(
            swarmService, swarmId, "task-F", "fail-reviewer-1", "fail-reviewer", "Done before crash.", "Completed result.");

        var throwingLeader = new Mock<IChatClient>();
        throwingLeader
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Simulated planning crash."));

        var eventBus = new SwarmEventBus();
        var agUiAdapter = new SwarmEventAdapter();
        var transitionService = new NoOpStateTransitionService(agUiAdapter) { Inner = localStateService };
        var options = new SwarmOptions { MaxRounds = 1, SuspendTimeoutSeconds = 5 };
        var httpClient = new HttpClient();
        this.disposables.Add(httpClient);

        var orchestrator = new SwarmOrchestrator(
            throwingLeader.Object,
            _ => new Mock<IChatClient>().Object,
            eventBus,
            agUiAdapter,
            swarmService,
            transitionService,
            options,
            template: null,
            workDirectory: this.testDir,
            httpClient: httpClient);

        // Act — RunAsync must surface the planning crash.
        Func<Task> act = () => orchestrator.RunAsync(swarmId, "goal");
        await act.Should().ThrowAsync<InvalidOperationException>();

        // Assert — the finally flush still wrote the index for the completed task.
        var indexPath = Path.Combine(this.testDir, "task-outputs.json");
        File.Exists(indexPath).Should().BeTrue("the all-paths flush must emit the index on a Failed run.");
        using var doc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(indexPath));
        doc.RootElement.GetProperty("tasks").EnumerateArray()
            .Should().Contain(e => e.GetProperty("taskId").GetString() == "task-F");
    }

    private async Task SeedCompletedTaskAsync(
        SwarmService swarmService,
        Guid swarmId,
        string taskId,
        string workerName,
        string workerRole,
        string subject,
        string result)
    {
        if (swarmService.SwarmId != swarmId)
        {
            await swarmService.CreateSwarmAsync(swarmId, "Seed goal.", null);
        }

        var task = NewTask(swarmId, taskId, workerName, workerRole);
        task.Subject = subject;
        task.Status = TaskState.Completed;
        await swarmService.AddTaskAsync(task);

        // The verbatim result is owned by the state-transition writer, not
        // AddTaskAsync; a Completed -> Completed transition is the supported
        // way to attach it (CanTransitionTask allows the self-loop).
        await this.stateService!.TransitionTaskAsync(
            swarmId,
            taskId,
            TaskState.Completed,
            reason: "test-seed",
            actor: "test",
            result: result);
    }

    private static SwarmTask NewTask(Guid swarmId, string id, string workerName, string workerRole) => new()
    {
        SwarmId = swarmId,
        Id = id,
        Subject = $"Subject for {id}",
        Description = $"Description for {id}",
        WorkerName = workerName,
        WorkerRole = workerRole,
    };

    private static Mock<IChatClient> WorkerReturns(TaskState status, string result)
    {
        var funcCall = new FunctionCallContent(
            $"call-{Guid.NewGuid():N}",
            "task_update",
            new Dictionary<string, object?>
            {
                ["task_id"] = "any",
                ["status"] = status.ToString(),
                ["result"] = result,
            });
        var funcResult = new FunctionResultContent(
            funcCall.CallId,
            "{\"success\":true}");

        var mock = new Mock<IChatClient>();
        mock.Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(
            [
                new ChatMessage(ChatRole.Assistant, [funcCall]),
                new ChatMessage(ChatRole.Tool, [funcResult]),
                new ChatMessage(ChatRole.Assistant, result),
            ]));
        return mock;
    }

    private static Mock<IChatClient> WorkerReturnsPlainText(string text)
    {
        var mock = new Mock<IChatClient>();
        mock.Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse([new ChatMessage(ChatRole.Assistant, text)]));
        return mock;
    }

    private static SwarmAgent CreateAgent(string name, Mock<IChatClient> chatClient) => new(
        name: name,
        role: "writer",
        displayName: name,
        systemPrompt: "System prompt.",
        tools: [],
        chatClient: chatClient.Object,
        logger: null,
        systemPromptCore: "Driver prompt core.");

    private SwarmOrchestrator CreateOrchestrator(Guid swarmId, out SwarmService swarmService)
    {
        // SwarmId is applied inside RunAsync in production; in tests we rely on
        // NoOpStateTransitionService to ignore the id so Guid.Empty is fine.
        _ = swarmId;
        return this.CreateOrchestratorCore(loggerFactory: null, out swarmService);
    }

    private SwarmOrchestrator CreateOrchestrator(Guid swarmId, ILoggerProvider loggerProvider, out SwarmService swarmService)
    {
        _ = swarmId;
        var factory = LoggerFactory.Create(builder =>
        {
            builder.AddProvider(loggerProvider);
            builder.SetMinimumLevel(LogLevel.Trace);
        });
        this.disposables.Add(factory);
        return this.CreateOrchestratorCore(factory, out swarmService);
    }

    private SwarmOrchestrator CreateOrchestrator()
    {
        return this.CreateOrchestratorCore(loggerFactory: null, out _);
    }

    private SwarmOrchestrator CreateOrchestratorCore(ILoggerFactory? loggerFactory, out SwarmService swarmService)
    {
        var dbFactory = new InMemoryDbContextFactory("ArtifactFlush_" + Guid.NewGuid());
        var repository = new SwarmRepository(dbFactory);
        swarmService = new SwarmService(
            new InboxSystem(),
            new TeamRegistry(),
            repository);
        var eventBus = new SwarmEventBus();
        var agUiAdapter = new SwarmEventAdapter();
        this.stateService = new Swarmwright.Hosting.StateMachine.StateTransitionService(
            dbFactory,
            new NullEmissionBroker(),
            Mock.Of<ISwarmObservationSink>());
        var transitionService = new NoOpStateTransitionService(agUiAdapter)
        {
            Inner = this.stateService,
        };
        var options = new SwarmOptions { MaxRounds = 1, SuspendTimeoutSeconds = 5 };
        var httpClient = new HttpClient();
        this.disposables.Add(httpClient);

        return new SwarmOrchestrator(
            new Mock<IChatClient>().Object,
            _ => new Mock<IChatClient>().Object,
            eventBus,
            agUiAdapter,
            swarmService,
            transitionService,
            options,
            template: null,
            workDirectory: this.testDir,
            httpClient: httpClient,
            loggerFactory: loggerFactory);
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentBag<CapturedLogEntry> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, this.Entries);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger : ILogger
        {
            private readonly string category;
            private readonly ConcurrentBag<CapturedLogEntry> entries;

            public CapturingLogger(string category, ConcurrentBag<CapturedLogEntry> entries)
            {
                this.category = category;
                this.entries = entries;
            }

            public IDisposable BeginScope<TState>(TState state)
                where TState : notnull => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                this.entries.Add(new CapturedLogEntry(this.category, formatter(state, exception), exception));
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }

    private sealed record CapturedLogEntry(string Category, string Message, Exception? Exception);
}
