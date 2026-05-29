export type BoardChromeRepository = {
  provider: string;
  name?: string | null;
  owner?: string | null;
  defaultBranch?: string | null;
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
  accountType?: string | null;
  canCreateRepositories?: boolean | null;
  requiresUserAuthorizationForRepositoryCreation?: boolean | null;
  hasUserAuthorization?: boolean | null;
  authorizedGitHubLogin?: string | null;
  repositoryCreationMessage?: string | null;
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

export type WorkItemTabKey = 'overview' | 'ai' | 'preview' | 'pull-request' | 'logs';

export type WorkItemModalTab = {
  key: WorkItemTabKey;
  label: string;
};

export type WorkItemAutosaveStatus = 'idle' | 'dirty' | 'saving' | 'saved' | 'error';

export function workItemAutosaveStatusLabel(status: WorkItemAutosaveStatus) {
  switch (status) {
    case 'dirty': return 'Unsaved changes';
    case 'saving': return 'Saving...';
    case 'saved': return 'Saved';
    case 'error': return 'Autosave failed';
    default: return 'All changes saved';
  }
}

export type WorkItemTabRun = {
  id: string;
  title: string;
  status?: string | null;
  updatedAt?: string | null;
  steps: PreviewStepDisplay[];
};

export const timelineLanes: TimelineLane[] = ['Card', 'Implementation PR', 'Cleanup', 'Preview', 'Pipeline'];

export const workItemModalTabs: WorkItemModalTab[] = [
  { key: 'overview', label: 'Overview' },
  { key: 'ai', label: 'AI' },
  { key: 'preview', label: 'Preview' },
  { key: 'pull-request', label: 'Pull request' },
  { key: 'logs', label: 'Logs' }
];

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

export type DevelopmentChrome = {
  repository: string;
  branch: string;
  pullRequestUrl?: string | null;
  checksStatus: string;
  pullRequestProvider?: string | null;
  pullRequestNumber?: number | null;
  pullRequestState?: string | null;
  pullRequestApprovedAt?: string | null;
  pullRequestApprovedBy?: string | null;
  pullRequestMergedAt?: string | null;
  pullRequestFailure?: string | null;
};

export type RepositoryHostingChrome = {
  canCreateRepositories?: boolean | null;
  localGitEnabled?: boolean | null;
  localGitAvailable?: boolean | null;
  localGitMessage?: string | null;
};

export type PullRequestDiffFileChrome = {
  path: string;
  status?: string | null;
  additions?: number | null;
  deletions?: number | null;
};

export type PullRequestReviewCommentChrome = {
  id: string;
  filePath: string;
  status: string;
  side?: string | null;
  lineNumber?: number | null;
};

export type AiPlanReviewCommentChrome = {
  id?: string | null;
  aiRunId: string;
  anchorKey?: string | null;
  status: string;
};

export type AiPlanReviewBlock = {
  anchorKey: string;
  kind: 'heading' | 'paragraph' | 'list-item';
  text: string;
};

export type OverviewPreviewChrome = {
  status?: string | null;
  message?: string | null;
  phase?: string | null;
};

export type OverviewDeliverySummaryItem = {
  key: 'app' | 'repository' | 'preview' | 'pull-request' | 'logs';
  label: string;
  value: string;
  actionLabel: string;
  href?: string | null;
  tab?: WorkItemTabKey | null;
  disabled?: boolean;
};

export type LocalPullRequestApprovalStateChrome = {
  canApprove: boolean;
  status: 'reviewable' | 'merged' | 'blocked' | 'failed' | 'approving';
  message: string;
};

export type ContinuousDiffLine = {
  id: string;
  text: string;
  kind: 'meta' | 'hunk' | 'context' | 'add' | 'delete';
  oldLine?: number | null;
  newLine?: number | null;
  side?: 'old' | 'new' | 'both' | null;
  commentable: boolean;
};

export type ContinuousDiffSection = {
  path: string;
  file: PullRequestDiffFileChrome | null;
  lines: ContinuousDiffLine[];
};

export type ContinuousDiff = {
  sections: ContinuousDiffSection[];
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

export function boardRepositorySummary(board: BoardChromeBoard): string {
  const repository = board.repository;
  if (!repository) return 'No repository linked';
  const owner = repository.owner?.trim();
  const name = repository.name?.trim();
  const repositoryName = owner && name ? `${owner}/${name}` : name || repository.provider;
  const branch = repository.defaultBranch?.trim();
  const profile = repository.implementationProfile?.trim();
  return [
    `${repository.provider} / ${repositoryName}`,
    branch || null,
    profile ? profileLabel(profile) : null
  ].filter(Boolean).join(' - ');
}

function profileLabel(profile: string): string {
  if (profile === 'react-preview') return 'React preview';
  if (profile === 'gitops-homelab') return 'GitOps';
  if (profile === 'code-repo') return 'Code repository';
  return profile;
}

export function isLocalGitDevelopmentRecord(development: DevelopmentChrome): boolean {
  return development.pullRequestProvider?.trim().toLowerCase() === 'localgit';
}

export function pullRequestDisplayLabel(development: DevelopmentChrome): string {
  if (!isLocalGitDevelopmentRecord(development)) return 'Pull request';
  return `Local pull request${development.pullRequestNumber ? ` #${development.pullRequestNumber}` : ''}`;
}

export function approvePullRequestActionLabel(development: DevelopmentChrome): string {
  return isLocalGitDevelopmentRecord(development) ? 'Merge local PR and deploy app' : 'Approve PR';
}

export function unresolvedReviewCommentCount(comments: readonly Pick<PullRequestReviewCommentChrome, 'status'>[]): number {
  return comments.filter((comment) => comment.status?.toLowerCase() !== 'resolved').length;
}

export function canApprovePullRequestWithComments(comments: readonly Pick<PullRequestReviewCommentChrome, 'status'>[]): boolean {
  return unresolvedReviewCommentCount(comments) === 0;
}

export function reviewCommentCountsByFile(comments: readonly Pick<PullRequestReviewCommentChrome, 'filePath' | 'status'>[]): Record<string, { total: number; unresolved: number }> {
  return comments.reduce<Record<string, { total: number; unresolved: number }>>((counts, comment) => {
    const key = comment.filePath;
    counts[key] ??= { total: 0, unresolved: 0 };
    counts[key].total += 1;
    if (comment.status?.toLowerCase() !== 'resolved') {
      counts[key].unresolved += 1;
    }
    return counts;
  }, {});
}

export function splitAiPlanReviewBlocks(plan: string | null | undefined): AiPlanReviewBlock[] {
  const blocks: AiPlanReviewBlock[] = [];
  for (const rawLine of (plan ?? '').split(/\r?\n/)) {
    const line = rawLine.trim();
    if (!line) continue;
    const heading = line.match(/^#{1,6}\s+(.+)$/);
    const listItem = line.match(/^((?:[-*]\s+)|(?:\d+\.\s+))(.+)$/);
    blocks.push({
      anchorKey: `block-${blocks.length + 1}`,
      kind: heading ? 'heading' : listItem ? 'list-item' : 'paragraph',
      text: heading ? heading[1].trim() : listItem ? (listItem[1].trim().match(/^\d+\.$/) ? `${listItem[1]}${listItem[2]}`.trim() : listItem[2].trim()) : line
    });
  }

  return blocks;
}

export function unresolvedAiPlanReviewCommentCount(comments: readonly Pick<AiPlanReviewCommentChrome, 'aiRunId' | 'status'>[], aiRunId?: string | null): number {
  return comments.filter((comment) =>
    (!aiRunId || comment.aiRunId === aiRunId) &&
    comment.status?.toLowerCase() !== 'resolved').length;
}

export function canApproveAiPlanWithComments(aiRunId: string | null | undefined, comments: readonly Pick<AiPlanReviewCommentChrome, 'aiRunId' | 'status'>[]): boolean {
  return !!aiRunId && unresolvedAiPlanReviewCommentCount(comments, aiRunId) === 0;
}

export function planReviewCommentCountsByRun(comments: readonly Pick<AiPlanReviewCommentChrome, 'aiRunId' | 'status'>[]): Record<string, { total: number; unresolved: number }> {
  return comments.reduce<Record<string, { total: number; unresolved: number }>>((counts, comment) => {
    const key = comment.aiRunId;
    counts[key] ??= { total: 0, unresolved: 0 };
    counts[key].total += 1;
    if (comment.status?.toLowerCase() !== 'resolved') {
      counts[key].unresolved += 1;
    }
    return counts;
  }, {});
}

export function buildOverviewDeliverySummary({ board, development, preview }: {
  board: BoardChromeBoard | null;
  development?: DevelopmentChrome | null;
  preview?: OverviewPreviewChrome | null;
}): OverviewDeliverySummaryItem[] {
  const appUrl = board ? boardPublicAppUrl(board) : null;
  const repositoryUrl = board ? boardRepositoryUrl(board) : null;
  const pullRequestLabel = development?.pullRequestUrl ? pullRequestDisplayLabel(development) : 'No pull request';
  const pullRequestState = development
    ? isLocalGitDevelopmentRecord(development) && development.pullRequestApprovedAt
      ? 'merged'
      : development.pullRequestState ?? 'open'
    : null;

  return [
    {
      key: 'app',
      label: 'App',
      value: board ? appUrl ? 'App running' : boardPublicAppStatusLabel(board) ?? 'App not deployed' : 'No board loaded',
      actionLabel: appUrl ? 'Go to app' : 'Not deployed',
      href: appUrl,
      disabled: !appUrl
    },
    {
      key: 'repository',
      label: 'Repository',
      value: board ? boardRepositorySummary(board) : 'No repository',
      actionLabel: repositoryUrl ? 'Go to repository' : 'RDO managed',
      href: repositoryUrl,
      disabled: !repositoryUrl
    },
    {
      key: 'preview',
      label: 'Preview',
      value: preview ? previewDisplayMessage(preview.status ?? '', preview.message, preview.phase) : 'No preview',
      actionLabel: preview ? 'Open preview' : 'Not started',
      tab: 'preview',
      disabled: !preview
    },
    {
      key: 'pull-request',
      label: 'Pull request',
      value: development?.pullRequestUrl ? `${pullRequestLabel}${pullRequestState ? ` ${pullRequestState}` : ''}` : 'No pull request',
      actionLabel: development?.pullRequestUrl ? 'Open PR' : 'Not created',
      tab: 'pull-request',
      disabled: !development?.pullRequestUrl
    },
    {
      key: 'logs',
      label: 'Logs',
      value: 'Runner output',
      actionLabel: 'Open logs',
      tab: 'logs'
    }
  ];
}

export function buildLocalPullRequestApprovalState(source: {
  state?: string | null;
  pullRequestState?: string | null;
  pullRequestApprovedAt?: string | null;
  pullRequestApprovedBy?: string | null;
  pullRequestMergedAt?: string | null;
  pullRequestFailure?: string | null;
  canApprove?: boolean | null;
  approvalStatus?: string | null;
  approvalMessage?: string | null;
  unresolvedComments?: number | null;
  pending?: boolean | null;
}): LocalPullRequestApprovalStateChrome {
  if (source.pending) {
    return { canApprove: false, status: 'approving', message: 'Merging local PR and deploying app...' };
  }

  const backendStatus = normalizeApprovalStatus(source.approvalStatus);
  if (source.pullRequestApprovedAt || backendStatus === 'merged') {
    const actor = source.pullRequestApprovedBy || 'RDO';
    return { canApprove: false, status: 'merged', message: source.approvalMessage || `Merged and deployed by ${actor}.` };
  }

  const unresolved = source.unresolvedComments ?? 0;
  if (unresolved > 0) {
    return {
      canApprove: false,
      status: 'blocked',
      message: `Resolve ${unresolved} review comment${unresolved === 1 ? '' : 's'} before approving PR.`
    };
  }

  if (source.approvalMessage || backendStatus) {
    return {
      canApprove: !!source.canApprove,
      status: backendStatus ?? (source.canApprove ? 'reviewable' : 'blocked'),
      message: source.approvalMessage || (source.canApprove ? 'Ready to merge and deploy.' : 'Pull request cannot be approved.')
    };
  }

  const state = (source.state ?? source.pullRequestState ?? 'open').trim().toLowerCase();
  if (source.pullRequestMergedAt || state === 'merged') {
    return {
      canApprove: false,
      status: 'failed',
      message: source.pullRequestFailure
        ? `Local pull request was merged, but deployment failed: ${source.pullRequestFailure}`
        : 'Local pull request is merged, but RDO has not marked the app as deployed.'
    };
  }

  if (state !== 'open') {
    return {
      canApprove: false,
      status: 'blocked',
      message: `Local pull request is ${state} in Forgejo and cannot be approved by RDO.`
    };
  }

  return { canApprove: true, status: 'reviewable', message: 'Ready to merge and deploy.' };
}

function normalizeApprovalStatus(value: string | null | undefined): LocalPullRequestApprovalStateChrome['status'] | null {
  const normalized = (value ?? '').trim().toLowerCase();
  return normalized === 'reviewable' || normalized === 'merged' || normalized === 'blocked' || normalized === 'failed' || normalized === 'approving'
    ? normalized
    : null;
}

export function shouldRenderPlanReferenceActivity(comment: Pick<ActivityCommentChrome, 'kind' | 'body'>): boolean {
  if (comment.kind === 'Plan') return true;
  if (!comment.kind || comment.kind.toLowerCase() !== 'result') return false;
  const normalized = comment.body.trim().replace(/\s+/g, ' ');
  return /^Created plan #\d+:/i.test(normalized) || /^AI needs input for plan #\d+:/i.test(normalized);
}

export function parseUnifiedDiffForContinuousReview(diff: string, files: readonly PullRequestDiffFileChrome[]): ContinuousDiff {
  const fileByPath = new Map(files.map((file) => [file.path, file]));
  const sections: ContinuousDiffSection[] = [];
  let current: ContinuousDiffSection | null = null;
  let oldLine: number | null = null;
  let newLine: number | null = null;

  const ensureSection = (path: string) => {
    current = { path, file: fileByPath.get(path) ?? null, lines: [] };
    sections.push(current);
  };

  const linePath = (line: string) => {
    const match = /^diff --git a\/(.+?) b\/(.+)$/.exec(line);
    return match?.[2] ?? null;
  };

  diff.replace(/\r\n/g, '\n').split('\n').forEach((line, index) => {
    const nextPath = linePath(line);
    if (nextPath) {
      ensureSection(nextPath);
      oldLine = null;
      newLine = null;
    }

    if (!current) {
      ensureSection(files[0]?.path ?? 'diff');
    }

    const hunk = /^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@/.exec(line);
    if (hunk) {
      oldLine = Number(hunk[1]);
      newLine = Number(hunk[2]);
      current!.lines.push({ id: `${current!.path}:${index}`, text: line, kind: 'hunk', commentable: false });
      return;
    }

    if (line.startsWith('+') && !line.startsWith('+++')) {
      const renderedLine = newLine;
      current!.lines.push({ id: `${current!.path}:new:${renderedLine ?? index}`, text: line, kind: 'add', newLine: renderedLine, side: 'new', commentable: renderedLine !== null });
      if (newLine !== null) newLine += 1;
      return;
    }

    if (line.startsWith('-') && !line.startsWith('---')) {
      const renderedLine = oldLine;
      current!.lines.push({ id: `${current!.path}:old:${renderedLine ?? index}`, text: line, kind: 'delete', oldLine: renderedLine, side: 'old', commentable: renderedLine !== null });
      if (oldLine !== null) oldLine += 1;
      return;
    }

    if (oldLine !== null && newLine !== null && (line.startsWith(' ') || line === '')) {
      const renderedOldLine = oldLine;
      const renderedNewLine = newLine;
      current!.lines.push({ id: `${current!.path}:both:${renderedNewLine}:${index}`, text: line, kind: 'context', oldLine: renderedOldLine, newLine: renderedNewLine, side: 'both', commentable: true });
      oldLine += 1;
      newLine += 1;
      return;
    }

    current!.lines.push({ id: `${current!.path}:${index}`, text: line, kind: 'meta', commentable: false });
  });

  for (const file of files) {
    if (!sections.some((section) => section.path === file.path)) {
      sections.push({ path: file.path, file, lines: [] });
    }
  }

  return { sections };
}

export function boardDeleteCleanupMessage(boardName: string): string {
  return `Delete ${boardName}, clean Kubernetes runtime resources, and delete board-owned Local Git repositories? GitHub repositories and PRs will remain.`;
}

export function localGitProviderState(repositories: RepositoryHostingChrome): { visible: boolean; available: boolean; message: string } {
  const visible = repositories.localGitEnabled === true;
  const available = visible && (repositories.localGitAvailable ?? repositories.canCreateRepositories) === true;
  const message = repositories.localGitMessage?.trim() ||
    (available
      ? 'Local Git is ready.'
      : 'Local Git is enabled, but Forgejo is not available yet.');
  return { visible, available, message };
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

    const key = `${comment.workItemId}\n${comment.author.toLowerCase()}\n${comment.kind.toLowerCase()}\n${normalizedGeneratedActivityBody(comment.body)}`;
    if (seen.has(key)) return false;
    seen.add(key);
    return true;
  });
}

function normalizedGeneratedActivityBody(body: string): string {
  const normalized = body.trim().replace(/\s+/g, ' ');
  const plan = normalized.match(/^Created plan #(\d+):\s*(.+?)\.?$/i);
  if (plan) return `created-plan:${plan[1]}:${plan[2].trim().replace(/\.$/, '').toLowerCase()}`;
  return normalized;
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
  return Boolean(integration?.canCreateRepositories && (!integration.requiresUserAuthorizationForRepositoryCreation || integration.hasUserAuthorization));
}

export function repositoryCreatePermissionMessage(integration: GitHubRepositoryCreationIntegration | null | undefined): string | null {
  if (!integration) return 'Select a GitHub installation before creating a repository.';
  if (integration.canCreateRepositories) return null;
  if (integration.repositoryCreationMessage) return integration.repositoryCreationMessage;
  if (integration.requiresUserAuthorizationForRepositoryCreation && !integration.hasUserAuthorization) {
    return `Authorize GitHub user access before creating repositories under ${integration.accountLogin ?? 'this account'}.`;
  }
  if (integration.hasUserAuthorization && integration.authorizedGitHubLogin && integration.accountLogin && integration.authorizedGitHubLogin.toLowerCase() !== integration.accountLogin.toLowerCase()) {
    return `Connected as ${integration.authorizedGitHubLogin}; repository creation under ${integration.accountLogin} is blocked.`;
  }
  return 'You do not have permission to create repositories for this GitHub installation.';
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
