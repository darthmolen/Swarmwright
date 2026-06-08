import { useState, useEffect, useCallback } from 'react';
import { CopilotKit } from '@copilotkit/react-core';
import { CopilotChat } from '@copilotkit/react-ui';
import '@copilotkit/react-ui/styles.css';
import { TaskPillBar } from './TaskPillBar';
import { TaskDetailDrawer } from './TaskDetailDrawer';
import type { Task } from '../types/swarm';

const API_BASE = import.meta.env.VITE_API_BASE ?? '';

interface AgentMeta {
  name: string;
  description: string;
}

export interface ReportRightPanelProps {
  swarmId?: string;
  tasks: Task[];
  getAuthHeaders: () => Promise<Record<string, string>>;
  onNavigate?: (swarmId: string) => void;
}

export function ReportRightPanel({
  swarmId,
  tasks,
  getAuthHeaders,
  onNavigate,
}: ReportRightPanelProps) {
  const [selectedTaskId, setSelectedTaskId] = useState<string | null>(null);
  const [selectedAgent, setSelectedAgent] = useState('synthesis');
  const [authHeaders, setAuthHeaders] = useState<Record<string, string>>({});
  const [agents, setAgents] = useState<AgentMeta[]>([]);
  const [chatAvailable, setChatAvailable] = useState<boolean | null>(null);

  // Reset selection when swarm changes
  useEffect(() => {
    setSelectedTaskId(null);
    setSelectedAgent('synthesis');
    setAgents([]);
    setChatAvailable(null);
  }, [swarmId]);

  // Refresh auth headers on mount and periodically
  const refreshHeaders = useCallback(async () => {
    const headers = await getAuthHeaders();
    setAuthHeaders(headers);
  }, [getAuthHeaders]);

  useEffect(() => {
    refreshHeaders();
    const interval = setInterval(refreshHeaders, 4 * 60 * 1000);
    return () => clearInterval(interval);
  }, [refreshHeaders]);

  // Fetch agent info from the copilot endpoint to determine chat availability
  // and populate the agent selector pills.
  useEffect(() => {
    if (!swarmId || Object.keys(authHeaders).length === 0) return;

    let cancelled = false;
    (async () => {
      try {
        const response = await fetch(`${API_BASE}/api/swarm/${swarmId}/copilot`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json', ...authHeaders },
          body: JSON.stringify({ method: 'info' }),
        });
        if (!response.ok) {
          if (!cancelled) setChatAvailable(false);
          return;
        }
        const data = await response.json();
        if (cancelled) return;

        const agentsDict = data.agents ?? {};
        const agentList: AgentMeta[] = Object.entries(agentsDict).map(([name, info]) => ({
          name,
          description: (info as { description?: string }).description ?? '',
        }));
        setAgents(agentList);
        // If backend explicitly says chatAvailable:false OR returns no agents, chat is unavailable.
        setChatAvailable(data.chatAvailable !== false && agentList.length > 0);
      } catch {
        if (!cancelled) setChatAvailable(false);
      }
    })();

    return () => {
      cancelled = true;
    };
  }, [swarmId, authHeaders]);

  const selectedTask = tasks.find((t) => t.id === selectedTaskId) ?? null;

  function handlePillSelect(taskId: string) {
    setSelectedTaskId((prev) => (prev === taskId ? null : taskId));
  }

  // Build agent pill list: always synthesis + all other agents from metadata
  const agentPills = [
    { name: 'synthesis', label: 'Synthesis' },
    ...agents
      .filter((a) => a.name !== 'synthesis')
      .map((a) => ({ name: a.name, label: a.name })),
  ];

  return (
    <div className="right-panel">
      <TaskPillBar
        tasks={tasks}
        selectedTaskId={selectedTaskId}
        onSelect={handlePillSelect}
        onNavigate={onNavigate}
      />
      {selectedTask && (
        <TaskDetailDrawer
          task={selectedTask}
          onClose={() => setSelectedTaskId(null)}
        />
      )}

      {/* Agent selector pills */}
      {chatAvailable && (
        <div className="agent-selector">
          {agentPills.map((agent) => (
            <button
              type="button"
              key={agent.name}
              className={`agent-pill${selectedAgent === agent.name ? ' agent-pill--active' : ''}`}
              onClick={() => setSelectedAgent(agent.name)}
            >
              {agent.label}
            </button>
          ))}
        </div>
      )}

      {/* CopilotKit chat — only render when agents are available */}
      {swarmId && chatAvailable && Object.keys(authHeaders).length > 0 && (
        <CopilotKit
          runtimeUrl={`${API_BASE}/api/swarm/${swarmId}/copilot`}
          headers={authHeaders}
          agent={selectedAgent}
          showDevConsole={false}
          enableInspector={false}
        >
          <CopilotChat
            key={selectedAgent}
            className="copilot-chat"
            labels={{
              title: `Chat with ${selectedAgent}`,
              placeholder: 'Ask about this agent\'s work...',
            }}
          />
        </CopilotKit>
      )}

      {/* Unavailable message */}
      {chatAvailable === false && (
        <div className="chat-unavailable">
          Chat will be available after the swarm completes.
        </div>
      )}
    </div>
  );
}
