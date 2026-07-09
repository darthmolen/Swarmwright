import { useState, useCallback } from 'react';
import toast from 'react-hot-toast';
import type { SwarmPhase, Task, AgentInfo } from '../types/swarm';
import type { SwarmContinueRecommendation } from '../hooks/useSwarmHydration';
import { useAuthToken } from '../auth/useAuthToken';
import { devLog } from '../lib/devLog';

const API_BASE = import.meta.env.VITE_API_URL ?? '';
const apiLog = devLog('api');

interface SwarmStatusWindowProps {
  swarmId: string;
  phase: SwarmPhase;
  tasks: Task[];
  agents: AgentInfo[];
  roundNumber: number;
  suspended?: { remaining_tasks: number; max_rounds: number; reason: string };
  /** Identity holding the diagnose lock, when one is set. */
  lockedBy?: string | null;
  /** Identity of the viewer — compared against lockedBy to decide mutator availability. */
  currentActor?: string | null;
  /**
   * Server-computed recovery recommendation from <c>GET /api/swarm/{id}</c>.
   * Present when the swarm is in an actionable non-terminal state
   * (AwaitingIntervention / NeedsDiagnosis); <c>null</c> otherwise. Drives the
   * recovery-button gates (<see cref="validActions"/>) so the SPA follows the
   * backend's acceptance logic instead of re-deriving it client-side.
   */
  recommendation?: SwarmContinueRecommendation | null;
  /**
   * Maximum Continue retries per Failed task. Retained for backwards compat
   * with callers that have not migrated to passing <c>recommendation</c>;
   * when neither is supplied, recovery buttons remain disabled.
   * @deprecated Prefer <c>recommendation</c>.
   */
  maxTaskRetries?: number;
  onGoToReport: () => void;
  onClose: () => void;
  onDiagnose?: () => void;
  /**
   * Fired after a successful POST /mark-as-awaiting-intervention (204).
   * Callers should invalidate the hydration cache for this swarm and
   * re-fetch metadata — a Failed swarm has no active SSE stream, so
   * nothing else will surface the AwaitingIntervention phase to the UI.
   */
  onManualRecoverSuccess?: (swarmId: string) => void;
  /**
   * Fired after a successful recovery action — POST /continue, /smart-continue,
   * /skip, or /cancel — when the server returns 204. Callers should invalidate
   * the hydration cache and re-fetch metadata. Recovery actions flip the swarm
   * from a non-running phase (AwaitingIntervention / NeedsDiagnosis) to a
   * running one (Executing / Synthesizing / Cancelled). <c>useSwarmHydration</c>
   * only opens SSE when the hydration snapshot reports <c>isRunning=true</c>,
   * so without this post-success hook the SPA captures the pre-click snapshot
   * (isRunning=false), never opens SSE, and the UI freezes at the stale state
   * while the backend drives the swarm to completion.
   */
  onRecoverySuccess?: (swarmId: string) => void;
}

const PHASE_COLORS: Record<string, string> = {
  created: '#6b7280',
  planning: '#8b5cf6',
  spawning: '#f59e0b',
  executing: '#3b82f6',
  awaiting_intervention: '#f59e0b',
  needs_diagnosis: '#ef4444',
  awaiting_feedback: '#06b6d4',
  synthesizing: '#06b6d4',
  complete: '#22c55e',
  cancelled: '#6b7280',
  failed: '#ef4444',
};

const PHASE_LABELS: Record<string, string> = {
  created: 'Created',
  planning: 'Planning',
  spawning: 'Spawning',
  executing: 'Executing',
  awaiting_intervention: 'Awaiting Intervention',
  needs_diagnosis: 'Needs Diagnosis',
  awaiting_feedback: 'Awaiting Feedback',
  synthesizing: 'Synthesizing',
  complete: 'Complete',
  cancelled: 'Cancelled',
  failed: 'Failed',
};

type ActionKind = 'continue' | 'smart-continue' | 'skip' | 'cancel' | 'diagnose' | 'manual-recover';

/**
 * Translate a non-OK backend response into a user-facing message. Matches the
 * shape produced by `SwarmInterventionHandler` (409/410/423 with `{ code, ... }`).
 */
async function interpretSwarmError(response: Response): Promise<string> {
  let body: { code?: string; message?: string; from?: string; to?: string; lockedBy?: string; state?: string } = {};
  try { body = await response.json(); } catch { /* empty body */ }
  switch (response.status) {
    case 409:
      if (body.code === 'no_retry_budget') return 'No retry budget remaining. Try Smart Continue.';
      if (body.code === 'invalid_transition' && body.from && body.to) {
        return `Cannot transition from ${body.from} to ${body.to}.`;
      }
      return body.message ?? 'Action rejected by the swarm state machine.';
    case 410:
      return `This swarm is ${body.state ?? 'no longer active'}.`;
    case 423:
      return body.lockedBy
        ? `${body.lockedBy} is diagnosing this swarm — wait for them or steal the lock.`
        : 'Swarm is locked by another user.';
    case 403:
      return 'You do not hold the diagnose lock on this swarm.';
    case 404:
      return 'Swarm not found.';
    default:
      return `Request failed: ${response.status}`;
  }
}

export function SwarmStatusWindow({
  swarmId,
  phase,
  tasks,
  agents,
  roundNumber,
  suspended,
  lockedBy,
  currentActor,
  recommendation,
  onGoToReport,
  onClose,
  onDiagnose,
  onManualRecoverSuccess,
  onRecoverySuccess,
}: SwarmStatusWindowProps) {
  const { getToken } = useAuthToken();
  const [loading, setLoading] = useState<ActionKind | null>(null);
  const [error, setError] = useState<string | null>(null);

  const getAuthHeaders = useCallback(async (): Promise<Record<string, string>> => {
    const token = await getToken();
    return token ? { Authorization: `Bearer ${token}` } : {};
  }, [getToken]);

  const completedCount = tasks.filter((t) => t.status === 'completed').length;
  const totalCount = tasks.length;
  const progressPct = totalCount > 0 ? Math.round((completedCount / totalCount) * 100) : 0;
  const phaseColor = PHASE_COLORS[phase] ?? '#6b7280';
  const phaseLabel = PHASE_LABELS[phase] ?? phase;

  const isRunning =
    phase === 'executing' ||
    phase === 'planning' ||
    phase === 'spawning' ||
    phase === 'synthesizing' ||
    phase === 'created';

  const isNeedsDiagnosis = phase === 'needs_diagnosis';
  const isRecoveryState = isNeedsDiagnosis || phase === 'awaiting_intervention';

  // Recovery-button gates are driven by the server-computed recommendation,
  // NOT by client-side heuristics on task shape. The backend returns
  // `recommendation.validActions` = the list of actions the intervention
  // handler will accept for the current state; the SPA faithfully mirrors
  // that decision. When `recommendation` is null (swarm not in a recovery
  // state) every gate defaults to false. This replaces the old
  // `hasContinueBudget` client heuristic which drifted from the handler
  // (see the orphan-InProgress defense-in-depth plan, Layer 3).
  const canContinue = recommendation?.validActions?.includes('continue') ?? false;
  const canSmartContinue = recommendation?.validActions?.includes('smart-continue') ?? false;
  const canForceSynthesis = recommendation?.validActions?.includes('force-synthesis') ?? false;
  const canCancel = recommendation?.validActions?.includes('cancel') ?? false;

  const lockedByOther =
    typeof lockedBy === 'string'
      && lockedBy.length > 0
      && lockedBy !== currentActor;

  const mutatorsDisabled = lockedByOther || loading !== null;

  async function callEndpoint(kind: ActionKind, path: string, method: 'POST' | 'DELETE' = 'POST') {
    setLoading(kind);
    setError(null);
    const url = `${API_BASE}/api/swarm/${swarmId}${path}`;
    const t0 = performance.now();
    apiLog.info(`-> ${method} ${path}`, { swarmId, kind });
    try {
      const headers = await getAuthHeaders();
      const res = await fetch(url, { method, headers });
      const ms = Math.round(performance.now() - t0);
      if (!res.ok) {
        apiLog.warn(`<- ${method} ${path} ${res.status} (${ms}ms)`, { swarmId, kind });
        const msg = await interpretSwarmError(res);
        toast.error(msg);
        setError(msg);
        return false;
      }
      apiLog.info(`<- ${method} ${path} ${res.status} (${ms}ms)`, { swarmId, kind });
      return true;
    } catch (err) {
      apiLog.error(`<- ${method} ${path} FAILED`, err);
      const msg = err instanceof Error ? err.message : 'Request failed';
      toast.error(msg);
      setError(msg);
      return false;
    } finally {
      setLoading(null);
    }
  }

  // On a successful recovery action the backend flips isRunning false→true
  // (or drives the swarm to a terminal phase). useSwarmHydration only opens
  // SSE when its metadata snapshot reports isRunning=true; if hydration ran
  // while the swarm was AwaitingIntervention/NeedsDiagnosis the snapshot is
  // frozen at isRunning=false and no SSE is open. onRecoverySuccess signals
  // the parent to invalidate the hydration cache and re-fetch so SSE opens.
  const handleContinue = async () => {
    const ok = await callEndpoint('continue', '/continue');
    if (ok) onRecoverySuccess?.(swarmId);
  };
  const handleSmartContinue = async () => {
    const ok = await callEndpoint('smart-continue', '/smart-continue');
    if (ok) onRecoverySuccess?.(swarmId);
  };
  const handleSkip = async () => {
    const ok = await callEndpoint('skip', '/skip');
    if (ok) {
      onRecoverySuccess?.(swarmId);
      onGoToReport();
    }
  };
  const handleCancel = async () => {
    const ok = await callEndpoint('cancel', '/cancel');
    if (ok) onRecoverySuccess?.(swarmId);
  };
  const handleManualRecover = async () => {
    const ok = await callEndpoint('manual-recover', '/mark-as-awaiting-intervention');
    if (ok) {
      onManualRecoverSuccess?.(swarmId);
    }
  };

  const handleDiagnose = async () => {
    setLoading('diagnose');
    setError(null);
    try {
      const headers = await getAuthHeaders();
      const res = await fetch(`${API_BASE}/api/swarm/${swarmId}/lock`, { method: 'POST', headers });
      if (!res.ok) {
        const msg = await interpretSwarmError(res);
        toast.error(msg);
        setError(msg);
        return;
      }
      onDiagnose?.();
    } catch (err) {
      const msg = err instanceof Error ? err.message : 'Diagnose failed';
      toast.error(msg);
      setError(msg);
    } finally {
      setLoading(null);
    }
  };

  // Running state
  if (isRunning) {
    return (
      <div className="swarm-status-window swarm-status-window--running">
        <div className="swarm-status-header">
          <span className="swarm-status-phase" style={{ background: phaseColor }}>
            {phaseLabel}
          </span>
          <span className="swarm-status-round">Round {roundNumber}</span>
          <span className="swarm-status-agents">{agents.length} agent{agents.length !== 1 ? 's' : ''}</span>
        </div>
        <div className="swarm-status-progress">
          <div className="swarm-status-progress-label">
            {completedCount} / {totalCount} tasks completed
          </div>
          <div className="swarm-status-progress-bar">
            <div
              className="swarm-status-progress-fill"
              style={{ width: `${progressPct}%`, background: phaseColor }}
            />
          </div>
        </div>
      </div>
    );
  }

  // Recovery state: AwaitingIntervention OR NeedsDiagnosis — full 4-button surface.
  if (isRecoveryState) {
    return (
      <div className="swarm-status-window swarm-status-window--suspended">
        <div className="swarm-status-banner swarm-status-banner--warning">
          {isNeedsDiagnosis
            ? 'Needs diagnosis — retry budget exhausted.'
            : 'Execution paused — awaiting intervention.'}
          {suspended
            ? ` \u2014 ${suspended.remaining_tasks} tasks remain after ${suspended.max_rounds} rounds`
            : ''}
        </div>
        {lockedByOther && (
          <div className="swarm-status-lock-badge" data-testid="swarm-lock-badge">
            {`${lockedBy} is diagnosing this swarm`}
          </div>
        )}
        <div className="swarm-status-actions">
          <button
            type="button"
            className="swarm-status-btn swarm-status-btn--continue"
            onClick={handleContinue}
            disabled={mutatorsDisabled || !canContinue}
            title={!canContinue ? 'Continue is not applicable in the current swarm state. See the recommended action.' : undefined}
          >
            {loading === 'continue' ? 'Resuming...' : 'Continue'}
          </button>
          <button
            type="button"
            className="swarm-status-btn swarm-status-btn--smart-continue"
            onClick={handleSmartContinue}
            disabled={mutatorsDisabled || !canSmartContinue}
          >
            {loading === 'smart-continue' ? 'Thinking...' : 'Smart Continue'}
          </button>
          <button
            type="button"
            className="swarm-status-btn swarm-status-btn--diagnose"
            onClick={handleDiagnose}
            disabled={mutatorsDisabled}
          >
            {loading === 'diagnose' ? 'Locking...' : 'Diagnose'}
          </button>
          <button
            type="button"
            className="swarm-status-btn swarm-status-btn--skip"
            onClick={handleSkip}
            disabled={mutatorsDisabled || !canForceSynthesis}
          >
            {loading === 'skip' ? 'Skipping...' : 'Force Synthesis'}
          </button>
          <button
            type="button"
            className="swarm-status-btn swarm-status-btn--cancel"
            onClick={handleCancel}
            disabled={mutatorsDisabled || !canCancel}
          >
            {loading === 'cancel' ? 'Cancelling...' : 'Cancel'}
          </button>
        </div>
        {error && <p className="error-text">{error}</p>}
      </div>
    );
  }

  // Awaiting feedback — passive until the user answers; no mutator buttons here.
  if (phase === 'awaiting_feedback') {
    return (
      <div className="swarm-status-window swarm-status-window--feedback">
        <div className="swarm-status-banner swarm-status-banner--info">
          An agent has a question for you.
        </div>
      </div>
    );
  }

  // Complete state
  if (phase === 'complete') {
    return (
      <div className="swarm-status-window swarm-status-window--complete">
        <div className="swarm-status-banner swarm-status-banner--success">
          All tasks completed
        </div>
        <div className="swarm-status-actions">
          <button
            type="button"
            className="swarm-status-btn swarm-status-btn--report"
            onClick={onGoToReport}
          >
            Go to Report
          </button>
          <button
            type="button"
            className="swarm-status-btn swarm-status-btn--close"
            onClick={onClose}
          >
            Close
          </button>
        </div>
      </div>
    );
  }

  // Failed — Manual Recover flips the swarm to AwaitingIntervention so
  // the operator can choose a recovery strategy. It is a pure state
  // transition; the orchestrator stays asleep until the operator picks a
  // recovery action. Transient errors (e.g. infrastructure faults caught
  // by the orchestrator's catch-all) should never strand viable work.
  if (phase === 'failed') {
    return (
      <div className="swarm-status-window swarm-status-window--failed">
        <div className="swarm-status-banner swarm-status-banner--error">
          Run failed. Manual Recover flips the swarm to Awaiting
          Intervention so you can pick Continue, Smart Continue, Force
          Synthesis, or Cancel.
        </div>
        <div className="swarm-status-actions">
          <button
            type="button"
            className="swarm-status-btn swarm-status-btn--manual-recover"
            onClick={handleManualRecover}
            disabled={loading === 'manual-recover'}
          >
            {loading === 'manual-recover' ? 'Marking…' : 'Manual Recover'}
          </button>
        </div>
        {error && <p className="error-text">{error}</p>}
      </div>
    );
  }

  // Fallback for other phases (cancelled) — show phase badge only
  return (
    <div className="swarm-status-window">
      <div className="swarm-status-header">
        <span className="swarm-status-phase" style={{ background: phaseColor }}>
          {phaseLabel}
        </span>
      </div>
    </div>
  );
}
