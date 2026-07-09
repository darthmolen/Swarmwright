using Swarmwright.Tools;
using FluentAssertions;

namespace Swarmwright.Tests.Tools;

/// <summary>
/// Tests for <see cref="SwarmToolAttribute"/>.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SwarmToolAttributeTests
{
    /// <summary>
    /// The constructor sets the Name and Description properties.
    /// </summary>
    [TestMethod]
    public void Constructor_SetsNameAndDescription()
    {
        var attribute = new SwarmToolAttribute("query_db", "Run a SELECT query.");

        attribute.Name.Should().Be("query_db");
        attribute.Description.Should().Be("Run a SELECT query.");
    }
}
