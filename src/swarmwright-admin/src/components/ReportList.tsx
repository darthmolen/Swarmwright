import type { ReportListItem } from '../utils/swarmListItemAdapter';

interface ReportListProps {
  items: ReportListItem[];
  activeId: string | null;
  onSelect: (swarmId: string) => void;
  onHydrate?: (swarmId: string) => void;
  onResume?: (swarmId: string) => void;
}

export function ReportList({ items, activeId, onSelect, onHydrate, onResume }: ReportListProps) {
  return (
    <div className="report-list">
      {items.length === 0 ? (
        <div className="report-list-empty">No sessions yet</div>
      ) : (
        items.map((item) => (
          <div
            key={item.swarmId}
            className={[
              'report-list-item',
              `report-list-item--${item.status}`,
              item.swarmId === activeId ? 'report-list-item--active' : '',
            ].filter(Boolean).join(' ')}
          >
            <button
              type="button"
              className="report-list-item__nav"
              data-testid={`report-nav-${item.swarmId}`}
              onClick={() => onSelect(item.swarmId)}
              title="View report"
            >
              <span className={`report-status-dot report-status-dot--${item.status}`}>
                {item.status === 'live' ? '\u{1F4C1}' : '\u25CF'}
              </span>
            </button>
            <button
              type="button"
              className="report-list-item__content"
              data-testid={`report-hydrate-${item.swarmId}`}
              onClick={() => onHydrate ? onHydrate(item.swarmId) : onSelect(item.swarmId)}
            >
              <span className="report-list-title">{item.title}</span>
              <span className="report-list-meta">
                {item.swarmId.slice(0, 8)} · {new Date(item.timestamp).toLocaleDateString()}
              </span>
            </button>
            {item.status === 'suspended' && onResume && (
              <button
                className="report-list-resume-btn"
                onClick={(e) => { e.stopPropagation(); onResume(item.swarmId); }}
              >
                Resume
              </button>
            )}
          </div>
        ))
      )}
    </div>
  );
}
