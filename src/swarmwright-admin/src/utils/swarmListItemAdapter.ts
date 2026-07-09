// Adapter from API SwarmListItem to ReportListItem.
import type { SwarmListItem } from '../hooks/useSwarmList';

export type ReportStatus = 'running' | 'generating' | 'live' | 'saved' | 'suspended';

export interface ReportListItem {
  swarmId: string;
  title: string;
  timestamp: number;
  status: ReportStatus;
}

function truncateTitle(text: string, maxLen = 50): string {
  if (!text) return 'Untitled Report';
  return text.length <= maxLen ? text : text.slice(0, maxLen) + '...';
}

/**
 * Derive a ReportList status from a server swarm entry. Running swarms that are
 * mid-synthesis render as `generating`; everything else falls through the
 * `running` / `suspended` / `live` / `saved` mapping used by the existing UI.
 */
function deriveStatus(item: SwarmListItem): ReportStatus {
  const phase = (item.phase || '').toLowerCase();
  if (item.isRunning) {
    if (phase === 'synthesizing') return 'generating';
    return 'running';
  }
  if (phase === 'awaiting_intervention' || phase === 'needs_diagnosis') {
    return 'suspended';
  }
  return 'live';
}

/** Extract a human-readable title from a goal string. */
function titleFromGoal(goal: string, fallbackId: string): string {
  const firstLine = goal.split('\n').find((l) => l.trim() && !l.startsWith('#'))?.trim() ?? '';
  return truncateTitle(firstLine || `Session ${fallbackId.slice(0, 8)}...`);
}

/** Project the latest meaningful timestamp (completed > lastEvent > created) to an epoch ms. */
function pickTimestamp(item: SwarmListItem): number {
  const iso = item.completedAt ?? item.lastEventAt ?? item.createdAt;
  const parsed = iso ? Date.parse(iso) : NaN;
  return Number.isFinite(parsed) ? parsed : Date.now();
}

/**
 * Convert the server-authoritative `SwarmListItem[]` into the `ReportListItem[]`
 * shape the left-pane `ReportList` consumes. The adapter preserves the existing
 * ordering (running/generating -> suspended -> live/saved).
 */
export function swarmListItemAdapter(
  serverSwarms: SwarmListItem[],
): ReportListItem[] {
  const items: ReportListItem[] = [];

  for (const item of serverSwarms) {
    items.push({
      swarmId: item.swarmId,
      title: titleFromGoal(item.goal, item.swarmId),
      timestamp: pickTimestamp(item),
      status: deriveStatus(item),
    });
  }

  const priority = (s: ReportStatus): number => {
    if (s === 'running' || s === 'generating') return 0;
    if (s === 'suspended') return 1;
    return 2;
  };
  items.sort((a, b) => {
    const pa = priority(a.status);
    const pb = priority(b.status);
    if (pa !== pb) return pa - pb;
    return b.timestamp - a.timestamp;
  });

  return items;
}
