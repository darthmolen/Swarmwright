import type { Task, TaskStatus } from '../types/swarm';

export interface TaskPillBarProps {
  tasks: Task[];
  selectedTaskId: string | null;
  onSelect: (taskId: string) => void;
  onNavigate?: (swarmId: string) => void;
}

export function statusColorClass(status: TaskStatus): string {
  return `--${status}`;
}

function truncatePillText(text: string, maxLen = 30): string {
  if (text.length <= maxLen) return text;
  return text.slice(0, maxLen) + '...';
}

export function TaskPillBar({ tasks, selectedTaskId, onSelect, onNavigate }: TaskPillBarProps) {
  if (tasks.length === 0) return null;

  return (
    <div className="task-pill-bar">
      {tasks.map((task) => {
        const label = `${task.worker_name}:${task.subject}`;
        const truncated = truncatePillText(label);
        const isSelected = task.id === selectedTaskId;

        const pillClasses = [
          'task-pill',
          `task-pill${statusColorClass(task.status)}`,
          isSelected ? 'task-pill--selected' : '',
        ]
          .filter(Boolean)
          .join(' ');

        return (
          <div key={task.id} className="task-pill-group">
            {onNavigate && task.swarm_id && (
              <button
                type="button"
                className={`task-status-icon task-status-icon${statusColorClass(task.status)}`}
                data-testid={`task-status-${task.id}`}
                onClick={() => onNavigate(task.swarm_id!)}
                title="View report"
              >
                &#128193;
              </button>
            )}
            <button
              type="button"
              className={pillClasses}
              data-testid={`task-pill-${task.id}`}
              onClick={() => onSelect(task.id)}
            >
              {truncated}
            </button>
          </div>
        );
      })}
    </div>
  );
}
