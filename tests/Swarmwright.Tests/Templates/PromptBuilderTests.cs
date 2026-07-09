using Swarmwright.Templates;
using FluentAssertions;

namespace Swarmwright.Tests.Templates;

[TestClass]
public class PromptBuilderTests
{
    [TestMethod]
    public void ForPlanning_WithTemplate_IncludesTemplatePromptAndToolInstruction()
    {
        var template = new LoadedTemplate { LeaderPrompt = "You are a research team leader." };

        var result = PromptBuilder.ForPlanning(template);

        result.Should().StartWith("You are a research team leader.");
        result.Should().Contain("create_plan");
        result.Should().Contain("team_description");
        result.Should().Contain("tasks");
        result.Should().Contain("blockedByIndices");
    }

    [TestMethod]
    public void ForPlanning_NullTemplate_UsesDefaultPromptWithToolInstruction()
    {
        var result = PromptBuilder.ForPlanning(null);

        result.Should().Contain("swarm leader");
        result.Should().Contain("create_plan");
    }

    [TestMethod]
    public void ForPlanning_AlwaysIncludesMustCallInstruction()
    {
        var template = new LoadedTemplate { LeaderPrompt = "Custom prompt." };

        var result = PromptBuilder.ForPlanning(template);

        result.Should().Contain("MUST call the create_plan tool");
    }

    [TestMethod]
    public void ForSynthesis_WithTemplate_IncludesTemplateAndToolInstruction()
    {
        var template = new LoadedTemplate { SynthesisPrompt = "Synthesize findings." };

        var result = PromptBuilder.ForSynthesis(template);

        result.Should().StartWith("Synthesize findings.");
        result.Should().Contain("submit_report");
    }

    [TestMethod]
    public void ForSynthesis_NullTemplate_UsesDefaultWithToolInstruction()
    {
        var result = PromptBuilder.ForSynthesis(null);

        result.Should().Contain("submit_report");
    }

    [TestMethod]
    public void ForQa_WithTemplate_IncludesBeginSwarmInstruction()
    {
        var template = new LoadedTemplate { LeaderPrompt = "Interview the user." };

        var result = PromptBuilder.ForQa(template);

        result.Should().StartWith("Interview the user.");
        result.Should().Contain("begin_swarm");
    }

    [TestMethod]
    public void ForWorker_WithSystemPreambleAndTemplate_LayersBoth()
    {
        var preamble = "## System Protocol\nUse coordination tools.";
        var templatePrompt = "You are {display_name}, expert in {role}.";

        var result = PromptBuilder.ForWorker(preamble, workDirectory: null, "Alice", "Data Analysis", templatePrompt);

        result.Should().Contain("System Protocol");
        result.Should().Contain("You are Alice, expert in Data Analysis.");
    }

    [TestMethod]
    public void ForWorker_NullTemplate_UsesFallback()
    {
        var result = PromptBuilder.ForWorker(null, workDirectory: null, "Bob", "Research", null);

        result.Should().Contain("Bob");
        result.Should().Contain("Research");
    }

    [TestMethod]
    public void ForWorker_WithWorkDirectory_EmitsDirective()
    {
        var workDir = @"C:\temp\swarm-abc";

        var result = PromptBuilder.ForWorker(
            systemPreamble: null,
            workDirectory: workDir,
            displayName: "Alice",
            role: "Analyst",
            templatePrompt: "You are {display_name}.");

        result.Should().Contain("Work Directory");
        result.Should().Contain(workDir);
        result.Should().Contain("read");
        result.Should().Contain("write");
        result.Should().Contain("You are Alice.");
    }

    [TestMethod]
    public void ForWorker_NullWorkDirectory_SkipsDirective()
    {
        var result = PromptBuilder.ForWorker(
            systemPreamble: null,
            workDirectory: null,
            displayName: "Bob",
            role: "Writer",
            templatePrompt: null);

        result.Should().NotContain("Work Directory");
    }

    /// <summary>
    /// Verifies that the worker system prompt carries a strengthened task_update
    /// mandate — mandatory language, explicit anti-fabrication guidance for task ids,
    /// and a concrete example. This mandate lives in <see cref="PromptBuilder"/>
    /// (system-side), not in the template markdown, so every worker across every
    /// template gets the same strong signal regardless of how the template author
    /// writes their domain prompt.
    /// </summary>
    [TestMethod]
    public void ForWorker_AlwaysIncludesStrengthenedTaskUpdateMandate()
    {
        var result = PromptBuilder.ForWorker(
            systemPreamble: null,
            workDirectory: null,
            displayName: "Alice",
            role: "Analyst",
            templatePrompt: "You are {display_name}, expert in {role}.");

        // Core mandate language — must appear verbatim so the LLM sees strong verbs.
        result.Should().Contain("MANDATORY");
        result.Should().Contain("task_update");

        // Anti-fabrication: the LLM must copy the real task id from task_list,
        // not invent one like "PrimaryResearch-001" (observed in real runs).
        result.Should().Contain("task_list");
        result.Should().ContainAny("do NOT make one up", "do NOT fabricate", "do not invent");

        // Must tell the worker that a missing task_update call fails the task.
        result.Should().Contain("marked Failed");

        // Must explicitly show the canonical PascalCase status values the tool
        // accepts, so the LLM doesn't default to snake_case variants.
        result.Should().Contain("Completed");
        result.Should().Contain("Failed");
    }

    [TestMethod]
    public void ForWorker_AllThreeLayers_OrderedCorrectly()
    {
        var preamble = "## Coordination";
        var workDir = "/tmp/swarm-1";
        var templatePrompt = "# {display_name} Agent";

        var result = PromptBuilder.ForWorker(preamble, workDir, "Carol", "Researcher", templatePrompt);

        // Preamble should come before work directory, which should come before template prompt.
        var preambleIndex = result.IndexOf("Coordination", StringComparison.Ordinal);
        var workDirIndex = result.IndexOf("Work Directory", StringComparison.Ordinal);
        var templateIndex = result.IndexOf("Carol Agent", StringComparison.Ordinal);

        preambleIndex.Should().BeGreaterThanOrEqualTo(0);
        workDirIndex.Should().BeGreaterThan(preambleIndex);
        templateIndex.Should().BeGreaterThan(workDirIndex);
    }
}
