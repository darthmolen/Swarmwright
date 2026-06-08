// Copyright (c) CSAT.IT. All rights reserved. Tests for the useSwarmHydration hook.
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, waitFor, act } from '@testing-library/react';
import { useCallback, useState } from 'react';
import type { MultiSwarmAction } from './useSwarmState';

// Mock useAuthToken — the hook's auth token fetch would otherwise require MSAL.
const getTokenMock = vi.fn<() => Promise<string | null>>();
vi.mock('../auth/useAuthToken', () => ({
  useAuthToken: () => ({ getToken: getTokenMock }),
}));

// Mock useSSE so we can spy on when it is (or is not) invoked with enabled:true.
const useSseSpy = vi.fn();
vi.mock('./useSSE', () => ({
  useSSE: (opts: unknown) => {
    useSseSpy(opts);
    return { connected: false, disconnect: () => {} };
  },
}));

// Import AFTER mocks.
import { useSwarmHydration } from './useSwarmHydration';

/** Build a fetch Response-like object returning the given JSON body. */
function jsonResponse(body: unknown, ok = true): Response {
  return {
    ok,
    status: ok ? 200 : 500,
    json: async () => body,
  } as unknown as Response;
}

/** Capturing dispatch so tests can inspect the full ordered action sequence. */
function createDispatchSpy(): {
  dispatch: (action: MultiSwarmAction) => void;
  actions: MultiSwarmAction[];
} {
  const actions: MultiSwarmAction[] = [];
  return {
    actions,
    dispatch: (action: MultiSwarmAction) => {
      actions.push(action);
    },
  };
}

describe('useSwarmHydration', () => {
  let fetchSpy: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    getTokenMock.mockReset();
    getTokenMock.mockResolvedValue('fake-token');
    useSseSpy.mockClear();
    fetchSpy = vi.spyOn(globalThis, 'fetch');
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('useSwarmHydration_unknownSwarm_fetchesMetadataTasksAgentsEvents', async () => {
    fetchSpy.mockImplementation(async (input: RequestInfo | URL) => {
      const url = typeof input === 'string' ? input : (input as URL).toString();
      if (url.endsWith('/api/swarm/abc')) {
        return jsonResponse({
          swarmId: 'abc', goal: 'g', templateKey: 'dr', phase: 'executing', isRunning: false,
        });
      }
      if (url.endsWith('/api/swarm/abc/tasks')) return jsonResponse([]);
      if (url.endsWith('/api/swarm/abc/agents')) return jsonResponse([]);
      if (url.endsWith('/api/swarm/abc/messages')) return jsonResponse([]);
      if (url.endsWith('/api/swarm/abc/events?limit=100')) return jsonResponse([]);
      throw new Error(`Unexpected fetch: ${url}`);
    });

    const { dispatch } = createDispatchSpy();
    renderHook(() =>
      useSwarmHydration('abc', { knownSwarmIds: new Set<string>(), dispatch }),
    );

    await waitFor(() => {
      expect(fetchSpy).toHaveBeenCalledTimes(5);
    });

    const urls = fetchSpy.mock.calls.map((c: unknown[]) => String(c[0]));
    expect(urls).toContain('/api/swarm/abc');
    expect(urls).toContain('/api/swarm/abc/tasks');
    expect(urls).toContain('/api/swarm/abc/agents');
    expect(urls).toContain('/api/swarm/abc/messages');
    expect(urls).toContain('/api/swarm/abc/events?limit=100');
  });

  it('useSwarmHydration_unknownRunningSwarm_opensSseStream', async () => {
    fetchSpy.mockImplementation(async (input: RequestInfo | URL) => {
      const url = typeof input === 'string' ? input : (input as URL).toString();
      if (url.endsWith('/api/swarm/running-1')) {
        return jsonResponse({
          swarmId: 'running-1', goal: 'g', templateKey: null, phase: 'executing', isRunning: true,
        });
      }
      if (url.endsWith('/tasks') || url.endsWith('/agents') || url.endsWith('/messages')) return jsonResponse([]);
      if (url.includes('/events?limit=100')) return jsonResponse([]);
      throw new Error(`Unexpected: ${url}`);
    });

    const { dispatch } = createDispatchSpy();
    renderHook(() =>
      useSwarmHydration('running-1', { knownSwarmIds: new Set<string>(), dispatch }),
    );

    await waitFor(() => {
      // After metadata lands, the hook re-renders and useSSE is invoked with enabled:true.
      const enabledCalls = useSseSpy.mock.calls.filter(
        (c) => (c[0] as { enabled: boolean }).enabled === true,
      );
      expect(enabledCalls.length).toBeGreaterThan(0);
    });
  });

  it('useSwarmHydration_unknownCompletedSwarm_doesNotOpenSse', async () => {
    fetchSpy.mockImplementation(async (input: RequestInfo | URL) => {
      const url = typeof input === 'string' ? input : (input as URL).toString();
      if (url.endsWith('/api/swarm/done-1')) {
        return jsonResponse({
          swarmId: 'done-1', goal: 'g', templateKey: null, phase: 'complete', isRunning: false,
        });
      }
      if (url.endsWith('/tasks') || url.endsWith('/agents') || url.endsWith('/messages')) return jsonResponse([]);
      if (url.includes('/events?limit=100')) return jsonResponse([]);
      throw new Error(`Unexpected: ${url}`);
    });

    const { dispatch, actions } = createDispatchSpy();
    renderHook(() =>
      useSwarmHydration('done-1', { knownSwarmIds: new Set<string>(), dispatch }),
    );

    // Wait for hydration to finish (swarm.eventsSeed dispatched).
    await waitFor(() => {
      expect(actions.some((a) => a.type === 'swarm.eventsSeed')).toBe(true);
    });

    // useSSE was called (rules of hooks) but NEVER with enabled:true.
    const enabledCalls = useSseSpy.mock.calls.filter(
      (c) => (c[0] as { enabled: boolean }).enabled === true,
    );
    expect(enabledCalls).toHaveLength(0);
  });

  it('useSwarmHydration_knownSwarm_skipsHydration', async () => {
    const { dispatch, actions } = createDispatchSpy();
    renderHook(() =>
      useSwarmHydration('known-1', {
        knownSwarmIds: new Set<string>(['known-1']),
        dispatch,
      }),
    );

    // Short grace period — we are asserting that NO fetches fire.
    await act(async () => {
      await Promise.resolve();
      await Promise.resolve();
    });

    expect(fetchSpy).not.toHaveBeenCalled();
    expect(actions).toHaveLength(0);

    // useSSE is still called (rules of hooks) but must be disabled.
    const enabledCalls = useSseSpy.mock.calls.filter(
      (c) => (c[0] as { enabled: boolean }).enabled === true,
    );
    expect(enabledCalls).toHaveLength(0);
  });

  it('useSwarmHydration_fetchError_setsErrorState', async () => {
    fetchSpy.mockImplementation(async (input: RequestInfo | URL) => {
      const url = typeof input === 'string' ? input : (input as URL).toString();
      if (url.endsWith('/api/swarm/err-1')) {
        return jsonResponse({ error: 'not found' }, false);
      }
      return jsonResponse([]);
    });

    const { dispatch } = createDispatchSpy();
    const { result } = renderHook(() =>
      useSwarmHydration('err-1', { knownSwarmIds: new Set<string>(), dispatch }),
    );

    await waitFor(() => {
      expect(result.current.error).not.toBeNull();
    });
    expect(result.current.loading).toBe(false);
  });

  it('useSwarmHydration_clearsOnNullId', async () => {
    const { dispatch, actions } = createDispatchSpy();
    const { result } = renderHook(() =>
      useSwarmHydration(null, { knownSwarmIds: new Set<string>(), dispatch }),
    );

    await act(async () => {
      await Promise.resolve();
    });

    expect(fetchSpy).not.toHaveBeenCalled();
    expect(actions).toHaveLength(0);
    expect(result.current.loading).toBe(false);
    expect(result.current.error).toBeNull();
  });

  it('useSwarmHydration_dispatchesSwarmEventsSeed_withBackfilledEvents', async () => {
    const backfilled = [
      { type: 'STEP_STARTED', stepName: 'Planning' },
      {
        type: 'SWARM_CUSTOM',
        name: 'SWARM_TASK_CREATED',
        value: { id: 't1', subject: 'First task', status: 'pending' },
      },
      {
        type: 'SWARM_CUSTOM',
        name: 'SWARM_AGENT_SPAWNED',
        value: { name: 'researcher', role: 'Analyst', displayName: 'Analyst' },
      },
      { type: 'TEXT_MESSAGE_START', messageId: 'm1', role: 'assistant', agentName: 'researcher' },
      { type: 'TEXT_MESSAGE_END', messageId: 'm1' },
    ];

    fetchSpy.mockImplementation(async (input: RequestInfo | URL) => {
      const url = typeof input === 'string' ? input : (input as URL).toString();
      if (url.endsWith('/api/swarm/seed-1')) {
        return jsonResponse({
          swarmId: 'seed-1', goal: 'g', templateKey: null, phase: 'executing', isRunning: false,
        });
      }
      if (url.endsWith('/tasks') || url.endsWith('/agents') || url.endsWith('/messages')) return jsonResponse([]);
      if (url.includes('/events?limit=100')) return jsonResponse(backfilled);
      throw new Error(`Unexpected: ${url}`);
    });

    const { dispatch, actions } = createDispatchSpy();
    renderHook(() =>
      useSwarmHydration('seed-1', { knownSwarmIds: new Set<string>(), dispatch }),
    );

    await waitFor(() => {
      expect(actions.some((a) => a.type === 'swarm.eventsSeed')).toBe(true);
    });

    // Exactly one swarm.eventsSeed dispatch carrying all backfilled events in order.
    const seedActions = actions.filter((a) => a.type === 'swarm.eventsSeed');
    expect(seedActions).toHaveLength(1);
    const seedAction = seedActions[0] as Extract<MultiSwarmAction, { type: 'swarm.eventsSeed' }>;
    expect(seedAction.swarmId).toBe('seed-1');
    expect(seedAction.events).toEqual(backfilled);

    // Regression guard: the hook must NOT fan out to 5 individual swarm.event
    // dispatches for the backfilled events (that would drift from the pinned
    // eventsSeed contract). The one swarm.event dispatch we do expect is the
    // STATE_SNAPSHOT seeded from tasks + agents, not a backfilled event.
    const eventActionsFromBackfill = actions
      .filter((a) => a.type === 'swarm.event')
      .filter((a) => {
        const event = (a as Extract<MultiSwarmAction, { type: 'swarm.event' }>).event;
        // Backfilled events are step/task/text events, not the synthetic STATE_SNAPSHOT.
        return event.type !== 'STATE_SNAPSHOT';
      });
    expect(eventActionsFromBackfill).toHaveLength(0);
  });

  it('useSwarmHydration_exposesOwnedSwarmId_stableAcrossKnownSetUpdates', async () => {
    // Regression guard for the SSE race: ownership must be captured at the
    // moment hydration starts (id unknown) and must NOT flip off when the
    // caller re-renders with an updated `knownSwarmIds` set that now
    // contains the id (which is exactly what happens after the hook
    // dispatches `swarm.add`). Ownership only releases when `swarmId`
    // changes to a different value or becomes null.
    fetchSpy.mockImplementation(async (input: RequestInfo | URL) => {
      const url = typeof input === 'string' ? input : (input as URL).toString();
      if (url.endsWith('/api/swarm/sticky-1')) {
        return jsonResponse({
          swarmId: 'sticky-1', goal: 'g', templateKey: null, phase: 'executing', isRunning: true,
        });
      }
      if (url.endsWith('/tasks') || url.endsWith('/agents') || url.endsWith('/messages')) return jsonResponse([]);
      if (url.includes('/events?limit=100')) return jsonResponse([]);
      throw new Error(`Unexpected: ${url}`);
    });

    const { dispatch } = createDispatchSpy();
    const { result, rerender } = renderHook(
      ({ swarmId, knownSwarmIds }: { swarmId: string | null; knownSwarmIds: Set<string> }) =>
        useSwarmHydration(swarmId, { knownSwarmIds, dispatch }),
      {
        initialProps: {
          swarmId: 'sticky-1' as string | null,
          knownSwarmIds: new Set<string>(),
        },
      },
    );

    // After hydration settles, the hook owns SSE for sticky-1.
    await waitFor(() => {
      expect(result.current.ownedSwarmId).toBe('sticky-1');
    });

    // Simulate the caller's `knownSwarmIds` set updating to include sticky-1
    // (this is what happens after `swarm.add` lands in the reducer). The
    // hook's ownership must NOT flip to null.
    rerender({
      swarmId: 'sticky-1',
      knownSwarmIds: new Set<string>(['sticky-1']),
    });
    // Let any pending microtasks settle.
    await act(async () => {
      await Promise.resolve();
    });
    expect(result.current.ownedSwarmId).toBe('sticky-1');

    // Another set update with unrelated ids — ownership is still sticky.
    rerender({
      swarmId: 'sticky-1',
      knownSwarmIds: new Set<string>(['sticky-1', 'other-1']),
    });
    await act(async () => {
      await Promise.resolve();
    });
    expect(result.current.ownedSwarmId).toBe('sticky-1');

    // Clearing `swarmId` to null releases ownership.
    rerender({
      swarmId: null,
      knownSwarmIds: new Set<string>(['sticky-1']),
    });
    await act(async () => {
      await Promise.resolve();
    });
    expect(result.current.ownedSwarmId).toBeNull();
  });

  it('dispatches swarm.add before swarm.eventsSeed so the slot exists', async () => {
    fetchSpy.mockImplementation(async (input: RequestInfo | URL) => {
      const url = typeof input === 'string' ? input : (input as URL).toString();
      if (url.endsWith('/api/swarm/order-1')) {
        return jsonResponse({
          swarmId: 'order-1', goal: 'g', templateKey: null, phase: 'executing', isRunning: false,
        });
      }
      if (url.endsWith('/tasks') || url.endsWith('/agents') || url.endsWith('/messages')) return jsonResponse([]);
      if (url.includes('/events?limit=100')) return jsonResponse([]);
      throw new Error(`Unexpected: ${url}`);
    });

    const { dispatch, actions } = createDispatchSpy();
    renderHook(() =>
      useSwarmHydration('order-1', { knownSwarmIds: new Set<string>(), dispatch }),
    );

    await waitFor(() => {
      expect(actions.some((a) => a.type === 'swarm.eventsSeed')).toBe(true);
    });

    const addIdx = actions.findIndex((a) => a.type === 'swarm.add');
    const seedIdx = actions.findIndex((a) => a.type === 'swarm.eventsSeed');
    expect(addIdx).toBeGreaterThanOrEqual(0);
    expect(seedIdx).toBeGreaterThan(addIdx);
  });

  it('forceRefresh_afterInitialHydration_reFiresAllFiveFetchesAndDispatchesSwarmAddAgain', async () => {
    // Regression guard for the Manual Recover flow: after clicking Recover
    // the backend flips the swarm to AwaitingIntervention (204), then the
    // frontend calls forceRefresh(id). That must re-fetch the five hydration
    // endpoints so the UI sees the fresh phase. Previously the hydration
    // effect's dep array was [swarmId] only, so forceRefresh cleared the
    // per-instance cache and dispatched swarm.remove but never re-fired the
    // effect — the store ended up empty, the detail pane fell back to the
    // "Select a swarm" placeholder, and the AwaitingIntervention UI never
    // rendered. This test pins the contract that forceRefresh must actually
    // refresh.
    fetchSpy.mockImplementation(async (input: RequestInfo | URL) => {
      const url = typeof input === 'string' ? input : (input as URL).toString();
      if (url.endsWith('/api/swarm/refresh-1')) {
        return jsonResponse({
          swarmId: 'refresh-1', goal: 'g', templateKey: null, phase: 'failed', isRunning: false,
        });
      }
      if (url.endsWith('/tasks') || url.endsWith('/agents') || url.endsWith('/messages')) return jsonResponse([]);
      if (url.includes('/events?limit=100')) return jsonResponse([]);
      throw new Error(`Unexpected: ${url}`);
    });

    // Use a harness that wires dispatch back to the knownSwarmIds set,
    // mimicking the App.tsx useReducer + useMemo wiring. This is essential
    // because forceRefresh dispatches swarm.remove AND bumps refreshToken
    // in the same function call; React batches both updates so the re-fired
    // effect sees the updated knownSwarmIds. A spy dispatcher that doesn't
    // reflect the remove back into the set would leave knownSwarmIds stale
    // and the effect would short-circuit on the "known in store" guard.
    const actions: MultiSwarmAction[] = [];
    const { result } = renderHook(() => {
      const [knownIds, setKnownIds] = useState<Set<string>>(new Set());
      const dispatch = useCallback((action: MultiSwarmAction) => {
        actions.push(action);
        setKnownIds((prev) => {
          const next = new Set(prev);
          if (action.type === 'swarm.add') next.add(action.swarmId);
          else if (action.type === 'swarm.remove') next.delete(action.swarmId);
          return next;
        });
      }, []);
      return useSwarmHydration('refresh-1', { knownSwarmIds: knownIds, dispatch });
    });

    // Initial hydration: 5 fetches + one swarm.add.
    await waitFor(() => {
      expect(fetchSpy).toHaveBeenCalledTimes(5);
    });
    expect(actions.filter((a) => a.type === 'swarm.add')).toHaveLength(1);

    // User clicks Manual Recover; the POST returns 204; onSuccess fires
    // forceRefresh. The hook must re-trigger hydration: dispatch swarm.remove
    // (harness removes id from knownIds) and re-fire the effect.
    await act(async () => {
      result.current.forceRefresh('refresh-1');
    });

    // Five more fetches (total 10) and a second swarm.add dispatch.
    await waitFor(() => {
      expect(fetchSpy).toHaveBeenCalledTimes(10);
    });
    expect(actions.filter((a) => a.type === 'swarm.add')).toHaveLength(2);
    expect(actions.some((a) => a.type === 'swarm.remove' && a.swarmId === 'refresh-1')).toBe(true);
  });

  it('transforms EventEntity-shaped backfill into SwarmEvent before eventsSeed dispatch', async () => {
    // Backend /events endpoint returns EventEntity shape: { eventType, dataJson }
    const backfilled = [
      { eventType: 'STEP_STARTED', dataJson: '{"stepName":"Planning"}', createdAt: '2026-01-01T00:00:00Z' },
      { eventType: 'SWARM_CUSTOM', dataJson: '{"name":"SWARM_INBOX_MESSAGE","value":{"sender":"alpha","recipient":"leader","content":"alpha done"}}', createdAt: '2026-01-01T00:01:00Z' },
      { eventType: 'SWARM_CUSTOM', dataJson: '{"name":"SWARM_TASK_UPDATED","value":{"taskId":"t1","status":"Completed"}}', createdAt: '2026-01-01T00:02:00Z' },
    ];

    fetchSpy.mockImplementation(async (input: RequestInfo | URL) => {
      const url = typeof input === 'string' ? input : (input as URL).toString();
      if (url.endsWith('/api/swarm/entity-1')) {
        return jsonResponse({
          swarmId: 'entity-1', goal: 'g', templateKey: null, phase: 'complete', isRunning: false,
        });
      }
      if (url.endsWith('/tasks') || url.endsWith('/agents') || url.endsWith('/messages')) return jsonResponse([]);
      if (url.includes('/events?limit=100')) return jsonResponse(backfilled);
      throw new Error(`Unexpected: ${url}`);
    });

    const { dispatch, actions } = createDispatchSpy();
    renderHook(() =>
      useSwarmHydration('entity-1', { knownSwarmIds: new Set<string>(), dispatch }),
    );

    await waitFor(() => {
      expect(actions.some((a) => a.type === 'swarm.eventsSeed')).toBe(true);
    });

    const seedAction = actions.find((a) => a.type === 'swarm.eventsSeed') as
      Extract<MultiSwarmAction, { type: 'swarm.eventsSeed' }>;

    // Events should be transformed to SwarmEvent shape with `type` (not `eventType`)
    expect(seedAction.events).toHaveLength(3);
    expect(seedAction.events[0]).toHaveProperty('type', 'STEP_STARTED');
    expect(seedAction.events[0]).toHaveProperty('stepName', 'Planning');
    expect(seedAction.events[1]).toHaveProperty('type', 'SWARM_CUSTOM');
    expect(seedAction.events[1]).toHaveProperty('name', 'SWARM_INBOX_MESSAGE');
    expect(seedAction.events[2]).toHaveProperty('type', 'SWARM_CUSTOM');
    expect(seedAction.events[2]).toHaveProperty('name', 'SWARM_TASK_UPDATED');
  });
});
