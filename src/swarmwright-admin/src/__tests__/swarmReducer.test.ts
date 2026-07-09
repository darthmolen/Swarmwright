import { describe, it, expect } from 'vitest';
import { swarmReducer, initialState, isThinking, multiSwarmReducer, initialMultiSwarmState, shouldShowReportView } from '../hooks/useSwarmState';
import type { SwarmState, SwarmEvent } from '../types/swarm';

describe('swarmReducer — AG-UI lifecycle events', () => {
  it('RUN_STARTED sets phase to created and captures threadId/runId', () => {
    const event: SwarmEvent = { type: 'RUN_STARTED', threadId: 'thread-1', runId: 'run-1' };
    const result = swarmReducer(initialState, event);
    expect(result.phase).toBe('created');
    expect(result.threadId).toBe('thread-1');
    expect(result.runId).toBe('run-1');
  });

  it('RUN_FINISHED sets phase to complete', () => {
    const event: SwarmEvent = { type: 'RUN_FINISHED', threadId: 'thread-1', runId: 'run-1' };
    const result = swarmReducer(initialState, event);
    expect(result.phase).toBe('complete');
  });

  it('RUN_ERROR sets error from message', () => {
    const event: SwarmEvent = { type: 'RUN_ERROR', message: 'Planning timed out', code: 'TIMEOUT' };
    const result = swarmReducer(initialState, event);
    expect(result.error).toBe('Planning timed out');
  });
});

describe('swarmReducer — AG-UI step events', () => {
  it('STEP_STARTED sets phase from stepName', () => {
    const event: SwarmEvent = { type: 'STEP_STARTED', stepName: 'Planning' };
    const result = swarmReducer(initialState, event);
    expect(result.phase).toBe('planning');
  });

  it('STEP_STARTED normalizes phase to lowercase', () => {
    const event: SwarmEvent = { type: 'STEP_STARTED', stepName: 'Executing' };
    const result = swarmReducer(initialState, event);
    expect(result.phase).toBe('executing');
  });

  it('STEP_FINISHED returns unchanged state', () => {
    const state: SwarmState = { ...initialState, phase: 'planning' };
    const event: SwarmEvent = { type: 'STEP_FINISHED', stepName: 'Planning' };
    const result = swarmReducer(state, event);
    expect(result.phase).toBe('planning');
  });
});

describe('swarmReducer — AG-UI state management', () => {
  it('STATE_SNAPSHOT replaces entire state from snapshot', () => {
    const snapshot = {
      phase: 'Executing',
      roundNumber: 2,
      tasks: [{ id: 't1', subject: 'Task 1', status: 'pending' }],
      agents: [{ name: 'researcher', role: 'analyst', displayName: 'Analyst' }],
      messages: [],
    };
    const event: SwarmEvent = { type: 'STATE_SNAPSHOT', snapshot };
    const result = swarmReducer(initialState, event);
    expect(result.phase).toBe('executing');
    expect(result.roundNumber).toBe(2);
    expect(result.tasks).toHaveLength(1);
    expect(result.tasks[0].id).toBe('t1');
    expect(result.agents).toHaveLength(1);
  });

  it('STATE_SNAPSHOT normalizes backend task shape (camelCase props + PascalCase status) to frontend canonical form', () => {
    // This snapshot mirrors exactly what SwarmOrchestrator.PlanAsync emits:
    // - property names are camelCase (JsonSerializerDefaults.Web)
    // - `status` is the PascalCase enum name (SwarmJsonOptions.Default uses JsonStringEnumConverter)
    // The frontend Task type expects snake_case props and lowercase_snake_case status,
    // so the reducer must normalize on ingestion or the TaskBoard filter returns zero rows.
    const snapshot = {
      phase: 'Spawning',
      roundNumber: 0,
      tasks: [
        {
          id: 'task-1',
          subject: 'Skeptical analysis of agent architectures',
          description: 'Critically analyze the claims.',
          workerRole: 'Skeptic',
          workerName: 'skeptic',
          status: 'Pending',
          blockedBy: [],
          result: '',
          swarmId: '11111111-1111-1111-1111-111111111111',
        },
        {
          id: 'task-2',
          subject: 'Primary research',
          description: 'Literature review.',
          workerRole: 'Primary Researcher',
          workerName: 'primary_researcher',
          status: 'InProgress',
          blockedBy: [],
          result: '',
          swarmId: '11111111-1111-1111-1111-111111111111',
        },
        {
          id: 'task-3',
          subject: 'Quantitative data analysis',
          description: 'Crunch numbers.',
          workerRole: 'Data Analyst',
          workerName: 'data_analyst',
          status: 'Completed',
          blockedBy: [],
          result: 'All done.',
          swarmId: '11111111-1111-1111-1111-111111111111',
        },
      ],
      agents: [],
      messages: [],
    };
    const event: SwarmEvent = { type: 'STATE_SNAPSHOT', snapshot };
    const result = swarmReducer(initialState, event);

    // Shape: three tasks preserved
    expect(result.tasks).toHaveLength(3);

    // Status must be lowercase snake_case so TaskBoard.tsx column filters match.
    expect(result.tasks[0].status).toBe('pending');
    expect(result.tasks[1].status).toBe('in_progress');
    expect(result.tasks[2].status).toBe('completed');

    // Worker fields must surface on the snake_case properties the type declares,
    // not as undefined (which would make task cards render with empty worker labels).
    expect(result.tasks[0].worker_name).toBe('skeptic');
    expect(result.tasks[0].worker_role).toBe('Skeptic');
    expect(result.tasks[1].worker_name).toBe('primary_researcher');
    expect(result.tasks[2].worker_name).toBe('data_analyst');

    // swarm_id surfaces so TaskCard can show the short prefix.
    expect(result.tasks[0].swarm_id).toBe('11111111-1111-1111-1111-111111111111');

    // blocked_by surfaces as an array (even when empty) so downstream code can
    // iterate without null-guarding every access.
    expect(result.tasks[0].blocked_by).toEqual([]);
  });

  it('STATE_SNAPSHOT preserves already-lowercase status (backwards compat with pre-normalized snapshots)', () => {
    // Hand-constructed test snapshots (like the case above and many existing tests)
    // use the frontend canonical form directly. The normalizer must be a no-op on
    // values that are already snake_case/lowercase.
    const snapshot = {
      phase: 'Executing',
      tasks: [
        {
          id: 't1',
          subject: 'Already normalized',
          status: 'in_progress',
          worker_name: 'worker_1',
          worker_role: 'Analyst',
          blocked_by: [],
          result: '',
        },
      ],
      agents: [],
      messages: [],
    };
    const event: SwarmEvent = { type: 'STATE_SNAPSHOT', snapshot };
    const result = swarmReducer(initialState, event);

    expect(result.tasks[0].status).toBe('in_progress');
    expect(result.tasks[0].worker_name).toBe('worker_1');
    expect(result.tasks[0].worker_role).toBe('Analyst');
  });

  it('STATE_SNAPSHOT normalizes backend agent shape (camelCase props) to frontend canonical form', () => {
    // The /agents endpoint returns AgentEntity serialized with JsonSerializerDefaults.Web,
    // producing camelCase property names. The frontend AgentInfo type expects snake_case.
    const snapshot = {
      phase: 'Executing',
      tasks: [],
      agents: [
        {
          name: 'researcher',
          role: 'Research Analyst',
          displayName: 'Research Analyst',
          status: 'working',
          tasksCompleted: 3,
          swarmId: '11111111-1111-1111-1111-111111111111',
        },
        {
          name: 'analyst',
          role: 'Data Analyst',
          display_name: 'Data Analyst',
          status: 'idle',
          tasks_completed: 1,
        },
      ],
      messages: [],
    };
    const event: SwarmEvent = { type: 'STATE_SNAPSHOT', snapshot };
    const result = swarmReducer(initialState, event);

    expect(result.agents).toHaveLength(2);
    // camelCase → snake_case
    expect(result.agents[0].display_name).toBe('Research Analyst');
    expect(result.agents[0].tasks_completed).toBe(3);
    expect(result.agents[0].swarm_id).toBe('11111111-1111-1111-1111-111111111111');
    // Already snake_case passes through
    expect(result.agents[1].display_name).toBe('Data Analyst');
    expect(result.agents[1].tasks_completed).toBe(1);
  });

  it('STATE_SNAPSHOT normalizes backend message shape (createdAt → timestamp) to frontend canonical form', () => {
    // The /messages endpoint returns MessageEntity with createdAt (camelCase).
    // The frontend InboxMessage type expects timestamp.
    const snapshot = {
      phase: 'Complete',
      tasks: [],
      agents: [],
      messages: [
        {
          sender: 'researcher',
          recipient: 'leader',
          content: 'Findings ready',
          createdAt: '2026-04-10T12:00:00Z',
        },
        {
          sender: 'analyst',
          recipient: 'leader',
          content: 'Data processed',
          timestamp: '2026-04-10T12:05:00Z',
        },
      ],
    };
    const event: SwarmEvent = { type: 'STATE_SNAPSHOT', snapshot };
    const result = swarmReducer(initialState, event);

    expect(result.messages).toHaveLength(2);
    expect(result.messages[0].sender).toBe('researcher');
    expect(result.messages[0].timestamp).toBe('2026-04-10T12:00:00Z');
    expect(result.messages[1].timestamp).toBe('2026-04-10T12:05:00Z');
  });

  it('STATE_DELTA applies JSON Patch operations', () => {
    const state: SwarmState = { ...initialState, phase: 'planning', roundNumber: 0 };
    const event: SwarmEvent = {
      type: 'STATE_DELTA',
      delta: [
        { op: 'replace', path: '/phase', value: 'executing' },
        { op: 'replace', path: '/roundNumber', value: 3 },
      ],
    };
    const result = swarmReducer(state, event);
    expect(result.phase).toBe('executing');
    expect(result.roundNumber).toBe(3);
  });

  it('STATE_DELTA normalizes PascalCase /phase value to snake_case', () => {
    // The backend emits /phase patches with the canonical state-machine
    // name (e.g. "AwaitingIntervention"). The reducer must normalize to
    // the frontend's snake_case variant so downstream branches in
    // SwarmStatusWindow fire correctly. Without this, the swarm card
    // renders "AwaitingIntervention" (or legacy "Suspended") with no
    // recovery buttons because none of the phase === 'awaiting_intervention'
    // comparisons match a PascalCase string.
    const state: SwarmState = { ...initialState, phase: 'executing' };
    const event: SwarmEvent = {
      type: 'STATE_DELTA',
      delta: [
        { op: 'replace', path: '/phase', value: 'AwaitingIntervention' },
      ],
    };
    const result = swarmReducer(state, event);
    expect(result.phase).toBe('awaiting_intervention');
  });

  it('STATE_DELTA normalizes /phase even when the value is already snake_case', () => {
    const state: SwarmState = { ...initialState, phase: 'executing' };
    const event: SwarmEvent = {
      type: 'STATE_DELTA',
      delta: [
        { op: 'replace', path: '/phase', value: 'awaiting_intervention' },
      ],
    };
    const result = swarmReducer(state, event);
    expect(result.phase).toBe('awaiting_intervention');
  });

  it('STATE_DELTA falls back to created when /phase value is unrecognized', () => {
    // Legacy strings that are no longer part of the state machine (like
    // the pre-Phase-B "Suspended" literal) should fall through to the
    // default rather than landing in state as-is.
    const state: SwarmState = { ...initialState, phase: 'executing' };
    const event: SwarmEvent = {
      type: 'STATE_DELTA',
      delta: [
        { op: 'replace', path: '/phase', value: 'Suspended' },
      ],
    };
    const result = swarmReducer(state, event);
    expect(result.phase).toBe('created');
  });
});

describe('swarmReducer — AG-UI text message events', () => {
  it('TEXT_MESSAGE_START begins accumulating text for agentName', () => {
    const event: SwarmEvent = {
      type: 'TEXT_MESSAGE_START', messageId: 'msg-1', role: 'assistant', agentName: 'leader',
    };
    const result = swarmReducer(initialState, event);
    // Should initialize the agentOutputs entry
    expect(result.agentOutputs['leader']).toBeDefined();
  });

  it('TEXT_MESSAGE_CONTENT appends delta to agent output', () => {
    const state: SwarmState = {
      ...initialState,
      agentOutputs: { leader: 'Hello ' },
      currentTextMessage: { messageId: 'msg-1', agentName: 'leader' },
    };
    const event: SwarmEvent = { type: 'TEXT_MESSAGE_CONTENT', messageId: 'msg-1', delta: 'World' };
    const result = swarmReducer(state, event);
    expect(result.agentOutputs['leader']).toBe('Hello World');
  });

  it('TEXT_MESSAGE_CONTENT for leader role accumulates into leaderReport', () => {
    const state: SwarmState = {
      ...initialState,
      leaderReport: '# Report\n',
      currentTextMessage: { messageId: 'msg-1', agentName: 'leader', isLeaderReport: true },
    };
    const event: SwarmEvent = { type: 'TEXT_MESSAGE_CONTENT', messageId: 'msg-1', delta: 'Details...' };
    const result = swarmReducer(state, event);
    expect(result.leaderReport).toBe('# Report\nDetails...');
  });

  it('TEXT_MESSAGE_END clears currentTextMessage', () => {
    const state: SwarmState = {
      ...initialState,
      currentTextMessage: { messageId: 'msg-1', agentName: 'leader' },
    };
    const event: SwarmEvent = { type: 'TEXT_MESSAGE_END', messageId: 'msg-1' };
    const result = swarmReducer(state, event);
    expect(result.currentTextMessage).toBeNull();
  });
});

describe('swarmReducer — AG-UI tool call events', () => {
  it('TOOL_CALL_START adds tool to activeTools', () => {
    const event: SwarmEvent = {
      type: 'TOOL_CALL_START',
      toolCallId: 'tc-1',
      toolCallName: 'task_update',
      agentName: 'worker-1',
    };
    const result = swarmReducer(initialState, event);
    expect(result.activeTools).toHaveLength(1);
    expect(result.activeTools[0]).toMatchObject({
      toolCallId: 'tc-1',
      toolName: 'task_update',
      agentName: 'worker-1',
      status: 'running',
    });
  });

  it('TOOL_CALL_ARGS accumulates args on active tool', () => {
    const state: SwarmState = {
      ...initialState,
      activeTools: [{ toolCallId: 'tc-1', toolName: 'task_update', agentName: 'w', status: 'running' }],
    };
    const event: SwarmEvent = {
      type: 'TOOL_CALL_ARGS', toolCallId: 'tc-1', delta: '{"taskId":"t1"}',
    };
    const result = swarmReducer(state, event);
    expect(result.activeTools[0].input).toBe('{"taskId":"t1"}');
  });

  it('TOOL_CALL_END marks tool args complete (no status change)', () => {
    const state: SwarmState = {
      ...initialState,
      activeTools: [{ toolCallId: 'tc-1', toolName: 'task_update', agentName: 'w', status: 'running' }],
    };
    const event: SwarmEvent = { type: 'TOOL_CALL_END', toolCallId: 'tc-1' };
    const result = swarmReducer(state, event);
    expect(result.activeTools[0].status).toBe('running');
  });

  it('TOOL_CALL_RESULT marks tool as complete', () => {
    const state: SwarmState = {
      ...initialState,
      activeTools: [{ toolCallId: 'tc-1', toolName: 'task_update', agentName: 'w', status: 'running' }],
    };
    const event: SwarmEvent = {
      type: 'TOOL_CALL_RESULT', toolCallId: 'tc-1', content: '{"success":true}',
    };
    const result = swarmReducer(state, event);
    expect(result.activeTools[0].status).toBe('complete');
    expect(result.activeTools[0].output).toBe('{"success":true}');
  });

  it('multiple tool calls accumulate independently', () => {
    let state = swarmReducer(initialState, {
      type: 'TOOL_CALL_START', toolCallId: 'tc-1', toolCallName: 'task_update', agentName: 'w',
    });
    state = swarmReducer(state, {
      type: 'TOOL_CALL_START', toolCallId: 'tc-2', toolCallName: 'inbox_send', agentName: 'w',
    });
    expect(state.activeTools).toHaveLength(2);

    state = swarmReducer(state, {
      type: 'TOOL_CALL_RESULT', toolCallId: 'tc-1', content: '{"success":true}',
    });
    expect(state.activeTools[0].status).toBe('complete');
    expect(state.activeTools[1].status).toBe('running');
  });
});

describe('swarmReducer — SWARM_CUSTOM events', () => {
  it('SWARM_TASK_CREATED adds task to tasks array', () => {
    const event: SwarmEvent = {
      type: 'SWARM_CUSTOM',
      name: 'SWARM_TASK_CREATED',
      value: { id: 't1', subject: 'Build API', status: 'pending' },
    };
    const result = swarmReducer(initialState, event);
    expect(result.tasks).toHaveLength(1);
    expect(result.tasks[0].id).toBe('t1');
  });

  it('SWARM_TASK_UPDATED normalizes incoming backend PascalCase status to frontend lowercase', () => {
    // SwarmToolFactory emits SWARM_TASK_UPDATED with the raw enum name ("Completed",
    // "InProgress", etc.). The reducer must normalize that to the frontend's
    // lowercase snake_case form so TaskBoard column filters keep matching.
    const state: SwarmState = {
      ...initialState,
      tasks: [{ id: 't1', subject: 'Build API', description: '', worker_role: 'dev', worker_name: 'w', status: 'pending', blocked_by: [], result: '' }],
    };
    const event: SwarmEvent = {
      type: 'SWARM_CUSTOM',
      name: 'SWARM_TASK_UPDATED',
      value: { taskId: 't1', status: 'Completed', agent: 'worker-1' },
    };
    const result = swarmReducer(state, event);
    expect(result.tasks[0].status).toBe('completed');
  });

  it('SWARM_TASK_UPDATED normalizes PascalCase InProgress to in_progress', () => {
    const state: SwarmState = {
      ...initialState,
      tasks: [{ id: 't1', subject: 'Build API', description: '', worker_role: 'dev', worker_name: 'w', status: 'pending', blocked_by: [], result: '' }],
    };
    const event: SwarmEvent = {
      type: 'SWARM_CUSTOM',
      name: 'SWARM_TASK_UPDATED',
      value: { taskId: 't1', status: 'InProgress', agent: 'worker-1' },
    };
    const result = swarmReducer(state, event);
    expect(result.tasks[0].status).toBe('in_progress');
  });

  it('SWARM_AGENT_SPAWNED adds agent to agents array', () => {
    const event: SwarmEvent = {
      type: 'SWARM_CUSTOM',
      name: 'SWARM_AGENT_SPAWNED',
      value: { name: 'researcher', role: 'Research Analyst', displayName: 'Research Analyst' },
    };
    const result = swarmReducer(initialState, event);
    expect(result.agents).toHaveLength(1);
    expect(result.agents[0].name).toBe('researcher');
  });

  it('SWARM_INBOX_MESSAGE adds to messages array', () => {
    const event: SwarmEvent = {
      type: 'SWARM_CUSTOM',
      name: 'SWARM_INBOX_MESSAGE',
      value: { sender: 'researcher', recipient: 'leader', content: 'Results attached' },
    };
    const result = swarmReducer(initialState, event);
    expect(result.messages).toHaveLength(1);
    expect(result.messages[0].sender).toBe('researcher');
    expect(result.messages[0].content).toBe('Results attached');
  });
});

describe('swarmReducer — dedup on replay', () => {
  it('swarmReducer_SWARM_TASK_CREATED_deduplicatesByTaskId', () => {
    const event: SwarmEvent = {
      type: 'SWARM_CUSTOM',
      name: 'SWARM_TASK_CREATED',
      value: { id: 't1', subject: 'Build API', status: 'pending' },
    };
    let state = swarmReducer(initialState, event);
    state = swarmReducer(state, event);
    expect(state.tasks).toHaveLength(1);
  });

  it('swarmReducer_SWARM_AGENT_SPAWNED_deduplicatesByAgentName', () => {
    const event: SwarmEvent = {
      type: 'SWARM_CUSTOM',
      name: 'SWARM_AGENT_SPAWNED',
      value: { name: 'worker-1', role: 'Analyst', displayName: 'Analyst' },
    };
    let state = swarmReducer(initialState, event);
    state = swarmReducer(state, event);
    expect(state.agents).toHaveLength(1);
  });

  it('swarmReducer_eventsSeed_withStateSnapshot_thenTaskCreated_noDuplicates', () => {
    const swarmId = 's1';
    const events: SwarmEvent[] = [
      {
        type: 'STATE_SNAPSHOT',
        snapshot: {
          phase: 'Executing',
          roundNumber: 1,
          tasks: [{ id: 't1', subject: 'Research', status: 'pending' }],
          agents: [{ name: 'worker-1', role: 'Analyst', displayName: 'Analyst' }],
          messages: [],
        },
      },
      {
        type: 'SWARM_CUSTOM',
        name: 'SWARM_TASK_CREATED',
        value: { id: 't1', subject: 'Research', status: 'pending' },
      },
      {
        type: 'SWARM_CUSTOM',
        name: 'SWARM_AGENT_SPAWNED',
        value: { name: 'worker-1', role: 'Analyst', displayName: 'Analyst' },
      },
    ];

    let state = multiSwarmReducer(initialMultiSwarmState, { type: 'swarm.add', swarmId });
    state = multiSwarmReducer(state, { type: 'swarm.eventsSeed', swarmId, events });
    expect(state.swarms[swarmId].tasks).toHaveLength(1);
    expect(state.swarms[swarmId].agents).toHaveLength(1);
  });
});

describe('swarmReducer — unknown events', () => {
  it('returns unchanged state for unknown event types', () => {
    const event: SwarmEvent = { type: 'UNKNOWN_EVENT' as never };
    const result = swarmReducer(initialState, event);
    expect(result).toBe(initialState);
  });
});

describe('multiSwarmReducer — AG-UI events', () => {
  it('dispatches events to correct swarm only', () => {
    let state = multiSwarmReducer(initialMultiSwarmState, { type: 'swarm.add', swarmId: 's1' });
    state = multiSwarmReducer(state, { type: 'swarm.add', swarmId: 's2' });
    state = multiSwarmReducer(state, {
      type: 'swarm.event', swarmId: 's1',
      event: {
        type: 'SWARM_CUSTOM', name: 'SWARM_TASK_CREATED',
        value: { id: 't1', subject: 'Task A', status: 'pending' },
      },
    });

    expect(state.swarms['s1'].tasks).toHaveLength(1);
    expect(state.swarms['s2'].tasks).toHaveLength(0);
  });

  it('RUN_FINISHED moves swarm to completedSwarmIds', () => {
    let state = multiSwarmReducer(initialMultiSwarmState, { type: 'swarm.add', swarmId: 's1' });
    state = multiSwarmReducer(state, {
      type: 'swarm.event', swarmId: 's1',
      event: { type: 'RUN_FINISHED', threadId: 's1', runId: 'r1' },
    });

    expect(state.activeSwarmIds).not.toContain('s1');
    expect(state.completedSwarmIds).toContain('s1');
  });

  it('multiSwarmReducer_swarmEventsSeed_rebuildsDerivedStateIdenticallyToLiveEvents', () => {
    // The contract for `swarm.eventsSeed` is "dispatch each event through the
    // per-swarm reducer in order, as if they had arrived live." This test pins
    // that contract by constructing a representative event sequence, replaying
    // it twice (once live, once via eventsSeed), and asserting the resulting
    // per-swarm state is deeply equal. If the contract drifts — for example,
    // someone introduces a separate "backfill mode" code path — this test fires.
    const swarmId = 's1';
    const events: SwarmEvent[] = [
      { type: 'STEP_STARTED', stepName: 'Planning' },
      {
        type: 'SWARM_CUSTOM',
        name: 'SWARM_TASK_CREATED',
        value: { id: 't1', subject: 'Research foundations', status: 'pending' },
      },
      {
        type: 'SWARM_CUSTOM',
        name: 'SWARM_TASK_UPDATED',
        value: { taskId: 't1', status: 'InProgress', agent: 'worker-1' },
      },
      {
        type: 'SWARM_CUSTOM',
        name: 'SWARM_AGENT_SPAWNED',
        value: { name: 'researcher', role: 'Research Analyst', displayName: 'Research Analyst' },
      },
      { type: 'TEXT_MESSAGE_START', messageId: 'msg-1', role: 'assistant', agentName: 'researcher' },
      { type: 'TEXT_MESSAGE_CONTENT', messageId: 'msg-1', delta: 'Hello ' },
      { type: 'TEXT_MESSAGE_CONTENT', messageId: 'msg-1', delta: 'world' },
      { type: 'TEXT_MESSAGE_END', messageId: 'msg-1' },
      { type: 'STEP_FINISHED', stepName: 'Planning' },
    ];

    // Run A: dispatch each event via individual `swarm.event` actions.
    let stateA = multiSwarmReducer(initialMultiSwarmState, { type: 'swarm.add', swarmId });
    for (const event of events) {
      stateA = multiSwarmReducer(stateA, { type: 'swarm.event', swarmId, event });
    }

    // Run B: dispatch the same events in a single `swarm.eventsSeed` action.
    let stateB = multiSwarmReducer(initialMultiSwarmState, { type: 'swarm.add', swarmId });
    stateB = multiSwarmReducer(stateB, { type: 'swarm.eventsSeed', swarmId, events });

    expect(stateB.swarms[swarmId]).toEqual(stateA.swarms[swarmId]);
  });

  it('eventsSeed with SWARM_INBOX_MESSAGE events populates messages', () => {
    const swarmId = 'inbox-1';
    const withSlot = multiSwarmReducer(initialMultiSwarmState, { type: 'swarm.add', swarmId });

    const events: SwarmEvent[] = [
      {
        type: 'SWARM_CUSTOM',
        name: 'SWARM_INBOX_MESSAGE',
        value: { sender: 'researcher', recipient: 'leader', content: 'findings attached' },
      },
      {
        type: 'SWARM_CUSTOM',
        name: 'SWARM_INBOX_MESSAGE',
        value: { sender: 'analyst', recipient: 'leader', content: 'data ready' },
      },
    ];

    const result = multiSwarmReducer(withSlot, { type: 'swarm.eventsSeed', swarmId, events });
    expect(result.swarms[swarmId].messages).toHaveLength(2);
    expect(result.swarms[swarmId].messages[0].sender).toBe('researcher');
    expect(result.swarms[swarmId].messages[1].sender).toBe('analyst');
  });

  it('swarm.eventsSeed is a no-op when the swarm is not yet in the store', () => {
    const events: SwarmEvent[] = [{ type: 'STEP_STARTED', stepName: 'Planning' }];
    const state = multiSwarmReducer(initialMultiSwarmState, {
      type: 'swarm.eventsSeed',
      swarmId: 'unknown',
      events,
    });
    expect(state).toBe(initialMultiSwarmState);
  });
});

describe('isThinking', () => {
  it('is true when phase is planning', () => expect(isThinking('planning')).toBe(true));
  it('is true when phase is executing', () => expect(isThinking('executing')).toBe(true));
  it('is true when phase is synthesizing', () => expect(isThinking('synthesizing')).toBe(true));
  it('is false when phase is complete', () => expect(isThinking('complete')).toBe(false));
  it('is false when phase is null', () => expect(isThinking(null)).toBe(false));
  it('is false when phase is awaiting_intervention (human-wait)', () =>
    expect(isThinking('awaiting_intervention')).toBe(false));
  it('is false when phase is needs_diagnosis (human-wait)', () =>
    expect(isThinking('needs_diagnosis')).toBe(false));
  it('is false when phase is awaiting_feedback (human-wait)', () =>
    expect(isThinking('awaiting_feedback')).toBe(false));
});

describe('shouldShowReportView', () => {
  it('returns true when report exists', () => {
    expect(shouldShowReportView('swarm-1', 'Some report', null)).toBe(true);
  });
  it('returns false when no swarm selected', () => {
    expect(shouldShowReportView(null, 'Some report', null)).toBe(false);
  });
  it('returns true when phase is complete', () => {
    expect(shouldShowReportView('swarm-1', null, 'complete')).toBe(true);
  });
  it('returns true when phase is failed', () => {
    expect(shouldShowReportView('swarm-1', null, 'failed')).toBe(true);
  });
  it('returns true when phase is cancelled', () => {
    expect(shouldShowReportView('swarm-1', null, 'cancelled')).toBe(true);
  });
  it('returns false when phase is executing (non-terminal, no report)', () => {
    expect(shouldShowReportView('swarm-1', null, 'executing')).toBe(false);
  });
  it('returns false when phase is awaiting_intervention (human-wait, no report yet)', () => {
    expect(shouldShowReportView('swarm-1', null, 'awaiting_intervention')).toBe(false);
  });
});

describe('normalizePhase', () => {
  it('maps backend PascalCase AwaitingIntervention to awaiting_intervention', () => {
    const event: SwarmEvent = { type: 'STEP_STARTED', stepName: 'AwaitingIntervention' };
    const result = swarmReducer(initialState, event);
    expect(result.phase).toBe('awaiting_intervention');
  });

  it('maps backend PascalCase NeedsDiagnosis to needs_diagnosis', () => {
    const event: SwarmEvent = { type: 'STEP_STARTED', stepName: 'NeedsDiagnosis' };
    const result = swarmReducer(initialState, event);
    expect(result.phase).toBe('needs_diagnosis');
  });

  it('maps backend Created to created', () => {
    const event: SwarmEvent = { type: 'STEP_STARTED', stepName: 'Created' };
    const result = swarmReducer(initialState, event);
    expect(result.phase).toBe('created');
  });

  it('treats legacy Starting as unknown (falls back to created)', () => {
    const event: SwarmEvent = { type: 'STEP_STARTED', stepName: 'Starting' };
    const result = swarmReducer(initialState, event);
    expect(result.phase).toBe('created');
  });

  it('treats legacy Suspended as unknown (falls back to created)', () => {
    const event: SwarmEvent = { type: 'STEP_STARTED', stepName: 'Suspended' };
    const result = swarmReducer(initialState, event);
    expect(result.phase).toBe('created');
  });

  it('leaves already-normalized phase strings unchanged', () => {
    const event: SwarmEvent = { type: 'STEP_STARTED', stepName: 'needs_diagnosis' };
    const result = swarmReducer(initialState, event);
    expect(result.phase).toBe('needs_diagnosis');
  });

  it('falls back to created when given an unknown value', () => {
    const event: SwarmEvent = { type: 'STEP_STARTED', stepName: 'garbage' };
    const result = swarmReducer(initialState, event);
    expect(result.phase).toBe('created');
  });
});
