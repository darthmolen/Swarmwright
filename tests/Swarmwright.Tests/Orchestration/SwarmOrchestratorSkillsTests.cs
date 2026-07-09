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
using FluentAssertions;
using Microsoft.Extensions.AI;
using Moq;
using Swarmwright.Hosting;

namespace Swarmwright.Tests.Orchestration;

/// <summary>
/// End-to-end tests verifying skills are wired through the full orchestrator pipeline:
/// template load → skill resolution → spawn → tool list + prompt assembly.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public sealed class SwarmOrchestratorSkillsTests : IDisposable
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

    /// <summary>
    /// Captured tools from the worker's ChatOptions during the execution phase.
    /// </summary>
    private IList<AITool>? capturedWorkerTools;

    /// <summary>
    /// Captured system prompt from the worker's conversation history.
    /// </summary>
    private string? capturedWorkerSystemPrompt;

    /// <summary>
    /// Initializes a new instance of the <see cref="SwarmOrchestratorSkillsTests"/> class.
    /// </summary>
    public SwarmOrchestratorSkillsTests()
    {
        this.testDir = Path.Combine(Path.GetTempPath(), "swarm-skills-e2e-" + Guid.NewGuid().ToString("N"));
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
        this.dbFactory = new InMemoryDbContextFactory("Skills_" + Guid.NewGuid());
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
        this.capturedWorkerSystemPrompt = null;
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
    /// E2E: A worker with <c>skills: [azure-arch]</c> on a template with scripts disabled
    /// receives <c>load_skill</c> and <c>read_skill_resource</c> tools in its ChatOptions,
    /// but NOT <c>run_skill_script</c>. The system prompt contains the skill description.
    /// </summary>
    [TestMethod]
    public async Task RunAsync_WorkerWithNormalSkill_ReceivesLoadAndReadToolsButNotScript()
    {
        // Arrange — create template with one worker and one normal skill.
        var templateKey = "skill-template";
        this.CreateTemplateOnDisk(templateKey, allowSkillScripts: false);
        this.CreateSkillOnDisk(
            templateKey,
            "azure-arch",
            "Cloud architecture decision framework.",
            "Apply Well-Architected Framework five pillars.");
        this.CreateSkillResource(templateKey, "azure-arch", "pillars.md", "Reliability, Security, Cost, Ops, Perf");

        var loader = new TemplateLoader(this.testDir);
        var template = loader.Load(templateKey);

        this.SetupLeaderToCreatePlan("researcher", "Research Specialist");
        this.SetupWorkerToCaptureThenComplete("Worker findings.");

        var orchestrator = new SwarmOrchestrator(
            this.mockLeaderClient.Object,
            _ => this.mockWorkerClient.Object,
            this.eventBus,
            this.agUiAdapter,
            this.swarmService,
            this.transitionService,
            this.options,
            template,
            workDirectory: this.testDir,
            httpClient: this.httpClient,
            templatesDirectory: this.testDir);

        // Act
        await orchestrator.RunAsync(Guid.NewGuid(), "Research Azure architecture.", CancellationToken.None);

        // Assert — worker tools
        this.capturedWorkerTools.Should().NotBeNull("the worker mock should have captured ChatOptions.Tools");
        var toolNames = this.capturedWorkerTools!.Select(t => t.Name).ToList();

        toolNames.Should().Contain("load_skill", "skills should provide load_skill tool");
        toolNames.Should().Contain("read_skill_resource", "skills should provide read_skill_resource tool");
        toolNames.Should().NotContain("run_skill_script", "scripts are disabled for this template");

        // Assert — system prompt contains skill description
        this.capturedWorkerSystemPrompt.Should().NotBeNull();
        this.capturedWorkerSystemPrompt.Should().Contain("## Available Skills");
        this.capturedWorkerSystemPrompt.Should().Contain("azure-arch");
        this.capturedWorkerSystemPrompt.Should().Contain("Cloud architecture decision framework.");

        // Assert — load_skill returns the skill body when invoked
        var loadSkill = this.capturedWorkerTools!
            .OfType<AIFunction>()
            .First(t => t.Name == "load_skill");
        var body = await loadSkill.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["skillName"] = "azure-arch" }));
        body.Should().NotBeNull();
        body!.ToString().Should().Contain("Well-Architected Framework");

        // Assert — read_skill_resource returns reference file content
        var readResource = this.capturedWorkerTools!
            .OfType<AIFunction>()
            .First(t => t.Name == "read_skill_resource");
        var refContent = await readResource.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["skillName"] = "azure-arch",
                ["resourceName"] = "pillars.md",
            }));
        refContent.Should().NotBeNull();
        refContent!.ToString().Should().Contain("Reliability");
    }

    /// <summary>
    /// E2E: A worker with <c>skills: [scripted]</c> on a template with
    /// <c>allow_skill_scripts: true</c> receives all three skill tools including
    /// <c>run_skill_script</c>.
    /// </summary>
    [TestMethod]
    public async Task RunAsync_WorkerWithScriptSkill_ReceivesAllThreeSkillTools()
    {
        // Arrange — create template with scripts enabled.
        var templateKey = "script-template";
        this.CreateTemplateOnDisk(templateKey, allowSkillScripts: true);
        this.CreateSkillOnDisk(
            templateKey,
            "scripted",
            "A skill with executable scripts.",
            "Run the conversion script for unit transformations.");
        this.CreateSkillScript(templateKey, "scripted", "convert.py", "#!/usr/bin/env python3\nprint('converted')");

        var loader = new TemplateLoader(this.testDir);
        var template = loader.Load(templateKey);

        this.SetupLeaderToCreatePlan("researcher", "Research Specialist");
        this.SetupWorkerToCaptureThenComplete("Worker findings.");

        var orchestrator = new SwarmOrchestrator(
            this.mockLeaderClient.Object,
            _ => this.mockWorkerClient.Object,
            this.eventBus,
            this.agUiAdapter,
            this.swarmService,
            this.transitionService,
            this.options,
            template,
            workDirectory: this.testDir,
            httpClient: this.httpClient,
            templatesDirectory: this.testDir);

        // Act
        await orchestrator.RunAsync(Guid.NewGuid(), "Convert units.", CancellationToken.None);

        // Assert — all three skill tools present
        this.capturedWorkerTools.Should().NotBeNull();
        var toolNames = this.capturedWorkerTools!.Select(t => t.Name).ToList();

        toolNames.Should().Contain("load_skill");
        toolNames.Should().Contain("read_skill_resource");
        toolNames.Should().Contain("run_skill_script", "scripts are enabled for this template");

        // Assert — coordination tools still present alongside skill tools
        toolNames.Should().Contain("task_update", "coordination tools must coexist with skill tools");
        toolNames.Should().Contain("task_list");

        // Assert — system prompt contains the skill
        this.capturedWorkerSystemPrompt.Should().Contain("scripted");
        this.capturedWorkerSystemPrompt.Should().Contain("executable scripts");
    }

    // ---- Helpers ----

    private void CreateTemplateOnDisk(string key, bool allowSkillScripts)
    {
        var templateDir = Path.Combine(this.testDir, key);
        Directory.CreateDirectory(templateDir);

        var scriptLine = allowSkillScripts ? "\nallow_skill_scripts: true" : string.Empty;
        var yaml = $"key: {key}\nname: Test\ndescription: Test template\ngoal_template: \"Research: {{user_input}}\"{scriptLine}";

        File.WriteAllText(Path.Combine(templateDir, "_template.yaml"), yaml);
        File.WriteAllText(Path.Combine(templateDir, "leader.md"), "# Leader\n\nYou lead the team.");

        var workerMd = """
            ---
            name: researcher
            displayName: Research Specialist
            description: Researches topics
            skills:
              - azure-arch
              - scripted
            ---

            # Researcher

            You are {display_name}, a {role} specialist.
            """;
        File.WriteAllText(Path.Combine(templateDir, "worker-researcher.md"), workerMd);
        File.WriteAllText(Path.Combine(templateDir, "synthesis.md"), "# Synthesis\n\nSynthesize results.");
    }

    private void CreateSkillOnDisk(string templateKey, string skillName, string description, string body)
    {
        var skillDir = Path.Combine(this.testDir, templateKey, "skills", skillName);
        Directory.CreateDirectory(skillDir);

        var content = $"---\nname: {skillName}\ndescription: {description}\n---\n\n{body}";
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), content);
    }

    private void CreateSkillResource(string templateKey, string skillName, string fileName, string content)
    {
        var refsDir = Path.Combine(this.testDir, templateKey, "skills", skillName, "references");
        Directory.CreateDirectory(refsDir);
        File.WriteAllText(Path.Combine(refsDir, fileName), content);
    }

    private void CreateSkillScript(string templateKey, string skillName, string fileName, string content)
    {
        var scriptsDir = Path.Combine(this.testDir, templateKey, "skills", skillName, "scripts");
        Directory.CreateDirectory(scriptsDir);
        File.WriteAllText(Path.Combine(scriptsDir, fileName), content);
    }

    private void SetupLeaderToCreatePlan(string workerName, string workerRole)
    {
        var planCallCount = 0;

        this.mockLeaderClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns<IList<ChatMessage>, ChatOptions, CancellationToken>(
                (msgs, opts, ct) =>
                {
                    planCallCount++;

                    if (planCallCount == 1 && opts?.Tools != null)
                    {
                        var planTool = opts.Tools
                            .OfType<AIFunction>()
                            .FirstOrDefault(t => t.Name == "create_plan");

                        if (planTool != null)
                        {
                            var tasks = new List<TaskPlan>
                            {
                                new()
                                {
                                    Subject = "Research task",
                                    Description = "Do research.",
                                    WorkerRole = workerRole,
                                    WorkerName = workerName,
                                },
                            };

                            _ = Task.Run(
                                async () =>
                                {
                                    await planTool.InvokeAsync(
                                        new AIFunctionArguments(new Dictionary<string, object?>
                                        {
                                            ["team_description"] = "Research team",
                                            ["tasks"] = JsonSerializer.Serialize(tasks, CamelCaseOptions),
                                        }),
                                        ct);
                                },
                                ct);
                        }
                    }

                    // Synthesis phase — invoke submit_report.
                    if (opts?.Tools != null && planCallCount > 1)
                    {
                        var reportTool = opts.Tools
                            .OfType<AIFunction>()
                            .FirstOrDefault(t => t.Name == "submit_report");

                        if (reportTool != null)
                        {
                            _ = Task.Run(
                                async () =>
                                {
                                    await reportTool.InvokeAsync(
                                        new AIFunctionArguments(new Dictionary<string, object?>
                                        {
                                            ["report"] = "Final report.",
                                        }),
                                        ct);
                                },
                                ct);
                        }
                    }

                    return Task.FromResult(
                        new ChatResponse(new ChatMessage(ChatRole.Assistant, "Acknowledged.")));
                });
    }

    private void SetupWorkerToCaptureThenComplete(string result)
    {
        var funcCall = new FunctionCallContent(
            "call-worker",
            "task_update",
            new Dictionary<string, object?>
            {
                ["task_id"] = "any",
                ["status"] = "Completed",
                ["result"] = result,
            });
        var funcResult = new FunctionResultContent(
            "call-worker",
            "{\"success\":true,\"status\":\"Completed\"}");

        this.mockWorkerClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns<IList<ChatMessage>, ChatOptions, CancellationToken>(
                (msgs, opts, ct) =>
                {
                    // Capture the tools the worker received.
                    this.capturedWorkerTools = opts?.Tools;

                    // Capture the system prompt from conversation history.
                    var systemMsg = msgs.FirstOrDefault(m => m.Role == ChatRole.System);
                    this.capturedWorkerSystemPrompt = systemMsg?.Text;

                    // Invoke task_update so the orchestrator sees completion.
                    if (opts?.Tools != null)
                    {
                        var taskListTool = opts.Tools
                            .OfType<AIFunction>()
                            .FirstOrDefault(t => t.Name == "task_list");

                        var taskUpdateTool = opts.Tools
                            .OfType<AIFunction>()
                            .FirstOrDefault(t => t.Name == "task_update");

                        if (taskListTool != null && taskUpdateTool != null)
                        {
                            _ = Task.Run(
                                async () =>
                                {
                                    // Get real task id from task_list.
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
                                                ["result"] = result,
                                            }),
                                            ct);
                                    }
                                },
                                ct);
                        }
                    }

                    return Task.FromResult(
                        new ChatResponse(
                        [
                            new ChatMessage(ChatRole.Assistant, [funcCall]),
                            new ChatMessage(ChatRole.Tool, [funcResult]),
                            new ChatMessage(ChatRole.Assistant, result),
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
}
