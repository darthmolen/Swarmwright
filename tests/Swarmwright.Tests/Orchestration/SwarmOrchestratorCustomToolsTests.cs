using System.Text.Json;
using Swarmwright.Configuration;
using Swarmwright.Core;
using Swarmwright.Database.Repositories;
using Swarmwright.Events;
using Swarmwright.Events.AgUI;
using Swarmwright.Models;
using Swarmwright.Orchestration;
using Swarmwright.Services;
using Swarmwright.Templates;
using Swarmwright.Tests.Hosting.StateMachine;
using Swarmwright.Tools;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Moq;
using Swarmwright.Hosting;

namespace Swarmwright.Tests.Orchestration;

/// <summary>
/// End-to-end tests verifying custom tool providers wire through the orchestrator:
/// provider instances resolved via constructor → per-worker allowlist filter → tool list.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public sealed class SwarmOrchestratorCustomToolsTests : IDisposable
{
    private static readonly JsonSerializerOptions CamelCaseOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string testDir;
#pragma warning disable IDE0370 // analyzer says these are unnecessary, but CS8618 fires without them in classes with an explicit instance ctor that doesn't initialize them; [TestInitialize] sets them per-test.
    private Mock<IChatClient> mockLeaderClient = null!;
    private Mock<IChatClient> mockWorkerClient = null!;
    private InMemoryDbContextFactory dbFactory = null!;
    private SwarmRepository repository = null!;
    private SwarmService swarmService = null!;
    private NoOpStateTransitionService transitionService = null!;
    private SwarmEventBus eventBus = null!;
    private SwarmEventAdapter agUiAdapter = null!;
    private SwarmOptions options = null!;
    private HttpClient httpClient = null!;
#pragma warning restore IDE0370

    private IList<AITool>? capturedWorkerTools;

    /// <summary>
    /// Initializes a new instance of the <see cref="SwarmOrchestratorCustomToolsTests"/> class.
    /// </summary>
    public SwarmOrchestratorCustomToolsTests()
    {
        this.testDir = Path.Combine(Path.GetTempPath(), "swarm-customtools-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.testDir);
    }

    /// <summary>
    /// Initializes test dependencies before each test.
    /// </summary>
    [TestInitialize]
    public void TestInitialize()
    {
        this.mockLeaderClient = new Mock<IChatClient>();
        this.mockWorkerClient = new Mock<IChatClient>();
        this.dbFactory = new InMemoryDbContextFactory("CustomTools_" + Guid.NewGuid());
        this.repository = new SwarmRepository(this.dbFactory);
        this.swarmService = new SwarmService(
            new InboxSystem(),
            new TeamRegistry(),
            this.repository);
        this.transitionService = new NoOpStateTransitionService
        {
            Inner = new Swarmwright.Hosting.StateMachine.StateTransitionService(
                this.dbFactory,
                new NullEmissionBroker(),
                Mock.Of<ISwarmObservationSink>()),
        };
        this.eventBus = new SwarmEventBus();
        this.agUiAdapter = new SwarmEventAdapter();
        this.httpClient = new HttpClient();
        this.options = new SwarmOptions
        {
            MaxRounds = 3,
            SuspendTimeoutSeconds = 5,
        };
        this.capturedWorkerTools = null;
    }

    /// <summary>
    /// Disposes managed resources.
    /// </summary>
    public void Dispose()
    {
        this.httpClient?.Dispose();

        if (Directory.Exists(this.testDir))
        {
            Directory.Delete(this.testDir, recursive: true);
        }
    }

    /// <summary>
    /// A worker with <c>custom_tools: [query_db]</c> receives only the allowlisted
    /// custom tool, not other tools from the same provider.
    /// </summary>
    [TestMethod]
    public async Task RunAsync_WorkerWithCustomTools_ReceivesOnlyAllowlistedTools()
    {
        this.CreateTemplateOnDisk("ct-allowlist", workerCustomTools: ["query_db"]);

        var loader = new TemplateLoader(this.testDir);
        var template = loader.Load("ct-allowlist");

        this.SetupLeaderToCreatePlan("researcher", "Researcher");
        this.SetupWorkerToCaptureThenComplete();

        var orchestrator = new SwarmOrchestrator(
            this.mockLeaderClient.Object,
            _ => this.mockWorkerClient.Object,
            this.eventBus,
            this.agUiAdapter,
            this.swarmService,
            this.transitionService,
            this.options,
            template,
            workDirectory: Path.GetTempPath(),
            httpClient: this.httpClient,
            templatesDirectory: this.testDir,
            customToolProviders: [new DbAndApiToolsProvider()]);

        await orchestrator.RunAsync(Guid.NewGuid(), "Work", CancellationToken.None);

        this.capturedWorkerTools.Should().NotBeNull();
        var toolNames = this.capturedWorkerTools!.Select(t => t.Name).ToList();
        toolNames.Should().Contain("query_db");
        toolNames.Should().NotContain("call_api", "call_api was not in the worker's custom_tools allowlist");
    }

    /// <summary>
    /// A worker without <c>custom_tools</c> frontmatter receives no custom tools.
    /// </summary>
    [TestMethod]
    public async Task RunAsync_WorkerWithoutCustomToolsField_ReceivesNoCustomTools()
    {
        this.CreateTemplateOnDisk("ct-none", workerCustomTools: null);

        var loader = new TemplateLoader(this.testDir);
        var template = loader.Load("ct-none");

        this.SetupLeaderToCreatePlan("researcher", "Researcher");
        this.SetupWorkerToCaptureThenComplete();

        var orchestrator = new SwarmOrchestrator(
            this.mockLeaderClient.Object,
            _ => this.mockWorkerClient.Object,
            this.eventBus,
            this.agUiAdapter,
            this.swarmService,
            this.transitionService,
            this.options,
            template,
            workDirectory: Path.GetTempPath(),
            httpClient: this.httpClient,
            templatesDirectory: this.testDir,
            customToolProviders: [new DbAndApiToolsProvider()]);

        await orchestrator.RunAsync(Guid.NewGuid(), "Work", CancellationToken.None);

        this.capturedWorkerTools.Should().NotBeNull();
        var toolNames = this.capturedWorkerTools!.Select(t => t.Name).ToList();
        toolNames.Should().NotContain("query_db");
        toolNames.Should().NotContain("call_api");
    }

    /// <summary>
    /// Coordination tools, skill tools, and custom tools all coexist in the same
    /// worker's tool list when all three are in play.
    /// </summary>
    [TestMethod]
    public async Task RunAsync_CoordinationAndSkillTools_CoexistWithCustomTools()
    {
        this.CreateTemplateOnDisk("ct-coexist", workerCustomTools: ["query_db"], withSkill: true);

        var loader = new TemplateLoader(this.testDir);
        var template = loader.Load("ct-coexist");

        this.SetupLeaderToCreatePlan("researcher", "Researcher");
        this.SetupWorkerToCaptureThenComplete();

        var orchestrator = new SwarmOrchestrator(
            this.mockLeaderClient.Object,
            _ => this.mockWorkerClient.Object,
            this.eventBus,
            this.agUiAdapter,
            this.swarmService,
            this.transitionService,
            this.options,
            template,
            workDirectory: Path.GetTempPath(),
            httpClient: this.httpClient,
            templatesDirectory: this.testDir,
            customToolProviders: [new DbAndApiToolsProvider()]);

        await orchestrator.RunAsync(Guid.NewGuid(), "Work", CancellationToken.None);

        this.capturedWorkerTools.Should().NotBeNull();
        var toolNames = this.capturedWorkerTools!.Select(t => t.Name).ToList();
        toolNames.Should().Contain("task_update", "coordination tool present");
        toolNames.Should().Contain("load_skill", "skill tool present (worker declares a skill)");
        toolNames.Should().Contain("query_db", "custom tool present");
    }

    /// <summary>
    /// When two providers are registered and a worker's <c>custom_tools</c> references
    /// tools from both, tools from both providers appear in the tool list.
    /// </summary>
    [TestMethod]
    public async Task RunAsync_MultipleProviders_ToolsFromAllResolve()
    {
        this.CreateTemplateOnDisk("ct-multi", workerCustomTools: ["query_db", "send_slack"]);

        var loader = new TemplateLoader(this.testDir);
        var template = loader.Load("ct-multi");

        this.SetupLeaderToCreatePlan("researcher", "Researcher");
        this.SetupWorkerToCaptureThenComplete();

        var orchestrator = new SwarmOrchestrator(
            this.mockLeaderClient.Object,
            _ => this.mockWorkerClient.Object,
            this.eventBus,
            this.agUiAdapter,
            this.swarmService,
            this.transitionService,
            this.options,
            template,
            workDirectory: Path.GetTempPath(),
            httpClient: this.httpClient,
            templatesDirectory: this.testDir,
            customToolProviders: [new DbAndApiToolsProvider(), new SlackToolsProvider()]);

        await orchestrator.RunAsync(Guid.NewGuid(), "Work", CancellationToken.None);

        this.capturedWorkerTools.Should().NotBeNull();
        var toolNames = this.capturedWorkerTools!.Select(t => t.Name).ToList();
        toolNames.Should().Contain("query_db");
        toolNames.Should().Contain("send_slack");
    }

    // ---- Test helpers ----

    private void CreateTemplateOnDisk(string key, IReadOnlyList<string>? workerCustomTools, bool withSkill = false)
    {
        var templateDir = Path.Combine(this.testDir, key);
        Directory.CreateDirectory(templateDir);

        File.WriteAllText(
            Path.Combine(templateDir, "_template.yaml"),
            $"key: {key}\nname: Test\ndescription: Test\ngoal_template: \"do\"");
        File.WriteAllText(Path.Combine(templateDir, "leader.md"), "# Leader");
        File.WriteAllText(Path.Combine(templateDir, "synthesis.md"), "# Synthesis");

        var frontmatterLines = new List<string>
        {
            "name: researcher",
            "displayName: Researcher",
            "description: Test",
        };

        if (workerCustomTools is { Count: > 0 })
        {
            frontmatterLines.Add("custom_tools:");
            foreach (var name in workerCustomTools)
            {
                frontmatterLines.Add($"  - {name}");
            }
        }

        if (withSkill)
        {
            frontmatterLines.Add("skills:");
            frontmatterLines.Add("  - test-skill");

            var skillDir = Path.Combine(templateDir, "skills", "test-skill");
            Directory.CreateDirectory(skillDir);
            File.WriteAllText(
                Path.Combine(skillDir, "SKILL.md"),
                "---\nname: test-skill\ndescription: Test.\n---\n\nBody.");
        }

        var frontmatter = string.Join("\n", frontmatterLines);
        File.WriteAllText(
            Path.Combine(templateDir, "worker-researcher.md"),
            $"---\n{frontmatter}\n---\n\n# Researcher");
    }

    private void SetupLeaderToCreatePlan(string workerName, string workerRole)
    {
        var planCallCount = 0;
        this.mockLeaderClient
            .Setup(c => c.GetResponseAsync(It.IsAny<IList<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns<IList<ChatMessage>, ChatOptions, CancellationToken>((msgs, opts, ct) =>
            {
                planCallCount++;

                if (planCallCount == 1 && opts?.Tools != null)
                {
                    var planTool = opts.Tools.OfType<AIFunction>().FirstOrDefault(t => t.Name == "create_plan");
                    if (planTool != null)
                    {
                        var tasks = new List<TaskPlan>
                        {
                            new()
                            {
                                Subject = "T",
                                Description = "Do.",
                                WorkerRole = workerRole,
                                WorkerName = workerName,
                            },
                        };
                        _ = Task.Run(
                            async () => await planTool.InvokeAsync(
                                new AIFunctionArguments(new Dictionary<string, object?>
                                {
                                    ["team_description"] = "team",
                                    ["tasks"] = JsonSerializer.Serialize(tasks, CamelCaseOptions),
                                }),
                                ct),
                            ct);
                    }
                }

                if (opts?.Tools != null && planCallCount > 1)
                {
                    var reportTool = opts.Tools.OfType<AIFunction>().FirstOrDefault(t => t.Name == "submit_report");
                    if (reportTool != null)
                    {
                        _ = Task.Run(
                            async () => await reportTool.InvokeAsync(
                                new AIFunctionArguments(new Dictionary<string, object?> { ["report"] = "Done." }),
                                ct),
                            ct);
                    }
                }

                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
            });
    }

    private void SetupWorkerToCaptureThenComplete()
    {
        var funcCall = new FunctionCallContent(
            "call-w",
            "task_update",
            new Dictionary<string, object?> { ["task_id"] = "any", ["status"] = "Completed", ["result"] = "done" });
        var funcResult = new FunctionResultContent("call-w", "{\"success\":true,\"status\":\"Completed\"}");

        this.mockWorkerClient
            .Setup(c => c.GetResponseAsync(It.IsAny<IList<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns<IList<ChatMessage>, ChatOptions, CancellationToken>((msgs, opts, ct) =>
            {
                this.capturedWorkerTools = opts?.Tools;

                if (opts?.Tools != null)
                {
                    var taskListTool = opts.Tools.OfType<AIFunction>().FirstOrDefault(t => t.Name == "task_list");
                    var taskUpdateTool = opts.Tools.OfType<AIFunction>().FirstOrDefault(t => t.Name == "task_update");

                    if (taskListTool != null && taskUpdateTool != null)
                    {
                        _ = Task.Run(
                            async () =>
                            {
                                var tasksJson = await taskListTool.InvokeAsync(
                                    new AIFunctionArguments(new Dictionary<string, object?>()),
                                    ct);
                                var taskId = ExtractFirstTaskId(tasksJson?.ToString());
                                if (taskId != null)
                                {
                                    await taskUpdateTool.InvokeAsync(
                                        new AIFunctionArguments(new Dictionary<string, object?>
                                        {
                                            ["task_id"] = taskId,
                                            ["status"] = "Completed",
                                            ["result"] = "done",
                                        }),
                                        ct);
                                }
                            },
                            ct);
                    }
                }

                return Task.FromResult(new ChatResponse(
                [
                    new ChatMessage(ChatRole.Assistant, [funcCall]),
                    new ChatMessage(ChatRole.Tool, [funcResult]),
                    new ChatMessage(ChatRole.Assistant, "done"),
                ]));
            });
    }

    private static string? ExtractFirstTaskId(string? tasksJson)
    {
        if (string.IsNullOrEmpty(tasksJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(tasksJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
            {
                return doc.RootElement[0].GetProperty("id").GetString();
            }
        }
        catch (JsonException)
        {
            // Fall through.
        }

        return null;
    }

#pragma warning disable CA1822 // instance methods by design — wrapped as delegates bound to `this`
    private sealed class DbAndApiToolsProvider : CustomToolProvider
    {
        [SwarmTool("query_db", "Query the DB.")]
        public string QueryDb() => "rows";

        [SwarmTool("call_api", "Call the API.")]
        public string CallApi() => "response";
    }

    private sealed class SlackToolsProvider : CustomToolProvider
    {
        [SwarmTool("send_slack", "Send a Slack message.")]
        public string SendSlack() => "sent";
    }
#pragma warning restore CA1822
}
