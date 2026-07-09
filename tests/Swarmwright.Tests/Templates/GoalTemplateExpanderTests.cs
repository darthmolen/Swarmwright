using Swarmwright.Templates;
using FluentAssertions;

namespace Swarmwright.Tests.Templates;

[TestClass]
public class GoalTemplateExpanderTests
{
    [TestMethod]
    public void Expand_WithUserInputPlaceholder_ReplacesIt()
    {
        var result = GoalTemplateExpander.Expand(
            "Assemble a deep research team to: {user_input}",
            "Compare autonomous agent architectures");

        result.Should().Be("Assemble a deep research team to: Compare autonomous agent architectures");
    }

    [TestMethod]
    public void Expand_WithGoalPlaceholder_ReplacesIt()
    {
        var result = GoalTemplateExpander.Expand(
            "Research the following: {goal}",
            "Impact of AI on software development");

        result.Should().Be("Research the following: Impact of AI on software development");
    }

    [TestMethod]
    public void Expand_WithBothPlaceholders_ReplacesBoth()
    {
        var result = GoalTemplateExpander.Expand(
            "Goal: {goal} — Input: {user_input}",
            "Test goal");

        result.Should().Be("Goal: Test goal — Input: Test goal");
    }

    [TestMethod]
    public void Expand_NullTemplate_ReturnsGoal()
    {
        var result = GoalTemplateExpander.Expand(null, "Raw goal text");

        result.Should().Be("Raw goal text");
    }

    [TestMethod]
    public void Expand_EmptyTemplate_ReturnsGoal()
    {
        var result = GoalTemplateExpander.Expand(string.Empty, "Raw goal text");

        result.Should().Be("Raw goal text");
    }

    [TestMethod]
    public void Expand_NoPlaceholders_ReturnsTemplateAsIs()
    {
        var result = GoalTemplateExpander.Expand(
            "A static template with no placeholders",
            "Unused goal");

        result.Should().Be("A static template with no placeholders");
    }
}
