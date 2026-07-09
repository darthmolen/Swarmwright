namespace Swarmwright.Models;

/// <summary>
/// A single attributed task output: the producing lens/instance, the task
/// input (<see cref="Subject"/>), the verbatim output (<see cref="Result"/>),
/// and pointers to the producing agent's reasoning artifacts.
/// </summary>
/// <param name="TaskId">The stable task join key.</param>
/// <param name="WorkerName">The producing worker instance name.</param>
/// <param name="WorkerRole">The producing worker role — the lens; training buckets by this.</param>
/// <param name="Subject">The task subject (the input half).</param>
/// <param name="Result">The verbatim task result — never summarized or truncated.</param>
/// <param name="CompletedUtc">The task's last-update timestamp, used as completion time.</param>
/// <param name="Artifacts">Pointers to the producing agent's transcript and system prompt.</param>
public sealed record TaskOutputEntry(
    string TaskId,
    string WorkerName,
    string WorkerRole,
    string Subject,
    string Result,
    DateTime CompletedUtc,
    TaskArtifacts Artifacts);
