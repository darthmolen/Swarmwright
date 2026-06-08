using Swarmwright.Configuration;
using FluentAssertions;

namespace Swarmwright.Tests.Configuration;

/// <summary>
/// Regression guards that ensure dead / never-consumed configuration keys
/// stay removed from <see cref="SwarmOptions"/>. Documented config that silently
/// does nothing is actively misleading — this test class fails on re-add so a
/// future accidental reintroduction is caught immediately.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SwarmOptionsTests
{
    /// <summary>
    /// <c>CircuitBreakerThreshold</c> used to exist but no code path ever read it;
    /// the CircuitBreaker class itself is gone (SwarmAgent.cs comment: "CircuitBreaker
    /// removed. FunctionInvokingChatClient handles tool invocation internally.").
    /// Removed on 2026-04-17 (T6.1).
    /// </summary>
    [TestMethod]
    public void SwarmOptions_DoesNotExposeCircuitBreakerThreshold()
    {
        typeof(SwarmOptions).GetProperty("CircuitBreakerThreshold").Should().BeNull(
            "CircuitBreakerThreshold was dead config — document/sample drift that misled integrators. Re-adding requires wiring it to actual behavior first.");
    }

    /// <summary>
    /// <c>TimeoutSeconds</c> was documented as a per-task execution timeout but
    /// was never consumed — only <c>SuspendTimeoutSeconds</c> is read by the
    /// orchestrator. Removed on 2026-04-17 (T6.1).
    /// </summary>
    [TestMethod]
    public void SwarmOptions_DoesNotExposeTimeoutSeconds()
    {
        typeof(SwarmOptions).GetProperty("TimeoutSeconds").Should().BeNull(
            "TimeoutSeconds was dead config — never read by the orchestrator (only SuspendTimeoutSeconds is). Re-adding requires wiring it to actual behavior first.");
    }
}
