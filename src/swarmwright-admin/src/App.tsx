import { useReducer, useCallback, useState, useEffect, useRef, useMemo } from 'react';
import { AuthenticatedTemplate, UnauthenticatedTemplate } from '@azure/msal-react';
import toast, { Toaster } from 'react-hot-toast';
import { multiSwarmReducer, initialMultiSwarmState, isThinking, shouldShowReportView, getDashboardData } from './hooks/useSwarmState';
import { chatReducer, initialChatStore } from './hooks/useChatState';
import { useSSE } from './hooks/useSSE';
import { useSwarmHydration } from './hooks/useSwarmHydration';
import { useSwarmList } from './hooks/useSwarmList';
import { useMsal } from '@azure/msal-react';
import { useAuthToken } from './auth/useAuthToken';
import { Login } from './auth/Login';
import { SwarmControls } from './components/SwarmControls';
import { SwarmStatusWindow } from './components/SwarmStatusWindow';
import { TaskBoard } from './components/TaskBoard';
import { AgentRoster } from './components/AgentRoster';
import { InboxFeed } from './components/InboxFeed';
import { ResizableLayout } from './components/ResizableLayout';
import { ToolCardList } from './components/ToolCard';
import { ArtifactList } from './components/ArtifactList';
import { ReportRightPanel } from './components/ReportRightPanel';
import { JsonlChatViewer } from './components/JsonlChatViewer';
import { ReportHeader } from './components/ReportHeader';
import { useMermaid } from './hooks/useMermaid';
import { marked } from 'marked';
import DOMPurify from 'dompurify';
import type { SwarmEvent, FileInfo } from './types/swarm';
import { parseSessionFromSearch } from './utils/urlSession';
import { swarmListItemAdapter } from './utils/swarmListItemAdapter';
import { hydrateTasksIntoSwarm } from './utils/hydrateTasksIntoSwarm';
import { ReportList } from './components/ReportList';
import { InterventionView } from './components/InterventionView';
import './App.css';
import './styles/copilotkit-overrides.css';

const API_BASE = import.meta.env.VITE_API_URL ?? '';
const DEBUG = import.meta.env.VITE_DEBUG === 'true';

function renderMarkdown(md: string): string {
  return DOMPurify.sanitize(marked.parse(md) as string);
}

/** Invisible component that owns an SSE connection for one swarm. */
function SwarmConnection({
  swarmId,
  onEvent,
  getToken,
}: {
  swarmId: string;
  onEvent: (swarmId: string, event: SwarmEvent) => void;
  getToken: () => Promise<string | null>;
}) {
  const handler = useCallback(
    (event: SwarmEvent) => onEvent(swarmId, event),
    [swarmId, onEvent],
  );
  // Do NOT add an `enabled` prop here: the App_clickingUnknownSwarm_doesNotDoubleSubscribeSSE
  // test uses `enabled === undefined` as the fingerprint to distinguish this
  // mount site from useSwarmHydration's SSE call (which always sets an explicit boolean).
  useSSE({
    url: `${API_BASE}/api/swarm/${swarmId}/stream`,
    onEvent: handler,
    getToken,
  });
  return null;
}

function App() {
  return (
    <>
      <AuthenticatedTemplate>
        <Toaster position="top-right" toastOptions={{
          style: { background: '#1e293b', color: '#e2e8f0', border: '1px solid #334155' },
        }} />
        <SwarmDashboard />
      </AuthenticatedTemplate>
      <UnauthenticatedTemplate>
        <Login />
      </UnauthenticatedTemplate>
    </>
  );
}

function SwarmDashboard() {
  const { instance } = useMsal();
  const { getToken } = useAuthToken();
  // Server-authoritative session list. Declared before handleStartSwarm so the
  // create-swarm handler can invalidate the list after a successful POST.
  const {
    swarms: swarmList,
    refresh: refreshSwarmList,
  } = useSwarmList({ limit: 50, pollIntervalMs: 30_000 });
  const [store, swarmDispatch] = useReducer(multiSwarmReducer, initialMultiSwarmState);
  const [, chatDispatch] = useReducer(chatReducer, initialChatStore);
  const [reportSwarmId, setReportSwarmId] = useState<string | null>(() => {
    return parseSessionFromSearch(window.location.search);
  });
  const [dashboardSwarmId, setDashboardSwarmId] = useState<string | null>(null);

  // Hydrate unknown swarms (spawned in another tab/process). The hook owns
  // SSE for hydrated swarms; App must exclude `hydratedSseOwnedId` from its
  // SwarmConnection JSX maps so two readers can't race the single backend
  // ChannelReader. Ownership is STICKY — sourced from the hook's own state
  // so `swarm.add` landing in `knownSwarmIds` does not flip the filter off
  // mid-hydration. See useSwarmHydration.ts for race details. `loading` /
  // `error` are destructured for future UX work (spinner / failure toast).
  const knownSwarmIds = useMemo(() => new Set<string>([...store.activeSwarmIds, ...store.completedSwarmIds]), [store.activeSwarmIds, store.completedSwarmIds]);
  const { ownedSwarmId: hydratedSseOwnedId, loading: _hydrationLoading, error: hydrationError } =
    useSwarmHydration(reportSwarmId, { knownSwarmIds, dispatch: swarmDispatch });
  void _hydrationLoading;

  // Dashboard-only hydration: populates the task board without navigating to report view.
  const {
    ownedSwarmId: dashboardSseOwnedId,
    forceRefresh: forceDashboardRefresh,
    recommendation: dashboardRecommendation,
  } = useSwarmHydration(dashboardSwarmId, { knownSwarmIds, dispatch: swarmDispatch });

  useEffect(() => {
    if (hydrationError) toast.error(`Failed to load session: ${hydrationError.message}`);
  }, [hydrationError]);

  // Artifact explorer state
  const [swarmFiles, setSwarmFiles] = useState<FileInfo[]>([]);
  const [activeFilePath, setActiveFilePath] = useState<string | null>(null);
  const [activeFileContent, setActiveFileContent] = useState<string | null>(null);

  // Intervention view state
  const [interventionTaskId, setInterventionTaskId] = useState<string | null>(null);

  /** Helper: build auth headers using the MSAL token. */
  const getAuthHeaders = useCallback(async (): Promise<Record<string, string>> => {
    const token = await getToken();
    return token ? { Authorization: `Bearer ${token}` } : {};
  }, [getToken]);

  // Fetch report from backend when URL has a session
  const fetchedRef = useRef(false);
  useEffect(() => {
    if (fetchedRef.current) return;
    const sessionId = parseSessionFromSearch(window.location.search);
    if (!sessionId) return;
    fetchedRef.current = true;

    (async () => {
      const headers = await getAuthHeaders();
      fetch(`${API_BASE}/api/swarm/${sessionId}`, { headers })
        .then((res) => (res.ok ? res.json() : null))
        .then((data) => {
          if (data?.report) {
            // Hydrate tasks into swarm state so report view can display them
            for (const action of hydrateTasksIntoSwarm(sessionId, data.tasks)) {
              swarmDispatch(action);
            }
            setReportSwarmId(sessionId);
          }
        })
        .catch(() => {
          // Backend unreachable or swarm not found — stay on dashboard
        });
    })();
  }, []); // eslint-disable-line react-hooks/exhaustive-deps

  const handleSwarmEvent = useCallback(
    (swarmId: string, event: SwarmEvent) => {
      if (DEBUG) console.log(`[Event] ${event.type}`, { swarmId, ...event });

      // Route tool call events to both swarm + chat reducers
      if (event.type === 'TOOL_CALL_START') {
        if (event.parentMessageId) {
          chatDispatch({
            type: 'chat.tool_start',
            swarmId,
            toolName: (event.toolCallName as string) ?? '',
            toolCallId: (event.toolCallId as string) ?? '',
            input: event.delta as string | undefined,
          });
        }
        swarmDispatch({ type: 'swarm.event', swarmId, event });
      } else if (event.type === 'TOOL_CALL_RESULT') {
        if (event.messageId) {
          chatDispatch({
            type: 'chat.tool_result',
            swarmId,
            toolCallId: (event.toolCallId as string) ?? '',
            success: true,
            output: event.content as string | undefined,
          });
        }
        swarmDispatch({ type: 'swarm.event', swarmId, event });
      } else if (event.type === 'TEXT_MESSAGE_CONTENT' && event.messageId) {
        // Route text deltas to chat reducer for chat context
        chatDispatch({
          type: 'chat.delta',
          swarmId,
          delta: (event.delta as string) ?? '',
          messageId: (event.messageId as string) ?? '',
        });
        swarmDispatch({ type: 'swarm.event', swarmId, event });
      } else {
        // All other events go to swarm reducer
        swarmDispatch({ type: 'swarm.event', swarmId, event });

        // Notify user when swarm is suspended (STATE_DELTA with /phase = Suspended)
        if (event.type === 'STATE_DELTA') {
          const ops = event.delta as Array<{ op: string; path: string; value: unknown }>;
          const phaseOp = ops?.find((op) => op.path === '/phase');
          if (phaseOp?.value === 'Suspended') {
            toast('Swarm paused — action required', { icon: '\u23F8', duration: 8000 });
          }
        }

        // Auto-switch to report view when synthesizing starts or run finishes
        if (event.type === 'STEP_STARTED' && (event.stepName as string)?.toLowerCase() === 'synthesizing') {
          setReportSwarmId(swarmId);
        }
        if (event.type === 'RUN_FINISHED') {
          setReportSwarmId(swarmId);
        }

        // Auto-switch back to dashboard when planning starts
        if (event.type === 'STEP_STARTED' && (event.stepName as string)?.toLowerCase() === 'planning') {
          setReportSwarmId(null);
          toast('Swarm started! Watch progress on the task board.', { icon: '\u{1F680}', duration: 5000 });
        }
      }
    },
    [],
  );

  async function handleStartSwarm(swarmId: string) {
    setDashboardSwarmId(swarmId);
    swarmDispatch({ type: 'swarm.add', swarmId });

    // Invalidate the server-authoritative session list immediately so the left
    // pane picks up the new swarm without waiting for the next poll tick.
    // Fire-and-forget: the swarm create POST already succeeded, so a transient
    // list-refresh failure must not propagate as an unhandled rejection
    // (SwarmControls.onStart invokes this handler without awaiting). The next
    // poll tick will reconcile the list.
    void refreshSwarmList().catch(() => {
      // next poll tick will fix it; swarm creation succeeded
    });

    // (The 'qa' phase was removed when the state machine refactor landed.
    //  Nothing else was being polled here, so no catch-up fetch is needed.)
  }

  // When entering report view: ensure report on server + fetch file list
  const artifactFetchedRef = useRef<string | null>(null);

  /** Fetch (or re-fetch) the artifact list for the current report swarm. Used on initial
   *  load via the useEffect below AND on demand via the Refresh button in the header. */
  const refreshArtifacts = useCallback(async () => {
    if (!reportSwarmId) return;

    const headers = await getAuthHeaders();
    try {
      const response = await fetch(`${API_BASE}/api/swarm/${reportSwarmId}/artifacts`, { headers });
      const data = response.ok ? await response.json() : { files: [] };
      const files: FileInfo[] = data.files ?? [];
      setSwarmFiles(files);

      const defaultFile = files.find((f: FileInfo) => f.name === 'synthesis_report.md')?.path
        ?? files[0]?.path ?? null;
      setActiveFilePath(defaultFile);

      if (defaultFile) {
        const r = await fetch(`${API_BASE}/api/swarm/${reportSwarmId}/artifacts/${defaultFile}`, { headers });
        if (r.ok) {
          const text = await r.text();
          setActiveFileContent(text);
        }
      }
    } catch {
      // Silent — the UI shows "No files yet" when files is empty.
    }
  }, [reportSwarmId, getAuthHeaders]);

  useEffect(() => {
    if (!reportSwarmId) {
      artifactFetchedRef.current = null;  // Reset so re-entry re-fetches
      return;
    }
    if (artifactFetchedRef.current === reportSwarmId) return;
    artifactFetchedRef.current = reportSwarmId;

    (async () => {
      const headers = await getAuthHeaders();

      // Hydrate tasks if not already in memory (past report from localStorage).
      // Skip when useSwarmHydration owns the swarm — it already fetches /tasks.
      if (hydratedSseOwnedId !== reportSwarmId && !(store.swarms[reportSwarmId]?.tasks?.length)) {
        fetch(`${API_BASE}/api/swarm/${reportSwarmId}`, { headers })
          .then((res) => (res.ok ? res.json() : null))
          .then((data) => {
            for (const action of hydrateTasksIntoSwarm(reportSwarmId, data?.tasks)) {
              swarmDispatch(action);
            }
          })
          .catch(() => null);
      }

      await refreshArtifacts();
    })();
  }, [reportSwarmId, refreshArtifacts]); // eslint-disable-line react-hooks/exhaustive-deps

  // Fetch file content when active file changes
  function handleSelectArtifact(path: string) {
    if (!reportSwarmId || path === activeFilePath) return;
    setActiveFilePath(path);
    setActiveFileContent(null); // clear while loading

    (async () => {
      const headers = await getAuthHeaders();
      fetch(`${API_BASE}/api/swarm/${reportSwarmId}/artifacts/${path}`, { headers })
        .then((res) => (res.ok ? res.text() : null))
        .then((text) => { if (text) setActiveFileContent(text); })
        .catch(() => null);
    })();
  }

  // Sync URL with current report view
  useEffect(() => {
    if (reportSwarmId) {
      window.history.replaceState(null, '', `?session=${reportSwarmId}`);
    } else {
      window.history.replaceState(null, '', window.location.pathname);
    }
  }, [reportSwarmId]);

  // Dashboard shows exactly one swarm at a time, identified by dashboardSwarmId.
  const { tasks: allTasks, agents: allAgents, messages: allMessages, agentOutputs: allOutputs, activeTools: allActiveTools } = getDashboardData(store, dashboardSwarmId);

  // Focused swarm for status window
  const focusedSwarm = dashboardSwarmId ? store.swarms[dashboardSwarmId] ?? null : null;

  // Header status
  const anyConnected = store.activeSwarmIds.length > 0;
  const anyThinking = Object.values(store.swarms).some((s) => isThinking(s.phase));
  const anyError = Object.values(store.swarms).find((s) => s.error)?.error ?? null;

  // Session list comes from the server via useSwarmList.
  const reportListItems = swarmListItemAdapter(swarmList);
  const currentReport = reportSwarmId
    ? (store.swarms[reportSwarmId]?.leaderReport || null)
    : null;
  const currentPhase = reportSwarmId ? (store.swarms[reportSwarmId]?.phase ?? null) : null;

  // Tasks for the report swarm (used by TaskPillBar in the report view)
  const reportSwarmTasks = reportSwarmId ? (store.swarms[reportSwarmId]?.tasks ?? []) : [];

  // Compute failed/timeout tasks for intervention view
  const failedTasks = allTasks.filter(
    (t) => t.status === 'failed',
  );

  // Handler to enter intervention view when a failed task pill is clicked
  const handleInterventionClick = (taskId: string) => setInterventionTaskId(taskId);

  // Resume a suspended swarm from DB
  const handleResumeSwarm = async (swarmId: string) => {
    try {
      const headers = await getAuthHeaders();
      const resp = await fetch(`${API_BASE}/api/swarm/${swarmId}/resume`, {
        method: 'POST',
        headers,
      });
      if (!resp.ok) {
        const detail = await resp.json().catch(() => ({}));
        toast.error(`Resume failed: ${detail.detail ?? resp.statusText}`);
        return;
      }
      toast.success('Swarm resuming...');
      // Add to active swarms so we start tracking it
      swarmDispatch({ type: 'swarm.add', swarmId });
      // Server list will reflect the phase change on the next poll; kick one now.
      void refreshSwarmList();

      // Hydrate existing task/agent state from backend before SSE connects
      try {
        const statusHeaders = await getAuthHeaders();
        const statusResp = await fetch(`${API_BASE}/api/swarm/${swarmId}`, {
          headers: statusHeaders,
        });
        if (statusResp.ok) {
          const status = await statusResp.json();
          // Hydrate with a STATE_SNAPSHOT containing all tasks + agents
          swarmDispatch({
            type: 'swarm.event', swarmId,
            event: {
              type: 'STATE_SNAPSHOT',
              snapshot: {
                phase: status.phase ?? 'executing',
                roundNumber: status.round ?? 0,
                tasks: status.tasks ?? [],
                agents: status.agents ?? [],
                messages: [],
              },
            },
          });
        }
      } catch { /* status fetch is best-effort */ }
    } catch {
      toast.error('Failed to resume swarm');
    }
  };

  // Determine template key for the intervention swarm (use first active swarm's id as fallback)
  const interventionSwarmId = dashboardSwarmId ?? reportSwarmId ?? '';

  // Mermaid diagram rendering for report view
  const reportContentRef = useRef<HTMLDivElement>(null);
  useMermaid(reportContentRef, [activeFileContent, currentReport]);

  // Intervention view: shown when a task is selected for intervention
  if (interventionTaskId && failedTasks.length > 0) {
    return (
      <div className="app app--intervention-view">
        {/* Keep SSE connections alive. Exclude the id whose SSE is owned by
            useSwarmHydration (see comment on hydratedSseOwnedId above). */}
        {store.activeSwarmIds
          .filter((id) => id !== hydratedSseOwnedId && id !== dashboardSseOwnedId)
          .map((id) => (
            <SwarmConnection key={id} swarmId={id} onEvent={handleSwarmEvent} getToken={getToken} />
          ))}
        <InterventionView
          swarmId={interventionSwarmId}
          templateKey={interventionSwarmId}
          tasks={failedTasks}
          selectedTaskId={interventionTaskId}
          onSelectTask={setInterventionTaskId}
          agentOutputs={allOutputs}
          onBack={() => setInterventionTaskId(null)}
          onSaveAndRetry={async () => {
            if (!interventionSwarmId) return;
            try {
              const headers = await getAuthHeaders();
              await fetch(`${API_BASE}/api/swarm/${interventionSwarmId}/continue`, {
                method: 'POST',
                headers,
              });
              setInterventionTaskId(null);
            } catch (err) {
              console.error('Failed to continue swarm:', err);
            }
          }}
        />
      </div>
    );
  }

  // Full-screen report + chat view (also shown during QA phase before report exists)
  if (shouldShowReportView(reportSwarmId, currentReport, currentPhase)) {
    return (
      <div className="app app--report-view">
        <ReportHeader
          swarmId={reportSwarmId!}
          onBack={() => setReportSwarmId(null)}
          onCopy={() => {
            navigator.clipboard.writeText(currentReport ?? '');
            const btn = document.querySelector('.copy-button');
            if (btn) { btn.textContent = 'Copied!'; setTimeout(() => btn.textContent = 'Copy', 1500); }
          }}
          onRefresh={() => {
            // Allow a fresh fetch even if we already fetched for this swarm id once.
            artifactFetchedRef.current = null;
            void refreshArtifacts();
          }}
        />

        {/* SSE connections stay alive for chat events. Exclude the hydration-
            owned id (see hydratedSseOwnedId above). */}
        {store.activeSwarmIds
          .filter((id) => id !== hydratedSseOwnedId && id !== dashboardSseOwnedId)
          .map((id) => (
            <SwarmConnection key={id} swarmId={id} onEvent={handleSwarmEvent} getToken={getToken} />
          ))}
        {/* Connect SSE for saved/resumed sessions that are still running.
            Skip when the hydration hook already owns the stream, or when the
            swarm has reached a terminal phase (no events to stream). */}
        {reportSwarmId
          && !store.activeSwarmIds.includes(reportSwarmId)
          && reportSwarmId !== hydratedSseOwnedId
          && reportSwarmId !== dashboardSseOwnedId
          && currentPhase !== 'complete' && currentPhase !== 'failed' && currentPhase !== 'cancelled' && (
          <SwarmConnection key={`chat-${reportSwarmId}`} swarmId={reportSwarmId} onEvent={handleSwarmEvent} getToken={getToken} />
        )}

        <ResizableLayout
          left={
            <div className="report-view">
              <ArtifactList
                files={swarmFiles}
                activeFile={activeFilePath}
                onSelect={handleSelectArtifact}
                swarmId={reportSwarmId ?? undefined}
              />
              {activeFilePath?.endsWith('.jsonl') && activeFileContent ? (
                <div ref={reportContentRef} className="report-content">
                  <JsonlChatViewer content={activeFileContent} />
                </div>
              ) : (
                <div
                  ref={reportContentRef}
                  className="report-content"
                  dangerouslySetInnerHTML={{
                    __html: renderMarkdown(activeFileContent ?? currentReport ?? ''),
                  }}
                />
              )}
            </div>
          }
          right={
            <ReportRightPanel
              swarmId={reportSwarmId ?? undefined}
              tasks={reportSwarmTasks}
              getAuthHeaders={getAuthHeaders}
              onNavigate={setReportSwarmId}
            />
          }
          defaultLeftPercent={55}
        />
      </div>
    );
  }

  // Dashboard view
  return (
    <div className="app">
      <header className="app-header">
        <h1>Multi-Agent Swarm</h1>
        <div className="status-bar">
          <button
            type="button"
            className="logout-btn"
            onClick={() => instance.logoutPopup({ postLogoutRedirectUri: window.location.origin })}
          >
            Sign out
          </button>
          <span className={`connection-status ${anyConnected ? 'connected' : 'disconnected'}`}>
            {anyConnected ? 'Connected' : 'Disconnected'}
          </span>
          {anyThinking && (
            <span className="thinking-badge">
              <span className="thinking-icon">🧠</span>
              <span className="thinking-text">Thinking...</span>
            </span>
          )}
          {store.activeSwarmIds.length > 0 && (
            <span className="phase-badge">{store.activeSwarmIds.length} active</span>
          )}
        </div>
        {anyError && <p className="error-banner">{anyError}</p>}
      </header>

      <div className="controls-row">
        <ReportList
          items={reportListItems}
          activeId={reportSwarmId ?? dashboardSwarmId}
          onSelect={setReportSwarmId}
          onHydrate={(id) => { setDashboardSwarmId(id); setReportSwarmId(null); }}
          onResume={handleResumeSwarm}
        />
        <div className="controls-stack">
          {focusedSwarm && dashboardSwarmId && (
            <SwarmStatusWindow
              swarmId={dashboardSwarmId}
              phase={focusedSwarm.phase ?? 'created'}
              tasks={focusedSwarm.tasks}
              agents={focusedSwarm.agents}
              roundNumber={focusedSwarm.roundNumber}
              suspended={focusedSwarm.suspended}
              recommendation={dashboardRecommendation}
              onGoToReport={() => setReportSwarmId(dashboardSwarmId)}
              onClose={() => { swarmDispatch({ type: 'swarm.remove', swarmId: dashboardSwarmId }); setDashboardSwarmId(null); }}
              onManualRecoverSuccess={forceDashboardRefresh}
              onRecoverySuccess={forceDashboardRefresh}
            />
          )}
          <SwarmControls onStart={handleStartSwarm} />
        </div>
      </div>

      {/* Failed task pills — click to enter intervention view */}
      {failedTasks.length > 0 && (
        <div className="failed-tasks-bar">
          <span className="failed-tasks-label">Failed tasks:</span>
          {failedTasks.map((t) => (
            <button
              key={t.id}
              className="failed-task-pill"
              onClick={() => handleInterventionClick(t.id)}
              title={`${t.subject} (${t.status})`}
            >
              {t.worker_name} — {t.status}
            </button>
          ))}
        </div>
      )}

      {/* SSE connections -- one per active swarm. Exclude the hydration-owned
          id (see hydratedSseOwnedId above). */}
      {store.activeSwarmIds
        .filter((id) => id !== hydratedSseOwnedId && id !== dashboardSseOwnedId)
        .map((id) => (
          <SwarmConnection key={id} swarmId={id} onEvent={handleSwarmEvent} getToken={getToken} />
        ))}

      {!dashboardSwarmId || !store.swarms[dashboardSwarmId] ? (
        <div className="dashboard-empty">Select a swarm from the list, or start a new one</div>
      ) : (
        <div className="dashboard-new">
          <div className="top-row">
            <AgentRoster agents={allAgents} outputs={allOutputs} />
            <InboxFeed messages={allMessages} />
          </div>
          <ToolCardList tools={allActiveTools.filter((t) => t.status === 'running')} />
          <div className="bottom-row">
            <TaskBoard tasks={allTasks} />
          </div>
        </div>
      )}
    </div>
  );
}

export default App;
