namespace Swarmwright.Models;

/// <summary>
/// Pointers to a producing agent's reasoning artifacts, relative to the swarm
/// work directory, plus the system-prompt hash for prompt-version / drift
/// detection.
/// </summary>
/// <param name="Transcript">The relative path to the agent's JSONL transcript.</param>
/// <param name="SystemPrompt">The relative path to the agent's system-prompt snapshot.</param>
/// <param name="SystemPromptHash">The SHA-256 of the system-prompt snapshot, or <c>null</c> when absent.</param>
public sealed record TaskArtifacts(
    string Transcript,
    string SystemPrompt,
    string? SystemPromptHash);
