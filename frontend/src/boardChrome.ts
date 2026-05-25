export type BoardChromeRepository = {
  provider: string;
  webUrl?: string | null;
  implementationProfile?: string | null;
};

export type BoardChromeBoard = {
  id: string;
  name: string;
  repository?: BoardChromeRepository | null;
  repositorySyncState?: string | null;
  providerCapabilities?: string[] | null;
};

export type TimelineChromeEvent = {
  id: string;
  workItemId?: string | null;
  kind: string;
  title: string;
  message?: string | null;
  createdAt: string;
};

export type TimelineLane = 'Card' | 'Implementation PR' | 'Cleanup' | 'Preview' | 'Pipeline';

export type TimelineFlowNode = TimelineChromeEvent & {
  lane: TimelineLane;
};

export type TimelineFlowRow = {
  id: string;
  topic: string;
  taskKey?: string | null;
  nodes: TimelineFlowNode[];
};

export const timelineLanes: TimelineLane[] = ['Card', 'Implementation PR', 'Cleanup', 'Preview', 'Pipeline'];

export function boardSyncLabel(board: BoardChromeBoard): string {
  if (board.repositorySyncState) return board.repositorySyncState;
  if (!board.repository) return 'Preview only';
  return board.repository.provider.toLowerCase() === 'github' ? 'Synced to GitHub' : 'Synced to provider';
}

export function boardRepositoryUrl(board: BoardChromeBoard): string | null {
  return board.repository?.webUrl?.trim() || null;
}

export function canSyncBoardToProvider(board: BoardChromeBoard): boolean {
  return !board.repository && Boolean(board.providerCapabilities?.includes('sync-github'));
}

export function timelineLaneForKind(kind: string): TimelineLane {
  const normalized = kind.toLowerCase();
  if (normalized.includes('cleanup')) return 'Cleanup';
  if (normalized.includes('preview')) return 'Preview';
  if (normalized.includes('pipeline')) return 'Pipeline';
  if (normalized.includes('pull') || normalized.includes('pr') || normalized.includes('implementation') || normalized.includes('commit') || normalized.includes('branch')) {
    return 'Implementation PR';
  }

  return 'Card';
}

export function buildTimelineFlow(events: TimelineChromeEvent[]): TimelineFlowRow[] {
  const rows = new Map<string, TimelineFlowRow>();
  for (const event of [...events].sort((left, right) => Date.parse(left.createdAt) - Date.parse(right.createdAt))) {
    const rowId = event.workItemId || `event:${event.id}`;
    const row = rows.get(rowId) ?? {
      id: rowId,
      topic: event.workItemId ? topicFromTimelineEvent(event) : event.title || 'Board events',
      taskKey: taskKeyFromTimelineEvent(event),
      nodes: []
    };
    if (row.topic === row.taskKey) {
      row.topic = topicFromTimelineEvent(event);
    }
    row.nodes.push({ ...event, lane: timelineLaneForKind(event.kind) });
    rows.set(rowId, row);
  }

  return [...rows.values()].sort((left, right) => {
    const leftTime = Date.parse(left.nodes[0]?.createdAt ?? '');
    const rightTime = Date.parse(right.nodes[0]?.createdAt ?? '');
    return (Number.isFinite(leftTime) ? leftTime : 0) - (Number.isFinite(rightTime) ? rightTime : 0);
  });
}

export function filterTimelineFlowRows(rows: TimelineFlowRow[], query: string): TimelineFlowRow[] {
  const normalized = query.trim().toLowerCase();
  if (!normalized) return rows;

  return rows.filter((row) => {
    const rowText = `${row.topic} ${row.taskKey ?? ''}`.toLowerCase();
    if (rowText.includes(normalized)) return true;
    return row.nodes.some((node) => `${node.title} ${node.kind} ${node.message ?? ''}`.toLowerCase().includes(normalized));
  });
}

export function containedWheelScrollTop(currentScrollTop: number, deltaY: number, maxScrollTop: number): number {
  if (maxScrollTop <= 0) return currentScrollTop;
  return Math.min(Math.max(currentScrollTop + deltaY, 0), maxScrollTop);
}

export function publicApplicationUrls(values: string[] | null | undefined): string[] {
  const seen = new Set<string>();
  return (values ?? []).filter((value) => {
    const normalized = value.trim();
    if (!normalized || seen.has(normalized.toLowerCase())) return false;
    try {
      const parsed = new URL(normalized);
      if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') return false;
    } catch {
      return false;
    }

    seen.add(normalized.toLowerCase());
    return true;
  });
}

export function applicationUrlLabel(url: string): string {
  try {
    const parsed = new URL(url);
    return parsed.pathname === '/' ? parsed.host : `${parsed.host}${parsed.pathname}`;
  } catch {
    return url;
  }
}

export function safeMarkdownHref(value: string): string | null {
  const trimmed = value.trim();
  if (!trimmed || /[\u0000-\u001f\u007f]/.test(trimmed)) return null;
  if (trimmed.startsWith('/') && !trimmed.startsWith('//')) return trimmed;
  try {
    const parsed = new URL(trimmed);
    if (parsed.protocol === 'https:') return parsed.toString();
    if (parsed.protocol === 'http:' && ['localhost', '127.0.0.1', '::1'].includes(parsed.hostname)) return parsed.toString();
  } catch {
    return null;
  }

  return null;
}

function taskKeyFromTimelineEvent(event: TimelineChromeEvent): string | null {
  return /\bTASK-\d+\b/i.exec(`${event.title} ${event.message ?? ''}`)?.[0].toUpperCase() ?? null;
}

function topicFromTimelineEvent(event: TimelineChromeEvent): string {
  const taskKey = taskKeyFromTimelineEvent(event);
  const titleWithoutKey = taskKey ? event.title.replace(new RegExp(`\\b${taskKey}\\b`, 'i'), '').trim() : event.title.trim();
  if (titleWithoutKey) return titleWithoutKey;

  const message = (event.message ?? '').trim();
  const patterns = [
    /^Created\s+(.+?)\.?$/i,
    /^Deleted\s+(.+?)\.?$/i,
    /^Closed\s+(.+?)\.?$/i,
    /^Moved\s+(.+?)\s+to\s+.+?\.?$/i,
    /^AI plan ready for\s+(.+?)\.?$/i,
    /^Pull request ready for\s+(.+?)\.?$/i
  ];
  for (const pattern of patterns) {
    const match = pattern.exec(message);
    if (match?.[1]) return trimTopic(match[1]);
  }

  return taskKey ?? (event.title || 'Board events');
}

function trimTopic(value: string): string {
  return value.replace(/\.$/, '').trim();
}
