// Hydration hook that wires an unknown swarm into the current tab.
import { useCallback, useEffect, useRef, useState } from 'react';
import { useAuthToken } from '../auth/useAuthToken';
import { useSSE } from './useSSE';
import type { SwarmDispatch } from './useSwarmState';
import type { SwarmEvent } from '../types/swarm';
import { devLog } from '../lib/devLog';

const API_BASE = import.meta.env.VITE_API_URL ?? '';
const log = devLog('hydrate');

/**
 * Server-computed recovery opinion, a subset of the metadata response. The
 * SPA reads this to drive button gating + recommended-action highlighting.
 * Populated by the backend only when the swarm is in an actionable
 * non-terminal state (AwaitingIntervention / NeedsDiagnosis); otherwise the
 * outer field is <c>null</c>. Mirrors <c>SwarmContinueRecommendation</c>
 * in the server DTO.
 */
export interface SwarmContinueRecommendation {
  validActions: string[];
  recommendedAction: string;
  rationale: string;
}

/**
 * Swarm metadata shape returned by `GET /api/swarm/{id}`. Only fields the hook
 * needs are listed — other properties are ignored at the boundary.
 */
interface SwarmMetadataResponse {
  swarmId: string;
  goal: string;
  templateKey: string | null;
  phase: string;
  isRunning: boolean;
  recommendation: SwarmContinueRecommendation | null;
}

/**
 * Return value of `useSwarmHydration`. `loading` is true while the 4-fetch
 * hydration sequence is in flight; `error` is set if any fetch fails.
 *
 * `ownedSwarmId` identifies the swarm whose SSE stream this hook currently
 * owns. It is non-null only when the current `swarmId` was unknown at the
 * moment hydration started — i.e., when the hook is responsible for driving
 * its SSE connection via the internal `useSSE`. The caller MUST exclude this
 * id from any other SSE subscription sites (e.g., App.tsx's
 * `SwarmConnection` JSX maps) for as long as it is non-null, otherwise two
 * readers would race the single backend ChannelReader and drop events.
 *
 * Crucially, ownership is STICKY across `knownSwarmIds` set updates: once
 * the hook starts owning an id, a subsequent `swarm.add` dispatch (which
 * inserts the id into the store's active set) must NOT flip ownership off.
 * Ownership only releases when `swarmId` changes to a different id or null.
 */
export interface UseSwarmHydrationResult {
  loading: boolean;
  error: Error | null;
  ownedSwarmId: string | null;
  /**
   * Server-computed recommendation from the last successful metadata fetch,
   * or <c>null</c> when the swarm is not in an actionable non-terminal state.
   * Callers wire this to the recovery-button gates in SwarmStatusWindow so
   * the SPA follows the backend's acceptance rules instead of re-deriving
   * them client-side (which historically drifted from the handler).
   */
  recommendation: SwarmContinueRecommendation | null;
  /**
   * Force a re-hydration of the given swarm id. Drops it from the
   * hook's per-instance "already fetched" cache and dispatches
   * `swarm.remove` so the next render's effect sees the id as
   * unknown-to-the-store and runs the fetch+stream sequence from
   * scratch. Used by the Manual Recover action: a Failed swarm has
   * no active SSE, so the UI otherwise stays stale after its DB
   * state flips to AwaitingIntervention.
   */
  forceRefresh: (swarmId: string) => void;
}

/**
 * Optional override hook for testing. `multiSwarmReducer` snapshot is read
 * through this so tests can assert that a known swarm is skipped without
 * standing up a full React tree.
 */
export interface UseSwarmHydrationOptions {
  /**
   * Set of swarm ids the current tab already knows about (active + completed).
   * When the requested id is in this set, the hook short-circuits — no fetches,
   * no SSE, no dispatches.
   */
  knownSwarmIds: Set<string>;
  /** Dispatcher for the multi-swarm reducer. */
  dispatch: SwarmDispatch;
}

/**
 * Hydration hook that connects the current tab to a swarm it has never seen.
 *
 * When `swarmId` transitions to an id NOT in `options.knownSwarmIds`, the hook
 * fires five fetches in parallel:
 * - `GET /api/swarm/{id}`            → metadata (`isRunning`, `phase`, ...)
 * - `GET /api/swarm/{id}/tasks`      → seed the task list
 * - `GET /api/swarm/{id}/agents`     → seed the agent list
 * - `GET /api/swarm/{id}/messages`   → seed the inbox messages
 * - `GET /api/swarm/{id}/events?limit=100` → backfill the last 100 events
 *
 * After the four fetches settle, the hook dispatches:
 * 1. `swarm.add` — creates the per-swarm reducer slot.
 * 2. `swarm.eventsSeed` — a single action carrying the backfilled events array,
 *    which replays each event through the per-swarm reducer exactly as if it
 *    had arrived live via SSE. Derived state (tasks, agents, phase, messages)
 *    rebuilds through the same code path as the live handler.
 *
 * If the hydrated metadata reports `isRunning: true`, the hook also opens an
 * SSE stream to `/api/swarm/{id}/stream` via the shared `useSSE` hook. Because
 * `useSwarmHydration` owns the SSE stream for hydrated swarms, `App.tsx` must
 * exclude the hydrated swarm id from its existing `SwarmConnection` map —
 * otherwise two concurrent readers would race the single ChannelReader on the
 * backend and drop events.
 *
 * @param swarmId The swarm id to hydrate, or `null` to reset loading/error state.
 * @param options Dispatcher and the known-swarm-id set.
 * @returns Loading + error state so consumers can surface a spinner / toast.
 */
export function useSwarmHydration(
  swarmId: string | null,
  options: UseSwarmHydrationOptions,
): UseSwarmHydrationResult {
  const { knownSwarmIds, dispatch } = options;

  const { getToken } = useAuthToken();
  const getTokenRef = useRef(getToken);
  getTokenRef.current = getToken;

  const dispatchRef = useRef(dispatch);
  dispatchRef.current = dispatch;

  const [loading, setLoading] = useState<boolean>(false);
  const [error, setError] = useState<Error | null>(null);

  // The fetched metadata drives the SSE stream decision. `useSSE` must be
  // called unconditionally (rules of hooks), so we gate it via `enabled`.
  const [hydratedMeta, setHydratedMeta] = useState<SwarmMetadataResponse | null>(null);

  // Snapshot the "known at mount / id-change time" flag. Tests assert that the
  // hook is a no-op for swarms already in the store, so we capture this value
  // when swarmId changes and use it below to suppress the fetch sequence.
  const knownAtStartRef = useRef<boolean>(false);

  // Id of the swarm this hook currently owns the SSE stream for. Set in the
  // hydration effect when `swarmId` is unknown-at-start, cleared when
  // `swarmId` changes to a different id or becomes null. Deliberately NOT
  // reset when `knownSwarmIds` updates mid-hydration — that set changes every
  // time the reducer dispatches, and the hook's own `swarm.add` dispatch
  // would otherwise flip ownership off one render later, letting App.tsx's
  // SwarmConnection map re-subscribe and race the hook's SSE reader.
  const [ownedSwarmId, setOwnedSwarmId] = useState<string | null>(null);

  // Track every id we have already hydrated so repeated renders — or a
  // back-and-forth `null → id → null → id` transition — do not re-fetch.
  const hydratedIdsRef = useRef<Set<string>>(new Set<string>());

  // Re-hydration token. `forceRefresh` bumps this to re-fire the hydration
  // effect without needing `swarmId` to change. `knownSwarmIds` stays
  // excluded from the effect's deps (every reducer dispatch produces a new
  // Set, which would otherwise re-run hydration mid-flight), so `forceRefresh`
  // is the only legitimate path that re-triggers the fetch sequence.
  const [refreshToken, setRefreshToken] = useState(0);

  useEffect(() => {
    // Null id resets loading/error; nothing to fetch.
    if (!swarmId) {
      setLoading(false);
      setError(null);
      setHydratedMeta(null);
      knownAtStartRef.current = false;
      setOwnedSwarmId(null);
      return;
    }

    // Already known to the current tab's multi-swarm store: short-circuit.
    // Ownership is cleared — App.tsx owns SSE through its normal SwarmConnection
    // map for swarms that were already in the store.
    if (knownSwarmIds.has(swarmId)) {
      log.debug(`known in store, short-circuit`, swarmId);
      knownAtStartRef.current = true;
      setLoading(false);
      setError(null);
      setHydratedMeta(null);
      setOwnedSwarmId(null);
      return;
    }

    // Already hydrated in a previous pass of this hook instance: short-circuit.
    // NOTE: we do NOT touch ownedSwarmId here — the hook already set it on the
    // first pass for this id and is still the SSE owner.
    if (hydratedIdsRef.current.has(swarmId)) {
      log.debug(`already hydrated this instance, short-circuit`, swarmId);
      return;
    }

    log.info(`begin hydration`, swarmId);
    knownAtStartRef.current = false;
    // Take SSE ownership for this unknown id BEFORE dispatching swarm.add
    // (which lands in knownSwarmIds on the next render). Ownership is sticky
    // — it only releases when swarmId changes away, not when knownSwarmIds
    // updates underneath us.
    setOwnedSwarmId(swarmId);
    let cancelled = false;
    setLoading(true);
    setError(null);
    setHydratedMeta(null);

    (async (): Promise<void> => {
      try {
        const token = await getTokenRef.current();
        const headers: Record<string, string> = {};
        if (token) headers.Authorization = `Bearer ${token}`;

        // Five fetches in parallel. Any failure aborts the whole hydration —
        // partial state is worse than no state because the UI would render
        // a half-populated swarm that looks complete.
        const fetchStart = performance.now();
        const [metaRes, tasksRes, agentsRes, messagesRes, eventsRes] = await Promise.all([
          fetch(`${API_BASE}/api/swarm/${swarmId}`, { headers }),
          fetch(`${API_BASE}/api/swarm/${swarmId}/tasks`, { headers }),
          fetch(`${API_BASE}/api/swarm/${swarmId}/agents`, { headers }),
          fetch(`${API_BASE}/api/swarm/${swarmId}/messages`, { headers }),
          fetch(`${API_BASE}/api/swarm/${swarmId}/events?limit=100`, { headers }),
        ]);
        log.info(
          `5 fetches complete in ${Math.round(performance.now() - fetchStart)}ms`,
          { swarmId, meta: metaRes.status, tasks: tasksRes.status, agents: agentsRes.status, messages: messagesRes.status, events: eventsRes.status },
        );

        if (!metaRes.ok) {
          throw new Error(`Metadata fetch failed: ${metaRes.status}`);
        }
        if (!tasksRes.ok) {
          throw new Error(`Tasks fetch failed: ${tasksRes.status}`);
        }
        if (!agentsRes.ok) {
          throw new Error(`Agents fetch failed: ${agentsRes.status}`);
        }
        if (!messagesRes.ok) {
          throw new Error(`Messages fetch failed: ${messagesRes.status}`);
        }
        if (!eventsRes.ok) {
          throw new Error(`Events fetch failed: ${eventsRes.status}`);
        }

        const meta = (await metaRes.json()) as SwarmMetadataResponse;
        const tasksRaw = (await tasksRes.json()) as unknown;
        const agentsRaw = (await agentsRes.json()) as unknown;
        const messagesRaw = (await messagesRes.json()) as unknown;
        const eventsRaw = (await eventsRes.json()) as unknown;

        // Defensive: the five responses are expected to be arrays, but test
        // harnesses and proxy layers sometimes return `{}` or an error envelope
        // shape. Coerce non-array responses to empty arrays so downstream
        // `reduce` / `map` cannot throw and break the render tree.
        const tasks = Array.isArray(tasksRaw) ? tasksRaw : [];
        const agents = Array.isArray(agentsRaw) ? agentsRaw : [];
        const messages = Array.isArray(messagesRaw) ? messagesRaw : [];
        const rawEvents = Array.isArray(eventsRaw) ? (eventsRaw as Record<string, unknown>[]) : [];

        // Transform EventEntity objects (from /events endpoint) into SwarmEvent
        // objects the reducer can process. EventEntity has { eventType, dataJson };
        // SwarmEvent has { type, ...parsedData }. Events already in SwarmEvent
        // shape (with `type` and no `dataJson`) pass through unchanged.
        const backfilledEvents: SwarmEvent[] = rawEvents
          .map((raw) => {
            const eventType = (raw.type ?? raw.eventType ?? raw.event_type) as string | undefined;
            if (!eventType) return null;
            if (raw.type && !raw.dataJson && !raw.data_json) return raw as SwarmEvent;
            const dataJson = raw.dataJson ?? raw.data_json;
            let data: Record<string, unknown> = {};
            if (typeof dataJson === 'string') {
              try { data = JSON.parse(dataJson); } catch { /* skip malformed */ }
            } else if (typeof dataJson === 'object' && dataJson !== null) {
              data = dataJson as Record<string, unknown>;
            }
            return { type: eventType, ...data } as SwarmEvent;
          })
          .filter((e): e is SwarmEvent => e !== null);

        if (cancelled) {
          log.debug(`cancelled after fetch`, swarmId);
          return;
        }

        log.info(
          `hydrated meta`,
          { swarmId, phase: meta.phase, isRunning: meta.isRunning, tasks: tasks.length, agents: agents.length, messages: messages.length, backfillEvents: backfilledEvents.length },
        );

        // Dispatch order matters:
        // 1. `swarm.add` creates the slot so subsequent live events land here.
        dispatchRef.current({ type: 'swarm.add', swarmId });

        // 2. Seed a STATE_SNAPSHOT from the tasks, agents, and messages
        //    endpoints. Events alone are not enough because the events endpoint
        //    caps at 100 and the full data set may include older rows that fell
        //    out of the event window. STATE_SNAPSHOT is the same shape
        //    SwarmOrchestrator emits and is normalized by `swarmReducer`
        //    automatically (normalizeTask, normalizeAgent, normalizeMessage).
        dispatchRef.current({
          type: 'swarm.event',
          swarmId,
          event: {
            type: 'STATE_SNAPSHOT',
            snapshot: {
              phase: meta.phase,
              tasks,
              agents,
              messages,
            },
          },
        });

        // 3. Replay the last 100 events as a SINGLE `swarm.eventsSeed` so the
        //    per-swarm reducer rebuilds timeline-derived state through the same
        //    code path as live SSE events. Order is preserved — the backend
        //    returns events chronologically ascending (see Batch 3 repository
        //    change in `SwarmRepository.GetEventsAsync`).
        dispatchRef.current({ type: 'swarm.eventsSeed', swarmId, events: backfilledEvents });

        hydratedIdsRef.current.add(swarmId);
        setHydratedMeta(meta);
        setLoading(false);
        log.info(`hydration complete`, { swarmId, sseWillOpen: meta.isRunning });
      } catch (err) {
        if (cancelled) return;
        log.error(`hydration failed`, swarmId, err);
        setError(err instanceof Error ? err : new Error(String(err)));
        setLoading(false);
        // Release SSE ownership on the error path: if the hydration fetches
        // failed, no `swarm.add` was dispatched and the hook never opened its
        // own SSE stream, so keeping `ownedSwarmId` pointing at this id would
        // break the invariant "ownedSwarmId !== null => hook owns a live SSE
        // reader." Any future click that lands back on this id will re-fire
        // the effect and re-attempt hydration from scratch.
        setOwnedSwarmId(null);
      }
    })();

    return () => {
      cancelled = true;
    };
    // `knownSwarmIds` is intentionally excluded — it changes on every multi-
    // swarm reducer dispatch, which would otherwise re-run hydration mid-flight.
    // We read the current value via the closure and trust the short-circuit
    // check at the top of the effect to gate re-entry. `refreshToken` is the
    // legitimate re-fire signal, bumped only by `forceRefresh`.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [swarmId, refreshToken]);

  // SSE stream opens only after metadata is hydrated AND `isRunning: true`.
  // `enabled: false` makes `useSSE` a no-op, which keeps the hook call
  // unconditional (rules of hooks) while still giving us test observability:
  // unit tests mock `useSSE` and assert the `enabled` flag.
  //
  // Important interaction with App.tsx: when `enabled: true`, App must exclude
  // this swarm id from its `store.activeSwarmIds.map(...)` SwarmConnection
  // block, otherwise two readers would race the single backend ChannelReader
  // and drop events. The `useSwarmHydration` call site in App.tsx tracks the
  // hydration-owned id via the returned state and filters the map accordingly.
  const sseEnabled = hydratedMeta?.isRunning === true && swarmId !== null && !knownAtStartRef.current;
  useSSE({
    url: swarmId ? `${API_BASE}/api/swarm/${swarmId}/stream` : '',
    onEvent: (event) => {
      if (!swarmId) return;
      dispatchRef.current({ type: 'swarm.event', swarmId, event });
    },
    getToken,
    enabled: sseEnabled,
  });

  const forceRefresh = useCallback((id: string) => {
    // Clear the per-instance fetched-once guard and drop the id from the
    // store. Then bump the refresh token so the hydration effect re-fires
    // even though `swarmId` did not change — `knownSwarmIds` is intentionally
    // excluded from the effect's deps (it churns on every reducer dispatch),
    // so `refreshToken` is the legitimate re-fire signal.
    log.info(`forceRefresh`, id);
    hydratedIdsRef.current.delete(id);
    dispatchRef.current({ type: 'swarm.remove', swarmId: id });
    setRefreshToken((n) => n + 1);
  }, []);

  return {
    loading,
    error,
    ownedSwarmId,
    recommendation: hydratedMeta?.recommendation ?? null,
    forceRefresh,
  };
}
