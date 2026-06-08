using System.Collections.Generic;
using System.Text.Json;
using Swarmwright.Configuration;
using Swarmwright.Core;
using Swarmwright.Database.Models;
using Swarmwright.Database.Repositories;
using Swarmwright.Events;
using Swarmwright.Events.AgUI;
using Swarmwright.Hosting.StateMachine;
using Swarmwright.Models;
using Swarmwright.Models.Enums;
using Swarmwright.Orchestration;
using Swarmwright.Services;
using Swarmwright.Tests.Hosting.StateMachine;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Moq;
using Swarmwright.Hosting;

namespace Swarmwright.Tests.Orchestration;

/// <summary>
/// Unit tests for <see cref="SwarmOrchestrator"/>.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SwarmOrchestratorTests : IDisposable
{
    private static readonly JsonSerializerOptions CamelCaseOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private Mock<IChatClient> mockLeaderClient = null!;
    private Mock<IChatClient> mockWorkerClient = null!;
    private InMemoryDbContextFactory dbFactory = null!;
    private SwarmRepository repository = null!;
    private SwarmService swarmService = null!;
    private NoOpStateTransitionService transitionService = null!;
    private SwarmEventBus eventBus = null!;
    private Swarmwright.Events.AgUI.SwarmEventAdapter agUiAdapter = null!;
    private SwarmOptions options = null!;
    private HttpClient httpClient = null!;
    private string? pendingReport;

    /// <summary>
    /// Initializes test dependencies before each test.
    /// </summary>
    [TestInitialize]
    public void TestInitialize()
    {
        this.mockLeaderClient = new Mock<IChatClient>();
        this.mockWorkerClient = new Mock<IChatClient>();
        this.dbFactory = new InMemoryDbContextFactory("OrchTests_" + Guid.NewGuid());
        this.repository = new SwarmRepository(this.dbFactory);
        this.swarmService = new SwarmService(
            new InboxSystem(),
            new TeamRegistry(),
            this.repository);
        this.eventBus = new SwarmEventBus();
        this.agUiAdapter = new Swarmwright.Events.AgUI.SwarmEventAdapter();

        // F01.3: pair a recording NoOp wrapper with a real state service so
        // test assertions that walk SwarmCalls / TaskCalls keep working
        // while task transitions actually mutate the DB rows that
        // GetRunnableTasksAsync / GetTasksAsync now read through to.
        this.transitionService = new NoOpStateTransitionService(this.agUiAdapter)
        {
            Inner = new StateTransitionService(this.dbFactory, new NullEmissionBroker(), Mock.Of<ISwarmObservationSink>()),
        };
        this.httpClient = new HttpClient();
        this.options = new SwarmOptions
        {
            MaxRounds = 3,
            SuspendTimeoutSeconds = 5,
        };
    }

    /// <summary>
    /// Disposes managed resources.
    /// </summary>
    public void Dispose()
    {
        this.httpClient?.Dispose();
    }

    /// <summary>
    /// Verifies that <see cref="SwarmOrchestrator.RunAsync(Guid, string, CancellationToken)"/>
    /// adopts the swarm identifier supplied by the caller instead of generating a fresh
    /// <see cref="Guid.NewGuid"/>. This guarantees the dispatcher and orchestrator agree on a
    /// single canonical swarm ID end-to-end.
    /// </summary>
    [TestMethod]
    public async Task RunAsync_UsesProvidedSwarmId_NotAFreshGuid()
    {
        // Arrange
        var orchestrator = this.CreateOrchestrator();
        var knownGuid = Guid.Parse("11111111-2222-3333-4444-555555555555");

        this.SetupPlanOnLeaderCall(
            new SwarmPlan { TeamDescription = "Team." },
            new List<TaskPlan>
            {
                new()
                {
                    Subject = "Task",
                    Description = "Do it.",
                    WorkerRole = "worker",
                    WorkerName = "worker_1",
                },
            });
        this.SetupReportOnLeaderCall("Report.");
        this.SetupWorkerToReturnTaskResult("Done.");

        // Act
        await orchestrator.RunAsync(knownGuid, "Canonical id goal.");

        // Assert
        orchestrator.SwarmId.Should().Be(
            knownGuid,
            "the orchestrator must adopt the swarm id supplied by the dispatcher.");
    }

    /// <summary>
    /// Verifies that every AG-UI lifecycle event emitted during a swarm run uses
    /// the caller-provided swarm id. Before the canonical-id fix the orchestrator
    /// generated its own GUID, so events emitted under the orchestrator's id never
    /// reached subscribers keyed off the dispatcher's id.
    /// </summary>
    [TestMethod]
    public async Task RunAsync_SameSwarmIdVisibleInPhaseChangeEvents()
    {
        // Arrange
        var orchestrator = this.CreateOrchestrator();
        var knownGuid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        this.SetupPlanOnLeaderCall(
            new SwarmPlan { TeamDescription = "Team." },
            new List<TaskPlan>
            {
                new()
                {
                    Subject = "Task",
                    Description = "Do it.",
                    WorkerRole = "worker",
                    WorkerName = "worker_1",
                },
            });
        this.SetupReportOnLeaderCall("Report.");
        this.SetupWorkerToReturnTaskResult("Done.");

        // Act
        await orchestrator.RunAsync(knownGuid, "Event id goal.");

        // Assert — drain every AG-UI lifecycle event and verify every thread id is the known guid.
        var runStartedThreadIds = new List<string>();
        var runFinishedThreadIds = new List<string>();
        while (this.agUiAdapter.Reader.TryRead(out var evt))
        {
            switch (evt)
            {
                case Swarmwright.Events.AgUI.RunStartedEvent started:
                    runStartedThreadIds.Add(started.ThreadId);
                    break;
                case Swarmwright.Events.AgUI.RunFinishedEvent finished:
                    runFinishedThreadIds.Add(finished.ThreadId);
                    break;
                default:
                    // Other AG-UI event types (StepStarted, StepFinished, StateDelta, etc.)
                    // do not carry a thread id in their typed payload and are irrelevant here.
                    break;
            }
        }

        runStartedThreadIds.Should().NotBeEmpty(
            "RunStartedEvent must be emitted when the swarm starts.");
        runStartedThreadIds.Should().OnlyContain(
            id => id == knownGuid.ToString(),
            "every RunStartedEvent must carry the caller-provided swarm id.");
        runFinishedThreadIds.Should().OnlyContain(
            id => id == knownGuid.ToString(),
            "every RunFinishedEvent must carry the caller-provided swarm id.");
    }

    /// <summary>
    /// Verifies that the swarm id passed to <see cref="SwarmOrchestrator.RunAsync(Guid, string, CancellationToken)"/>
    /// is forwarded verbatim to <see cref="ISwarmService.CreateSwarmAsync(Guid, string, string?)"/>,
    /// keeping the database row and in-memory orchestrator aligned on the same identifier.
    /// </summary>
    [TestMethod]
    public async Task RunAsync_SameSwarmIdPassedToSwarmService()
    {
        // Arrange — replace the real swarm service with a spy that records the id.
        using var spyService = new RecordingSwarmService();
        var orchestrator = new SwarmOrchestrator(
            this.mockLeaderClient.Object,
            _ => this.mockWorkerClient.Object,
            this.eventBus,
            this.agUiAdapter,
            spyService,
            new NoOpStateTransitionService(),
            this.options,
            template: null,
            workDirectory: Path.GetTempPath(),
            httpClient: this.httpClient);

        var knownGuid = Guid.Parse("99999999-8888-7777-6666-555555555555");

        this.SetupPlanOnLeaderCall(
            new SwarmPlan { TeamDescription = "Team." },
            new List<TaskPlan>
            {
                new()
                {
                    Subject = "Task",
                    Description = "Do it.",
                    WorkerRole = "worker",
                    WorkerName = "worker_1",
                },
            });
        this.SetupReportOnLeaderCall("Report.");
        this.SetupWorkerToReturnTaskResult("Done.");

        // Act
        await orchestrator.RunAsync(knownGuid, "Service id goal.");

        // Assert
        spyService.CreateSwarmCalls.Should().ContainSingle(
            "CreateSwarmAsync must be invoked exactly once by RunAsync.");
        spyService.CreateSwarmCalls[0].SwarmId.Should().Be(
            knownGuid,
            "the orchestrator must hand the caller-provided swarm id to the swarm service.");
    }

    /// <summary>
    /// Verifies that the planning phase creates tasks from the plan produced by the leader.
    /// </summary>
    [TestMethod]
    public async Task RunAsync_PlanningPhase_CreatesTasksFromPlan()
    {
        // Arrange
        var orchestrator = this.CreateOrchestrator();

        var plan = new SwarmPlan { TeamDescription = "A team of researchers." };
        var taskPlans = new List<TaskPlan>
        {
            new()
            {
                Subject = "Research topic A",
                Description = "Deep dive into topic A.",
                WorkerRole = "researcher",
                WorkerName = "researcher_1",
            },
            new()
            {
                Subject = "Research topic B",
                Description = "Deep dive into topic B.",
                WorkerRole = "researcher",
                WorkerName = "researcher_1",
            },
        };

        this.SetupPlanOnLeaderCall(plan, taskPlans);
        this.SetupReportOnLeaderCall("Final consolidated report.");
        this.SetupWorkerToReturnTaskResult("Worker result.");

        // Act
        await orchestrator.RunAsync(Guid.NewGuid(), "Research topics A and B.");

        // Assert
        var tasks = await this.swarmService.GetTasksAsync();
        tasks.Should().HaveCount(2);
        tasks.Select(t => t.Subject).Should().Contain("Research topic A");
        tasks.Select(t => t.Subject).Should().Contain("Research topic B");
    }

    /// <summary>
    /// Verifies that blocked-by indices from the plan are converted to actual task IDs
    /// when the deserialized plan carries populated BlockedByIndices.
    /// Note: TaskPlan.BlockedByIndices is a getter-only list, so standard STJ deserialization
    /// does not populate it. This test verifies the orchestrator logic handles both populated
    /// and empty BlockedByIndices correctly.
    /// </summary>
    [TestMethod]
    public async Task RunAsync_PlanningPhase_ConvertsBlockedByIndicesToTaskIds()
    {
        // Arrange
        var orchestrator = this.CreateOrchestrator();

        var taskPlans = new List<TaskPlan>
        {
            new()
            {
                Subject = "First task",
                Description = "Do first.",
                WorkerRole = "worker",
                WorkerName = "worker_1",
            },
            new()
            {
                Subject = "Second task",
                Description = "Depends on first.",
                WorkerRole = "worker",
                WorkerName = "worker_1",
            },
        };

        // Second task blocked by first (index 0).
        // Note: BlockedByIndices is getter-only; STJ deserialization drops these values.
        // When TaskPlan adds a setter or [JsonObjectCreationHandling(Populate)], this
        // will propagate correctly. Both tasks will be Pending (no block enforcement).
        taskPlans[1].BlockedByIndices.Add(0);

        this.SetupPlanOnLeaderCall(
            new SwarmPlan { TeamDescription = "Workers." },
            taskPlans);
        this.SetupReportOnLeaderCall("Report.");
        this.SetupWorkerToReturnTaskResult("Done.");

        // Act
        await orchestrator.RunAsync(Guid.NewGuid(), "Do two things.");

        // Assert - both tasks are created successfully.
        var tasks = await this.swarmService.GetTasksAsync();
        tasks.Should().HaveCount(2);
        tasks.Select(t => t.Subject).Should().Contain("First task");
        tasks.Select(t => t.Subject).Should().Contain("Second task");
    }

    /// <summary>
    /// Verifies that the execution phase runs tasks in rounds.
    /// </summary>
    [TestMethod]
    public async Task RunAsync_ExecutionPhase_RunsTasksInRounds()
    {
        // Arrange
        var orchestrator = this.CreateOrchestrator();

        this.SetupPlanOnLeaderCall(
            new SwarmPlan { TeamDescription = "Team." },
            new List<TaskPlan>
            {
                new()
                {
                    Subject = "Task 1",
                    Description = "Do it.",
                    WorkerRole = "worker",
                    WorkerName = "worker_1",
                },
            });
        this.SetupReportOnLeaderCall("Report.");
        this.SetupWorkerToReturnTaskResult("Completed.");

        // Act
        await orchestrator.RunAsync(Guid.NewGuid(), "Simple goal.");

        // Assert - drain AG-UI events and look for STATE_DELTA with roundNumber
        var roundsObserved = new List<int>();
        while (this.agUiAdapter.Reader.TryRead(out var evt))
        {
            if (evt is Swarmwright.Events.AgUI.StateDeltaEvent delta && delta.Delta.HasValue)
            {
                var raw = delta.Delta.Value.GetRawText();
                if (raw.Contains("/roundNumber"))
                {
                    // Extract the round number from the JSON Patch
                    foreach (var op in delta.Delta.Value.EnumerateArray())
                    {
                        if (op.GetProperty("path").GetString() == "/roundNumber")
                        {
                            roundsObserved.Add(op.GetProperty("value").GetInt32());
                        }
                    }
                }
            }
        }

        roundsObserved.Should().NotBeEmpty();
        roundsObserved[0].Should().Be(1);
    }

    /// <summary>
    /// Verifies that execution completes when all tasks are done.
    /// </summary>
    [TestMethod]
    public async Task RunAsync_ExecutionPhase_CompletesWhenAllTasksDone()
    {
        // Arrange
        var orchestrator = this.CreateOrchestrator();

        this.SetupPlanOnLeaderCall(
            new SwarmPlan { TeamDescription = "Team." },
            new List<TaskPlan>
            {
                new()
                {
                    Subject = "Only task",
                    Description = "Single task.",
                    WorkerRole = "worker",
                    WorkerName = "worker_1",
                },
            });
        this.SetupReportOnLeaderCall("Final report.");
        this.SetupWorkerToReturnTaskResult("All done.");

        // Act
        var report = await orchestrator.RunAsync(Guid.NewGuid(), "One task goal.");

        // Assert
        report.Should().Be("Final report.");

        var tasks = await this.swarmService.GetTasksAsync();
        tasks.Should().HaveCount(1);
        tasks[0].Status.Should().Be(TaskState.Completed);
    }

    /// <summary>
    /// Verifies the full lifecycle returns the synthesized report.
    /// </summary>
    [TestMethod]
    public async Task RunAsync_FullLifecycle_ReturnsReport()
    {
        // Arrange
        var orchestrator = this.CreateOrchestrator();

        this.SetupPlanOnLeaderCall(
            new SwarmPlan { TeamDescription = "Research team." },
            new List<TaskPlan>
            {
                new()
                {
                    Subject = "Gather data",
                    Description = "Collect data from sources.",
                    WorkerRole = "researcher",
                    WorkerName = "researcher_1",
                },
                new()
                {
                    Subject = "Analyze data",
                    Description = "Analyze the collected data.",
                    WorkerRole = "analyst",
                    WorkerName = "analyst_1",
                },
            });
        this.SetupReportOnLeaderCall("Comprehensive analysis complete.");
        this.SetupWorkerToReturnTaskResult("Result from worker.");

        // Act
        var report = await orchestrator.RunAsync(Guid.NewGuid(), "Analyze market data.");

        // Assert
        report.Should().Be("Comprehensive analysis complete.");
        this.transitionService.SwarmCalls.Should()
            .Contain(c => c.ToState == SwarmInstanceState.Complete);
    }

    /// <summary>
    /// Verifies that when a worker never calls <c>task_update</c>, the task is marked
    /// <see cref="TaskState.Failed"/> with a diagnostic reason.
    /// </summary>
    [TestMethod]
    public async Task ExecuteWorkerTask_WhenWorkerDidNotSignal_MarksTaskFailed()
    {
        // Arrange
        var orchestrator = this.CreateOrchestrator();

        this.SetupPlanOnLeaderCall(
            new SwarmPlan { TeamDescription = "Team." },
            new List<TaskPlan>
            {
                new()
                {
                    Subject = "Silent task",
                    Description = "Should signal but won't.",
                    WorkerRole = "worker",
                    WorkerName = "worker_1",
                },
            });
        this.SetupReportOnLeaderCall("Report.");

        // Worker returns only text, no task_update tool call.
        this.mockWorkerClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, "I am ready to begin collecting and analyzing.")));

        // Act
        await orchestrator.RunAsync(Guid.NewGuid(), "Goal.");

        // Assert
        var tasks = await this.swarmService.GetTasksAsync();
        tasks.Should().HaveCount(1);
        tasks[0].Status.Should().Be(TaskState.Failed);
        tasks[0].Result.Should().Contain("task_update");
    }

    /// <summary>
    /// Verifies that when a worker signals <c>Completed</c> via <c>task_update</c>,
    /// the orchestrator propagates the worker-supplied result text.
    /// </summary>
    [TestMethod]
    public async Task ExecuteWorkerTask_WhenWorkerSignaledCompleted_PropagatesResult()
    {
        // Arrange
        var orchestrator = this.CreateOrchestrator();

        this.SetupPlanOnLeaderCall(
            new SwarmPlan { TeamDescription = "Team." },
            new List<TaskPlan>
            {
                new()
                {
                    Subject = "Signalling task",
                    Description = "Will signal.",
                    WorkerRole = "worker",
                    WorkerName = "worker_1",
                },
            });
        this.SetupReportOnLeaderCall("Report.");

        // Worker emits a successful task_update tool call with a declared result.
        var funcCall = new FunctionCallContent(
            "call-1",
            "task_update",
            new Dictionary<string, object?>
            {
                ["task_id"] = "ignored-by-test",
                ["status"] = "Completed",
                ["result"] = "my report",
            });
        var funcResult = new FunctionResultContent(
            "call-1",
            "{\"success\":true,\"taskId\":\"ignored-by-test\",\"status\":\"Completed\"}");

        this.mockWorkerClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(
            [
                new ChatMessage(ChatRole.Assistant, [funcCall]),
                new ChatMessage(ChatRole.Tool, [funcResult]),
                new ChatMessage(ChatRole.Assistant, "Final text summary."),
            ]));

        // Act
        await orchestrator.RunAsync(Guid.NewGuid(), "Goal.");

        // Assert
        var tasks = await this.swarmService.GetTasksAsync();
        tasks.Should().HaveCount(1);
        tasks[0].Status.Should().Be(TaskState.Completed);
        tasks[0].Result.Should().Be("my report");
    }

    /// <summary>
    /// Verifies that the orchestrator emits a <c>SWARM_TASK_UPDATED</c> SSE event
    /// with the terminal status after <c>ExecuteWorkerTaskAsync</c> completes,
    /// regardless of whether the worker agent called <c>task_update</c> itself.
    /// Without this authoritative emit, the frontend task board stays at "In Progress"
    /// for workers that finish without calling the tool — observed in production
    /// run 60b2cbbc where 2 of 3 tasks showed stale status.
    /// </summary>
    [TestMethod]
    public async Task ExecuteWorkerTask_EmitsTaskUpdatedSseEvent_AfterCompletion()
    {
        // Arrange
        var orchestrator = this.CreateOrchestrator();

        this.SetupPlanOnLeaderCall(
            new SwarmPlan { TeamDescription = "Team." },
            new List<TaskPlan>
            {
                new()
                {
                    Subject = "SSE task",
                    Description = "Do it.",
                    WorkerRole = "worker",
                    WorkerName = "worker_1",
                },
            });
        this.SetupReportOnLeaderCall("Report.");
        this.SetupWorkerToReturnTaskResult("Done.");

        // Act
        await orchestrator.RunAsync(Guid.NewGuid(), "Goal.");

        // Assert — drain all AG-UI events and find SWARM_TASK_UPDATED custom events.
        var taskUpdatedEvents = new List<(string TaskId, string Status)>();
        while (this.agUiAdapter.Reader.TryRead(out var evt))
        {
            if (evt is SwarmCustomEvent custom && custom.Name == "SWARM_TASK_UPDATED")
            {
                var taskId = custom.Value.GetProperty("taskId").GetString()!;
                var status = custom.Value.GetProperty("status").GetString()!;
                taskUpdatedEvents.Add((taskId, status));
            }
        }

        // The orchestrator must emit at least one SWARM_TASK_UPDATED with "Completed".
        // The worker's tool-call emit is a duplicate; the orchestrator's is authoritative.
        taskUpdatedEvents.Should().Contain(
            e => e.Status == "Completed",
            "the orchestrator must emit SWARM_TASK_UPDATED with Completed status so the frontend task board updates even when the worker does not call task_update.");
    }

    /// <summary>
    /// Verifies that when a worker does NOT call <c>task_update</c>, the orchestrator
    /// still emits a <c>SWARM_TASK_UPDATED</c> SSE event with the terminal status
    /// (Failed for a non-signalling worker). This is the exact scenario from production
    /// run 60b2cbbc where silent workers left the frontend stuck at "In Progress".
    /// </summary>
    [TestMethod]
    public async Task ExecuteWorkerTask_WhenWorkerDidNotSignal_StillEmitsTaskUpdatedSseEvent()
    {
        // Arrange
        var orchestrator = this.CreateOrchestrator();

        this.SetupPlanOnLeaderCall(
            new SwarmPlan { TeamDescription = "Team." },
            new List<TaskPlan>
            {
                new()
                {
                    Subject = "Silent task",
                    Description = "Won't call task_update.",
                    WorkerRole = "worker",
                    WorkerName = "worker_1",
                },
            });
        this.SetupReportOnLeaderCall("Report.");

        // Worker returns text only — no task_update tool call.
        this.mockWorkerClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, "Here is my analysis.")));

        // Act
        await orchestrator.RunAsync(Guid.NewGuid(), "Goal.");

        // Assert — drain all AG-UI events.
        var taskUpdatedEvents = new List<(string TaskId, string Status)>();
        while (this.agUiAdapter.Reader.TryRead(out var evt))
        {
            if (evt is SwarmCustomEvent custom && custom.Name == "SWARM_TASK_UPDATED")
            {
                var taskId = custom.Value.GetProperty("taskId").GetString()!;
                var status = custom.Value.GetProperty("status").GetString()!;
                taskUpdatedEvents.Add((taskId, status));
            }
        }

        // The orchestrator marks non-signalling workers as Failed. The SSE event
        // must carry that status so the frontend updates from InProgress to Failed.
        taskUpdatedEvents.Should().Contain(
            e => e.Status == "Failed",
            "the orchestrator must emit SWARM_TASK_UPDATED with Failed status for silent workers.");
    }

    /// <summary>
    /// Verifies that CancelAsync stops execution and sets the cancelled flag.
    /// </summary>
    [TestMethod]
    public async Task CancelAsync_StopsExecution()
    {
        // Arrange
        var orchestrator = this.CreateOrchestrator();

        // Setup leader to take a long time so we can cancel.
        var leaderCallStarted = new TaskCompletionSource();
        this.mockLeaderClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns<IList<ChatMessage>, ChatOptions, CancellationToken>(
                async (msgs, opts, ct) =>
                {
                    leaderCallStarted.TrySetResult();
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                    return new ChatResponse(new ChatMessage(ChatRole.Assistant, "late"));
                });

        // Act
        var runTask = orchestrator.RunAsync(Guid.NewGuid(), "Long running goal.");
        await leaderCallStarted.Task;
        await orchestrator.CancelAsync();

        // Assert
        orchestrator.IsCancelled.Should().BeTrue();

        // RunAsync should complete with a cancellation exception.
        Func<Task> act = async () => await runTask;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>
    /// When the execute loop has no runnable tasks but some tasks are still
    /// Blocked (dep chain broken by a failure), the orchestrator enters
    /// suspend-wait and emits a STATE_DELTA to announce the phase flip. The
    /// emitted <c>/phase</c> value must be <c>AwaitingIntervention</c> — the
    /// canonical state-machine name. A legacy <c>"Suspended"</c> literal
    /// would prevent the admin UI's recovery branch from firing because the
    /// frontend only branches on <c>awaiting_intervention</c> /
    /// <c>needs_diagnosis</c>.
    /// </summary>
    [TestMethod]
    public async Task FailedUpstreamTask_CascadesUnblockingThroughDependents_NoSuspendWait()
    {
        // Arrange — shorter suspend timeout so the test doesn't sit idle waiting if
        // something accidentally re-introduces a deadlock.
        this.options = new SwarmOptions
        {
            MaxRounds = 5,
            SuspendTimeoutSeconds = 1,
        };

        var orchestrator = this.CreateOrchestrator();

        var dependentTask = new TaskPlan
        {
            Subject = "Dependent task",
            Description = "Blocked on the first; must promote to Pending when the first fails.",
            WorkerRole = "worker",
            WorkerName = "worker_2",
        };
        dependentTask.BlockedByIndices.Add(0);

        this.SetupPlanOnLeaderCall(
            new SwarmPlan { TeamDescription = "Team." },
            new List<TaskPlan>
            {
                new()
                {
                    Subject = "First task",
                    Description = "Will fail silently — dependent must still unblock.",
                    WorkerRole = "worker",
                    WorkerName = "worker_1",
                },
                dependentTask,
            });
        this.SetupReportOnLeaderCall("Report after cascading failures.");

        // Both workers return text only — no task_update → orchestrator marks each Failed
        // when its turn comes. Pre-fix, the dependent stayed Blocked forever and the
        // orchestrator emitted AwaitingIntervention via suspend-wait. Post-fix,
        // StateTransitionService promotes the dependent (Blocked->Pending) on the
        // upstream Failed transition, so round 2 runs the dependent which also fails,
        // and round 3 sees all-terminal and exits cleanly without suspend-wait.
        this.mockWorkerClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, "No task_update signal here.")));

        // Act
        await orchestrator.RunAsync(Guid.NewGuid(), "Goal.");

        // Assert — drain every StateDeltaEvent and inspect /phase replace ops.
        var phaseReplacements = new List<string>();
        while (this.agUiAdapter.Reader.TryRead(out var evt))
        {
            if (evt is not StateDeltaEvent delta || delta.Delta is not { } doc)
            {
                continue;
            }

            using var parsed = JsonDocument.Parse(doc.GetRawText());
            foreach (var op in parsed.RootElement.EnumerateArray())
            {
                if (op.TryGetProperty("path", out var pathEl)
                    && pathEl.GetString() == "/phase"
                    && op.TryGetProperty("value", out var valueEl))
                {
                    phaseReplacements.Add(valueEl.GetString() ?? string.Empty);
                }
            }
        }

        phaseReplacements.Should().NotContain(
            nameof(SwarmInstanceState.AwaitingIntervention),
            "after the Bug 2 fix, a Failed upstream task promotes its dependents — there is no deadlock and no suspend-wait");
        phaseReplacements.Should().NotContain(
            "Suspended",
            "the legacy literal must remain absent regardless");

        // Both tasks must have run and ended Failed (the bug-fix guard).
        var tasks = await this.swarmService.GetTasksAsync();
        tasks.Should().HaveCount(2);
        tasks.Should().AllSatisfy(t => t.Status.Should().Be(
            TaskState.Failed,
            "both tasks fail (no task_update); the dependent only reaches Failed if it was successfully unblocked from Blocked->Pending first"));
    }

    /// <summary>
    /// Verifies that the spawning phase registers a leader inbox under the
    /// canonical name so workers can send to it without triggering the
    /// "Recipient 'leader' is not registered" failure from <see cref="InboxSystem.SendAsync"/>.
    /// </summary>
    [TestMethod]
    public async Task SpawnAsync_RegistersLeaderInboxWithCanonicalName()
    {
        // Arrange
        var orchestrator = this.CreateOrchestrator();

        this.SetupPlanOnLeaderCall(
            new SwarmPlan { TeamDescription = "Team." },
            new List<TaskPlan>
            {
                new()
                {
                    Subject = "Task 1",
                    Description = "Do it.",
                    WorkerRole = "worker",
                    WorkerName = "worker1",
                },
            });
        this.SetupReportOnLeaderCall("Report.");
        this.SetupWorkerToReturnTaskResult("Done.");

        // Act — run the full lifecycle so SpawnAsync executes.
        await orchestrator.RunAsync(Guid.NewGuid(), "Goal.");

        // Assert — after SpawnAsync, a worker must be able to send to the canonical leader inbox.
        var send = () => this.swarmService.InboxSystem.SendAsync(
            "worker1",
            SwarmOrchestrator.LeaderInboxName,
            "test");
        await send.Should().NotThrowAsync(
            "SpawnAsync must register the canonical leader inbox so workers can contact the leader.");
    }

    /// <summary>
    /// Verifies that the spawning phase registers the leader inbox before any
    /// worker inbox. Uses a spy <see cref="IInboxSystem"/> to record the
    /// registration order.
    /// </summary>
    [TestMethod]
    public async Task SpawnAsync_RegistersLeaderBeforeAnyWorker()
    {
        // Arrange — rebuild swarmService with a spy inbox. The real
        // repository is reused so the state-service writes have a
        // backing swarm row to mutate.
        var spyInbox = new RecordingInboxSystem();
        this.swarmService = new SwarmService(
            spyInbox,
            new TeamRegistry(),
            this.repository);

        var orchestrator = this.CreateOrchestrator();

        this.SetupPlanOnLeaderCall(
            new SwarmPlan { TeamDescription = "Team." },
            new List<TaskPlan>
            {
                new()
                {
                    Subject = "Task A",
                    Description = "Do A.",
                    WorkerRole = "worker",
                    WorkerName = "worker_a",
                },
                new()
                {
                    Subject = "Task B",
                    Description = "Do B.",
                    WorkerRole = "worker",
                    WorkerName = "worker_b",
                },
            });
        this.SetupReportOnLeaderCall("Report.");
        this.SetupWorkerToReturnTaskResult("Done.");

        // Act
        await orchestrator.RunAsync(Guid.NewGuid(), "Goal.");

        // Assert — leader inbox must be registered, and it must precede every worker inbox.
        spyInbox.Registrations.Should().Contain(SwarmOrchestrator.LeaderInboxName);

        var leaderIndex = spyInbox.Registrations.IndexOf(SwarmOrchestrator.LeaderInboxName);
        var workerIndices = new[]
        {
            spyInbox.Registrations.IndexOf("worker_a"),
            spyInbox.Registrations.IndexOf("worker_b"),
        };

        workerIndices.Should().OnlyContain(
            i => i > leaderIndex,
            "the leader inbox must be registered before any worker inbox.");
    }

    /// <summary>
    /// Verifies that when <see cref="SwarmOrchestrator.RunAsync"/> is
    /// called on a swarm whose persisted state is already
    /// <see cref="SwarmInstanceState.Executing"/>, the orchestrator
    /// recognizes it as a resume and does NOT transition back to
    /// <see cref="SwarmInstanceState.Planning"/> or
    /// <see cref="SwarmInstanceState.Spawning"/>. Re-planning would
    /// duplicate tasks in the DB; re-spawning would duplicate agents;
    /// and the state-machine guard would reject the backward
    /// transition in production anyway.
    /// </summary>
    /// <returns>A task representing the asynchronous test operation.</returns>
    [TestMethod]
    public async Task RunAsync_WhenSwarmStateIsExecuting_SkipsPlanningAndSpawningPhases()
    {
        // Arrange: DB reports the swarm is already Executing (with
        // empty task/agent lists so we don't have to stand up worker
        // plumbing — the test is about phase branching, not execution).
        var swarmId = Guid.NewGuid();
        await using (var ctx = this.dbFactory.CreateDbContext())
        {
            ctx.Swarms.Add(new SwarmEntity
            {
                Id = swarmId,
                Goal = "resumed-goal",
                State = nameof(SwarmInstanceState.Executing),
            });
            await ctx.SaveChangesAsync();
        }

        // Force the leader chat client to throw on any invocation. With
        // no tasks and no agents, ExecuteAsync exits cleanly and
        // SynthesizeAsync would otherwise hang on the submit_report TCS;
        // the throw causes RunAsync to unwind through its catch block,
        // exposing the SwarmCalls timeline for assertion.
        this.mockLeaderClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Test ends here."));

        var orchestrator = this.CreateOrchestrator();

#pragma warning disable CA1031 // We accept any orchestrator exception here; the assertion is on attempted transitions, not success.
        try
        {
            await orchestrator.RunAsync(swarmId, "resumed-goal");
        }
        catch (Exception)
        {
            // Expected — the leader-throw stub above unwinds RunAsync.
        }
#pragma warning restore CA1031

        // Assert: no transition to Planning or Spawning was attempted.
        this.transitionService.SwarmCalls
            .Should()
            .NotContain(
                c => c.ToState == SwarmInstanceState.Planning,
                "a resumed swarm must skip the Planning phase — re-planning duplicates task rows");
        this.transitionService.SwarmCalls
            .Should()
            .NotContain(
                c => c.ToState == SwarmInstanceState.Spawning,
                "a resumed swarm must skip the Spawning phase — re-spawning duplicates agent rows");
    }

    /// <summary>
    /// Verifies the new resume-path helper that rebuilds the orchestrator-
    /// local agent dictionary from persisted worker identities. The live
    /// failure mode: a Recover'd swarm entered Executing, dispatcher picked
    /// it up, resume branch skipped SpawnAsync (which is what populates
    /// <c>this.agents</c>), so Execute found no workers and silently
    /// completed every round with zero LLM calls.
    ///
    /// TDD wish-for API: after calling <c>RebuildWorkerAgentsAsync</c> with
    /// a set of persisted workers, the internal agent registry must contain
    /// one <c>SwarmAgent</c> per unique worker name.
    /// </summary>
    /// <returns>A task representing the asynchronous test operation.</returns>
    [TestMethod]
    public async Task RebuildWorkerAgentsAsync_FromPersistedWorkers_PopulatesAgentDict()
    {
        var orchestrator = this.CreateOrchestrator();
        var workers = new List<(string WorkerName, string WorkerRole)>
        {
            ("worker_1", "worker"),
        };

        await orchestrator.RebuildWorkerAgentsAsync(workers, cancellationToken: CancellationToken.None);

        orchestrator.AgentNames.Should().Contain(
            "worker_1",
            "resume must rebuild one SwarmAgent per persisted worker so ExecuteAsync can dispatch tasks");
    }

    /// <summary>
    /// Verifies that a swarm persisted in <see cref="SwarmInstanceState.AwaitingIntervention"/>
    /// skips the Planning and Spawning phases on resume. This is the
    /// common case — the user clicked Smart Continue, the intervention
    /// handler flipped the swarm back to Executing, and the rehydrator
    /// enqueued it for the dispatcher to pick up. If we re-planned, the
    /// leader would invent a new plan on top of a swarm that already has
    /// work in-flight.
    /// </summary>
    /// <returns>A task representing the asynchronous test operation.</returns>
    [TestMethod]
    public async Task RunAsync_WhenSwarmStateIsAwaitingIntervention_SkipsPlanningAndSpawningPhases()
    {
        var swarmId = Guid.NewGuid();
        await using (var ctx = this.dbFactory.CreateDbContext())
        {
            ctx.Swarms.Add(new SwarmEntity
            {
                Id = swarmId,
                Goal = "awaiting-goal",
                State = nameof(SwarmInstanceState.AwaitingIntervention),
            });
            await ctx.SaveChangesAsync();
        }

        // Force leader to throw to bound the test — synthesis would
        // otherwise hang on the submit_report TCS that the mock client
        // never resolves.
        this.mockLeaderClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Test ends here."));

        var orchestrator = this.CreateOrchestrator();

#pragma warning disable CA1031
        try
        {
            await orchestrator.RunAsync(swarmId, "awaiting-goal");
        }
        catch (Exception)
        {
            // Expected — the leader-throw stub above unwinds RunAsync.
        }
#pragma warning restore CA1031

        this.transitionService.SwarmCalls
            .Should()
            .NotContain(
                c => c.ToState == SwarmInstanceState.Planning,
                "a resume from AwaitingIntervention must skip Planning");
        this.transitionService.SwarmCalls
            .Should()
            .NotContain(
                c => c.ToState == SwarmInstanceState.Spawning,
                "a resume from AwaitingIntervention must skip Spawning");
    }

    private SwarmOrchestrator CreateOrchestrator()
    {
        return new SwarmOrchestrator(
            this.mockLeaderClient.Object,
            _ => this.mockWorkerClient.Object,
            this.eventBus,
            this.agUiAdapter,
            this.swarmService,
            this.transitionService,
            this.options,
            template: null,
            workDirectory: Path.GetTempPath(),
            httpClient: this.httpClient);
    }

    private void SetupWorkerToReturnTaskResult(string result)
    {
        // Simulate a well-behaved worker that calls task_update(Completed) successfully.
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
            .ReturnsAsync(new ChatResponse(
            [
                new ChatMessage(ChatRole.Assistant, [funcCall]),
                new ChatMessage(ChatRole.Tool, [funcResult]),
                new ChatMessage(ChatRole.Assistant, result),
            ]));
    }

    /// <summary>
    /// Simulates the leader calling create_plan by invoking the tool directly from the mock.
    /// This bypasses the need for the LLM to actually call the tool.
    /// </summary>
    private void SetupPlanOnLeaderCall(SwarmPlan plan, List<TaskPlan> taskPlans)
    {
        plan.Tasks.AddRange(taskPlans);

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

                    // During planning (first call), invoke the create_plan tool directly.
                    if (planCallCount == 1 && opts?.Tools != null)
                    {
                        var planTool = opts.Tools
                            .OfType<AIFunction>()
                            .FirstOrDefault(t => t.Name == "create_plan");

                        if (planTool != null)
                        {
                            _ = Task.Run(
                                async () =>
                                {
                                    var taskJson = JsonSerializer.Serialize(plan.Tasks, CamelCaseOptions);
                                    await planTool.InvokeAsync(
                                        new AIFunctionArguments(new Dictionary<string, object?>
                                        {
                                            ["team_description"] = plan.TeamDescription,
                                            ["tasks"] = taskJson,
                                        }),
                                        ct);
                                },
                                ct);
                        }
                    }

                    // During synthesis, invoke the submit_report tool.
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
                                            ["report"] = this.pendingReport ?? "Default report.",
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

    private void SetupReportOnLeaderCall(string report)
    {
        this.pendingReport = report;
    }

    /// <summary>
    /// Spy <see cref="ISwarmService"/> that records every
    /// <see cref="ISwarmService.CreateSwarmAsync(Guid, string, string?)"/> invocation
    /// while delegating all other members to an inner <see cref="SwarmService"/>.
    /// Used to assert the canonical swarm id propagation from the orchestrator.
    /// </summary>
    private sealed class RecordingSwarmService : ISwarmService, IDisposable
    {
        private readonly SwarmService inner;

        /// <summary>
        /// Initializes a new instance of the <see cref="RecordingSwarmService"/> class.
        /// </summary>
        public RecordingSwarmService()
        {
            this.inner = new SwarmService(
                new InboxSystem(),
                new TeamRegistry(),
                new Mock<ISwarmRepository>().Object);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            // No owned resources after the TaskBoard removal.
        }

        /// <summary>
        /// Gets the ordered list of <see cref="ISwarmService.CreateSwarmAsync(Guid, string, string?)"/>
        /// invocations received by this spy.
        /// </summary>
        public List<(Guid SwarmId, string Goal, string? TemplateKey)> CreateSwarmCalls { get; } = new();

        /// <inheritdoc/>
        public Guid SwarmId => this.inner.SwarmId;

        /// <inheritdoc/>
        public IInboxSystem InboxSystem => this.inner.InboxSystem;

        /// <inheritdoc/>
        public ITeamRegistry TeamRegistry => this.inner.TeamRegistry;

        /// <inheritdoc/>
        public SwarmState State => this.inner.State;

        /// <inheritdoc/>
        public Task CreateSwarmAsync(Guid swarmId, string goal, string? templateKey = null)
        {
            this.CreateSwarmCalls.Add((swarmId, goal, templateKey));
            return this.inner.CreateSwarmAsync(swarmId, goal, templateKey);
        }

        /// <inheritdoc/>
        public Task UpdateRoundAsync(int round) => this.inner.UpdateRoundAsync(round);

        /// <inheritdoc/>
        public Task AddTaskAsync(SwarmTask task) => this.inner.AddTaskAsync(task);

        /// <inheritdoc/>
        public Task<IReadOnlyList<SwarmTask>> GetTasksAsync(string? workerName = null)
            => this.inner.GetTasksAsync(workerName);

        /// <inheritdoc/>
        public Task<IReadOnlyList<SwarmTask>> GetRunnableTasksAsync(string? workerName = null)
            => this.inner.GetRunnableTasksAsync(workerName);

        /// <inheritdoc/>
        public Task RegisterAgentAsync(AgentInfo agent) => this.inner.RegisterAgentAsync(agent);

        /// <inheritdoc/>
        public Task SendMessageAsync(string sender, string recipient, string content)
            => this.inner.SendMessageAsync(sender, recipient, content);

        /// <inheritdoc/>
        public Task SaveFileAsync(string path, long sizeBytes)
            => this.inner.SaveFileAsync(path, sizeBytes);

        /// <inheritdoc/>
        public Task LoadAsync(Guid swarmId) => this.inner.LoadAsync(swarmId);

        /// <inheritdoc/>
        public Task<SwarmInstanceState?> GetPersistedStateAsync(Guid swarmId) =>
            this.inner.GetPersistedStateAsync(swarmId);

        public Task<bool> IsRetryBudgetExhaustedAsync(int maxRetries) =>
            this.inner.IsRetryBudgetExhaustedAsync(maxRetries);
    }

    /// <summary>
    /// Spy <see cref="IInboxSystem"/> that records the sequence of
    /// <see cref="RegisterAgent(string)"/> calls, then delegates to a real
    /// <see cref="InboxSystem"/> so the rest of the swarm lifecycle behaves normally.
    /// </summary>
    private sealed class RecordingInboxSystem : IInboxSystem
    {
        private readonly InboxSystem inner = new();

        /// <summary>
        /// Gets the ordered list of agent names passed to <see cref="RegisterAgent(string)"/>.
        /// </summary>
        public List<string> Registrations { get; } = new();

        /// <inheritdoc/>
        public void RegisterAgent(string agentName)
        {
            this.Registrations.Add(agentName);
            this.inner.RegisterAgent(agentName);
        }

        /// <inheritdoc/>
        public Task SendAsync(string sender, string recipient, string content)
        {
            return this.inner.SendAsync(sender, recipient, content);
        }

        /// <inheritdoc/>
        public Task<IReadOnlyList<InboxMessage>> ReceiveAsync(string agentName)
        {
            return this.inner.ReceiveAsync(agentName);
        }

        /// <inheritdoc/>
        public Task<IReadOnlyList<InboxMessage>> PeekAsync(string agentName)
        {
            return this.inner.PeekAsync(agentName);
        }

        /// <inheritdoc/>
        public Task BroadcastAsync(string sender, string content, IEnumerable<string>? exclude = null)
        {
            return this.inner.BroadcastAsync(sender, content, exclude);
        }

        /// <inheritdoc/>
        public Task ClearAsync()
        {
            return this.inner.ClearAsync();
        }
    }
}
