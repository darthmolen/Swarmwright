export type TaskStatus = 'blocked' | 'pending' | 'in_progress' | 'completed' | 'failed' | 'awaiting_feedback';
export type AgentStatus = 'idle' | 'thinking' | 'working' | 'ready' | 'failed';

/**
 * Lowercase snake_case mirror of the backend `SwarmInstanceState` enum. The
 * backend serializes PascalCase (e.g. `AwaitingIntervention`); the frontend
 * normalizer at `hooks/useSwarmState.ts#normalizePhase` converts on
 * ingestion so every downstream consumer sees the snake form.
 *
 * `starting`, `qa`, and `suspended` were removed when the state machine
 * refactor landed: `starting` → `created`, `qa` dropped entirely, and
 * `suspended` split into `awaiting_intervention` / `needs_diagnosis`
 * depending on recovery-budget exhaustion.
 */
export type SwarmPhase =
  | 'created'
  | 'planning'
  | 'spawning'
  | 'executing'
  | 'awaiting_intervention'
  | 'needs_diagnosis'
  | 'awaiting_feedback'
  | 'synthesizing'
  | 'complete'
  | 'cancelled'
  | 'failed';

export interface Task {
  id: string;
  subject: string;
  description: string;
  worker_role: string;
  worker_name: string;
  status: TaskStatus;
  blocked_by: string[];
  result: string;
  swarm_id?: string;
  retry_count?: number;
}

export interface AgentInfo {
  name: string;
  role: string;
  display_name: string;
  status: AgentStatus;
  tasks_completed: number;
  swarm_id?: string;
}

export interface InboxMessage {
  sender: string;
  recipient: string;
  content: string;
  timestamp: string;
  swarm_id?: string;
}

export interface ActiveTool {
  toolCallId: string;
  toolName: string;
  agentName?: string;
  status: 'running' | 'complete' | 'failed';
  input?: string;
  output?: string;
  error?: string;
  startedAt?: number;
  completedAt?: number;
}

export interface SwarmState {
  phase: SwarmPhase | null;
  threadId: string | null;
  runId: string | null;
  tasks: Task[];
  agents: AgentInfo[];
  messages: InboxMessage[];
  leaderPlan: string;
  leaderReport: string;
  agentOutputs: Record<string, string>;
  activeTools: ActiveTool[];
  roundNumber: number;
  error: string | null;
  suspended?: { remaining_tasks: number; max_rounds: number; reason: string };
  /**
   * When the backend exposes a diagnose lock on the swarm, the admin app
   * renders a "X is diagnosing" badge and disables recovery buttons for
   * anyone who is not the holder.
   */
  lockedBy?: string | null;
  lockedAt?: string | null;
  currentTextMessage: { messageId: string; agentName: string; isLeaderReport?: boolean } | null;
}

export interface ChatMessage {
  id: string;
  role: 'user' | 'assistant';
  content: string;
}

export type ChatEntry =
  | { type: 'message'; message: ChatMessage }
  | { type: 'tool_group'; tools: ActiveTool[] }
  | { type: 'streaming'; content: string; id: string };

export interface ChatState {
  entries: ChatEntry[];
  streamingMessage: { id: string; content: string } | null;
  sessionStarting: boolean;
}

export interface ChatStore {
  chats: Record<string, ChatState>;
  activeSwarmId: string | null;
}

export interface FileInfo {
  name: string;
  path: string;
  size: number;
}

export interface SavedReport {
  swarmId: string;
  title: string;
  timestamp: number;
  report: string;
  phase: SwarmPhase;
}

// AG-UI standard + custom swarm event types
export type SwarmEventType =
  | 'RUN_STARTED' | 'RUN_FINISHED' | 'RUN_ERROR'
  | 'STEP_STARTED' | 'STEP_FINISHED'
  | 'TEXT_MESSAGE_START' | 'TEXT_MESSAGE_CONTENT' | 'TEXT_MESSAGE_END'
  | 'TOOL_CALL_START' | 'TOOL_CALL_ARGS' | 'TOOL_CALL_END' | 'TOOL_CALL_RESULT'
  | 'STATE_SNAPSHOT' | 'STATE_DELTA'
  | 'SWARM_CUSTOM';

export interface SwarmEvent {
  type: SwarmEventType;
  [key: string]: unknown;
}
