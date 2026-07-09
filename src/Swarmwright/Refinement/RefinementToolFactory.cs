using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Swarmwright.Orchestration;
using Swarmwright.Tools;

namespace Swarmwright.Refinement;

/// <summary>
/// Creates refinement-chat-only tools that expose the persisted
/// <c>.chat/{agentId}.jsonl</c> transcripts and <c>.chat/{agentId}.system.md</c>
/// driver-prompt snapshots on demand. Paired with the safe default tool set
/// (<c>read</c>, <c>write</c>, <c>web_fetch</c>) in
/// the refinement chat handler.
/// </summary>
public static class RefinementToolFactory
{
    private const string ChatDirectoryName = ".chat";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    /// <summary>
    /// Creates the refinement tool set scoped to the given work directory.
    /// </summary>
    /// <param name="workDirectory">The per-swarm work directory the tools are confined to.</param>
    /// <param name="currentAgentId">The agent currently selected in the refinement chat; used when the model omits the <c>agentId</c> parameter.</param>
    /// <returns>The list of refinement <see cref="AITool"/> instances.</returns>
    public static IList<AITool> CreateRefinementTools(string workDirectory, string currentAgentId)
    {
        return
        [
            CreateReadConversationHistoryTool(workDirectory, currentAgentId),
            CreateReadDriverPromptTool(workDirectory, currentAgentId),
        ];
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Tool functions must return error JSON to the LLM, not throw.")]
    private static AIFunction CreateReadConversationHistoryTool(string workDirectory, string currentAgentId)
    {
        return AIFunctionFactory.Create(
            async (
                [Description("Agent name whose history to read. Leave empty to read your own.")] string? agentId = null,
                [Description("Maximum number of most-recent messages to return. 0 or negative means all.")] int limit = 0) =>
            {
                try
                {
                    var resolvedAgent = string.IsNullOrWhiteSpace(agentId) ? currentAgentId : agentId;
                    var relativePath = Path.Combine(ChatDirectoryName, $"{resolvedAgent}.jsonl");

                    if (!PathSecurity.TryResolveSafePath(workDirectory, relativePath, out var resolved))
                    {
                        return JsonSerializer.Serialize(
                            new { error = $"Invalid agentId '{resolvedAgent}'." },
                            JsonOptions);
                    }

                    var history = await ConversationHistorySerializer.DeserializeAsync(resolved).ConfigureAwait(false);
                    IEnumerable<ChatMessage> selected = history;
                    if (limit > 0 && history.Count > limit)
                    {
                        selected = history.Skip(history.Count - limit);
                    }

                    var messages = selected
                        .Select(m => new { role = m.Role.Value, text = m.Text })
                        .ToArray();

                    return JsonSerializer.Serialize(
                        new { agentId = resolvedAgent, messageCount = messages.Length, messages },
                        JsonOptions);
                }
                catch (Exception ex)
                {
                    return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
                }
            },
            "read_conversation_history",
            "Reads the execution transcript for your own or any sibling agent. Use this to recall what you did or inspect what another agent in this swarm produced.");
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Tool functions must return error JSON to the LLM, not throw.")]
    private static AIFunction CreateReadDriverPromptTool(string workDirectory, string currentAgentId)
    {
        return AIFunctionFactory.Create(
            async (
                [Description("Agent name whose driver prompt to read. Leave empty to read your own.")] string? agentId = null) =>
            {
                try
                {
                    var resolvedAgent = string.IsNullOrWhiteSpace(agentId) ? currentAgentId : agentId;
                    var relativePath = Path.Combine(ChatDirectoryName, $"{resolvedAgent}.system.md");

                    if (!PathSecurity.TryResolveSafePath(workDirectory, relativePath, out var resolved))
                    {
                        return JsonSerializer.Serialize(
                            new { error = $"Invalid agentId '{resolvedAgent}'." },
                            JsonOptions);
                    }

                    if (!File.Exists(resolved))
                    {
                        return JsonSerializer.Serialize(
                            new { error = $"No driver prompt snapshot for agent '{resolvedAgent}'." },
                            JsonOptions);
                    }

                    var content = await File.ReadAllTextAsync(resolved).ConfigureAwait(false);
                    return JsonSerializer.Serialize(
                        new { agentId = resolvedAgent, content },
                        JsonOptions);
                }
                catch (Exception ex)
                {
                    return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
                }
            },
            "read_driver_prompt",
            "Reads the original driver prompt that guided your own or any sibling agent's execution. Use this to recall the instructions you were given before the swarm run.");
    }
}
