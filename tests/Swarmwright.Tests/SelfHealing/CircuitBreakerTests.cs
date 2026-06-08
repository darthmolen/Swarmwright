using Swarmwright.SelfHealing;
using FluentAssertions;

namespace Swarmwright.Tests.SelfHealing;

/// <summary>
/// Unit tests for <see cref="CircuitBreaker"/>.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class CircuitBreakerTests
{
    /// <summary>
    /// Verifies that recording a failure increments the consecutive failure count.
    /// </summary>
    [TestMethod]
    public void RecordFailure_IncrementsCount()
    {
        // Arrange
        var breaker = new CircuitBreaker();

        // Act
        breaker.RecordFailure();

        // Assert
        breaker.ConsecutiveFailures.Should().Be(1);
    }

    /// <summary>
    /// Verifies that recording a success resets the consecutive failure count to zero.
    /// </summary>
    [TestMethod]
    public void RecordSuccess_ResetsCount()
    {
        // Arrange
        var breaker = new CircuitBreaker();
        breaker.RecordFailure();
        breaker.RecordFailure();

        // Act
        breaker.RecordSuccess();

        // Assert
        breaker.ConsecutiveFailures.Should().Be(0);
    }

    /// <summary>
    /// Verifies that the circuit breaker opens after reaching the failure threshold.
    /// </summary>
    [TestMethod]
    public void IsOpen_TrueAfterThresholdFailures()
    {
        // Arrange
        var breaker = new CircuitBreaker(threshold: 3);

        // Act
        breaker.RecordFailure();
        breaker.RecordFailure();
        breaker.RecordFailure();

        // Assert
        breaker.IsOpen.Should().BeTrue();
    }

    /// <summary>
    /// Verifies that the circuit breaker remains closed before reaching the threshold.
    /// </summary>
    [TestMethod]
    public void IsOpen_FalseBeforeThreshold()
    {
        // Arrange
        var breaker = new CircuitBreaker(threshold: 5);

        // Act
        breaker.RecordFailure();
        breaker.RecordFailure();

        // Assert
        breaker.IsOpen.Should().BeFalse();
    }

    /// <summary>
    /// Verifies that resetting the circuit breaker clears failure count and closes it.
    /// </summary>
    [TestMethod]
    public void Reset_ClearsState()
    {
        // Arrange
        var breaker = new CircuitBreaker(threshold: 2);
        breaker.RecordFailure();
        breaker.RecordFailure();
        breaker.IsOpen.Should().BeTrue();

        // Act
        breaker.Reset();

        // Assert
        breaker.ConsecutiveFailures.Should().Be(0);
        breaker.IsOpen.Should().BeFalse();
    }
}
