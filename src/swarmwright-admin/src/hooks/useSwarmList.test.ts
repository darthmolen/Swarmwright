// Tests for useSwarmList hook.
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, waitFor, act } from '@testing-library/react';
import { useSwarmList } from './useSwarmList';

// Mock useAuthToken so tests don't need ConfigProvider / MSAL.
const getTokenMock = vi.fn<() => Promise<string | null>>();
vi.mock('../auth/useAuthToken', () => ({
  useAuthToken: () => ({ getToken: getTokenMock }),
}));

/** Build a fetch Response-like object returning the given JSON body. */
function jsonResponse(body: unknown, ok = true): Response {
  return {
    ok,
    status: ok ? 200 : 500,
    json: async () => body,
  } as unknown as Response;
}

/** Shape returned by the backend list endpoint. */
interface BackendSwarm {
  swarmId: string;
  goal: string;
  templateKey: string | null;
  phase: string;
  isRunning: boolean;
  createdAt: string;
  completedAt: string | null;
  lastEventAt: string | null;
  taskCount: number;
  workerCount: number;
}

function makeSwarm(id: string, overrides: Partial<BackendSwarm> = {}): BackendSwarm {
  return {
    swarmId: id,
    goal: `Goal ${id}`,
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

describe('useSwarmList', () => {
  let fetchSpy: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    localStorage.clear();
    getTokenMock.mockReset();
    getTokenMock.mockResolvedValue('fake-token');
    fetchSpy = vi.spyOn(globalThis, 'fetch');
    // Default: return empty list so tests that don't override see a clean shape.
    fetchSpy.mockResolvedValue(jsonResponse({ swarms: [] }));
    // Default document.visibilityState to 'visible' in jsdom.
    Object.defineProperty(document, 'visibilityState', {
      configurable: true,
      get: () => 'visible',
    });
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it('useSwarmList_initialFetch_populatesSwarms', async () => {
    fetchSpy.mockResolvedValueOnce(
      jsonResponse({ swarms: [makeSwarm('abc'), makeSwarm('def')] }),
    );

    const { result } = renderHook(() => useSwarmList());

    await waitFor(() => {
      expect(result.current.swarms).toHaveLength(2);
    });
    expect(result.current.swarms[0].swarmId).toBe('abc');
    expect(result.current.loading).toBe(false);
    expect(result.current.error).toBeNull();
    // Regression guard: the hook must hit the paginated list endpoint.
    // VITE_API_URL is empty in tests so API_BASE === ''.
    expect(fetchSpy.mock.calls[0][0]).toBe('/api/swarm/?limit=50');
  });

  it('useSwarmList_pollsOnInterval', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    fetchSpy.mockResolvedValue(jsonResponse({ swarms: [] }));

    const { unmount } = renderHook(() => useSwarmList({ pollIntervalMs: 30_000 }));

    await waitFor(() => {
      expect(fetchSpy).toHaveBeenCalledTimes(1);
    });

    await act(async () => {
      await vi.advanceTimersByTimeAsync(30_000);
    });

    expect(fetchSpy.mock.calls.length).toBeGreaterThanOrEqual(2);
    unmount();
  });

  it('useSwarmList_pausesWhenTabHidden', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    fetchSpy.mockResolvedValue(jsonResponse({ swarms: [] }));

    const { unmount } = renderHook(() => useSwarmList({ pollIntervalMs: 30_000 }));

    await waitFor(() => {
      expect(fetchSpy).toHaveBeenCalledTimes(1);
    });

    // Flip visibility to hidden and dispatch event.
    Object.defineProperty(document, 'visibilityState', {
      configurable: true,
      get: () => 'hidden',
    });
    await act(async () => {
      document.dispatchEvent(new Event('visibilitychange'));
    });

    await act(async () => {
      await vi.advanceTimersByTimeAsync(60_000);
    });

    // Still only the initial fetch — polling paused.
    expect(fetchSpy).toHaveBeenCalledTimes(1);
    unmount();
  });

  it('useSwarmList_refreshesWhenTabVisible', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    fetchSpy.mockResolvedValue(jsonResponse({ swarms: [] }));

    const { unmount } = renderHook(() => useSwarmList({ pollIntervalMs: 30_000 }));

    await waitFor(() => {
      expect(fetchSpy).toHaveBeenCalledTimes(1);
    });

    // Hide.
    Object.defineProperty(document, 'visibilityState', {
      configurable: true,
      get: () => 'hidden',
    });
    await act(async () => {
      document.dispatchEvent(new Event('visibilitychange'));
    });

    // Show again — should immediately refetch.
    Object.defineProperty(document, 'visibilityState', {
      configurable: true,
      get: () => 'visible',
    });
    await act(async () => {
      document.dispatchEvent(new Event('visibilitychange'));
    });

    await waitFor(() => {
      expect(fetchSpy.mock.calls.length).toBeGreaterThanOrEqual(2);
    });
    unmount();
  });

  it('useSwarmList_exposesRefresh', async () => {
    fetchSpy.mockResolvedValue(jsonResponse({ swarms: [makeSwarm('abc')] }));

    const { result } = renderHook(() => useSwarmList());

    await waitFor(() => {
      expect(result.current.swarms).toHaveLength(1);
    });

    fetchSpy.mockResolvedValueOnce(
      jsonResponse({ swarms: [makeSwarm('abc'), makeSwarm('def')] }),
    );

    await act(async () => {
      await result.current.refresh();
    });

    expect(result.current.swarms).toHaveLength(2);
  });

  it('useSwarmList_propagatesFetchError', async () => {
    fetchSpy.mockRejectedValueOnce(new Error('boom'));

    const { result } = renderHook(() => useSwarmList());

    await waitFor(() => {
      expect(result.current.error).not.toBeNull();
    });
    expect(result.current.error?.message).toBe('boom');
    expect(result.current.loading).toBe(false);
  });

  it('useSwarmList_cleansUpOnUnmount', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    fetchSpy.mockResolvedValue(jsonResponse({ swarms: [] }));

    const { unmount } = renderHook(() => useSwarmList({ pollIntervalMs: 30_000 }));

    await waitFor(() => {
      expect(fetchSpy).toHaveBeenCalledTimes(1);
    });

    unmount();
    const callsAtUnmount = fetchSpy.mock.calls.length;

    await act(async () => {
      await vi.advanceTimersByTimeAsync(120_000);
    });

    expect(fetchSpy.mock.calls.length).toBe(callsAtUnmount);
  });

  it('useSwarmList_usesAuthToken_asyncHeaderBuild', async () => {
    getTokenMock.mockResolvedValue('fake-token');
    fetchSpy.mockResolvedValue(jsonResponse({ swarms: [] }));

    const { result } = renderHook(() => useSwarmList());

    await waitFor(() => {
      expect(result.current.loading).toBe(false);
    });

    expect(getTokenMock).toHaveBeenCalled();
    const firstCall = fetchSpy.mock.calls[0];
    // Regression guard: confirm full URL including the ?limit=50 query string.
    expect(firstCall[0]).toBe('/api/swarm/?limit=50');
    const init = firstCall[1] as RequestInit | undefined;
    const headers = init?.headers as Record<string, string> | undefined;
    expect(headers?.Authorization).toBe('Bearer fake-token');
  });

  it('useSwarmList_concurrentRefreshAndPoll_dropsToSingleFetch', async () => {
    // Build a deferred so we can hold the in-flight fetch open while we call
    // refresh() a second time. The in-flight lock should make refresh() return
    // the same promise instead of kicking off a second fetch.
    let resolveFetch: ((v: Response) => void) | null = null;
    const deferred = new Promise<Response>((resolve) => {
      resolveFetch = resolve;
    });
    fetchSpy.mockReset();
    fetchSpy.mockReturnValueOnce(deferred as unknown as Promise<Response>);
    // Any follow-up polls (there shouldn't be any in this test, but guard) get
    // an empty list.
    fetchSpy.mockResolvedValue(jsonResponse({ swarms: [] }));

    const { result, unmount } = renderHook(() => useSwarmList({ pollIntervalMs: 30_000 }));

    // Wait until the initial fetch has been issued — we can't observe state
    // yet because the deferred hasn't resolved.
    await waitFor(() => {
      expect(fetchSpy).toHaveBeenCalledTimes(1);
    });

    // Kick an imperative refresh() while the initial fetch is still in flight.
    // The in-flight lock should short-circuit this call: no extra fetch gets
    // issued, and refresh()'s returned promise resolves only after the
    // original fetch lands.
    let refreshResolved = false;
    const refreshPromise = act(async () => {
      await result.current.refresh();
      refreshResolved = true;
    });

    // Give microtasks a chance to run so the short-circuit branch has a
    // chance to fire a duplicate fetch (it shouldn't).
    await Promise.resolve();
    await Promise.resolve();
    expect(fetchSpy).toHaveBeenCalledTimes(1);
    expect(refreshResolved).toBe(false);

    // Now let the initial fetch complete. Both the initial fetch and the
    // pending refresh() should unblock together.
    await act(async () => {
      resolveFetch!(jsonResponse({ swarms: [makeSwarm('solo')] }));
      await refreshPromise;
    });

    expect(refreshResolved).toBe(true);
    expect(fetchSpy).toHaveBeenCalledTimes(1);
    expect(result.current.swarms).toHaveLength(1);
    expect(result.current.swarms[0].swarmId).toBe('solo');
    // Unmount to stop the interval and prevent cross-test state leakage.
    unmount();
  });
});
