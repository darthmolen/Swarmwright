import { describe, it, expect } from 'vitest';
import {
  multiSwarmReducer,
  initialMultiSwarmState,
} from '../hooks/useSwarmState';
import { hydrateTasksIntoSwarm } from '../utils/hydrateTasksIntoSwarm';
import type { Task } from '../types/swarm';

function makeTask(overrides: Partial<Task> = {}): Task {
  return {
    id: 'task-1',
    subject: 'Build API',
    description: 'Build the REST API',
    worker_role: 'backend-dev',
    worker_name: 'agent-1',
    status: 'completed',
    blocked_by: [],
    result: 'Done',
    ...overrides,
  };
}

describe('hydrateTasksIntoSwarm', () => {
  it('returns an array of swarm.event actions with SWARM_TASK_CREATED events', () => {
    const tasks = [
      makeTask({ id: 't1', subject: 'Task A' }),
      makeTask({ id: 't2', subject: 'Task B' }),
    ];
    const actions = hydrateTasksIntoSwarm('swarm-abc', tasks);

    expect(actions).toHaveLength(2);
    expect(actions[0]).toEqual({
      type: 'swarm.event',
      swarmId: 'swarm-abc',
      event: {
        type: 'SWARM_CUSTOM',
        name: 'SWARM_TASK_CREATED',
        value: { ...tasks[0], swarm_id: 'swarm-abc' },
      },
    });
  });

  it('returns empty array when tasks is undefined', () => {
    const actions = hydrateTasksIntoSwarm('swarm-abc', undefined);
    expect(actions).toEqual([]);
  });

  it('returns empty array when tasks is null', () => {
    const actions = hydrateTasksIntoSwarm('swarm-abc', null as unknown as undefined);
    expect(actions).toEqual([]);
  });

  it('returns empty array when tasks is empty', () => {
    const actions = hydrateTasksIntoSwarm('swarm-abc', []);
    expect(actions).toEqual([]);
  });
});

describe('task hydration through multiSwarmReducer', () => {
  it('hydrates tasks into a swarm that has no prior swarm.add', () => {
    const task = makeTask({ id: 't1', subject: 'Cold-load task' });
    const actions = hydrateTasksIntoSwarm('cold-swarm', [task]);

    let state = initialMultiSwarmState;
    for (const action of actions) {
      state = multiSwarmReducer(state, action);
    }

    expect(state.swarms['cold-swarm']).toBeDefined();
    expect(state.swarms['cold-swarm'].tasks).toHaveLength(1);
    expect(state.swarms['cold-swarm'].tasks[0].id).toBe('t1');
  });

  it('hydrates multiple tasks preserving order', () => {
    const tasks = [
      makeTask({ id: 't1', subject: 'First' }),
      makeTask({ id: 't2', subject: 'Second' }),
      makeTask({ id: 't3', subject: 'Third' }),
    ];
    const actions = hydrateTasksIntoSwarm('swarm-1', tasks);

    let state = initialMultiSwarmState;
    for (const action of actions) {
      state = multiSwarmReducer(state, action);
    }

    expect(state.swarms['swarm-1'].tasks).toHaveLength(3);
    expect(state.swarms['swarm-1'].tasks.map((t) => t.subject)).toEqual([
      'First', 'Second', 'Third',
    ]);
  });

  it('does not duplicate tasks if already present from a prior swarm.add', () => {
    const task = makeTask({ id: 't1', subject: 'Existing task' });

    let state = multiSwarmReducer(initialMultiSwarmState, {
      type: 'swarm.add', swarmId: 'swarm-1',
    });
    state = multiSwarmReducer(state, {
      type: 'swarm.event', swarmId: 'swarm-1',
      event: {
        type: 'SWARM_CUSTOM',
        name: 'SWARM_TASK_CREATED',
        value: { ...task, swarm_id: 'swarm-1' },
      },
    });
    expect(state.swarms['swarm-1'].tasks).toHaveLength(1);

    // Hydrate same task again — reducer deduplicates by task id
    const actions = hydrateTasksIntoSwarm('swarm-1', [task]);
    for (const action of actions) {
      state = multiSwarmReducer(state, action);
    }

    expect(state.swarms['swarm-1'].tasks).toHaveLength(1);
  });
});
