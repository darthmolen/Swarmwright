namespace Swarmwright.Templates;

/// <summary>
/// Builds system prompts for swarm phases by composing template content with tool instructions.
/// Follows the Python swarm pattern: domain context (from template) + tool mandates (from framework).
/// </summary>
public static class PromptBuilder
{
    private const string DefaultLeaderPrompt =
        "You are a swarm leader. Analyze the goal and create an execution plan.";

    private const string PlanToolInstruction = """

        ## IMPORTANT — Tool Mandate
        You MUST call the create_plan tool to submit your plan. Do NOT respond with text.
        Use the create_plan tool with these parameters:
        - team_description: a brief description of the team and their roles
        - tasks: a JSON array of task objects, each containing:
          - "subject": short task title
          - "description": detailed instructions for the worker
          - "workerRole": specialist role needed (e.g., "Primary Researcher")
          - "workerName": snake_case identifier (e.g., "primary-researcher")
          - "blockedByIndices": array of 0-based indices of tasks that must complete first, or empty array []
        """;

    private const string ReportToolInstruction = """

        ## IMPORTANT — Tool Mandate
        You MUST call the submit_report tool to submit your report. Do NOT respond with text.
        Use the submit_report tool with:
        - report: the full consolidated report in markdown format
        """;

    private const string BeginSwarmToolInstruction = """

        ## IMPORTANT — Tool Mandate
        When you have gathered enough information, call the begin_swarm tool.
        Use the begin_swarm tool with:
        - refined_goal: the refined and clarified goal incorporating all information gathered from the user
        """;

    private const string WorkerTaskUpdateMandate = """

        ## CRITICAL — Task Completion Signal (MANDATORY)

        Every task you work on MUST end with a successful call to the `task_update` tool.
        This is NOT optional. The task board is the ONLY way the orchestrator knows whether
        your work succeeded; if you do not call `task_update`, the task is automatically
        marked Failed regardless of how good your final text is, your work is excluded
        from the synthesis phase, and the swarm's final report will not reflect your
        findings.

        **Required pattern for EVERY task:**

        1. First, call `task_list` to get your assigned task and copy the exact `id` field.
        2. Do the work (tool calls, research, analysis, file writes).
        3. Immediately before your final message, call `task_update` with:
           - `task_id`: the EXACT id from `task_list` — do NOT make one up, do NOT invent
             ids like `"PrimaryResearch-001"` or `"task-1"`. Copy the real value.
           - `status`: `"Completed"` if the work finished, `"Failed"` with a reason if not.
             Use the canonical PascalCase values — not `in_progress`, not `completed`,
             not any other variant.
           - `result`: a self-contained summary of what you produced. This is the ONLY
             input the synthesis phase receives from your work. Include:
             • Key findings and conclusions
             • Source URLs for any web-fetched data (markdown links)
             • MCP tool/query references for any data-sourced claims (e.g., `[MCP: provider/tool] query`)
             • Flag unsourced claims with `[model-knowledge]`

        **Example (use your real task id from `task_list`):**

            task_update(
              task_id="<the id field from task_list>",
              status="Completed",
              result="Key findings: ... [Source](https://url) ... [MCP: sql-mcp/query] SELECT ...")

        **Non-negotiables:**

        - Do NOT emit your final assistant message without first making a successful
          `task_update` call.
        - Do NOT fabricate task ids — always copy the exact `id` field returned by
          `task_list`.
        - Do NOT use `Pending`, `InProgress`, or any variant as your FINAL status. The
          terminal statuses are `Completed` or `Failed`.
        - If a task cannot be completed, `task_update(status="Failed", result="<reason>")`
          is the correct terminal signal — not silence.

        If you fail to follow this protocol, the orchestrator will mark your task Failed
        and the swarm will run to completion without your contribution.
        """;

    private const string WorkerLeaderInboxDirective = """

        ## Contacting the Team Leader
        When you need to contact the team leader, use `inbox_send` with `to="leader"`.
        `"leader"` is the only valid recipient name for the leader — do not invent
        variants such as `Leader`, `team_leader`, or role-based names.
        """;

    /// <summary>
    /// Builds the system prompt for the planning phase.
    /// </summary>
    /// <param name="template">The loaded template, or null for default prompt.</param>
    /// <returns>The composed system prompt including tool instructions.</returns>
    public static string ForPlanning(LoadedTemplate? template)
    {
        return (template?.LeaderPrompt ?? DefaultLeaderPrompt) + PlanToolInstruction;
    }

    /// <summary>
    /// Builds the system prompt for the synthesis phase.
    /// </summary>
    /// <param name="template">The loaded template, or null for default prompt.</param>
    /// <returns>The composed system prompt including tool instructions.</returns>
    public static string ForSynthesis(LoadedTemplate? template)
    {
        return (template?.SynthesisPrompt ?? "Synthesize the results into a comprehensive report.")
            + ReportToolInstruction;
    }

    /// <summary>
    /// Builds the system prompt for the QA interview phase.
    /// </summary>
    /// <param name="template">The loaded template, or null for default prompt.</param>
    /// <returns>The composed system prompt including tool instructions.</returns>
    public static string ForQa(LoadedTemplate? template)
    {
        return (template?.LeaderPrompt ?? DefaultLeaderPrompt) + BeginSwarmToolInstruction;
    }

    /// <summary>
    /// Builds the system prompt for a worker agent by layering the system preamble,
    /// the work directory directive, and the template prompt with variable expansion.
    /// Follows the Python pattern: system preamble (tool mandates) + work directory
    /// (file tool scope) + template prompt (domain expertise).
    /// </summary>
    /// <param name="systemPreamble">The system coordination protocol (from system-prompt.md), or null.</param>
    /// <param name="workDirectory">The per-swarm work directory for file tools, or null/empty to skip the directive.</param>
    /// <param name="displayName">The worker's display name.</param>
    /// <param name="role">The worker's role description.</param>
    /// <param name="templatePrompt">The template's worker prompt with {display_name} and {role} placeholders, or null.</param>
    /// <param name="skillsPromptFragment">The skills description fragment to inject, or null/empty to omit.</param>
    /// <returns>The composed worker system prompt.</returns>
    public static string ForWorker(
        string? systemPreamble,
        string? workDirectory,
        string displayName,
        string role,
        string? templatePrompt,
        string? skillsPromptFragment = null)
    {
        var parts = new List<string>
        {
            ForWorkerCore(systemPreamble, workDirectory, displayName, role, templatePrompt),
        };

        if (!string.IsNullOrEmpty(skillsPromptFragment))
        {
            parts.Add(skillsPromptFragment);
        }

        parts.Add(WorkerTaskUpdateMandate);
        parts.Add(WorkerLeaderInboxDirective);

        return string.Join("\n\n", parts);
    }

    /// <summary>
    /// Builds the worker system prompt without swarm-coordination mandates
    /// (<c>task_update</c>, leader inbox). This is the "driver" prompt that
    /// shaped the agent's execution-time behavior minus tool-specific
    /// instructions that no longer apply outside of a live swarm run.
    /// Used by refinement chat to provide historical context about what
    /// the agent was originally asked to do.
    /// </summary>
    /// <param name="systemPreamble">The system coordination protocol, or null.</param>
    /// <param name="workDirectory">The per-swarm work directory, or null/empty to skip.</param>
    /// <param name="displayName">The worker's display name.</param>
    /// <param name="role">The worker's role description.</param>
    /// <param name="templatePrompt">The template's worker prompt with placeholders, or null.</param>
    /// <returns>The worker prompt without coordination mandates.</returns>
    public static string ForWorkerCore(
        string? systemPreamble,
        string? workDirectory,
        string displayName,
        string role,
        string? templatePrompt)
    {
        var parts = new List<string>();

        if (!string.IsNullOrEmpty(systemPreamble))
        {
            parts.Add(systemPreamble);
        }

        if (!string.IsNullOrEmpty(workDirectory))
        {
            parts.Add(
                $"## Work Directory\n\n" +
                $"Your work directory is: `{workDirectory}`\n\n" +
                "Use the `read` and `write` tools to access files in this directory. " +
                "All file paths must be relative to the work directory. " +
                "Do not use absolute paths and do not attempt to escape the directory with `..`.");
        }

        if (!string.IsNullOrEmpty(templatePrompt))
        {
            var expanded = templatePrompt
                .Replace("{display_name}", displayName, StringComparison.Ordinal)
                .Replace("{role}", role, StringComparison.Ordinal);
            parts.Add(expanded);
        }
        else
        {
            parts.Add($"You are {displayName}, a specialist in {role}.");
        }

        return string.Join("\n\n", parts);
    }

    /// <summary>
    /// Builds the system prompt for a refinement chat session. Frames the
    /// conversation for post-execution review without coordination tool mandates.
    /// </summary>
    /// <param name="displayName">The agent's display name.</param>
    /// <param name="role">The agent's specialist role.</param>
    /// <param name="originalSystemPrompt">The original system prompt from execution, or null.</param>
    /// <param name="currentAgentName">The agent's internal name used by refinement tools. When supplied alongside <paramref name="siblingAgentNames"/> the prompt tells the model which identifiers to pass to the refinement tools.</param>
    /// <param name="siblingAgentNames">Internal names of all agents in the swarm (including the current agent). When supplied the prompt lists them so the model uses the correct ids.</param>
    /// <returns>The refinement system prompt.</returns>
    public static string ForRefinement(
        string displayName,
        string role,
        string? originalSystemPrompt,
        string? currentAgentName = null,
        IReadOnlyList<string>? siblingAgentNames = null)
    {
        var selfLabel = string.IsNullOrEmpty(currentAgentName)
            ? displayName
            : $"{displayName} (internal name: `{currentAgentName}`)";

        var prompt = $"You are {selfLabel}, a {role} specialist. " +
            "You previously completed work as part of a multi-agent swarm. " +
            "The user wants to discuss your work -- reviewing findings, refining outputs, or diagnosing issues. " +
            "Your full conversation history from the execution is included as context. " +
            "You have access to file tools to reference artifacts in your work directory.";

        if (siblingAgentNames is { Count: > 0 })
        {
            var formatted = string.Join(", ", siblingAgentNames.Select(n => "`" + n + "`"));
            prompt += "\n\nAgents in this swarm (use these exact internal names with the refinement tools): " + formatted + ".";
        }

        prompt += "\n\nAdditional tools for this refinement session:\n" +
            "- `read_conversation_history(agentId?, limit?)` — inspect your own or any sibling agent's execution transcript.\n" +
            "- `read_driver_prompt(agentId?)` — inspect the original instructions that guided your own or any sibling agent's work.\n" +
            "Leave `agentId` empty to refer to yourself; pass another agent's internal name from the list above to inspect their work. Never pass a display name or invented id.";

        if (!string.IsNullOrEmpty(originalSystemPrompt))
        {
            prompt = originalSystemPrompt + "\n\n" + prompt;
        }

        return prompt;
    }
}
