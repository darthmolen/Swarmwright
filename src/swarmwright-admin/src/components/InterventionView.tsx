import { useState, useRef, useCallback } from 'react';
import toast from 'react-hot-toast';
import { TemplateEditorPanel } from './TemplateEditorPanel';
import { ChatPanel } from './ChatPanel';
import { ResizableLayout } from './ResizableLayout';
import type { Task } from '../types/swarm';
import { useAuthToken } from '../auth/useAuthToken';
import { devLog } from '../lib/devLog';

const API_BASE = import.meta.env.VITE_API_URL ?? '';
const apiLog = devLog('api');

function statusColor(status: string): string {
  switch (status) {
    case 'failed':
      return '#ef4444';
    default:
      return '#6b7280';
  }
}

function statusLabel(status: string): string {
  switch (status) {
    case 'failed':
      return 'FAILED';
    default:
      return status.toUpperCase();
  }
}

type RecoveryAction = 'continue' | 'smart-continue' | 'skip' | 'cancel';

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

interface InterventionViewProps {
  swarmId: string;
  templateKey: string;
  tasks: Task[];
  selectedTaskId: string;
  onSelectTask: (taskId: string) => void;
  agentOutputs: Record<string, string>;
  onBack: () => void;
  onSaveAndRetry: () => void;
}

export function InterventionView({
  swarmId,
  templateKey,
  tasks,
  selectedTaskId,
  onSelectTask,
  agentOutputs,
  onBack,
  onSaveAndRetry,
}: InterventionViewProps) {
  const [hasModifications, setHasModifications] = useState(false);
  const [recoveryLoading, setRecoveryLoading] = useState<RecoveryAction | null>(null);
  const { getToken } = useAuthToken();

  // Releasing the diagnose lock on back-navigation lets another admin pick
  // up the session cleanly. We always navigate (via onBack) even when the
  // release fails — the stale-timeout will reclaim it server-side.
  const handleBack = useCallback(async () => {
    try {
      const token = await getToken();
      const headers: Record<string, string> = token ? { Authorization: `Bearer ${token}` } : {};
      const res = await fetch(`${API_BASE}/api/swarm/${swarmId}/lock`, {
        method: 'DELETE',
        headers,
      });
      if (!res.ok && res.status !== 204) {
        toast.error('Lock release failed; it will expire via the stale timeout.');
      }
    } catch {
      toast.error('Lock release failed; it will expire via the stale timeout.');
    } finally {
      onBack();
    }
  }, [getToken, onBack, swarmId]);

  // Recovery actions: POST the endpoint, release the diagnose lock on
  // success (the backend handler clears it atomically when the caller is
  // the holder), and navigate back to the dashboard. Failures surface as
  // toasts and leave the user on the intervention view so they can pick
  // a different action.
  const callRecoveryEndpoint = useCallback(
    async (action: RecoveryAction, path: string) => {
      setRecoveryLoading(action);
      const t0 = performance.now();
      apiLog.info(`-> POST ${path}`, { swarmId, action });
      try {
        const token = await getToken();
        const headers: Record<string, string> = token ? { Authorization: `Bearer ${token}` } : {};
        const res = await fetch(`${API_BASE}/api/swarm/${swarmId}${path}`, {
          method: 'POST',
          headers,
        });
        const ms = Math.round(performance.now() - t0);
        if (!res.ok) {
          apiLog.warn(`<- POST ${path} ${res.status} (${ms}ms)`, { swarmId, action });
          const msg = await interpretSwarmError(res);
          toast.error(msg);
          return;
        }
        apiLog.info(`<- POST ${path} ${res.status} (${ms}ms)`, { swarmId, action });
        onBack();
      } catch (err) {
        apiLog.error(`<- POST ${path} FAILED`, err);
        const msg = err instanceof Error ? err.message : 'Request failed';
        toast.error(msg);
      } finally {
        setRecoveryLoading(null);
      }
    },
    [getToken, onBack, swarmId],
  );

  const handleContinue = useCallback(
    () => callRecoveryEndpoint('continue', '/continue'),
    [callRecoveryEndpoint],
  );
  const handleSmartContinue = useCallback(
    () => callRecoveryEndpoint('smart-continue', '/smart-continue'),
    [callRecoveryEndpoint],
  );
  const handleSkip = useCallback(
    () => callRecoveryEndpoint('skip', '/skip'),
    [callRecoveryEndpoint],
  );
  const handleCancel = useCallback(
    () => callRecoveryEndpoint('cancel', '/cancel'),
    [callRecoveryEndpoint],
  );

  const selectedTask = tasks.find((t) => t.id === selectedTaskId) ?? tasks[0];
  const taskOutput = selectedTask
    ? agentOutputs[selectedTask.worker_name] ?? ''
    : '';

  // Extract error lines from task output for highlighting
  const outputLines = taskOutput.split('\n');

  // Scroll ref for task logs
  const logsRef = useRef<HTMLDivElement>(null);

  return (
    <div className="intervention-view">
      {/* Header */}
      <header className="intervention-header">
        <button type="button" className="back-button" onClick={handleBack}>
          &larr; Dashboard
        </button>
        <span className="intervention-swarm-label">
          Intervention -- {swarmId.slice(0, 8)}
        </span>
        <div className="intervention-task-pills">
          {tasks.map((task) => (
            <button
              key={task.id}
              className={`intervention-pill ${task.id === selectedTaskId ? 'intervention-pill--active' : ''}`}
              style={
                task.id === selectedTaskId
                  ? { borderColor: '#3b82f6', background: '#1e3a5f' }
                  : { borderColor: statusColor(task.status) }
              }
              onClick={() => onSelectTask(task.id)}
              title={`${task.subject} (${task.status})`}
            >
              <span
                className="intervention-pill__dot"
                style={{ background: statusColor(task.status) }}
              />
              <span className="intervention-pill__label">
                {task.worker_name}
              </span>
              <span className="intervention-pill__status">
                {statusLabel(task.status)}
              </span>
            </button>
          ))}
        </div>
      </header>

      {/* Body: two-column layout */}
      <div className="intervention-body">
        {/* Left column: Template editor panel (45%) */}
        <div className="intervention-left">
          {selectedTask && (
            <TemplateEditorPanel
              key={`${templateKey}-${selectedTask.worker_name}`}
              templateKey={templateKey}
              workerName={selectedTask.worker_name}
              onModified={setHasModifications}
            />
          )}
        </div>

        {/* Right column: Logs + Chat (55%) */}
        <div className="intervention-right">
          <ResizableLayout
            direction="vertical"
            defaultLeftPercent={55}
            left={
              <div className="intervention-logs" ref={logsRef}>
                <div className="intervention-logs__header">
                  <h3>
                    Task Logs{' '}
                    {selectedTask && (
                      <span className="intervention-logs__task-name">
                        -- {selectedTask.subject}
                      </span>
                    )}
                  </h3>
                </div>
                <div className="intervention-logs__content">
                  {outputLines.length === 0 ||
                  (outputLines.length === 1 && outputLines[0] === '') ? (
                    <p className="empty-text">
                      No agent output recorded for this task.
                    </p>
                  ) : (
                    outputLines.map((line, i) => {
                      const isError =
                        /error|exception|traceback|failed|fatal/i.test(line);
                      return (
                        <div
                          key={i}
                          className={`intervention-log-line ${isError ? 'intervention-log-line--error' : ''}`}
                        >
                          {line}
                        </div>
                      );
                    })
                  )}
                </div>
              </div>
            }
            right={
              <ChatPanel
                entries={[]}
                streamingMessage={null}
                sessionStarting={false}
                onSend={() => {
                  /* Chat disabled for now -- will be wired to intervention endpoint later */
                }}
                chatEnabled={false}
              />
            }
          />
        </div>
      </div>

      {/* Footer — recovery actions + save-retry */}
      <footer className="intervention-footer">
        <button
          type="button"
          className="swarm-status-btn swarm-status-btn--continue"
          onClick={handleContinue}
          disabled={recoveryLoading !== null}
        >
          {recoveryLoading === 'continue' ? 'Resuming...' : 'Continue'}
        </button>
        <button
          type="button"
          className="swarm-status-btn swarm-status-btn--smart-continue"
          onClick={handleSmartContinue}
          disabled={recoveryLoading !== null}
        >
          {recoveryLoading === 'smart-continue' ? 'Thinking...' : 'Smart Continue'}
        </button>
        <button
          type="button"
          className="swarm-status-btn swarm-status-btn--skip"
          onClick={handleSkip}
          disabled={recoveryLoading !== null}
        >
          {recoveryLoading === 'skip' ? 'Skipping...' : 'Force Synthesis'}
        </button>
        <button
          type="button"
          className="swarm-status-btn swarm-status-btn--cancel"
          onClick={handleCancel}
          disabled={recoveryLoading !== null}
        >
          {recoveryLoading === 'cancel' ? 'Cancelling...' : 'Cancel'}
        </button>
        <button
          className="intervention-save-retry-btn"
          onClick={onSaveAndRetry}
          disabled={!hasModifications || recoveryLoading !== null}
          title={
            hasModifications
              ? 'Save all modified templates and retry failed tasks'
              : 'No modifications to save'
          }
        >
          Save All &amp; Retry
        </button>
      </footer>
    </div>
  );
}
