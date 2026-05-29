import React from 'react';
import { User, UserManager, WebStorageStateStore } from 'oidc-client-ts';
import { createApiClient, type AuthSession } from './apiClient';
import { apiUnavailableBannerMessage, applicationUrlLabel, approvePullRequestActionLabel, boardDeleteCleanupMessage, boardPublicAppStatusLabel, boardPublicAppUrl, boardRepositoryUrl, boardSyncLabel, buildLocalPullRequestApprovalState, buildOverviewDeliverySummary, buildTimelineFlow, canApproveAiPlanWithComments, canApprovePullRequestWithComments, canCreateRepositoryInInstallation, canSyncBoardToProvider, containedWheelScrollTop, dedupeGeneratedActivityComments, defaultPreviewStepKey, filterTimelineFlowRows, githubUserAuthorizationResultFromUrl, isLocalGitDevelopmentRecord, isPreviewTerminalLive, localGitProviderState, parseUnifiedDiffForContinuousReview, planReviewCommentCountsByRun, previewDisplayMessage, previewStepLogsForDisplay, publicApplicationUrls, pullRequestDisplayLabel, repositoryCreatePermissionMessage, reviewCommentCountsByFile, safeMarkdownHref, shouldRenderPlanReferenceActivity, splitAiPlanReviewBlocks, unresolvedAiPlanReviewCommentCount, unresolvedReviewCommentCount, workItemAutosaveStatusLabel, workItemModalTabs, type AiPlanReviewBlock, type ContinuousDiffLine, type TimelineLane, type WorkItemAutosaveStatus, type WorkItemTabKey, type WorkItemTabRun } from './boardChrome';
import { implementationActionState, isImplementationRunPendingStatus, repositoryRunPresentation, workflowForRepositoryProfile, type ImplementationWorkflow } from './implementationRetry';
import { extractPlanQuestions, formatPlanQuestionAnswers, type PlanQuestion } from './planQuestions';
import {
  Activity,
  Bot,
  Boxes,
  CheckCircle2,
  ExternalLink,
  GitPullRequest,
  Github,
  History,
  KeyRound,
  LayoutDashboard,
  LogOut,
  Maximize2,
  PanelLeft,
  Play,
  Plus,
  RefreshCw,
  Save,
  Search,
  Settings,
  Sparkles,
  SquareTerminal,
  Trash2,
  Users,
  X
} from 'lucide-react';
import {
  CollisionDetection,
  DndContext,
  DragEndEvent,
  DragOverlay,
  DragStartEvent,
  PointerSensor,
  pointerWithin,
  rectIntersection,
  useDroppable,
  useSensor,
  useSensors
} from '@dnd-kit/core';
import {
  SortableContext,
  useSortable,
  verticalListSortingStrategy
} from '@dnd-kit/sortable';
import { CSS } from '@dnd-kit/utilities';

type View = 'dashboard' | 'board' | 'timeline' | 'gitops' | 'ai' | 'environment' | 'configuration' | 'teams' | 'settings';

type LoadShellOptions = {
  silentBusy?: boolean;
};

type Workspace = {
  id: string;
  name: string;
  environmentName: string;
  region: string;
  activeProjects: number;
  openPullRequests: number;
  successfulAiImplementations: number;
  computeUsagePercent: number;
};

type Board = {
  id: string;
  workspaceId: string;
  name: string;
  columns: BoardColumn[];
  repository?: RepositoryDto | null;
  repositories?: BoardRepositoryDto[] | null;
  teamAccess?: BoardTeamAccessDto[] | null;
  gitOpsSettings?: BoardGitOpsSettingsDto | null;
  aiContext?: BoardAiContextDto | null;
  repositorySyncState?: string | null;
  providerCapabilities?: string[] | null;
  implementationWorkflow?: ImplementationWorkflow | string | null;
  publicHostname?: string | null;
  publicApp?: BoardPublicAppDto | null;
};

type BoardPublicAppDto = {
  boardId: string;
  hostname: string;
  url: string;
  namespace: string;
  resourceName: string;
  status: 'NotDeployed' | 'Queued' | 'Deploying' | 'Running' | 'Failed' | string;
  sourceWorkItemId?: string | null;
  sourcePreviewId?: string | null;
  sourceImplementationRunId?: string | null;
  sourcePullRequestUrl?: string | null;
  sourceBranch?: string | null;
  commitSha?: string | null;
  createdAt: string;
  updatedAt: string;
  lastDeployedAt?: string | null;
  failureReason?: string | null;
  message?: string | null;
};

type BoardGitOpsSettingsDto = {
  boardId: string;
  allowedPaths: string[];
  argoNamespace: string;
  argoApplicationSelector: string;
};

type BoardAiContextDto = {
  boardId: string;
  instructions: string;
  enabledSkills: string[];
  askWhenUncertain: boolean;
  agentInstructions?: string | null;
};

type RepositoryDto = {
  id: string;
  provider: string;
  name: string;
  remoteUrl: string;
  webUrl?: string | null;
  defaultBranch: string;
  createdAt: string;
  owner?: string | null;
  implementationProfile: 'react-preview' | 'code-repo' | 'unity' | string;
  implementationWorkflow?: ImplementationWorkflow | string | null;
};

type BoardRepositoryDto = {
  boardId: string;
  repositoryId: string;
  isPrimary: boolean;
  implementationProfile: 'react-preview' | 'code-repo' | 'unity' | string;
  repository: RepositoryDto;
  profile?: RepositoryProfileDto | null;
  implementationWorkflow?: ImplementationWorkflow | string | null;
};

type BoardTeamAccessDto = {
  boardId: string;
  teamId: string;
  teamName: string;
  role: string;
};

type BoardColumn = {
  name: string;
  items: WorkItemSummary[];
};

type WorkItemSummary = {
  id: string;
  key: string;
  type: string;
  title: string;
  status: string;
  assignee?: string | null;
  priority: string;
  commentCount: number;
  aiStatus?: string | null;
  pullRequestUrl?: string | null;
  sortOrder: number;
  previewUrl?: string | null;
  parentWorkItemId?: string | null;
  parentKey?: string | null;
  rootWorkItemId?: string | null;
  rootKey?: string | null;
  hierarchyPath?: string | null;
  isBug?: boolean;
  childCount?: number;
  doneChildCount?: number;
  blockedChildCount?: number;
  openPullRequestChildCount?: number;
};

type WorkItemDetail = {
  item: WorkItemSummary;
  description: string;
  comments: CommentDto[];
  preview?: PreviewDto | null;
  development?: DevelopmentDto | null;
  implementationRuns?: ImplementationRunDto[] | null;
  repositoryCleanupRuns?: RepositoryCleanupRunDto[] | null;
  aiSession?: AiSessionDto | null;
  previewEvents?: PreviewEventDto[] | null;
  previewImplementationRunsAwaitingRecovery?: AiRun[] | null;
  aiPlanReviewComments?: AiPlanReviewCommentDto[] | null;
  parent?: WorkItemSummary | null;
  children?: WorkItemSummary[] | null;
  ancestors?: WorkItemSummary[] | null;
  descendants?: WorkItemSummary[] | null;
  epicRuns?: EpicRunDto[] | null;
  epicGoalRuns?: EpicGoalRunDto[] | null;
  boardContext?: {
    boardId: string;
    repositoryProfile: string;
    gitOpsSettings?: BoardGitOpsSettingsDto | null;
    aiContext?: BoardAiContextDto | null;
    implementationWorkflow?: ImplementationWorkflow | string | null;
    publicHostname?: string | null;
  } | null;
};

type EpicRunChildDto = {
  workItemId: string;
  workItemKey: string;
  workItemTitle: string;
  agentRole: string;
  status: string;
  aiRunId?: string | null;
  implementationRunId?: string | null;
  summary?: string | null;
  updatedAt?: string | null;
};

type EpicRunDto = {
  id: string;
  rootWorkItemId: string;
  rootWorkItemKey: string;
  rootWorkItemTitle: string;
  status: string;
  actor: string;
  createdAt: string;
  updatedAt: string;
  children: EpicRunChildDto[];
  summary?: string | null;
  failureReason?: string | null;
};

type EpicGoalRunDto = {
  id: string;
  rootWorkItemId: string;
  epicRunId?: string | null;
  status: string;
  actor: string;
  createdAt: string;
  updatedAt: string;
  summary?: string | null;
  failureReason?: string | null;
};

type CommentDto = {
  id: string;
  workItemId: string;
  author: string;
  kind: string;
  body: string;
  createdAt: string;
};

type PreviewDto = {
  id: string;
  workItemId: string;
  url: string;
  image: string;
  status: string;
  expiresAt: string;
  staticHtml?: string | null;
  namespace?: string | null;
  resourceName?: string | null;
  phase?: string | null;
  message?: string | null;
  lastCheckedAt?: string | null;
  podName?: string | null;
  failureReason?: string | null;
  failureLog?: string | null;
  sourceFiles?: Array<{ key: string; path: string; content: string }> | null;
  terminalLines?: PreviewTerminalLineDto[] | null;
  stepLogs?: PreviewStepLogDto[] | null;
};

type PreviewTerminalLineDto = {
  createdAt: string;
  stream: string;
  message: string;
};

type PreviewStepLogDto = {
  key: string;
  title?: string | null;
  description?: string | null;
  state?: string | null;
  startedAt?: string | null;
  completedAt?: string | null;
  terminalLines?: PreviewTerminalLineDto[] | null;
};

type ImplementationRunDto = {
  id: string;
  repositoryId: string;
  workItemId: string;
  aiRunId: string;
  workItemKey: string;
  workItemTitle: string;
  status: 'Queued' | 'Cloning' | 'Inspecting' | 'Implementing' | 'Validating' | 'Testing' | 'Pushing' | 'PullRequestReady' | 'Failed' | string;
  branch: string;
  pullRequestUrl?: string | null;
  commitSha?: string | null;
  failureReason?: string | null;
  createdAt: string;
  updatedAt: string;
  terminalLines?: PreviewTerminalLineDto[] | null;
  jobName?: string | null;
  podName?: string | null;
  lastCondition?: string | null;
  lastEventSummary?: string | null;
  runKind?: string | null;
  pullRequestProvider?: string | null;
  pullRequestNumber?: number | null;
  pullRequestState?: string | null;
  pullRequestMergedAt?: string | null;
};

type RepositoryCleanupRunDto = {
  id: string;
  repositoryId: string;
  workItemId: string;
  sourceImplementationRunId: string;
  workItemKey: string;
  workItemTitle: string;
  status: 'Queued' | 'Cloning' | 'Implementing' | 'Validating' | 'Pushing' | 'PullRequestReady' | 'Failed' | string;
  branch: string;
  sourcePullRequestUrl: string;
  cleanupPullRequestUrl?: string | null;
  commitSha?: string | null;
  failureReason?: string | null;
  sourcePullRequestState?: string | null;
  createdAt: string;
  updatedAt: string;
  terminalLines?: PreviewTerminalLineDto[] | null;
  jobName?: string | null;
  podName?: string | null;
  lastCondition?: string | null;
  lastEventSummary?: string | null;
  adopted?: boolean;
  mergedAt?: string | null;
  verifiedAt?: string | null;
  verificationFailure?: string | null;
};

type GitOpsApplicationStatusDto = {
  name: string;
  namespace: string;
  syncStatus: string;
  healthStatus: string;
  revision?: string | null;
  message: string;
  url?: string | null;
  updatedAt?: string | null;
  applicationUrls?: string[] | null;
};

type GitOpsApplicationsResponseDto = {
  applications: GitOpsApplicationStatusDto[];
  message?: string | null;
};

type AiSessionDto = {
  id: string;
  workItemId: string;
  provider: string;
  model: string;
  providerSessionId?: string | null;
  status: string;
  lastPromptAt: string;
  repositoryId?: string | null;
  lastRunId?: string | null;
  contextSummary?: string | null;
  reasoningEffort?: string | null;
};

type UserDto = {
  id: string;
  displayName: string;
  email: string;
  subject: string;
  avatarUrl?: string | null;
};

type TeamMemberDto = {
  userId: string;
  role: string;
  displayName?: string | null;
  email?: string | null;
  status?: string | null;
};

type TeamDto = {
  id: string;
  name: string;
  members: TeamMemberDto[];
  createdAt: string;
};

type GitHubIntegrationDto = {
  id: string;
  installationId: number;
  accountLogin: string;
  accountType: string;
  status: string;
  repositoriesCount: number;
  installedBy: string;
  createdAt: string;
  canCreateRepositories?: boolean;
  repositoryCreatorTeamIds?: string[] | null;
  canManageRepositoryCreationPolicy?: boolean;
  requiresUserAuthorizationForRepositoryCreation?: boolean;
  hasUserAuthorization?: boolean;
  authorizedGitHubLogin?: string | null;
  repositoryCreationMessage?: string | null;
};

type BoardSecretDto = {
  id: string;
  boardId: string;
  repositoryId?: string | null;
  key: string;
  createdAt: string;
  updatedAt: string;
  lastUsedAt?: string | null;
};

type PreviewEnvironmentDto = {
  id: string;
  workItemId?: string | null;
  workItemKey: string;
  workItemTitle: string;
  url: string;
  namespace: string;
  resourceName: string;
  image: string;
  status: string;
  expiresAt: string;
  phase?: string | null;
  message?: string | null;
  lastCheckedAt?: string | null;
  podName?: string | null;
  failureReason?: string | null;
  failureLog?: string | null;
};

type PreviewEventDto = {
  id: string;
  workItemId?: string | null;
  workItemKey: string;
  workItemTitle: string;
  eventType: string;
  namespace?: string | null;
  url?: string | null;
  actor: string;
  message: string;
  createdAt: string;
};

type PipelineStatusDto = {
  id: string;
  workItemId?: string | null;
  workItemKey: string;
  workItemTitle: string;
  stage: string;
  status: string;
  message: string;
  updatedAt: string;
};

type MetricsDto = {
  boardId?: string | null;
  tokensUsed: number;
  codeAdded: number;
  codeDeleted: number;
  pipelineRuns: number;
};

type TimelineEventDto = {
  id: string;
  boardId?: string | null;
  repositoryId?: string | null;
  workItemId?: string | null;
  kind: string;
  title: string;
  message: string;
  actor: string;
  url?: string | null;
  createdAt: string;
};

type DevelopmentDto = {
  repository: string;
  branch: string;
  pullRequestUrl?: string | null;
  checksStatus: string;
  pullRequestApprovedBy?: string | null;
  pullRequestApprovedAt?: string | null;
  repositoryId?: string | null;
  pullRequestProvider?: string | null;
  pullRequestNumber?: number | null;
  pullRequestState?: string | null;
  pullRequestMergedAt?: string | null;
  pullRequestFailure?: string | null;
};

type PullRequestDiffFileDto = {
  path: string;
  status?: string | null;
  additions?: number | null;
  deletions?: number | null;
  previousPath?: string | null;
};

type PullRequestReviewCommentDto = {
  id: string;
  workItemId: string;
  pullRequestProvider: string;
  pullRequestNumber: number;
  filePath: string;
  side: string;
  lineNumber: number;
  diffLine: string;
  author: string;
  body: string;
  status: string;
  createdAt: string;
  updatedAt: string;
  resolvedBy?: string | null;
  resolvedAt?: string | null;
};

type AiPlanReviewCommentDto = {
  id: string;
  workItemId: string;
  aiRunId: string;
  anchorKey: string;
  quotedText: string;
  author: string;
  body: string;
  status: string;
  createdAt: string;
  updatedAt: string;
  resolvedBy?: string | null;
  resolvedAt?: string | null;
  aiRunSequenceNumber?: number | null;
};

type PullRequestDiffDto = {
  provider: string;
  repository: string;
  number: number;
  state: string;
  baseBranch?: string | null;
  headBranch?: string | null;
  pullRequestUrl?: string | null;
  changedFiles: number;
  additions: number;
  deletions: number;
  truncated: boolean;
  message?: string | null;
  files: PullRequestDiffFileDto[];
  diff: string;
  reviewComments?: PullRequestReviewCommentDto[] | null;
  pullRequestApprovedBy?: string | null;
  pullRequestApprovedAt?: string | null;
  pullRequestMergedAt?: string | null;
  pullRequestFailure?: string | null;
  canApprove?: boolean | null;
  approvalStatus?: string | null;
  approvalMessage?: string | null;
};

type SettingsDto = {
  gitHub: {
    account: string;
    targetRepository: string;
    branchWatchPatterns: string;
    connected: boolean;
    appConfigured: boolean;
    installUrl?: string | null;
    syncAvailable: boolean;
  };
  ai: {
    provider: string;
    endpoint: string;
    activeModel: string;
    availableModels: string[];
    autoReviewPullRequests: boolean;
    availableProviders: AiProviderSettingsDto[];
  };
  preview: {
    domain: string;
    defaultTtlDays: number;
    namespace: string;
  };
  repositories: {
    provider: string;
    mode: string;
    apiBaseUrl: string;
    canCreateRepositories: boolean;
    localGitEnabled?: boolean;
    localGitAvailable?: boolean;
    localGitMessage?: string | null;
  };
  authentik: {
    enabled: boolean;
    authority: string;
    usersEndpoint: string;
  };
};

type AiProviderSettingsDto = {
  provider: string;
  displayName: string;
  status: string;
  endpoint: string;
  activeModel: string;
  availableModels: string[];
  availableReasoningEfforts?: string[] | null;
  defaultReasoningEffort?: string | null;
};

type AiRun = {
  id: string;
  workItemId: string;
  provider: string;
  model: string;
  status: string;
  plan?: string | null;
  approvedBy?: string | null;
  sequenceNumber: number;
  createdAt: string;
  reasoningEffort?: string | null;
};

type WorkItemForm = {
  title: string;
  description: string;
  type: string;
  status: string;
  priority: string;
  assignee: string;
  parentWorkItemId: string;
  isBug: boolean;
};

type AssigneeOption = {
  value: string;
  label: string;
  hint?: string;
};

type AssigneeDto = {
  id: string;
  displayName: string;
  email: string;
  source: string;
};

type CreateBoardForm = {
  name: string;
  repositoryProvider: 'GitHub' | 'LocalGit' | 'GenericGit' | string;
  providerMode: 'NoRepository' | 'GitHub' | 'GitHubNew' | 'LocalGitNew' | 'CustomUrl';
  repositoryId?: string | null;
  repositoryOwner: string;
  repositoryRemoteUrl: string;
  repositoryWebUrl: string;
  repositoryDefaultBranch: string;
  implementationProfile: 'react-preview' | 'code-repo' | 'unity' | 'gitops-homelab';
  implementationWorkflow: ImplementationWorkflow;
  publicHostname: string;
  teamIds: string[];
  gitOpsAllowedPaths: string;
  argoNamespace: string;
  argoApplicationSelector: string;
  aiInstructions: string;
  agentInstructions: string;
  enabledSkills: string;
  capabilityTags: string;
  skillDrafts: RepositorySkillDraftDto[];
  askWhenUncertain: boolean;
};

type GitHubRepositoryOnboardingFileDto = {
  path: string;
  content: string;
};

type GitHubRepositoryOnboardingDraftDto = {
  name: string;
  description: string;
  prompt: string;
  repositoryProfile: RepositoryProfileDto;
  aiContext: {
    instructions?: string | null;
    enabledSkills?: string[] | null;
    askWhenUncertain?: boolean | null;
  };
  files: GitHubRepositoryOnboardingFileDto[];
  source: string;
  model?: string | null;
};

type GitHubRepositoryCreateResponse = {
  repository: RepositoryDto;
  repositoryProfile?: RepositoryProfileDto | null;
  aiContext?: {
    instructions?: string | null;
    enabledSkills?: string[] | null;
    askWhenUncertain?: boolean | null;
  } | null;
};

type GitHubUserAuthorizationStartDto = {
  authorizationUrl: string;
};

type GitHubRepositoryPickerDto = {
  status: 'Loading' | 'Loaded' | 'Empty' | 'Error' | string;
  message?: string | null;
  repositories: RepositoryDto[];
  activeInstallationId?: number | null;
};

type RepositoryProfileDto = {
  implementationProfile: CreateBoardForm['implementationProfile'];
  displayName: string;
  confidence: number;
  enabledSkills: string[];
  instructions: string;
  signals: string[];
  source: string;
  capabilityTags?: string[] | null;
  skillDrafts?: RepositorySkillDraftDto[] | null;
  analyzerModel?: string | null;
  analyzedAt?: string | null;
};

type RepositorySkillDraftDto = {
  name: string;
  description: string;
  content: string;
  enabled: boolean;
};

type ToastMessage = {
  id: string;
  kind: 'info' | 'success' | 'error';
  message: string;
};

const emptyForm: WorkItemForm = {
  title: '',
  description: '',
  type: 'Feature',
  status: 'Todo',
  priority: 'Medium',
  assignee: '',
  parentWorkItemId: '',
  isBug: false
};

const workItemTypeOptions = ['Epic', 'Feature', 'Task'] as const;

const emptyBoardForm: CreateBoardForm = {
  name: '',
  repositoryProvider: 'GitHub',
  providerMode: 'GitHub',
  repositoryId: null,
  repositoryOwner: '',
  repositoryRemoteUrl: '',
  repositoryWebUrl: '',
  repositoryDefaultBranch: 'main',
  implementationProfile: 'code-repo',
  implementationWorkflow: 'direct-pr',
  publicHostname: '',
  teamIds: [],
  gitOpsAllowedPaths: 'apps/\nclusters/\ninfrastructure/\nkubernetes/\ntofu/',
  argoNamespace: 'argocd',
  argoApplicationSelector: '',
  aiInstructions: '',
  agentInstructions: '',
  enabledSkills: 'kubernetes\nargocd\ngitops-homelab',
  capabilityTags: 'kubernetes\nargocd',
  skillDrafts: [],
  askWhenUncertain: true
};

const selectedBoardStorageKey = 'rosenvall-devops:selected-board';
const selectedAiProviderStorageKey = 'rosenvall-devops:selected-ai-provider';
const selectedAiModelStorageKey = 'rosenvall-devops:selected-ai-model';
const selectedAiReasoningStorageKey = 'rosenvall-devops:selected-ai-reasoning';

let authSession: AuthSession = { getAccessToken: () => null };

function setAuthSession(session: AuthSession) {
  authSession = session;
}

const api = createApiClient({
  getAccessToken: () => authSession.getAccessToken(),
  refreshAccessToken: () => authSession.refreshAccessToken?.() ?? Promise.resolve(null),
  handleUnauthorized: () => authSession.handleUnauthorized?.() ?? Promise.resolve()
});
const AI_PLAN_TIMEOUT_MS = 150000;

function App() {
  const auth = useAuth();
  const [view, setView] = useStateFromHash();
  const [shell, setShell] = React.useState<ShellState>({ status: 'loading' });
  const [selected, setSelected] = React.useState<SelectedState>({ status: 'closed' });
  const [createStatus, setCreateStatus] = React.useState<string | null>(null);
  const [createBoardOpen, setCreateBoardOpen] = React.useState(false);
  const [syncBoardOpen, setSyncBoardOpen] = React.useState(false);
  const [query, setQuery] = React.useState('');
  const [toasts, setToasts] = React.useState<ToastMessage[]>([]);
  const [busyAction, setBusyAction] = React.useState<string | null>(null);
  const [apiBanner, setApiBanner] = React.useState<string | null>(null);
  const [selectedAiProvider, setSelectedAiProviderState] = React.useState<string | null>(() => window.localStorage.getItem(selectedAiProviderStorageKey));
  const [selectedAiModel, setSelectedAiModelState] = React.useState<string | null>(() => window.localStorage.getItem(selectedAiModelStorageKey));
  const [selectedAiReasoning, setSelectedAiReasoningState] = React.useState<string | null>(() => window.localStorage.getItem(selectedAiReasoningStorageKey));
  const setSelectedAiProvider = React.useCallback((provider: string) => {
    setSelectedAiProviderState(provider);
    setSelectedAiModelState(null);
    setSelectedAiReasoningState(null);
    window.localStorage.setItem(selectedAiProviderStorageKey, provider);
    window.localStorage.removeItem(selectedAiModelStorageKey);
    window.localStorage.removeItem(selectedAiReasoningStorageKey);
  }, []);
  const setSelectedAiModel = React.useCallback((model: string) => {
    setSelectedAiModelState(model);
    window.localStorage.setItem(selectedAiModelStorageKey, model);
  }, []);
  const setSelectedAiReasoning = React.useCallback((reasoning: string) => {
    setSelectedAiReasoningState(reasoning);
    window.localStorage.setItem(selectedAiReasoningStorageKey, reasoning);
  }, []);
  const [selectedBoardId, setSelectedBoardIdState] = React.useState<string | null>(() => window.localStorage.getItem(selectedBoardStorageKey));
  const selectedBoardIdRef = React.useRef<string | null>(selectedBoardId);
  const setSelectedBoardId = React.useCallback((id: string | null) => {
    selectedBoardIdRef.current = id;
    setSelectedBoardIdState(id);
    if (id) {
      window.localStorage.setItem(selectedBoardStorageKey, id);
    } else {
      window.localStorage.removeItem(selectedBoardStorageKey);
    }
  }, []);
  const actor = auth.status === 'ready' ? auth.userName : 'Christopher Rosenvall';
  const assigneeOptions = React.useMemo(
    () => buildAssigneeOptions(shell.status === 'ready' ? shell.board : null, shell.status === 'ready' ? shell.assignees : [], auth),
    [shell, auth]
  );

  React.useEffect(() => {
    setAuthSession(auth.status === 'ready'
      ? { getAccessToken: () => latestAccessToken(auth), refreshAccessToken: auth.refreshAccessToken, handleUnauthorized: auth.handleUnauthorized }
      : { getAccessToken: () => null });
  }, [auth]);

  const addToast = React.useCallback((kind: ToastMessage['kind'], message: string) => {
    const id = crypto.randomUUID();
    setToasts((current) => [{ id, kind, message }, ...current].slice(0, 3));
    window.setTimeout(() => {
      setToasts((current) => current.filter((toast) => toast.id !== id));
    }, kind === 'success' ? 5000 : 30000);
  }, []);

  const loadShell = React.useCallback(async (preferredBoardId?: string | null, options?: LoadShellOptions) => {
    if (auth.status === 'checking') return;
    setShell((current) => {
      if (current.status !== 'ready') return { status: 'loading' };
      return options?.silentBusy ? current : { ...current, busy: true };
    });
    try {
      const workspaces = await api.get<Workspace[]>('/api/workspaces');
      const workspace = workspaces[0];
      if (!workspace) throw new Error('No workspace available for this account.');
      const boards = await api.get<Board[]>(`/api/workspaces/${workspace.id}/boards`);
      const board = boards.find((entry) => entry.id === (preferredBoardId ?? selectedBoardIdRef.current)) ?? boards[0];
      if (!board) throw new Error('No board returned by API');
      const [repositories, settings, previews, events, pipelines, timeline, metrics, assignees, me, teams, githubIntegrations, boardSecrets, gitOpsApplications] = await Promise.all([
        api.get<RepositoryDto[]>('/api/repositories'),
        api.get<SettingsDto>('/api/settings'),
        api.get<PreviewEnvironmentDto[]>('/api/preview-environments'),
        api.get<PreviewEventDto[]>('/api/preview-events'),
        api.get<PipelineStatusDto[]>('/api/pipelines'),
        api.get<TimelineEventDto[]>(`/api/boards/${board.id}/timeline`),
        api.get<MetricsDto>(`/api/metrics?boardId=${board.id}`),
        api.get<AssigneeDto[]>(`/api/assignees?boardId=${board.id}`),
        api.get<UserDto>('/api/me'),
        api.get<TeamDto[]>('/api/teams'),
        api.get<GitHubIntegrationDto[]>('/api/integrations/github'),
        api.get<BoardSecretDto[]>(`/api/boards/${board.id}/secrets`),
        api.get<GitOpsApplicationsResponseDto>(`/api/boards/${board.id}/gitops/applications`).catch(() => ({ applications: [], message: 'ArgoCD status is unavailable.' }))
      ]);
      setSelectedBoardId(board.id);
      setApiBanner(null);
      setShell({ status: 'ready', workspace, boards, board, repositories, settings, previews, events, pipelines, timeline, metrics, assignees, me, teams, githubIntegrations, boardSecrets, gitOpsApplications, busy: false });
    } catch (loadError) {
      const banner = apiUnavailableBannerMessage(loadError);
      if (banner) {
        setApiBanner(banner);
      }
      if (options?.silentBusy) {
        console.warn('Failed to refresh API state', loadError);
        setShell((current) => current.status === 'ready' ? { ...current, busy: false } : current);
        return;
      }
      if (banner) {
        setShell((current) => current.status === 'ready' ? { ...current, busy: false } : { status: 'error', message: loadError instanceof Error ? loadError.message : 'Failed to load API data' });
        return;
      }
      setShell({ status: 'error', message: loadError instanceof Error ? loadError.message : 'Failed to load API data' });
    }
  }, [auth.status, setSelectedBoardId]);

  const handledGitHubUserAuthorizationResult = React.useRef(false);
  React.useEffect(() => {
    if (handledGitHubUserAuthorizationResult.current || (auth.status !== 'ready' && auth.status !== 'disabled')) return;
    const result = githubUserAuthorizationResultFromUrl(window.location.href);
    if (!result) return;

    handledGitHubUserAuthorizationResult.current = true;
    addToast(result.kind, result.message);
    const cleanUrl = `${window.location.pathname}${window.location.hash || '#settings'}`;
    window.history.replaceState({}, document.title, cleanUrl);
    if (result.kind === 'success') {
      void loadShell(undefined, { silentBusy: true });
    }
  }, [addToast, auth.status, loadShell]);

  const loadWorkItem = React.useCallback(async (id: string) => {
    setSelected((current) => current.status === 'open' && current.detail.item.id === id ? { ...current, busy: true } : { status: 'loading', id });
    try {
      const [detail, runs] = await Promise.all([
        api.get<WorkItemDetail>(`/api/work-items/${id}`),
        api.get<AiRun[]>(`/api/work-items/${id}/ai-runs`)
      ]);
      setSelected({ status: 'open', detail, aiRuns: runs, busy: false });
    } catch (loadError) {
      const banner = apiUnavailableBannerMessage(loadError);
      if (banner) setApiBanner(banner);
      setSelected({ status: 'closed' });
      addToast('error', loadError instanceof Error ? loadError.message : 'Failed to load work item');
    }
  }, [addToast]);

  React.useEffect(() => {
    if (auth.status === 'ready' || auth.status === 'disabled') {
      void loadShell();
    }
  }, [auth.status, loadShell]);

  const refreshAfterChange = React.useCallback(async (workItemId?: string) => {
    await loadShell();
    if (workItemId) {
      await loadWorkItem(workItemId);
    }
  }, [loadShell, loadWorkItem]);

  const shouldPollOpenWorkItem = selected.status === 'open' && (
    isPreviewPendingStatus(selected.detail.preview?.status) ||
    (selected.detail.implementationRuns ?? []).some((run) => isImplementationRunPendingStatus(run.status)) ||
    (selected.detail.repositoryCleanupRuns ?? []).some((run) => isImplementationRunPendingStatus(run.status))
  );
  const previewPollWorkItemId = selected.status === 'open' && shouldPollOpenWorkItem
    ? selected.detail.item.id
    : null;

  React.useEffect(() => {
    if (!previewPollWorkItemId) return;
    let cancelled = false;

    const refreshPreviewStatus = async () => {
      try {
        const [detail, runs] = await Promise.all([
          api.get<WorkItemDetail>(`/api/work-items/${previewPollWorkItemId}`),
          api.get<AiRun[]>(`/api/work-items/${previewPollWorkItemId}/ai-runs`)
        ]);
        if (cancelled) return;
        setSelected((current) => current.status === 'open' && current.detail.item.id === previewPollWorkItemId
          ? { ...current, detail, aiRuns: runs, busy: false }
          : current);
        await loadShell(undefined, { silentBusy: true });
      } catch (pollError) {
        console.warn('Failed to refresh preview status', pollError);
      }
    };

    const interval = window.setInterval(() => void refreshPreviewStatus(), 2000);
    void refreshPreviewStatus();

    return () => {
      cancelled = true;
      window.clearInterval(interval);
    };
  }, [loadShell, previewPollWorkItemId]);

  const activeGitOpsBoardId = shell.status === 'ready' && view === 'gitops' && isGitOpsBoard(shell.board) ? shell.board.id : null;
  React.useEffect(() => {
    if (!activeGitOpsBoardId) return;
    let cancelled = false;

    const refreshGitOpsApplications = async () => {
      try {
        const result = await api.get<GitOpsApplicationsResponseDto>(`/api/boards/${activeGitOpsBoardId}/gitops/applications`);
        if (cancelled) return;
        setShell((current) => current.status === 'ready' && current.board.id === activeGitOpsBoardId
          ? { ...current, gitOpsApplications: result }
          : current);
      } catch (error) {
        if (!cancelled) {
          setShell((current) => current.status === 'ready' && current.board.id === activeGitOpsBoardId
            ? { ...current, gitOpsApplications: { applications: [], message: error instanceof Error ? error.message : 'ArgoCD status is unavailable.' } }
            : current);
        }
      }
    };

    void refreshGitOpsApplications();
    const interval = window.setInterval(() => void refreshGitOpsApplications(), 15000);
    return () => {
      cancelled = true;
      window.clearInterval(interval);
    };
  }, [activeGitOpsBoardId]);

  const runAction = React.useCallback(async (label: string, action: () => Promise<void>): Promise<boolean> => {
    setBusyAction(label);
    try {
      await action();
      setApiBanner(null);
      addToast('success', `${label} completed.`);
      return true;
    } catch (actionError) {
      const message = actionError instanceof Error ? actionError.message : `${label} failed`;
      const banner = apiUnavailableBannerMessage(actionError);
      if (banner) setApiBanner(banner);
      addToast('error', message);
      return false;
    } finally {
      setBusyAction(null);
    }
  }, [addToast]);

  const actions: BoardActions = {
    openWorkItem: (id) => void loadWorkItem(id),
    openCreateCard: (status) => setCreateStatus(status),
    openCreateBoard: () => setCreateBoardOpen(true),
    selectBoard: (id) => {
      setSelectedBoardId(id);
      void loadShell(id);
    },
    createBoard: async (form) => {
      return runAction('Creating board', async () => {
        if (shell.status !== 'ready') return;
        const hasRepository = form.providerMode !== 'NoRepository';
        const created = await api.post<Board>(`/api/workspaces/${shell.workspace.id}/boards`, {
          name: form.name,
          repositoryId: form.repositoryId ?? null,
          repositoryProvider: hasRepository ? form.repositoryProvider : null,
          repositoryName: hasRepository ? repositoryNameFromRemote(form.repositoryRemoteUrl, form.name) : null,
          repositoryRemoteUrl: hasRepository ? form.repositoryRemoteUrl : null,
          repositoryWebUrl: hasRepository ? form.repositoryWebUrl || webUrlFromRemote(form.repositoryRemoteUrl) : null,
          repositoryDefaultBranch: hasRepository ? form.repositoryDefaultBranch || 'main' : null,
          repositoryOwner: hasRepository ? form.repositoryOwner || repositoryOwnerFromRemote(form.repositoryRemoteUrl) : null,
          implementationProfile: form.implementationProfile,
          implementationWorkflow: hasRepository ? form.implementationWorkflow : 'preview-only',
          publicHostname: hasRepository ? form.publicHostname || null : null,
          providerMode: form.providerMode,
          customRepositoryUrl: form.providerMode === 'CustomUrl' ? form.repositoryRemoteUrl : null,
          gitHubRepositoryId: form.providerMode === 'GitHub' || form.providerMode === 'GitHubNew' ? `${form.repositoryOwner}/${repositoryNameFromRemote(form.repositoryRemoteUrl, form.name)}` : null,
          teamIds: form.teamIds,
          gitOpsSettings: form.implementationProfile === 'gitops-homelab' ? {
            allowedPaths: linesFromTextarea(form.gitOpsAllowedPaths),
            argoNamespace: form.argoNamespace,
            argoApplicationSelector: form.argoApplicationSelector
          } : null,
          aiContext: form.implementationProfile === 'gitops-homelab' || form.aiInstructions.trim() || form.agentInstructions.trim() || form.enabledSkills.trim() ? {
            instructions: form.aiInstructions,
            enabledSkills: linesFromTextarea(form.enabledSkills),
            askWhenUncertain: form.askWhenUncertain,
            agentInstructions: form.agentInstructions
          } : null,
          repositoryProfile: repositoryProfileFromForm(form)
        });
        setCreateBoardOpen(false);
        setSelectedBoardId(created.id);
        await loadShell(created.id);
      });
    },
    executePipeline: async (pipelineRunId) => {
      return runAction('Starting pipeline', async () => {
        await api.post<PipelineStatusDto>(`/api/pipeline-runs/${pipelineRunId}/execute`, { actor });
        await refreshAfterChange();
      });
    },
    createCard: async (form) => {
      return runAction('Creating card', async () => {
        if (shell.status !== 'ready') return;
        const created = await api.post<WorkItemSummary>('/api/work-items', {
          boardId: shell.board.id,
          type: form.type,
          title: form.title,
          description: form.description,
          status: form.status,
          priority: form.priority,
          assignee: form.assignee || null,
          parentWorkItemId: form.parentWorkItemId || null,
          isBug: form.isBug
        });
        setCreateStatus(null);
        await refreshAfterChange(created.id);
      });
    },
    createChildCard: async (parentId, request) => {
      return runAction('Creating child card', async () => {
        const created = await api.post<WorkItemSummary>(`/api/work-items/${parentId}/children`, request);
        await refreshAfterChange(created.id);
      });
    },
    updateCard: async (id, form) => {
      return runAction('Saving card', async () => {
        await api.patch<WorkItemSummary>(`/api/work-items/${id}`, {
          title: form.title,
          description: form.description,
          type: form.type,
          status: form.status,
          priority: form.priority,
          assignee: form.assignee || null,
          parentWorkItemId: form.parentWorkItemId || null,
          isBug: form.isBug
        });
        await refreshAfterChange(id);
      });
    },
    autosaveCard: async (id, form) => {
      try {
        await api.patch<WorkItemSummary>(`/api/work-items/${id}`, {
          title: form.title,
          description: form.description,
          type: form.type,
          status: form.status,
          priority: form.priority,
          assignee: form.assignee || null,
          parentWorkItemId: form.parentWorkItemId || null,
          isBug: form.isBug
        });
        await refreshAfterChange(id);
        return true;
      } catch (error) {
        addToast('error', error instanceof Error ? error.message : 'Autosave failed');
        return false;
      }
    },
    updateCardHierarchy: async (id, parentWorkItemId) => {
      return runAction('Updating hierarchy', async () => {
        await api.patch<WorkItemSummary>(`/api/work-items/${id}/hierarchy`, { parentWorkItemId: parentWorkItemId || null });
        await refreshAfterChange(id);
      });
    },
    deleteCard: async (id) => {
      return runAction('Deleting card and cleaning repository state', async () => {
        const response = await api.post<unknown>(`/api/work-items/${id}/delete-and-clean-up`, { actor });
        if (response === undefined || response === null) {
          setSelected({ status: 'closed' });
          await refreshAfterChange();
          return;
        }

        await refreshAfterChange(id);
      });
    },
    deleteBoard: async (boardId) => {
      return runAction('Deleting board and cleaning runtime resources', async () => {
        await api.post<unknown>(`/api/boards/${boardId}/delete-and-clean-up`, { actor });
        setSelected({ status: 'closed' });
        setSelectedBoardId(null);
        await loadShell();
      });
    },
    startAiPlan: async (id) => {
      return runAction('Generating AI plan', async () => {
        if (shell.status !== 'ready') return;
        const provider = resolveActiveAiProvider(shell.settings, selectedAiProvider);
        await api.post<AiRun>(`/api/work-items/${id}/ai-plan`, {
          provider: provider.provider,
          model: resolveActiveAiModel(shell.settings, selectedAiProvider, selectedAiModel),
          reasoningEffort: resolveActiveAiReasoning(shell.settings, selectedAiProvider, selectedAiReasoning)
        }, { timeoutMs: AI_PLAN_TIMEOUT_MS });
        await refreshAfterChange(id);
      });
    },
    startEpicRun: async (workItemId) => {
      return runAction('Starting epic implementation', async () => {
        await api.post<EpicRunDto>(`/api/work-items/${workItemId}/epic-runs`, { actor });
        await refreshAfterChange(workItemId);
      });
    },
    startEpicGoal: async (workItemId) => {
      return runAction('Starting epic goal', async () => {
        await api.post<EpicGoalRunDto>(`/api/work-items/${workItemId}/epic-goals`, { actor });
        await refreshAfterChange(workItemId);
      });
    },
    cancelEpicGoal: async (goalId, workItemId) => {
      return runAction('Cancelling epic goal', async () => {
        await api.post<EpicGoalRunDto>(`/api/epic-goals/${goalId}/cancel`, { actor });
        await refreshAfterChange(workItemId);
      });
    },
    reviseAiPlan: async (workItemId, aiRunId, message) => {
      return runAction('Revising AI plan', async () => {
        if (shell.status !== 'ready') return;
        const provider = resolveActiveAiProvider(shell.settings, selectedAiProvider);
        await api.post<AiRun>(`/api/work-items/${workItemId}/ai-plan/revise`, {
          aiRunId,
          message,
          provider: provider.provider,
          model: resolveActiveAiModel(shell.settings, selectedAiProvider, selectedAiModel),
          reasoningEffort: resolveActiveAiReasoning(shell.settings, selectedAiProvider, selectedAiReasoning)
        }, { timeoutMs: AI_PLAN_TIMEOUT_MS });
        await refreshAfterChange(workItemId);
      });
    },
    approvePlan: async (runId, workItemId) => {
      setBusyAction('Starting preview implementation');
      try {
        await api.post<WorkItemDetail>(`/api/ai-runs/${runId}/approve`, { approvedBy: actor, reasoningEffort: resolveActiveAiReasoning(shell.status === 'ready' ? shell.settings : null, selectedAiProvider, selectedAiReasoning) });
        await refreshAfterChange(workItemId);
        addToast('info', 'Preview implementation started. Follow the terminal log in the preview panel.');
      } catch (approveError) {
        await refreshAfterChange(workItemId);
        addToast('error', approveError instanceof Error ? approveError.message : 'Preview implementation failed to start');
      } finally {
        setBusyAction(null);
      }
    },
    startImplementationRun: async (workItemId, aiRunId, repositoryId) => {
      setBusyAction('Starting repository implementation');
      try {
        await api.post<ImplementationRunDto>(`/api/work-items/${workItemId}/implementation-runs`, { aiRunId, actor, repositoryId, reasoningEffort: resolveActiveAiReasoning(shell.status === 'ready' ? shell.settings : null, selectedAiProvider, selectedAiReasoning) });
        await refreshAfterChange(workItemId);
        setApiBanner(null);
        addToast('info', 'Repository implementation started. Follow the implementation run terminal.');
        return null;
      } catch (implementationError) {
        await refreshAfterChange(workItemId);
        const message = implementationError instanceof Error ? implementationError.message : 'Repository implementation failed to start';
        const banner = apiUnavailableBannerMessage(implementationError);
        if (banner) setApiBanner(banner);
        addToast('error', message);
        return message;
      } finally {
        setBusyAction(null);
      }
    },
    addGitHubIntegration: async () => {
      return runAction('Opening GitHub integration', async () => {
        const install = await api.get<{ url: string }>('/api/integrations/github/install-url');
        window.location.href = install.url;
      });
    },
    syncGitHubIntegration: async () => {
      return runAction('Syncing GitHub installations', async () => {
        await api.post<GitHubIntegrationDto[]>('/api/integrations/github/sync', {});
        await refreshAfterChange(selected.status === 'open' ? selected.detail.item.id : undefined);
      });
    },
    authorizeGitHubUser: async (installationId) => {
      return runAction('Opening GitHub authorization', async () => {
        const authorization = await api.get<GitHubUserAuthorizationStartDto>(`/api/integrations/github/user-authorization/start?installationId=${installationId}`, { timeoutMs: 15000 });
        window.location.href = authorization.authorizationUrl;
      });
    },
    syncBoardRepository: async (boardId, request) => {
      return runAction('Syncing board to GitHub', async () => {
        const board = await api.post<Board>(`/api/boards/${boardId}/repositories/github`, request);
        setSyncBoardOpen(false);
        setSelectedBoardId(board.id);
        await loadShell(board.id);
      });
    },
    adoptCleanupPullRequest: async (workItemId, pullRequestUrl) => {
      return runAction('Adopting cleanup pull request', async () => {
        await api.post<RepositoryCleanupRunDto>(`/api/work-items/${workItemId}/cleanup-runs/adopt`, { actor, pullRequestUrl });
        await refreshAfterChange(workItemId);
      });
    },
    createBoardSecret: async (boardId, key, value, repositoryId) => {
      return runAction('Saving board secret', async () => {
        await api.post<BoardSecretDto>(`/api/boards/${boardId}/secrets`, { key, value, repositoryId: repositoryId || null });
        await refreshAfterChange(selected.status === 'open' ? selected.detail.item.id : undefined);
      });
    },
    deleteBoardSecret: async (boardId, secretId) => {
      return runAction('Deleting board secret', async () => {
        await api.delete(`/api/boards/${boardId}/secrets/${secretId}`);
        await refreshAfterChange(selected.status === 'open' ? selected.detail.item.id : undefined);
      });
    },
    updateBoardGitOpsSettings: async (boardId, settings) => {
      return runAction('Saving GitOps settings', async () => {
        await api.put<BoardGitOpsSettingsDto>(`/api/boards/${boardId}/gitops-settings`, settings);
        await refreshAfterChange(selected.status === 'open' ? selected.detail.item.id : undefined);
      });
    },
    updateBoardAiContext: async (boardId, context) => {
      return runAction('Saving board AI context', async () => {
        await api.put<BoardAiContextDto>(`/api/boards/${boardId}/ai-context`, context);
        await refreshAfterChange(selected.status === 'open' ? selected.detail.item.id : undefined);
      });
    },
    updateBoardHosting: async (boardId, settings) => {
      return runAction('Saving board hosting', async () => {
        await api.put<Board>(`/api/boards/${boardId}/hosting`, settings);
        await refreshAfterChange(selected.status === 'open' ? selected.detail.item.id : undefined);
      });
    },
    updateBoardRepositoryProfile: async (boardId, repositoryId, profile) => {
      return runAction('Saving repository profile', async () => {
        await api.put<Board>(`/api/boards/${boardId}/repositories/${repositoryId}/profile`, profile);
        await refreshAfterChange(selected.status === 'open' ? selected.detail.item.id : undefined);
      });
    },
    discardPlan: async (runId, workItemId) => {
      return runAction('Discarding plan', async () => {
        await api.post<AiRun>(`/api/ai-runs/${runId}/discard`, { discardedBy: actor });
        await refreshAfterChange(workItemId);
      });
    },
    approvePullRequest: async (workItemId) => {
      const currentDevelopment = selected.status === 'open' && selected.detail.item.id === workItemId
        ? selected.detail.development
        : null;
      const label = currentDevelopment && isLocalGitDevelopmentRecord(currentDevelopment)
        ? 'Merging local PR and deploying app'
        : 'Approving PR and deploying app';
      return runAction(label, async () => {
        await api.post<WorkItemDetail>(`/api/work-items/${workItemId}/approve-pr`, { approvedBy: actor });
        await refreshAfterChange(workItemId);
      });
    },
    approvePreviewForPr: async (workItemId) => {
      return runAction('Creating pull request from approved preview', async () => {
        await api.post<ImplementationRunDto>(`/api/work-items/${workItemId}/preview/approve-for-pr`, { actor });
        await refreshAfterChange(workItemId);
        addToast('info', 'Pull request creation started from the approved preview.');
      });
    },
    createPullRequestReviewComment: async (workItemId, request) => {
      try {
        const comment = await api.post<PullRequestReviewCommentDto>(`/api/work-items/${workItemId}/pull-request/comments`, request);
        await refreshAfterChange(workItemId);
        return comment;
      } catch (error) {
        addToast('error', error instanceof Error ? error.message : 'Could not save review comment');
        return null;
      }
    },
    updatePullRequestReviewComment: async (workItemId, commentId, request) => {
      try {
        const comment = await api.patch<PullRequestReviewCommentDto>(`/api/work-items/${workItemId}/pull-request/comments/${commentId}`, request);
        await refreshAfterChange(workItemId);
        return comment;
      } catch (error) {
        addToast('error', error instanceof Error ? error.message : 'Could not update review comment');
        return null;
      }
    },
    deletePullRequestReviewComment: async (workItemId, commentId) => {
      return runAction('Deleting review comment', async () => {
        await api.delete(`/api/work-items/${workItemId}/pull-request/comments/${commentId}`);
        await refreshAfterChange(workItemId);
      });
    },
    fixPullRequestReviewCommentsWithAi: async (workItemId) => {
      return runAction('Fixing review comments with AI', async () => {
        await api.post<ImplementationRunDto>(`/api/work-items/${workItemId}/pull-request/ai-fix-comments`, { actor, reasoningEffort: resolveActiveAiReasoning(shell.status === 'ready' ? shell.settings : null, selectedAiProvider, selectedAiReasoning) });
        await refreshAfterChange(workItemId);
        addToast('info', 'AI review fix started on the existing pull request branch.');
      });
    },
    createAiPlanReviewComment: async (workItemId, aiRunId, request) => {
      try {
        const comment = await api.post<AiPlanReviewCommentDto>(`/api/work-items/${workItemId}/ai-plans/${aiRunId}/comments`, request);
        await refreshAfterChange(workItemId);
        return comment;
      } catch (error) {
        addToast('error', error instanceof Error ? error.message : 'Could not save plan comment');
        return null;
      }
    },
    updateAiPlanReviewComment: async (workItemId, aiRunId, commentId, request) => {
      try {
        const comment = await api.patch<AiPlanReviewCommentDto>(`/api/work-items/${workItemId}/ai-plans/${aiRunId}/comments/${commentId}`, request);
        await refreshAfterChange(workItemId);
        return comment;
      } catch (error) {
        addToast('error', error instanceof Error ? error.message : 'Could not update plan comment');
        return null;
      }
    },
    deleteAiPlanReviewComment: async (workItemId, aiRunId, commentId) => {
      return runAction('Deleting plan comment', async () => {
        await api.delete(`/api/work-items/${workItemId}/ai-plans/${aiRunId}/comments/${commentId}`);
        await refreshAfterChange(workItemId);
      });
    },
    startPreview: async (workItemId) => {
      return runAction('Starting preview', async () => {
        try {
          await api.post<WorkItemDetail>(`/api/work-items/${workItemId}/preview/start`, { actor });
        } catch (previewError) {
          await refreshAfterChange(workItemId);
          throw previewError;
        }
        await refreshAfterChange(workItemId);
      });
    },
    stopPreview: async (workItemId) => {
      return runAction('Stopping preview', async () => {
        await api.post<WorkItemDetail>(`/api/work-items/${workItemId}/preview/stop`, { actor });
        await refreshAfterChange(workItemId);
      });
    },
    addComment: async (id, body) => {
      return runAction('Posting comment', async () => {
        await api.post<CommentDto>(`/api/work-items/${id}/comments`, { author: actor, kind: 'Comment', body });
        await refreshAfterChange(id);
      });
    },
    addCommentAndAskAi: async (id, body) => {
      return runAction('Posting comment and asking AI', async () => {
        if (shell.status !== 'ready') return;
        const provider = resolveActiveAiProvider(shell.settings, selectedAiProvider);
        await api.post<CommentDto>(`/api/work-items/${id}/comments`, { author: actor, kind: 'Comment', body });
        await api.post<AiRun>(`/api/work-items/${id}/ai-plan`, {
          provider: provider.provider,
          model: resolveActiveAiModel(shell.settings, selectedAiProvider, selectedAiModel),
          reasoningEffort: resolveActiveAiReasoning(shell.settings, selectedAiProvider, selectedAiReasoning)
        }, { timeoutMs: AI_PLAN_TIMEOUT_MS });
        await refreshAfterChange(id);
      });
    },
    updateComment: async (commentId, workItemId, body) => {
      return runAction('Updating comment', async () => {
        await api.patch<CommentDto>(`/api/comments/${commentId}`, { actor, body });
        await refreshAfterChange(workItemId);
      });
    },
    deleteComment: async (commentId, workItemId) => {
      return runAction('Deleting comment', async () => {
        await api.delete(`/api/comments/${commentId}?actor=${encodeURIComponent(actor)}`);
        await refreshAfterChange(workItemId);
      });
    },
    createTeam: async (name) => {
      return runAction('Creating team', async () => {
        await api.post<TeamDto>('/api/teams', { name });
        await refreshAfterChange();
      });
    },
    inviteTeamMember: async (teamId, email, role) => {
      return runAction('Adding team member', async () => {
        await api.post<TeamDto>(`/api/teams/${teamId}/members`, { email, role });
        await refreshAfterChange();
      });
    },
    assignTeamToBoard: async (boardId, teamId, role) => {
      return runAction('Assigning team to board', async () => {
        await api.put<BoardTeamAccessDto>(`/api/boards/${boardId}/teams/${teamId}`, { role });
        await refreshAfterChange();
      });
    },
    removeTeamFromBoard: async (boardId, teamId) => {
      return runAction('Removing team from board', async () => {
        await api.delete(`/api/boards/${boardId}/teams/${teamId}`);
        await refreshAfterChange();
      });
    },
    moveCard: async (id, status, sortOrder) => {
      if (shell.status !== 'ready') return;
      const previous = shell.board;
      setShell({ ...shell, board: moveCardInBoard(shell.board, id, status, sortOrder), busy: true });
      try {
        await api.post<WorkItemSummary>(`/api/work-items/${id}/move`, { status, sortOrder });
        await refreshAfterChange(selected.status === 'open' ? selected.detail.item.id : undefined);
      } catch (moveError) {
        setShell({ ...shell, board: previous, busy: false });
        addToast('error', moveError instanceof Error ? moveError.message : 'Could not move card');
      }
    }
  };

  const board = shell.status === 'ready' ? filterBoard(shell.board, query) : null;
  const gitOpsAvailable = shell.status === 'ready' && isGitOpsBoard(shell.board);
  const activeView = view === 'gitops' && !gitOpsAvailable ? 'board' : view;
  const activeBoardId = shell.status === 'ready' && selectedBoardId && shell.boards.some((entry) => entry.id === selectedBoardId)
    ? selectedBoardId
    : shell.status === 'ready'
      ? shell.board.id
      : selectedBoardId;

  return (
    <div className="app-shell">
      <Sidebar
        view={activeView}
        showGitOps={gitOpsAvailable}
        boards={shell.status === 'ready' ? shell.boards : []}
        selectedBoardId={activeBoardId}
        activeBoard={shell.status === 'ready' ? shell.board : null}
        onSelectBoard={actions.selectBoard}
        onAddBoard={() => setCreateBoardOpen(true)}
        onChange={setView}
        onNewCard={() => setCreateStatus('Todo')}
      />
      <main className="main">
        <Topbar
          query={query}
          onQueryChange={setQuery}
          userName={auth.status === 'ready' ? auth.userName : null}
          userEmail={auth.status === 'ready' ? auth.userEmail : undefined}
          onLogout={auth.status === 'ready' ? auth.logout : undefined}
        />
        {apiBanner && <div className="api-status-banner" role="alert">{apiBanner}</div>}
        {auth.status === 'checking' && <Loading message="Checking authentication..." />}
        {auth.status === 'signedOut' && <SignedOutPanel onLogin={auth.login} />}
        {auth.status === 'error' && <ErrorPanel message={auth.message} onRetry={() => window.location.reload()} />}
        {(auth.status === 'ready' || auth.status === 'disabled') && shell.status === 'loading' && <Loading />}
        {(auth.status === 'ready' || auth.status === 'disabled') && shell.status === 'error' && <ErrorPanel message={shell.message} onRetry={loadShell} />}
        {(auth.status === 'ready' || auth.status === 'disabled') && shell.status === 'ready' && board && (
          <>
            {activeView === 'dashboard' && <DashboardView workspace={shell.workspace} board={shell.board} previews={shell.previews} events={shell.events} pipelines={shell.pipelines} metrics={shell.metrics} actions={actions} />}
            {activeView === 'board' && <BoardView board={board} actions={actions} onSyncBoard={() => setSyncBoardOpen(true)} />}
            {activeView === 'timeline' && <TimelineView board={shell.board} timeline={shell.timeline} />}
            {activeView === 'gitops' && <GitOpsView board={shell.board} gitOpsApplications={shell.gitOpsApplications} actions={actions} onBack={() => setView('board')} />}
            {activeView === 'ai' && <SettingsView scope="ai" settings={shell.settings} board={shell.board} me={shell.me} teams={shell.teams} repositories={shell.repositories} boardSecrets={shell.boardSecrets} githubIntegrations={shell.githubIntegrations} selectedProvider={selectedAiProvider} selectedModel={selectedAiModel} selectedReasoning={selectedAiReasoning} actions={actions} onProviderChange={setSelectedAiProvider} onModelChange={setSelectedAiModel} onReasoningChange={setSelectedAiReasoning} onSyncBoard={() => setSyncBoardOpen(true)} onBack={() => setView('board')} />}
            {activeView === 'environment' && <SettingsView scope="environment" settings={shell.settings} board={shell.board} me={shell.me} teams={shell.teams} repositories={shell.repositories} boardSecrets={shell.boardSecrets} githubIntegrations={shell.githubIntegrations} selectedProvider={selectedAiProvider} selectedModel={selectedAiModel} selectedReasoning={selectedAiReasoning} actions={actions} onProviderChange={setSelectedAiProvider} onModelChange={setSelectedAiModel} onReasoningChange={setSelectedAiReasoning} onSyncBoard={() => setSyncBoardOpen(true)} onBack={() => setView('board')} />}
            {activeView === 'configuration' && <SettingsView scope="board" settings={shell.settings} board={shell.board} me={shell.me} teams={shell.teams} repositories={shell.repositories} boardSecrets={shell.boardSecrets} githubIntegrations={shell.githubIntegrations} selectedProvider={selectedAiProvider} selectedModel={selectedAiModel} selectedReasoning={selectedAiReasoning} actions={actions} onProviderChange={setSelectedAiProvider} onModelChange={setSelectedAiModel} onReasoningChange={setSelectedAiReasoning} onSyncBoard={() => setSyncBoardOpen(true)} onBack={() => setView('board')} />}
            {activeView === 'teams' && <TeamsView teams={shell.teams} boards={shell.boards} me={shell.me} actions={actions} />}
            {activeView === 'settings' && <SettingsView scope="global" settings={shell.settings} board={shell.board} me={shell.me} teams={shell.teams} repositories={shell.repositories} boardSecrets={shell.boardSecrets} githubIntegrations={shell.githubIntegrations} selectedProvider={selectedAiProvider} selectedModel={selectedAiModel} selectedReasoning={selectedAiReasoning} actions={actions} onProviderChange={setSelectedAiProvider} onModelChange={setSelectedAiModel} onReasoningChange={setSelectedAiReasoning} onSyncBoard={() => setSyncBoardOpen(true)} onBack={() => setView('board')} />}
          </>
        )}
      </main>
      {selected.status === 'loading' && <ModalFrame title="Work item" onClose={() => setSelected({ status: 'closed' })}><div className="modal-loading">Loading work item...</div></ModalFrame>}
      {selected.status === 'open' && (
        <WorkItemModal
          detail={selected.detail}
          aiRuns={selected.aiRuns}
          busy={selected.busy || busyAction !== null}
          busyLabel={busyAction}
          board={shell.status === 'ready' ? shell.board : null}
          aiProvider={shell.status === 'ready' ? resolveActiveAiProvider(shell.settings, selectedAiProvider).displayName : null}
          aiModel={shell.status === 'ready' ? resolveActiveAiModel(shell.settings, selectedAiProvider, selectedAiModel) : null}
          assigneeOptions={assigneeOptions}
          actor={actor}
          actions={actions}
          onClose={() => setSelected({ status: 'closed' })}
        />
      )}
      {createStatus && shell.status === 'ready' && (
        <CreateWorkItemModal
          board={shell.board}
          initialStatus={createStatus}
          assigneeOptions={assigneeOptions}
          onCreate={actions.createCard}
          onClose={() => setCreateStatus(null)}
        />
      )}
      {createBoardOpen && shell.status === 'ready' && (
        <CreateBoardModal
          teams={shell.teams}
          githubIntegrations={shell.githubIntegrations}
          settings={shell.settings}
          me={shell.me}
          actions={actions}
          onNotify={addToast}
          onCreate={actions.createBoard}
          onClose={() => setCreateBoardOpen(false)}
        />
      )}
      {syncBoardOpen && shell.status === 'ready' && (
        <SyncBoardRepositoryModal
          board={shell.board}
          repositories={shell.repositories}
          settings={shell.settings}
          githubIntegrations={shell.githubIntegrations}
          onSync={(request) => actions.syncBoardRepository(shell.board.id, request)}
          onClose={() => setSyncBoardOpen(false)}
        />
      )}
      <ToastStack busyAction={busyAction ?? (shell.status === 'ready' && shell.busy ? 'Syncing API state' : null)} toasts={toasts} onDismiss={(id) => setToasts((current) => current.filter((toast) => toast.id !== id))} />
    </div>
  );
}

type AuthState =
  | { status: 'checking' }
  | { status: 'disabled' }
  | { status: 'signedOut'; login: () => Promise<void> }
  | { status: 'ready'; accessToken: string; userName: string; userEmail?: string; refreshAccessToken: () => Promise<string | null>; handleUnauthorized: () => Promise<void>; logout: () => Promise<void> }
  | { status: 'error'; message: string };

function useAuth(): AuthState {
  const [auth, setAuth] = React.useState<AuthState>(() => authSettings.enabled ? { status: 'checking' } : { status: 'disabled' });
  const login = React.useCallback(async () => {
    if (!userManager) return;
    window.sessionStorage.removeItem(signedOutStorageKey);
    await userManager.signinRedirect({ prompt: 'login' });
  }, []);
  const logout = React.useCallback(async () => {
    if (!userManager) {
      setAuth({ status: 'disabled' });
      return;
    }

    await userManager.removeUser();
    window.sessionStorage.setItem(signedOutStorageKey, 'true');
    window.location.assign(authSettings.postLogoutRedirectUri);
  }, []);
  const handleUnauthorized = React.useCallback(async () => {
    if (!userManager) return;
    await userManager.removeUser();
    await userManager.signinRedirect();
  }, []);
  const refreshAccessToken = React.useCallback(async () => {
    if (!userManager) return null;
    try {
      const renewed = await userManager.signinSilent();
      if (!renewed || renewed.expired) return null;
      setAuth(toReadyAuthState(renewed, refreshAccessToken, handleUnauthorized, logout));
      return renewed.access_token;
    } catch {
      await userManager.removeUser();
      return null;
    }
  }, [handleUnauthorized, logout]);

  React.useEffect(() => {
    if (!authSettings.enabled) return;
    if (window.sessionStorage.getItem(signedOutStorageKey) === 'true') {
      setAuth({ status: 'signedOut', login });
      return;
    }

    let cancelled = false;
    const removeUserLoaded = userManager?.events.addUserLoaded((user) => {
      if (!cancelled) {
        setAuth(toReadyAuthState(user, refreshAccessToken, handleUnauthorized, logout));
      }
    });
    const removeAccessTokenExpiring = userManager?.events.addAccessTokenExpiring(() => {
      void refreshAccessToken();
    });
    const removeAccessTokenExpired = userManager?.events.addAccessTokenExpired(() => {
      void refreshAccessToken();
    });

    initializeAuth()
      .then((user) => {
        if (!cancelled) {
          setAuth(toReadyAuthState(user, refreshAccessToken, handleUnauthorized, logout));
        }
      })
      .catch((error) => {
        if (!cancelled) setAuth({ status: 'error', message: error instanceof Error ? error.message : 'Authentication failed' });
      });

    return () => {
      cancelled = true;
      removeUserLoaded?.();
      removeAccessTokenExpiring?.();
      removeAccessTokenExpired?.();
    };
  }, [handleUnauthorized, login, logout, refreshAccessToken]);

  return auth;
}

function toReadyAuthState(user: User, refreshAccessToken: () => Promise<string | null>, handleUnauthorized: () => Promise<void>, logout: () => Promise<void>): AuthState {
  const userEmail = typeof user.profile.email === 'string' ? user.profile.email : undefined;
  return {
    status: 'ready',
    accessToken: user.access_token,
    userName: user.profile.name || user.profile.preferred_username || userEmail || 'Authenticated',
    userEmail,
    refreshAccessToken,
    handleUnauthorized,
    logout
  };
}

async function latestAccessToken(auth: Extract<AuthState, { status: 'ready' }>): Promise<string | null> {
  const latestUser = await userManager?.getUser();
  if (latestUser && !latestUser.expired) {
    return latestUser.access_token;
  }

  return auth.accessToken;
}

const authSettings = {
  enabled: import.meta.env.VITE_AUTH_ENABLED === 'true',
  authority: import.meta.env.VITE_AUTH_AUTHORITY as string | undefined,
  clientId: import.meta.env.VITE_AUTH_CLIENT_ID as string | undefined,
  redirectUri: (import.meta.env.VITE_AUTH_REDIRECT_URI as string | undefined) ?? `${window.location.origin}/auth/callback`,
  postLogoutRedirectUri: (import.meta.env.VITE_AUTH_POST_LOGOUT_REDIRECT_URI as string | undefined) ?? window.location.origin,
  proxyPrefix: import.meta.env.VITE_AUTH_PROXY_PREFIX as string | undefined
};

const signedOutStorageKey = 'rosenvall-devops-signed-out';

function localAuthMetadata() {
  if (!authSettings.proxyPrefix || !authSettings.authority) return undefined;

  const proxyPrefix = authSettings.proxyPrefix.replace(/\/+$/, '');
  const authority = authSettings.authority.endsWith('/') ? authSettings.authority : `${authSettings.authority}/`;
  const authorityUrl = new URL(authority);
  const issuerPath = authorityUrl.pathname;
  const providerBasePath = issuerPath.replace(/[^/]+\/$/, '');
  const proxiedProviderBase = `${window.location.origin}${proxyPrefix}${providerBasePath}`;

  return {
    issuer: authority,
    authorization_endpoint: `${authorityUrl.origin}${providerBasePath}authorize/`,
    token_endpoint: `${proxiedProviderBase}token/`,
    userinfo_endpoint: `${proxiedProviderBase}userinfo/`,
    end_session_endpoint: `${authority}end-session/`,
    jwks_uri: `${window.location.origin}${proxyPrefix}${issuerPath}jwks/`,
    revocation_endpoint: `${proxiedProviderBase}revoke/`
  };
}

const userManager = authSettings.enabled ? new UserManager({
  authority: authSettings.authority ?? '',
  metadata: localAuthMetadata(),
  client_id: authSettings.clientId ?? '',
  redirect_uri: authSettings.redirectUri,
  post_logout_redirect_uri: authSettings.postLogoutRedirectUri,
  response_type: 'code',
  scope: 'openid profile email offline_access',
  automaticSilentRenew: true,
  userStore: new WebStorageStateStore({ store: window.localStorage })
}) : null;

async function initializeAuth(): Promise<User> {
  if (!userManager || !authSettings.authority || !authSettings.clientId) {
    throw new Error('Authentication is enabled but authority or client ID is missing.');
  }

  if (window.location.pathname === '/auth/callback') {
    const callbackUser = await userManager.signinRedirectCallback();
    window.history.replaceState({}, document.title, '/');
    return callbackUser;
  }

  const user = await userManager.getUser();
  if (user && !user.expired) {
    return user;
  }

  if (user) {
    try {
      const renewed = await userManager.signinSilent();
      if (renewed && !renewed.expired) {
        return renewed;
      }
    } catch {
      await userManager.removeUser();
    }
  }

  await userManager.signinRedirect();
  return new Promise<User>(() => undefined);
}

type ShellState =
  | { status: 'loading' }
  | { status: 'error'; message: string }
  | { status: 'ready'; workspace: Workspace; boards: Board[]; board: Board; repositories: RepositoryDto[]; settings: SettingsDto; previews: PreviewEnvironmentDto[]; events: PreviewEventDto[]; pipelines: PipelineStatusDto[]; timeline: TimelineEventDto[]; metrics: MetricsDto; assignees: AssigneeDto[]; me: UserDto; teams: TeamDto[]; githubIntegrations: GitHubIntegrationDto[]; boardSecrets: BoardSecretDto[]; gitOpsApplications: GitOpsApplicationsResponseDto; busy: boolean };

type SelectedState =
  | { status: 'closed' }
  | { status: 'loading'; id: string }
  | { status: 'open'; detail: WorkItemDetail; aiRuns: AiRun[]; busy: boolean };

type BoardActions = {
  openWorkItem(id: string): void;
  openCreateCard(status: string): void;
  openCreateBoard(): void;
  selectBoard(id: string): void;
  createBoard(form: CreateBoardForm): Promise<boolean>;
  executePipeline(pipelineRunId: string): Promise<boolean>;
  createCard(form: WorkItemForm): Promise<boolean>;
  createChildCard(parentId: string, request: { type: string; title: string; description: string; status: string; priority: string; assignee?: string | null; isBug: boolean }): Promise<boolean>;
  updateCard(id: string, form: WorkItemForm): Promise<boolean>;
  autosaveCard(id: string, form: WorkItemForm): Promise<boolean>;
  updateCardHierarchy(id: string, parentWorkItemId: string | null): Promise<boolean>;
  deleteCard(id: string): Promise<boolean>;
  deleteBoard(boardId: string): Promise<boolean>;
  startAiPlan(id: string): Promise<boolean>;
  startEpicRun(workItemId: string): Promise<boolean>;
  startEpicGoal(workItemId: string): Promise<boolean>;
  cancelEpicGoal(goalId: string, workItemId: string): Promise<boolean>;
  reviseAiPlan(workItemId: string, aiRunId: string, message: string): Promise<boolean>;
  approvePlan(runId: string, workItemId: string): Promise<void>;
  startImplementationRun(workItemId: string, aiRunId: string, repositoryId?: string | null): Promise<string | null>;
  addGitHubIntegration(): Promise<boolean>;
  syncGitHubIntegration(): Promise<boolean>;
  authorizeGitHubUser(installationId: number): Promise<boolean>;
  syncBoardRepository(boardId: string, request: SyncBoardRepositoryRequest): Promise<boolean>;
  adoptCleanupPullRequest(workItemId: string, pullRequestUrl: string): Promise<boolean>;
  createBoardSecret(boardId: string, key: string, value: string, repositoryId?: string | null): Promise<boolean>;
  deleteBoardSecret(boardId: string, secretId: string): Promise<boolean>;
  updateBoardGitOpsSettings(boardId: string, settings: BoardGitOpsSettingsDto): Promise<boolean>;
  updateBoardAiContext(boardId: string, context: BoardAiContextDto): Promise<boolean>;
  updateBoardHosting(boardId: string, settings: { publicHostname?: string | null; implementationWorkflow?: string | null }): Promise<boolean>;
  updateBoardRepositoryProfile(boardId: string, repositoryId: string, profile: RepositoryProfileDto): Promise<boolean>;
  discardPlan(runId: string, workItemId: string): Promise<boolean>;
  approvePullRequest(workItemId: string): Promise<boolean>;
  approvePreviewForPr(workItemId: string): Promise<boolean>;
  createPullRequestReviewComment(workItemId: string, request: { filePath: string; side: string; lineNumber: number; diffLine: string; body: string }): Promise<PullRequestReviewCommentDto | null>;
  updatePullRequestReviewComment(workItemId: string, commentId: string, request: { body?: string | null; status?: string | null }): Promise<PullRequestReviewCommentDto | null>;
  deletePullRequestReviewComment(workItemId: string, commentId: string): Promise<boolean>;
  fixPullRequestReviewCommentsWithAi(workItemId: string): Promise<boolean>;
  createAiPlanReviewComment(workItemId: string, aiRunId: string, request: { anchorKey: string; quotedText: string; body: string }): Promise<AiPlanReviewCommentDto | null>;
  updateAiPlanReviewComment(workItemId: string, aiRunId: string, commentId: string, request: { body?: string | null; status?: string | null }): Promise<AiPlanReviewCommentDto | null>;
  deleteAiPlanReviewComment(workItemId: string, aiRunId: string, commentId: string): Promise<boolean>;
  startPreview(workItemId: string): Promise<boolean>;
  stopPreview(workItemId: string): Promise<boolean>;
  addComment(id: string, body: string): Promise<boolean>;
  addCommentAndAskAi(id: string, body: string): Promise<boolean>;
  updateComment(commentId: string, workItemId: string, body: string): Promise<boolean>;
  deleteComment(commentId: string, workItemId: string): Promise<boolean>;
  createTeam(name: string): Promise<boolean>;
  inviteTeamMember(teamId: string, email: string, role: string): Promise<boolean>;
  assignTeamToBoard(boardId: string, teamId: string, role: string): Promise<boolean>;
  removeTeamFromBoard(boardId: string, teamId: string): Promise<boolean>;
  moveCard(id: string, status: string, sortOrder: number): Promise<void>;
};

type SyncBoardRepositoryRequest = {
  repositoryId?: string | null;
  name?: string | null;
  owner?: string | null;
  private?: boolean;
  description?: string | null;
  installationId?: number | null;
  implementationProfile?: string | null;
  createNew?: boolean;
  remoteUrl?: string | null;
  webUrl?: string | null;
  defaultBranch?: string | null;
};

function useStateFromHash(): [View, (view: View) => void] {
  const readHash = () => {
    const next = (window.location.hash.replace('#', '') as View) || 'board';
    return ['dashboard', 'board', 'timeline', 'gitops', 'ai', 'environment', 'configuration', 'teams', 'settings'].includes(next) ? next : 'board';
  };
  const [view, setViewState] = React.useState<View>(readHash);
  React.useEffect(() => {
    const onHashChange = () => setViewState(readHash());
    window.addEventListener('hashchange', onHashChange);
    return () => window.removeEventListener('hashchange', onHashChange);
  }, []);
  const setView = (next: View) => {
    window.location.hash = next;
    setViewState(next);
  };
  return [view, setView];
}

function Sidebar({ view, showGitOps, boards, selectedBoardId, activeBoard, onSelectBoard, onAddBoard, onChange, onNewCard }: {
  view: View;
  showGitOps: boolean;
  boards: Board[];
  selectedBoardId: string | null;
  activeBoard: Board | null;
  onSelectBoard: (id: string) => void;
  onAddBoard: () => void;
  onChange: (view: View) => void;
  onNewCard: () => void;
}) {
  return (
    <aside className="sidebar">
      <div className="brand">
        <div className="brand-mark"><SquareTerminal size={20} /></div>
        <div>
          <div className="brand-name">Rosenvall</div>
          <div className="brand-subtitle">DevOps Engine</div>
        </div>
      </div>
      {boards.length > 0 && (
        <div className="sidebar-board-picker">
          <BoardSelector boards={boards} selectedBoardId={selectedBoardId ?? activeBoard?.id ?? ''} onSelect={onSelectBoard} onAdd={onAddBoard} />
        </div>
      )}
      <button className="primary-action" onClick={onNewCard}><Plus size={16} />New card</button>
      <nav className="side-nav board-nav" aria-label="Board navigation">
        <NavButton active={view === 'board'} icon={<PanelLeft size={20} />} label="Board" onClick={() => onChange('board')} />
        <NavButton active={view === 'timeline'} icon={<History size={20} />} label="Timeline" onClick={() => onChange('timeline')} />
        {showGitOps && <NavButton active={view === 'gitops'} icon={<Boxes size={20} />} label="GitOps" onClick={() => onChange('gitops')} />}
        <NavButton active={view === 'ai'} icon={<Bot size={20} />} label="AI" onClick={() => onChange('ai')} />
        <NavButton active={view === 'environment'} icon={<KeyRound size={20} />} label="Environment" onClick={() => onChange('environment')} />
        <NavButton active={view === 'configuration'} icon={<Settings size={20} />} label="Configuration" onClick={() => onChange('configuration')} />
      </nav>
      <nav className="side-nav global-nav" aria-label="Global navigation">
        <NavButton active={view === 'dashboard'} icon={<LayoutDashboard size={20} />} label="Dashboard" onClick={() => onChange('dashboard')} />
        <NavButton active={view === 'teams'} icon={<Users size={20} />} label="Teams" onClick={() => onChange('teams')} />
        <NavButton active={view === 'settings'} icon={<Settings size={20} />} label="Settings" onClick={() => onChange('settings')} />
      </nav>
    </aside>
  );
}

function NavButton({ active, icon, label, onClick }: { active: boolean; icon: React.ReactNode; label: string; onClick: () => void }) {
  return (
    <button className={active ? 'nav-item active' : 'nav-item'} onClick={onClick}>
      {icon}
      <span>{label}</span>
    </button>
  );
}

function Topbar({ query, onQueryChange, userName, userEmail, onLogout }: {
  query: string;
  onQueryChange: (query: string) => void;
  userName: string | null;
  userEmail?: string;
  onLogout?: () => void;
}) {
  const [menuOpen, setMenuOpen] = React.useState(false);
  const menuRef = React.useRef<HTMLDivElement | null>(null);

  React.useEffect(() => {
    if (!menuOpen) return;
    const closeOnOutsideClick = (event: MouseEvent) => {
      if (menuRef.current && !menuRef.current.contains(event.target as Node)) {
        setMenuOpen(false);
      }
    };
    document.addEventListener('mousedown', closeOnOutsideClick);
    return () => document.removeEventListener('mousedown', closeOnOutsideClick);
  }, [menuOpen]);

  return (
    <header className="topbar">
      <label className="search">
        <Search size={18} />
        <input value={query} onChange={(event) => onQueryChange(event.target.value)} placeholder="Search work items..." />
      </label>
      <div className="user-menu" ref={menuRef}>
        <button
          aria-expanded={menuOpen}
          aria-haspopup="menu"
          className="avatar avatar-button"
          onClick={() => setMenuOpen((current) => !current)}
          type="button"
        >
          {userName ? initials(userName) : 'CR'}
        </button>
        {menuOpen && (
          <div className="user-menu-popover" role="menu">
            <div className="user-menu-identity">
              <strong>{userName ?? 'Local user'}</strong>
              {userEmail && <span>{userEmail}</span>}
            </div>
            {onLogout && (
              <button
                className="user-menu-action"
                onClick={() => {
                  setMenuOpen(false);
                  void onLogout();
                }}
                role="menuitem"
                type="button"
              >
                <LogOut size={16} />
                Log out
              </button>
            )}
          </div>
        )}
      </div>
    </header>
  );
}

function Loading({ message = 'Loading Rosenvall DevOps...' }: { message?: string }) {
  return <section className="page"><div className="panel state-panel">{message}</div></section>;
}

function SignedOutPanel({ onLogin }: { onLogin: () => Promise<void> }) {
  return (
    <section className="page">
      <div className="panel state-panel">
        <h2>Signed out</h2>
        <p>You are signed out locally. Log in again to choose an Authentik account.</p>
        <button className="primary-action narrow" onClick={() => void onLogin()}>Log in</button>
      </div>
    </section>
  );
}

function ErrorPanel({ message, onRetry }: { message: string; onRetry: () => void }) {
  const isWorkspaceAccessIssue = message.toLowerCase().includes('no workspace available');
  return (
    <section className="page">
      <div className="panel state-panel">
        <h2>{isWorkspaceAccessIssue ? 'No workspace available' : 'API unavailable'}</h2>
        <p>{message}</p>
        <button className="primary-action narrow" onClick={onRetry}>Retry</button>
      </div>
    </section>
  );
}

function DashboardView({ workspace, board, previews, events, pipelines, metrics, actions }: { workspace: Workspace; board: Board; previews: PreviewEnvironmentDto[]; events: PreviewEventDto[]; pipelines: PipelineStatusDto[]; metrics: MetricsDto; actions: BoardActions }) {
  const items = board.columns.flatMap((column) => column.items);
  const activeAi = items.filter((item) => item.aiStatus && item.aiStatus !== 'Completed');
  const openPrs = items.filter((item) => item.pullRequestUrl && !pipelines.some((pipeline) => pipeline.workItemId === item.id && pipeline.stage === 'PR' && pipeline.status === 'Approved')).length;
  const completedAi = items.filter((item) => item.aiStatus === 'Completed').length;
  return (
    <section className="page">
      <div className="page-heading">
        <div>
          <h1>Workspace Overview</h1>
          <p>{workspace.environmentName} - {workspace.region}</p>
        </div>
        <div className="health"><span />System Healthy</div>
      </div>
      <div className="metrics">
        <Metric label="Work items" value={items.length.toString()} note={`${activeAi.length} active AI jobs`} icon={<Boxes size={22} />} />
        <Metric label="Open PRs" value={openPrs.toString()} note="Waiting for human approval" icon={<GitPullRequest size={22} />} />
        <Metric label="AI completed" value={completedAi.toString()} note="Completed implementations" icon={<Sparkles size={22} />} accent />
        <Metric label="Tokens" value={compactNumber(metrics.tokensUsed)} note={`${metrics.codeAdded} added / ${metrics.codeDeleted} removed`} icon={<Activity size={22} />} />
      </div>
      <div className="dashboard-grid">
        <section className="panel large">
          <PanelHeader icon={<Bot size={22} />} title="Active AI jobs" />
          {activeAi.length === 0 && <EmptyState>No active AI jobs.</EmptyState>}
          {activeAi.map((item) => (
            <div className="job" key={item.id}>
              <div className="job-body">
                <div className="job-row"><strong>{item.title}</strong><span>{item.aiStatus}</span></div>
                <code>{item.key} - {item.type}</code>
              </div>
            </div>
          ))}
        </section>
        <section className="panel preview-panel">
          <PanelHeader icon={<ExternalLink size={22} />} title="Demo environments" />
          {previews.length === 0 && <EmptyState>No demo environments.</EmptyState>}
          {previews.map((preview) => (
            <div className="preview-row" key={preview.id}>
              <div className="row-title"><code>{preview.workItemKey}</code><span className={statusClass(preview.status)}>{preview.status}</span></div>
              <SafeExternalLink href={preview.url}>{preview.url}</SafeExternalLink>
              <p>{preview.namespace}</p>
              {preview.workItemId && (
                <div className="row-actions">
                  {preview.status !== 'Running' && <button className="secondary compact" onClick={() => void actions.startPreview(preview.workItemId!)}><Play size={13} />Start</button>}
                </div>
              )}
            </div>
          ))}
        </section>
        <section className="panel large">
          <PanelHeader icon={<Activity size={22} />} title="Pipeline status" />
          {pipelines.length === 0 && <EmptyState>No pipeline activity.</EmptyState>}
          {pipelines.slice(0, 10).map((pipeline) => (
            <div className="list-row" key={pipeline.id}>
              <div><strong>{pipeline.workItemKey}</strong><p>{pipeline.workItemTitle}</p></div>
              <div className="pipeline-actions">
                <span className={statusClass(pipeline.status)}>{pipeline.stage}: {pipeline.status}</span>
                {pipeline.status === 'Queued' && <button className="secondary compact inline" onClick={() => void actions.executePipeline(pipeline.id)}><Play size={13} />Run</button>}
              </div>
            </div>
          ))}
        </section>
        <section className="panel preview-panel">
          <PanelHeader icon={<History size={22} />} title="Runtime history" />
          {events.length === 0 && <EmptyState>No runtime history.</EmptyState>}
          {events.slice(0, 12).map((event) => (
            <div className="history-row" key={event.id}>
              <div className="row-title"><code>{event.workItemKey}</code><span className={statusClass(event.eventType)}>{event.eventType}</span></div>
              <p>{event.message}</p>
              <time>{relativeTime(event.createdAt)}</time>
            </div>
          ))}
        </section>
      </div>
    </section>
  );
}

function Metric({ label, value, note, icon, accent }: { label: string; value: string; note: string; icon: React.ReactNode; accent?: boolean }) {
  return (
    <div className="metric">
      <div className="metric-top"><span>{label}</span>{icon}</div>
      <strong>{value}</strong>
      <p className={accent ? 'ok' : ''}>{note}</p>
    </div>
  );
}

function BoardHeader({ board, subtitle, onSyncBoard, children }: {
  board: Board;
  subtitle: string;
  onSyncBoard?: () => void;
  children?: React.ReactNode;
}) {
  const repositoryUrl = boardRepositoryUrl(board);
  const appUrl = boardPublicAppUrl(board);
  const appStatus = boardPublicAppStatusLabel(board);
  const syncable = onSyncBoard && canSyncBoardToProvider(board);
  const profile = board.repository?.implementationProfile;
  return (
    <div className="page-heading board-header compact-heading">
      <div className="board-heading-copy">
        <div className="board-title-line">
          <h1>{board.name}</h1>
          <span className={board.repositorySyncState === 'GitOps board' ? 'state-good' : board.repositorySyncState === 'Demo board' ? 'state-muted' : board.repository ? 'state-muted' : 'state-warn'}>
            {boardSyncLabel(board)}
          </span>
          {profile && profile !== 'code-repo' && <span className="state-muted">{profileLabel(profile)}</span>}
        </div>
        <p>{subtitle}</p>
      </div>
      <div className="board-header-actions">
        {appUrl && <SafeExternalLink className="primary-action" href={appUrl}><ExternalLink size={16} />Go to app</SafeExternalLink>}
        {!appUrl && appStatus && <span className={appStatus === 'App failed' ? 'state-bad' : appStatus === 'App deploying' ? 'state-warn' : 'state-muted'}>{appStatus}</span>}
        {repositoryUrl && <SafeExternalLink className="secondary" href={repositoryUrl}><ExternalLink size={16} />Go to repository</SafeExternalLink>}
        {syncable && <button className="secondary" onClick={onSyncBoard} type="button"><Github size={16} />Sync to provider</button>}
        {children}
      </div>
    </div>
  );
}

function BoardHierarchyPanel({ board, actions }: { board: Board; actions: BoardActions }) {
  const items = React.useMemo(() => allBoardItems(board), [board]);
  const childMap = React.useMemo(() => {
    const map = new Map<string, WorkItemSummary[]>();
    for (const item of items) {
      if (!item.parentWorkItemId) continue;
      const children = map.get(item.parentWorkItemId) ?? [];
      children.push(item);
      map.set(item.parentWorkItemId, children);
    }
    for (const children of map.values()) {
      children.sort(compareWorkItemsForHierarchy);
    }
    return map;
  }, [items]);
  const itemIds = React.useMemo(() => new Set(items.map((item) => item.id)), [items]);
  const roots = React.useMemo(() => items
    .filter((item) => !item.parentWorkItemId || !itemIds.has(item.parentWorkItemId))
    .sort(compareWorkItemsForHierarchy), [itemIds, items]);
  const hierarchyItems = roots.filter((item) => item.type === 'Epic' || item.childCount || childMap.has(item.id));

  if (hierarchyItems.length === 0) return null;

  return (
    <section className="board-hierarchy-panel" aria-label="Board hierarchy">
      <div className="board-hierarchy-head">
        <strong>Hierarchy</strong>
        <span>{hierarchyItems.length} root {hierarchyItems.length === 1 ? 'item' : 'items'}</span>
      </div>
      <div className="board-hierarchy-scroll">
        {hierarchyItems.map((item) => (
          <HierarchyNode item={item} childMap={childMap} actions={actions} depth={0} key={item.id} />
        ))}
      </div>
    </section>
  );
}

function HierarchyNode({ item, childMap, actions, depth }: { item: WorkItemSummary; childMap: Map<string, WorkItemSummary[]>; actions: BoardActions; depth: number }) {
  const children = childMap.get(item.id) ?? [];
  return (
    <div className="hierarchy-node" style={{ '--depth': depth } as React.CSSProperties}>
      <button className="hierarchy-node-button" type="button" onClick={() => actions.openWorkItem(item.id)}>
        <WorkItemTypeBadges item={item} />
        <strong>{item.key}</strong>
        <span>{item.title}</span>
        {children.length > 0 && <em>{item.doneChildCount ?? 0}/{item.childCount ?? children.length} done</em>}
        {(item.openPullRequestChildCount ?? 0) > 0 && <em>{item.openPullRequestChildCount} PR</em>}
      </button>
      {children.map((child) => (
        <HierarchyNode item={child} childMap={childMap} actions={actions} depth={depth + 1} key={child.id} />
      ))}
    </div>
  );
}

function BoardView({ board, actions, onSyncBoard }: { board: Board; actions: BoardActions; onSyncBoard: () => void }) {
  const sensors = useSensors(useSensor(PointerSensor, { activationConstraint: { distance: 6 } }));
  const itemIds = board.columns.flatMap((column) => column.items.map((item) => item.id));
  const [activeCard, setActiveCard] = React.useState<WorkItemSummary | null>(null);

  function handleDragStart(event: DragStartEvent) {
    const activeId = String(event.active.id);
    setActiveCard(board.columns.flatMap((column) => column.items).find((item) => item.id === activeId) ?? null);
  }

  function handleDragEnd(event: DragEndEvent) {
    const activeId = String(event.active.id);
    setActiveCard(null);
    if (!event.over || !itemIds.includes(activeId)) return;
    const target = resolveDropTarget(board, String(event.over.id));
    if (!target) return;
    void actions.moveCard(activeId, target.status, target.sortOrder);
  }

  return (
    <section className="page board-page">
      <BoardHeader board={board} onSyncBoard={onSyncBoard} subtitle={`${boardRepositorySummary(board)} - ${board.columns.reduce((total, column) => total + column.items.length, 0)} active items`} />
      <BoardHierarchyPanel board={board} actions={actions} />
      <DndContext sensors={sensors} collisionDetection={stableCollisionDetection} onDragStart={handleDragStart} onDragCancel={() => setActiveCard(null)} onDragEnd={handleDragEnd}>
        <div className="board">
          {board.columns.map((column) => (
            <BoardColumnView column={column} actions={actions} key={column.name} />
          ))}
        </div>
        <DragOverlay>{activeCard ? <WorkItemCardPreview item={activeCard} /> : null}</DragOverlay>
      </DndContext>
    </section>
  );
}

function BoardSelector({ boards, selectedBoardId, onSelect, onAdd }: {
  boards: Board[];
  selectedBoardId: string;
  onSelect: (id: string) => void;
  onAdd: () => void;
}) {
  return (
    <select className="board-select" value={selectedBoardId} onChange={(event) => {
      if (event.target.value === '__add_board__') {
        onAdd();
        return;
      }
      onSelect(event.target.value);
    }}>
      {boards.map((board) => (
        <option value={board.id} key={board.id}>{board.name}</option>
      ))}
      <option value="__add_board__">Add board...</option>
    </select>
  );
}

function TimelineView({ board, timeline }: {
  board: Board;
  timeline: TimelineEventDto[];
}) {
  const [filter, setFilter] = React.useState('All');
  const [selectedEventId, setSelectedEventId] = React.useState<string | null>(null);
  const eventRefs = React.useRef<Record<string, HTMLElement | null>>({});
  const filters = ['All', 'Cards', 'Git', 'Pipelines', 'Previews'];
  const filtered = timeline.filter((entry) => filter === 'All' || timelineBucket(entry.kind) === filter);
  const selectEvent = (eventId: string) => {
    setSelectedEventId(eventId);
    requestAnimationFrame(() => eventRefs.current[eventId]?.scrollIntoView({ block: 'center', behavior: 'smooth' }));
  };
  return (
    <section className="page timeline-page">
      <BoardHeader board={board} subtitle={`${boardRepositorySummary(board)} - ${filtered.length} events`}>
        <div className="timeline-filters">
          {filters.map((entry) => (
            <button className={filter === entry ? 'secondary active-filter' : 'secondary'} onClick={() => setFilter(entry)} key={entry}>{entry}</button>
          ))}
        </div>
      </BoardHeader>
      <TimelineFlowGraph events={filtered} selectedEventId={selectedEventId} onSelect={selectEvent} />
      <section className="panel timeline-panel">
        {filtered.length === 0 && <EmptyState>No timeline events for this board.</EmptyState>}
        {filtered.map((entry) => (
          <article
            className={`timeline-row ${timelineClass(entry.kind)} ${selectedEventId === entry.id ? 'selected' : ''}`}
            key={entry.id}
            ref={(node) => { eventRefs.current[entry.id] = node; }}
          >
            <div className="timeline-marker" aria-hidden="true">{timelineIcon(entry.kind)}</div>
            <div>
              <div className="timeline-row-head"><strong>{entry.title}</strong><span className={statusClass(entry.kind)}>{entry.kind}</span></div>
              <p>{entry.message}</p>
              {entry.url && <SafeExternalLink href={entry.url}>{entry.url}</SafeExternalLink>}
            </div>
            <time>{relativeTime(entry.createdAt)}</time>
          </article>
        ))}
      </section>
    </section>
  );
}

function TimelineFlowGraph({ events, selectedEventId, onSelect }: {
  events: TimelineEventDto[];
  selectedEventId: string | null;
  onSelect: (eventId: string) => void;
}) {
  const rows = buildTimelineFlow(events);
  const [query, setQuery] = React.useState('');
  const bodyRef = React.useRef<HTMLDivElement | null>(null);
  const railDragRef = React.useRef<{
    rail: HTMLDivElement;
    pointerId: number;
    startX: number;
    startScrollLeft: number;
    dragging: boolean;
  } | null>(null);
  const suppressRailClickUntil = React.useRef(0);
  const visibleRows = filterTimelineFlowRows(rows, query);
  const handleWheel = (event: React.WheelEvent<HTMLElement>) => {
    const target = event.target as HTMLElement | null;
    const rail = target?.closest('.timeline-flow-rail') as HTMLDivElement | null;
    if (rail && (Math.abs(event.deltaX) > Math.abs(event.deltaY) || event.shiftKey)) {
      const delta = event.shiftKey && event.deltaX === 0 ? event.deltaY : event.deltaX;
      if (delta !== 0 && rail.scrollWidth > rail.clientWidth) {
        event.preventDefault();
        rail.scrollLeft += delta;
      }
      return;
    }
    if (event.deltaY === 0) return;
    const body = bodyRef.current;
    if (!body) return;
    const maxScrollTop = body.scrollHeight - body.clientHeight;
    if (maxScrollTop <= 0) return;
    event.preventDefault();
    body.scrollTop = containedWheelScrollTop(body.scrollTop, event.deltaY, maxScrollTop);
  };
  const handleRailPointerDown = (event: React.PointerEvent<HTMLDivElement>) => {
    if (event.button !== 0) return;
    const rail = event.currentTarget;
    if (rail.scrollWidth <= rail.clientWidth) return;
    railDragRef.current = {
      rail,
      pointerId: event.pointerId,
      startX: event.clientX,
      startScrollLeft: rail.scrollLeft,
      dragging: false
    };
    rail.setPointerCapture(event.pointerId);
  };
  const handleRailPointerMove = (event: React.PointerEvent<HTMLDivElement>) => {
    const state = railDragRef.current;
    if (!state || state.pointerId !== event.pointerId) return;
    const delta = event.clientX - state.startX;
    if (Math.abs(delta) > 3) {
      state.dragging = true;
      state.rail.classList.add('dragging');
      event.preventDefault();
    }
    if (state.dragging) {
      state.rail.scrollLeft = state.startScrollLeft - delta;
    }
  };
  const handleRailPointerEnd = (event: React.PointerEvent<HTMLDivElement>) => {
    const state = railDragRef.current;
    if (!state || state.pointerId !== event.pointerId) return;
    if (state.dragging) {
      suppressRailClickUntil.current = Date.now() + 180;
    }
    state.rail.classList.remove('dragging');
    if (state.rail.hasPointerCapture(event.pointerId)) {
      state.rail.releasePointerCapture(event.pointerId);
    }
    railDragRef.current = null;
  };
  return (
    <section className="panel timeline-flow-panel" aria-label="Timeline flow graph" onWheel={handleWheel}>
      <div className="timeline-flow-toolbar">
        <div>
          <strong>Flow</strong>
          <span>{visibleRows.length} of {rows.length} tasks</span>
        </div>
        <label className="timeline-flow-search">
          <Search size={15} />
          <input value={query} onChange={(event) => setQuery(event.target.value)} placeholder="Search topic or task id..." />
        </label>
      </div>
      <div className="timeline-flow-scroll">
        <div className="timeline-flow-body" ref={bodyRef}>
          {rows.length > 0 && visibleRows.length === 0 && <EmptyState>No matching flow rows.</EmptyState>}
          {visibleRows.map((row) => (
            <div className="timeline-flow-row" key={row.id}>
              <div className="timeline-flow-title" title={row.taskKey ? `${row.topic} ${row.taskKey}` : row.topic}>
                <strong>{row.topic}</strong>
                {row.taskKey && <span>{row.taskKey}</span>}
              </div>
              <div
                className="timeline-flow-rail"
                onPointerCancel={handleRailPointerEnd}
                onPointerDown={handleRailPointerDown}
                onPointerMove={handleRailPointerMove}
                onPointerUp={handleRailPointerEnd}
              >
                <div className="timeline-flow-rail-inner" style={{ minWidth: `max(100%, ${Math.max(row.nodes.length * 48, 320)}px)` }}>
                  {row.nodes.map((node) => (
                    <button
                      className={`timeline-flow-node ${timelineFlowClass(node.lane, node.kind)} ${selectedEventId === node.id ? 'selected' : ''}`}
                      key={node.id}
                      onClick={(event) => {
                        if (Date.now() < suppressRailClickUntil.current) {
                          event.preventDefault();
                          return;
                        }
                        onSelect(node.id);
                      }}
                      type="button"
                      title={`${node.kind}: ${node.title}`}
                      aria-label={`${node.kind}: ${node.title}`}
                    >
                      {timelineFlowIcon(node.lane, node.kind)}
                    </button>
                  ))}
                </div>
              </div>
            </div>
          ))}
          {rows.length === 0 && <EmptyState>No flow events for this filter.</EmptyState>}
        </div>
      </div>
    </section>
  );
}

function GitOpsView({ board, gitOpsApplications, actions, onBack }: {
  board: Board;
  gitOpsApplications: GitOpsApplicationsResponseDto;
  actions: BoardActions;
  onBack: () => void;
}) {
  return (
    <section className="page gitops-page">
      <BoardHeader board={board} subtitle={`${boardRepositorySummary(board)} - ArgoCD application status`}>
        <button className="secondary" onClick={onBack}>Back to board</button>
      </BoardHeader>
      <div className="gitops-content">
        <SectionTitle icon={<Settings size={22} />} title="GitOps settings" />
        <section className="panel form-panel">
          <GitOpsSettingsForm board={board} actions={actions} />
        </section>
        <SectionTitle icon={<Boxes size={22} />} title="Applications" />
        <section className="panel form-panel">
          <GitOpsApplicationsPanel response={gitOpsApplications} />
        </section>
      </div>
    </section>
  );
}

function BoardColumnView({ column, actions }: { column: BoardColumn; actions: BoardActions }) {
  const { setNodeRef, isOver } = useDroppable({ id: `column:${column.name}`, data: { status: column.name } });
  return (
    <section ref={setNodeRef} className={column.name === 'AI Planning' ? 'column ai-column' : isOver ? 'column over' : 'column'}>
      <div className="column-head">
        <strong>{column.name === 'AI Planning' && <Sparkles size={16} />} {column.name}</strong>
        <span>{column.items.length}</span>
        <button className="icon-button" onClick={() => actions.openCreateCard(column.name)} aria-label={`Create card in ${column.name}`}><Plus size={16} /></button>
      </div>
      <SortableContext items={column.items.map((item) => item.id)} strategy={verticalListSortingStrategy}>
        <div className="card-stack">
          {column.items.map((item) => <WorkItemCard item={item} actions={actions} key={item.id} />)}
        </div>
      </SortableContext>
    </section>
  );
}

function WorkItemCard({ item, actions }: { item: WorkItemSummary; actions: BoardActions }) {
  const { attributes, listeners, setNodeRef, transform, transition, isDragging } = useSortable({
    id: item.id,
    data: { type: 'card', status: item.status }
  });
  const style = { transform: CSS.Transform.toString(transform), transition };
  return (
    <article
      ref={setNodeRef}
      style={style}
      className={`${item.status === 'AI Planning' ? 'card ai-card' : 'card'}${isDragging ? ' dragging' : ''}`}
      onClick={() => actions.openWorkItem(item.id)}
      {...attributes}
      {...listeners}
    >
      <div className="card-row">
        <WorkItemTypeBadges item={item} />
        <code>{item.key}</code>
      </div>
      <h3>{item.title}</h3>
      {(item.parentKey || item.childCount) && (
        <div className="hierarchy-chip">
          {item.parentKey ? <span>{item.parentKey}</span> : <span>Root</span>}
          {(item.childCount ?? 0) > 0 && <strong>{item.doneChildCount ?? 0}/{item.childCount} done</strong>}
        </div>
      )}
      {item.aiStatus && <div className="ai-state"><Sparkles size={14} />{item.aiStatus}</div>}
      {item.previewUrl && <div className="preview-chip"><ExternalLink size={13} />Demo ready</div>}
      <div className="card-footer"><span>{item.assignee ?? 'Unassigned'}</span><span>{item.commentCount ? `${item.commentCount} comments` : ''}</span></div>
    </article>
  );
}

function WorkItemCardPreview({ item }: { item: WorkItemSummary }) {
  return (
    <article className={item.status === 'AI Planning' ? 'card ai-card overlay-card' : 'card overlay-card'}>
      <div className="card-row"><WorkItemTypeBadges item={item} /><code>{item.key}</code></div>
      <h3>{item.title}</h3>
      {item.aiStatus && <div className="ai-state"><Sparkles size={14} />{item.aiStatus}</div>}
    </article>
  );
}

function WorkItemTypeBadges({ item }: { item: Pick<WorkItemSummary, 'type' | 'isBug'> }) {
  return (
    <span className="type-stack">
      <span className="type">{item.type}</span>
      {item.isBug && <span className="type bug">Bug</span>}
    </span>
  );
}

function WorkItemModal({ detail, aiRuns, busy, busyLabel, board, aiProvider, aiModel, assigneeOptions, actor, actions, onClose }: {
  detail: WorkItemDetail;
  aiRuns: AiRun[];
  busy: boolean;
  busyLabel: string | null;
  board: Board | null;
  aiProvider: string | null;
  aiModel: string | null;
  assigneeOptions: AssigneeOption[];
  actor: string;
  actions: BoardActions;
  onClose: () => void;
}) {
  const [form, setForm] = React.useState<WorkItemForm>(() => formFromDetail(detail));
  const [lastSavedForm, setLastSavedForm] = React.useState<WorkItemForm>(() => formFromDetail(detail));
  const [autosaveStatus, setAutosaveStatus] = React.useState<WorkItemAutosaveStatus>('idle');
  const [autosaveError, setAutosaveError] = React.useState<string | null>(null);
  const [comment, setComment] = React.useState('');
  const sortedPlans = React.useMemo(() => [...aiRuns].sort((left, right) => left.sequenceNumber - right.sequenceNumber || left.createdAt.localeCompare(right.createdAt)), [aiRuns]);
  const defaultPlan = [...sortedPlans].reverse().find((run) => run.status === 'PlanReady') ?? sortedPlans[sortedPlans.length - 1];
  const [selectedPlanId, setSelectedPlanId] = React.useState<string | null>(() => defaultPlan?.id ?? null);
  const selectedPlan = sortedPlans.find((run) => run.id === selectedPlanId) ?? defaultPlan;
  const targetRepositories = board?.repositories?.length ? board.repositories : board?.repository ? [{ boardId: board.id, repositoryId: board.repository.id, isPrimary: true, implementationProfile: board.repository.implementationProfile, implementationWorkflow: board.implementationWorkflow ?? board.repository.implementationWorkflow, repository: board.repository }] : [];
  const [targetRepositoryId, setTargetRepositoryId] = React.useState<string | null>(() => targetRepositories.find((entry) => entry.isPrimary)?.repositoryId ?? targetRepositories[0]?.repositoryId ?? null);
  const selectedTargetRepository = targetRepositories.find((entry) => entry.repositoryId === targetRepositoryId) ?? targetRepositories.find((entry) => entry.isPrimary) ?? targetRepositories[0];
  const selectedWorkflow = workflowForRepositoryProfile(selectedTargetRepository?.implementationProfile ?? board?.repository?.implementationProfile, selectedTargetRepository?.implementationWorkflow ?? board?.implementationWorkflow ?? board?.repository?.implementationWorkflow ?? null);

  React.useEffect(() => {
    const nextForm = formFromDetail(detail);
    setForm(nextForm);
    setLastSavedForm(nextForm);
    setAutosaveStatus('idle');
    setAutosaveError(null);
  }, [detail]);

  React.useEffect(() => {
    if (!selectedPlanId || !sortedPlans.some((run) => run.id === selectedPlanId)) {
      setSelectedPlanId(defaultPlan?.id ?? null);
    }
  }, [defaultPlan?.id, selectedPlanId, sortedPlans]);
  React.useEffect(() => {
    if (targetRepositoryId && targetRepositories.some((entry) => entry.repositoryId === targetRepositoryId)) return;
    setTargetRepositoryId(targetRepositories.find((entry) => entry.isPrimary)?.repositoryId ?? targetRepositories[0]?.repositoryId ?? null);
  }, [targetRepositories, targetRepositoryId]);
  const activeImplementationRun = latestImplementationRun(detail.implementationRuns);
  const activeRepositoryCleanupRun = latestRepositoryCleanupRun(detail.repositoryCleanupRuns);
  const cleanupPending = isImplementationRunPendingStatus(activeRepositoryCleanupRun?.status);
  const cleanupReady = activeRepositoryCleanupRun?.status === 'PullRequestReady' || activeRepositoryCleanupRun?.status === 'Merged';
  const hasRepositoryPr = !!detail.item.pullRequestUrl || (detail.implementationRuns ?? []).some((run) => !!run.pullRequestUrl);
  const canApprovePreviewForPr = selectedWorkflow === 'preview-then-pr' &&
    detail.preview?.status === 'Running' &&
    (detail.preview.sourceFiles?.length ?? 0) > 0 &&
    !detail.development?.pullRequestUrl &&
    !isImplementationRunPendingStatus(activeImplementationRun?.status);
  const cleanupActionLabel = cleanupPending
    ? 'Repository cleanup running'
    : cleanupReady
      ? 'Delete card after cleanup'
      : hasRepositoryPr && detail.item.status === 'Done'
        ? 'Start repository cleanup'
        : 'Delete and clean up';
  const activityComments = React.useMemo(() => dedupeGeneratedActivityComments(detail.comments), [detail.comments]);
  const [pullRequestDiffState, setPullRequestDiffState] = React.useState<
    { status: 'closed' } |
    { status: 'loading' } |
    { status: 'loaded'; diff: PullRequestDiffDto; selectedPath: string | null } |
    { status: 'error'; message: string }
  >({ status: 'closed' });
  React.useEffect(() => {
    setPullRequestDiffState({ status: 'closed' });
  }, [detail.item.id, detail.development?.pullRequestUrl]);
  const openLocalPullRequestDiff = React.useCallback(async () => {
    setPullRequestDiffState({ status: 'loading' });
    try {
      const diff = await api.get<PullRequestDiffDto>(`/api/work-items/${detail.item.id}/pull-request/diff`);
      setPullRequestDiffState({ status: 'loaded', diff, selectedPath: diff.files[0]?.path ?? null });
    } catch (error) {
      setPullRequestDiffState({ status: 'error', message: error instanceof Error ? error.message : 'Could not load pull request diff.' });
    }
  }, [detail.item.id]);
  const approvePrBusy = busy && (busyLabel === 'Merging local PR and deploying app' || busyLabel === 'Approving PR and deploying app');
  const localDevelopmentApprovalState = detail.development && isLocalGitDevelopmentRecord(detail.development)
    ? buildLocalPullRequestApprovalState(detail.development)
    : null;
  const canApproveDevelopmentPullRequest = !!detail.development?.pullRequestUrl &&
    !detail.development.pullRequestApprovedAt &&
    (!isLocalGitDevelopmentRecord(detail.development) || localDevelopmentApprovalState?.canApprove === true);
  const [activeTab, setActiveTab] = React.useState<WorkItemTabKey>('overview');
  const humanComments = React.useMemo(() => detail.comments.filter((entry) => entry.kind === 'Comment'), [detail.comments]);
  const parentOptions = React.useMemo(() => availableParentOptions(board, detail.item.id, form.type), [board, detail.item.id, form.type]);
  const selectedParentAllowed = !form.parentWorkItemId || parentOptions.some((item) => item.id === form.parentWorkItemId);
  React.useEffect(() => {
    if (selectedParentAllowed) return;
    setForm((current) => ({ ...current, parentWorkItemId: '' }));
  }, [selectedParentAllowed]);
  const latestEpicRun = detail.epicRuns?.[0] ?? null;
  const activeEpicGoal = detail.epicGoalRuns?.find((goal) => goal.status === 'Running' || goal.status === 'Queued') ?? null;
  const canAddFeatureChild = detail.item.type === 'Epic';
  const canAddTaskChild = detail.item.type === 'Epic' || detail.item.type === 'Feature';
  const createChild = React.useCallback(async (type: 'Feature' | 'Task', isBug: boolean) => {
    const title = window.prompt(isBug ? 'Bug task title' : `${type} title`);
    if (!title?.trim()) return;
    await actions.createChildCard(detail.item.id, {
      type,
      title: title.trim(),
      description: '',
      status: board?.columns[0]?.name ?? detail.item.status,
      priority: isBug ? 'High' : 'Medium',
      assignee: detail.item.assignee ?? null,
      isBug
    });
  }, [actions, board?.columns, detail.item.assignee, detail.item.id, detail.item.status]);

  React.useEffect(() => {
    setActiveTab('overview');
  }, [detail.item.id]);

  React.useEffect(() => {
    if (activeTab !== 'pull-request') return;
    if (!detail.development?.pullRequestUrl || !isLocalGitDevelopmentRecord(detail.development)) return;
    if (pullRequestDiffState.status !== 'closed') return;
    void openLocalPullRequestDiff();
  }, [activeTab, detail.development, openLocalPullRequestDiff, pullRequestDiffState.status]);

  React.useEffect(() => {
    if (workItemFormsEqual(form, lastSavedForm)) {
      setAutosaveStatus((current) => current === 'saving' ? current : 'idle');
      setAutosaveError(null);
      return;
    }

    setAutosaveStatus('dirty');
    if (!form.title.trim() || !selectedParentAllowed) return;

    const timeout = window.setTimeout(() => {
      setAutosaveStatus('saving');
      void actions.autosaveCard(detail.item.id, form).then((saved) => {
        if (saved) {
          setLastSavedForm(form);
          setAutosaveStatus('saved');
          setAutosaveError(null);
        } else {
          setAutosaveStatus('error');
          setAutosaveError('The latest card changes could not be saved.');
        }
      });
    }, 800);

    return () => window.clearTimeout(timeout);
  }, [actions, detail.item.id, form, lastSavedForm, selectedParentAllowed]);

  return (
    <ModalFrame title={`${detail.item.key} ${detail.item.title}`} onClose={onClose} size="wide">
      <div className="work-item-shell">
        <nav className="work-item-tabs" aria-label="Work item sections">
          {workItemModalTabs.map((tab) => (
            <button
              className={activeTab === tab.key ? 'active' : ''}
              key={tab.key}
              onClick={() => setActiveTab(tab.key)}
              type="button"
            >
              {tab.label}
            </button>
          ))}
        </nav>

        {activeTab === 'overview' && (
          <section className="work-item-tab-panel overview-tab">
          <div className="form-grid">
            <label>Title<input value={form.title} onChange={(event) => setForm({ ...form, title: event.target.value })} /></label>
            <label>Description<textarea value={form.description} onChange={(event) => setForm({ ...form, description: event.target.value })} /></label>
            <label>Type<select value={form.type} onChange={(event) => {
              const nextType = event.target.value;
              setForm({ ...form, type: nextType, parentWorkItemId: nextType === 'Epic' ? '' : form.parentWorkItemId, isBug: nextType === 'Epic' ? false : form.isBug });
            }}>{workItemTypeOptions.map((option) => <option key={option}>{option}</option>)}</select></label>
            <label>Status<select value={form.status} onChange={(event) => setForm({ ...form, status: event.target.value })}>{board?.columns.map((column) => <option key={column.name}>{column.name}</option>)}</select></label>
            <label>Priority<select value={form.priority} onChange={(event) => setForm({ ...form, priority: event.target.value })}><option>Low</option><option>Medium</option><option>High</option></select></label>
            <label>Assignee<AssigneeSelect value={form.assignee} options={assigneeOptions} onChange={(assignee) => setForm({ ...form, assignee })} /></label>
            <label>Parent<select value={selectedParentAllowed ? form.parentWorkItemId : ''} disabled={form.type === 'Epic'} onChange={(event) => setForm({ ...form, parentWorkItemId: event.target.value })}>
              <option value="">No parent</option>
              {parentOptions.map((item) => <option value={item.id} key={item.id}>{item.key} - {item.title}</option>)}
            </select></label>
            <label className="checkbox-row"><input type="checkbox" checked={form.isBug} disabled={form.type === 'Epic'} onChange={(event) => setForm({ ...form, isBug: event.target.checked })} />Mark as bug</label>
          </div>
          <div className={`autosave-status ${autosaveStatus}`}>
            {autosaveStatus === 'saving' && <span className="spinner" />}
            <span>{workItemAutosaveStatusLabel(autosaveStatus)}</span>
            {autosaveError && <em>{autosaveError}</em>}
          </div>
          {activeRepositoryCleanupRun && (
            <RepositoryCleanupRunPanel run={activeRepositoryCleanupRun} workItemId={detail.item.id} busy={busy} onAdopt={actions.adoptCleanupPullRequest} prominent />
          )}
          <WorkItemOverviewStatus
            board={board}
            development={detail.development}
            preview={detail.preview}
            onOpenPullRequest={() => setActiveTab('pull-request')}
            onOpenPreview={() => setActiveTab('preview')}
            onOpenLogs={() => setActiveTab('logs')}
          />
          <WorkItemHierarchyPanel
            detail={detail}
            latestEpicRun={latestEpicRun}
            activeEpicGoal={activeEpicGoal}
            busy={busy}
            canAddFeatureChild={canAddFeatureChild}
            canAddTaskChild={canAddTaskChild}
            onAddFeature={() => createChild('Feature', false)}
            onAddTask={() => createChild('Task', false)}
            onAddBugTask={() => createChild('Task', true)}
            onOpenWorkItem={actions.openWorkItem}
            onPlanEpic={() => actions.startAiPlan(detail.item.id)}
            onStartEpicRun={() => actions.startEpicRun(detail.item.id)}
            onStartEpicGoal={() => actions.startEpicGoal(detail.item.id)}
            onCancelEpicGoal={(goalId) => actions.cancelEpicGoal(goalId, detail.item.id)}
          />
          <section className="activity">
            <h2>Comments</h2>
            {humanComments.length === 0 && <EmptyState>No human comments yet.</EmptyState>}
            {humanComments.slice(-5).map((entry) => (
              <ActivityEntry
                actor={actor}
                aiRuns={sortedPlans}
                comment={entry}
                key={entry.id}
                onDelete={async (commentId) => {
                  await actions.deleteComment(commentId, detail.item.id);
                }}
                onSelectPlan={setSelectedPlanId}
                onUpdate={async (commentId, body) => {
                  await actions.updateComment(commentId, detail.item.id, body);
                }}
                busy={busy}
              />
            ))}
            <form className="comment-form" onSubmit={(event) => {
              event.preventDefault();
              if (!comment.trim()) return;
              void actions.addComment(detail.item.id, comment.trim()).then((saved) => {
                if (saved) setComment('');
              });
            }}>
              <textarea value={comment} onChange={(event) => setComment(event.target.value)} placeholder="Write a comment or question..." />
              <div className="comment-actions">
                <button className="secondary" disabled={!comment.trim() || busy}>Comment</button>
                <button className="primary-action" type="button" disabled={!comment.trim() || busy} onClick={() => void actions.addCommentAndAskAi(detail.item.id, comment.trim()).then((saved) => { if (saved) setComment(''); })}><Sparkles size={16} />Comment + ask AI</button>
                <button className={`${cleanupReady ? 'primary-action' : 'danger-button'} delete-action-inline`} type="button" disabled={busy || cleanupPending} onClick={() => confirm('Delete this work item and clean up runtime resources and repository PR state? Open implementation PRs will be closed. Merged implementation PRs will create a cleanup PR first.') && void actions.deleteCard(detail.item.id)}><Trash2 size={16} />{cleanupActionLabel}</button>
              </div>
            </form>
          </section>
          </section>
        )}

        {activeTab === 'ai' && (
          <section className="work-item-tab-panel">
            <AiPlanPanel detail={detail} board={board} targetRepositoryId={targetRepositoryId} onTargetRepositoryChange={setTargetRepositoryId} aiRuns={sortedPlans} planReviewComments={detail.aiPlanReviewComments ?? []} selectedPlan={selectedPlan} onSelectPlan={setSelectedPlanId} busy={busy} busyLabel={busyLabel} aiProvider={aiProvider} aiModel={aiModel} actions={actions} />
          </section>
        )}

        {activeTab === 'preview' && (
          <section className="work-item-tab-panel">
            {detail.preview
              ? <PreviewPanel preview={detail.preview} busy={busy} showTerminal={false} orientation="horizontal" onRetry={async () => {
              const retryPlan = selectedPlan ?? sortedPlans.find((run) => run.status === 'Approved' || run.status === 'PlanReady');
              if (detail.preview?.failureReason === 'ImplementationFailed' && retryPlan) {
                await actions.approvePlan(retryPlan.id, detail.item.id);
              } else {
                await actions.startPreview(detail.item.id);
              }
            }} busyLabel={busyLabel} canApproveForPr={canApprovePreviewForPr} onApproveForPr={() => actions.approvePreviewForPr(detail.item.id)} />
              : <EmptyState>No preview has been created for this card.</EmptyState>}
          </section>
        )}

        {activeTab === 'pull-request' && (
          <section className="work-item-tab-panel">
            <PullRequestTab
              development={detail.development}
              pullRequestDiffState={pullRequestDiffState}
              busy={busy}
              busyLabel={busyLabel}
              canApproveDevelopmentPullRequest={canApproveDevelopmentPullRequest}
              localDevelopmentApprovalState={localDevelopmentApprovalState}
              approvePrBusy={approvePrBusy}
              onOpenDiff={openLocalPullRequestDiff}
              onRetryDiff={openLocalPullRequestDiff}
              onSelectFile={(path) => setPullRequestDiffState((current) => current.status === 'loaded' ? { ...current, selectedPath: path } : current)}
              onApprovePullRequest={() => actions.approvePullRequest(detail.item.id)}
              onCreateComment={async (request) => {
                const saved = await actions.createPullRequestReviewComment(detail.item.id, request);
                if (saved) {
                  setPullRequestDiffState((current) => current.status === 'loaded'
                    ? { ...current, diff: { ...current.diff, reviewComments: [...(current.diff.reviewComments ?? []), saved] } }
                    : current);
                }
              }}
              onUpdateComment={async (commentId, request) => {
                const saved = await actions.updatePullRequestReviewComment(detail.item.id, commentId, request);
                if (saved) {
                  setPullRequestDiffState((current) => current.status === 'loaded'
                    ? { ...current, diff: { ...current.diff, reviewComments: (current.diff.reviewComments ?? []).map((entry) => entry.id === saved.id ? saved : entry) } }
                    : current);
                }
              }}
              onDeleteComment={async (commentId) => {
                const deleted = await actions.deletePullRequestReviewComment(detail.item.id, commentId);
                if (deleted) {
                  setPullRequestDiffState((current) => current.status === 'loaded'
                    ? { ...current, diff: { ...current.diff, reviewComments: (current.diff.reviewComments ?? []).filter((entry) => entry.id !== commentId) } }
                    : current);
                }
              }}
              onFixComments={() => actions.fixPullRequestReviewCommentsWithAi(detail.item.id)}
              onApproveDiff={async () => {
                const approved = await actions.approvePullRequest(detail.item.id);
                if (approved) {
                  setPullRequestDiffState((current) => current.status === 'loaded'
                    ? {
                        ...current,
                        diff: {
                          ...current.diff,
                          state: 'closed',
                          pullRequestApprovedAt: new Date().toISOString(),
                          pullRequestApprovedBy: actor,
                          pullRequestMergedAt: current.diff.pullRequestMergedAt ?? new Date().toISOString(),
                          pullRequestFailure: null,
                          canApprove: false,
                          approvalStatus: 'merged',
                          approvalMessage: `Merged and deployed by ${actor}.`
                        }
                      }
                    : current);
                }
                return approved;
              }}
            />
          </section>
        )}

        {activeTab === 'logs' && (
          <section className="work-item-tab-panel logs-tab">
            <WorkItemLogsTab
              aiRuns={sortedPlans}
              selectedPlan={selectedPlan}
              preview={detail.preview}
              implementationRuns={detail.implementationRuns}
              repositoryCleanupRuns={detail.repositoryCleanupRuns}
            />
          </section>
        )}
      </div>
    </ModalFrame>
  );
}

function WorkItemOverviewStatus({ board, development, preview, onOpenPullRequest, onOpenPreview, onOpenLogs }: {
  board: Board | null;
  development?: DevelopmentDto | null;
  preview?: PreviewDto | null;
  onOpenPullRequest: () => void;
  onOpenPreview: () => void;
  onOpenLogs: () => void;
}) {
  const items = buildOverviewDeliverySummary({ board, development, preview });
  const handleAction = (tab: WorkItemTabKey | null | undefined) => {
    if (tab === 'preview') onOpenPreview();
    if (tab === 'pull-request') onOpenPullRequest();
    if (tab === 'logs') onOpenLogs();
  };
  return (
    <section className="overview-delivery-row" aria-label="Delivery status">
      {items.map((item) => (
        <div className="overview-delivery-item" key={item.key}>
          <span>{item.label}</span>
          <strong>{item.value}</strong>
          {item.href ? (
            <SafeExternalLink href={item.href}>{item.actionLabel} <ExternalLink size={13} /></SafeExternalLink>
          ) : item.tab ? (
            <button className="link-button" disabled={item.disabled} type="button" onClick={() => handleAction(item.tab)}>
              {item.actionLabel}
            </button>
          ) : (
            <em>{item.actionLabel}</em>
          )}
        </div>
      ))}
    </section>
  );
}

function WorkItemHierarchyPanel({
  detail,
  latestEpicRun,
  activeEpicGoal,
  busy,
  canAddFeatureChild,
  canAddTaskChild,
  onAddFeature,
  onAddTask,
  onAddBugTask,
  onOpenWorkItem,
  onPlanEpic,
  onStartEpicRun,
  onStartEpicGoal,
  onCancelEpicGoal
}: {
  detail: WorkItemDetail;
  latestEpicRun: EpicRunDto | null;
  activeEpicGoal: EpicGoalRunDto | null;
  busy: boolean;
  canAddFeatureChild: boolean;
  canAddTaskChild: boolean;
  onAddFeature: () => Promise<void>;
  onAddTask: () => Promise<void>;
  onAddBugTask: () => Promise<void>;
  onOpenWorkItem: (id: string) => void;
  onPlanEpic: () => Promise<boolean>;
  onStartEpicRun: () => Promise<boolean>;
  onStartEpicGoal: () => Promise<boolean>;
  onCancelEpicGoal: (goalId: string) => Promise<boolean>;
}) {
  const children = detail.children ?? [];
  const descendants = detail.descendants ?? [];
  const ancestors = detail.ancestors ?? [];
  const isEpicRoot = detail.item.type === 'Epic' && !detail.item.parentWorkItemId;
  const hasHierarchy = isEpicRoot || detail.parent || children.length > 0 || descendants.length > 0;

  if (!hasHierarchy && detail.item.type === 'Task') return null;

  return (
    <section className="hierarchy-detail-panel" aria-label="Card hierarchy">
      <div className="hierarchy-detail-head">
        <div>
          <strong>Hierarchy</strong>
          <p>{hierarchySummary(detail.item)}</p>
        </div>
        <div className="hierarchy-actions">
          {canAddFeatureChild && <button className="secondary compact" disabled={busy} type="button" onClick={() => void onAddFeature()}><Plus size={14} />Add feature</button>}
          {canAddTaskChild && <button className="secondary compact" disabled={busy} type="button" onClick={() => void onAddTask()}><Plus size={14} />Add task</button>}
          {canAddTaskChild && <button className="secondary compact" disabled={busy} type="button" onClick={() => void onAddBugTask()}><Plus size={14} />Add bug task</button>}
        </div>
      </div>
      {ancestors.length > 0 && (
        <div className="hierarchy-path-row">
          {ancestors.map((ancestor) => (
            <button type="button" onClick={() => onOpenWorkItem(ancestor.id)} key={ancestor.id}>{ancestor.key}</button>
          ))}
          <span>{detail.item.key}</span>
        </div>
      )}
      {children.length > 0 && (
        <div className="child-card-list">
          {children.map((child) => (
            <button type="button" onClick={() => onOpenWorkItem(child.id)} key={child.id}>
              <WorkItemTypeBadges item={child} />
              <strong>{child.key}</strong>
              <span>{child.title}</span>
              {(child.childCount ?? 0) > 0 && <em>{child.doneChildCount ?? 0}/{child.childCount} done</em>}
            </button>
          ))}
        </div>
      )}
      {isEpicRoot && (
        <div className="epic-orchestration">
          <div className="epic-progress">
            <strong>{detail.item.doneChildCount ?? 0}/{detail.item.childCount ?? descendants.length} done</strong>
            <span>{detail.item.blockedChildCount ?? 0} blocked</span>
            <span>{detail.item.openPullRequestChildCount ?? 0} open PRs</span>
          </div>
          <div className="epic-actions">
            <button className="secondary" disabled={busy} type="button" onClick={() => void onPlanEpic()}><Sparkles size={15} />Plan epic</button>
            <button className="secondary" disabled={busy} type="button" onClick={() => void onStartEpicRun()}><Play size={15} />Start epic implementation</button>
            {activeEpicGoal
              ? <button className="danger-button compact" disabled={busy} type="button" onClick={() => void onCancelEpicGoal(activeEpicGoal.id)}>Cancel goal</button>
              : <button className="primary-action" disabled={busy} type="button" onClick={() => void onStartEpicGoal()}><CheckCircle2 size={15} />Start goal</button>}
          </div>
          {latestEpicRun && (
            <div className="epic-run-summary">
              <strong>Latest epic run: {latestEpicRun.status}</strong>
              <p>{latestEpicRun.summary ?? 'Feature agent runs are tracked under this epic.'}</p>
              {latestEpicRun.children.length > 0 && (
                <div className="epic-agent-grid">
                  {latestEpicRun.children.map((child) => (
                    <div className="epic-agent" key={child.workItemId}>
                      <span>{child.workItemKey}</span>
                      <strong>{child.agentRole}</strong>
                      <em>{child.status}</em>
                    </div>
                  ))}
                </div>
              )}
            </div>
          )}
          {activeEpicGoal && <p className="status-line"><span />Goal running since {relativeTime(activeEpicGoal.createdAt)}.</p>}
        </div>
      )}
    </section>
  );
}

function PullRequestTab({
  development,
  pullRequestDiffState,
  busy,
  busyLabel,
  canApproveDevelopmentPullRequest,
  localDevelopmentApprovalState,
  approvePrBusy,
  onOpenDiff,
  onRetryDiff,
  onSelectFile,
  onApprovePullRequest,
  onCreateComment,
  onUpdateComment,
  onDeleteComment,
  onFixComments,
  onApproveDiff
}: {
  development?: DevelopmentDto | null;
  pullRequestDiffState:
    { status: 'closed' } |
    { status: 'loading' } |
    { status: 'loaded'; diff: PullRequestDiffDto; selectedPath: string | null } |
    { status: 'error'; message: string };
  busy: boolean;
  busyLabel: string | null;
  canApproveDevelopmentPullRequest: boolean;
  localDevelopmentApprovalState: ReturnType<typeof buildLocalPullRequestApprovalState> | null;
  approvePrBusy: boolean;
  onOpenDiff: () => Promise<void>;
  onRetryDiff: () => Promise<void>;
  onSelectFile: (path: string) => void;
  onApprovePullRequest: () => Promise<boolean>;
  onCreateComment: PullRequestDiffReviewProps['onCreateComment'];
  onUpdateComment: PullRequestDiffReviewProps['onUpdateComment'];
  onDeleteComment: PullRequestDiffReviewProps['onDeleteComment'];
  onFixComments: PullRequestDiffReviewProps['onFixComments'];
  onApproveDiff: PullRequestDiffReviewProps['onApprovePr'];
}) {
  if (!development?.pullRequestUrl) {
    return <EmptyState>No pull request has been created for this card.</EmptyState>;
  }

  if (!isLocalGitDevelopmentRecord(development)) {
    return (
      <section className="panel pull-request-tab-summary">
        <PanelHeader icon={<Github size={20} />} title="GitHub pull request" />
        <SafeExternalLink className="url-box" href={development.pullRequestUrl}>Open pull request <ExternalLink size={16} /></SafeExternalLink>
        <p className="status-line"><span />{developmentStatusText(development)}</p>
        {canApproveDevelopmentPullRequest && (
          <button className="primary-action side-action" disabled={busy} onClick={() => void onApprovePullRequest()}>
            {approvePrBusy ? <span className="spinner" /> : <CheckCircle2 size={16} />}
            {approvePrBusy ? 'Approving...' : approvePullRequestActionLabel(development)}
          </button>
        )}
      </section>
    );
  }

  return (
    <div className="pull-request-tab">
      <section className="panel pull-request-tab-summary">
        <PanelHeader icon={<GitPullRequest size={20} />} title={pullRequestDisplayLabel(development)} />
        <div className="pull-request-meta">
          <p>{development.repository}</p>
          <code>{development.branch}</code>
          <strong>{localDevelopmentApprovalState?.message ?? developmentStatusText(development)}</strong>
        </div>
        {development.pullRequestFailure && <p className="failure-reason">{development.pullRequestFailure}</p>}
        {pullRequestDiffState.status === 'closed' && (
          <button className="primary-action" disabled={busy} onClick={() => void onOpenDiff()}>
            <GitPullRequest size={16} />
            Open diff and review
          </button>
        )}
      </section>
      {pullRequestDiffState.status !== 'closed' && (
        <PullRequestDiffReview
          state={pullRequestDiffState}
          busy={busy}
          busyLabel={busyLabel}
          onClose={() => undefined}
          onSelectFile={onSelectFile}
          onRetry={() => void onRetryDiff()}
          onCreateComment={onCreateComment}
          onUpdateComment={onUpdateComment}
          onDeleteComment={onDeleteComment}
          onFixComments={onFixComments}
          onApprovePr={onApproveDiff}
        />
      )}
    </div>
  );
}

function WorkItemLogsTab({ aiRuns, selectedPlan, preview, implementationRuns, repositoryCleanupRuns }: {
  aiRuns: AiRun[];
  selectedPlan?: AiRun;
  preview?: PreviewDto | null;
  implementationRuns?: ImplementationRunDto[] | null;
  repositoryCleanupRuns?: RepositoryCleanupRunDto[] | null;
}) {
  const runs = React.useMemo(() => buildWorkItemLogRuns(aiRuns, selectedPlan, preview, implementationRuns, repositoryCleanupRuns), [aiRuns, selectedPlan, preview, implementationRuns, repositoryCleanupRuns]);
  const [selectedRunId, setSelectedRunId] = React.useState<string | null>(runs[0]?.id ?? null);
  const selectedRun = runs.find((run) => run.id === selectedRunId) ?? runs[0] ?? null;
  const defaultStepKey = React.useMemo(() => selectedRun ? defaultPreviewStepKey(selectedRun.steps) : null, [selectedRun]);
  const [selectedStepKey, setSelectedStepKey] = React.useState<string | null>(defaultStepKey);
  React.useEffect(() => {
    if (!runs.some((run) => run.id === selectedRunId)) {
      setSelectedRunId(runs[0]?.id ?? null);
    }
  }, [runs, selectedRunId]);
  React.useEffect(() => {
    if (!selectedRun) return;
    if (!selectedStepKey || !selectedRun.steps.some((step) => step.key === selectedStepKey)) {
      setSelectedStepKey(defaultStepKey);
    }
  }, [defaultStepKey, selectedRun, selectedStepKey]);

  const selectedStep = selectedRun?.steps.find((step) => step.key === selectedStepKey) ?? selectedRun?.steps[0] ?? null;
  if (runs.length === 0) return <EmptyState>No runner logs have been recorded for this card.</EmptyState>;

  return (
    <div className="logs-workspace">
      <nav className="log-run-list" aria-label="Runs">
        {runs.map((run) => (
          <button className={run.id === selectedRun?.id ? 'active' : ''} key={run.id} type="button" onClick={() => {
            setSelectedRunId(run.id);
            setSelectedStepKey(defaultPreviewStepKey(run.steps));
          }}>
            <strong>{run.title}</strong>
            <span>{run.status ?? 'Recorded'}{run.updatedAt ? ` · ${relativeTime(run.updatedAt)}` : ''}</span>
          </button>
        ))}
      </nav>
      {selectedRun && (
        <section className="log-run-detail">
          <HorizontalStepSelector
            steps={selectedRun.steps}
            selectedStepKey={selectedStep?.key ?? null}
            onSelectStep={setSelectedStepKey}
          />
          {selectedStep && (
            <PreviewTerminal
              active={selectedStep.state === 'active'}
              lines={selectedStep.terminalLines}
              title={`${selectedRun.title}: ${selectedStep.title}`}
            />
          )}
        </section>
      )}
    </div>
  );
}

function HorizontalStepSelector({ steps, selectedStepKey, onSelectStep }: { steps: WorkItemTabRun['steps']; selectedStepKey: string | null; onSelectStep: (key: string) => void }) {
  return (
    <ol className="horizontal-stepper" aria-label="Run lifecycle">
      {steps.map((step, index) => (
        <li className={`stepper-item ${step.state} ${step.key === selectedStepKey ? 'selected' : ''}`} key={step.key}>
          <button className="stepper-button" type="button" onClick={() => onSelectStep(step.key)} aria-pressed={step.key === selectedStepKey}>
            <span className="stepper-marker">{step.state === 'done' ? <CheckCircle2 size={13} /> : index + 1}</span>
            <span className="stepper-body">
              <strong>{step.title}</strong>
              <span>{step.description}</span>
              <span className="step-log-count">{step.logCount === 1 ? '1 log line' : `${step.logCount} log lines`}</span>
            </span>
          </button>
        </li>
      ))}
    </ol>
  );
}

type WorkItemLogRun = WorkItemTabRun & {
  terminalTitle: string;
};

function buildWorkItemLogRuns(aiRuns: AiRun[], selectedPlan?: AiRun, preview?: PreviewDto | null, implementationRuns?: ImplementationRunDto[] | null, repositoryCleanupRuns?: RepositoryCleanupRunDto[] | null): WorkItemLogRun[] {
  const runs: WorkItemLogRun[] = [];
  const plan = selectedPlan ?? [...aiRuns].sort((left, right) => right.createdAt.localeCompare(left.createdAt))[0];
  if (plan) {
    runs.push({
      id: `ai-${plan.id}`,
      title: `AI plan #${plan.sequenceNumber}`,
      terminalTitle: 'AI plan',
      status: plan.status,
      updatedAt: plan.createdAt,
      steps: [{
        key: 'plan',
        title: plan.status === 'NeedsInput' ? 'Waiting for input' : 'Plan generated',
        description: plan.status === 'Approved' ? 'The plan is approved for implementation.' : 'The AI planning state for this card.',
        state: plan.status === 'NeedsInput' ? 'active' : 'done',
        terminalLines: [],
        logCount: 0
      }]
    });
  }

  if (preview) {
    const previewSteps = previewStepLogsForDisplay(preview);
    const sourceStep = previewSteps.find((step) => step.key === 'source');
    if (sourceStep) {
      runs.push({
        id: `preview-source-${preview.id}`,
        title: 'Preview source',
        terminalTitle: 'Preview source',
        status: preview.status,
        updatedAt: preview.lastCheckedAt ?? null,
        steps: [sourceStep]
      });
    }
    runs.push({
      id: `preview-deploy-${preview.id}`,
      title: 'Preview deploy',
      terminalTitle: 'Preview deploy',
      status: preview.status,
      updatedAt: preview.lastCheckedAt ?? null,
      steps: previewSteps.filter((step) => step.key !== 'source')
    });
  }

  const latestPreviewPromotion = latestImplementationRun((implementationRuns ?? []).filter((run) => run.runKind === 'preview-promotion'));
  if (latestPreviewPromotion) {
    runs.push(repositoryRunToLogRun(latestPreviewPromotion, 'PR creation'));
  }

  const latestImplementation = latestImplementationRun((implementationRuns ?? []).filter((run) => run.runKind !== 'preview-promotion'));
  if (latestImplementation) {
    runs.push(repositoryRunToLogRun(latestImplementation, 'Implementation'));
  }

  const latestCleanup = latestRepositoryCleanupRun(repositoryCleanupRuns);
  if (latestCleanup) {
    runs.push(repositoryCleanupRunToLogRun(latestCleanup));
  }

  return runs;
}

function repositoryRunToLogRun(run: ImplementationRunDto, title: string): WorkItemLogRun {
  const presentation = repositoryRunPresentation(run.status, run.runKind);
  return {
    id: `implementation-${run.id}`,
    title,
    terminalTitle: presentation.terminalTitle,
    status: run.status,
    updatedAt: run.updatedAt,
    steps: repositoryStepsWithTerminalLines(presentation.steps, run.terminalLines ?? [], run.runKind)
  };
}

function repositoryCleanupRunToLogRun(run: RepositoryCleanupRunDto): WorkItemLogRun {
  const presentation = repositoryRunPresentation(run.status);
  return {
    id: `cleanup-${run.id}`,
    title: 'Cleanup',
    terminalTitle: 'Cleanup log',
    status: run.status,
    updatedAt: run.updatedAt,
    steps: repositoryStepsWithTerminalLines(presentation.steps, run.terminalLines ?? [], null)
  };
}

function repositoryStepsWithTerminalLines(steps: Array<{ key: string; title: string; description: string; state: 'done' | 'blocked' | 'active' | 'pending' }>, terminalLines: PreviewTerminalLineDto[], runKind?: string | null): WorkItemTabRun['steps'] {
  const buckets = new Map(steps.map((step) => [step.key, [] as PreviewTerminalLineDto[]]));
  let currentKey = steps[0]?.key ?? '';
  for (const line of terminalLines) {
    const marker = line.message.match(/RDO_STEP=([A-Za-z0-9_-]+)/)?.[1];
    if (marker) {
      currentKey = normalizeRepositoryLogStep(marker, steps, runKind);
    }
    if (buckets.has(currentKey)) {
      buckets.get(currentKey)?.push(line);
    }
  }

  if (terminalLines.length > 0 && [...buckets.values()].every((lines) => lines.length === 0)) {
    buckets.get(steps.find((step) => step.state === 'active')?.key ?? steps[0]?.key ?? '')?.push(...terminalLines);
  }

  return steps.map((step) => {
    const lines = buckets.get(step.key) ?? [];
    return { ...step, terminalLines: lines, logCount: lines.length };
  });
}

function normalizeRepositoryLogStep(marker: string, steps: Array<{ key: string }>, runKind?: string | null): string {
  if (runKind === 'preview-promotion' && marker === 'Implementing') return 'WritingPreviewSource';
  return steps.some((step) => step.key === marker) ? marker : steps[0]?.key ?? marker;
}

function AiPlanPanel({ detail, board, targetRepositoryId, onTargetRepositoryChange, aiRuns, planReviewComments, selectedPlan, onSelectPlan, busy, busyLabel, aiProvider, aiModel, actions }: {
  detail: WorkItemDetail;
  board: Board | null;
  targetRepositoryId: string | null;
  onTargetRepositoryChange: (repositoryId: string | null) => void;
  aiRuns: AiRun[];
  planReviewComments: AiPlanReviewCommentDto[];
  selectedPlan?: AiRun;
  onSelectPlan: (id: string) => void;
  busy: boolean;
  busyLabel: string | null;
  aiProvider: string | null;
  aiModel: string | null;
  actions: BoardActions;
}) {
  const boardRepositories = board?.repositories?.length ? board.repositories : board?.repository ? [{ boardId: board.id, repositoryId: board.repository.id, isPrimary: true, implementationProfile: board.repository.implementationProfile, implementationWorkflow: board.implementationWorkflow ?? board.repository.implementationWorkflow, repository: board.repository }] : [];
  const targetRepository = boardRepositories.find((entry) => entry.repositoryId === targetRepositoryId) ?? boardRepositories.find((entry) => entry.isPrimary) ?? boardRepositories[0];
  const repositoryProfile = targetRepository?.implementationProfile ?? board?.repository?.implementationProfile ?? 'react-preview';
  const implementationWorkflow = workflowForRepositoryProfile(repositoryProfile, targetRepository?.implementationWorkflow ?? board?.implementationWorkflow ?? board?.repository?.implementationWorkflow ?? null);
  const isRepositoryImplementation = implementationWorkflow === 'direct-pr';
  const repositoryCanRunImplementation = !isRepositoryImplementation || targetRepository?.repository.provider === 'GitHub' || targetRepository?.repository.provider === 'LocalGit';
  const gitOpsSettingsReady = repositoryProfile !== 'gitops-homelab' || !!board?.gitOpsSettings;
  const previewBusy = ['Implementing', 'Applying', 'Provisioning'].includes(detail.preview?.status ?? '');
  const previewRunning = detail.preview?.status === 'Running';
  const previewHasGeneratedSource = (detail.preview?.sourceFiles?.length ?? 0) > 0;
  const relevantImplementationRuns = (detail.implementationRuns ?? []).filter((run) =>
    (!targetRepository?.repositoryId || run.repositoryId === targetRepository.repositoryId) &&
    (!selectedPlan?.id || run.aiRunId === selectedPlan.id));
  const implementationRunsForAction = relevantImplementationRuns.length > 0 ? relevantImplementationRuns : detail.implementationRuns;
  const latestRunForAction = latestImplementationRun(implementationRunsForAction);
  const implementationAction = implementationActionState({
    workflow: implementationWorkflow,
    isRepositoryImplementation,
    repositoryProfile,
    repositoryCanRunImplementation,
    gitOpsSettingsReady,
    hasSelectedPlan: !!selectedPlan,
    selectedPlanStatus: selectedPlan?.status,
    latestRun: latestRunForAction,
    hasPendingRun: (implementationRunsForAction ?? []).some((run) => isImplementationRunPendingStatus(run.status)),
    hasReadyPullRequest: (implementationRunsForAction ?? []).some((run) => run.status === 'PullRequestReady' && !!run.pullRequestUrl),
    previewBusy,
    previewRunning,
    previewHasGeneratedSource
  });
  const planQuestions = React.useMemo(() => selectedPlan?.plan ? extractPlanQuestions(selectedPlan.plan) : [], [selectedPlan?.plan]);
  const planBlocks = React.useMemo(() => splitAiPlanReviewBlocks(selectedPlan?.plan), [selectedPlan?.plan]);
  const commentCountsByRun = React.useMemo(() => planReviewCommentCountsByRun(planReviewComments), [planReviewComments]);
  const selectedPlanComments = React.useMemo(
    () => selectedPlan ? planReviewComments.filter((comment) => comment.aiRunId === selectedPlan.id) : [],
    [planReviewComments, selectedPlan]
  );
  const unresolvedPlanComments = selectedPlan ? unresolvedAiPlanReviewCommentCount(planReviewComments, selectedPlan.id) : 0;
  const selectedPlanCanStart = selectedPlan ? canApproveAiPlanWithComments(selectedPlan.id, planReviewComments) : false;
  const [activeAnchorKey, setActiveAnchorKey] = React.useState<string | null>(null);
  const [commentDraft, setCommentDraft] = React.useState('');
  const [revisionMessage, setRevisionMessage] = React.useState('');
  const [editingCommentId, setEditingCommentId] = React.useState<string | null>(null);
  const [editingBody, setEditingBody] = React.useState('');
  const [implementationStartError, setImplementationStartError] = React.useState<string | null>(null);
  React.useEffect(() => {
    setImplementationStartError(null);
    setActiveAnchorKey(null);
    setCommentDraft('');
    setRevisionMessage('');
    setEditingCommentId(null);
    setEditingBody('');
  }, [selectedPlan?.id, targetRepository?.repositoryId]);
  React.useEffect(() => {
    if (!activeAnchorKey) return;
    if (planBlocks.some((block) => block.anchorKey === activeAnchorKey)) return;
    setActiveAnchorKey(null);
  }, [activeAnchorKey, planBlocks]);
  const startSelectedPlan = React.useCallback(async () => {
    if (!selectedPlan) return;
    setImplementationStartError(null);
    if (!selectedPlanCanStart) {
      setImplementationStartError('Resolve all AI plan review comments before starting implementation.');
      return;
    }
    if (isRepositoryImplementation) {
      const error = await actions.startImplementationRun(detail.item.id, selectedPlan.id, targetRepository?.repositoryId ?? null);
      if (error) {
        setImplementationStartError(error);
      }
      return;
    }

    await actions.approvePlan(selectedPlan.id, detail.item.id);
  }, [actions, detail.item.id, isRepositoryImplementation, selectedPlan, selectedPlanCanStart, targetRepository?.repositoryId]);
  const commentsByAnchor = React.useMemo(() => selectedPlanComments.reduce<Record<string, AiPlanReviewCommentDto[]>>((map, comment) => {
    map[comment.anchorKey] ??= [];
    map[comment.anchorKey].push(comment);
    return map;
  }, {}), [selectedPlanComments]);
  const submitPlanComment = React.useCallback(async (block: AiPlanReviewBlock) => {
    if (!selectedPlan || !commentDraft.trim()) return;
    const saved = await actions.createAiPlanReviewComment(detail.item.id, selectedPlan.id, {
      anchorKey: block.anchorKey,
      quotedText: block.text,
      body: commentDraft.trim()
    });
    if (saved) setCommentDraft('');
  }, [actions, commentDraft, detail.item.id, selectedPlan]);
  const submitRevision = React.useCallback(async () => {
    if (!selectedPlan || !revisionMessage.trim()) return;
    const revised = await actions.reviseAiPlan(detail.item.id, selectedPlan.id, revisionMessage.trim());
    if (revised) setRevisionMessage('');
  }, [actions, detail.item.id, revisionMessage, selectedPlan]);
  return (
    <section className="panel ai-plan-panel">
      <PanelHeader icon={<Bot size={20} />} title="AI plan review" />
      {busyLabel && <div className="inline-progress"><Sparkles size={15} />{busyLabel}...</div>}
      <div className="ai-review-workspace">
        <aside className="plan-version-rail" aria-label="Plan versions">
          <div className="plan-version-header">
            <span>{aiModel ? `${aiProvider ? `${aiProvider} / ` : ''}${aiModel}` : 'Plan versions'}</span>
            <button className="icon-button" disabled={busy} onClick={() => void actions.startAiPlan(detail.item.id)} title="Generate AI plan" type="button">
              <Sparkles size={15} />
            </button>
          </div>
          {aiRuns.length === 0 && <EmptyState>No AI plans yet.</EmptyState>}
          {aiRuns.map((run) => {
            const counts = commentCountsByRun[run.id];
            return (
              <button className={run.id === selectedPlan?.id ? 'plan-version active' : 'plan-version'} key={run.id} onClick={() => onSelectPlan(run.id)} type="button">
                <strong>#{run.sequenceNumber} {planTitle(run)}</strong>
                <span>{run.status} · {run.model}</span>
                {counts && <small>{counts.unresolved} unresolved / {counts.total} comments</small>}
              </button>
            );
          })}
        </aside>
        <main className="plan-review-main">
          {selectedPlan?.plan ? (
            <>
              <div className="plan-review-meta">
                <div>
                  <strong>{selectedPlan.provider} / {selectedPlan.model}</strong>
                  <span>{selectedPlan.status} · {relativeTime(selectedPlan.createdAt)}</span>
                </div>
                {implementationWorkflow !== 'preview-only' && boardRepositories.length > 0 && (
                  <label className="target-repo-select compact">Target repo<select value={targetRepository?.repositoryId ?? ''} onChange={(event) => onTargetRepositoryChange(event.target.value || null)}>
                    {boardRepositories.map((entry) => <option key={entry.repositoryId} value={entry.repositoryId}>{repositoryLabel(entry.repository)} {entry.isPrimary ? '(primary)' : ''} - {profileLabel(entry.implementationProfile)}</option>)}
                  </select></label>
                )}
              </div>
              {detail.aiSession && <p className="session-line">Session: {detail.aiSession.provider} / {detail.aiSession.model} - {detail.aiSession.providerSessionId ? 'Codex resume ready' : 'No provider session id yet'}.</p>}
              {planQuestions.length > 0 && (
                <PlanQuestionStepper
                  busy={busy}
                  questions={planQuestions}
                  onSubmit={async (answers) => {
                    await actions.addCommentAndAskAi(detail.item.id, formatPlanQuestionAnswers(planQuestions, answers));
                  }}
                />
              )}
              <div className="plan-markdown">
                <CommentBody
                  body={selectedPlan.plan}
                  review={{
                    activeAnchorKey,
                    busy,
                    commentDraft,
                    commentsByAnchor,
                    editingBody,
                    editingCommentId,
                    onCancelEdit: () => {
                      setEditingCommentId(null);
                      setEditingBody('');
                    },
                    onDeleteComment: (commentId) => void actions.deleteAiPlanReviewComment(detail.item.id, selectedPlan.id, commentId),
                    onDraftChange: setCommentDraft,
                    onEditingBodyChange: setEditingBody,
                    onSaveEdit: (commentId) => void actions.updateAiPlanReviewComment(detail.item.id, selectedPlan.id, commentId, { body: editingBody.trim() }).then((updated) => {
                      if (updated) {
                        setEditingCommentId(null);
                        setEditingBody('');
                      }
                    }),
                    onSelectAnchor: setActiveAnchorKey,
                    onStartEdit: (comment) => {
                      setEditingCommentId(comment.id);
                      setEditingBody(comment.body);
                    },
                    onSubmitComment: (block) => void submitPlanComment(block),
                    onToggleResolved: (comment) => void actions.updateAiPlanReviewComment(detail.item.id, selectedPlan.id, comment.id, { status: comment.status.toLowerCase() === 'resolved' ? 'Open' : 'Resolved' })
                  }}
                />
              </div>
            </>
          ) : <EmptyState>No AI plan selected.</EmptyState>}
        </main>
        <aside className="plan-review-sidebar">
          <section className="plan-review-card">
            <strong>Ask AI to revise</strong>
            <p>Overview discussion and unresolved plan comments are included in the next plan context.</p>
            <textarea value={revisionMessage} onChange={(event) => setRevisionMessage(event.target.value)} placeholder="Describe what should change in the plan..." />
            <button className="primary-action" disabled={!selectedPlan || !revisionMessage.trim() || busy} onClick={() => void submitRevision()}><Sparkles size={16} />Revise plan with AI</button>
          </section>
          <section className="plan-review-card">
            <strong>Start delivery</strong>
            {implementationStartError && <p className="failure-reason action-error">Implementation did not start: {implementationStartError}</p>}
            {selectedPlan && implementationAction.canStart && (
              <button className="primary-action" disabled={busy || !selectedPlanCanStart} onClick={() => void startSelectedPlan()}><CheckCircle2 size={16} />{implementationAction.label}</button>
            )}
            {selectedPlan?.status === 'PlanReady' && <button className="secondary" disabled={busy} onClick={() => void actions.discardPlan(selectedPlan.id, detail.item.id)}>Discard plan</button>}
            {!selectedPlanCanStart && selectedPlan && <p className="plan-help">Resolve plan comments before starting implementation.</p>}
            {implementationAction.retryContext && <p className="plan-help">{implementationAction.retryContext}</p>}
            {selectedPlan?.status === 'NeedsInput' && <p className="plan-help">Answer the open questions or ask AI to revise the plan.</p>}
            {selectedPlan && ['PlanReady', 'Approved'].includes(selectedPlan.status) && selectedPlanCanStart && <p className="plan-help">{implementationAction.helpText}</p>}
          </section>
        </aside>
      </div>
    </section>
  );
}

function PlanQuestionStepper({ questions, busy, onSubmit }: {
  questions: PlanQuestion[];
  busy: boolean;
  onSubmit: (answers: Record<string, string>) => Promise<void>;
}) {
  const [activeIndex, setActiveIndex] = React.useState(0);
  const [answers, setAnswers] = React.useState<Record<string, string>>({});
  const [customText, setCustomText] = React.useState<Record<string, string>>({});

  React.useEffect(() => {
    setActiveIndex(0);
    setAnswers({});
    setCustomText({});
  }, [questions]);

  const activeQuestion = questions[Math.min(activeIndex, questions.length - 1)];
  const activeAnswer = activeQuestion ? answers[activeQuestion.id] ?? '' : '';
  const customValue = activeQuestion ? customText[activeQuestion.id] ?? '' : '';
  const answeredCount = questions.filter((question) => (answers[question.id] ?? '').trim()).length;
  const canSubmit = answeredCount === questions.length && !busy;

  const setAnswer = (question: PlanQuestion, value: string) => {
    setAnswers((current) => ({ ...current, [question.id]: value }));
  };

  if (!activeQuestion) return null;

  return (
    <section className="plan-question-panel">
      <div className="plan-question-head">
        <div>
          <strong>Blocking questions</strong>
          <p>{answeredCount} of {questions.length} answered. Answers will be sent back as a comment and revised AI plan.</p>
        </div>
        <button className="primary-action compact" disabled={!canSubmit} onClick={() => void onSubmit(answers)} type="button"><Sparkles size={14} />Send answers</button>
      </div>
      <ol className="question-stepper" aria-label="Blocking questions">
        {questions.map((question, index) => {
          const answered = !!answers[question.id]?.trim();
          const state = index === activeIndex ? 'active' : answered ? 'done' : 'pending';
          return (
            <li className={`question-step ${state}`} key={question.id}>
              <button type="button" onClick={() => setActiveIndex(index)}>
                <span>{answered ? <CheckCircle2 size={13} /> : index + 1}</span>
                <strong>Question {index + 1}</strong>
              </button>
            </li>
          );
        })}
      </ol>
      <div className="question-answer-editor">
        <p>{renderInlineMarkdown(activeQuestion.text)}</p>
        <label>Answer<select value={activeAnswer && activeAnswer === customValue ? '__custom__' : activeQuestion.options.includes(activeAnswer) ? activeAnswer : activeAnswer ? '__custom__' : ''} onChange={(event) => {
          const value = event.target.value;
          if (value === '__custom__') {
            setAnswer(activeQuestion, customValue);
          } else {
            setAnswer(activeQuestion, value);
          }
        }}>
          <option value="">Choose answer...</option>
          {activeQuestion.options.map((option) => <option value={option} key={option}>{option}</option>)}
          <option value="__custom__">Custom answer</option>
        </select></label>
        {(!activeQuestion.options.includes(activeAnswer) || activeAnswer === customValue) && (
          <label>Custom answer<input value={customValue} onChange={(event) => {
            const value = event.target.value;
            setCustomText((current) => ({ ...current, [activeQuestion.id]: value }));
            setAnswer(activeQuestion, value);
          }} placeholder="Write the answer for this question" /></label>
        )}
        <div className="question-step-actions">
          <button className="secondary compact inline" disabled={activeIndex === 0} onClick={() => setActiveIndex((index) => Math.max(0, index - 1))} type="button">Previous</button>
          <button className="secondary compact inline" disabled={activeIndex >= questions.length - 1} onClick={() => setActiveIndex((index) => Math.min(questions.length - 1, index + 1))} type="button">Next</button>
        </div>
      </div>
    </section>
  );
}

function ImplementationRunPanel({ run }: { run: ImplementationRunDto }) {
  const pending = isImplementationRunPendingStatus(run.status);
  const failed = run.status === 'Failed';
  const ready = run.status === 'PullRequestReady';
  const isLocalGit = run.pullRequestProvider?.toLowerCase() === 'localgit';
  const presentation = repositoryRunPresentation(run.status, run.runKind);
  const steps = presentation.steps;
  return (
    <section className="panel compact-panel implementation-panel">
      <PanelHeader icon={<GitPullRequest size={20} />} title={presentation.title} />
      <ol className="preview-stepper implementation-stepper" aria-label={presentation.ariaLabel}>
        {steps.map((step, index) => (
          <li className={`stepper-item ${step.state}`} key={step.key}>
            <span className="stepper-marker">{step.state === 'done' ? <CheckCircle2 size={13} /> : index + 1}</span>
            <span className="stepper-body"><strong>{step.title}</strong><span>{step.description}</span></span>
          </li>
        ))}
      </ol>
      <p className="namespace-note">Branch: <code>{run.branch}</code></p>
      {run.commitSha && <p className="namespace-note">Commit: <code>{run.commitSha.slice(0, 12)}</code></p>}
      {ready && run.pullRequestUrl && isLocalGit && <div className="url-box local-url-box">Local pull request{run.pullRequestNumber ? ` #${run.pullRequestNumber}` : ''}<span>{run.pullRequestState ?? 'open'}</span></div>}
      {ready && run.pullRequestUrl && !isLocalGit && <SafeExternalLink className="url-box" href={run.pullRequestUrl}>Open pull request <ExternalLink size={16} /></SafeExternalLink>}
      {failed && run.failureReason && <p className="failure-reason">Reason: {run.failureReason}</p>}
      {(run.jobName || run.podName || run.lastCondition || run.lastEventSummary) && (
        <div className="cleanup-diagnostics">
          {run.jobName && <p>Job: <code>{run.jobName}</code></p>}
          {run.podName && <p>Pod: <code>{run.podName}</code></p>}
          {run.lastCondition && <p>Condition: {run.lastCondition}</p>}
          {run.lastEventSummary && <p>Last event: {run.lastEventSummary}</p>}
        </div>
      )}
      <PreviewTerminal lines={run.terminalLines ?? []} active={pending} title={presentation.terminalTitle} />
      <div className="split-stats"><span>Status<br /><strong>{run.status}</strong></span><span>Updated<br /><strong>{relativeTime(run.updatedAt)}</strong></span></div>
    </section>
  );
}

function RepositoryCleanupRunPanel({ run, workItemId, busy = false, onAdopt, prominent = false }: { run: RepositoryCleanupRunDto; workItemId?: string; busy?: boolean; onAdopt?: (workItemId: string, pullRequestUrl: string) => Promise<boolean>; prominent?: boolean }) {
  const pending = isImplementationRunPendingStatus(run.status);
  const failed = run.status === 'Failed';
  const ready = run.status === 'PullRequestReady' || run.status === 'Merged';
  const steps = repositoryRunPresentation(run.status).steps;
  return (
    <section className={prominent ? 'panel cleanup-panel prominent-cleanup-panel' : 'panel compact-panel cleanup-panel'}>
      <PanelHeader icon={<Trash2 size={20} />} title="Cleanup run" />
      {run.adopted && <p className="namespace-note">Adopted external cleanup PR.</p>}
      <ol className="preview-stepper implementation-stepper" aria-label="Repository cleanup lifecycle">
        {steps.map((step, index) => (
          <li className={`stepper-item ${step.state}`} key={step.key}>
            <span className="stepper-marker">{step.state === 'done' ? <CheckCircle2 size={13} /> : index + 1}</span>
            <span className="stepper-body"><strong>{step.title}</strong><span>{step.description}</span></span>
          </li>
        ))}
      </ol>
      <p className="namespace-note">Branch: <code>{run.branch}</code></p>
      <p className="namespace-note">Source PR: <SafeExternalLink href={run.sourcePullRequestUrl}>open source PR</SafeExternalLink></p>
      {run.commitSha && <p className="namespace-note">Commit: <code>{run.commitSha.slice(0, 12)}</code></p>}
      {ready && run.cleanupPullRequestUrl && <SafeExternalLink className="url-box" href={run.cleanupPullRequestUrl}>Open cleanup pull request <ExternalLink size={16} /></SafeExternalLink>}
      {failed && run.failureReason && <p className="failure-reason">Reason: {run.failureReason}</p>}
      {(run.jobName || run.podName || run.lastCondition || run.lastEventSummary) && (
        <div className="cleanup-diagnostics">
          {run.jobName && <p>Job: <code>{run.jobName}</code></p>}
          {run.podName && <p>Pod: <code>{run.podName}</code></p>}
          {run.lastCondition && <p>Condition: {run.lastCondition}</p>}
          {run.lastEventSummary && <p>Last event: {run.lastEventSummary}</p>}
        </div>
      )}
      {failed && workItemId && onAdopt && <CleanupAdoptionPanel workItemId={workItemId} busy={busy} onAdopt={onAdopt} compact />}
      <PreviewTerminal lines={run.terminalLines ?? []} active={pending} />
      <div className="split-stats"><span>Status<br /><strong>{run.status}</strong></span><span>Updated<br /><strong>{relativeTime(run.updatedAt)}</strong></span></div>
    </section>
  );
}

function CleanupAdoptionPanel({ workItemId, busy, onAdopt, compact = false }: { workItemId: string; busy: boolean; onAdopt: (workItemId: string, pullRequestUrl: string) => Promise<boolean>; compact?: boolean }) {
  const [pullRequestUrl, setPullRequestUrl] = React.useState('');
  const [submitting, setSubmitting] = React.useState(false);
  const disabled = busy || submitting || !pullRequestUrl.trim();
  return (
    <form className={compact ? 'cleanup-adopt-form compact' : 'panel compact-panel cleanup-adopt-form'} onSubmit={(event) => {
      event.preventDefault();
      if (disabled) return;
      setSubmitting(true);
      void onAdopt(workItemId, pullRequestUrl.trim())
        .then((saved) => {
          if (saved) setPullRequestUrl('');
        })
        .finally(() => setSubmitting(false));
    }}>
      {!compact && <PanelHeader icon={<GitPullRequest size={20} />} title="Adopt cleanup PR" />}
      <label>Cleanup PR URL<input value={pullRequestUrl} onChange={(event) => setPullRequestUrl(event.target.value)} placeholder="https://github.com/owner/repo/pull/35" /></label>
      <button className="secondary compact inline" disabled={disabled}><GitPullRequest size={14} />Adopt cleanup PR</button>
    </form>
  );
}

function PreviewPanel({ preview, busy, busyLabel, onRetry, canApproveForPr = false, onApproveForPr, showTerminal = true, orientation = 'vertical' }: { preview: PreviewDto; busy: boolean; busyLabel?: string | null; onRetry: () => Promise<void>; canApproveForPr?: boolean; onApproveForPr?: () => Promise<boolean>; showTerminal?: boolean; orientation?: 'vertical' | 'horizontal' }) {
  const status = preview.status;
  const running = status === 'Running';
  const failed = status === 'Failed';
  const implementing = status === 'Implementing';
  const stopped = status === 'Stopped';
  const waiting = !running && !failed && !stopped;
  const failedDuringSourceGeneration = preview.failureReason === 'ImplementationFailed';
  const canRetrySetup = failed && (failedDuringSourceGeneration || (preview.sourceFiles?.length ?? 0) > 0 || !!preview.staticHtml);
  const actionLabel = failed ? 'Preview failed' : implementing ? 'Implementing plan...' : stopped ? 'Preview stopped' : waiting ? 'Waiting for healthy preview...' : 'Open demo environment';
  const retryLabel = stopped ? 'Recreate preview' : failedDuringSourceGeneration ? 'Retry preview implementation' : 'Retry preview setup';
  const steps = React.useMemo(() => previewStepLogsForDisplay(preview), [preview]);
  const approvingForPr = busy && busyLabel === 'Creating pull request from approved preview';
  const preferredStepKey = React.useMemo(() => defaultPreviewStepKey(steps), [steps]);
  const [selectedStepKey, setSelectedStepKey] = React.useState(preferredStepKey);
  React.useEffect(() => {
    const selectedStep = steps.find((step) => step.key === selectedStepKey);
    if (!selectedStep || (selectedStep.state === 'pending' && selectedStepKey !== preferredStepKey)) {
      setSelectedStepKey(preferredStepKey);
    }
  }, [preferredStepKey, selectedStepKey, steps]);
  const selectedStep = steps.find((step) => step.key === selectedStepKey) ?? steps[0];
  const runtimeDiagnostic = previewRuntimeDiagnostic(preview);
  return (
    <section className={`panel compact-panel preview-panel ${orientation === 'horizontal' ? 'preview-panel-horizontal' : ''}`}>
      <PanelHeader icon={<ExternalLink size={20} />} title="Preview environment" />
      {implementing && <div className="implementation-banner"><Sparkles size={16} /><span>Implementing plan...</span></div>}
      {running
        ? <SafeExternalLink className="demo-link" href={preview.url}>Open demo environment <ExternalLink size={16} /></SafeExternalLink>
        : <button className="demo-link disabled" disabled>{waiting && <span className="spinner" />}{actionLabel}</button>}
      {running && canApproveForPr && onApproveForPr && (
        <button className="primary-action side-action" disabled={busy} onClick={() => void onApproveForPr()}>{approvingForPr ? <span className="spinner" /> : <GitPullRequest size={16} />}{approvingForPr ? 'Creating pull request...' : 'Approve preview and create PR'}</button>
      )}
      {approvingForPr && <p className="namespace-note">Job accepted, waiting for Kubernetes runner to create the pull request...</p>}
      <ol className="preview-stepper" aria-label="Preview lifecycle">
        {steps.map((step, index) => (
          <li className={`stepper-item ${step.state} ${selectedStep?.key === step.key ? 'selected' : ''}`} key={step.key}>
            <button className="stepper-button" type="button" onClick={() => setSelectedStepKey(step.key)} aria-pressed={selectedStep?.key === step.key}>
              <span className="stepper-marker">{step.state === 'done' ? <CheckCircle2 size={13} /> : index + 1}</span>
              <span className="stepper-body">
                <strong>{step.title}</strong>
                <span>{step.description}</span>
                <span className="step-log-count">{step.logCount === 1 ? '1 log line' : `${step.logCount} log lines`}</span>
              </span>
            </button>
          </li>
        ))}
      </ol>
      {preview.namespace && <p className="namespace-note">Namespace: <code>{preview.namespace}</code></p>}
      <p className="preview-message">{previewDisplayMessage(preview.status, preview.message, preview.phase)}</p>
      {runtimeDiagnostic && <p className="failure-reason">{runtimeDiagnostic}</p>}
      {preview.podName && <p className="namespace-note">Pod: <code>{preview.podName}</code></p>}
      {preview.lastCheckedAt && <p className="namespace-note">Last checked {relativeTime(preview.lastCheckedAt)}.</p>}
      {showTerminal && selectedStep && <PreviewTerminal lines={selectedStep.terminalLines} active={isPreviewTerminalLive(status) && selectedStep.state === 'active'} title={selectedStep.title} />}
      <div className="split-stats"><span>Status<br /><strong>{status}</strong></span><span>TTL<br /><strong>{relativeDays(preview.expiresAt)}</strong></span></div>
      {failed && preview.failureReason && <p className="failure-reason">Reason: {preview.failureReason}</p>}
      {failed && preview.failureLog && <pre className="failure-log">{preview.failureLog}</pre>}
      {(failed && canRetrySetup || stopped && ((preview.sourceFiles?.length ?? 0) > 0 || !!preview.staticHtml)) && <button className="primary-action side-action" disabled={busy} onClick={() => void onRetry()}><Play size={16} />{retryLabel}</button>}
      {failed && !canRetrySetup && <p className="namespace-note">Source generation did not finish, so there is no Kubernetes preview manifest to retry.</p>}
    </section>
  );
}

function previewRuntimeDiagnostic(preview: PreviewDto): string | null {
  const text = [
    preview.message,
    preview.failureReason,
    preview.failureLog,
    ...(preview.terminalLines ?? []).slice(-12).map((line) => line.message)
  ].filter(Boolean).join('\n').toLowerCase();
  if (text.includes('oomkilled') || text.includes('exit code: 137')) {
    return 'API or Kubernetes reported OOMKilled. The previous operation exceeded its memory limit.';
  }
  if (text.includes('memory pressured')) {
    return 'API memory pressure blocked this operation before starting more work.';
  }
  if (text.includes('reattach') || text.includes('reusing it instead of starting codex again')) {
    return 'The API recovered after a restart and is reusing the existing preview source job/result.';
  }
  return null;
}

function PreviewTerminal({ lines, active, title = 'Implementation log' }: { lines: PreviewTerminalLineDto[]; active: boolean; title?: string }) {
  const [expanded, setExpanded] = React.useState(false);
  const visibleLines = lines.slice(-160);
  const resetKey = lines[0] ? `${lines[0].createdAt}-${lines[0].message}` : 'empty';
  const terminalContent = <TerminalLog lines={visibleLines} resetKey={resetKey} />;
  return (
    <>
      <div className="preview-terminal">
        <div className="terminal-head">
          <span><SquareTerminal size={15} />{title}</span>
          <div className="terminal-actions">
            {active && <span className="terminal-live"><span className="spinner" />live</span>}
            <button className="terminal-expand" type="button" onClick={() => setExpanded(true)} aria-label={`Open larger ${title} log`}><Maximize2 size={14} />Expand</button>
          </div>
        </div>
        {terminalContent}
      </div>
      {expanded && (
        <ModalFrame title={`${title} log`} onClose={() => setExpanded(false)} size="wide">
          <div className="terminal-modal-content">
            <div className="terminal-modal-meta">
              <span>{lines.length} log lines</span>
              {active && <span className="terminal-live"><span className="spinner" />live</span>}
            </div>
            <TerminalLog lines={lines} expanded resetKey={resetKey} />
          </div>
        </ModalFrame>
      )}
    </>
  );
}

function TerminalLog({ lines, expanded = false, resetKey }: { lines: PreviewTerminalLineDto[]; expanded?: boolean; resetKey: string }) {
  const terminalRef = React.useRef<HTMLDivElement | null>(null);
  const bottomRef = React.useRef<HTMLDivElement | null>(null);
  const autoFollowRef = React.useRef(true);
  const previousResetKey = React.useRef(resetKey);

  React.useEffect(() => {
    if (previousResetKey.current !== resetKey) {
      autoFollowRef.current = true;
      previousResetKey.current = resetKey;
    }

    if (autoFollowRef.current) {
      bottomRef.current?.scrollIntoView({ block: 'end' });
    }
  }, [lines.length, resetKey]);

  const handleScroll = React.useCallback(() => {
    const element = terminalRef.current;
    if (!element) return;
    autoFollowRef.current = element.scrollHeight - element.scrollTop - element.clientHeight < 36;
  }, []);

  return (
    <div className={expanded ? 'terminal-body expanded' : 'terminal-body'} ref={terminalRef} onScroll={handleScroll}>
      {lines.length === 0
        ? <p className="terminal-empty">Waiting for Codex output...</p>
        : lines.map((line, index) => (
          <div className={`terminal-line ${line.stream.toLowerCase()}`} key={`${line.createdAt}-${index}`}>
            <span>{terminalTime(line.createdAt)}</span>
            <code><strong>{terminalStreamLabel(line.stream)}</strong>{line.message}</code>
          </div>
        ))}
      <div className="terminal-bottom-sentinel" ref={bottomRef} aria-hidden="true" />
    </div>
  );
}

function terminalStreamLabel(stream: string) {
  const normalized = stream.toLowerCase();
  if (normalized === 'stderr') return 'agent';
  if (normalized === 'stdout') return 'output';
  return normalized || 'system';
}

function isPreviewPendingStatus(status?: string) {
  return status === 'Implementing' || status === 'Applying' || status === 'Provisioning';
}

function developmentStatusText(development: DevelopmentDto) {
  if (development.repository === 'local/vite-react-tailwind' &&
      development.checksStatus.toLowerCase().includes('preview ready')) {
    return 'Local React/Tailwind source generated';
  }

  if (isLocalGitDevelopmentRecord(development) && development.pullRequestApprovedAt) {
    return 'Local pull request merged and deployed';
  }

  return development.checksStatus;
}

function CreateWorkItemModal({ board, initialStatus, assigneeOptions, onCreate, onClose }: {
  board: Board;
  initialStatus: string;
  assigneeOptions: AssigneeOption[];
  onCreate: (form: WorkItemForm) => Promise<boolean>;
  onClose: () => void;
}) {
  const [form, setForm] = React.useState<WorkItemForm>({ ...emptyForm, status: initialStatus, assignee: preferredAssignee(assigneeOptions) });
  const [submitting, setSubmitting] = React.useState(false);
  const parentOptions = React.useMemo(() => availableParentOptions(board, null, form.type), [board, form.type]);
  React.useEffect(() => {
    if (!form.parentWorkItemId || parentOptions.some((item) => item.id === form.parentWorkItemId)) return;
    setForm((current) => ({ ...current, parentWorkItemId: '' }));
  }, [form.parentWorkItemId, parentOptions]);
  return (
    <ModalFrame title="New card" onClose={onClose}>
      <form className="create-form" onSubmit={(event) => {
        event.preventDefault();
        if (!form.title.trim() || submitting) return;
        setSubmitting(true);
        void onCreate(form)
          .then((saved) => {
            if (saved) onClose();
          })
          .finally(() => setSubmitting(false));
      }}>
        <label>Title<input autoFocus value={form.title} onChange={(event) => setForm({ ...form, title: event.target.value })} /></label>
        <label>Description<textarea value={form.description} onChange={(event) => setForm({ ...form, description: event.target.value })} /></label>
        <div className="form-grid two">
          <label>Type<select value={form.type} onChange={(event) => {
            const nextType = event.target.value;
            setForm({ ...form, type: nextType, parentWorkItemId: nextType === 'Epic' ? '' : form.parentWorkItemId, isBug: nextType === 'Epic' ? false : form.isBug });
          }}>{workItemTypeOptions.map((option) => <option key={option}>{option}</option>)}</select></label>
          <label>Status<select value={form.status} onChange={(event) => setForm({ ...form, status: event.target.value })}>{board.columns.map((column) => <option key={column.name}>{column.name}</option>)}</select></label>
          <label>Priority<select value={form.priority} onChange={(event) => setForm({ ...form, priority: event.target.value })}><option>Low</option><option>Medium</option><option>High</option></select></label>
          <label>Assignee<AssigneeSelect value={form.assignee} options={assigneeOptions} onChange={(assignee) => setForm({ ...form, assignee })} /></label>
          <label>Parent<select value={form.parentWorkItemId} disabled={form.type === 'Epic'} onChange={(event) => setForm({ ...form, parentWorkItemId: event.target.value })}>
            <option value="">No parent</option>
            {parentOptions.map((item) => <option value={item.id} key={item.id}>{item.key} - {item.title}</option>)}
          </select></label>
          <label className="checkbox-row"><input type="checkbox" checked={form.isBug} disabled={form.type === 'Epic'} onChange={(event) => setForm({ ...form, isBug: event.target.checked })} />Mark as bug</label>
        </div>
        <div className="modal-actions">
          <button className="primary-action" disabled={!form.title.trim() || submitting}><Plus size={16} />Create card</button>
          <button className="secondary" type="button" onClick={onClose}>Cancel</button>
        </div>
      </form>
    </ModalFrame>
  );
}

function AssigneeSelect({ value, options, onChange }: {
  value: string;
  options: AssigneeOption[];
  onChange: (value: string) => void;
}) {
  const normalizedOptions = options.some((option) => option.value === value)
    ? options
    : [...options, { value, label: value, hint: 'Existing card' }];

  return (
    <select value={value} onChange={(event) => onChange(event.target.value)}>
      {normalizedOptions.map((option) => (
        <option value={option.value} key={option.value}>
          {option.hint ? `${option.label} - ${option.hint}` : option.label}
        </option>
      ))}
    </select>
  );
}

function ToastStack({ busyAction, toasts, onDismiss }: {
  busyAction: string | null;
  toasts: ToastMessage[];
  onDismiss: (id: string) => void;
}) {
  const visibleToasts = toasts.slice(0, busyAction ? 2 : 3);
  if (!busyAction && visibleToasts.length === 0) return null;

  return (
    <div className="toast-stack" role="status" aria-live="polite">
      {busyAction && <div className="toast info"><span className="spinner" />{busyAction}...</div>}
      {visibleToasts.map((toast) => (
        <div className={`toast ${toast.kind}`} key={toast.id}>
          <span>{toast.message}</span>
          <button onClick={() => onDismiss(toast.id)} aria-label="Dismiss notification"><X size={14} /></button>
        </div>
      ))}
    </div>
  );
}

function CreateBoardModal({ teams, githubIntegrations, settings, me, actions, onNotify, onCreate, onClose }: {
  teams: TeamDto[];
  githubIntegrations: GitHubIntegrationDto[];
  settings: SettingsDto;
  me: UserDto;
  actions: BoardActions;
  onNotify: (kind: ToastMessage['kind'], message: string) => void;
  onCreate: (form: CreateBoardForm) => Promise<boolean>;
  onClose: () => void;
}) {
  const defaultTeamIds = React.useMemo(() => teams.filter((team) => team.members.some((member) => ['Owner', 'Admin', 'Member'].includes(member.role))).slice(0, 1).map((team) => team.id), [teams]);
  const isDemoUser = me.email?.toLowerCase() === 'demo@rosenvall.local';
  const localGitState = localGitProviderState(settings.repositories);
  const localGitEnabled = localGitState.visible;
  const localGitAvailable = localGitState.available;
  const localGitMessage = localGitState.message;
  const initialProviderMode: CreateBoardForm['providerMode'] = isDemoUser && localGitEnabled ? 'LocalGitNew' : emptyBoardForm.providerMode;
  const [form, setForm] = React.useState<CreateBoardForm>({
    ...emptyBoardForm,
    providerMode: initialProviderMode,
    repositoryProvider: initialProviderMode === 'LocalGitNew' ? 'LocalGit' : emptyBoardForm.repositoryProvider,
    implementationProfile: initialProviderMode === 'LocalGitNew' ? 'react-preview' : emptyBoardForm.implementationProfile,
    implementationWorkflow: initialProviderMode === 'LocalGitNew' ? 'preview-then-pr' : emptyBoardForm.implementationWorkflow,
    teamIds: defaultTeamIds
  });
  const [githubRepos, setGithubRepos] = React.useState<RepositoryDto[]>([]);
  const [githubStatus, setGithubStatus] = React.useState<'idle' | 'loading' | 'loaded' | 'empty' | 'error'>('idle');
  const [githubError, setGithubError] = React.useState<string | null>(null);
  const [githubInstallationId, setGithubInstallationId] = React.useState<number | null>(null);
  const [repositoryProfile, setRepositoryProfile] = React.useState<RepositoryProfileDto | null>(null);
  const [profileStatus, setProfileStatus] = React.useState<'idle' | 'loading' | 'loaded' | 'error'>('idle');
  const [aiProfileStatus, setAiProfileStatus] = React.useState<'idle' | 'loading' | 'loaded' | 'error'>('idle');
  const [profileError, setProfileError] = React.useState<string | null>(null);
  const [newRepoName, setNewRepoName] = React.useState('');
  const [newRepoDescription, setNewRepoDescription] = React.useState('');
  const [newRepoPrivate, setNewRepoPrivate] = React.useState(true);
  const [newRepoPrompt, setNewRepoPrompt] = React.useState('');
  const [newRepoInstallationId, setNewRepoInstallationId] = React.useState<number | null>(null);
  const [onboardingDraft, setOnboardingDraft] = React.useState<GitHubRepositoryOnboardingDraftDto | null>(null);
  const [onboardingStatus, setOnboardingStatus] = React.useState<'idle' | 'loading' | 'loaded' | 'error'>('idle');
  const [onboardingError, setOnboardingError] = React.useState<string | null>(null);
  const [submitError, setSubmitError] = React.useState<string | null>(null);
  const [submitting, setSubmitting] = React.useState(false);
  const [publicHostnameTouched, setPublicHostnameTouched] = React.useState(false);
  const profileRequestRef = React.useRef(0);
  const normalizedName = form.name.trim();
  const usesGitHub = form.providerMode === 'GitHub';
  const usesGitHubNew = form.providerMode === 'GitHubNew';
  const usesLocalGitNew = form.providerMode === 'LocalGitNew';
  const usesCustomUrl = form.providerMode === 'CustomUrl';
  const usesNoRepository = form.providerMode === 'NoRepository';
  const selectedInstallationId = newRepoInstallationId ?? githubInstallationId ?? githubIntegrations[0]?.installationId ?? null;
  const selectedNewRepoIntegration = githubIntegrations.find((integration) => integration.installationId === selectedInstallationId) ?? null;
  const createPermissionMessage = usesGitHubNew ? repositoryCreatePermissionMessage(selectedNewRepoIntegration) : usesLocalGitNew && !localGitAvailable ? localGitMessage : null;
  const canCreate = normalizedName.length > 0 && profileStatus !== 'loading' && (
    usesNoRepository ||
    (usesGitHub ? form.repositoryRemoteUrl.trim().length > 0 : false) ||
    (usesGitHubNew ? normalizeRepositoryNameInput(newRepoName || form.name).length > 0 && !!onboardingDraft && !!selectedInstallationId && canCreateRepositoryInInstallation(selectedNewRepoIntegration) : false) ||
    (usesLocalGitNew ? normalizeRepositoryNameInput(newRepoName || form.name).length > 0 && !!onboardingDraft && localGitAvailable : false) ||
    (usesCustomUrl ? isPublicGitUrl(form.repositoryRemoteUrl) : false)
  );

  React.useEffect(() => {
    if (publicHostnameTouched) return;
    setForm((current) => {
      if (current.implementationWorkflow !== 'preview-then-pr') {
        return current.publicHostname ? { ...current, publicHostname: '' } : current;
      }

      const hostname = `${slugFromText(current.name || 'app')}.rosenvall.se`;
      return current.publicHostname === hostname ? current : { ...current, publicHostname: hostname };
    });
  }, [form.name, form.implementationWorkflow, publicHostnameTouched]);

  const loadGithubRepositories = React.useCallback(async () => {
    setGithubStatus('loading');
    setGithubError(null);
    try {
      const result = await api.get<GitHubRepositoryPickerDto>('/api/integrations/github/repository-picker', { timeoutMs: 15000 });
      setGithubRepos(result.repositories ?? []);
      setGithubInstallationId(result.activeInstallationId ?? null);
      const status = result.status.toLowerCase();
      setGithubStatus(status === 'loaded' ? 'loaded' : status === 'empty' ? 'empty' : status === 'error' ? 'error' : 'loaded');
      setGithubError(result.message ?? (result.repositories.length === 0 ? 'No repositories were returned by GitHub.' : null));
    } catch (error) {
      setGithubStatus('error');
      setGithubError(error instanceof Error ? error.message : 'Could not load GitHub repositories');
    }
  }, []);

  React.useEffect(() => {
    if (!usesGitHub || githubStatus !== 'idle') return;
    let cancelled = false;
    void loadGithubRepositories().finally(() => {
      if (cancelled) return;
    });
    return () => {
      cancelled = true;
    };
  }, [githubStatus, loadGithubRepositories, usesGitHub]);

  function applyRepositoryProfile(profile: RepositoryProfileDto) {
    const implementationProfile = normalizeImplementationProfile(profile.implementationProfile);
    setRepositoryProfile(profile);
    setForm((current) => ({
      ...current,
      implementationProfile,
      implementationWorkflow: workflowForRepositoryProfile(implementationProfile, null),
      enabledSkills: (profile.enabledSkills ?? []).join('\n'),
      capabilityTags: (profile.capabilityTags ?? []).join('\n'),
      aiInstructions: profile.instructions,
      skillDrafts: profile.skillDrafts ?? [],
      askWhenUncertain: implementationProfile === 'gitops-homelab' ? true : current.askWhenUncertain
    }));
  }

  function applyOnboardingDraft(draft: GitHubRepositoryOnboardingDraftDto) {
    setOnboardingDraft(draft);
    setNewRepoName(draft.name);
    setNewRepoDescription(draft.description);
    setNewRepoPrompt(draft.prompt);
    applyRepositoryProfile(draft.repositoryProfile);
    setForm((current) => ({
      ...current,
      name: current.name || draft.name,
      aiInstructions: draft.aiContext.instructions ?? draft.repositoryProfile.instructions,
      enabledSkills: (draft.aiContext.enabledSkills ?? draft.repositoryProfile.enabledSkills ?? []).join('\n'),
      askWhenUncertain: draft.aiContext.askWhenUncertain ?? current.askWhenUncertain
    }));
  }

  async function generateOnboardingDraft() {
    setOnboardingStatus('loading');
    setOnboardingError(null);
    setSubmitError(null);
    try {
      const draft = await api.post<GitHubRepositoryOnboardingDraftDto>('/api/repositories/github/onboarding-draft', {
        name: normalizeRepositoryNameInput(newRepoName || form.name),
        description: newRepoDescription,
        prompt: newRepoPrompt,
        implementationProfile: form.implementationProfile
      }, { timeoutMs: 60000 });
      applyOnboardingDraft(draft);
      setOnboardingStatus('loaded');
    } catch (error) {
      setOnboardingStatus('error');
      setOnboardingError(error instanceof Error ? error.message : 'Could not generate onboarding draft');
    }
  }

  async function authorizeGitHubUser(installationId: number) {
    try {
      const response = await api.get<GitHubUserAuthorizationStartDto>(`/api/integrations/github/user-authorization/start?installationId=${installationId}`, { timeoutMs: 15000 });
      window.location.href = response.authorizationUrl;
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Could not start GitHub user authorization';
      setSubmitError(message);
      onNotify('error', message);
    }
  }

  async function createNewGitHubRepositoryFromDraft() {
    if (!onboardingDraft || !selectedInstallationId) {
      throw new Error('Generate an onboarding draft and select a GitHub installation before creating the board.');
    }
    if (!canCreateRepositoryInInstallation(selectedNewRepoIntegration)) {
      throw new Error(createPermissionMessage ?? 'You do not have permission to create repositories for this GitHub installation.');
    }

    const response = await api.post<GitHubRepositoryCreateResponse>('/api/repositories/github', {
      installationId: selectedInstallationId,
      name: normalizeRepositoryNameInput(newRepoName || form.name),
      private: newRepoPrivate,
      description: newRepoDescription,
      implementationProfile: form.implementationProfile,
      implementationWorkflow: form.implementationWorkflow,
      onboardingPrompt: newRepoPrompt,
      files: onboardingDraft.files,
      repositoryProfile: repositoryProfileFromForm(form, onboardingDraft.repositoryProfile),
      aiContext: {
        instructions: form.aiInstructions,
        enabledSkills: linesFromTextarea(form.enabledSkills),
        askWhenUncertain: form.askWhenUncertain
      }
    }, { timeoutMs: 60000 });

    const repository = response.repository;
    const implementationProfile = normalizeImplementationProfile(repository.implementationProfile);
    return {
      ...form,
      repositoryId: repository.id,
      providerMode: 'GitHubNew' as const,
      repositoryProvider: 'GitHub',
      repositoryOwner: repository.owner ?? '',
      repositoryRemoteUrl: repository.remoteUrl,
      repositoryWebUrl: repository.webUrl ?? '',
      repositoryDefaultBranch: repository.defaultBranch || 'main',
      implementationProfile,
      implementationWorkflow: workflowForRepositoryProfile(implementationProfile, repository.implementationWorkflow)
    };
  }

  async function createNewLocalGitRepositoryFromDraft() {
    if (!onboardingDraft) {
      throw new Error('Generate an onboarding draft before creating the local Git board.');
    }
    if (!localGitAvailable) {
      throw new Error(localGitMessage);
    }

    const response = await api.post<GitHubRepositoryCreateResponse>('/api/repositories/local', {
      name: normalizeRepositoryNameInput(newRepoName || form.name),
      private: newRepoPrivate,
      description: newRepoDescription,
      implementationProfile: form.implementationProfile,
      implementationWorkflow: form.implementationWorkflow,
      onboardingPrompt: newRepoPrompt,
      files: onboardingDraft.files,
      repositoryProfile: repositoryProfileFromForm(form, onboardingDraft.repositoryProfile),
      aiContext: {
        instructions: form.aiInstructions,
        enabledSkills: linesFromTextarea(form.enabledSkills),
        askWhenUncertain: form.askWhenUncertain
      }
    }, { timeoutMs: 60000 });

    const repository = response.repository;
    const implementationProfile = normalizeImplementationProfile(repository.implementationProfile);
    return {
      ...form,
      repositoryId: repository.id,
      providerMode: 'LocalGitNew' as const,
      repositoryProvider: 'LocalGit',
      repositoryOwner: repository.owner ?? '',
      repositoryRemoteUrl: repository.remoteUrl,
      repositoryWebUrl: repository.webUrl ?? '',
      repositoryDefaultBranch: repository.defaultBranch || 'main',
      implementationProfile,
      implementationWorkflow: workflowForRepositoryProfile(implementationProfile, repository.implementationWorkflow)
    };
  }

  const loadRepositoryProfile = React.useCallback(async (repository: RepositoryDto) => {
    if (!repository.owner || !repository.name) return;
    const requestId = profileRequestRef.current + 1;
    profileRequestRef.current = requestId;
    setProfileStatus('loading');
    setProfileError(null);
    setRepositoryProfile(null);
    setAiProfileStatus('idle');
    const query = new URLSearchParams({
      owner: repository.owner,
      repo: repository.name,
      branch: repository.defaultBranch || 'main'
    });
    if (githubInstallationId) query.set('installationId', String(githubInstallationId));
    try {
      query.set('mode', 'scanner');
      const profile = await api.get<RepositoryProfileDto>(`/api/integrations/github/repository-profile?${query.toString()}`, { timeoutMs: 15000 });
      if (profileRequestRef.current !== requestId) return;
      applyRepositoryProfile(profile);
      setProfileStatus('loaded');
      setAiProfileStatus('loading');
      const fullQuery = new URLSearchParams(query);
      fullQuery.set('mode', 'full');
      api.get<RepositoryProfileDto>(`/api/integrations/github/repository-profile?${fullQuery.toString()}`, { timeoutMs: 45000 })
        .then((codexProfile) => {
          if (profileRequestRef.current !== requestId) return;
          applyRepositoryProfile(codexProfile);
          setAiProfileStatus('loaded');
        })
        .catch((error) => {
          if (profileRequestRef.current !== requestId) return;
          setAiProfileStatus('error');
          setProfileError(error instanceof Error ? error.message : 'Codex analysis did not return a profile');
        });
    } catch (error) {
      if (profileRequestRef.current !== requestId) return;
      setProfileStatus('error');
      setProfileError(error instanceof Error ? error.message : 'Could not detect repository profile');
      setForm((current) => ({ ...current, implementationProfile: 'code-repo' }));
    }
  }, [githubInstallationId]);

  const selectGitHubRepository = (repoKey: string) => {
    const repository = githubRepos.find((entry) => `${entry.owner ?? ''}/${entry.name}` === repoKey);
    if (!repository) return;
    const implementationProfile = normalizeImplementationProfile(repository.implementationProfile);
    setForm({
      ...form,
      name: form.name || repository.name,
      repositoryId: repository.id && repository.id !== '00000000-0000-0000-0000-000000000000' ? repository.id : null,
      providerMode: 'GitHub',
      repositoryProvider: repository.provider,
      repositoryOwner: repository.owner ?? '',
      repositoryRemoteUrl: repository.remoteUrl,
      repositoryWebUrl: repository.webUrl ?? '',
      repositoryDefaultBranch: repository.defaultBranch || 'main',
      implementationProfile,
      implementationWorkflow: workflowForRepositoryProfile(implementationProfile, null)
    });
    void loadRepositoryProfile(repository);
  };
  const selectedGitHubRepository = githubRepos.find((entry) => `${entry.owner ?? ''}/${entry.name}` === (form.repositoryOwner && form.repositoryRemoteUrl ? `${form.repositoryOwner}/${repositoryNameFromRemote(form.repositoryRemoteUrl, form.name)}` : ''));

  return (
    <ModalFrame title="Add board" onClose={onClose}>
      <form className="create-form" onSubmit={(event) => {
        event.preventDefault();
        if (!canCreate || submitting) return;
        setSubmitting(true);
        setSubmitError(null);
        const submitForm = usesGitHubNew
          ? createNewGitHubRepositoryFromDraft().then((repoForm) => ({ ...repoForm, name: normalizedName }))
          : usesLocalGitNew
            ? createNewLocalGitRepositoryFromDraft().then((repoForm) => ({ ...repoForm, name: normalizedName }))
            : Promise.resolve({ ...form, name: normalizedName });
        void submitForm
          .then((nextForm) => onCreate(nextForm))
          .then((saved) => {
            if (saved) onClose();
          })
          .catch((error) => {
            const message = error instanceof Error ? error.message : 'Could not create repository';
            setOnboardingStatus('error');
            setOnboardingError(message);
            setSubmitError(message);
            onNotify('error', message);
          })
          .finally(() => setSubmitting(false));
      }}>
        <label>Board name<input autoFocus value={form.name} onChange={(event) => setForm({ ...form, name: event.target.value })} placeholder="Gatewaybound" /></label>
        <div className="form-grid two">
          <label>Provider<select value={form.providerMode} onChange={(event) => {
            const providerMode = event.target.value as CreateBoardForm['providerMode'];
            setForm({
              ...form,
              providerMode,
              repositoryProvider: providerMode === 'GitHub' || providerMode === 'GitHubNew' ? 'GitHub' : providerMode === 'LocalGitNew' ? 'LocalGit' : providerMode === 'NoRepository' ? '' : 'GenericGit',
              repositoryId: null,
              repositoryRemoteUrl: '',
              repositoryWebUrl: '',
              repositoryOwner: '',
              implementationProfile: providerMode === 'LocalGitNew' ? 'react-preview' : providerMode === 'GitHub' || providerMode === 'GitHubNew' ? 'code-repo' : form.implementationProfile,
              implementationWorkflow: providerMode === 'LocalGitNew' ? 'preview-then-pr' : form.implementationWorkflow
            });
            setRepositoryProfile(null);
            setProfileStatus('idle');
            setAiProfileStatus('idle');
            setProfileError(null);
            setOnboardingDraft(null);
            setOnboardingStatus('idle');
            setOnboardingError(null);
            if (providerMode === 'GitHub') setGithubStatus('idle');
            if (providerMode === 'GitHubNew' || providerMode === 'LocalGitNew') setNewRepoName(normalizeRepositoryNameInput(form.name));
          }}>
            <option value="NoRepository">No repository</option>
            {!isDemoUser && <option value="GitHub">GitHub existing repository</option>}
            {!isDemoUser && <option value="GitHubNew">GitHub new repository</option>}
            {localGitEnabled && <option value="LocalGitNew">Local Git new repository</option>}
            {!isDemoUser && <option value="CustomUrl">Custom URL</option>}
          </select></label>
          {usesGitHub && (
            <label>GitHub repository<select value={form.repositoryOwner && form.repositoryRemoteUrl ? `${form.repositoryOwner}/${repositoryNameFromRemote(form.repositoryRemoteUrl, form.name)}` : ''} onChange={(event) => selectGitHubRepository(event.target.value)}>
              <option value="">{githubStatus === 'loading' ? 'Loading repositories...' : 'Select repository...'}</option>
              {githubRepos.map((repository) => <option key={`${repository.owner}/${repository.name}`} value={`${repository.owner ?? ''}/${repository.name}`}>{repository.owner ? `${repository.owner}/` : ''}{repository.name}</option>)}
            </select></label>
          )}
          {!usesNoRepository && <label>Default branch<input value={form.repositoryDefaultBranch} onChange={(event) => setForm({ ...form, repositoryDefaultBranch: event.target.value })} /></label>}
          {usesCustomUrl && (
            <>
              <label>Repository URL<input value={form.repositoryRemoteUrl} onChange={(event) => setForm({ ...form, repositoryRemoteUrl: event.target.value, repositoryOwner: repositoryOwnerFromRemote(event.target.value) ?? form.repositoryOwner, repositoryWebUrl: webUrlFromRemote(event.target.value) ?? form.repositoryWebUrl })} placeholder="https://github.com/owner/repo.git" /></label>
              <label>Web URL<input value={form.repositoryWebUrl} onChange={(event) => setForm({ ...form, repositoryWebUrl: event.target.value })} placeholder="https://github.com/owner/repo" /></label>
            </>
          )}
        </div>
        {usesNoRepository && <p className="provider-status">This board starts preview-only. You can sync it to GitHub later from the board header.</p>}
        {(usesGitHubNew || usesLocalGitNew) && (
          <NewRepositoryOnboardingPanel
            form={form}
            localGitMode={usesLocalGitNew}
            installationId={selectedInstallationId}
            integrations={githubIntegrations}
            repoName={newRepoName || normalizeRepositoryNameInput(form.name)}
            selectedInstallationId={selectedInstallationId}
            description={newRepoDescription}
            isPrivate={newRepoPrivate}
            prompt={newRepoPrompt}
            draft={onboardingDraft}
            error={onboardingError}
            onInstallationChange={setNewRepoInstallationId}
            onRepoNameChange={setNewRepoName}
            onDescriptionChange={setNewRepoDescription}
            onPrivateChange={setNewRepoPrivate}
            onPromptChange={setNewRepoPrompt}
            onDraftChange={setOnboardingDraft}
            onAuthorize={authorizeGitHubUser}
            createPermissionMessage={createPermissionMessage}
            localGitMessage={localGitMessage}
          />
        )}
        {usesGitHub && form.repositoryRemoteUrl && (
          <RepositoryProfileEditor
            form={form}
            profile={repositoryProfile}
            profileStatus={profileStatus}
            aiProfileStatus={aiProfileStatus}
            profileError={profileError}
            onChange={setForm}
            onAnalyze={selectedGitHubRepository ? () => void loadRepositoryProfile(selectedGitHubRepository) : undefined}
          />
        )}
        {!usesNoRepository && form.implementationWorkflow === 'preview-then-pr' && (
          <section className="repository-profile-editor">
            <div className="settings-inline">
              <div>
                <strong>Production hosting</strong>
                <p>Previewable boards are published to this hostname after an approved preview PR is merged.</p>
              </div>
            </div>
            <label>Public hostname<input value={form.publicHostname} onChange={(event) => {
              setPublicHostnameTouched(true);
              setForm({ ...form, publicHostname: event.target.value });
            }} placeholder={`${slugFromText(form.name || 'board')}.rosenvall.se`} /></label>
          </section>
        )}
        <fieldset className="team-picker">
          <legend>Teams</legend>
          {teams.length === 0
            ? <p className="provider-status">No teams are available yet. You can create teams from the Teams view.</p>
            : teams.map((team) => (
              <label className="checkbox-row" key={team.id}>
                <input type="checkbox" checked={form.teamIds.includes(team.id)} onChange={(event) => setForm({ ...form, teamIds: event.target.checked ? [...form.teamIds, team.id] : form.teamIds.filter((id) => id !== team.id) })} />
                <span>{team.name}</span>
              </label>
            ))}
        </fieldset>
        {usesGitHub && githubError && (
          <div className="provider-status with-actions">
            <span>GitHub repositories: {githubError}</span>
            <div className="button-row">
              <button type="button" className="secondary compact" onClick={() => void actions.syncGitHubIntegration()}><RefreshCw size={14} />Sync</button>
              <button type="button" className="secondary compact" onClick={() => void loadGithubRepositories()}><RefreshCw size={14} />Retry</button>
            </div>
          </div>
        )}
        {usesCustomUrl && form.repositoryRemoteUrl && !isPublicGitUrl(form.repositoryRemoteUrl) && <p className="provider-status">Custom URL supports public HTTP(S) clone URLs only in v1.</p>}
        {submitError && <p className="failure-reason submit-error">{submitError}</p>}
        {createPermissionMessage && <p className="failure-reason submit-error">{createPermissionMessage}</p>}
        <div className="modal-actions">
          {(usesGitHubNew || usesLocalGitNew) && (
            <button type="button" className="secondary" disabled={onboardingStatus === 'loading' || normalizeRepositoryNameInput(newRepoName || form.name).length === 0} onClick={() => void generateOnboardingDraft()}>
              <Sparkles size={16} />{onboardingStatus === 'loading' ? 'Generating draft...' : onboardingDraft ? 'Regenerate draft' : 'Generate draft'}
            </button>
          )}
          <button className="primary-action" disabled={!canCreate || submitting}><Plus size={16} />Create board</button>
          <button className="secondary" type="button" onClick={onClose}>Cancel</button>
        </div>
      </form>
    </ModalFrame>
  );
}

function NewRepositoryOnboardingPanel({ form, localGitMode = false, installationId, integrations, repoName, selectedInstallationId, description, isPrivate, prompt, draft, error, createPermissionMessage, localGitMessage, onInstallationChange, onRepoNameChange, onDescriptionChange, onPrivateChange, onPromptChange, onDraftChange, onAuthorize }: {
  form: CreateBoardForm;
  localGitMode?: boolean;
  installationId: number | null;
  integrations: GitHubIntegrationDto[];
  repoName: string;
  selectedInstallationId: number | null;
  description: string;
  isPrivate: boolean;
  prompt: string;
  draft: GitHubRepositoryOnboardingDraftDto | null;
  error: string | null;
  createPermissionMessage: string | null;
  localGitMessage?: string | null;
  onInstallationChange: (value: number | null) => void;
  onRepoNameChange: (value: string) => void;
  onDescriptionChange: (value: string) => void;
  onPrivateChange: (value: boolean) => void;
  onPromptChange: (value: string) => void;
  onDraftChange: React.Dispatch<React.SetStateAction<GitHubRepositoryOnboardingDraftDto | null>>;
  onAuthorize: (installationId: number) => Promise<void>;
}) {
  const selectedIntegration = integrations.find((integration) => integration.installationId === selectedInstallationId) ?? null;
  const updateFile = (index: number, patch: Partial<GitHubRepositoryOnboardingFileDto>) => {
    onDraftChange((current) => current ? { ...current, files: current.files.map((file, fileIndex) => fileIndex === index ? { ...file, ...patch } : file) } : current);
  };

  return (
    <section className="repository-profile-panel">
      <div className="panel-heading-row">
        <div>
          <h3>{localGitMode ? 'Local Git repository onboarding' : 'New repository onboarding'}</h3>
          <p>Codex drafts editable guidance files only. App code and deployment manifests start from later cards.</p>
        </div>
      </div>
      <div className="form-grid two">
        <label>Repository name<input value={repoName} onChange={(event) => onRepoNameChange(event.target.value)} onBlur={(event) => onRepoNameChange(normalizeRepositoryNameInput(event.target.value))} placeholder={normalizeRepositoryNameInput(form.name) || 'new-repository'} /></label>
        <label>Visibility<select value={isPrivate ? 'private' : 'public'} onChange={(event) => onPrivateChange(event.target.value === 'private')}>
          <option value="private">Private</option>
          <option value="public">Public</option>
        </select></label>
        {!localGitMode && <label className="full-width">GitHub installation<select value={selectedInstallationId ?? ''} onChange={(event) => onInstallationChange(event.target.value ? Number(event.target.value) : null)}>
          <option value="">Select installation...</option>
          {integrations.map((integration) => <option key={integration.installationId} value={integration.installationId}>{integration.accountLogin} ({integration.accountType})</option>)}
        </select></label>}
        <label className="full-width">Description<input value={description} onChange={(event) => onDescriptionChange(event.target.value)} placeholder={`Repository for ${form.name || 'this board'}.`} /></label>
        <label className="full-width">What should this repository be for?<textarea value={prompt} onChange={(event) => onPromptChange(event.target.value)} placeholder="Describe the repo purpose, stack, operating model, and any conventions Codex should encode as guidance." /></label>
      </div>
      {!localGitMode && !installationId && <p className="failure-reason">No GitHub App installation is available for creating a repository.</p>}
      {localGitMode && <p className={createPermissionMessage ? 'provider-status warning' : 'provider-status ready'}>{localGitMessage ?? 'Local Git repository: RDO will create a private Forgejo repository inside the DevOps namespace. Demo users do not need GitHub.'}</p>}
      {!localGitMode && selectedIntegration && <p className="provider-status">GitHub installation: {selectedIntegration.accountLogin}. New repositories are private by default.</p>}
      {!localGitMode && selectedIntegration?.requiresUserAuthorizationForRepositoryCreation && (
        <div className="provider-status with-actions">
          <span>{selectedIntegration.repositoryCreationMessage ?? `Authorize GitHub user access before creating repositories under ${selectedIntegration.accountLogin}.`}</span>
          <button type="button" className="secondary compact" onClick={() => void onAuthorize(selectedIntegration.installationId)}><Github size={14} />Authorize GitHub user</button>
        </div>
      )}
      {!localGitMode && selectedIntegration?.hasUserAuthorization && selectedIntegration.authorizedGitHubLogin && !selectedIntegration.canCreateRepositories && (
        <p className="failure-reason">Connected as {selectedIntegration.authorizedGitHubLogin}, but this installation belongs to {selectedIntegration.accountLogin}. Repository creation is only allowed under your own GitHub login.</p>
      )}
      {createPermissionMessage && <p className="failure-reason">{createPermissionMessage}</p>}
      <p className="provider-status">Repository names are normalized to kebab-case, for example <code>date-site</code>.</p>
      {error && <p className="failure-reason">{error}</p>}
      {draft && (
        <div className="settings-list skill-drafts-list">
          <div className="settings-row">
            <div>
              <strong>Draft source</strong>
              <p>{draft.source}{draft.model ? ` / ${draft.model}` : ''} - {draft.repositoryProfile.displayName}</p>
            </div>
          </div>
          {draft.files.map((file, index) => (
            <div className="settings-row vertical" key={`${file.path}-${index}`}>
              <label>Path<input value={file.path} onChange={(event) => updateFile(index, { path: event.target.value })} /></label>
              <label>Content<textarea value={file.content} onChange={(event) => updateFile(index, { content: event.target.value })} /></label>
            </div>
          ))}
        </div>
      )}
    </section>
  );
}

function SyncBoardRepositoryModal({ board, repositories, settings, githubIntegrations, onSync, onClose }: {
  board: Board;
  repositories: RepositoryDto[];
  settings: SettingsDto;
  githubIntegrations: GitHubIntegrationDto[];
  onSync: (request: SyncBoardRepositoryRequest) => Promise<boolean>;
  onClose: () => void;
}) {
  const [availableRepos, setAvailableRepos] = React.useState<RepositoryDto[]>(repositories.filter((repository) => repository.provider === 'GitHub'));
  const [pickerStatus, setPickerStatus] = React.useState<'idle' | 'loading' | 'error'>('idle');
  const [pickerMessage, setPickerMessage] = React.useState<string | null>(null);
  const [selectedRepoKey, setSelectedRepoKey] = React.useState('');
  const [implementationProfile, setImplementationProfile] = React.useState('code-repo');
  const [submitting, setSubmitting] = React.useState(false);

  React.useEffect(() => {
    let cancelled = false;
    async function loadPicker() {
      setPickerStatus('loading');
      setPickerMessage(null);
      try {
        const result = await api.get<GitHubRepositoryPickerDto>('/api/integrations/github/repository-picker', { timeoutMs: 15000 });
        if (cancelled) return;
        const merged = [...repositories.filter((repository) => repository.provider === 'GitHub'), ...(result.repositories ?? [])];
        setAvailableRepos(uniqueRepositories(merged));
        setPickerStatus(result.status.toLowerCase() === 'error' ? 'error' : 'idle');
        setPickerMessage(result.message ?? null);
      } catch (error) {
        if (!cancelled) {
          setPickerStatus('error');
          setPickerMessage(error instanceof Error ? error.message : 'Could not load GitHub repositories');
        }
      }
    }

    if (settings.gitHub.connected || settings.gitHub.appConfigured) {
      void loadPicker();
    }

    return () => {
      cancelled = true;
    };
  }, [repositories, settings.gitHub.appConfigured, settings.gitHub.connected]);

  const selectedRepo = availableRepos.find((repository) => repositoryKey(repository) === selectedRepoKey) ?? null;
  const canSync = !!selectedRepo;

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    if (!canSync || submitting) return;
    setSubmitting(true);
    try {
      const saved = await onSync({
        createNew: false,
        repositoryId: selectedRepo?.id && selectedRepo.id !== '00000000-0000-0000-0000-000000000000' ? selectedRepo.id : null,
        owner: selectedRepo?.owner ?? null,
        name: selectedRepo?.name ?? null,
        remoteUrl: selectedRepo?.remoteUrl ?? null,
        webUrl: selectedRepo?.webUrl ?? null,
        defaultBranch: selectedRepo?.defaultBranch ?? 'main',
        implementationProfile
      });
      if (saved) onClose();
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <ModalFrame title="Sync board to GitHub" onClose={onClose}>
      <form className="create-form" onSubmit={(event) => void submit(event)}>
        <div className="provider-status">Link an existing GitHub repository. Creating new repositories from Rosenvall DevOps is disabled for now.</div>
        <div className="form-grid two">
          <label className="full-width">GitHub repository<select value={selectedRepoKey} onChange={(event) => setSelectedRepoKey(event.target.value)}>
            <option value="">{pickerStatus === 'loading' ? 'Loading repositories...' : 'Select repository...'}</option>
            {availableRepos.map((repository) => <option key={repositoryKey(repository)} value={repositoryKey(repository)}>{repositoryLabel(repository)} - {repository.defaultBranch}</option>)}
          </select></label>
          <label>Implementation profile<select value={implementationProfile} onChange={(event) => setImplementationProfile(event.target.value)}>
            <option value="code-repo">Code repo</option>
            <option value="gitops-homelab">GitOps homelab</option>
            <option value="unity">Unity</option>
            <option value="react-preview">React preview</option>
          </select></label>
        </div>
        {pickerMessage && <p className={pickerStatus === 'error' ? 'failure-reason' : 'provider-status'}>{pickerMessage}</p>}
        <div className="modal-actions">
          <button className="primary-action" disabled={!canSync || submitting}><Github size={16} />Sync to GitHub</button>
          <button className="secondary" type="button" onClick={onClose}>Cancel</button>
        </div>
      </form>
    </ModalFrame>
  );
}

function RepositoryProfileEditor({ form, profile, profileStatus, aiProfileStatus, profileError, onChange, onAnalyze, saveLabel, onSave }: {
  form: CreateBoardForm;
  profile?: RepositoryProfileDto | null;
  profileStatus?: 'idle' | 'loading' | 'loaded' | 'error';
  aiProfileStatus?: 'idle' | 'loading' | 'loaded' | 'error';
  profileError?: string | null;
  onChange: React.Dispatch<React.SetStateAction<CreateBoardForm>>;
  onAnalyze?: () => void;
  saveLabel?: string;
  onSave?: () => void;
}) {
  const updateDraft = (index: number, patch: Partial<RepositorySkillDraftDto>) => {
    onChange((current) => ({
      ...current,
      skillDrafts: current.skillDrafts.map((draft, draftIndex) => draftIndex === index ? { ...draft, ...patch } : draft)
    }));
  };
  const removeDraft = (index: number) => onChange((current) => ({ ...current, skillDrafts: current.skillDrafts.filter((_, draftIndex) => draftIndex !== index) }));
  const addDraft = () => onChange((current) => ({
    ...current,
    skillDrafts: [...current.skillDrafts, { name: 'repo-skill', description: '', content: '', enabled: true }]
  }));
  const statusLine = profileStatus === 'loading'
    ? 'Scanner is reading repository structure...'
    : aiProfileStatus === 'loading'
      ? 'Scanner result is editable. Codex analysis is refining it in the background...'
      : profile
        ? `${profile.displayName} - ${Math.round((profile.confidence ?? 0) * 100)}% confidence - ${profile.source}${profile.analyzerModel ? ` / ${profile.analyzerModel}` : ''}`
        : profileLabel(form.implementationProfile);

  return (
    <section className="repository-profile-editor">
      <div className="settings-inline profile-toolbar">
        <div>
          <strong>Repository profile</strong>
          <p>{statusLine}</p>
        </div>
        <div className="button-row">
          {onAnalyze && <button type="button" className="secondary compact" onClick={onAnalyze}><Sparkles size={14} />Analyze again</button>}
          {onSave && <button type="button" className="primary-action compact" onClick={onSave}><Save size={14} />{saveLabel ?? 'Save profile'}</button>}
        </div>
      </div>
      {profileError && <p className="provider-status">{profileError}</p>}
      {profile?.signals?.length ? <p className="profile-signals">Signals: {profile.signals.join(', ')}</p> : null}
      <div className="form-grid two">
        <label>Profile<select value={form.implementationProfile} onChange={(event) => onChange((current) => {
          const nextProfile = normalizeImplementationProfile(event.target.value);
          return { ...current, implementationProfile: nextProfile, implementationWorkflow: workflowForRepositoryProfile(nextProfile, null) };
        })}>
          <option value="code-repo">Code repo</option>
          <option value="gitops-homelab">GitOps homelab</option>
          <option value="unity">Unity</option>
          <option value="react-preview">React preview</option>
        </select></label>
        <label>Ask when uncertain<select value={form.askWhenUncertain ? 'yes' : 'no'} onChange={(event) => onChange((current) => ({ ...current, askWhenUncertain: event.target.value === 'yes' }))}>
          <option value="yes">Yes</option>
          <option value="no">No</option>
        </select></label>
        <label className="full-width">Capability tags<textarea value={form.capabilityTags} onChange={(event) => onChange((current) => ({ ...current, capabilityTags: event.target.value }))} /></label>
        <label className="full-width">Enabled skills<textarea value={form.enabledSkills} onChange={(event) => onChange((current) => ({ ...current, enabledSkills: event.target.value }))} /></label>
        <label className="full-width">Board instructions<textarea value={form.aiInstructions} onChange={(event) => onChange((current) => ({ ...current, aiInstructions: event.target.value }))} /></label>
      </div>
      {form.implementationProfile === 'gitops-homelab' && (
        <div className="form-grid two">
          <label className="full-width">GitOps paths<textarea value={form.gitOpsAllowedPaths} onChange={(event) => onChange((current) => ({ ...current, gitOpsAllowedPaths: event.target.value }))} /></label>
          <label>ArgoCD namespace<input value={form.argoNamespace} onChange={(event) => onChange((current) => ({ ...current, argoNamespace: event.target.value }))} /></label>
          <label>ArgoCD application label selector<input value={form.argoApplicationSelector} onChange={(event) => onChange((current) => ({ ...current, argoApplicationSelector: event.target.value }))} placeholder="app.kubernetes.io/part-of=homelab" /></label>
          <p className="provider-status full-width">Optional Kubernetes label selector used when listing ArgoCD Application resources in the configured namespace. Leave blank to list all apps.</p>
        </div>
      )}
      <div className="skill-drafts">
        <div className="settings-inline">
          <div><strong>Skill drafts</strong><p>Editable repo-local suggestions. They are not installed globally.</p></div>
          <button type="button" className="secondary compact" onClick={addDraft}><Plus size={14} />Add draft</button>
        </div>
        {form.skillDrafts.length === 0
          ? <p className="provider-status">No skill drafts suggested yet.</p>
          : form.skillDrafts.map((draft, index) => (
            <details className="skill-draft-row" key={`${draft.name}-${index}`}>
              <summary>
                <label className="checkbox-row" onClick={(event) => event.stopPropagation()}>
                  <input type="checkbox" checked={draft.enabled} onChange={(event) => updateDraft(index, { enabled: event.target.checked })} />
                  <span>{draft.name || 'Unnamed skill'}</span>
                </label>
                <button type="button" className="danger-button compact" onClick={(event) => { event.preventDefault(); removeDraft(index); }}><Trash2 size={14} />Remove</button>
              </summary>
              <div className="form-grid two">
                <label>Name<input value={draft.name} onChange={(event) => updateDraft(index, { name: event.target.value })} /></label>
                <label>Description<input value={draft.description} onChange={(event) => updateDraft(index, { description: event.target.value })} /></label>
                <label className="full-width">Content<textarea value={draft.content} onChange={(event) => updateDraft(index, { content: event.target.value })} /></label>
              </div>
            </details>
          ))}
      </div>
    </section>
  );
}

function TeamsView({ teams, boards, me, actions }: {
  teams: TeamDto[];
  boards: Board[];
  me: UserDto;
  actions: BoardActions;
}) {
  const [selectedTeamId, setSelectedTeamId] = React.useState(() => teams[0]?.id ?? '');
  const [teamName, setTeamName] = React.useState('');
  const [inviteEmail, setInviteEmail] = React.useState('');
  const [inviteRole, setInviteRole] = React.useState('Member');
  const [boardId, setBoardId] = React.useState('');
  const [boardRole, setBoardRole] = React.useState('Member');
  const selectedTeam = teams.find((team) => team.id === selectedTeamId) ?? teams[0];
  const assignedBoards = selectedTeam
    ? boards.filter((board) => board.teamAccess?.some((access) => access.teamId === selectedTeam.id))
    : [];
  const assignableBoards = selectedTeam
    ? boards.filter((board) => !board.teamAccess?.some((access) => access.teamId === selectedTeam.id))
    : boards;

  React.useEffect(() => {
    if (!selectedTeamId && teams[0]) setSelectedTeamId(teams[0].id);
  }, [selectedTeamId, teams]);

  async function createTeam(event: React.FormEvent) {
    event.preventDefault();
    const name = teamName.trim();
    if (!name) return;
    if (await actions.createTeam(name)) {
      setTeamName('');
    }
  }

  async function inviteMember(event: React.FormEvent) {
    event.preventDefault();
    if (!selectedTeam || !inviteEmail.trim()) return;
    if (await actions.inviteTeamMember(selectedTeam.id, inviteEmail.trim(), inviteRole)) {
      setInviteEmail('');
    }
  }

  async function assignBoard(event: React.FormEvent) {
    event.preventDefault();
    if (!selectedTeam || !boardId) return;
    if (await actions.assignTeamToBoard(boardId, selectedTeam.id, boardRole)) {
      setBoardId('');
    }
  }

  return (
    <section className="page teams-page">
      <div className="page-heading">
        <div>
          <h1>Teams</h1>
          <p>Teams own access to boards and repositories.</p>
        </div>
      </div>
      <div className="teams-layout">
        <aside className="panel team-list-panel">
          <form className="compact-form" onSubmit={(event) => void createTeam(event)}>
            <label>New team<input value={teamName} onChange={(event) => setTeamName(event.target.value)} placeholder="Gatebound" /></label>
            <button className="primary-action" disabled={!teamName.trim()}><Plus size={16} />Create team</button>
          </form>
          <div className="settings-list">
            {teams.map((team) => (
              <button className={selectedTeam?.id === team.id ? 'team-select active' : 'team-select'} key={team.id} onClick={() => setSelectedTeamId(team.id)}>
                <strong>{team.name}</strong>
                <span>{team.members.length} members</span>
              </button>
            ))}
            {teams.length === 0 && <p className="provider-status">No teams yet.</p>}
          </div>
        </aside>
        <section className="panel form-panel">
          {selectedTeam ? (
            <>
              <div className="settings-row">
                <div>
                  <strong>{selectedTeam.name}</strong>
                  <p>{selectedTeam.members.length} members - created {relativeTime(selectedTeam.createdAt)}</p>
                </div>
                <span className="state-good">{selectedTeam.members.some((member) => member.userId === me.id) ? 'Your team' : 'Team'}</span>
              </div>
              <form className="team-action-form" onSubmit={(event) => void inviteMember(event)}>
                <label>Email<input value={inviteEmail} onChange={(event) => setInviteEmail(event.target.value)} placeholder="person@example.com" /></label>
                <label>Role<select value={inviteRole} onChange={(event) => setInviteRole(event.target.value)}><option>Member</option><option>Admin</option><option>Viewer</option><option>Owner</option></select></label>
                <button className="secondary" disabled={!inviteEmail.trim()}><Users size={16} />Add member</button>
              </form>
              <div className="settings-list">
                {selectedTeam.members.map((member) => (
                  <div className="member-row detailed" key={`${selectedTeam.id}-${member.userId}`}>
                    <span>{member.displayName || member.email || displayUserName(member.userId, me)}<span className="member-meta">{member.email || member.status}</span></span>
                    <strong>{member.role}</strong>
                  </div>
                ))}
              </div>
              <form className="team-action-form" onSubmit={(event) => void assignBoard(event)}>
                <label>Board<select value={boardId} onChange={(event) => setBoardId(event.target.value)}>
                  <option value="">Select board...</option>
                  {assignableBoards.map((board) => <option key={board.id} value={board.id}>{board.name}</option>)}
                </select></label>
                <label>Board role<select value={boardRole} onChange={(event) => setBoardRole(event.target.value)}><option>Member</option><option>Admin</option><option>Viewer</option><option>Owner</option></select></label>
                <button className="secondary" disabled={!boardId}><PanelLeft size={16} />Assign board</button>
              </form>
              <div className="settings-list">
                {assignedBoards.length === 0
                  ? <p className="provider-status">No boards are assigned to this team.</p>
                  : assignedBoards.map((board) => {
                    const access = board.teamAccess?.find((entry) => entry.teamId === selectedTeam.id);
                    return (
                      <div className="settings-row" key={board.id}>
                        <div>
                          <strong>{board.name}</strong>
                          <p>{boardRepositorySummary(board)}</p>
                        </div>
                        <div className="button-row">
                          <span className="state-muted">{access?.role ?? 'Member'}</span>
                          <button className="danger-button compact" type="button" onClick={() => void actions.removeTeamFromBoard(board.id, selectedTeam.id)}><Trash2 size={14} />Remove</button>
                        </div>
                      </div>
                    );
                  })}
              </div>
            </>
          ) : <EmptyState>Select or create a team.</EmptyState>}
        </section>
      </div>
    </section>
  );
}

function SettingsView({ scope, settings, board, me, teams, repositories, boardSecrets, githubIntegrations, selectedProvider, selectedModel, selectedReasoning, actions, onProviderChange, onModelChange, onReasoningChange, onSyncBoard, onBack }: {
  scope: 'global' | 'board' | 'ai' | 'environment';
  settings: SettingsDto;
  board: Board;
  me: UserDto;
  teams: TeamDto[];
  repositories: RepositoryDto[];
  boardSecrets: BoardSecretDto[];
  githubIntegrations: GitHubIntegrationDto[];
  selectedProvider: string | null;
  selectedModel: string | null;
  selectedReasoning: string | null;
  actions: BoardActions;
  onProviderChange: (provider: string) => void;
  onModelChange: (model: string) => void;
  onReasoningChange: (reasoning: string) => void;
  onSyncBoard: () => void;
  onBack: () => void;
}) {
  const activeProvider = resolveActiveAiProvider(settings, selectedProvider);
  const modelOptions = activeProvider.availableModels.length > 0 ? activeProvider.availableModels : [activeProvider.activeModel];
  const activeModel = resolveActiveAiModel(settings, selectedProvider, selectedModel);
  const reasoningOptions = activeProvider.availableReasoningEfforts?.length ? activeProvider.availableReasoningEfforts : [];
  const activeReasoning = resolveActiveAiReasoning(settings, selectedProvider, selectedReasoning);
  const [secretKey, setSecretKey] = React.useState('');
  const [secretValue, setSecretValue] = React.useState('');
  const [secretRepositoryId, setSecretRepositoryId] = React.useState('');
  const boardRepositories = board.repositories?.length ? board.repositories : board.repository ? [{ boardId: board.id, repositoryId: board.repository.id, isPrimary: true, implementationProfile: board.repository.implementationProfile, implementationWorkflow: board.implementationWorkflow ?? board.repository.implementationWorkflow, repository: board.repository }] : [];
  const canSaveSecret = secretKey.trim().length > 0 && secretValue.length > 0;
  const latestGitHubIntegration = githubIntegrations[0];
  const gitHubConnected = settings.gitHub.connected || githubIntegrations.length > 0;
  const gitHubAccount = latestGitHubIntegration ? `${latestGitHubIntegration.accountLogin} (${latestGitHubIntegration.accountType})` : settings.gitHub.account;
  const gitHubTarget = latestGitHubIntegration ? `${latestGitHubIntegration.repositoriesCount} repositories granted` : settings.gitHub.targetRepository;

  async function saveSecret(event: React.FormEvent) {
    event.preventDefault();
    if (!canSaveSecret) return;
    if (await actions.createBoardSecret(board.id, secretKey.trim(), secretValue, secretRepositoryId || null)) {
      setSecretKey('');
      setSecretValue('');
      setSecretRepositoryId('');
    }
  }

  return (
    <section className="page settings-page">
      {scope !== 'global'
        ? (
          <BoardHeader board={board} onSyncBoard={onSyncBoard} subtitle={boardSettingsSubtitle(scope)}>
            <button className="secondary" onClick={onBack}>Back to board</button>
          </BoardHeader>
        )
        : (
          <div className="page-heading">
            <div>
              <h1>Settings</h1>
              <p>Global integrations, AI providers and system settings.</p>
            </div>
            <button className="secondary" onClick={onBack}>Back to board</button>
          </div>
        )}
      <div className="settings-content">
        {scope === 'global' && <>
        <SectionTitle icon={<Github size={22} />} title="GitHub integration" />
        <section className="panel form-panel">
          <div className="connected"><Github size={28} /><div><strong>Connected account</strong><p>{gitHubAccount}</p></div><span>{gitHubConnected ? 'Active' : 'Disconnected'}</span></div>
          <label>Target repository<input value={gitHubTarget} readOnly /></label>
          <label>Branch watch patterns<input value={settings.gitHub.branchWatchPatterns} readOnly /></label>
          <div className="settings-inline">
            <div>
              <strong>GitHub App installations</strong>
              <p>Repositories are fetched through server-side installation tokens. Browser clients never receive GitHub credentials.</p>
            </div>
            <div className="button-row">
              {settings.gitHub.appConfigured && (
                <button className="secondary" onClick={() => void actions.syncGitHubIntegration()} type="button"><RefreshCw size={16} />Sync existing installation</button>
              )}
              {!settings.gitHub.appConfigured && (
                <button className="secondary" onClick={() => void actions.addGitHubIntegration()} type="button"><Github size={16} />Install GitHub App</button>
              )}
            </div>
          </div>
          {githubIntegrations.length === 0
            ? <p className="provider-status">{settings.gitHub.appConfigured ? 'App exists but no GitHub installation is visible yet. Sync existing installation before creating another app.' : 'GitHub App credentials are not mounted on the API pod yet.'}</p>
            : (
              <div className="settings-list">
                {githubIntegrations.map((integration) => (
                  <GitHubIntegrationStatus
                    integration={integration}
                    key={integration.id}
                    onAuthorize={actions.authorizeGitHubUser}
                  />
                ))}
              </div>
            )}
        </section>
        <SectionTitle icon={<Bot size={22} />} title="AI engine" amber />
        <section className="panel form-panel ai-settings">
          <label>Provider<select value={activeProvider.provider} onChange={(event) => onProviderChange(event.target.value)}>{settings.ai.availableProviders.map((provider) => <option value={provider.provider} key={provider.provider} disabled={provider.status === 'Unavailable'}>{provider.displayName} - {provider.status}</option>)}</select></label>
          <label>Planning model<select value={activeModel} onChange={(event) => onModelChange(event.target.value)}>{modelOptions.map((model) => <option value={model} key={model}>{model}</option>)}</select></label>
          {reasoningOptions.length > 0 && <label>Reasoning effort<select value={activeReasoning ?? ''} onChange={(event) => onReasoningChange(event.target.value)}>{reasoningOptions.map((effort) => <option value={effort} key={effort}>{effort}</option>)}</select></label>}
          <label>Adapter endpoint<input value={activeProvider.endpoint} readOnly /></label>
          <p className={activeProvider.status === 'Ready' ? 'provider-status ready' : 'provider-status'}>{activeProvider.displayName} status: {activeProvider.status === 'LoginRequired' ? 'Login required on server' : activeProvider.status}.</p>
          <label>Auto review pull requests<input value={settings.ai.autoReviewPullRequests ? 'Enabled' : 'Disabled'} readOnly /></label>
        </section>
        </>}
        {scope === 'board' && <>
        <SectionTitle icon={<ExternalLink size={22} />} title="Hosting workflow" />
        <section className="panel form-panel">
          <BoardHostingForm board={board} actions={actions} />
        </section>
        <SectionTitle icon={<GitPullRequest size={22} />} title="Board repositories" />
        <section className="panel form-panel">
          <BoardRepositorySummary repositories={boardRepositories} onSyncBoard={onSyncBoard} />
        </section>
        <SectionTitle icon={<Trash2 size={22} />} title="Danger zone" />
        <section className="panel form-panel">
          <BoardDangerZone board={board} actions={actions} />
        </section>
        </>}
        {scope === 'global' && <>
        <SectionTitle icon={<Users size={22} />} title="Current user" />
        <section className="panel form-panel">
          <div className="connected"><Users size={28} /><div><strong>{me.displayName}</strong><p>{me.email || me.subject}</p></div><span>Signed in</span></div>
          <p className="provider-status">Team membership and board assignment now live in the Teams view.</p>
        </section>
        </>}
        {scope === 'environment' && <>
        <SectionTitle icon={<KeyRound size={22} />} title="Environment" />
        <section className="panel form-panel">
          <div className="settings-inline">
            <div>
              <strong>{board.name}</strong>
              <p>Secret values are written to Kubernetes Secrets. RDO only stores metadata and never returns saved values.</p>
            </div>
          </div>
          <form className="secret-form" onSubmit={(event) => void saveSecret(event)}>
            <label>Key<input value={secretKey} onChange={(event) => setSecretKey(event.target.value)} placeholder="NPM_TOKEN" /></label>
            <label>Value<input value={secretValue} onChange={(event) => setSecretValue(event.target.value)} placeholder="Stored once, never shown again" type="password" /></label>
            <label>Scope<select value={secretRepositoryId} onChange={(event) => setSecretRepositoryId(event.target.value)}>
              <option value="">Board-wide</option>
              {boardRepositories.map((entry) => <option key={entry.repositoryId} value={entry.repositoryId}>{repositoryLabel(entry.repository)}</option>)}
            </select></label>
            <button className="primary-action" disabled={!canSaveSecret} type="submit"><Save size={16} />Save secret</button>
          </form>
          {boardSecrets.length === 0
            ? <p className="provider-status">No board secrets configured.</p>
            : (
              <div className="settings-list">
                {boardSecrets.map((secret) => {
                  const repository = repositories.find((entry) => entry.id === secret.repositoryId) ?? boardRepositories.find((entry) => entry.repositoryId === secret.repositoryId)?.repository;
                  return (
                    <div className="settings-row" key={secret.id}>
                      <div>
                        <strong>{secret.key}</strong>
                        <p>{repository ? repositoryLabel(repository) : 'Board-wide'} - updated {relativeTime(secret.updatedAt)}{secret.lastUsedAt ? ` - last used ${relativeTime(secret.lastUsedAt)}` : ''}</p>
                      </div>
                      <button className="danger-button" onClick={() => confirm(`Delete ${secret.key}?`) && void actions.deleteBoardSecret(board.id, secret.id)} type="button"><Trash2 size={16} />Delete</button>
                    </div>
                  );
                })}
              </div>
            )}
        </section>
        </>}
        {scope === 'ai' && <>
        <SectionTitle icon={<Bot size={22} />} title="Board AI" amber />
        <section className="panel form-panel">
          <BoardAiSettingsForm board={board} actions={actions} />
        </section>
        <SectionTitle icon={<Sparkles size={22} />} title="Repository profiles and skill drafts" />
        <section className="panel form-panel">
          <div className="settings-list">
            {boardRepositories.length === 0
              ? <p className="provider-status">No repositories are linked to this board.</p>
              : boardRepositories.map((entry) => (
                <RepositorySettingsProfileEditor board={board} entry={entry} actions={actions} key={entry.repositoryId} />
              ))}
          </div>
        </section>
        </>}
        {scope === 'global' && <>
        <SectionTitle icon={<ExternalLink size={22} />} title="Preview environments" />
        <section className="panel form-panel">
          <label>Domain<input value={settings.preview.domain} readOnly /></label>
          <label>Default TTL<input value={`${settings.preview.defaultTtlDays} days`} readOnly /></label>
          <label>Namespace strategy<input value={settings.preview.namespace} readOnly /></label>
        </section>
        <SectionTitle icon={<GitPullRequest size={22} />} title="Repository hosting" />
        <section className="panel form-panel">
          <div className="connected"><GitPullRequest size={28} /><div><strong>{settings.repositories.provider}</strong><p>{settings.repositories.mode}</p></div><span>{settings.repositories.canCreateRepositories ? 'Create enabled' : 'Link only'}</span></div>
          <label>Forgejo API<input value={settings.repositories.apiBaseUrl} readOnly /></label>
        </section>
        <SectionTitle icon={<Users size={22} />} title="Authentik users" />
        <section className="panel form-panel">
          <div className="connected"><Users size={28} /><div><strong>{settings.authentik.enabled ? 'Enabled' : 'Disabled'}</strong><p>{settings.authentik.authority}</p></div><span>{settings.authentik.enabled ? 'Active' : 'Local'}</span></div>
          <label>Users endpoint<input value={settings.authentik.usersEndpoint} readOnly /></label>
        </section>
        </>}
      </div>
    </section>
  );
}

function BoardHostingForm({ board, actions }: { board: Board; actions: BoardActions }) {
  const [publicHostname, setPublicHostname] = React.useState(board.publicHostname ?? '');
  const [workflow, setWorkflow] = React.useState(board.implementationWorkflow ?? workflowForRepositoryProfile(board.repository?.implementationProfile, board.repository?.implementationWorkflow));
  const appStatus = boardPublicAppStatusLabel(board);

  React.useEffect(() => {
    setPublicHostname(board.publicHostname ?? '');
    setWorkflow(board.implementationWorkflow ?? workflowForRepositoryProfile(board.repository?.implementationProfile, board.repository?.implementationWorkflow));
  }, [board.id, board.publicHostname, board.implementationWorkflow, board.repository?.implementationProfile, board.repository?.implementationWorkflow]);

  return (
    <form className="form-grid two" onSubmit={(event) => {
      event.preventDefault();
      void actions.updateBoardHosting(board.id, { publicHostname: publicHostname.trim() || null, implementationWorkflow: workflow });
    }}>
      <label>Implementation workflow<select value={workflow ?? 'preview-only'} onChange={(event) => setWorkflow(event.target.value)}>
        <option value="preview-only">Preview only</option>
        <option value="preview-then-pr">Preview, then PR</option>
        <option value="direct-pr">Direct PR</option>
      </select></label>
      <label>Public hostname<input value={publicHostname} onChange={(event) => setPublicHostname(event.target.value)} placeholder={`${slugFromText(board.name || 'app')}.rosenvall.se`} /></label>
      <p className="provider-status">Preview URLs stay temporary per card. Public hostname is production intent for website repository PRs.</p>
      {board.publicApp?.status === 'Running' && <p className="provider-status full-width">Production app is live at {board.publicApp.url}.</p>}
      {appStatus === 'App not deployed' && <p className="provider-status full-width">Production app will be created from an approved preview after the pull request is merged.</p>}
      {appStatus && appStatus !== 'App not deployed' && appStatus !== 'App deploying' && <p className="failure-reason full-width">{appStatus}{board.publicApp?.message ? `: ${board.publicApp.message}` : ''}</p>}
      {appStatus === 'App deploying' && <p className="provider-status full-width">{appStatus}{board.publicApp?.message ? `: ${board.publicApp.message}` : ''}</p>}
      <div className="form-actions-row"><button className="secondary" type="submit"><Save size={16} />Save hosting</button></div>
    </form>
  );
}

function GitHubIntegrationStatus({ integration, onAuthorize }: { integration: GitHubIntegrationDto; onAuthorize: (installationId: number) => Promise<boolean> }) {
  const canCreate = canCreateRepositoryInInstallation(integration);
  const needsAuthorization = integration.requiresUserAuthorizationForRepositoryCreation && !canCreate;
  return (
    <div className="settings-row">
      <div>
        <strong>{integration.accountLogin}</strong>
        <p>{integration.accountType} - {integration.repositoriesCount} repositories - installed by {integration.installedBy}</p>
        <p>{integration.repositoryCreationMessage ?? (canCreate ? 'Repository creation permission granted.' : 'Existing repositories can still be linked.')}</p>
      </div>
      <div className="status-badges">
        <span className={integration.status === 'Active' ? 'state-good' : 'state-muted'}>{integration.status}</span>
        <span className={canCreate ? 'state-good' : 'state-muted'}>{canCreate ? 'Can create repos' : 'Existing repos only'}</span>
        {integration.hasUserAuthorization && <span className="state-good">User authorized{integration.authorizedGitHubLogin ? `: ${integration.authorizedGitHubLogin}` : ''}</span>}
        {needsAuthorization && <button type="button" className="secondary compact" onClick={() => void onAuthorize(integration.installationId)}><Github size={14} />Authorize GitHub user</button>}
      </div>
    </div>
  );
}

function BoardRepositorySummary({ repositories, onSyncBoard }: { repositories: BoardRepositoryDto[]; onSyncBoard: () => void }) {
  return (
    <div className="settings-list">
      {repositories.length === 0
        ? <p className="provider-status">No repositories are linked to this board.</p>
        : repositories.map((entry) => (
          <div className="settings-row" key={entry.repositoryId}>
            <div>
              <strong>{repositoryLabel(entry.repository)}</strong>
              <p>{entry.repository.provider} - {entry.repository.defaultBranch} - {profileLabel(entry.implementationProfile)} - {workflowLabel(entry.implementationWorkflow ?? entry.repository.implementationWorkflow)}</p>
            </div>
            <span className={entry.isPrimary ? 'state-good' : 'state-muted'}>{entry.isPrimary ? 'Primary' : 'Linked'}</span>
          </div>
        ))}
      <div className="settings-inline">
        <p className="provider-status">Use AI to edit profiles and skill drafts. Use Environment for board secrets.</p>
        <button className="secondary compact" type="button" onClick={onSyncBoard}><Github size={14} />Link GitHub repo</button>
      </div>
    </div>
  );
}

function BoardDangerZone({ board, actions }: { board: Board; actions: BoardActions }) {
  const [confirmText, setConfirmText] = React.useState('');
  const expected = board.name;
  const canDelete = confirmText === expected;
  return (
    <div className="settings-list">
      <div className="settings-row vertical">
        <div>
          <strong>Delete board and clean up</strong>
          <p>Kubernetes preview namespaces, runner jobs, per-run token secrets, pipeline jobs, board-owned environment secrets and board-owned Local Git repositories are deleted. GitHub repositories, branches, pull requests and shared platform credentials are not deleted automatically.</p>
        </div>
        <label>Type board name to confirm<input value={confirmText} onChange={(event) => setConfirmText(event.target.value)} placeholder={expected} /></label>
        <button className="danger-button" type="button" disabled={!canDelete} onClick={() => canDelete && confirm(boardDeleteCleanupMessage(board.name)) && void actions.deleteBoard(board.id)}><Trash2 size={16} />Delete board and clean up</button>
      </div>
    </div>
  );
}

function BoardAiSettingsForm({ board, actions }: { board: Board; actions: BoardActions }) {
  const initial = board.aiContext ?? defaultAiContext(board.id);
  const [instructions, setInstructions] = React.useState(initial.instructions);
  const [agentInstructions, setAgentInstructions] = React.useState(initial.agentInstructions ?? '');
  const [askWhenUncertain, setAskWhenUncertain] = React.useState(initial.askWhenUncertain);
  const [skills, setSkills] = React.useState<string[]>(initial.enabledSkills);
  const [newSkill, setNewSkill] = React.useState('');

  React.useEffect(() => {
    const next = board.aiContext ?? defaultAiContext(board.id);
    setInstructions(next.instructions);
    setAgentInstructions(next.agentInstructions ?? '');
    setAskWhenUncertain(next.askWhenUncertain);
    setSkills(next.enabledSkills);
    setNewSkill('');
  }, [board.id, board.aiContext]);

  const addSkill = () => {
    const normalized = newSkill.trim();
    if (!normalized || skills.some((skill) => skill.toLowerCase() === normalized.toLowerCase())) return;
    setSkills([...skills, normalized]);
    setNewSkill('');
  };

  return (
    <form className="settings-stack" onSubmit={(event) => {
      event.preventDefault();
      void actions.updateBoardAiContext(board.id, {
        boardId: board.id,
        instructions,
        enabledSkills: skills,
        askWhenUncertain,
        agentInstructions
      });
    }}>
      <div className="form-grid two">
        <label>Board instructions<textarea value={instructions} onChange={(event) => setInstructions(event.target.value)} placeholder="Operating rules and board-specific conventions." /></label>
        <label>Agent instructions<textarea value={agentInstructions} onChange={(event) => setAgentInstructions(event.target.value)} placeholder="AGENTS-style guidance used in planning and implementation context." /></label>
        <label className="checkbox-row"><input type="checkbox" checked={askWhenUncertain} onChange={(event) => setAskWhenUncertain(event.target.checked)} /><span>Ask when uncertain</span></label>
      </div>
      <div className="skill-editor">
        <div className="settings-inline">
          <div>
            <strong>Enabled skills</strong>
            <p>Board-local skill names that are included in AI context. Repo-specific draft contents are edited below.</p>
          </div>
          <div className="button-row">
            <input value={newSkill} onChange={(event) => setNewSkill(event.target.value)} placeholder="skill-name" />
            <button type="button" className="secondary compact" onClick={addSkill}><Plus size={14} />Add skill</button>
          </div>
        </div>
        {skills.length === 0
          ? <p className="provider-status">No board skills enabled.</p>
          : <div className="tag-list">{skills.map((skill) => (
            <span className="tag-pill" key={skill}>{skill}<button type="button" onClick={() => setSkills(skills.filter((entry) => entry !== skill))} aria-label={`Remove ${skill}`}><X size={12} /></button></span>
          ))}</div>}
      </div>
      <div className="form-actions-row"><button className="primary-action" type="submit"><Save size={16} />Save AI</button></div>
    </form>
  );
}

function RepositorySettingsProfileEditor({ board, entry, actions }: { board: Board; entry: BoardRepositoryDto; actions: BoardActions }) {
  const [form, setForm] = React.useState<CreateBoardForm>(() => createBoardFormFromRepositoryProfile(board, entry));
  const [profile, setProfile] = React.useState<RepositoryProfileDto | null>(entry.profile ?? repositoryProfileFromForm(createBoardFormFromRepositoryProfile(board, entry)));
  const [profileStatus, setProfileStatus] = React.useState<'idle' | 'loading' | 'loaded' | 'error'>('loaded');
  const [aiProfileStatus, setAiProfileStatus] = React.useState<'idle' | 'loading' | 'loaded' | 'error'>('idle');
  const [profileError, setProfileError] = React.useState<string | null>(null);

  React.useEffect(() => {
    const next = createBoardFormFromRepositoryProfile(board, entry);
    setForm(next);
    setProfile(entry.profile ?? repositoryProfileFromForm(next));
    setProfileStatus('loaded');
    setAiProfileStatus('idle');
    setProfileError(null);
  }, [board.id, board.aiContext, board.gitOpsSettings, entry.profile, entry.implementationProfile, entry.repository.defaultBranch, entry.repository.name, entry.repository.owner]);

  const analyzeAgain = async () => {
    if (!entry.repository.owner) {
      setProfileError('Repository owner is missing; cannot analyze through GitHub.');
      return;
    }

    setProfileStatus('loaded');
    setAiProfileStatus('loading');
    setProfileError(null);
    const query = new URLSearchParams({
      owner: entry.repository.owner,
      repo: entry.repository.name,
      branch: entry.repository.defaultBranch || 'main',
      mode: 'full'
    });
    try {
      const nextProfile = await api.get<RepositoryProfileDto>(`/api/integrations/github/repository-profile?${query.toString()}`, { timeoutMs: 45000 });
      setProfile(nextProfile);
      setForm((current) => ({
        ...current,
        implementationProfile: normalizeImplementationProfile(nextProfile.implementationProfile),
        enabledSkills: (nextProfile.enabledSkills ?? []).join('\n'),
        capabilityTags: (nextProfile.capabilityTags ?? []).join('\n'),
        aiInstructions: nextProfile.instructions,
        skillDrafts: nextProfile.skillDrafts ?? []
      }));
      setAiProfileStatus('loaded');
    } catch (error) {
      setAiProfileStatus('error');
      setProfileError(error instanceof Error ? error.message : 'Codex analysis failed');
    }
  };

  return (
    <div className="repository-settings-editor">
      <div className="settings-row">
        <div>
          <strong>{repositoryLabel(entry.repository)}</strong>
          <p>{entry.repository.provider} - {entry.repository.defaultBranch} - {profileLabel(form.implementationProfile)}</p>
        </div>
        <span className={entry.isPrimary ? 'state-good' : 'state-muted'}>{entry.isPrimary ? 'Primary' : 'Linked'}</span>
      </div>
      <RepositoryProfileEditor
        form={form}
        profile={profile}
        profileStatus={profileStatus}
        aiProfileStatus={aiProfileStatus}
        profileError={profileError}
        onChange={setForm}
        onAnalyze={() => void analyzeAgain()}
        onSave={() => void actions.updateBoardRepositoryProfile(board.id, entry.repositoryId, repositoryProfileFromForm(form, profile))}
        saveLabel="Save profile"
      />
    </div>
  );
}

function GitOpsSettingsForm({ board, actions }: { board: Board; actions: BoardActions }) {
  const initialGitOps = board.gitOpsSettings ?? defaultGitOpsSettings(board.id);
  const [allowedPaths, setAllowedPaths] = React.useState(initialGitOps.allowedPaths.join('\n'));
  const [argoNamespace, setArgoNamespace] = React.useState(initialGitOps.argoNamespace);
  const [argoApplicationSelector, setArgoApplicationSelector] = React.useState(initialGitOps.argoApplicationSelector);

  React.useEffect(() => {
    const gitops = board.gitOpsSettings ?? defaultGitOpsSettings(board.id);
    setAllowedPaths(gitops.allowedPaths.join('\n'));
    setArgoNamespace(gitops.argoNamespace);
    setArgoApplicationSelector(gitops.argoApplicationSelector);
  }, [board.id, board.gitOpsSettings]);

  return (
    <div className="settings-stack">
      <form className="form-grid two" onSubmit={(event) => {
        event.preventDefault();
        void actions.updateBoardGitOpsSettings(board.id, {
          boardId: board.id,
          allowedPaths: linesFromTextarea(allowedPaths),
          argoNamespace,
          argoApplicationSelector
        });
      }}>
        <label>Allowed paths<textarea value={allowedPaths} onChange={(event) => setAllowedPaths(event.target.value)} /></label>
        <label>ArgoCD namespace<input value={argoNamespace} onChange={(event) => setArgoNamespace(event.target.value)} /></label>
        <label>ArgoCD application label selector<input value={argoApplicationSelector} onChange={(event) => setArgoApplicationSelector(event.target.value)} placeholder="app.kubernetes.io/part-of=homelab" /></label>
        <p className="provider-status full-width">Optional Kubernetes label selector used when listing ArgoCD Application resources in the configured namespace. Leave blank to list all apps.</p>
        <div className="form-actions-row"><button className="secondary" type="submit"><Save size={16} />Save GitOps</button></div>
      </form>
    </div>
  );
}

function GitOpsApplicationsPanel({ response }: { response: GitOpsApplicationsResponseDto }) {
  return (
    <div className="settings-list gitops-applications">
      {response.message && <p className="provider-status">{response.message}</p>}
      {response.applications.map((app) => {
        const appUrls = publicApplicationUrls(app.applicationUrls ?? []);
        return (
          <div className="settings-row" key={`${app.namespace}/${app.name}`}>
            <div>
              <strong>{app.name}</strong>
              <p>{app.namespace} - {app.revision || 'no revision'}{app.updatedAt ? ` - updated ${relativeTime(app.updatedAt)}` : ''}</p>
              {app.message && <p>{app.message}</p>}
            </div>
            <div className="status-badges">
              <span className={statusBadgeClass(app.syncStatus)}>{app.syncStatus}</span>
              <span className={statusBadgeClass(app.healthStatus)}>{app.healthStatus}</span>
              {appUrls.length === 1 && <SafeExternalLink className="secondary compact" href={appUrls[0]} ariaLabel={`Open ${app.name} app`}><ExternalLink size={14} />Go to app</SafeExternalLink>}
              {appUrls.length > 1 && (
                <select className="compact-select" value="" aria-label={`Open ${app.name} app`} onChange={(event) => {
                  if (event.target.value) window.open(event.target.value, '_blank', 'noopener,noreferrer');
                  event.currentTarget.value = '';
                }}>
                  <option value="">Open app...</option>
                  {appUrls.map((url) => <option key={url} value={url}>{applicationUrlLabel(url)}</option>)}
                </select>
              )}
              {app.url && <SafeExternalLink href={app.url} ariaLabel={`Open ${app.name} in ArgoCD`}><ExternalLink size={16} /></SafeExternalLink>}
            </div>
          </div>
        );
      })}
    </div>
  );
}

function ModalFrame({ title, onClose, children, size = 'default' }: { title: string; onClose: () => void; children: React.ReactNode; size?: 'default' | 'wide' }) {
  return (
    <div className="modal-backdrop" role="presentation" onMouseDown={(event) => event.target === event.currentTarget && onClose()}>
      <section className={size === 'wide' ? 'modal modal-wide' : 'modal'} role="dialog" aria-modal="true" aria-label={title}>
        <header className="modal-head">
          <h2>{title}</h2>
          <button className="icon-button" onClick={onClose} aria-label="Close"><X size={18} /></button>
        </header>
        {children}
      </section>
    </div>
  );
}

function PullRequestDiffModal(props: PullRequestDiffReviewProps) {
  const title = props.state.status === 'loaded' ? `Local pull request #${props.state.diff.number}` : 'Local pull request diff';
  return (
    <ModalFrame title={title} onClose={props.onClose} size="wide">
      <PullRequestDiffReview {...props} />
    </ModalFrame>
  );
}

type PullRequestDiffReviewProps = {
  state:
    { status: 'loading' } |
    { status: 'loaded'; diff: PullRequestDiffDto; selectedPath: string | null } |
    { status: 'error'; message: string };
  busy: boolean;
  busyLabel: string | null;
  onClose: () => void;
  onSelectFile: (path: string) => void;
  onRetry: () => void;
  onCreateComment: (request: { filePath: string; side: string; lineNumber: number; diffLine: string; body: string }) => Promise<void>;
  onUpdateComment: (commentId: string, request: { body?: string | null; status?: string | null }) => Promise<void>;
  onDeleteComment: (commentId: string) => Promise<void>;
  onFixComments: () => Promise<boolean>;
  onApprovePr: () => Promise<boolean>;
};

function PullRequestDiffReview({ state, busy, busyLabel, onSelectFile, onRetry, onCreateComment, onUpdateComment, onDeleteComment, onFixComments, onApprovePr }: PullRequestDiffReviewProps) {
  const scrollRef = React.useRef<HTMLDivElement | null>(null);
  const sectionRefs = React.useRef<Record<string, HTMLElement | null>>({});
  const [activePath, setActivePath] = React.useState<string | null>(state.status === 'loaded' ? state.selectedPath ?? state.diff.files[0]?.path ?? null : null);
  const [composer, setComposer] = React.useState<{ filePath: string; side: string; lineNumber: number; diffLine: string } | null>(null);
  const [composerBody, setComposerBody] = React.useState('');
  const [localApproving, setLocalApproving] = React.useState(false);
  const [approvalError, setApprovalError] = React.useState<string | null>(null);
  const [showSlowApproval, setShowSlowApproval] = React.useState(false);
  const loadedState = state.status === 'loaded' ? state : null;
  const diff = loadedState?.diff ?? emptyPullRequestDiff();
  const parsed = React.useMemo(() => parseUnifiedDiffForContinuousReview(diff.diff, diff.files), [diff.diff, diff.files]);
  const comments = diff.reviewComments ?? [];
  const countsByFile = React.useMemo(() => reviewCommentCountsByFile(comments), [comments]);
  const unresolvedCount = unresolvedReviewCommentCount(comments);
  const approvalState = buildLocalPullRequestApprovalState({
    ...diff,
    unresolvedComments: unresolvedCount,
    pending: localApproving || (busy && (busyLabel === 'Merging local PR and deploying app' || busyLabel === 'Approving PR and deploying app'))
  });
  const canApprove = canApprovePullRequestWithComments(comments) && approvalState.canApprove;
  const fixing = busy && busyLabel === 'Fixing review comments with AI';
  const approving = approvalState.status === 'approving';
  const commentsByLine = React.useMemo(() => {
    const map = new Map<string, PullRequestReviewCommentDto[]>();
    for (const comment of comments) {
      const key = reviewLineKey(comment.filePath, comment.side, comment.lineNumber);
      map.set(key, [...(map.get(key) ?? []), comment]);
    }
    return map;
  }, [comments]);

  React.useEffect(() => {
    setActivePath(loadedState?.selectedPath ?? diff.files[0]?.path ?? parsed.sections[0]?.path ?? null);
  }, [diff.files, loadedState?.selectedPath, parsed.sections]);
  React.useEffect(() => {
    setApprovalError(null);
  }, [diff.number, diff.pullRequestApprovedAt, diff.approvalStatus]);
  React.useEffect(() => {
    if (!approving) {
      setShowSlowApproval(false);
      return;
    }

    const timer = window.setTimeout(() => setShowSlowApproval(true), 3000);
    return () => window.clearTimeout(timer);
  }, [approving]);

  if (state.status === 'loading') {
    return <div className="modal-loading"><span className="spinner" /> Loading pull request diff...</div>;
  }

  if (state.status === 'error') {
    return (
      <div className="diff-error">
        <p className="failure-reason">{state.message}</p>
        <button className="secondary" type="button" onClick={onRetry}><RefreshCw size={16} />Retry</button>
      </div>
    );
  }

  const jumpToFile = (path: string) => {
    setActivePath(path);
    onSelectFile(path);
    sectionRefs.current[path]?.scrollIntoView({ block: 'start' });
  };

  const handleScroll = () => {
    const container = scrollRef.current;
    if (!container) return;
    const containerTop = container.getBoundingClientRect().top;
    let current = activePath;
    for (const section of parsed.sections) {
      const element = sectionRefs.current[section.path];
      if (!element) continue;
      if (element.getBoundingClientRect().top - containerTop <= 80) {
        current = section.path;
      }
    }
    if (current && current !== activePath) {
      setActivePath(current);
      onSelectFile(current);
    }
  };

  const openComposer = (sectionPath: string, line: ContinuousDiffLine) => {
    if (!line.commentable) return;
    const side = line.side === 'old' ? 'old' : 'new';
    const lineNumber = side === 'old' ? line.oldLine : line.newLine ?? line.oldLine;
    if (!lineNumber) return;
    setComposer({ filePath: sectionPath, side, lineNumber, diffLine: line.text });
    setComposerBody('');
  };

  const saveComposer = async () => {
    if (!composer || !composerBody.trim()) return;
    await onCreateComment({ ...composer, body: composerBody.trim() });
    setComposer(null);
    setComposerBody('');
  };
  const approvePullRequest = async () => {
    if (!canApprove || localApproving) return;
    setApprovalError(null);
    setLocalApproving(true);
    try {
      const approved = await onApprovePr();
      if (!approved) {
        setApprovalError('Approval failed. See the notification for details.');
      }
    } finally {
      setLocalApproving(false);
    }
  };

  return (
    <div className="diff-modal">
        <header className="diff-summary">
          <div>
            <strong>{diff.repository}</strong>
            <p>{diff.baseBranch ?? 'base'} {'<-'} {diff.headBranch ?? 'head'} - {diff.state}</p>
          </div>
          <div className="diff-stats">
            <span>{diff.changedFiles} files</span>
            <span className="diff-add">+{diff.additions}</span>
            <span className="diff-del">-{diff.deletions}</span>
          </div>
        </header>
        <div className="diff-actions">
          <span>{unresolvedCount > 0 ? `${unresolvedCount} unresolved review comment${unresolvedCount === 1 ? '' : 's'}` : 'No unresolved review comments'}</span>
          <div>
            <button className="secondary" disabled={busy || unresolvedCount === 0} onClick={() => void onFixComments()} type="button">{fixing ? <span className="spinner" /> : <Sparkles size={16} />}{fixing ? 'Fixing comments...' : 'Fix comments with AI'}</button>
            <button className="primary-action" disabled={busy || localApproving || !canApprove} onClick={() => void approvePullRequest()} type="button">{approving ? <span className="spinner" /> : <CheckCircle2 size={16} />}{approving ? 'Merging...' : approvalState.status === 'merged' ? 'Merged' : 'Approve PR'}</button>
          </div>
        </div>
        <p className={`diff-approval-status ${approvalState.status}`}>
          {approvalState.message}
          {showSlowApproval ? ' Merge accepted, deploying app and cleaning preview...' : ''}
        </p>
        {approvalError && <p className="failure-reason">{approvalError}</p>}
        {diff.message && <p className="provider-status">{diff.message}</p>}
        <div className="diff-layout">
          <nav className="diff-files" aria-label="Changed files">
            {diff.files.length === 0 && <p>No changed files were returned.</p>}
            {diff.files.map((file) => (
              <button className={file.path === activePath ? 'active' : ''} key={file.path} type="button" onClick={() => jumpToFile(file.path)}>
                <span>{file.path}</span>
                <small>{file.status ?? 'modified'} {file.additions ? `+${file.additions}` : ''}{file.deletions ? ` -${file.deletions}` : ''}</small>
                {countsByFile[file.path] && (
                  <small>{countsByFile[file.path].unresolved}/{countsByFile[file.path].total} unresolved</small>
                )}
              </button>
            ))}
          </nav>
          <div className="diff-view" aria-label="Unified diff" onScroll={handleScroll} ref={scrollRef}>
            {parsed.sections.length === 0 && <pre>No diff content returned.</pre>}
            {parsed.sections.map((section) => (
              <section className="diff-section" key={section.path} ref={(element) => { sectionRefs.current[section.path] = element; }}>
                <header className="diff-file-header">
                  <strong>{section.path}</strong>
                  <span>{section.file?.status ?? 'modified'} {section.file?.additions ? `+${section.file.additions}` : ''}{section.file?.deletions ? ` -${section.file.deletions}` : ''}</span>
                </header>
                <div className="diff-lines">
                  {section.lines.map((line) => {
                    const lineNumber = line.side === 'old' ? line.oldLine : line.newLine ?? line.oldLine ?? 0;
                    const side = line.side === 'old' ? 'old' : 'new';
                    const lineComments = lineNumber ? commentsByLine.get(reviewLineKey(section.path, side, lineNumber)) ?? [] : [];
                    const isComposerLine = composer?.filePath === section.path && composer.side === side && composer.lineNumber === lineNumber;
                    return (
                      <React.Fragment key={line.id}>
                        <button className={`diff-line ${line.kind}${line.commentable ? ' commentable' : ''}`} disabled={!line.commentable} onClick={() => openComposer(section.path, line)} type="button">
                          <span className="diff-line-number">{line.oldLine ?? ''}</span>
                          <span className="diff-line-number">{line.newLine ?? ''}</span>
                          <code>{line.text || ' '}</code>
                        </button>
                        {isComposerLine && (
                          <div className="diff-comment-composer">
                            <textarea value={composerBody} onChange={(event) => setComposerBody(event.target.value)} placeholder="Comment on this line..." />
                            <div>
                              <button className="primary-action" disabled={!composerBody.trim()} onClick={() => void saveComposer()} type="button">Save comment</button>
                              <button className="secondary" onClick={() => setComposer(null)} type="button">Cancel</button>
                            </div>
                          </div>
                        )}
                        {lineComments.map((comment) => (
                          <div className={comment.status === 'resolved' ? 'diff-comment resolved' : 'diff-comment'} key={comment.id}>
                            <div>
                              <strong>{comment.author}</strong>
                              <span>{comment.status}</span>
                            </div>
                            <p>{comment.body}</p>
                            <div>
                              {comment.status !== 'resolved' && <button className="link-button" onClick={() => void onUpdateComment(comment.id, { status: 'resolved' })} type="button">Resolve</button>}
                              {comment.status === 'resolved' && <button className="link-button" onClick={() => void onUpdateComment(comment.id, { status: 'open' })} type="button">Reopen</button>}
                              <button className="link-button danger-link" onClick={() => void onDeleteComment(comment.id)} type="button">Delete</button>
                            </div>
                          </div>
                        ))}
                      </React.Fragment>
                    );
                  })}
                </div>
              </section>
            ))}
          </div>
        </div>
    </div>
  );
}

function reviewLineKey(filePath: string, side: string, lineNumber: number): string {
  return `${filePath}\n${side === 'old' ? 'old' : 'new'}\n${lineNumber}`;
}

function emptyPullRequestDiff(): PullRequestDiffDto {
  return {
    provider: 'LocalGit',
    repository: '',
    number: 0,
    state: '',
    changedFiles: 0,
    additions: 0,
    deletions: 0,
    truncated: false,
    files: [],
    diff: '',
    reviewComments: [],
    canApprove: false,
    approvalStatus: 'blocked',
    approvalMessage: 'No Local pull request is available.'
  };
}

function ActivityEntry({ actor, aiRuns, comment, busy, onDelete, onSelectPlan, onUpdate }: {
  actor: string;
  aiRuns: AiRun[];
  comment: CommentDto;
  busy: boolean;
  onDelete: (commentId: string) => Promise<void>;
  onSelectPlan: (planId: string) => void;
  onUpdate: (commentId: string, body: string) => Promise<void>;
}) {
  if (comment.kind === 'Plan') {
    return <PlanReferenceComment aiRuns={aiRuns} comment={comment} onSelectPlan={onSelectPlan} />;
  }

  const editable = comment.kind === 'Comment' && comment.author.toLowerCase() === actor.toLowerCase();
  return comment.author === 'Rosenvall AI' || comment.kind !== 'Comment'
    ? <SystemComment aiRuns={aiRuns} comment={comment} onSelectPlan={onSelectPlan} />
    : <HumanComment busy={busy} comment={comment} editable={editable} onDelete={onDelete} onUpdate={onUpdate} />;
}

function HumanComment({ comment, editable, busy, onDelete, onUpdate }: {
  comment: CommentDto;
  editable: boolean;
  busy: boolean;
  onDelete: (commentId: string) => Promise<void>;
  onUpdate: (commentId: string, body: string) => Promise<void>;
}) {
  const [editing, setEditing] = React.useState(false);
  const [draft, setDraft] = React.useState(comment.body);

  React.useEffect(() => {
    setDraft(comment.body);
    setEditing(false);
  }, [comment.body, comment.id]);

  return (
    <article className="comment">
      <div className="avatar small">{initials(comment.author)}</div>
      <div>
        <div className="comment-head">
          <strong>{comment.author}</strong>
          <time>{relativeTime(comment.createdAt)}</time>
          {editable && !editing && (
            <div className="comment-tools">
              <button className="link-button" disabled={busy} onClick={() => setEditing(true)} type="button">Edit</button>
              <button className="link-button danger-link" disabled={busy} onClick={() => confirm('Delete this comment?') && void onDelete(comment.id)} type="button">Delete</button>
            </div>
          )}
        </div>
        {editing
          ? (
            <div className="comment-editor">
              <textarea value={draft} onChange={(event) => setDraft(event.target.value)} />
              <div className="comment-actions">
                <button className="primary-action" disabled={busy || !draft.trim()} onClick={() => void onUpdate(comment.id, draft.trim()).then(() => setEditing(false))} type="button">Save</button>
                <button className="secondary" disabled={busy} onClick={() => { setDraft(comment.body); setEditing(false); }} type="button">Cancel</button>
              </div>
            </div>
          )
          : <CommentBody body={comment.body} />}
      </div>
    </article>
  );
}

function SystemComment({ aiRuns, comment, onSelectPlan }: { aiRuns: AiRun[]; comment: CommentDto; onSelectPlan: (planId: string) => void }) {
  const referencedPlan = findReferencedPlan(comment, aiRuns);
  return (
    <article className="comment result-comment">
      <div className="avatar small">RA</div>
      <div>
        <div className="comment-head"><strong>{comment.author}</strong><span>{comment.kind}</span><time>{relativeTime(comment.createdAt)}</time></div>
        {referencedPlan
          ? <PlanReferenceButton plan={referencedPlan} prefix="Created" onSelectPlan={onSelectPlan} />
          : <CommentBody body={comment.body} />}
      </div>
    </article>
  );
}

function PlanReferenceComment({ aiRuns, comment, onSelectPlan }: { aiRuns: AiRun[]; comment: CommentDto; onSelectPlan: (planId: string) => void }) {
  const referencedPlan = findReferencedPlan(comment, aiRuns);
  return (
    <article className="ai-comment compact-plan-comment">
      <div className="comment-head"><strong>{comment.author}</strong><span>Plan</span><time>{relativeTime(comment.createdAt)}</time></div>
      {referencedPlan
        ? <PlanReferenceButton plan={referencedPlan} prefix="Created" onSelectPlan={onSelectPlan} />
        : (
          <>
            <p>Legacy AI plan</p>
            <CommentBody body={comment.body} />
          </>
        )}
    </article>
  );
}

function PlanReferenceButton({ plan, prefix, onSelectPlan }: { plan: AiRun; prefix: string; onSelectPlan: (planId: string) => void }) {
  return (
    <button className="plan-reference" onClick={() => onSelectPlan(plan.id)} type="button">
      {prefix} plan #{plan.sequenceNumber}: {planTitle(plan)}
    </button>
  );
}

function SafeExternalLink({ href, className, ariaLabel, children }: { href: string | null | undefined; className?: string; ariaLabel?: string; children: React.ReactNode }) {
  const safeHref = href ? safeMarkdownHref(href) : null;
  if (!safeHref) return null;
  return <a className={className} href={safeHref} target="_blank" rel="noreferrer" aria-label={ariaLabel}>{children}</a>;
}

type MarkdownReviewOptions = {
  activeAnchorKey: string | null;
  busy: boolean;
  commentDraft: string;
  commentsByAnchor: Record<string, AiPlanReviewCommentDto[]>;
  editingBody: string;
  editingCommentId: string | null;
  onCancelEdit: () => void;
  onDeleteComment: (commentId: string) => void;
  onDraftChange: (value: string) => void;
  onEditingBodyChange: (value: string) => void;
  onSaveEdit: (commentId: string) => void;
  onSelectAnchor: (anchorKey: string | null) => void;
  onStartEdit: (comment: AiPlanReviewCommentDto) => void;
  onSubmitComment: (block: AiPlanReviewBlock) => void;
  onToggleResolved: (comment: AiPlanReviewCommentDto) => void;
};

function CommentBody({ body, review }: { body: string; review?: MarkdownReviewOptions }) {
  const [expanded, setExpanded] = React.useState(false);
  const collapsible = body.length > 520 || body.split(/\r?\n/).length > 6;
  const collapsed = collapsible && !expanded;
  const visible = collapsed ? `${body.slice(0, 520).trimEnd()}...` : body;
  const activeReview = review && (!collapsible || expanded) ? review : undefined;
  return (
    <div
      className={`${collapsed ? 'markdown-body collapsed' : 'markdown-body'}${review ? ' markdown-review-body' : ''}`}
      onClick={() => collapsed && setExpanded(true)}
      role={collapsed ? 'button' : undefined}
      tabIndex={collapsed ? 0 : undefined}
      onKeyDown={(event) => {
        if (collapsed && (event.key === 'Enter' || event.key === ' ')) {
          setExpanded(true);
        }
      }}
    >
      <MarkdownText review={activeReview} text={activeReview ? body : visible} />
      {collapsed && <span className="read-more">Show more</span>}
    </div>
  );
}

function MarkdownText({ text, review }: { text: string; review?: MarkdownReviewOptions }) {
  if (review) {
    return <>{splitAiPlanReviewBlocks(text).map((block) => <MarkdownReviewBlock block={block} key={block.anchorKey} review={review} />)}</>;
  }

  const lines = text.split(/\r?\n/);
  const blocks: React.ReactNode[] = [];
  let list: string[] = [];

  const flushList = () => {
    if (list.length === 0) return;
    blocks.push(<ul key={`list-${blocks.length}`}>{list.map((item, index) => <li key={index}>{renderInlineMarkdown(item)}</li>)}</ul>);
    list = [];
  };

  for (const line of lines) {
    const bullet = line.match(/^\s*[-*]\s+(.+)$/);
    if (bullet) {
      list.push(bullet[1]);
      continue;
    }

    flushList();
    if (!line.trim()) {
      continue;
    }

    blocks.push(<p key={`p-${blocks.length}`}>{renderInlineMarkdown(line)}</p>);
  }

  flushList();
  return <>{blocks}</>;
}

function MarkdownReviewBlock({ block, review }: { block: AiPlanReviewBlock; review: MarkdownReviewOptions }) {
  const comments = review.commentsByAnchor[block.anchorKey] ?? [];
  const unresolvedCount = comments.filter((entry) => entry.status.toLowerCase() !== 'resolved').length;
  const active = block.anchorKey === review.activeAnchorKey;

  return (
    <section className={`markdown-review-block ${block.kind} ${active ? 'active' : ''}`}>
      <div className="markdown-review-line" role="button" tabIndex={0} onClick={() => review.onSelectAnchor(block.anchorKey)} onKeyDown={(event) => {
        if (event.key === 'Enter' || event.key === ' ') {
          event.preventDefault();
          review.onSelectAnchor(block.anchorKey);
        }
      }}>
        <MarkdownReviewBlockText block={block} />
        <span className={comments.length > 0 ? 'markdown-review-marker has-comments' : 'markdown-review-marker'} aria-hidden="true">
          {comments.length > 0 ? unresolvedCount || comments.length : '+'}
        </span>
      </div>
      {active && (
        <div className="markdown-review-composer">
          <textarea value={review.commentDraft} onChange={(event) => review.onDraftChange(event.target.value)} placeholder="Add a plan review comment..." />
          <div>
            <button className="primary-action" disabled={!review.commentDraft.trim() || review.busy} onClick={() => review.onSubmitComment(block)}>Add comment</button>
            <button className="secondary" disabled={review.busy} onClick={() => review.onSelectAnchor(null)} type="button">Cancel</button>
          </div>
        </div>
      )}
      {comments.map((entry) => (
        <article className={entry.status.toLowerCase() === 'resolved' ? 'markdown-review-thread resolved' : 'markdown-review-thread'} key={entry.id}>
          <header><strong>{entry.author}</strong><span>{entry.status} · {relativeTime(entry.createdAt)}</span></header>
          {review.editingCommentId === entry.id ? (
            <div className="plan-review-edit">
              <textarea value={review.editingBody} onChange={(event) => review.onEditingBodyChange(event.target.value)} />
              <button className="primary-action" disabled={!review.editingBody.trim() || review.busy} onClick={() => review.onSaveEdit(entry.id)}>Save</button>
              <button className="secondary" disabled={review.busy} onClick={review.onCancelEdit}>Cancel</button>
            </div>
          ) : (
            <p>{entry.body}</p>
          )}
          <footer>
            <button className="link-button" disabled={review.busy} type="button" onClick={() => review.onStartEdit(entry)}>Edit</button>
            <button className="link-button" disabled={review.busy} type="button" onClick={() => review.onToggleResolved(entry)}>
              {entry.status.toLowerCase() === 'resolved' ? 'Reopen' : 'Resolve'}
            </button>
            <button className="link-button danger-link" disabled={review.busy} type="button" onClick={() => review.onDeleteComment(entry.id)}>Delete</button>
          </footer>
        </article>
      ))}
    </section>
  );
}

function MarkdownReviewBlockText({ block }: { block: AiPlanReviewBlock }) {
  if (block.kind === 'heading') {
    return <h3>{renderInlineMarkdown(block.text)}</h3>;
  }

  if (block.kind === 'list-item') {
    const numbered = block.text.match(/^(\d+[.)])\s+(.+)$/);
    const marker = numbered ? numbered[1] : '-';
    const body = numbered ? numbered[2] : block.text;
    return (
      <div className="markdown-review-list-item">
        <span>{marker}</span>
        <p>{renderInlineMarkdown(body)}</p>
      </div>
    );
  }

  return <p>{renderInlineMarkdown(block.text)}</p>;
}

function renderInlineMarkdown(text: string): React.ReactNode[] {
  const nodes: React.ReactNode[] = [];
  const pattern = /(\*\*[^*]+\*\*|`[^`]+`|\[[^\]]+\]\([^)]+\))/g;
  let last = 0;
  for (const match of text.matchAll(pattern)) {
    if (match.index > last) {
      nodes.push(text.slice(last, match.index));
    }

    const token = match[0];
    if (token.startsWith('**')) {
      nodes.push(<strong key={nodes.length}>{token.slice(2, -2)}</strong>);
    } else if (token.startsWith('`')) {
      nodes.push(<code key={nodes.length}>{token.slice(1, -1)}</code>);
    } else {
      const link = token.match(/^\[([^\]]+)\]\(([^)]+)\)$/);
      const href = link ? safeMarkdownHref(link[2]) : null;
      nodes.push(link && href ? <a key={nodes.length} href={href} target="_blank" rel="noreferrer">{link[1]}</a> : token);
    }

    last = match.index + token.length;
  }

  if (last < text.length) {
    nodes.push(text.slice(last));
  }

  return nodes;
}

function PanelHeader({ icon, title }: { icon: React.ReactNode; title: string }) {
  return <header className="panel-head">{icon}<h2>{title}</h2></header>;
}

function SectionTitle({ icon, title, amber }: { icon: React.ReactNode; title: string; amber?: boolean }) {
  return <h2 className={amber ? 'section-title amber' : 'section-title'}>{icon}{title}</h2>;
}

function EmptyState({ children }: { children: React.ReactNode }) {
  return <p className="empty-state">{children}</p>;
}

function filterBoard(board: Board, query: string): Board {
  const normalized = query.trim().toLowerCase();
  if (!normalized) return board;
  return {
    ...board,
    columns: board.columns.map((column) => ({
      ...column,
      items: column.items.filter((item) =>
        [item.key, item.title, item.type, item.status, item.assignee ?? '', item.priority]
          .some((value) => value.toLowerCase().includes(normalized)))
    }))
  };
}

function resolveActiveAiProvider(settings: SettingsDto, selectedProvider: string | null): AiProviderSettingsDto {
  const providers = settings.ai.availableProviders.length > 0
    ? settings.ai.availableProviders
    : [{ provider: settings.ai.provider, displayName: settings.ai.provider, status: 'Ready', endpoint: settings.ai.endpoint, activeModel: settings.ai.activeModel, availableModels: settings.ai.availableModels }];
  return providers.find((provider) => provider.provider === selectedProvider && provider.status !== 'Unavailable')
    ?? providers.find((provider) => provider.provider === settings.ai.provider && provider.status !== 'Unavailable')
    ?? providers.find((provider) => provider.status !== 'Unavailable')
    ?? providers[0];
}

function resolveActiveAiModel(settings: SettingsDto, selectedProvider: string | null, selectedModel: string | null) {
  const provider = resolveActiveAiProvider(settings, selectedProvider);
  const options = provider.availableModels.length > 0 ? provider.availableModels : [provider.activeModel];
  return selectedModel && options.includes(selectedModel) ? selectedModel : provider.activeModel;
}

function resolveActiveAiReasoning(settings: SettingsDto | null, selectedProvider: string | null, selectedReasoning: string | null) {
  if (!settings) return selectedReasoning ?? 'high';
  const provider = resolveActiveAiProvider(settings, selectedProvider);
  const options = provider.availableReasoningEfforts ?? [];
  if (options.length === 0) return null;
  return selectedReasoning && options.includes(selectedReasoning)
    ? selectedReasoning
    : provider.defaultReasoningEffort ?? options[0];
}

function isPublicGitUrl(value: string) {
  const normalized = value.trim();
  return /^https:\/\/.+/i.test(normalized) || /^http:\/\/.+/i.test(normalized);
}

function planTitle(run: AiRun): string {
  const firstLine = (run.plan ?? '')
    .split(/\r?\n/)
    .map((line) => line.replace(/^#+\s*/, '').replace(/\*\*/g, '').trim())
    .find(Boolean);
  if (!firstLine) return 'Untitled plan';
  return firstLine.length > 72 ? `${firstLine.slice(0, 69).trimEnd()}...` : firstLine;
}

function findReferencedPlan(comment: CommentDto, aiRuns: AiRun[]): AiRun | undefined {
  if (!shouldRenderPlanReferenceActivity(comment)) {
    return undefined;
  }

  const sequence = comment.body.match(/plan\s+#(\d+)/i);
  if (sequence) {
    const match = aiRuns.find((run) => run.sequenceNumber === Number(sequence[1]));
    if (match) return match;
  }

  if (comment.kind === 'Plan') {
    return aiRuns.find((run) => run.plan?.trim() === comment.body.trim());
  }

  return undefined;
}

function resolveDropTarget(board: Board, overId: string): { status: string; sortOrder: number } | null {
  if (overId.startsWith('column:')) {
    const status = overId.slice('column:'.length);
    const column = board.columns.find((entry) => entry.name === status);
    return column ? { status, sortOrder: column.items.length } : null;
  }

  const column = board.columns.find((entry) => entry.items.some((item) => item.id === overId));
  if (!column) return null;
  return { status: column.name, sortOrder: column.items.findIndex((item) => item.id === overId) };
}

const stableCollisionDetection: CollisionDetection = (args) => {
  const pointerHits = pointerWithin(args);
  return pointerHits.length > 0 ? pointerHits : rectIntersection(args);
};

function statusClass(value: string) {
  const normalized = value.toLowerCase();
  if (['running', 'started', 'completed', 'approved', 'created'].some((token) => normalized.includes(token))) return 'state-good';
  if (['failed', 'cleanupfailed', 'applyfailed'].some((token) => normalized.includes(token))) return 'state-bad';
  if (['stopped', 'deleted', 'discarded'].some((token) => normalized.includes(token))) return 'state-muted';
  return 'state-warn';
}

function timelineBucket(kind: string) {
  const normalized = kind.toLowerCase();
  if (normalized.includes('preview')) return 'Previews';
  if (normalized.includes('pipeline')) return 'Pipelines';
  if (normalized.includes('cleanup') || normalized.includes('pullrequest') || normalized.includes('implementation') || normalized.includes('commit') || normalized.includes('branch')) return 'Git';
  return 'Cards';
}

function timelineClass(kind: string) {
  const normalized = kind.toLowerCase();
  if (normalized.includes('cleanup')) return 'timeline-cleanup';
  if (normalized.includes('pullrequest') || normalized.includes('implementation') || normalized.includes('commit') || normalized.includes('branch')) return 'timeline-git';
  if (normalized.includes('preview')) return 'timeline-preview';
  if (normalized.includes('pipeline')) return 'timeline-pipeline';
  return 'timeline-card';
}

function timelineIcon(kind: string) {
  const normalized = kind.toLowerCase();
  if (normalized.includes('cleanup')) return <Trash2 size={15} />;
  if (normalized.includes('pullrequest') || normalized.includes('implementation') || normalized.includes('commit') || normalized.includes('branch')) return <GitPullRequest size={15} />;
  if (normalized.includes('preview')) return <ExternalLink size={15} />;
  if (normalized.includes('pipeline')) return <Activity size={15} />;
  return <PanelLeft size={15} />;
}

function timelineFlowClass(lane: TimelineLane, kind: string) {
  const status = statusClass(kind).replace('state-', '');
  return `lane-${lane.toLowerCase().replaceAll(' ', '-')} flow-${status}`;
}

function timelineFlowIcon(lane: TimelineLane, kind: string) {
  if (statusClass(kind) === 'state-bad') return <X size={14} />;
  switch (lane) {
    case 'Implementation PR': return <GitPullRequest size={14} />;
    case 'Cleanup': return <Trash2 size={14} />;
    case 'Preview': return <ExternalLink size={14} />;
    case 'Pipeline': return <Activity size={14} />;
    default: return <PanelLeft size={14} />;
  }
}

function moveCardInBoard(board: Board, id: string, status: string, sortOrder: number): Board {
  const moving = board.columns.flatMap((column) => column.items).find((item) => item.id === id);
  if (!moving) return board;
  return {
    ...board,
    columns: board.columns.map((column) => {
      const withoutMoving = column.items.filter((item) => item.id !== id);
      if (column.name !== status) return { ...column, items: withoutMoving };
      const next = [...withoutMoving];
      next.splice(Math.min(Math.max(sortOrder, 0), next.length), 0, { ...moving, status, sortOrder });
      return { ...column, items: next.map((item, index) => ({ ...item, sortOrder: index })) };
    })
  };
}

function allBoardItems(board: Board | null): WorkItemSummary[] {
  return board?.columns.flatMap((column) => column.items) ?? [];
}

function compareWorkItemsForHierarchy(left: WorkItemSummary, right: WorkItemSummary) {
  const leftPath = left.hierarchyPath ?? '';
  const rightPath = right.hierarchyPath ?? '';
  if (leftPath && rightPath && leftPath !== rightPath) return leftPath.localeCompare(rightPath);
  return left.sortOrder - right.sortOrder || left.key.localeCompare(right.key);
}

function availableParentOptions(board: Board | null, currentId: string | null, type: string): WorkItemSummary[] {
  if (type === 'Epic') return [];
  const items = allBoardItems(board);
  const itemById = new Map(items.map((item) => [item.id, item]));
  const isDescendantOfCurrent = (candidate: WorkItemSummary) => {
    if (!currentId) return false;
    let parentId = candidate.parentWorkItemId ?? null;
    const seen = new Set<string>();
    while (parentId) {
      if (parentId === currentId) return true;
      if (seen.has(parentId)) return false;
      seen.add(parentId);
      parentId = itemById.get(parentId)?.parentWorkItemId ?? null;
    }
    return false;
  };
  return items
    .filter((item) => item.id !== currentId)
    .filter((item) => type === 'Feature' ? item.type === 'Epic' : item.type === 'Epic' || item.type === 'Feature')
    .filter((item) => !isDescendantOfCurrent(item))
    .sort(compareWorkItemsForHierarchy);
}

function hierarchySummary(item: WorkItemSummary) {
  const childCount = item.childCount ?? 0;
  if (childCount > 0) {
    return `${item.doneChildCount ?? 0}/${childCount} children done`;
  }
  if (item.parentKey) return `Child of ${item.parentKey}`;
  if (item.type === 'Epic') return 'Epic root';
  return 'Standalone card';
}

function workItemFormsEqual(left: WorkItemForm, right: WorkItemForm) {
  return left.title === right.title &&
    left.description === right.description &&
    left.type === right.type &&
    left.status === right.status &&
    left.priority === right.priority &&
    left.assignee === right.assignee &&
    left.parentWorkItemId === right.parentWorkItemId &&
    left.isBug === right.isBug;
}

function formFromDetail(detail: WorkItemDetail): WorkItemForm {
  return {
    title: detail.item.title,
    description: detail.description,
    type: detail.item.type,
    status: detail.item.status,
    priority: detail.item.priority,
    assignee: detail.item.assignee ?? '',
    parentWorkItemId: detail.item.parentWorkItemId ?? '',
    isBug: detail.item.isBug ?? false
  };
}

function buildAssigneeOptions(board: Board | null, assignees: AssigneeDto[], auth: AuthState): AssigneeOption[] {
  const options = new Map<string, AssigneeOption>();
  options.set('', { value: '', label: 'Unassigned' });

  for (const assignee of assignees) {
    options.set(assignee.displayName, {
      value: assignee.displayName,
      label: assignee.displayName,
      hint: assignee.source === 'Authentik' ? assignee.email : assignee.source
    });
  }

  if (auth.status === 'ready') {
    options.set(auth.userName, { value: auth.userName, label: auth.userName, hint: 'Signed in' });
    if (auth.userEmail && auth.userEmail !== auth.userName) {
      options.set(auth.userEmail, { value: auth.userEmail, label: auth.userEmail, hint: 'Email' });
    }
  } else {
    options.set('Christopher Rosenvall', {
      value: 'Christopher Rosenvall',
      label: 'Christopher Rosenvall',
      hint: 'Local dev'
    });
  }

  for (const item of board?.columns.flatMap((column) => column.items) ?? []) {
    const assignee = item.assignee?.trim();
    if (assignee && !options.has(assignee)) {
      options.set(assignee, { value: assignee, label: assignee });
    }
  }

  return [...options.values()];
}

function compactNumber(value: number) {
  return new Intl.NumberFormat('en', { notation: 'compact', maximumFractionDigits: 1 }).format(value);
}

function preferredAssignee(options: AssigneeOption[]) {
  return options.find((option) => option.value && (option.hint === 'Signed in' || option.hint === 'Local dev' || option.hint?.includes('@')))?.value
    ?? options.find((option) => option.value)?.value
    ?? '';
}

function repositoryNameFromRemote(remoteUrl: string, boardName: string) {
  const normalized = remoteUrl.trim().replace(/\.git$/i, '');
  const parts = normalized.split(/[/:]/).filter(Boolean);
  const name = parts.at(-1);
  return name || boardName.trim() || 'repository';
}

function repositoryOwnerFromRemote(remoteUrl: string) {
  const normalized = remoteUrl.trim().replace(/\.git$/i, '');
  const parts = normalized.split(/[/:]/).filter(Boolean);
  return parts.length >= 2 ? parts.at(-2) ?? null : null;
}

function webUrlFromRemote(remoteUrl: string) {
  const normalized = remoteUrl.trim().replace(/\.git$/i, '');
  if (normalized.startsWith('http://') || normalized.startsWith('https://')) {
    return normalized;
  }

  const sshMatch = normalized.match(/^ssh:\/\/git@([^/]+)\/(.+)$/i) ?? normalized.match(/^git@([^:]+):(.+)$/i);
  return sshMatch ? `https://${sshMatch[1]}/${sshMatch[2]}` : null;
}

function repositoryLabel(repository: RepositoryDto) {
  return repository.owner ? `${repository.owner}/${repository.name}` : repository.name;
}

function repositoryKey(repository: RepositoryDto) {
  return `${repository.owner ?? ''}/${repository.name}/${repository.remoteUrl}`;
}

function uniqueRepositories(repositories: RepositoryDto[]) {
  const seen = new Set<string>();
  return repositories.filter((repository) => {
    const key = `${repository.owner ?? ''}/${repository.name}`.toLowerCase();
    if (seen.has(key)) return false;
    seen.add(key);
    return true;
  }).sort((left, right) => repositoryLabel(left).localeCompare(repositoryLabel(right)));
}

function slugFromText(value: string) {
  const slug = value.toLowerCase().replace(/[^a-z0-9._-]+/g, '-').replace(/^-+|-+$/g, '').replace(/--+/g, '-');
  return slug || 'new-board';
}

function normalizeRepositoryNameInput(value: string) {
  const normalized = value
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9._-]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .replace(/--+/g, '-');
  return normalized || 'new-repository';
}

function looksLikeWebsiteText(value: string) {
  return /website|hemsida|frontend|react|vite|webapp|landing|site|clock|klock/i.test(value);
}

function boardRepositorySummary(board: Board) {
  const repositories = board.repositories?.length
    ? board.repositories
    : board.repository ? [{ repository: board.repository, implementationProfile: board.repository.implementationProfile, isPrimary: true }] : [];
  const primary = repositories.find((entry) => entry.isPrimary) ?? repositories[0];
  if (!primary) return 'No repository linked';
  const extraCount = repositories.length > 1 ? ` + ${repositories.length - 1} linked` : '';
  return `${primary.repository.provider} / ${repositoryLabel(primary.repository)} - ${primary.repository.defaultBranch} - ${profileLabel(primary.implementationProfile)}${extraCount}`;
}

function linesFromTextarea(value: string) {
  return value
    .split(/\r?\n|,/)
    .map((line) => line.trim())
    .filter(Boolean);
}

function repositoryProfileFromForm(form: CreateBoardForm, base?: RepositoryProfileDto | null): RepositoryProfileDto {
  const implementationProfile = normalizeImplementationProfile(form.implementationProfile);
  return {
    implementationProfile,
    displayName: base?.displayName ?? profileLabel(implementationProfile),
    confidence: base?.confidence ?? 0.9,
    enabledSkills: linesFromTextarea(form.enabledSkills),
    instructions: form.aiInstructions,
    signals: base?.signals ?? [],
    source: base?.source ?? 'user',
    capabilityTags: linesFromTextarea(form.capabilityTags),
    skillDrafts: form.skillDrafts.map((draft) => ({
      name: draft.name.trim(),
      description: draft.description.trim(),
      content: draft.content.trim(),
      enabled: draft.enabled
    })).filter((draft) => draft.name.length > 0),
    analyzerModel: base?.analyzerModel ?? null,
    analyzedAt: base?.analyzedAt ?? null
  };
}

function createBoardFormFromRepositoryProfile(board: Board, entry: BoardRepositoryDto): CreateBoardForm {
  const profile = entry.profile;
  const gitOps = board.gitOpsSettings ?? defaultGitOpsSettings(board.id);
  const ai = board.aiContext ?? defaultAiContext(board.id);
  return {
    ...emptyBoardForm,
    name: board.name,
    repositoryProvider: entry.repository.provider,
    providerMode: entry.repository.provider === 'GitHub' ? 'GitHub' : 'CustomUrl',
    repositoryId: entry.repository.id,
    repositoryOwner: entry.repository.owner ?? '',
    repositoryRemoteUrl: entry.repository.remoteUrl,
    repositoryWebUrl: entry.repository.webUrl ?? '',
    repositoryDefaultBranch: entry.repository.defaultBranch,
    implementationProfile: normalizeImplementationProfile(profile?.implementationProfile ?? entry.implementationProfile),
    implementationWorkflow: workflowForRepositoryProfile(profile?.implementationProfile ?? entry.implementationProfile, entry.implementationWorkflow ?? entry.repository.implementationWorkflow),
    publicHostname: board.publicHostname ?? '',
    gitOpsAllowedPaths: gitOps.allowedPaths.join('\n'),
    argoNamespace: gitOps.argoNamespace,
    argoApplicationSelector: gitOps.argoApplicationSelector,
    aiInstructions: profile?.instructions ?? ai.instructions,
    agentInstructions: ai.agentInstructions ?? '',
    enabledSkills: (profile?.enabledSkills ?? ai.enabledSkills).join('\n'),
    capabilityTags: (profile?.capabilityTags ?? []).join('\n'),
    skillDrafts: profile?.skillDrafts ?? [],
    askWhenUncertain: ai.askWhenUncertain
  };
}

function defaultGitOpsSettings(boardId: string): BoardGitOpsSettingsDto {
  return {
    boardId,
    allowedPaths: ['apps/', 'clusters/', 'infrastructure/', 'kubernetes/', 'tofu/'],
    argoNamespace: 'argocd',
    argoApplicationSelector: ''
  };
}

function defaultAiContext(boardId: string): BoardAiContextDto {
  return {
    boardId,
    instructions: '',
    enabledSkills: [],
    askWhenUncertain: true,
    agentInstructions: ''
  };
}

function boardSettingsSubtitle(scope: 'board' | 'ai' | 'environment'): string {
  if (scope === 'ai') return 'Board AI instructions, enabled skills, repository profiles and skill drafts.';
  if (scope === 'environment') return 'Board-owned environment variables and Kubernetes secret metadata.';
  return 'Board repository linkage, hosting workflow and lifecycle actions.';
}

function isGitOpsBoard(board: Board): boolean {
  if (board.gitOpsSettings) return true;
  const profiles = [
    board.repository?.implementationProfile,
    ...(board.repositories ?? []).flatMap((entry) => [entry.implementationProfile, entry.repository.implementationProfile])
  ];
  return profiles.some((profile) => (profile ?? '').toLowerCase().includes('gitops'));
}

function statusBadgeClass(status: string) {
  const normalized = status.toLowerCase();
  if (normalized === 'synced' || normalized === 'healthy') return 'state-good';
  if (normalized === 'progressing' || normalized === 'outofsync') return 'state-warn';
  if (normalized === 'degraded' || normalized === 'missing' || normalized === 'unknown') return 'state-bad';
  return 'state-muted';
}

function profileLabel(profile?: string | null) {
  if (profile === 'react-preview') return 'React preview';
  if (profile === 'code-repo') return 'Code repo';
  if (profile === 'gitops-homelab') return 'GitOps homelab';
  if (profile === 'unity') return 'Unity';
  return profile || 'React preview';
}

function normalizeImplementationProfile(profile?: string | null): CreateBoardForm['implementationProfile'] {
  return profile === 'react-preview' || profile === 'code-repo' || profile === 'gitops-homelab' || profile === 'unity'
    ? profile
    : 'code-repo';
}

function displayUserName(userId: string, me: UserDto) {
  return userId === me.id ? `${me.displayName} (you)` : userId.slice(0, 8);
}

function latestImplementationRun(runs?: ImplementationRunDto[] | null) {
  return [...(runs ?? [])].sort((left, right) => right.createdAt.localeCompare(left.createdAt))[0];
}

function workflowLabel(workflow?: string | null) {
  if (workflow === 'preview-only') return 'Preview only';
  if (workflow === 'preview-then-pr') return 'Preview, then PR';
  if (workflow === 'direct-pr') return 'Direct PR';
  return workflow || 'Default workflow';
}

function latestRepositoryCleanupRun(runs?: RepositoryCleanupRunDto[] | null) {
  return [...(runs ?? [])].sort((left, right) => right.createdAt.localeCompare(left.createdAt))[0];
}

function extractUnifiedDiffForFile(diff: string, path: string): string {
  if (!diff.trim()) return '';
  const escapedPath = path.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const pattern = new RegExp(`(^diff --git a\\/.* b\\/${escapedPath}[\\s\\S]*?)(?=^diff --git |(?![\\s\\S]))`, 'm');
  const match = diff.match(pattern);
  if (match?.[1]) return match[1].trimEnd();

  const lines = diff.split(/\r?\n/);
  const start = lines.findIndex((line) => line === `+++ b/${path}` || line.endsWith(` b/${path}`));
  if (start < 0) return '';
  let header = start;
  while (header > 0 && !lines[header].startsWith('diff --git ')) header -= 1;
  let end = start + 1;
  while (end < lines.length && !lines[end].startsWith('diff --git ')) end += 1;
  return lines.slice(header, end).join('\n').trimEnd();
}

function initials(value: string) {
  return value.split(' ').map((part) => part[0]).join('').slice(0, 2).toUpperCase();
}

function relativeTime(value: string) {
  const diff = Date.now() - new Date(value).getTime();
  const minutes = Math.max(0, Math.round(diff / 60000));
  if (minutes < 1) return 'Just now';
  if (minutes < 60) return `${minutes}m ago`;
  return `${Math.round(minutes / 60)}h ago`;
}

function relativeDays(value: string) {
  const diff = new Date(value).getTime() - Date.now();
  return `${Math.max(0, Math.ceil(diff / 86400000))}d`;
}

function terminalTime(value: string) {
  return new Intl.DateTimeFormat(undefined, {
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit'
  }).format(new Date(value));
}

export default App;
