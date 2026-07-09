using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Swarmwright.Orchestration;

/// <summary>
/// Serializes and deserializes agent conversation histories to JSONL format.
/// Captures text, tool calls (<see cref="FunctionCallContent"/>), and tool results
/// (<see cref="FunctionResultContent"/>) from <see cref="ChatMessage.Contents"/>.
/// Per-line schema:
/// <code>
/// {
///   "role": "user|assistant|system|tool",
///   "text": "...",
///   "toolCalls": [{"callId":"...", "name":"...", "args":{...}}]?,
///   "toolCallId": "..."?,
///   "result": "..."?
/// }
/// </code>
/// The deserializer returns text-only <see cref="ChatMessage"/> instances — tool
/// call detail is preserved in the file for frontend viewers but not reconstructed
/// into the chat history used by the refinement LLM.
/// </summary>
internal static class ConversationHistorySerializer
{
    /// <summary>
    /// The maximum number of messages persisted per agent. Histories exceeding
    /// this limit are truncated to the most recent messages.
    /// </summary>
    public const int MaxMessagesPerAgent = 200;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Serializes a conversation history to a JSONL file atomically. Writes
    /// to a unique per-call <c>.tmp</c> sibling first, then
    /// <see cref="File.Move(string, string, bool)"/> with overwrite so
    /// readers never observe a partially-written final file, and concurrent
    /// writers to the same final path never collide on the staging file.
    /// Last-writer-wins semantics at the final path.
    /// </summary>
    /// <param name="filePath">The absolute path to the output JSONL file.</param>
    /// <param name="history">The conversation history to serialize.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public static async Task SerializeAsync(string filePath, IReadOnlyList<ChatMessage> history)
    {
        var messages = history.Count > MaxMessagesPerAgent
            ? history.Skip(history.Count - MaxMessagesPerAgent).ToList()
            : history;

        var tmpPath = $"{filePath}.{Guid.NewGuid():N}.tmp";

        await using (var stream = new FileStream(
            tmpPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            useAsync: true))
        {
            await using var writer = new StreamWriter(stream);

            foreach (var message in messages)
            {
                var record = new Dictionary<string, object?>
                {
                    ["role"] = message.Role.Value,
                    ["text"] = message.Text ?? string.Empty,
                };

                var toolCalls = new List<object>();
                string? toolCallId = null;
                string? toolResult = null;

                foreach (var content in message.Contents)
                {
                    switch (content)
                    {
                        case FunctionCallContent fc:
                            toolCalls.Add(new
                            {
                                callId = fc.CallId,
                                name = fc.Name,
                                args = fc.Arguments,
                            });
                            break;
                        case FunctionResultContent fr:
                            toolCallId = fr.CallId;
                            toolResult = fr.Result switch
                            {
                                null => null,
                                string s => s,
                                JsonElement je => je.GetRawText(),
                                _ => JsonSerializer.Serialize(fr.Result, JsonOptions),
                            };
                            break;
                        default:
                            // TextContent is already captured via message.Text; other content
                            // types (DataContent, UriContent, etc.) are intentionally skipped.
                            break;
                    }
                }

                if (toolCalls.Count > 0)
                {
                    record["toolCalls"] = toolCalls;
                }

                if (toolCallId != null)
                {
                    record["toolCallId"] = toolCallId;
                }

                if (toolResult != null)
                {
                    record["result"] = toolResult;
                }

                var line = JsonSerializer.Serialize(record, JsonOptions);
                await writer.WriteLineAsync(line).ConfigureAwait(false);
            }
        }

        File.Move(tmpPath, filePath, overwrite: true);
    }

    /// <summary>
    /// Deserializes a JSONL file back to a list of <see cref="ChatMessage"/>.
    /// Returns an empty list if the file does not exist.
    /// </summary>
    /// <param name="filePath">The absolute path to the JSONL file.</param>
    /// <returns>The deserialized conversation history.</returns>
    public static async Task<List<ChatMessage>> DeserializeAsync(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return [];
        }

        var messages = new List<ChatMessage>();
        using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);

        using var reader = new StreamReader(stream);

        string? line;
        while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var doc = JsonDocument.Parse(line);
            var role = doc.RootElement.GetProperty("role").GetString() ?? "user";
            var text = doc.RootElement.GetProperty("text").GetString() ?? string.Empty;
            messages.Add(new ChatMessage(new ChatRole(role), text));
        }

        return messages;
    }
}
