import { describe, it, expect } from 'vitest';
import { getDashboardData } from '../hooks/useSwarmState';
import type { MultiSwarmStore } from '../hooks/useSwarmState';
import type { SwarmState, Task, AgentInfo, InboxMessage } from '../types/swarm';

function makeSwarmState(overrides: Partial<SwarmState> = {}): SwarmState {
  return {
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
    ...overrides,
  };
}

function makeTask(id: string, subject: string): Task {
  return {
    id,
    subject,
    description: '',
    worker_role: 'dev',
    worker_name: 'w',
    status: 'pending',
    blocked_by: [],
    result: '',
  };
}

function makeAgent(name: string): AgentInfo {
  return {
    name,
    role: name,
    display_name: name,
    status: 'idle',
    tasks_completed: 0,
  };
}

function makeMessage(sender: string, recipient: string, content: string): InboxMessage {
  return { sender, recipient, content, timestamp: new Date().toISOString() };
}

describe('getDashboardData', () => {
  it('whenSwarmIdNull_returnsEmptyData', () => {
    const store: MultiSwarmStore = {
      swarms: {
        'swarm-A': makeSwarmState({
          tasks: [makeTask('t1', 'Task A')],
          agents: [makeAgent('agent-a')],
        }),
      },
      activeSwarmIds: ['swarm-A'],
      completedSwarmIds: [],
    };

    const result = getDashboardData(store, null);

    expect(result.tasks).toEqual([]);
    expect(result.agents).toEqual([]);
    expect(result.messages).toEqual([]);
    expect(result.agentOutputs).toEqual({});
    expect(result.activeTools).toEqual([]);
  });

  it('whenSwarmExistsInStore_returnsOnlyThatSwarmsData', () => {
    const taskA = makeTask('t1', 'Task A');
    const taskB = makeTask('t2', 'Task B');
    const agentA = makeAgent('agent-a');
    const agentB = makeAgent('agent-b');
    const msgA = makeMessage('agent-a', 'leader', 'Report A');
    const msgB = makeMessage('agent-b', 'leader', 'Report B');

    const store: MultiSwarmStore = {
      swarms: {
        'swarm-A': makeSwarmState({
          tasks: [taskA],
          agents: [agentA],
          messages: [msgA],
          agentOutputs: { 'agent-a': 'output-a' },
          activeTools: [{ toolCallId: 'tc-1', toolName: 'search', agentName: 'agent-a', status: 'running' }],
        }),
        'swarm-B': makeSwarmState({
          tasks: [taskB],
          agents: [agentB],
          messages: [msgB],
          agentOutputs: { 'agent-b': 'output-b' },
          activeTools: [{ toolCallId: 'tc-2', toolName: 'write', agentName: 'agent-b', status: 'running' }],
        }),
      },
      activeSwarmIds: ['swarm-A', 'swarm-B'],
      completedSwarmIds: [],
    };

    const result = getDashboardData(store, 'swarm-A');

    expect(result.tasks).toEqual([taskA]);
    expect(result.agents).toEqual([agentA]);
    expect(result.messages).toEqual([msgA]);
    expect(result.agentOutputs).toEqual({ 'agent-a': 'output-a' });
    expect(result.activeTools).toHaveLength(1);
    expect(result.activeTools[0].toolCallId).toBe('tc-1');
  });

  it('whenSwarmNotYetInStore_returnsEmptyData', () => {
    const store: MultiSwarmStore = {
      swarms: {
        'swarm-A': makeSwarmState({ tasks: [makeTask('t1', 'Task A')] }),
      },
      activeSwarmIds: ['swarm-A'],
      completedSwarmIds: [],
    };

    const result = getDashboardData(store, 'ghost');

    expect(result.tasks).toEqual([]);
    expect(result.agents).toEqual([]);
    expect(result.messages).toEqual([]);
    expect(result.agentOutputs).toEqual({});
    expect(result.activeTools).toEqual([]);
  });

  it('agentOutputs_returnsOnlyFocusedSwarmsOutputs', () => {
    const store: MultiSwarmStore = {
      swarms: {
        'swarm-A': makeSwarmState({
          agentOutputs: { researcher: 'findings-A', analyst: 'analysis-A' },
        }),
        'swarm-B': makeSwarmState({
          agentOutputs: { researcher: 'findings-B', writer: 'draft-B' },
        }),
      },
      activeSwarmIds: ['swarm-A', 'swarm-B'],
      completedSwarmIds: [],
    };

    const result = getDashboardData(store, 'swarm-A');

    expect(result.agentOutputs).toEqual({ researcher: 'findings-A', analyst: 'analysis-A' });
    expect(result.agentOutputs).not.toHaveProperty('writer');
  });
});
