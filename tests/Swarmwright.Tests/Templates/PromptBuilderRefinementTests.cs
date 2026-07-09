using Swarmwright.Templates;
using FluentAssertions;

namespace Swarmwright.Tests.Templates;

/// <summary>
/// Unit tests for <see cref="PromptBuilder.ForRefinement"/>.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class PromptBuilderRefinementTests
{
    /// <summary>
    /// Verifies that ForRefinement returns a prompt containing the agent name and role.
    /// </summary>
    [TestMethod]
    public void ForRefinement_ContainsAgentNameAndRole()
    {
        // Act
        var result = PromptBuilder.ForRefinement("Alice", "researcher", null);

        // Assert
        result.Should().Contain("Alice");
        result.Should().Contain("researcher");
    }

    /// <summary>
    /// Verifies that ForRefinement includes refinement framing language.
    /// </summary>
    [TestMethod]
    public void ForRefinement_ContainsRefinementFraming()
    {
        // Act
        var result = PromptBuilder.ForRefinement("Alice", "researcher", null);

        // Assert
        result.Should().Contain("previously completed work");
        result.Should().Contain("multi-agent swarm");
        result.Should().Contain("discuss your work");
    }

    /// <summary>
    /// Verifies that ForRefinement does NOT contain task_update or inbox mandates.
    /// </summary>
    [TestMethod]
    public void ForRefinement_DoesNotContainCoordinationMandates()
    {
        // Act
        var result = PromptBuilder.ForRefinement("Alice", "researcher", null);

        // Assert
        result.Should().NotContain("task_update");
        result.Should().NotContain("inbox_send");
        result.Should().NotContain("inbox_receive");
        result.Should().NotContain("task_list");
    }

    /// <summary>
    /// Verifies that ForRefinement includes the original system prompt when provided.
    /// </summary>
    [TestMethod]
    public void ForRefinement_WithOriginalPrompt_IncludesBoth()
    {
        // Arrange
        var original = "You are a specialist in market analysis.";

        // Act
        var result = PromptBuilder.ForRefinement("Alice", "researcher", original);

        // Assert
        result.Should().Contain(original);
        result.Should().Contain("previously completed work");
    }
}
