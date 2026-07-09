import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor, cleanup } from '@testing-library/react';
import { InterventionView } from '../components/InterventionView';

vi.mock('../auth/useAuthToken', () => ({
  useAuthToken: () => ({ getToken: () => Promise.resolve(null) }),
}));

vi.mock('react-hot-toast', () => ({
  default: {
    error: vi.fn(),
    success: vi.fn(),
  },
}));

// TemplateEditorPanel and ChatPanel aren't the focus of these tests —
// stub them so we don't have to stand up their dependencies.
vi.mock('../components/TemplateEditorPanel', () => ({
  TemplateEditorPanel: () => <div data-testid="template-editor-panel" />,
}));

vi.mock('../components/ChatPanel', () => ({
  ChatPanel: () => <div data-testid="chat-panel" />,
}));

describe('InterventionView back-navigation', () => {
  const originalFetch = globalThis.fetch;

  beforeEach(() => {
    globalThis.fetch = vi.fn(async () => new Response(null, { status: 204 })) as typeof fetch;
  });

  afterEach(() => {
    cleanup();
    globalThis.fetch = originalFetch;
    vi.clearAllMocks();
  });

  it('Return-to-Dashboard click calls DELETE /lock then navigates back', async () => {
    const fetchMock = globalThis.fetch as ReturnType<typeof vi.fn>;
    const onBack = vi.fn();

    render(
      <InterventionView
        swarmId="sid-abc"
        templateKey="deep-research"
        tasks={[]}
        selectedTaskId=""
        onSelectTask={() => {}}
        agentOutputs={{}}
        onBack={onBack}
        onSaveAndRetry={() => {}}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: /Dashboard/ }));

    await waitFor(() => expect(onBack).toHaveBeenCalled());
    expect(fetchMock).toHaveBeenCalledWith(
      expect.stringContaining('/api/swarm/sid-abc/lock'),
      expect.objectContaining({ method: 'DELETE' }),
    );
  });

  it('still navigates back even when DELETE /lock fails', async () => {
    const fetchMock = globalThis.fetch as ReturnType<typeof vi.fn>;
    fetchMock.mockRejectedValueOnce(new Error('network down'));
    const onBack = vi.fn();

    render(
      <InterventionView
        swarmId="sid-abc"
        templateKey="deep-research"
        tasks={[]}
        selectedTaskId=""
        onSelectTask={() => {}}
        agentOutputs={{}}
        onBack={onBack}
        onSaveAndRetry={() => {}}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: /Dashboard/ }));

    await waitFor(() => expect(onBack).toHaveBeenCalled());
  });
});

describe('InterventionView recovery buttons', () => {
  const originalFetch = globalThis.fetch;

  beforeEach(() => {
    globalThis.fetch = vi.fn(async () => new Response(null, { status: 204 })) as typeof fetch;
  });

  afterEach(() => {
    cleanup();
    globalThis.fetch = originalFetch;
    vi.clearAllMocks();
  });

  it('renders the four recovery buttons in the footer', () => {
    render(
      <InterventionView
        swarmId="sid-recovery"
        templateKey="deep-research"
        tasks={[]}
        selectedTaskId=""
        onSelectTask={() => {}}
        agentOutputs={{}}
        onBack={() => {}}
        onSaveAndRetry={() => {}}
      />,
    );

    expect(screen.getByRole('button', { name: /^Continue$/ })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /^Smart Continue$/ })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /^Force Synthesis$/ })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /^Cancel$/ })).toBeInTheDocument();
  });

  it('Smart Continue click POSTs to /smart-continue then navigates back', async () => {
    const fetchMock = globalThis.fetch as ReturnType<typeof vi.fn>;
    const onBack = vi.fn();

    render(
      <InterventionView
        swarmId="sid-recovery"
        templateKey="deep-research"
        tasks={[]}
        selectedTaskId=""
        onSelectTask={() => {}}
        agentOutputs={{}}
        onBack={onBack}
        onSaveAndRetry={() => {}}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: /^Smart Continue$/ }));

    await waitFor(() => {
      expect(fetchMock).toHaveBeenCalledWith(
        expect.stringContaining('/api/swarm/sid-recovery/smart-continue'),
        expect.objectContaining({ method: 'POST' }),
      );
    });
    await waitFor(() => expect(onBack).toHaveBeenCalled());
  });

  it('Continue click POSTs to /continue then navigates back', async () => {
    const fetchMock = globalThis.fetch as ReturnType<typeof vi.fn>;
    const onBack = vi.fn();

    render(
      <InterventionView
        swarmId="sid-recovery"
        templateKey="deep-research"
        tasks={[]}
        selectedTaskId=""
        onSelectTask={() => {}}
        agentOutputs={{}}
        onBack={onBack}
        onSaveAndRetry={() => {}}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: /^Continue$/ }));

    await waitFor(() => {
      expect(fetchMock).toHaveBeenCalledWith(
        expect.stringContaining('/api/swarm/sid-recovery/continue'),
        expect.objectContaining({ method: 'POST' }),
      );
    });
    await waitFor(() => expect(onBack).toHaveBeenCalled());
  });

  it('Force Synthesis click POSTs to /skip then navigates back', async () => {
    const fetchMock = globalThis.fetch as ReturnType<typeof vi.fn>;
    const onBack = vi.fn();

    render(
      <InterventionView
        swarmId="sid-recovery"
        templateKey="deep-research"
        tasks={[]}
        selectedTaskId=""
        onSelectTask={() => {}}
        agentOutputs={{}}
        onBack={onBack}
        onSaveAndRetry={() => {}}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: /^Force Synthesis$/ }));

    await waitFor(() => {
      expect(fetchMock).toHaveBeenCalledWith(
        expect.stringContaining('/api/swarm/sid-recovery/skip'),
        expect.objectContaining({ method: 'POST' }),
      );
    });
    await waitFor(() => expect(onBack).toHaveBeenCalled());
  });

  it('Cancel click POSTs to /cancel then navigates back', async () => {
    const fetchMock = globalThis.fetch as ReturnType<typeof vi.fn>;
    const onBack = vi.fn();

    render(
      <InterventionView
        swarmId="sid-recovery"
        templateKey="deep-research"
        tasks={[]}
        selectedTaskId=""
        onSelectTask={() => {}}
        agentOutputs={{}}
        onBack={onBack}
        onSaveAndRetry={() => {}}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: /^Cancel$/ }));

    await waitFor(() => {
      expect(fetchMock).toHaveBeenCalledWith(
        expect.stringContaining('/api/swarm/sid-recovery/cancel'),
        expect.objectContaining({ method: 'POST' }),
      );
    });
    await waitFor(() => expect(onBack).toHaveBeenCalled());
  });

  it('stays on the view when a recovery endpoint returns a non-2xx response', async () => {
    const fetchMock = globalThis.fetch as ReturnType<typeof vi.fn>;
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify({ code: 'no_retry_budget' }), { status: 409 }),
    );
    const onBack = vi.fn();

    render(
      <InterventionView
        swarmId="sid-recovery"
        templateKey="deep-research"
        tasks={[]}
        selectedTaskId=""
        onSelectTask={() => {}}
        agentOutputs={{}}
        onBack={onBack}
        onSaveAndRetry={() => {}}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: /^Continue$/ }));

    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    // Failed recovery call should NOT auto-navigate back — the user stays
    // on the intervention view so they can pick a different action.
    expect(onBack).not.toHaveBeenCalled();
  });
});
