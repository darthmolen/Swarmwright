using Swarmwright.Configuration;
using FluentAssertions;

namespace Swarmwright.Tests.Configuration;

/// <summary>
/// Verifies sensible defaults on the state-machine recovery knobs added in
/// Phase B. An endpoint reading <c>MaxTaskRetries</c> at request time must
/// see <c>1</c> unless operators have opted in to something else.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SwarmOptionsDefaultsTests
{
    [TestMethod]
    public void MaxTaskRetries_DefaultsToOne()
    {
        new SwarmOptions().MaxTaskRetries.Should().Be(1);
    }

    [TestMethod]
    public void AutoSmartContinueAttempts_DefaultsToZero_DisablingAutoEscalation()
    {
        new SwarmOptions().AutoSmartContinueAttempts.Should().Be(0);
    }

    [TestMethod]
    public void DiagnoseLockTimeoutMinutes_DefaultsTo30()
    {
        new SwarmOptions().DiagnoseLockTimeoutMinutes.Should().Be(30);
    }
}
