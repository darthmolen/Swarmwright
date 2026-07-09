namespace Swarmwright.SelfHealing;

/// <summary>
/// Tracks consecutive tool failures and opens the circuit when a threshold is reached.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1724:Type names should not match namespaces", Justification = "CircuitBreaker is the canonical name for this pattern; no conflict in this project.")]
public class CircuitBreaker
{
    private readonly int threshold;

    /// <summary>
    /// Initializes a new instance of the <see cref="CircuitBreaker"/> class.
    /// </summary>
    /// <param name="threshold">The number of consecutive failures before the circuit opens.</param>
    public CircuitBreaker(int threshold = 5)
    {
        this.threshold = threshold;
    }

    /// <summary>
    /// Gets the current number of consecutive failures.
    /// </summary>
    public int ConsecutiveFailures { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the circuit breaker is open.
    /// </summary>
    public bool IsOpen => this.ConsecutiveFailures >= this.threshold;

    /// <summary>
    /// Records a failure, incrementing the consecutive failure count.
    /// </summary>
    public void RecordFailure()
    {
        this.ConsecutiveFailures++;
    }

    /// <summary>
    /// Records a success, resetting the consecutive failure count to zero.
    /// </summary>
    public void RecordSuccess()
    {
        this.ConsecutiveFailures = 0;
    }

    /// <summary>
    /// Resets the circuit breaker to its initial closed state.
    /// </summary>
    public void Reset()
    {
        this.ConsecutiveFailures = 0;
    }
}
