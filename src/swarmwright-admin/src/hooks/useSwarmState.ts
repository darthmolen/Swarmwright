import { useReducer } from 'react';
import * as jsonpatch from 'fast-json-patch';
import type { SwarmState, SwarmPhase, SwarmEvent, Task, TaskStatus, AgentInfo, ActiveTool, InboxMessage } from '../types/swarm';
import { devLog } from '../lib/devLog';

const log = devLog('state');

// ---------------------------------------------------------------------------
// Task normalization
// ---------------------------------------------------------------------------
// The backend serializes SwarmTask with Web-style camelCase property names
// (workerName, workerRole, blockedBy, swarmId) and the PascalCase enum form
// for status ("Pending", "InProgress", "Completed", ...) because
// SwarmJsonOptions.Default uses JsonSerializerDefaults.Web + JsonStringEnumConverter.
//
// The frontend Task type, TaskBoard column keys, and CSS selectors all
// expect snake_case property names and lowercase snake_case status values.
// If we feed a raw backend-shaped task object into state.tasks, every
// downstream consumer breaks silently — the task renders with undefined
// worker labels and filter(t => t.status === 'completed') returns zero rows.
//
// normalizeTask accepts either shape (backend camelCase/PascalCase OR
// frontend snake_case/lowercase) and always produces the frontend canonical
// form. Hand-written tests that already use snake_case pass through
// unchanged. Real STATE_SNAPSHOT / SWARM_TASK_CREATED / SWARM_TASK_UPDATED
// events from the backend are normalized on ingestion.
// ---------------------------------------------------------------------------
const VALID_TASK_STATUSES: readonly TaskStatus[] = [
  'blocked', 'pending', 'in_progress', 'completed', 'failed', 'awaiting_feedback',
];

const VALID_SWARM_PHASES: readonly SwarmPhase[] = [
  'created',
  'planning',
  'spawning',
  'executing',
  'awaiting_intervention',
  'needs_diagnosis',
  'awaiting_feedback',
  'synthesizing',
  'complete',
  'cancelled',
  'failed',
];

/**
 * Normalizes a phase string from the backend's PascalCase enum form
 * (`AwaitingIntervention`) to the frontend's lowercase snake_case form
 * (`awaiting_intervention`). Accepts already-normalized values unchanged
 * and falls back to `'created'` on unknowns so a stale event can't wedge
 * the reducer into an invalid state.
 */
export function normalizePhase(raw: unknown): SwarmPhase {
  if (typeof raw !== 'string') return 'created';
  const snake = raw.replace(/([a-z])([A-Z])/g, '$1_$2').toLowerCase();
  return (VALID_SWARM_PHASES as readonly string[]).includes(snake)
    ? (snake as SwarmPhase)
    : 'created';
}

function normalizeTaskStatus(raw: unknown): TaskStatus {
  if (typeof raw !== 'string') return 'pending';
  const snake = raw.replace(/([a-z])([A-Z])/g, '$1_$2').toLowerCase();
  return (VALID_TASK_STATUSES as readonly string[]).includes(snake)
    ? (snake as TaskStatus)
    : 'pending';
}

function normalizeTask(raw: unknown): Task {
  const t = (raw ?? {}) as Record<string, unknown>;
  return {
    id: String(t.id ?? ''),
    subject: String(t.subject ?? ''),
    description: String(t.description ?? ''),
    worker_role: String(t.worker_role ?? t.workerRole ?? ''),
    worker_name: String(t.worker_name ?? t.workerName ?? ''),
    status: normalizeTaskStatus(t.status),
    blocked_by: Array.isArray(t.blocked_by)
      ? (t.blocked_by as string[])
      : Array.isArray(t.blockedBy)
        ? (t.blockedBy as string[])
        : [],
    result: String(t.result ?? ''),
    swarm_id: (t.swarm_id ?? t.swarmId) as string | undefined,
  };
}

function normalizeAgent(raw: unknown): AgentInfo {
  const a = (raw ?? {}) as Record<string, unknown>;
  return {
    name: String(a.name ?? ''),
    role: String(a.role ?? ''),
    display_name: String(a.display_name ?? a.displayName ?? ''),
    status: (a.status as AgentInfo['status']) ?? 'idle',
    tasks_completed: Number(a.tasks_completed ?? a.tasksCompleted ?? 0),
    swarm_id: (a.swarm_id ?? a.swarmId) as string | undefined,
  };
}

function normalizeMessage(raw: unknown): InboxMessage {
  const m = (raw ?? {}) as Record<string, unknown>;
  return {
    sender: String(m.sender ?? ''),
    recipient: String(m.recipient ?? ''),
    content: String(m.content ?? ''),
    timestamp: String(m.timestamp ?? m.createdAt ?? new Date().toISOString()),
  };
}

export const initialState: SwarmState = {
  phase: null,
  threadId: null,
  runId: null,
  tasks: [],
  agents: [],
  messages: [],
  leaderPlan: '',
  leaderReport: '',
  agentOutputs: {},
  activeTools: [],
  roundNumber: 0,
  error: null,
  currentTextMessage: null,
};

export function swarmReducer(state: SwarmState, event: SwarmEvent): SwarmState {
  switch (event.type) {
    // -------------------------------------------------------------------
    // AG-UI Lifecycle events
    // -------------------------------------------------------------------
    case 'RUN_STARTED':
      return {
        ...state,
        phase: 'created',
        threadId: event.threadId as string,
        runId: event.runId as string,
      };

    case 'RUN_FINISHED':
      return { ...state, phase: 'complete' };

    case 'RUN_ERROR':
      return { ...state, error: event.message as string };

    // -------------------------------------------------------------------
    // AG-UI Step events (phase transitions)
    // -------------------------------------------------------------------
    case 'STEP_STARTED':
      return { ...state, phase: normalizePhase(event.stepName) };

    case 'STEP_FINISHED':
      return state;

    // -------------------------------------------------------------------
    // AG-UI State management
    // -------------------------------------------------------------------
    case 'STATE_SNAPSHOT': {
      const snap = event.snapshot as Record<string, unknown>;
      if (!snap) return state;
      return {
        ...state,
        phase: snap.phase ? normalizePhase(snap.phase) : state.phase,
        roundNumber: (snap.roundNumber as number) ?? state.roundNumber,
        lockedBy: (snap.lockedBy as string | null | undefined) ?? state.lockedBy,
        lockedAt: (snap.lockedAt as string | null | undefined) ?? state.lockedAt,
        tasks: Array.isArray(snap.tasks)
          ? (snap.tasks as unknown[]).map(normalizeTask)
          : state.tasks,
        agents: Array.isArray(snap.agents)
          ? (snap.agents as unknown[]).map(normalizeAgent)
          : state.agents,
        messages: Array.isArray(snap.messages)
          ? (snap.messages as unknown[]).map(normalizeMessage)
          : state.messages,
        leaderPlan: (snap.leaderPlan as string) ?? state.leaderPlan,
        leaderReport: (snap.leaderReport as string) ?? state.leaderReport,
      };
    }

    case 'STATE_DELTA': {
      const ops = event.delta as jsonpatch.Operation[];
      if (!ops || !Array.isArray(ops)) return state;
      const patched = jsonpatch.applyPatch(structuredClone(state), ops, false, false);
      // jsonpatch writes raw values; the backend emits /phase as the
      // canonical state-machine name ("AwaitingIntervention"), but the
      // rest of the UI branches on snake_case. Re-normalize here to
      // keep SwarmStatusWindow's recovery branch reachable regardless
      // of how the backend labels the phase.
      return {
        ...patched.newDocument,
        phase: normalizePhase(patched.newDocument.phase),
      };
    }

    // -------------------------------------------------------------------
    // AG-UI Text message events
    // -------------------------------------------------------------------
    case 'TEXT_MESSAGE_START': {
      const agentName = (event.agentName as string) ?? 'unknown';
      const isLeaderReport = state.phase === 'synthesizing' && agentName === 'leader';
      return {
        ...state,
        agentOutputs: {
          ...state.agentOutputs,
          [agentName]: state.agentOutputs[agentName] ?? '',
        },
        currentTextMessage: {
          messageId: event.messageId as string,
          agentName,
          isLeaderReport,
        },
      };
    }

    case 'TEXT_MESSAGE_CONTENT': {
      const msg = state.currentTextMessage;
      if (!msg) return state;
      const delta = event.delta as string;
      const updatedOutputs = {
        ...state.agentOutputs,
        [msg.agentName]: (state.agentOutputs[msg.agentName] ?? '') + delta,
      };
      const updatedReport = msg.isLeaderReport
        ? state.leaderReport + delta
        : state.leaderReport;
      return {
        ...state,
        agentOutputs: updatedOutputs,
        leaderReport: updatedReport,
      };
    }

    case 'TEXT_MESSAGE_END':
      return { ...state, currentTextMessage: null };

    // -------------------------------------------------------------------
    // AG-UI Tool call events
    // -------------------------------------------------------------------
    case 'TOOL_CALL_START': {
      const tool: ActiveTool = {
        toolCallId: event.toolCallId as string,
        toolName: event.toolCallName as string,
        agentName: event.agentName as string | undefined,
        status: 'running',
        startedAt: Date.now(),
      };
      return { ...state, activeTools: [...state.activeTools, tool] };
    }

    case 'TOOL_CALL_ARGS': {
      const callId = event.toolCallId as string;
      const delta = event.delta as string;
      return {
        ...state,
        activeTools: state.activeTools.map((t) =>
          t.toolCallId === callId
            ? { ...t, input: (t.input ?? '') + delta }
            : t,
        ),
      };
    }

    case 'TOOL_CALL_END':
      return state; // args complete, no status change needed

    case 'TOOL_CALL_RESULT': {
      const callId = event.toolCallId as string;
      const content = event.content as string;
      return {
        ...state,
        activeTools: state.activeTools.map((t) =>
          t.toolCallId === callId
            ? { ...t, status: 'complete' as const, output: content, completedAt: Date.now() }
            : t,
        ),
      };
    }

    // -------------------------------------------------------------------
    // Custom swarm domain events
    // -------------------------------------------------------------------
    case 'SWARM_CUSTOM':
      return handleSwarmCustom(state, event);

    default:
      return state;
  }
}

function handleSwarmCustom(state: SwarmState, event: SwarmEvent): SwarmState {
  const name = event.name as string;
  const value = event.value as Record<string, unknown>;

  switch (name) {
    case 'SWARM_TASK_CREATED': {
      const task = normalizeTask(value);
      // Dedup: STATE_SNAPSHOT replay followed by individual events can re-emit
      // the same task id. Skip if already present to avoid duplicate rows.
      if (state.tasks.some(t => t.id === task.id)) return state;
      return { ...state, tasks: [...state.tasks, task] };
    }

    case 'SWARM_TASK_UPDATED': {
      const taskId = value.taskId as string;
      const status = normalizeTaskStatus(value.status);
      return {
        ...state,
        tasks: state.tasks.map((t) =>
          t.id === taskId ? { ...t, status } : t,
        ),
      };
    }

    case 'SWARM_AGENT_SPAWNED': {
      const agent = value as unknown as AgentInfo;
      // Dedup: STATE_SNAPSHOT replay followed by individual events can re-emit
      // the same agent name. Skip if already present to avoid duplicate entries.
      if (state.agents.some(a => a.name === agent.name)) return state;
      return { ...state, agents: [...state.agents, agent] };
    }

    case 'SWARM_AGENT_STATUS': {
      const agentName = value.agentName as string;
      const status = value.status as AgentInfo['status'];
      return {
        ...state,
        agents: state.agents.map((a) =>
          a.name === agentName ? { ...a, status } : a,
        ),
      };
    }

    case 'SWARM_INBOX_MESSAGE': {
      const msg: InboxMessage = {
        sender: value.sender as string,
        recipient: value.recipient as string,
        content: value.content as string,
        timestamp: new Date().toISOString(),
      };
      return { ...state, messages: [...state.messages, msg] };
    }

    default:
      return state;
  }
}

export function isThinking(phase: SwarmState['phase']): boolean {
  if (phase === null) return false;
  // Terminal states and human-wait states ({awaiting_intervention,
  // needs_diagnosis, awaiting_feedback}) are not "thinking" — they are
  // passive states waiting on user input or already done.
  return phase !== 'complete'
    && phase !== 'cancelled'
    && phase !== 'failed'
    && phase !== 'awaiting_intervention'
    && phase !== 'needs_diagnosis'
    && phase !== 'awaiting_feedback';
}

export function shouldShowReportView(
  reportSwarmId: string | null,
  currentReport: string | null,
  phase: SwarmState['phase'],
): boolean {
  if (!reportSwarmId) return false;
  if (currentReport) return true;
  return phase === 'complete' || phase === 'failed' || phase === 'cancelled';
}

export interface DashboardData {
  tasks: Task[];
  agents: AgentInfo[];
  messages: InboxMessage[];
  agentOutputs: Record<string, string>;
  activeTools: ActiveTool[];
}

export function getDashboardData(
  store: MultiSwarmStore,
  dashboardSwarmId: string | null,
): DashboardData {
  const empty: DashboardData = { tasks: [], agents: [], messages: [], agentOutputs: {}, activeTools: [] };
  if (!dashboardSwarmId) return empty;
  const swarm = store.swarms[dashboardSwarmId];
  if (!swarm) return empty;
  return {
    tasks: swarm.tasks,
    agents: swarm.agents,
    messages: swarm.messages,
    agentOutputs: swarm.agentOutputs,
    activeTools: swarm.activeTools,
  };
}

export function useSwarmState() {
  const [state, dispatch] = useReducer(swarmReducer, initialState);
  return { state, dispatch };
}

// ---------------------------------------------------------------------------
// Multi-swarm state management
// ---------------------------------------------------------------------------

const MAX_SWARMS = 10;

export interface MultiSwarmStore {
  swarms: Record<string, SwarmState>;
  activeSwarmIds: string[];
  completedSwarmIds: string[];
}

export type MultiSwarmAction =
  | { type: 'swarm.add'; swarmId: string }
  | { type: 'swarm.remove'; swarmId: string }
  | { type: 'swarm.event'; swarmId: string; event: SwarmEvent }
  /**
   * Replay a batch of backfilled events through the existing per-swarm reducer
   * exactly as if each event had arrived live via SSE. Used by `useSwarmHydration`
   * to rebuild derived state (tasks, agents, phase, messages) when the current
   * tab connects to a swarm it never observed live.
   *
   * The multi-swarm reducer dispatches each entry through `swarmReducer` in order
   * — there is no separate "backfill mode" that could drift from the live path.
   * The swarm must already exist in the store (via `swarm.add`); if it does not,
   * the action is a no-op.
   */
  | { type: 'swarm.eventsSeed'; swarmId: string; events: SwarmEvent[] };

/** Dispatch signature for the multi-swarm reducer, exported for consumer typing. */
export type SwarmDispatch = (action: MultiSwarmAction) => void;

export const initialMultiSwarmState: MultiSwarmStore = {
  swarms: {},
  activeSwarmIds: [],
  completedSwarmIds: [],
};

export function multiSwarmReducer(
  state: MultiSwarmStore,
  action: MultiSwarmAction,
): MultiSwarmStore {
  switch (action.type) {
    case 'swarm.add': {
      log.info(`swarm.add`, action.swarmId);
      let next = {
        ...state,
        swarms: { ...state.swarms, [action.swarmId]: initialState },
        activeSwarmIds: [...state.activeSwarmIds, action.swarmId],
      };
      // Hard cap: evict oldest completed if over limit
      const total = next.activeSwarmIds.length + next.completedSwarmIds.length;
      if (total > MAX_SWARMS && next.completedSwarmIds.length > 0) {
        const evictId = next.completedSwarmIds[0];
        const { [evictId]: _, ...rest } = next.swarms;
        log.warn(`evicting oldest completed swarm`, evictId);
        next = {
          ...next,
          swarms: rest,
          completedSwarmIds: next.completedSwarmIds.slice(1),
        };
      }
      return next;
    }

    case 'swarm.remove': {
      log.info(`swarm.remove`, action.swarmId);
      const { [action.swarmId]: _, ...rest } = state.swarms;
      return {
        swarms: rest,
        activeSwarmIds: state.activeSwarmIds.filter((id) => id !== action.swarmId),
        completedSwarmIds: state.completedSwarmIds.filter((id) => id !== action.swarmId),
      };
    }

    case 'swarm.event': {
      const current = state.swarms[action.swarmId] ?? initialState;
      const updated = swarmReducer(current, action.event);

      if (updated.phase !== current.phase) {
        log.info(
          `phase transition ${current.phase} -> ${updated.phase}`,
          { swarmId: action.swarmId, trigger: action.event.type },
        );
      } else {
        log.debug(`swarm.event ${action.event.type}`, { swarmId: action.swarmId });
      }

      let { activeSwarmIds, completedSwarmIds } = state;

      // Auto-transition: active → completed when phase is complete/cancelled/failed
      if (
        (updated.phase === 'complete' || updated.phase === 'cancelled' || updated.phase === 'failed') &&
        activeSwarmIds.includes(action.swarmId)
      ) {
        log.info(`auto-move ${action.swarmId} from active -> completed (phase=${updated.phase})`);
        activeSwarmIds = activeSwarmIds.filter((id) => id !== action.swarmId);
        completedSwarmIds = [...completedSwarmIds, action.swarmId];
      }

      return {
        swarms: { ...state.swarms, [action.swarmId]: updated },
        activeSwarmIds,
        completedSwarmIds,
      };
    }

    case 'swarm.eventsSeed': {
      // Replay each event through the per-swarm reducer in order, as if it had
      // arrived live via SSE. Guarantees derived state (tasks, agents, phase,
      // messages) is rebuilt by the same code path that handles live events —
      // no bifurcation between backfill and live. If the swarm does not yet
      // exist in the store, callers must dispatch `swarm.add` first.
      const existing = state.swarms[action.swarmId];
      if (!existing) {
        log.warn(`swarm.eventsSeed ignored — swarm not in store`, action.swarmId);
        return state;
      }
      log.info(`swarm.eventsSeed`, { swarmId: action.swarmId, count: action.events.length });
      const rebuilt = action.events.reduce(swarmReducer, existing);
      if (rebuilt.phase !== existing.phase) {
        log.info(`phase after seed: ${existing.phase} -> ${rebuilt.phase}`, action.swarmId);
      }
      return { ...state, swarms: { ...state.swarms, [action.swarmId]: rebuilt } };
    }

    default:
      return state;
  }
}
