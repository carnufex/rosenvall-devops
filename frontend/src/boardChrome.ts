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
  publicHostname?: string | null;
  publicApp?: {
    status?: string | null;
    url?: string | null;
    hostname?: string | null;
    message?: string | null;
  } | null;
};

export type GitHubRepositoryCreationIntegration = {
  installationId: number;
  accountLogin?: string | null;
  canCreateRepositories?: boolean | null;
  requiresUserAuthorizationForRepositoryCreation?: boolean | null;
  hasUserAuthorization?: boolean | null;
  authorizedGitHubLogin?: string | null;
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

export type PreviewLifecycleStepState = 'done' | 'active' | 'pending' | 'blocked';

export type PreviewLifecycleStep = {
  key: string;
  title: string;
  description: string;
  state: PreviewLifecycleStepState;
};

export type PreviewTerminalLineChrome = {
  createdAt: string;
  stream: string;
  message: string;
};

export type PreviewStepLogChrome = {
  key: string;
  title?: string | null;
  description?: string | null;
  state?: string | null;
  terminalLines?: PreviewTerminalLineChrome[] | null;
};

export type PreviewStepDisplay = PreviewLifecycleStep & {
  terminalLines: PreviewTerminalLineChrome[];
  logCount: number;
};

export type PreviewStepSource = {
  status?: string | null;
  failureReason?: string | null;
  terminalLines?: PreviewTerminalLineChrome[] | null;
  stepLogs?: PreviewStepLogChrome[] | null;
};

export type ActivityCommentChrome = {
  workItemId: string;
  author: string;
  kind: string;
  body: string;
  createdAt?: string | null;
};

const previewLifecycleDefinitions = [
  ['Implementing', 'Implementing preview source', 'Codex generates the React/Tailwind files.'],
  ['Applying', 'Applying Kubernetes resources', 'The API submits namespace, ConfigMap, Deployment, Service and route.'],
  ['Provisioning', 'Waiting for pod readiness', 'Kubernetes starts the preview pod and health checks it.'],
  ['Running', 'Running', 'The deployment is available and the demo link is enabled.']
] as const;

const previewStepDisplayDefinitions = [
  ['source', 'Implementing preview source', 'Codex generates the React/Tailwind files.'],
  ['apply', 'Applying Kubernetes resources', 'The API submits namespace, ConfigMap, Deployment, Service and route.'],
  ['readiness', 'Waiting for pod readiness', 'Kubernetes starts the preview pod and health checks it.'],
  ['running', 'Running', 'The deployment is available and the demo link is enabled.']
] as const;

export function boardSyncLabel(board: BoardChromeBoard): string {
  if (board.repositorySyncState) return board.repositorySyncState;
  if (!board.repository) return 'Preview only';
  return board.repository.provider.toLowerCase() === 'github' ? 'Synced to GitHub' : 'Synced to provider';
}

export function boardRepositoryUrl(board: BoardChromeBoard): string | null {
  return board.repository?.webUrl?.trim() || null;
}

export function boardPublicAppUrl(board: BoardChromeBoard): string | null {
  if (!board.publicApp || board.publicApp.status !== 'Running') return null;
  return board.publicApp.url?.trim() || (board.publicApp.hostname ? `https://${board.publicApp.hostname.trim()}` : null);
}

export function boardPublicAppStatusLabel(board: BoardChromeBoard): string | null {
  const status = board.publicApp?.status?.trim();
  if (!status && !board.publicHostname) return null;
  if (status === 'Running') return null;
  if (status === 'Deploying' || status === 'Queued') return 'App deploying';
  if (status === 'Failed') return 'App failed';
  return 'App not deployed';
}

export function isPreviewTerminalLive(status: string | null | undefined): boolean {
  return ['Queued', 'Implementing', 'Applying', 'Provisioning'].includes((status ?? '').trim());
}

export function previewStatusMessage(status: string | null | undefined): string {
  if (status === 'Running') return 'Preview is healthy and ready.';
  if (status === 'Failed') return 'Preview setup failed. Review the reason below and retry after fixing the issue.';
  if (status === 'Implementing') return 'Codex is generating React/Tailwind source from the approved plan.';
  if (status === 'Applying') return 'Applying Kubernetes resources.';
  if (status === 'Stopped') return 'Preview is stopped. The reviewed source is kept so it can be recreated.';
  return 'Kubernetes resources are applied. Waiting for a healthy preview pod.';
}

export function previewDisplayMessage(status: string | null | undefined, message?: string | null, phase?: string | null): string {
  if (status === 'Stopped') return previewStatusMessage(status);
  return message?.trim() || phase?.trim() || previewStatusMessage(status);
}

export function buildPreviewLifecycleSteps(status: string | null | undefined, failureReason?: string | null): PreviewLifecycleStep[] {
  const normalizedStatus = (status ?? '').trim();
  const failedIndex = ['ImplementationFailed', 'ServerRestart', 'ManifestMissing'].includes(failureReason ?? '')
    ? 0
    : failureReason === 'ApplyFailed'
      ? 1
      : 2;
  const current = normalizedStatus === 'Failed'
    ? failedIndex
    : Math.max(0, previewLifecycleDefinitions.findIndex(([key]) => key === normalizedStatus));

  return previewLifecycleDefinitions.map(([key, title, description], index) => ({
    key,
    title,
    description,
    state: normalizedStatus === 'Running' || normalizedStatus === 'Stopped'
      ? 'done'
      : normalizedStatus === 'Failed' && index === current
        ? 'blocked'
        : index < current
          ? 'done'
          : index === current
            ? 'active'
            : 'pending'
  }));
}

export function previewStepLogsForDisplay(preview: PreviewStepSource): PreviewStepDisplay[] {
  const fallbackStates = buildPreviewLifecycleSteps(preview.status, preview.failureReason).map((step) => step.state);
  if (preview.stepLogs?.length) {
    const byKey = new Map(preview.stepLogs.map((step) => [normalizePreviewStepKey(step.key), step]));
    return previewStepDisplayDefinitions.map(([key, title, description], index) => {
      const step = byKey.get(key);
      const terminalLines = step?.terminalLines ?? [];
      return {
        key,
        title: step?.title?.trim() || title,
        description: step?.description?.trim() || description,
        state: normalizePreviewStepState(step?.state) ?? fallbackStates[index] ?? 'pending',
        terminalLines,
        logCount: terminalLines.length
      };
    });
  }

  const legacyLines = preview.terminalLines ?? [];
  return previewStepDisplayDefinitions.map(([key, title, description], index) => {
    const terminalLines = key === 'source' && legacyLines.length > 0
      ? [
          {
            createdAt: legacyLines[0]?.createdAt ?? new Date(0).toISOString(),
            stream: 'system',
            message: 'Legacy combined log. Older preview attempts stored one shared terminal tail.'
          },
          ...legacyLines
        ]
      : [];
    return {
      key,
      title,
      description,
      state: fallbackStates[index] ?? 'pending',
      terminalLines,
      logCount: terminalLines.length
    };
  });
}

export function defaultPreviewStepKey(steps: PreviewStepDisplay[]): string {
  return steps.find((step) => step.state === 'blocked')?.key ??
    steps.find((step) => step.state === 'active')?.key ??
    [...steps].reverse().find((step) => step.terminalLines.length > 0)?.key ??
    steps[0]?.key ??
    'source';
}

export function dedupeGeneratedActivityComments<T extends ActivityCommentChrome>(comments: readonly T[]): T[] {
  const seen = new Set<string>();
  return comments.filter((comment) => {
    if (comment.author.toLowerCase() !== 'rosenvall ai' || comment.kind.toLowerCase() !== 'result') {
      return true;
    }

    const key = `${comment.workItemId}\n${comment.author}\n${comment.kind}\n${comment.body}`;
    if (seen.has(key)) return false;
    seen.add(key);
    return true;
  });
}

function normalizePreviewStepKey(value: string | null | undefined): string {
  const normalized = (value ?? '').trim().toLowerCase();
  return previewStepDisplayDefinitions.some(([key]) => key === normalized) ? normalized : 'source';
}

function normalizePreviewStepState(value: string | null | undefined): PreviewLifecycleStepState | null {
  const normalized = (value ?? '').trim().toLowerCase();
  if (normalized === 'done' || normalized === 'active' || normalized === 'pending' || normalized === 'blocked') return normalized;
  if (normalized === 'failed' || normalized === 'failure') return 'blocked';
  return null;
}

export function canSyncBoardToProvider(board: BoardChromeBoard): boolean {
  return !board.repository && Boolean(board.providerCapabilities?.includes('sync-github'));
}

export function canCreateRepositoryInInstallation(integration: GitHubRepositoryCreationIntegration | null | undefined): boolean {
  return false;
}

export function repositoryCreatePermissionMessage(integration: GitHubRepositoryCreationIntegration | null | undefined): string | null {
  return 'GitHub repository creation is disabled. Link an existing repository instead.';
}

export type GitHubUserAuthorizationResult = {
  kind: 'success' | 'error';
  message: string;
};

export function githubUserAuthorizationResultFromUrl(url: string): GitHubUserAuthorizationResult | null {
  const parsed = new URL(url);
  if (parsed.searchParams.get('githubUserAuthorization') === 'connected') {
    return { kind: 'success', message: 'GitHub user authorization connected.' };
  }

  const error = parsed.searchParams.get('githubUserAuthorizationError');
  if (error) {
    return { kind: 'error', message: error };
  }

  return null;
}

export function apiUnavailableBannerMessage(error: unknown): string | null {
  const message = error instanceof Error ? error.message : String(error ?? '');
  const normalized = message.toLowerCase();
  if (normalized.includes('memory pressured')) {
    return 'API is memory pressured. Implementation cannot start until capacity recovers.';
  }

  if (normalized.includes('503') ||
    normalized.includes('service unavailable') ||
    normalized.includes('failed to fetch') ||
    normalized.includes('networkerror') ||
    normalized.includes('api unavailable')) {
    return 'API is restarting or unavailable. Latest known board state is still shown.';
  }

  return null;
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
