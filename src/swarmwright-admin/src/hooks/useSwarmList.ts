// useSwarmList polling hook for the session list pane.
import { useCallback, useEffect, useRef, useState } from 'react';
import { useAuthToken } from '../auth/useAuthToken';

const API_BASE = import.meta.env.VITE_API_URL ?? '';
const DEFAULT_POLL_INTERVAL_MS = 30_000;
const DEFAULT_LIMIT = 50;

/**
 * Single swarm entry as returned by `GET /api/swarm/` after Batch 1's DTO augmentation.
 */
export interface SwarmListItem {
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

/** Options for the useSwarmList hook. */
export interface UseSwarmListOptions {
  /** Max entries to request from the server. Defaults to 50. */
  limit?: number;
  /** Poll interval in milliseconds. Defaults to 30_000. */
  pollIntervalMs?: number;
}

/** Return value of useSwarmList. */
export interface UseSwarmListResult {
  swarms: SwarmListItem[];
  loading: boolean;
  error: Error | null;
  /**
   * Imperative refresh. Resolves after the fetch + state update land so callers can
   * `await refresh()` before showing confirmation UI.
   */
  refresh: () => Promise<void>;
}

interface ListResponse {
  swarms?: SwarmListItem[];
}

/**
 * Poll-based hook that makes `GET /api/swarm/` the single source of truth for the
 * session list pane. Handles async token acquisition, visibility-aware pausing,
 * and imperative refresh.
 */
export function useSwarmList(options: UseSwarmListOptions = {}): UseSwarmListResult {
  const limit = options.limit ?? DEFAULT_LIMIT;
  const pollIntervalMs = options.pollIntervalMs ?? DEFAULT_POLL_INTERVAL_MS;

  const { getToken } = useAuthToken();
  const getTokenRef = useRef(getToken);
  getTokenRef.current = getToken;

  const [swarms, setSwarms] = useState<SwarmListItem[]>([]);
  const [loading, setLoading] = useState<boolean>(true);
  const [error, setError] = useState<Error | null>(null);

  const mountedRef = useRef<boolean>(true);

  // Serializes concurrent fetches. If a fetch is already in flight, subsequent
  // callers (e.g. imperative refresh() firing while an interval tick is pending)
  // receive the same promise instead of kicking off a second request.
  const inFlightRef = useRef<Promise<void> | null>(null);

  const fetchList = useCallback((): Promise<void> => {
    if (inFlightRef.current) {
      return inFlightRef.current;
    }
    const p = (async (): Promise<void> => {
      const token = await getTokenRef.current();
      const headers: Record<string, string> = {};
      if (token) {
        headers.Authorization = `Bearer ${token}`;
      }
      const url = `${API_BASE}/api/swarm/?limit=${limit}`;
      try {
        const response = await fetch(url, { headers });
        if (!response.ok) {
          throw new Error(`Swarm list request failed: ${response.status}`);
        }
        const data = (await response.json()) as ListResponse | SwarmListItem[];
        const list: SwarmListItem[] = Array.isArray(data) ? data : data.swarms ?? [];
        if (!mountedRef.current) return;
        setSwarms(list);
        setError(null);
        setLoading(false);
      } catch (err) {
        if (!mountedRef.current) return;
        setError(err instanceof Error ? err : new Error(String(err)));
        setLoading(false);
      }
    })();
    inFlightRef.current = p;
    // Clear the in-flight slot once this fetch completes (success or failure).
    // We don't rethrow here — errors are already captured into `error` state.
    void p.finally(() => {
      if (inFlightRef.current === p) {
        inFlightRef.current = null;
      }
    });
    return p;
  }, [limit]);

  const fetchListRef = useRef(fetchList);
  fetchListRef.current = fetchList;

  const refresh = useCallback(async (): Promise<void> => {
    await fetchListRef.current();
  }, []);

  // Mount: initial fetch, start interval, install visibility listener.
  useEffect(() => {
    mountedRef.current = true;

    let intervalId: ReturnType<typeof setInterval> | null = null;

    const startInterval = (): void => {
      if (intervalId !== null) return;
      intervalId = setInterval(() => {
        void fetchListRef.current();
      }, pollIntervalMs);
    };

    const stopInterval = (): void => {
      if (intervalId !== null) {
        clearInterval(intervalId);
        intervalId = null;
      }
    };

    const handleVisibility = (): void => {
      if (document.visibilityState === 'hidden') {
        stopInterval();
      } else {
        void fetchListRef.current();
        startInterval();
      }
    };

    // Initial fetch.
    void fetchListRef.current();

    if (document.visibilityState !== 'hidden') {
      startInterval();
    }

    document.addEventListener('visibilitychange', handleVisibility);

    return () => {
      mountedRef.current = false;
      stopInterval();
      document.removeEventListener('visibilitychange', handleVisibility);
    };
  }, [pollIntervalMs]);

  return { swarms, loading, error, refresh };
}
