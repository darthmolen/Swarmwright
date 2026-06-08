import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { ReportRightPanel } from '../components/ReportRightPanel';
import type { Task } from '../types/swarm';

// Capture props passed to the mocked CopilotKit components so tests can assert them.
const copilotChatProps: Array<Record<string, unknown>> = [];
const copilotKitProps: Array<Record<string, unknown>> = [];

// Mock CopilotKit components to avoid rendering the actual chat UI
vi.mock('@copilotkit/react-core', () => ({
  CopilotKit: ({ children, ...rest }: { children: React.ReactNode } & Record<string, unknown>) => {
    copilotKitProps.push(rest);
    return <div data-testid="copilotkit">{children}</div>;
  },
}));
vi.mock('@copilotkit/react-ui', () => ({
  CopilotChat: (props: Record<string, unknown>) => {
    copilotChatProps.push(props);
    return <div data-testid="copilot-chat">Chat</div>;
  },
}));
vi.mock('@copilotkit/react-ui/styles.css', () => ({}));

vi.mock('../auth/useAuthToken', () => ({
  useAuthToken: () => ({ getToken: () => Promise.resolve(null) }),
}));

function makeTask(overrides: Partial<Task> = {}): Task {
  return {
    id: 'task-1',
    subject: 'Deploy API',
    description: 'Deploy the REST API to staging environment',
    worker_role: 'devops',
    worker_name: 'infra-agent',
    status: 'completed',
    blocked_by: [],
    result: 'Deployment successful',
    ...overrides,
  };
}

const defaultProps = {
  swarmId: 'swarm-1',
  tasks: [] as Task[],
  getAuthHeaders: vi.fn().mockResolvedValue({ Authorization: 'Bearer test' }),
};

beforeEach(() => {
  vi.clearAllMocks();
  copilotChatProps.length = 0;
  copilotKitProps.length = 0;
  // Default fetch mock: return chatAvailable with synthesis agent.
  globalThis.fetch = vi.fn().mockResolvedValue({
    ok: true,
    json: async () => ({
      agents: {
        synthesis: { name: 'synthesis', description: 'Synthesis agent' },
      },
      chatAvailable: true,
      mode: 'sse',
    }),
  }) as unknown as typeof fetch;
});

describe('ReportRightPanel — TaskPillBar + TaskDetailDrawer wiring', () => {
  // 1. TaskPillBar renders when tasks are present
  it('renders TaskPillBar when tasks are present', () => {
    const tasks = [
      makeTask({ id: 'task-1', worker_name: 'agent-a', subject: 'Do stuff' }),
      makeTask({ id: 'task-2', worker_name: 'agent-b', subject: 'Other stuff' }),
    ];
    render(<ReportRightPanel {...defaultProps} tasks={tasks} />);

    expect(screen.getByTestId('task-pill-task-1')).toBeInTheDocument();
    expect(screen.getByTestId('task-pill-task-2')).toBeInTheDocument();
  });

  // 2. TaskPillBar does not render when tasks are empty
  it('does not render TaskPillBar when tasks are empty', () => {
    render(<ReportRightPanel {...defaultProps} tasks={[]} />);

    expect(screen.queryByTestId('task-pill-task-1')).not.toBeInTheDocument();
    // The pill bar container itself should not be present
    expect(document.querySelector('.task-pill-bar')).toBeNull();
  });

  // 3. Clicking a pill shows TaskDetailDrawer for that task
  it('shows TaskDetailDrawer when a pill is clicked', () => {
    const tasks = [
      makeTask({ id: 'task-1', subject: 'Deploy API', description: 'Deploy the REST API' }),
    ];
    render(<ReportRightPanel {...defaultProps} tasks={tasks} />);

    // Initially no drawer
    expect(screen.queryByText('Deploy the REST API')).not.toBeInTheDocument();

    // Click the pill
    fireEvent.click(screen.getByTestId('task-pill-task-1'));

    // Drawer should appear with the task description
    expect(screen.getByText('Deploy the REST API')).toBeInTheDocument();
  });

  // 4. Clicking the same pill again closes the drawer (toggle behavior)
  it('closes TaskDetailDrawer when the same pill is clicked again', () => {
    const tasks = [
      makeTask({ id: 'task-1', subject: 'Deploy API', description: 'Deploy the REST API' }),
    ];
    render(<ReportRightPanel {...defaultProps} tasks={tasks} />);

    // Click pill to open
    fireEvent.click(screen.getByTestId('task-pill-task-1'));
    expect(screen.getByText('Deploy the REST API')).toBeInTheDocument();

    // Click same pill to close
    fireEvent.click(screen.getByTestId('task-pill-task-1'));
    expect(screen.queryByText('Deploy the REST API')).not.toBeInTheDocument();
  });

  // 5. TaskDetailDrawer shows correct task details
  it('shows the correct task details in the drawer', () => {
    const tasks = [
      makeTask({
        id: 'task-1',
        subject: 'Deploy API',
        description: 'Deploy the REST API',
        worker_name: 'infra-agent',
        worker_role: 'devops',
        status: 'completed',
        result: 'Deployment successful',
      }),
    ];
    render(<ReportRightPanel {...defaultProps} tasks={tasks} />);

    fireEvent.click(screen.getByTestId('task-pill-task-1'));

    // Subject is shown in the drawer header
    expect(screen.getByRole('heading', { name: 'Deploy API' })).toBeInTheDocument();
    // Worker info shown inside the drawer meta section
    const workerSpan = document.querySelector('.task-detail-drawer__worker');
    expect(workerSpan).not.toBeNull();
    expect(workerSpan!.textContent).toContain('infra-agent');
    expect(workerSpan!.textContent).toContain('devops');
    // Status
    expect(screen.getByTestId('task-detail-status')).toHaveTextContent('completed');
    // Result
    expect(screen.getByTestId('task-detail-result')).toHaveTextContent('Deployment successful');
  });

  // 6. CopilotKit chat remains visible when drawer is open
  it('keeps CopilotKit chat visible when TaskDetailDrawer is open', async () => {
    const tasks = [makeTask({ id: 'task-1' })];
    render(<ReportRightPanel {...defaultProps} tasks={tasks} />);

    // Wait for auth headers to resolve and CopilotKit to render
    const copilotkit = await screen.findByTestId('copilotkit');
    expect(copilotkit).toBeInTheDocument();

    // Open drawer
    fireEvent.click(screen.getByTestId('task-pill-task-1'));

    // CopilotKit still present
    expect(screen.getByTestId('copilotkit')).toBeInTheDocument();
  });

  // 7. Clicking a different pill switches the drawer to that task
  it('switches drawer to different task when different pill is clicked', () => {
    const tasks = [
      makeTask({ id: 'task-1', subject: 'Deploy API', description: 'Deploy description' }),
      makeTask({ id: 'task-2', subject: 'Run Tests', description: 'Test description' }),
    ];
    render(<ReportRightPanel {...defaultProps} tasks={tasks} />);

    // Click first task
    fireEvent.click(screen.getByTestId('task-pill-task-1'));
    expect(screen.getByText('Deploy description')).toBeInTheDocument();
    expect(screen.queryByText('Test description')).not.toBeInTheDocument();

    // Click second task
    fireEvent.click(screen.getByTestId('task-pill-task-2'));
    expect(screen.getByText('Test description')).toBeInTheDocument();
    expect(screen.queryByText('Deploy description')).not.toBeInTheDocument();
  });

  // 8. Close button on drawer closes it
  it('closes drawer when close button is clicked', () => {
    const tasks = [
      makeTask({ id: 'task-1', description: 'Deploy description' }),
    ];
    render(<ReportRightPanel {...defaultProps} tasks={tasks} />);

    fireEvent.click(screen.getByTestId('task-pill-task-1'));
    expect(screen.getByText('Deploy description')).toBeInTheDocument();

    // Click close button on the drawer
    fireEvent.click(screen.getByTestId('task-detail-close'));
    expect(screen.queryByText('Deploy description')).not.toBeInTheDocument();
  });

  // 9. Selected pill has selected class
  it('applies selected class to the active pill', () => {
    const tasks = [
      makeTask({ id: 'task-1' }),
      makeTask({ id: 'task-2' }),
    ];
    render(<ReportRightPanel {...defaultProps} tasks={tasks} />);

    fireEvent.click(screen.getByTestId('task-pill-task-1'));

    expect(screen.getByTestId('task-pill-task-1').className).toContain('task-pill--selected');
    expect(screen.getByTestId('task-pill-task-2').className).not.toContain('task-pill--selected');
  });
});

describe('ReportRightPanel — CopilotKit integration', () => {
  // The agent identity is resolved through <CopilotKit agent={selectedAgent}>
  // (the provider), not through an `agentId` prop on <CopilotChat>. An earlier
  // revision tried passing agentId on CopilotChat directly; that was reverted
  // intentionally. See the next two tests for the canonical assertions.

  // Bug: <CopilotListeners> (rendered inside <CopilotKit>) calls useAgent with the
  // agentId resolved from React context, which falls back to "default" unless the
  // <CopilotKit> provider sets `agent`. Without this prop we get:
  // "useAgent: Agent 'default' not found after runtime sync."
  it('passes agent prop on <CopilotKit> matching selectedAgent (fixes default agent error)', async () => {
    render(<ReportRightPanel {...defaultProps} />);
    await screen.findByTestId('copilot-chat');

    const latest = copilotKitProps[copilotKitProps.length - 1];
    expect(latest.agent).toBe('synthesis');
  });

  // When the user clicks a different agent pill, <CopilotKit>'s `agent` prop must
  // update so CopilotListeners subscribes to the new agent.
  it('updates agent prop on <CopilotKit> when a different pill is clicked', async () => {
    globalThis.fetch = vi.fn().mockResolvedValue({
      ok: true,
      json: async () => ({
        agents: {
          synthesis: { name: 'synthesis', description: '' },
          skeptic: { name: 'skeptic', description: '' },
        },
        chatAvailable: true,
        mode: 'sse',
      }),
    }) as unknown as typeof fetch;

    render(<ReportRightPanel {...defaultProps} />);
    await screen.findByTestId('copilot-chat');

    fireEvent.click(screen.getByText('skeptic'));

    const latest = copilotKitProps[copilotKitProps.length - 1];
    expect(latest.agent).toBe('skeptic');
  });

  // Bug: When chatAvailable:false, rendering CopilotKit crashes because no agents exist.
  it('does NOT render CopilotKit when chatAvailable is false', async () => {
    // Override default fetch mock to return chatAvailable:false
    globalThis.fetch = vi.fn().mockResolvedValue({
      ok: true,
      json: async () => ({ agents: {}, chatAvailable: false }),
    }) as unknown as typeof fetch;

    render(<ReportRightPanel {...defaultProps} />);

    // Wait for the async info fetch to resolve and component to update.
    await waitFor(() => {
      expect(screen.getByText(/chat.*(unavailable|will be available)/i)).toBeInTheDocument();
    });

    // CopilotKit must NOT be rendered — prevents "Agent default not found" crash.
    expect(screen.queryByTestId('copilotkit')).not.toBeInTheDocument();
    expect(screen.queryByTestId('copilot-chat')).not.toBeInTheDocument();
  });
});
