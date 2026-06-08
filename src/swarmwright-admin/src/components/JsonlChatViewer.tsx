import { marked } from 'marked';
import DOMPurify from 'dompurify';

interface ToolCall {
  callId: string;
  name: string;
  args: Record<string, unknown>;
}

interface JsonlMessage {
  role: string;
  text: string;
  toolCalls?: ToolCall[];
  toolCallId?: string;
  result?: string;
}

function renderMarkdown(md: string): string {
  return DOMPurify.sanitize(marked.parse(md) as string);
}

function parseLines(content: string): JsonlMessage[] {
  const messages: JsonlMessage[] = [];
  for (const line of content.split('\n')) {
    if (!line.trim()) continue;
    try {
      const obj = JSON.parse(line);
      if (typeof obj?.role !== 'string') continue;
      const msg: JsonlMessage = {
        role: obj.role,
        text: typeof obj.text === 'string' ? obj.text : '',
      };
      if (Array.isArray(obj.toolCalls)) msg.toolCalls = obj.toolCalls as ToolCall[];
      if (typeof obj.toolCallId === 'string') msg.toolCallId = obj.toolCallId;
      if (typeof obj.result === 'string') msg.result = obj.result;
      messages.push(msg);
    } catch {
      // skip malformed line
    }
  }
  return messages;
}

export interface JsonlChatViewerProps {
  content: string;
}

export function JsonlChatViewer({ content }: JsonlChatViewerProps) {
  const messages = parseLines(content);

  if (messages.length === 0) {
    return <div className="jsonl-viewer-empty">No messages found in this file.</div>;
  }

  return (
    <div className="jsonl-viewer">
      {messages.map((msg, idx) => {
        const hasText = msg.text.length > 0;
        const hasToolCalls = (msg.toolCalls?.length ?? 0) > 0;
        const hasToolResult = msg.result != null;

        // Skip truly empty messages (old-format placeholders with no tool data)
        if (!hasText && !hasToolCalls && !hasToolResult) return null;

        return (
          <div key={idx} className={`chat-bubble chat-bubble--${msg.role}`}>
            <div className="chat-bubble__header">{msg.role}</div>

            {hasText && (msg.role === 'assistant' ? (
              <div
                className="chat-bubble__content"
                dangerouslySetInnerHTML={{ __html: renderMarkdown(msg.text) }}
              />
            ) : (
              <div className="chat-bubble__content">{msg.text}</div>
            ))}

            {hasToolCalls && msg.toolCalls!.map((tc) => (
              <div key={tc.callId} className="tool-call-card">
                <div className="tool-call-card__name">→ {tc.name}</div>
                <pre className="tool-call-card__args">{JSON.stringify(tc.args, null, 2)}</pre>
              </div>
            ))}

            {hasToolResult && (
              <div className="tool-result-card">
                <pre className="tool-result-card__body">{msg.result}</pre>
              </div>
            )}
          </div>
        );
      })}
    </div>
  );
}
