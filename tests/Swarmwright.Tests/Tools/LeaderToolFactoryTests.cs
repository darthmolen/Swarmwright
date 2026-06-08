using System.Text.Json;
using Swarmwright.Models;
using Swarmwright.Tools;
using FluentAssertions;
using Microsoft.Extensions.AI;

namespace Swarmwright.Tests.Tools;

/// <summary>
/// Unit tests for <see cref="LeaderToolFactory"/>.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class LeaderToolFactoryTests
{
    /// <summary>
    /// Regression: production log shows "Cannot get the value of a token type 'StartArray' as a string"
    /// when the LLM passes <c>tasks</c> as a JSON array rather than a stringified JSON array.
    /// The tool must accept either shape — most LLMs honor the JSON schema and emit a real array.
    /// </summary>
    [TestMethod]
    public async Task CreatePlanTool_WhenTasksIsJsonArray_CapturesPlan()
    {
        // Arrange
        var (tool, planSource) = LeaderToolFactory.CreatePlanTool();
        var planTool = (AIFunction)tool;

        // Build a JsonElement of kind Array — exactly what Microsoft.Extensions.AI hands the
        // marshaller when the LLM produces {"tasks": [...]} in its tool call arguments.
        const string arrayJson = """
            [
              {
                "subject": "Research",
                "description": "Research the topic",
                "workerRole": "researcher",
                "workerName": "researcher_1"
              }
            ]
            """;
        using var doc = JsonDocument.Parse(arrayJson);
        var tasksArray = doc.RootElement.Clone();

        var args = new AIFunctionArguments
        {
            ["team_description"] = "A research team",
            ["tasks"] = tasksArray,
        };

        // Act
        await planTool.InvokeAsync(args);

        // Assert
        planSource.Task.IsCompleted.Should().BeTrue();
        var plan = await planSource.Task;
        plan.Tasks.Should().HaveCount(1);
        plan.Tasks[0].Subject.Should().Be("Research");
        plan.Tasks[0].WorkerName.Should().Be("researcher_1");
    }

    /// <summary>
    /// Verifies that the create_plan tool captures a SwarmPlan via the TaskCompletionSource.
    /// </summary>
    [TestMethod]
    public async Task CreatePlanTool_CapturesSwarmPlan()
    {
        // Arrange
        var (tool, planSource) = LeaderToolFactory.CreatePlanTool();
        var planTool = (AIFunction)tool;

        var tasksJson = JsonSerializer.Serialize(new List<TaskPlan>
        {
            new()
            {
                Subject = "Research",
                Description = "Research the topic",
                WorkerRole = "researcher",
                WorkerName = "researcher_1",
            },
        });

        var args = new AIFunctionArguments
        {
            ["team_description"] = "A research team",
            ["tasks"] = tasksJson,
        };

        // Act
        await planTool.InvokeAsync(args);

        // Assert
        planSource.Task.IsCompleted.Should().BeTrue();
        var plan = await planSource.Task;
        plan.TeamDescription.Should().Be("A research team");
        plan.Tasks.Should().HaveCount(1);
        plan.Tasks[0].Subject.Should().Be("Research");
    }

    /// <summary>
    /// Verifies that the submit_report tool captures the report string.
    /// </summary>
    [TestMethod]
    public async Task CreateReportTool_CapturesReport()
    {
        // Arrange
        var (tool, reportSource) = LeaderToolFactory.CreateReportTool();
        var reportTool = (AIFunction)tool;

        var args = new AIFunctionArguments
        {
            ["report"] = "All tasks completed successfully.",
        };

        // Act
        await reportTool.InvokeAsync(args);

        // Assert
        reportSource.Task.IsCompleted.Should().BeTrue();
        var report = await reportSource.Task;
        report.Should().Be("All tasks completed successfully.");
    }

    /// <summary>
    /// Verifies that the begin_swarm tool captures the refined goal string.
    /// </summary>
    [TestMethod]
    public async Task CreateBeginSwarmTool_CapturesGoal()
    {
        // Arrange
        var (tool, goalSource) = LeaderToolFactory.CreateBeginSwarmTool();
        var goalTool = (AIFunction)tool;

        var args = new AIFunctionArguments
        {
            ["refined_goal"] = "Build a REST API for user management",
        };

        // Act
        await goalTool.InvokeAsync(args);

        // Assert
        goalSource.Task.IsCompleted.Should().BeTrue();
        var goal = await goalSource.Task;
        goal.Should().Be("Build a REST API for user management");
    }
}
