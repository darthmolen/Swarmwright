export interface ReportHeaderProps {
  swarmId: string;
  onBack: () => void;
  onCopy: () => void;
  onRefresh: () => void;
}

export function ReportHeader({ swarmId, onBack, onCopy, onRefresh }: ReportHeaderProps) {
  return (
    <header className="app-header">
      <button type="button" className="back-button" onClick={onBack}>
        ← Dashboard
      </button>
      <h1>Report — {swarmId.slice(0, 8)}</h1>
      <div className="modal-actions">
        <button
          type="button"
          className="refresh-button"
          onClick={onRefresh}
          title="Refresh artifact list"
        >
          ↻ Refresh
        </button>
        <button type="button" className="copy-button" onClick={onCopy}>
          Copy
        </button>
      </div>
    </header>
  );
}
