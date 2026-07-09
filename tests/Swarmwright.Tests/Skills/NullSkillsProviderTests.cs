using Swarmwright.Skills;
using FluentAssertions;

namespace Swarmwright.Tests.Skills;

/// <summary>
/// Tests for <see cref="NullSkillsProvider"/>.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class NullSkillsProviderTests
{
    /// <summary>
    /// The null provider returns an empty prompt fragment.
    /// </summary>
    [TestMethod]
    public void GetPromptFragment_ReturnsEmptyString()
    {
        var result = NullSkillsProvider.Instance.GetPromptFragment();

        result.Should().BeEmpty();
    }

    /// <summary>
    /// The null provider returns an empty tool list.
    /// </summary>
    [TestMethod]
    public void GetTools_ReturnsEmptyList()
    {
        var result = NullSkillsProvider.Instance.GetTools();

        result.Should().BeEmpty();
    }
}
