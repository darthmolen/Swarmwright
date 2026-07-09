// Integration tests for dashboard single-swarm focus.
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

const refreshSpy = vi.fn<() => Promise<void>>();
const useSwarmListMock = vi.fn();
vi.mock('../hooks/useSwarmList', () => ({
  useSwarmList: () => useSwarmListMock(),
}));

vi.mock('../hooks/useSSE', () => ({
  useSSE: (_opts: { url: string; enabled?: boolean }) => {
    return { connected: false, disconnect: () => {} };
  },
}));

vi.mock('../hooks/useMermaid', () => ({
  useMermaid: () => {},
}));

vi.mock('../components/InboxFeed', () => ({
  InboxFeed: ({ messages }: { messages?: Array<{ sender: string }> }) => (
    <div data-testid="inbox-feed" data-count={messages?.length ?? 0}>
      {(messages ?? []).map((m, i) => (
        <div key={i} data-testid={`msg-${m.sender}`}>{m.sender}</div>
      ))}
    </div>
  ),
}));

vi.mock('../components/AgentRoster', () => ({
  AgentRoster: ({ agents }: { agents?: Array<{ name: string }> }) => (
    <div data-testid="agent-roster" data-count={agents?.length ?? 0}>
      {(agents ?? []).map((a) => (
        <div key={a.name} data-testid={`agent-${a.name}`}>{a.name}</div>
      ))}
    </div>
  ),
}));

vi.mock('../components/TaskBoard', () => ({
  TaskBoard: ({ tasks }: { tasks?: Array<{ id: string; subject: string }> }) => (
    <div data-testid="task-board" data-count={tasks?.length ?? 0}>
      {(tasks ?? []).map((t) => (
        <div key={t.id} data-testid={`task-${t.id}`}>{t.subject}</div>
      ))}
    </div>
  ),
}));

let capturedOnStart: ((id: string) => void) | null = null;
vi.mock('../components/SwarmControls', () => ({
  SwarmControls: ({ onStart }: { onStart: (id: string) => void }) => {
    capturedOnStart = onStart;
    return <div data-testid="swarm-controls" />;
  },
}));

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

/** Build a fetch mock that answers hydration requests for a given swarm. */
function makeFetchMock(swarmConfigs: Record<string, {
  phase: string;
  isRunning: boolean;
  tasks: Array<{ id: string; subject: string; status: string }>;
  agents: Array<{ name: string; role: string; displayName: string }>;
}>) {
  return async (input: RequestInfo | URL) => {
    const url = typeof input === 'string' ? input : (input as URL).toString();

    for (const [id, config] of Object.entries(swarmConfigs)) {
      if (url.endsWith(`/api/swarm/${id}`) && !url.includes('/tasks') && !url.includes('/agents') && !url.includes('/messages') && !url.includes('/events') && !url.includes('/artifacts')) {
        return {
          ok: true,
          json: async () => ({
            swarmId: id,
            goal: `Goal for ${id}`,
            templateKey: 'deep-research',
            phase: config.phase,
            isRunning: config.isRunning,
          }),
        } as Response;
      }
      if (url.endsWith(`/api/swarm/${id}/tasks`)) {
        return { ok: true, json: async () => config.tasks } as Response;
      }
      if (url.endsWith(`/api/swarm/${id}/agents`)) {
        return { ok: true, json: async () => config.agents } as Response;
      }
      if (url.endsWith(`/api/swarm/${id}/messages`)) {
        return { ok: true, json: async () => [] } as Response;
      }
      if (url.endsWith(`/api/swarm/${id}/events?limit=100`)) {
        return { ok: true, json: async () => [] } as Response;
      }
    }

    return { ok: true, json: async () => ({}) } as Response;
  };
}

beforeEach(() => {
  vi.restoreAllMocks();
  refreshSpy.mockReset();
  refreshSpy.mockResolvedValue(undefined);
  capturedOnStart = null;
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

describe('Dashboard single-swarm focus', () => {
  it('App_noDashboardSwarm_showsEmptyState', async () => {
    render(<App />);

    await waitFor(() => {
      expect(screen.getByTestId('swarm-controls')).toBeTruthy();
    });

    // Dashboard should show empty state when no swarm is focused
    expect(screen.getByText(/Select a swarm/i)).toBeTruthy();
    // TaskBoard should not be rendered (or receive empty tasks)
    expect(screen.queryByTestId('task-board')).toBeNull();
  });

  it('App_startSwarm_autoFocusesDashboard', async () => {
    (globalThis.fetch as ReturnType<typeof vi.fn>).mockImplementation(
      makeFetchMock({
        'new-swarm-1': {
          phase: 'executing',
          isRunning: true,
          tasks: [{ id: 'auto-t1', subject: 'Auto-focused task', status: 'Pending' }],
          agents: [{ name: 'auto-agent', role: 'researcher', displayName: 'Researcher' }],
        },
      }),
    );

    render(<App />);

    await waitFor(() => {
      expect(capturedOnStart).not.toBeNull();
    });

    // Start a swarm — should auto-focus the dashboard on it
    await act(async () => {
      capturedOnStart!('new-swarm-1');
    });

    // Empty state should be gone, dashboard should show
    await waitFor(() => {
      expect(screen.queryByText(/Select a swarm/i)).toBeNull();
    });
  });

  it('App_hydrateHistoricSwarm_showsOnlyThatSwarmData', async () => {
    useSwarmListMock.mockReturnValue({
      swarms: [
        makeItem('historic-1', {
          phase: 'complete',
          isRunning: false,
          goal: 'Historic swarm',
          completedAt: '2026-04-10T13:00:00Z',
        }),
      ],
      loading: false,
      error: null,
      refresh: refreshSpy,
    });

    (globalThis.fetch as ReturnType<typeof vi.fn>).mockImplementation(
      makeFetchMock({
        'historic-1': {
          phase: 'complete',
          isRunning: false,
          tasks: [
            { id: 'hist-t1', subject: 'Historic task 1', status: 'Completed' },
            { id: 'hist-t2', subject: 'Historic task 2', status: 'Completed' },
          ],
          agents: [
            { name: 'hist-agent', role: 'researcher', displayName: 'Researcher' },
          ],
        },
      }),
    );

    render(<App />);

    // Wait for the list to render
    await waitFor(() => {
      expect(screen.getByText(/Historic swarm/)).toBeTruthy();
    });

    // Click the hydrate button (title/content area) for the historic swarm
    await act(async () => {
      screen.getByTestId('report-hydrate-historic-1').click();
    });

    // Dashboard should show the historic swarm's tasks
    await waitFor(() => {
      expect(screen.getByTestId('task-hist-t1')).toBeTruthy();
      expect(screen.getByTestId('task-hist-t2')).toBeTruthy();
    }, { timeout: 3000 });

    // Agent should also appear
    expect(screen.getByTestId('agent-hist-agent')).toBeTruthy();
  });

  it('App_hydrateSwarm_dismissesReportView', async () => {
    // When the user is on the dashboard and navigates to report view via the
    // nav dot, then clicks "← Dashboard" and hydrates a swarm, the report view
    // should stay dismissed. More importantly: the onHydrate handler must clear
    // reportSwarmId so report view doesn't persist when switching swarms.
    useSwarmListMock.mockReturnValue({
      swarms: [
        makeItem('report-swarm', {
          phase: 'complete',
          isRunning: false,
          goal: 'Report swarm',
          completedAt: '2026-04-10T13:00:00Z',
        }),
        makeItem('hydrate-target', {
          phase: 'complete',
          isRunning: false,
          goal: 'Hydrate target',
          completedAt: '2026-04-10T14:00:00Z',
        }),
      ],
      loading: false,
      error: null,
      refresh: refreshSpy,
    });

    (globalThis.fetch as ReturnType<typeof vi.fn>).mockImplementation(
      makeFetchMock({
        'report-swarm': {
          phase: 'complete',
          isRunning: false,
          tasks: [{ id: 'rep-t1', subject: 'Report task', status: 'Completed' }],
          agents: [],
        },
        'hydrate-target': {
          phase: 'complete',
          isRunning: false,
          tasks: [{ id: 'hy-t1', subject: 'Hydrate task', status: 'Completed' }],
          agents: [],
        },
      }),
    );

    render(<App />);

    await waitFor(() => {
      expect(screen.getByText(/Report swarm/)).toBeTruthy();
    });

    // Enter report view via nav dot
    await act(async () => {
      screen.getByTestId('report-nav-report-swarm').click();
    });

    await waitFor(() => {
      expect(screen.getByText(/← Dashboard/)).toBeTruthy();
    });

    // Go back to dashboard
    await act(async () => {
      screen.getByText(/← Dashboard/).click();
    });

    await waitFor(() => {
      expect(screen.queryByText(/← Dashboard/)).toBeNull();
    });

    // Now hydrate the target swarm — should show its data on dashboard
    await act(async () => {
      screen.getByTestId('report-hydrate-hydrate-target').click();
    });

    // Dashboard should show the hydrated swarm, NOT re-enter report view
    await waitFor(() => {
      expect(screen.queryByText(/← Dashboard/)).toBeNull();
      expect(screen.getByTestId('task-hy-t1')).toBeTruthy();
    }, { timeout: 3000 });
  });

  it('App_clickDifferentSwarm_replacesData', async () => {
    useSwarmListMock.mockReturnValue({
      swarms: [
        makeItem('swarm-A', {
          phase: 'complete',
          isRunning: false,
          goal: 'Swarm Alpha',
          completedAt: '2026-04-10T13:00:00Z',
        }),
        makeItem('swarm-B', {
          phase: 'complete',
          isRunning: false,
          goal: 'Swarm Beta',
          completedAt: '2026-04-10T14:00:00Z',
        }),
      ],
      loading: false,
      error: null,
      refresh: refreshSpy,
    });

    (globalThis.fetch as ReturnType<typeof vi.fn>).mockImplementation(
      makeFetchMock({
        'swarm-A': {
          phase: 'complete',
          isRunning: false,
          tasks: [{ id: 'a-t1', subject: 'Alpha task', status: 'Completed' }],
          agents: [{ name: 'alpha-agent', role: 'dev', displayName: 'Alpha' }],
        },
        'swarm-B': {
          phase: 'complete',
          isRunning: false,
          tasks: [{ id: 'b-t1', subject: 'Beta task', status: 'Completed' }],
          agents: [{ name: 'beta-agent', role: 'dev', displayName: 'Beta' }],
        },
      }),
    );

    render(<App />);

    await waitFor(() => {
      expect(screen.getByText(/Swarm Alpha/)).toBeTruthy();
    });

    // Hydrate swarm-A
    await act(async () => {
      screen.getByTestId('report-hydrate-swarm-A').click();
    });

    await waitFor(() => {
      expect(screen.getByTestId('task-a-t1')).toBeTruthy();
    }, { timeout: 3000 });

    // Now hydrate swarm-B — should REPLACE swarm-A's data
    await act(async () => {
      screen.getByTestId('report-hydrate-swarm-B').click();
    });

    await waitFor(() => {
      expect(screen.getByTestId('task-b-t1')).toBeTruthy();
      // swarm-A's task should no longer be visible
      expect(screen.queryByTestId('task-a-t1')).toBeNull();
    }, { timeout: 3000 });

    // Agent should also switch
    expect(screen.getByTestId('agent-beta-agent')).toBeTruthy();
    expect(screen.queryByTestId('agent-alpha-agent')).toBeNull();
  });
});
