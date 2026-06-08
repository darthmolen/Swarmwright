import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, cleanup, fireEvent, waitFor } from '@testing-library/react';
import { SwarmStatusWindow } from '../components/SwarmStatusWindow';
import type { Task } from '../types/swarm';
import type { SwarmContinueRecommendation } from '../hooks/useSwarmHydration';

// Canonical "all four recovery actions are valid" opinion the backend returns
// for AwaitingIntervention with viable recoverable state. Tests that want a
// clickable button surface pass this. Tests that want a DISABLED button
// surface (phase mismatch, missing recommendation) omit it intentionally.
const recoveryRecommendation: SwarmContinueRecommendation = {
  validActions: ['continue', 'smart-continue', 'force-synthesis', 'cancel'],
  recommendedAction: 'continue',
  rationale: 'Test scenario: recovery actions enabled.',
};

vi.mock('../auth/useAuthToken', () => ({
  useAuthToken: () => ({ getToken: () => Promise.resolve(null) }),
}));

vi.mock('react-hot-toast', () => ({
  default: {
    error: vi.fn(),
    success: vi.fn(),
  },
}));

const makeTask = (overrides: Partial<Task> = {}): Task => ({
  id: 't',
  subject: 's',
  description: '',
  worker_role: 'r',
  worker_name: 'w',
  status: 'pending',
  blocked_by: [],
  result: '',
  ...overrides,
});

describe('SwarmStatusWindow', () => {
  const originalFetch = globalThis.fetch;

  beforeEach(() => {
    globalThis.fetch = vi.fn(async () => new Response(null, { status: 204 })) as typeof fetch;
  });

  afterEach(() => {
    cleanup();
    globalThis.fetch = originalFetch;
    vi.clearAllMocks();
  });

  it('AwaitingIntervention renders the full recovery button surface', () => {
    render(
      <SwarmStatusWindow
        swarmId="sid"
        phase="awaiting_intervention"
        tasks={[makeTask({ status: 'failed', retry_count: 0 })]}
        agents={[]}
        roundNumber={1}
        onGoToReport={() => {}}
        onClose={() => {}}
        onDiagnose={() => {}}
      />,
    );

    expect(screen.getByRole('button', { name: /^Continue$/ })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /^Smart Continue$/ })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /^Diagnose$/ })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /^Force Synthesis$/ })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /^Cancel$/ })).toBeInTheDocument();
  });

  it('NeedsDiagnosis disables Continue when server recommendation excludes it from validActions', () => {
    // Gate keyed off the server's recommendation: if `continue` is not in
    // validActions, the button is disabled regardless of local task shape.
    render(
      <SwarmStatusWindow
        swarmId="sid"
        phase="needs_diagnosis"
        tasks={[makeTask({ status: 'failed', retry_count: 1 })]}
        agents={[]}
        roundNumber={1}
        recommendation={{
          validActions: ['smart-continue', 'force-synthesis', 'cancel'],
          recommendedAction: 'smart-continue',
          rationale: 'Retry budget exhausted. Smart Continue required.',
        }}
        onGoToReport={() => {}}
        onClose={() => {}}
      />,
    );

    expect(screen.getByRole('button', { name: /^Continue$/ })).toBeDisabled();
    expect(screen.getByRole('button', { name: /^Smart Continue$/ })).not.toBeDisabled();
  });

  it('AwaitingIntervention with orphan-only recommendation enables Continue', () => {
    // Regression for the orphan-InProgress defense-in-depth Layer 3: when the
    // backend recommendation includes `continue` in validActions, the SPA
    // must enable the button even with zero Failed tasks. The old client
    // heuristic (Failed-with-budget only) greyed Continue out in this case
    // and required a backend-direct call to recover the swarm.
    render(
      <SwarmStatusWindow
        swarmId="sid"
        phase="awaiting_intervention"
        tasks={[makeTask({ id: 'orphan', status: 'in_progress' })]}
        agents={[]}
        roundNumber={1}
        recommendation={{
          validActions: ['continue', 'smart-continue', 'force-synthesis', 'cancel'],
          recommendedAction: 'continue',
          rationale: '1 orphan InProgress task(s) detected.',
        }}
        onGoToReport={() => {}}
        onClose={() => {}}
      />,
    );

    expect(screen.getByRole('button', { name: /^Continue$/ })).not.toBeDisabled();
  });

  it('Recovery state without a recommendation disables every mutator gate', () => {
    // Guard: if hydration has not yet supplied a recommendation, every
    // gated button is disabled. Click-then-reject noise is worse than
    // delayed enable; the hydration refresh will populate the gate shortly.
    render(
      <SwarmStatusWindow
        swarmId="sid"
        phase="awaiting_intervention"
        tasks={[makeTask({ status: 'failed', retry_count: 0 })]}
        agents={[]}
        roundNumber={1}
        onGoToReport={() => {}}
        onClose={() => {}}
      />,
    );

    expect(screen.getByRole('button', { name: /^Continue$/ })).toBeDisabled();
    expect(screen.getByRole('button', { name: /^Smart Continue$/ })).toBeDisabled();
    expect(screen.getByRole('button', { name: /^Force Synthesis$/ })).toBeDisabled();
    expect(screen.getByRole('button', { name: /^Cancel$/ })).toBeDisabled();
  });

  it('Smart Continue click POSTs to the smart-continue endpoint', async () => {
    const fetchMock = globalThis.fetch as ReturnType<typeof vi.fn>;
    render(
      <SwarmStatusWindow
        swarmId="sid-42"
        phase="awaiting_intervention"
        tasks={[]}
        agents={[]}
        roundNumber={1}
        recommendation={recoveryRecommendation}
        onGoToReport={() => {}}
        onClose={() => {}}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: /^Smart Continue$/ }));
    await waitFor(() => expect((globalThis.fetch as ReturnType<typeof vi.fn>).mock.calls.length).toBeGreaterThan(0));

    expect(fetchMock).toHaveBeenCalledWith(
      expect.stringContaining('/api/swarm/sid-42/smart-continue'),
      expect.objectContaining({ method: 'POST' }),
    );
  });

  it('Diagnose click acquires the lock and invokes onDiagnose', async () => {
    const fetchMock = globalThis.fetch as ReturnType<typeof vi.fn>;
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify({ lockedBy: 'alice', lockedAt: '2026-04-19T00:00:00Z' }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      }),
    );
    const onDiagnose = vi.fn();

    render(
      <SwarmStatusWindow
        swarmId="sid-x"
        phase="awaiting_intervention"
        tasks={[]}
        agents={[]}
        roundNumber={1}
        recommendation={recoveryRecommendation}
        onGoToReport={() => {}}
        onClose={() => {}}
        onDiagnose={onDiagnose}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: /^Diagnose$/ }));

    await waitFor(() => expect(onDiagnose).toHaveBeenCalled());
    expect(fetchMock).toHaveBeenCalledWith(
      expect.stringContaining('/api/swarm/sid-x/lock'),
      expect.objectContaining({ method: 'POST' }),
    );
  });

  it('Cancel click POSTs to /cancel', async () => {
    const fetchMock = globalThis.fetch as ReturnType<typeof vi.fn>;
    render(
      <SwarmStatusWindow
        swarmId="sid-c"
        phase="awaiting_intervention"
        tasks={[]}
        agents={[]}
        roundNumber={1}
        recommendation={recoveryRecommendation}
        onGoToReport={() => {}}
        onClose={() => {}}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: /^Cancel$/ }));

    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    expect(fetchMock).toHaveBeenCalledWith(
      expect.stringContaining('/api/swarm/sid-c/cancel'),
      expect.objectContaining({ method: 'POST' }),
    );
  });

  it('shows lock badge and disables mutators when locked by someone else', () => {
    render(
      <SwarmStatusWindow
        swarmId="sid"
        phase="awaiting_intervention"
        tasks={[makeTask({ status: 'failed', retry_count: 0 })]}
        agents={[]}
        roundNumber={1}
        lockedBy="alice@corp.com"
        currentActor="bob@corp.com"
        recommendation={recoveryRecommendation}
        onGoToReport={() => {}}
        onClose={() => {}}
      />,
    );

    expect(screen.getByTestId('swarm-lock-badge')).toHaveTextContent('alice@corp.com');
    expect(screen.getByRole('button', { name: /^Continue$/ })).toBeDisabled();
    expect(screen.getByRole('button', { name: /^Smart Continue$/ })).toBeDisabled();
    expect(screen.getByRole('button', { name: /^Cancel$/ })).toBeDisabled();
  });

  it('does not show lock badge when current actor holds the lock', () => {
    render(
      <SwarmStatusWindow
        swarmId="sid"
        phase="awaiting_intervention"
        tasks={[makeTask({ status: 'failed', retry_count: 0 })]}
        agents={[]}
        roundNumber={1}
        lockedBy="alice@corp.com"
        currentActor="alice@corp.com"
        recommendation={recoveryRecommendation}
        onGoToReport={() => {}}
        onClose={() => {}}
      />,
    );

    expect(screen.queryByTestId('swarm-lock-badge')).toBeNull();
    expect(screen.getByRole('button', { name: /^Continue$/ })).not.toBeDisabled();
  });

  it('surfaces a lock-holder message on 423 response', async () => {
    const fetchMock = globalThis.fetch as ReturnType<typeof vi.fn>;
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify({ code: 'locked', lockedBy: 'carol@corp.com', lockedAt: '2026-04-19T00:00:00Z' }), {
        status: 423,
        headers: { 'Content-Type': 'application/json' },
      }),
    );

    render(
      <SwarmStatusWindow
        swarmId="sid"
        phase="awaiting_intervention"
        tasks={[]}
        agents={[]}
        roundNumber={1}
        recommendation={recoveryRecommendation}
        onGoToReport={() => {}}
        onClose={() => {}}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: /^Smart Continue$/ }));
    await waitFor(() => expect((globalThis.fetch as ReturnType<typeof vi.fn>).mock.calls.length).toBeGreaterThan(0));
    expect(await screen.findByText(/carol@corp.com is diagnosing/i)).toBeInTheDocument();
  });

  it('surfaces a terminal-state message on 410 response', async () => {
    const fetchMock = globalThis.fetch as ReturnType<typeof vi.fn>;
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify({ code: 'terminal_state', state: 'Complete' }), {
        status: 410,
        headers: { 'Content-Type': 'application/json' },
      }),
    );

    render(
      <SwarmStatusWindow
        swarmId="sid"
        phase="awaiting_intervention"
        tasks={[]}
        agents={[]}
        roundNumber={1}
        recommendation={recoveryRecommendation}
        onGoToReport={() => {}}
        onClose={() => {}}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: /^Smart Continue$/ }));
    await waitFor(() => expect((globalThis.fetch as ReturnType<typeof vi.fn>).mock.calls.length).toBeGreaterThan(0));
    expect(await screen.findByText(/this swarm is complete/i)).toBeInTheDocument();
  });

  it('Failed phase renders Manual Recover button that POSTs /mark-as-awaiting-intervention', async () => {
    const fetchMock = globalThis.fetch as ReturnType<typeof vi.fn>;

    render(
      <SwarmStatusWindow
        swarmId="sid-failed"
        phase="failed"
        tasks={[]}
        agents={[]}
        roundNumber={0}
        onGoToReport={() => {}}
        onClose={() => {}}
      />,
    );

    const manualRecoverBtn = screen.getByRole('button', { name: /^Manual Recover$/ });
    expect(manualRecoverBtn).toBeInTheDocument();

    fireEvent.click(manualRecoverBtn);
    await waitFor(() => expect(fetchMock.mock.calls.length).toBeGreaterThan(0));

    expect(fetchMock).toHaveBeenCalledWith(
      expect.stringContaining('/api/swarm/sid-failed/mark-as-awaiting-intervention'),
      expect.objectContaining({ method: 'POST' }),
    );
  });

  it('fires onManualRecoverSuccess after a 204 so the dashboard can re-hydrate', async () => {
    // Manual Recover flips DB state to AwaitingIntervention, but the
    // Failed swarm has no SSE stream open (terminal swarms don't hydrate
    // one). The UI needs a post-success hook so the parent can invalidate
    // the hydration cache and fetch fresh metadata — without this, the
    // user clicks Manual Recover, sees 204 in devtools, and stares at the
    // unchanged banner.
    const onManualRecoverSuccess = vi.fn();

    render(
      <SwarmStatusWindow
        swarmId="sid-hook"
        phase="failed"
        tasks={[]}
        agents={[]}
        roundNumber={0}
        onGoToReport={() => {}}
        onClose={() => {}}
        onManualRecoverSuccess={onManualRecoverSuccess}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: /^Manual Recover$/ }));
    await waitFor(() => expect(onManualRecoverSuccess).toHaveBeenCalledWith('sid-hook'));
  });

  it('does NOT fire onManualRecoverSuccess when the mark-as-awaiting-intervention POST fails', async () => {
    const fetchMock = globalThis.fetch as ReturnType<typeof vi.fn>;
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify({ code: 'invalid_transition' }), {
        status: 409,
        headers: { 'Content-Type': 'application/json' },
      }),
    );
    const onManualRecoverSuccess = vi.fn();

    render(
      <SwarmStatusWindow
        swarmId="sid-err"
        phase="failed"
        tasks={[]}
        agents={[]}
        roundNumber={0}
        onGoToReport={() => {}}
        onClose={() => {}}
        onManualRecoverSuccess={onManualRecoverSuccess}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: /^Manual Recover$/ }));
    await waitFor(() => expect(fetchMock.mock.calls.length).toBeGreaterThan(0));

    expect(onManualRecoverSuccess).not.toHaveBeenCalled();
  });

  it('fires onRecoverySuccess after a 204 Continue so the dashboard re-hydrates and opens SSE', async () => {
    // When Continue succeeds, the backend flips isRunning false → true
    // (AwaitingIntervention → Executing). useSwarmHydration only opens SSE
    // when hydratedMeta.isRunning is true at hydration time, so without this
    // callback the SPA never reconnects to the event stream and the UI
    // stays frozen at the pre-Continue snapshot — even though the swarm is
    // actively making progress in the backend.
    const onRecoverySuccess = vi.fn();

    render(
      <SwarmStatusWindow
        swarmId="sid-continue"
        phase="awaiting_intervention"
        tasks={[]}
        agents={[]}
        roundNumber={1}
        recommendation={recoveryRecommendation}
        onGoToReport={() => {}}
        onClose={() => {}}
        onRecoverySuccess={onRecoverySuccess}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: /^Continue$/ }));
    await waitFor(() => expect(onRecoverySuccess).toHaveBeenCalledWith('sid-continue'));
  });

  it('fires onRecoverySuccess after a 204 Smart Continue', async () => {
    const onRecoverySuccess = vi.fn();

    render(
      <SwarmStatusWindow
        swarmId="sid-smart"
        phase="awaiting_intervention"
        tasks={[]}
        agents={[]}
        roundNumber={1}
        recommendation={recoveryRecommendation}
        onGoToReport={() => {}}
        onClose={() => {}}
        onRecoverySuccess={onRecoverySuccess}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: /^Smart Continue$/ }));
    await waitFor(() => expect(onRecoverySuccess).toHaveBeenCalledWith('sid-smart'));
  });

  it('fires onRecoverySuccess after a 204 Force Synthesis', async () => {
    const onRecoverySuccess = vi.fn();

    render(
      <SwarmStatusWindow
        swarmId="sid-skip"
        phase="awaiting_intervention"
        tasks={[]}
        agents={[]}
        roundNumber={1}
        recommendation={recoveryRecommendation}
        onGoToReport={() => {}}
        onClose={() => {}}
        onRecoverySuccess={onRecoverySuccess}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: /^Force Synthesis$/ }));
    await waitFor(() => expect(onRecoverySuccess).toHaveBeenCalledWith('sid-skip'));
  });

  it('does NOT fire onRecoverySuccess when Continue returns 409', async () => {
    const fetchMock = globalThis.fetch as ReturnType<typeof vi.fn>;
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify({ code: 'no_retry_budget' }), {
        status: 409,
        headers: { 'Content-Type': 'application/json' },
      }),
    );
    const onRecoverySuccess = vi.fn();

    render(
      <SwarmStatusWindow
        swarmId="sid-rejected"
        phase="awaiting_intervention"
        tasks={[]}
        agents={[]}
        roundNumber={1}
        recommendation={recoveryRecommendation}
        onGoToReport={() => {}}
        onClose={() => {}}
        onRecoverySuccess={onRecoverySuccess}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: /^Continue$/ }));
    await waitFor(() => expect(fetchMock.mock.calls.length).toBeGreaterThan(0));

    expect(onRecoverySuccess).not.toHaveBeenCalled();
  });
});
