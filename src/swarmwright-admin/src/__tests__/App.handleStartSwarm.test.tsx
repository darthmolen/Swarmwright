// Copyright (c) CSAT.IT. All rights reserved. Integration tests for the useSwarmList wiring in App.tsx.
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor, act } from '@testing-library/react';
import type { SwarmListItem } from '../hooks/useSwarmList';

// Mock MSAL
const mockUseMsal = vi.fn();
vi.mock('@azure/msal-react', () => ({
  useMsal: () => mockUseMsal(),
  MsalProvider: ({ children }: { children: React.ReactNode }) => <>{children}</>,
  AuthenticatedTemplate: ({ children }: { children: React.ReactNode }) => {
    const { accounts } = mockUseMsal();
    return accounts.length > 0 ? <>{children}</> : null;
  },
  UnauthenticatedTemplate: ({ children }: { children: React.ReactNode }) => {
    const { accounts } = mockUseMsal();
    return accounts.length === 0 ? <>{children}</> : null;
  },
}));

vi.mock('../auth/useAuthToken', () => ({
  useAuthToken: () => ({ getToken: async () => 'mock-token' }),
}));

vi.mock('../auth/ConfigContext', () => ({
  ConfigContext: { Provider: ({ children }: { children: React.ReactNode }) => <>{children}</> },
  useConfig: () => ({
    clientId: 'test',
    tenantId: 'test',
    defaultScope: 'api://test/.default',
    requiredPermissions: ['api://test/.default'],
  }),
}));

// Stub useSwarmList entirely so integration tests control its output.
const refreshSpy = vi.fn<() => Promise<void>>();
const useSwarmListMock = vi.fn();
vi.mock('../hooks/useSwarmList', () => ({
  useSwarmList: () => useSwarmListMock(),
}));

// Hoisted so the vi.mock factory can reference it — vi.mock is lifted above
// regular top-level statements, so a plain `const` would be out of scope.
const { useSseRecordedCalls } = vi.hoisted(() => {
  return { useSseRecordedCalls: [] as Array<{ url: string; enabled: boolean | undefined }> };
});

vi.mock('../hooks/useSSE', () => ({
  useSSE: (opts: { url: string; enabled?: boolean }) => {
    useSseRecordedCalls.push({ url: opts.url, enabled: opts.enabled });
    return { connected: false, disconnect: () => {} };
  },
}));

vi.mock('../hooks/useMermaid', () => ({
  useMermaid: () => {},
}));

vi.mock('../components/InboxFeed', () => ({
  InboxFeed: () => <div data-testid="inbox-feed" />,
}));

vi.mock('../components/AgentRoster', () => ({
  AgentRoster: () => <div data-testid="agent-roster" />,
}));

vi.mock('../components/TaskBoard', () => ({
  TaskBoard: ({ tasks }: { tasks?: Array<{ id: string; subject: string }> }) => (
    <div data-testid="task-board">
      {(tasks ?? []).map((t) => (
        <div key={t.id} data-testid={`task-${t.id}`}>{t.subject}</div>
      ))}
    </div>
  ),
}));

// Stub SwarmControls to expose an onStart hook we can invoke in the test.
let capturedOnStart: ((id: string) => void) | null = null;
vi.mock('../components/SwarmControls', () => ({
  SwarmControls: ({ onStart }: { onStart: (id: string) => void }) => {
    capturedOnStart = onStart;
    return <div data-testid="swarm-controls" />;
  },
}));

// Import App AFTER mocks.
import App from '../App';

function makeItem(id: string, overrides: Partial<SwarmListItem> = {}): SwarmListItem {
  return {
    swarmId: id,
    goal: `Goal for ${id}`,
    templateKey: 'deep-research',
    phase: 'executing',
    isRunning: true,
    createdAt: '2026-04-10T12:00:00Z',
    completedAt: null,
    lastEventAt: '2026-04-10T12:05:00Z',
    taskCount: 3,
    workerCount: 2,
    ...overrides,
  };
}

beforeEach(() => {
  vi.restoreAllMocks();
  refreshSpy.mockReset();
  refreshSpy.mockResolvedValue(undefined);
  capturedOnStart = null;
  useSseRecordedCalls.length = 0;
  useSwarmListMock.mockReturnValue({
    swarms: [],
    loading: false,
    error: null,
    refresh: refreshSpy,

  });
  mockUseMsal.mockReturnValue({
    instance: {
      loginPopup: vi.fn(),
      acquireTokenSilent: vi.fn().mockResolvedValue({ accessToken: 'token' }),
      logoutPopup: vi.fn(),
    },
    accounts: [{ username: 'test@example.com' }],
    inProgress: 'none',
  });
  vi.spyOn(globalThis, 'fetch').mockResolvedValue({
    ok: true,
    json: async () => ({}),
  } as Response);
});

describe('App — ReportList wiring via useSwarmList', () => {
  it('ReportList_rendersFromUseSwarmList', async () => {
    useSwarmListMock.mockReturnValue({
      swarms: [
        makeItem('active-1', { phase: 'executing', isRunning: true, goal: 'Active One' }),
        makeItem('done-1', {
          phase: 'complete',
          isRunning: false,
          completedAt: '2026-04-10T13:00:00Z',
          goal: 'Completed Done',
        }),
        makeItem('susp-1', {
          phase: 'awaiting_intervention',
          isRunning: false,
          goal: 'Suspended Work',
        }),
      ],
      loading: false,
      error: null,
      refresh: refreshSpy,
  
    });

    render(<App />);

    await waitFor(() => {
      expect(screen.getByText(/Active One/)).toBeTruthy();
    });
    expect(screen.getByText(/Completed Done/)).toBeTruthy();
    expect(screen.getByText(/Suspended Work/)).toBeTruthy();
  });
});

describe('App — clicking an unknown swarm triggers hydration', () => {
  it('App_clickingUnknownSwarm_hydratesAndRendersTasks', async () => {
    // Simulates the two-tab scenario: tab B opens the admin UI fresh, sees a
    // swarm in the left pane that was spawned in tab A, clicks it, and the
    // task panel renders the hydrated task list. The useSwarmList stub yields
    // one running swarm; the global fetch stub answers the four hydration
    // fetches the hook fires when the user clicks that row.

    useSwarmListMock.mockReturnValue({
      swarms: [
        makeItem('foreign-swarm-1', {
          phase: 'executing',
          isRunning: true,
          goal: 'Tab A launched this',
        }),
      ],
      loading: false,
      error: null,
      refresh: refreshSpy,
  
    });

    const hydratedTasks = [
      {
        id: 'task-hydrate-1',
        subject: 'Hydrated task one',
        description: '',
        workerRole: 'researcher',
        workerName: 'researcher',
        status: 'InProgress',
        blockedBy: [],
        result: '',
      },
    ];

    (globalThis.fetch as ReturnType<typeof vi.fn>).mockImplementation(
      async (input: RequestInfo | URL) => {
        const url = typeof input === 'string' ? input : (input as URL).toString();
        if (url.endsWith('/api/swarm/foreign-swarm-1')) {
          return {
            ok: true,
            json: async () => ({
              swarmId: 'foreign-swarm-1',
              goal: 'Tab A launched this',
              templateKey: 'deep-research',
              phase: 'executing',
              isRunning: true,
            }),
          } as Response;
        }
        if (url.endsWith('/api/swarm/foreign-swarm-1/tasks')) {
          return { ok: true, json: async () => hydratedTasks } as Response;
        }
        if (url.endsWith('/api/swarm/foreign-swarm-1/agents')) {
          return { ok: true, json: async () => [] } as Response;
        }
        if (url.endsWith('/api/swarm/foreign-swarm-1/messages')) {
          return { ok: true, json: async () => [] } as Response;
        }
        if (url.endsWith('/api/swarm/foreign-swarm-1/events?limit=100')) {
          return { ok: true, json: async () => [] } as Response;
        }
        // Default: the artifact fetch for report view, etc.
        return { ok: true, json: async () => ({}) } as Response;
      },
    );

    render(<App />);

    // The left pane renders the foreign swarm.
    await waitFor(() => {
      expect(screen.getByText(/Tab A launched this/)).toBeTruthy();
    });

    // Simulate click → setReportSwarmId('foreign-swarm-1')
    // Click path: the ReportList renders each item as a clickable element.
    // Use the rendered goal text as the click target.
    await act(async () => {
      screen.getByText(/Tab A launched this/).click();
    });

    // Hydration fires. Assert the task panel eventually shows the hydrated task.
    await waitFor(() => {
      const hydratedTaskNode = screen.queryByTestId('task-task-hydrate-1');
      expect(hydratedTaskNode).not.toBeNull();
    }, { timeout: 3000 });
  });

  it('App_clickingUnknownSwarm_doesNotDoubleSubscribeSSE', async () => {
    // Regression guard for the SSE race hole: when hydration starts on an
    // unknown swarm, useSwarmHydration's internal useSSE takes ownership of
    // the stream. After swarm.add dispatches, the id lands in knownSwarmIds
    // and (historically) the live-derived hydratedSseOwnedId in App.tsx
    // flipped to null on the next render, causing the SwarmConnection JSX
    // map to re-mount and call useSSE a SECOND time on the same swarm id.
    // Ownership must be sticky — count per-id useSSE invocations and assert
    // there is exactly ONE SSE subscription for the foreign swarm.
    useSwarmListMock.mockReturnValue({
      swarms: [
        makeItem('foreign-sse-1', {
          phase: 'executing',
          isRunning: true,
          goal: 'Foreign SSE swarm',
        }),
      ],
      loading: false,
      error: null,
      refresh: refreshSpy,
  
    });

    (globalThis.fetch as ReturnType<typeof vi.fn>).mockImplementation(
      async (input: RequestInfo | URL) => {
        const url = typeof input === 'string' ? input : (input as URL).toString();
        if (url.endsWith('/api/swarm/foreign-sse-1')) {
          return {
            ok: true,
            json: async () => ({
              swarmId: 'foreign-sse-1',
              goal: 'Foreign SSE swarm',
              templateKey: 'deep-research',
              phase: 'executing',
              isRunning: true,
            }),
          } as Response;
        }
        if (url.endsWith('/api/swarm/foreign-sse-1/tasks')) {
          return { ok: true, json: async () => [] } as Response;
        }
        if (url.endsWith('/api/swarm/foreign-sse-1/agents')) {
          return { ok: true, json: async () => [] } as Response;
        }
        if (url.endsWith('/api/swarm/foreign-sse-1/messages')) {
          return { ok: true, json: async () => [] } as Response;
        }
        if (url.endsWith('/api/swarm/foreign-sse-1/events?limit=100')) {
          return { ok: true, json: async () => [] } as Response;
        }
        return { ok: true, json: async () => ({}) } as Response;
      },
    );

    render(<App />);

    await waitFor(() => {
      expect(screen.getByText(/Foreign SSE swarm/)).toBeTruthy();
    });

    await act(async () => {
      screen.getByText(/Foreign SSE swarm/).click();
    });

    // Let hydration settle. `swarm.add` + STATE_SNAPSHOT + eventsSeed land
    // in the reducer, which re-renders App with the id now in knownSwarmIds.
    await act(async () => {
      await Promise.resolve();
      await Promise.resolve();
      await Promise.resolve();
      await Promise.resolve();
    });

    // Filter the recorded useSSE calls by this swarm's stream URL.
    const foreignCalls = useSseRecordedCalls.filter((c) =>
      c.url.includes('/api/swarm/foreign-sse-1/stream'),
    );

    // Exactly ONE SSE subscription for the foreign swarm id must be live.
    // The hook's useSSE is always called (rules of hooks), so earlier
    // disabled invocations may exist in the recorded list — we must count
    // ENABLED subscriptions, which represent actual wire connections.
    const enabledForeignCalls = foreignCalls.filter((c) => c.enabled !== false);
    // The set of DISTINCT subscribers (by render identity) is what matters:
    // one enabled call from useSwarmHydration, zero from SwarmConnection.
    // If SwarmConnection mounted a second subscriber, we'd see a call from
    // a component that doesn't pass `enabled` (SwarmConnection omits it, so
    // `enabled === undefined`). Count those separately.
    const undefEnabledCalls = foreignCalls.filter((c) => c.enabled === undefined);
    // SwarmConnection omits `enabled` → undefined. If any such call fired,
    // the JSX map double-subscribed.
    expect(undefEnabledCalls).toHaveLength(0);
    // And useSwarmHydration must have reached the enabled=true state at
    // least once (proving the hook actually owns SSE).
    expect(enabledForeignCalls.some((c) => c.enabled === true)).toBe(true);
  });
});

describe('App — handleStartSwarm refreshes the swarm list', () => {
  it('handleStartSwarm_afterSuccessfulCreate_callsRefreshSwarmList', async () => {
    render(<App />);

    await waitFor(() => {
      expect(capturedOnStart).not.toBeNull();
    });

    await act(async () => {
      capturedOnStart!('new-swarm-id');
    });

    await waitFor(() => {
      expect(refreshSpy).toHaveBeenCalled();
    });
  });

  it('handleStartSwarm_whenRefreshRejects_doesNotPropagateError', async () => {
    // Refresh can reject on a transient network blip. The swarm create POST
    // already succeeded, so the handler must swallow the refresh failure
    // instead of surfacing it as an unhandled rejection (SwarmControls calls
    // onStart without awaiting its returned promise).
    refreshSpy.mockReset();
    refreshSpy.mockRejectedValueOnce(new Error('boom'));

    const unhandled: unknown[] = [];
    const captureRejection = (reason: unknown): void => {
      unhandled.push(reason);
    };
    process.on('unhandledRejection', captureRejection);

    try {
      render(<App />);

      await waitFor(() => {
        expect(capturedOnStart).not.toBeNull();
      });

      // Invoking the captured handler must not throw synchronously or
      // asynchronously. We intentionally do NOT await its return value —
      // that mirrors how SwarmControls.onStart invokes it at line 94.
      await act(async () => {
        capturedOnStart!('new-swarm-id');
      });

      // Let any pending microtasks flush so a leaked rejection would fire.
      await act(async () => {
        await Promise.resolve();
        await Promise.resolve();
      });

      expect(refreshSpy).toHaveBeenCalled();
      expect(unhandled).toHaveLength(0);
    } finally {
      process.off('unhandledRejection', captureRejection);
    }
  });
});
