namespace Swarmwright.Templates;

/// <summary>
/// Expands goal templates by replacing known placeholders with the user's goal.
/// </summary>
public static class GoalTemplateExpander
{
    /// <summary>
    /// Expands a goal template by replacing {user_input} and {goal} placeholders.
    /// </summary>
    /// <param name="template">The goal template string. May be null or empty.</param>
    /// <param name="goal">The user's goal text.</param>
    /// <returns>The expanded goal, or the raw goal if no template is provided.</returns>
    public static string Expand(string? template, string goal)
    {
        if (string.IsNullOrEmpty(template))
        {
            return goal;
        }

        return template
            .Replace("{user_input}", goal, StringComparison.Ordinal)
            .Replace("{goal}", goal, StringComparison.Ordinal);
    }
}
