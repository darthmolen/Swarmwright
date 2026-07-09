import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { JsonlChatViewer } from '../components/JsonlChatViewer';

// Mock marked + DOMPurify (same pattern as existing tests) so we can assert text output directly.
vi.mock('marked', () => ({ marked: { parse: (md: string) => md } }));
vi.mock('dompurify', () => ({ default: { sanitize: (html: string) => html } }));

describe('JsonlChatViewer', () => {
  it('renders one bubble per JSONL line with role class', () => {
    const content = [
      JSON.stringify({ role: 'user', text: 'Hello' }),
      JSON.stringify({ role: 'assistant', text: 'Hi there' }),
      JSON.stringify({ role: 'system', text: 'You are…' }),
      JSON.stringify({ role: 'tool', text: '{"ok":true}' }),
    ].join('\n');

    const { container } = render(<JsonlChatViewer content={content} />);

    expect(container.querySelectorAll('.chat-bubble')).toHaveLength(4);
    expect(container.querySelector('.chat-bubble--user')).toBeInTheDocument();
    expect(container.querySelector('.chat-bubble--assistant')).toBeInTheDocument();
    expect(container.querySelector('.chat-bubble--system')).toBeInTheDocument();
    expect(container.querySelector('.chat-bubble--tool')).toBeInTheDocument();
    expect(screen.getByText('Hello')).toBeInTheDocument();
    expect(screen.getByText('Hi there')).toBeInTheDocument();
  });

  it('skips blank and malformed lines without throwing', () => {
    const content = [
      JSON.stringify({ role: 'user', text: 'valid' }),
      '',
      'not-json',
      '{"role":"user"}',
      JSON.stringify({ role: 'assistant', text: 'also valid' }),
    ].join('\n');

    const { container } = render(<JsonlChatViewer content={content} />);

    expect(container.querySelectorAll('.chat-bubble').length).toBeGreaterThanOrEqual(2);
    expect(screen.getByText('valid')).toBeInTheDocument();
    expect(screen.getByText('also valid')).toBeInTheDocument();
  });

  it('shows a fallback message when content has zero valid lines', () => {
    const { container } = render(<JsonlChatViewer content="total-garbage\n\n" />);
    expect(container.querySelectorAll('.chat-bubble')).toHaveLength(0);
    expect(screen.getByText(/no messages/i)).toBeInTheDocument();
  });

  it('renders assistant text through markdown (gets innerHTML from parser)', () => {
    const content = JSON.stringify({ role: 'assistant', text: '**bold**' });
    const { container } = render(<JsonlChatViewer content={content} />);
    const assistant = container.querySelector('.chat-bubble--assistant');
    expect(assistant?.innerHTML).toContain('**bold**');
  });

  it('renders tool call card for assistant message with toolCalls', () => {
    const content = JSON.stringify({
      role: 'assistant',
      text: '',
      toolCalls: [{ callId: 'c1', name: 'task_update', args: { status: 'InProgress' } }],
    });
    const { container } = render(<JsonlChatViewer content={content} />);

    const card = container.querySelector('.tool-call-card');
    expect(card).toBeInTheDocument();
    expect(card?.textContent).toContain('task_update');
    expect(card?.textContent).toContain('InProgress');
  });

  it('renders tool result for tool message with toolCallId and result', () => {
    const content = JSON.stringify({
      role: 'tool',
      text: '',
      toolCallId: 'c1',
      result: '{"success":true}',
    });
    const { container } = render(<JsonlChatViewer content={content} />);

    const card = container.querySelector('.tool-result-card');
    expect(card).toBeInTheDocument();
    expect(card?.textContent).toContain('success');
  });

  it('hides an assistant bubble that has neither text nor toolCalls', () => {
    const content = [
      JSON.stringify({ role: 'user', text: 'hi' }),
      JSON.stringify({ role: 'assistant', text: '' }),
      JSON.stringify({ role: 'assistant', text: 'Final response' }),
    ].join('\n');
    const { container } = render(<JsonlChatViewer content={content} />);

    // Only 2 bubbles: the user + the final assistant; the empty assistant is skipped.
    expect(container.querySelectorAll('.chat-bubble')).toHaveLength(2);
  });
});
