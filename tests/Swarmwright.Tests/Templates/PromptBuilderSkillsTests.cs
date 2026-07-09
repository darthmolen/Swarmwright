using Swarmwright.Templates;
using FluentAssertions;

namespace Swarmwright.Tests.Templates;

/// <summary>
/// Tests for <see cref="PromptBuilder"/> skills prompt fragment injection.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class PromptBuilderSkillsTests
{
    /// <summary>
    /// When a non-empty skills fragment is provided, the output contains the fragment.
    /// </summary>
    [TestMethod]
    public void ForWorker_InjectsSkillsSection_WhenFragmentProvided()
    {
        var fragment = "## Available Skills\n\n- **azure-architect**: Cloud architecture.";

        var result = PromptBuilder.ForWorker(
            "System preamble.",
            "/work",
            "Researcher",
            "research",
            "You are {display_name}, a {role} specialist.",
            fragment);

        result.Should().Contain("## Available Skills");
        result.Should().Contain("azure-architect");
    }

    /// <summary>
    /// When no skills fragment is provided, the output is unchanged from pre-skills behavior.
    /// </summary>
    [TestMethod]
    public void ForWorker_OmitsSkillsSection_WhenFragmentNullOrEmpty()
    {
        var withNull = PromptBuilder.ForWorker(
            "System preamble.",
            "/work",
            "Researcher",
            "research",
            "You are {display_name}.",
            null);

        var withEmpty = PromptBuilder.ForWorker(
            "System preamble.",
            "/work",
            "Researcher",
            "research",
            "You are {display_name}.",
            string.Empty);

        var withoutParam = PromptBuilder.ForWorker(
            "System preamble.",
            "/work",
            "Researcher",
            "research",
            "You are {display_name}.");

        withNull.Should().NotContain("Available Skills");
        withEmpty.Should().NotContain("Available Skills");
        withoutParam.Should().NotContain("Available Skills");
    }

    /// <summary>
    /// Skills fragment is placed after the template prompt and before the task-completion mandate.
    /// </summary>
    [TestMethod]
    public void ForWorker_PlacesSkillsBetweenTemplateAndMandates()
    {
        var fragment = "## Available Skills\n\n- **test-skill**: Test.";

        var result = PromptBuilder.ForWorker(
            "System preamble.",
            "/work",
            "Researcher",
            "research",
            "You are {display_name}.",
            fragment);

        var skillsIndex = result.IndexOf("## Available Skills", StringComparison.Ordinal);
        var mandateIndex = result.IndexOf("## CRITICAL", StringComparison.Ordinal);
        var templateIndex = result.IndexOf("You are Researcher", StringComparison.Ordinal);

        skillsIndex.Should().BeGreaterThan(templateIndex, "skills should come after template prompt");
        skillsIndex.Should().BeLessThan(mandateIndex, "skills should come before task-completion mandate");
    }

    /// <summary>
    /// Full prompt chain ordering: preamble &lt; work-dir &lt; template &lt; skills &lt; mandates.
    /// Pins the documented contract of <see cref="PromptBuilder.ForWorker"/>.
    /// </summary>
    [TestMethod]
    public void ForWorker_ChainOrdering_PreambleWorkdirTemplateSkillsMandates()
    {
        var fragment = "## Available Skills\n\n- **test-skill**: Test.";

        var result = PromptBuilder.ForWorker(
            "SENTINEL_PREAMBLE",
            "/work/dir",
            "Researcher",
            "research",
            "SENTINEL_TEMPLATE You are {display_name}.",
            fragment);

        var preambleIndex = result.IndexOf("SENTINEL_PREAMBLE", StringComparison.Ordinal);
        var workDirIndex = result.IndexOf("## Work Directory", StringComparison.Ordinal);
        var templateIndex = result.IndexOf("SENTINEL_TEMPLATE", StringComparison.Ordinal);
        var skillsIndex = result.IndexOf("## Available Skills", StringComparison.Ordinal);
        var mandateIndex = result.IndexOf("## CRITICAL", StringComparison.Ordinal);

        preambleIndex.Should().BeGreaterThanOrEqualTo(0);
        workDirIndex.Should().BeGreaterThan(preambleIndex, "work-dir follows preamble");
        templateIndex.Should().BeGreaterThan(workDirIndex, "template follows work-dir");
        skillsIndex.Should().BeGreaterThan(templateIndex, "skills follow template");
        mandateIndex.Should().BeGreaterThan(skillsIndex, "mandates follow skills");
    }
}
