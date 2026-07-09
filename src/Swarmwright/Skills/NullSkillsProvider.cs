using Microsoft.Extensions.AI;

namespace Swarmwright.Skills;

/// <summary>
/// No-op skills provider used when a worker has no skills declared.
/// </summary>
public sealed class NullSkillsProvider : ISkillsProvider
{
    /// <summary>
    /// Gets the singleton instance.
    /// </summary>
    public static NullSkillsProvider Instance { get; } = new();

    /// <inheritdoc/>
    public IReadOnlyList<SkillDefinition> Skills => [];

    /// <inheritdoc/>
    public string GetPromptFragment() => string.Empty;

    /// <inheritdoc/>
    public IReadOnlyList<AITool> GetTools() => [];
}
