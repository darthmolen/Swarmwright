namespace Swarmwright.Configuration;

/// <summary>
/// Configuration options for swarm orchestration.
/// </summary>
public class SwarmOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Swarm";

    /// <summary>Gets or sets the maximum number of execution rounds per swarm.</summary>
    public int MaxRounds { get; set; } = 8;

    /// <summary>Gets or sets the suspend timeout in seconds before auto-suspending.</summary>
    public int SuspendTimeoutSeconds { get; set; } = 1800;

    /// <summary>Gets or sets the base path for swarm work directories.</summary>
    public string WorkBasePath { get; set; } = string.Empty;

    /// <summary>Gets or sets the directory containing swarm templates.</summary>
    public string TemplatesDirectory { get; set; } = string.Empty;

    /// <summary>Gets or sets the late completion monitoring window in seconds.</summary>
    public int LateCompletionWindowSeconds { get; set; } = 3600;

    /// <summary>Gets or sets the maximum number of swarms that can run concurrently.</summary>
    public int MaxConcurrentSwarms { get; set; } = 4;

    /// <summary>Gets or sets the maximum number of swarm requests queued before backpressure.</summary>
    public int MaxQueuedSwarms { get; set; } = 10;

    /// <summary>
    /// Gets or sets the maximum number of Continue-driven retries per failed task.
    /// A task's <c>retry_count</c> is bumped every time the user clicks Continue on
    /// a swarm in <c>AwaitingIntervention</c>; once a Failed task has
    /// <c>retry_count &gt;= MaxTaskRetries</c> it is no longer eligible for Continue.
    /// </summary>
    public int MaxTaskRetries { get; set; } = 1;

    /// <summary>
    /// Gets or sets the maximum number of times the orchestrator may auto-invoke
    /// Smart Continue inline when a task fails persistently. When the counter is
    /// exhausted the swarm escalates to <c>NeedsDiagnosis</c>. <c>0</c> disables
    /// auto-escalation (the default) — a human must click Smart Continue.
    /// </summary>
    public int AutoSmartContinueAttempts { get; set; }

    /// <summary>
    /// Gets or sets the stale-timeout (in minutes) after which a diagnose lock
    /// is considered expired and may be stolen without the confirm-prompt flow.
    /// </summary>
    public int DiagnoseLockTimeoutMinutes { get; set; } = 30;
}
