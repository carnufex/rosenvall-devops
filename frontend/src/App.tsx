import React from 'react';
import { User, UserManager, WebStorageStateStore } from 'oidc-client-ts';
import {
  Activity,
  Bot,
  Boxes,
  CheckCircle2,
  ExternalLink,
  GitPullRequest,
  Github,
  History,
  LayoutDashboard,
  Maximize2,
  PanelLeft,
  Play,
  Plus,
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

type View = 'dashboard' | 'board' | 'timeline' | 'settings';

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
};

type RepositoryDto = {
  id: string;
  provider: string;
  name: string;
  remoteUrl: string;
  webUrl?: string | null;
  defaultBranch: string;
  createdAt: string;
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
};

type WorkItemDetail = {
  item: WorkItemSummary;
  description: string;
  comments: CommentDto[];
  preview?: PreviewDto | null;
  development?: DevelopmentDto | null;
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
};

type PreviewTerminalLineDto = {
  createdAt: string;
  stream: string;
  message: string;
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
};

type SettingsDto = {
  gitHub: {
    account: string;
    targetRepository: string;
    branchWatchPatterns: string;
    connected: boolean;
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
};

type WorkItemForm = {
  title: string;
  description: string;
  type: string;
  status: string;
  priority: string;
  assignee: string;
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
  repositoryProvider: string;
  repositoryRemoteUrl: string;
  repositoryDefaultBranch: string;
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
  assignee: ''
};

const emptyBoardForm: CreateBoardForm = {
  name: '',
  repositoryProvider: 'Forgejo',
  repositoryRemoteUrl: '',
  repositoryDefaultBranch: 'main'
};

const selectedBoardStorageKey = 'rosenvall-devops:selected-board';
const selectedAiProviderStorageKey = 'rosenvall-devops:selected-ai-provider';
const selectedAiModelStorageKey = 'rosenvall-devops:selected-ai-model';

const api = {
  async get<T>(path: string): Promise<T> {
    const response = await fetch(path, { headers: authHeaders() });
    return parseResponse<T>(response);
  },
  async post<T>(path: string, body: unknown): Promise<T> {
    const response = await fetch(path, {
      method: 'POST',
      headers: authHeaders({ 'Content-Type': 'application/json' }),
      body: JSON.stringify(body)
    });
    return parseResponse<T>(response);
  },
  async patch<T>(path: string, body: unknown): Promise<T> {
    const response = await fetch(path, {
      method: 'PATCH',
      headers: authHeaders({ 'Content-Type': 'application/json' }),
      body: JSON.stringify(body)
    });
    return parseResponse<T>(response);
  },
  async delete(path: string): Promise<void> {
    const response = await fetch(path, { method: 'DELETE', headers: authHeaders() });
    if (!response.ok) throw new Error(`${response.status} ${response.statusText}`);
  }
};

let accessTokenProvider: () => string | null = () => null;

function setAccessTokenProvider(provider: () => string | null) {
  accessTokenProvider = provider;
}

function authHeaders(base?: HeadersInit): HeadersInit {
  const token = accessTokenProvider();
  return token ? { ...base, Authorization: `Bearer ${token}` } : { ...base };
}

async function parseResponse<T>(response: Response): Promise<T> {
  if (!response.ok) {
    let message = `${response.status} ${response.statusText}`;
    try {
      const payload = await response.json() as { detail?: string; title?: string };
      message = payload.detail || payload.title || message;
    } catch {
      // Keep status text when the server did not return JSON problem details.
    }
    throw new Error(message);
  }
  if (response.status === 204) return undefined as T;
  return response.json();
}

function App() {
  const auth = useAuth();
  const [view, setView] = useStateFromHash();
  const [shell, setShell] = React.useState<ShellState>({ status: 'loading' });
  const [selected, setSelected] = React.useState<SelectedState>({ status: 'closed' });
  const [createStatus, setCreateStatus] = React.useState<string | null>(null);
  const [createBoardOpen, setCreateBoardOpen] = React.useState(false);
  const [query, setQuery] = React.useState('');
  const [toasts, setToasts] = React.useState<ToastMessage[]>([]);
  const [busyAction, setBusyAction] = React.useState<string | null>(null);
  const [selectedAiProvider, setSelectedAiProviderState] = React.useState<string | null>(() => window.localStorage.getItem(selectedAiProviderStorageKey));
  const [selectedAiModel, setSelectedAiModelState] = React.useState<string | null>(() => window.localStorage.getItem(selectedAiModelStorageKey));
  const setSelectedAiProvider = React.useCallback((provider: string) => {
    setSelectedAiProviderState(provider);
    setSelectedAiModelState(null);
    window.localStorage.setItem(selectedAiProviderStorageKey, provider);
    window.localStorage.removeItem(selectedAiModelStorageKey);
  }, []);
  const setSelectedAiModel = React.useCallback((model: string) => {
    setSelectedAiModelState(model);
    window.localStorage.setItem(selectedAiModelStorageKey, model);
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
    setAccessTokenProvider(() => auth.status === 'ready' ? auth.accessToken : null);
  }, [auth]);

  const addToast = React.useCallback((kind: ToastMessage['kind'], message: string) => {
    const id = crypto.randomUUID();
    setToasts((current) => [{ id, kind, message }, ...current].slice(0, 3));
    window.setTimeout(() => {
      setToasts((current) => current.filter((toast) => toast.id !== id));
    }, kind === 'success' ? 5000 : 30000);
  }, []);

  const loadShell = React.useCallback(async (preferredBoardId?: string | null) => {
    if (auth.status === 'checking') return;
    setShell((current) => current.status === 'ready' ? { ...current, busy: true } : { status: 'loading' });
    try {
      const workspaces = await api.get<Workspace[]>('/api/workspaces');
      const workspace = workspaces[0];
      if (!workspace) throw new Error('No workspace returned by API');
      const boards = await api.get<Board[]>(`/api/workspaces/${workspace.id}/boards`);
      const board = boards.find((entry) => entry.id === (preferredBoardId ?? selectedBoardIdRef.current)) ?? boards[0];
      if (!board) throw new Error('No board returned by API');
      const [repositories, settings, previews, events, pipelines, timeline, metrics, assignees] = await Promise.all([
        api.get<RepositoryDto[]>('/api/repositories'),
        api.get<SettingsDto>('/api/settings'),
        api.get<PreviewEnvironmentDto[]>('/api/preview-environments'),
        api.get<PreviewEventDto[]>('/api/preview-events'),
        api.get<PipelineStatusDto[]>('/api/pipelines'),
        api.get<TimelineEventDto[]>(`/api/boards/${board.id}/timeline`),
        api.get<MetricsDto>(`/api/metrics?boardId=${board.id}`),
        api.get<AssigneeDto[]>(`/api/assignees?boardId=${board.id}`)
      ]);
      setSelectedBoardId(board.id);
      setShell({ status: 'ready', workspace, boards, board, repositories, settings, previews, events, pipelines, timeline, metrics, assignees, busy: false });
    } catch (loadError) {
      setShell({ status: 'error', message: loadError instanceof Error ? loadError.message : 'Failed to load API data' });
    }
  }, [auth.status, setSelectedBoardId]);

  const loadWorkItem = React.useCallback(async (id: string) => {
    setSelected((current) => current.status === 'open' && current.detail.item.id === id ? { ...current, busy: true } : { status: 'loading', id });
    try {
      const [detail, runs] = await Promise.all([
        api.get<WorkItemDetail>(`/api/work-items/${id}`),
        api.get<AiRun[]>(`/api/work-items/${id}/ai-runs`)
      ]);
      setSelected({ status: 'open', detail, aiRuns: runs, busy: false });
    } catch (loadError) {
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

  const previewPollWorkItemId = selected.status === 'open' && isPreviewPendingStatus(selected.detail.preview?.status)
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
        await loadShell();
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

  const runAction = React.useCallback(async (label: string, action: () => Promise<void>) => {
    setBusyAction(label);
    try {
      await action();
      addToast('success', `${label} completed.`);
    } catch (actionError) {
      addToast('error', actionError instanceof Error ? actionError.message : `${label} failed`);
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
      await runAction('Creating board', async () => {
        if (shell.status !== 'ready') return;
        const created = await api.post<Board>(`/api/workspaces/${shell.workspace.id}/boards`, {
          name: form.name,
          repositoryId: null,
          repositoryProvider: form.repositoryProvider,
          repositoryName: repositoryNameFromRemote(form.repositoryRemoteUrl, form.name),
          repositoryRemoteUrl: form.repositoryRemoteUrl,
          repositoryWebUrl: webUrlFromRemote(form.repositoryRemoteUrl),
          repositoryDefaultBranch: form.repositoryDefaultBranch || 'main'
        });
        setCreateBoardOpen(false);
        setSelectedBoardId(created.id);
        await loadShell(created.id);
      });
    },
    executePipeline: async (pipelineRunId) => {
      await runAction('Starting pipeline', async () => {
        await api.post<PipelineStatusDto>(`/api/pipeline-runs/${pipelineRunId}/execute`, { actor });
        await refreshAfterChange();
      });
    },
    createCard: async (form) => {
      await runAction('Creating card', async () => {
        if (shell.status !== 'ready') return;
        const created = await api.post<WorkItemSummary>('/api/work-items', {
          boardId: shell.board.id,
          type: form.type,
          title: form.title,
          description: form.description,
          status: form.status,
          priority: form.priority,
          assignee: form.assignee || null
        });
        setCreateStatus(null);
        await refreshAfterChange(created.id);
      });
    },
    updateCard: async (id, form) => {
      await runAction('Saving card', async () => {
        await api.patch<WorkItemSummary>(`/api/work-items/${id}`, {
          title: form.title,
          description: form.description,
          type: form.type,
          status: form.status,
          priority: form.priority,
          assignee: form.assignee || null
        });
        await refreshAfterChange(id);
      });
    },
    deleteCard: async (id) => {
      await runAction('Deleting card and cleaning preview', async () => {
        await api.delete(`/api/work-items/${id}`);
        setSelected({ status: 'closed' });
        await refreshAfterChange();
      });
    },
    startAiPlan: async (id) => {
      await runAction('Generating AI plan', async () => {
        if (shell.status !== 'ready') return;
        const provider = resolveActiveAiProvider(shell.settings, selectedAiProvider);
        await api.post<AiRun>(`/api/work-items/${id}/ai-plan`, {
          provider: provider.provider,
          model: resolveActiveAiModel(shell.settings, selectedAiProvider, selectedAiModel)
        });
        await refreshAfterChange(id);
      });
    },
    approvePlan: async (runId, workItemId) => {
      setBusyAction('Starting preview implementation');
      try {
        await api.post<WorkItemDetail>(`/api/ai-runs/${runId}/approve`, { approvedBy: actor });
        await refreshAfterChange(workItemId);
        addToast('info', 'Preview implementation started. Follow the terminal log in the preview panel.');
      } catch (approveError) {
        await refreshAfterChange(workItemId);
        addToast('error', approveError instanceof Error ? approveError.message : 'Preview implementation failed to start');
      } finally {
        setBusyAction(null);
      }
    },
    discardPlan: async (runId, workItemId) => {
      await runAction('Discarding plan', async () => {
        await api.post<AiRun>(`/api/ai-runs/${runId}/discard`, { discardedBy: actor });
        await refreshAfterChange(workItemId);
      });
    },
    approvePullRequest: async (workItemId) => {
      await runAction('Approving PR and stopping preview', async () => {
        await api.post<WorkItemDetail>(`/api/work-items/${workItemId}/approve-pr`, { approvedBy: actor });
        await refreshAfterChange(workItemId);
      });
    },
    startPreview: async (workItemId) => {
      await runAction('Starting preview', async () => {
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
      await runAction('Stopping preview', async () => {
        await api.post<WorkItemDetail>(`/api/work-items/${workItemId}/preview/stop`, { actor });
        await refreshAfterChange(workItemId);
      });
    },
    addComment: async (id, body) => {
      await runAction('Posting comment', async () => {
        await api.post<CommentDto>(`/api/work-items/${id}/comments`, { author: actor, kind: 'Comment', body });
        await refreshAfterChange(id);
      });
    },
    addCommentAndAskAi: async (id, body) => {
      await runAction('Posting comment and asking AI', async () => {
        if (shell.status !== 'ready') return;
        const provider = resolveActiveAiProvider(shell.settings, selectedAiProvider);
        await api.post<CommentDto>(`/api/work-items/${id}/comments`, { author: actor, kind: 'Comment', body });
        await api.post<AiRun>(`/api/work-items/${id}/ai-plan`, {
          provider: provider.provider,
          model: resolveActiveAiModel(shell.settings, selectedAiProvider, selectedAiModel)
        });
        await refreshAfterChange(id);
      });
    },
    updateComment: async (commentId, workItemId, body) => {
      await runAction('Updating comment', async () => {
        await api.patch<CommentDto>(`/api/comments/${commentId}`, { actor, body });
        await refreshAfterChange(workItemId);
      });
    },
    deleteComment: async (commentId, workItemId) => {
      await runAction('Deleting comment', async () => {
        await api.delete(`/api/comments/${commentId}?actor=${encodeURIComponent(actor)}`);
        await refreshAfterChange(workItemId);
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
  const activeBoardId = shell.status === 'ready' && selectedBoardId && shell.boards.some((entry) => entry.id === selectedBoardId)
    ? selectedBoardId
    : shell.status === 'ready'
      ? shell.board.id
      : selectedBoardId;

  return (
    <div className="app-shell">
      <Sidebar view={view} onChange={setView} onNewCard={() => setCreateStatus('Todo')} />
      <main className="main">
        <Topbar query={query} onQueryChange={setQuery} userName={auth.status === 'ready' ? auth.userName : null} />
        {auth.status === 'checking' && <Loading message="Checking authentication..." />}
        {auth.status === 'error' && <ErrorPanel message={auth.message} onRetry={() => window.location.reload()} />}
        {shell.status === 'loading' && <Loading />}
        {shell.status === 'error' && <ErrorPanel message={shell.message} onRetry={loadShell} />}
        {shell.status === 'ready' && board && (
          <>
            {view === 'dashboard' && <DashboardView workspace={shell.workspace} board={shell.board} previews={shell.previews} events={shell.events} pipelines={shell.pipelines} metrics={shell.metrics} actions={actions} />}
            {view === 'board' && <BoardView board={board} boards={shell.boards} selectedBoardId={activeBoardId ?? shell.board.id} actions={actions} />}
            {view === 'timeline' && <TimelineView board={shell.board} boards={shell.boards} selectedBoardId={activeBoardId ?? shell.board.id} timeline={shell.timeline} actions={actions} />}
            {view === 'settings' && <SettingsView settings={shell.settings} selectedProvider={selectedAiProvider} selectedModel={selectedAiModel} onProviderChange={setSelectedAiProvider} onModelChange={setSelectedAiModel} onBack={() => setView('board')} />}
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
          onCreate={actions.createBoard}
          onClose={() => setCreateBoardOpen(false)}
        />
      )}
      <ToastStack busyAction={busyAction ?? (shell.status === 'ready' && shell.busy ? 'Syncing API state' : null)} toasts={toasts} onDismiss={(id) => setToasts((current) => current.filter((toast) => toast.id !== id))} />
    </div>
  );
}

type AuthState =
  | { status: 'checking' }
  | { status: 'disabled' }
  | { status: 'ready'; accessToken: string; userName: string; userEmail?: string }
  | { status: 'error'; message: string };

function useAuth(): AuthState {
  const [auth, setAuth] = React.useState<AuthState>(() => authSettings.enabled ? { status: 'checking' } : { status: 'disabled' });

  React.useEffect(() => {
    if (!authSettings.enabled) return;
    let cancelled = false;

    initializeAuth()
      .then((user) => {
        if (!cancelled) {
          const userEmail = typeof user.profile.email === 'string' ? user.profile.email : undefined;
          setAuth({
            status: 'ready',
            accessToken: user.access_token,
            userName: user.profile.name || user.profile.preferred_username || userEmail || 'Authenticated',
            userEmail
          });
        }
      })
      .catch((error) => {
        if (!cancelled) setAuth({ status: 'error', message: error instanceof Error ? error.message : 'Authentication failed' });
      });

    return () => {
      cancelled = true;
    };
  }, []);

  return auth;
}

const authSettings = {
  enabled: import.meta.env.VITE_AUTH_ENABLED === 'true',
  authority: import.meta.env.VITE_AUTH_AUTHORITY as string | undefined,
  clientId: import.meta.env.VITE_AUTH_CLIENT_ID as string | undefined,
  redirectUri: (import.meta.env.VITE_AUTH_REDIRECT_URI as string | undefined) ?? `${window.location.origin}/auth/callback`,
  postLogoutRedirectUri: (import.meta.env.VITE_AUTH_POST_LOGOUT_REDIRECT_URI as string | undefined) ?? window.location.origin
};

const userManager = authSettings.enabled ? new UserManager({
  authority: authSettings.authority ?? '',
  client_id: authSettings.clientId ?? '',
  redirect_uri: authSettings.redirectUri,
  post_logout_redirect_uri: authSettings.postLogoutRedirectUri,
  response_type: 'code',
  scope: 'openid profile email',
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

  await userManager.signinRedirect();
  return new Promise<User>(() => undefined);
}

type ShellState =
  | { status: 'loading' }
  | { status: 'error'; message: string }
  | { status: 'ready'; workspace: Workspace; boards: Board[]; board: Board; repositories: RepositoryDto[]; settings: SettingsDto; previews: PreviewEnvironmentDto[]; events: PreviewEventDto[]; pipelines: PipelineStatusDto[]; timeline: TimelineEventDto[]; metrics: MetricsDto; assignees: AssigneeDto[]; busy: boolean };

type SelectedState =
  | { status: 'closed' }
  | { status: 'loading'; id: string }
  | { status: 'open'; detail: WorkItemDetail; aiRuns: AiRun[]; busy: boolean };

type BoardActions = {
  openWorkItem(id: string): void;
  openCreateCard(status: string): void;
  openCreateBoard(): void;
  selectBoard(id: string): void;
  createBoard(form: CreateBoardForm): Promise<void>;
  executePipeline(pipelineRunId: string): Promise<void>;
  createCard(form: WorkItemForm): Promise<void>;
  updateCard(id: string, form: WorkItemForm): Promise<void>;
  deleteCard(id: string): Promise<void>;
  startAiPlan(id: string): Promise<void>;
  approvePlan(runId: string, workItemId: string): Promise<void>;
  discardPlan(runId: string, workItemId: string): Promise<void>;
  approvePullRequest(workItemId: string): Promise<void>;
  startPreview(workItemId: string): Promise<void>;
  stopPreview(workItemId: string): Promise<void>;
  addComment(id: string, body: string): Promise<void>;
  addCommentAndAskAi(id: string, body: string): Promise<void>;
  updateComment(commentId: string, workItemId: string, body: string): Promise<void>;
  deleteComment(commentId: string, workItemId: string): Promise<void>;
  moveCard(id: string, status: string, sortOrder: number): Promise<void>;
};

function useStateFromHash(): [View, (view: View) => void] {
  const readHash = () => {
    const next = (window.location.hash.replace('#', '') as View) || 'board';
    return ['dashboard', 'board', 'timeline', 'settings'].includes(next) ? next : 'board';
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

function Sidebar({ view, onChange, onNewCard }: { view: View; onChange: (view: View) => void; onNewCard: () => void }) {
  return (
    <aside className="sidebar">
      <div className="brand">
        <div className="brand-mark"><SquareTerminal size={20} /></div>
        <div>
          <div className="brand-name">Rosenvall</div>
          <div className="brand-subtitle">DevOps Engine</div>
        </div>
      </div>
      <button className="primary-action" onClick={onNewCard}><Plus size={16} />New card</button>
      <nav className="side-nav">
        <NavButton active={view === 'board'} icon={<PanelLeft size={20} />} label="Board" onClick={() => onChange('board')} />
        <NavButton active={view === 'timeline'} icon={<History size={20} />} label="Timeline" onClick={() => onChange('timeline')} />
        <NavButton active={view === 'dashboard'} icon={<LayoutDashboard size={20} />} label="Dashboard" onClick={() => onChange('dashboard')} />
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

function Topbar({ query, onQueryChange, userName }: { query: string; onQueryChange: (query: string) => void; userName: string | null }) {
  return (
    <header className="topbar">
      <label className="search">
        <Search size={18} />
        <input value={query} onChange={(event) => onQueryChange(event.target.value)} placeholder="Search work items..." />
      </label>
      <div className="avatar">{userName ? initials(userName) : 'CR'}</div>
    </header>
  );
}

function Loading({ message = 'Loading Rosenvall DevOps...' }: { message?: string }) {
  return <section className="page"><div className="panel state-panel">{message}</div></section>;
}

function ErrorPanel({ message, onRetry }: { message: string; onRetry: () => void }) {
  return (
    <section className="page">
      <div className="panel state-panel">
        <h2>API unavailable</h2>
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
              <a href={preview.url} target="_blank" rel="noreferrer">{preview.url}</a>
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

function BoardView({ board, boards, selectedBoardId, actions }: { board: Board; boards: Board[]; selectedBoardId: string; actions: BoardActions }) {
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
      <div className="page-heading compact-heading">
        <div>
          <BoardSelector boards={boards} selectedBoardId={selectedBoardId} onSelect={actions.selectBoard} onAdd={actions.openCreateBoard} />
          <p>{board.repository ? `${board.repository.provider} / ${board.repository.name} - ${board.repository.defaultBranch}` : 'No repository linked'} - {board.columns.reduce((total, column) => total + column.items.length, 0)} active items</p>
        </div>
        <button className="primary-action" onClick={() => actions.openCreateCard(board.columns[0]?.name ?? 'Todo')}><Plus size={16} />New card</button>
      </div>
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

function TimelineView({ board, boards, selectedBoardId, timeline, actions }: {
  board: Board;
  boards: Board[];
  selectedBoardId: string;
  timeline: TimelineEventDto[];
  actions: BoardActions;
}) {
  const [filter, setFilter] = React.useState('All');
  const filters = ['All', 'Cards', 'Git', 'Pipelines', 'Previews'];
  const filtered = timeline.filter((entry) => filter === 'All' || timelineBucket(entry.kind) === filter);
  return (
    <section className="page timeline-page">
      <div className="page-heading compact-heading">
        <div>
          <BoardSelector boards={boards} selectedBoardId={selectedBoardId} onSelect={actions.selectBoard} onAdd={actions.openCreateBoard} />
          <p>{board.repository ? `${board.repository.provider} / ${board.repository.name}` : 'No repository linked'} - {filtered.length} events</p>
        </div>
        <div className="timeline-filters">
          {filters.map((entry) => (
            <button className={filter === entry ? 'secondary active-filter' : 'secondary'} onClick={() => setFilter(entry)} key={entry}>{entry}</button>
          ))}
        </div>
      </div>
      <section className="panel timeline-panel">
        {filtered.length === 0 && <EmptyState>No timeline events for this board.</EmptyState>}
        {filtered.map((entry) => (
          <article className="timeline-row" key={entry.id}>
            <div>
              <div className="timeline-row-head"><strong>{entry.title}</strong><span className={statusClass(entry.kind)}>{entry.kind}</span></div>
              <p>{entry.message}</p>
              {entry.url && <a href={entry.url} target="_blank" rel="noreferrer">{entry.url}</a>}
            </div>
            <time>{relativeTime(entry.createdAt)}</time>
          </article>
        ))}
      </section>
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
        <span className={item.type === 'Bug' ? 'type bug' : 'type'}>{item.type}</span>
        <code>{item.key}</code>
      </div>
      <h3>{item.title}</h3>
      {item.aiStatus && <div className="ai-state"><Sparkles size={14} />{item.aiStatus}</div>}
      {item.previewUrl && <div className="preview-chip"><ExternalLink size={13} />Demo ready</div>}
      <div className="card-footer"><span>{item.assignee ?? 'Unassigned'}</span><span>{item.commentCount ? `${item.commentCount} comments` : ''}</span></div>
    </article>
  );
}

function WorkItemCardPreview({ item }: { item: WorkItemSummary }) {
  return (
    <article className={item.status === 'AI Planning' ? 'card ai-card overlay-card' : 'card overlay-card'}>
      <div className="card-row"><span className={item.type === 'Bug' ? 'type bug' : 'type'}>{item.type}</span><code>{item.key}</code></div>
      <h3>{item.title}</h3>
      {item.aiStatus && <div className="ai-state"><Sparkles size={14} />{item.aiStatus}</div>}
    </article>
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
  const [comment, setComment] = React.useState('');
  const sortedPlans = React.useMemo(() => [...aiRuns].sort((left, right) => left.sequenceNumber - right.sequenceNumber || left.createdAt.localeCompare(right.createdAt)), [aiRuns]);
  const defaultPlan = [...sortedPlans].reverse().find((run) => run.status === 'PlanReady') ?? sortedPlans[sortedPlans.length - 1];
  const [selectedPlanId, setSelectedPlanId] = React.useState<string | null>(() => defaultPlan?.id ?? null);
  const selectedPlan = sortedPlans.find((run) => run.id === selectedPlanId) ?? defaultPlan;

  React.useEffect(() => {
    setForm(formFromDetail(detail));
  }, [detail]);

  React.useEffect(() => {
    if (!selectedPlanId || !sortedPlans.some((run) => run.id === selectedPlanId)) {
      setSelectedPlanId(defaultPlan?.id ?? null);
    }
  }, [defaultPlan?.id, selectedPlanId, sortedPlans]);

  return (
    <ModalFrame title={`${detail.item.key} ${detail.item.title}`} onClose={onClose}>
      <div className="modal-grid">
        <section className="modal-main">
          <div className="form-grid">
            <label>Title<input value={form.title} onChange={(event) => setForm({ ...form, title: event.target.value })} /></label>
            <label>Description<textarea value={form.description} onChange={(event) => setForm({ ...form, description: event.target.value })} /></label>
            <label>Type<select value={form.type} onChange={(event) => setForm({ ...form, type: event.target.value })}><option>Feature</option><option>Bug</option><option>Task</option><option>Epic</option></select></label>
            <label>Status<select value={form.status} onChange={(event) => setForm({ ...form, status: event.target.value })}>{board?.columns.map((column) => <option key={column.name}>{column.name}</option>)}</select></label>
            <label>Priority<select value={form.priority} onChange={(event) => setForm({ ...form, priority: event.target.value })}><option>Low</option><option>Medium</option><option>High</option></select></label>
            <label>Assignee<AssigneeSelect value={form.assignee} options={assigneeOptions} onChange={(assignee) => setForm({ ...form, assignee })} /></label>
          </div>
          <div className="modal-actions">
            <button className="primary-action" disabled={!form.title.trim() || busy} onClick={() => void actions.updateCard(detail.item.id, form)}><Save size={16} />Save</button>
            <button className="danger-button" disabled={busy} onClick={() => confirm('Delete this work item and tear down its preview namespace/resources?') && void actions.deleteCard(detail.item.id)}><Trash2 size={16} />Delete and clean up</button>
          </div>
          <AiPlanPanel detail={detail} aiRuns={sortedPlans} selectedPlan={selectedPlan} onSelectPlan={setSelectedPlanId} busy={busy} busyLabel={busyLabel} aiProvider={aiProvider} aiModel={aiModel} actions={actions} />
          <section className="activity">
            <h2>Activity</h2>
            {detail.comments.length === 0 && <EmptyState>No comments yet.</EmptyState>}
            {detail.comments.map((entry) => (
              <ActivityEntry
                actor={actor}
                aiRuns={sortedPlans}
                comment={entry}
                key={entry.id}
                onDelete={(commentId) => actions.deleteComment(commentId, detail.item.id)}
                onSelectPlan={setSelectedPlanId}
                onUpdate={(commentId, body) => actions.updateComment(commentId, detail.item.id, body)}
                busy={busy}
              />
            ))}
            <form className="comment-form" onSubmit={(event) => {
              event.preventDefault();
              if (!comment.trim()) return;
              void actions.addComment(detail.item.id, comment.trim()).then(() => setComment(''));
            }}>
              <textarea value={comment} onChange={(event) => setComment(event.target.value)} placeholder="Write a comment or question..." />
              <div className="comment-actions">
                <button className="secondary" disabled={!comment.trim() || busy}>Comment</button>
                <button className="primary-action" type="button" disabled={!comment.trim() || busy} onClick={() => void actions.addCommentAndAskAi(detail.item.id, comment.trim()).then(() => setComment(''))}><Sparkles size={16} />Comment + ask AI</button>
              </div>
            </form>
          </section>
        </section>
        <aside className="modal-side">
          {detail.development && (
            <section className="panel compact-panel">
              <PanelHeader icon={<Github size={20} />} title="Development" />
              <p className="repo">{detail.development.repository}<br />{detail.development.branch}</p>
              {detail.development.pullRequestUrl && <a className="url-box" href={detail.development.pullRequestUrl} target="_blank" rel="noreferrer">Pull request <ExternalLink size={16} /></a>}
              <p className="status-line"><span />{developmentStatusText(detail.development)}</p>
              {detail.development.pullRequestUrl && !detail.development.pullRequestApprovedAt && (
                <button className="primary-action side-action" disabled={busy} onClick={() => void actions.approvePullRequest(detail.item.id)}><CheckCircle2 size={16} />Approve PR</button>
              )}
              {detail.development.pullRequestApprovedAt && (
                <p className="approval-note">Approved by {detail.development.pullRequestApprovedBy} {relativeTime(detail.development.pullRequestApprovedAt)}.</p>
              )}
              {!detail.development.pullRequestUrl && <button className="secondary side-action" disabled>No PR for local preview</button>}
            </section>
          )}
          {detail.preview && (
            <PreviewPanel preview={detail.preview} busy={busy} onRetry={() => actions.startPreview(detail.item.id)} />
          )}
        </aside>
      </div>
    </ModalFrame>
  );
}

function AiPlanPanel({ detail, aiRuns, selectedPlan, onSelectPlan, busy, busyLabel, aiProvider, aiModel, actions }: {
  detail: WorkItemDetail;
  aiRuns: AiRun[];
  selectedPlan?: AiRun;
  onSelectPlan: (id: string) => void;
  busy: boolean;
  busyLabel: string | null;
  aiProvider: string | null;
  aiModel: string | null;
  actions: BoardActions;
}) {
  const previewBusy = ['Implementing', 'Applying', 'Provisioning'].includes(detail.preview?.status ?? '');
  const previewRunning = detail.preview?.status === 'Running';
  const previewHasGeneratedSource = (detail.preview?.sourceFiles?.length ?? 0) > 0;
  const canImplementSelected = !!selectedPlan && ['PlanReady', 'Approved'].includes(selectedPlan.status) && !previewBusy && (!previewRunning || !previewHasGeneratedSource);
  return (
    <section className="panel ai-plan-panel">
      <PanelHeader icon={<Bot size={20} />} title="AI plans" />
      <div className="ai-plan-body">
        {busyLabel && <div className="inline-progress"><Sparkles size={15} />{busyLabel}...</div>}
        <div className="plan-toolbar">
          {aiModel && <p>Next run: {aiProvider ? `${aiProvider} / ` : ''}{aiModel}.</p>}
          <button className="secondary" disabled={busy} onClick={() => void actions.startAiPlan(detail.item.id)}><Sparkles size={16} />{aiRuns.length > 0 ? 'Generate revised AI plan' : 'Generate AI plan'}</button>
        </div>
        {aiRuns.length > 0 && (
          <div className="plan-tabs" aria-label="AI plans">
            {aiRuns.map((run) => (
              <button className={run.id === selectedPlan?.id ? 'plan-tab active' : 'plan-tab'} key={run.id} onClick={() => onSelectPlan(run.id)} type="button">
                <strong>#{run.sequenceNumber}</strong>
                <span>{planTitle(run)}</span>
                <small>{run.status} · {run.model} · {relativeTime(run.createdAt)}</small>
              </button>
            ))}
          </div>
        )}
        {selectedPlan?.plan
          ? (
            <>
              <p>Provider: {selectedPlan.provider} / {selectedPlan.model}. Status: {selectedPlan.status}.</p>
              <div className="plan-markdown"><CommentBody body={selectedPlan.plan} /></div>
            </>
          )
          : <EmptyState>No AI plans yet.</EmptyState>}
        <div className="approval-row">
          {selectedPlan && canImplementSelected && <button className="primary-action" disabled={busy} onClick={() => void actions.approvePlan(selectedPlan.id, detail.item.id)}><CheckCircle2 size={16} />{selectedPlan.status === 'Approved' ? 'Rebuild with Codex' : 'Implement plan'}</button>}
          {selectedPlan?.status === 'PlanReady' && <button className="secondary" disabled={busy} onClick={() => void actions.discardPlan(selectedPlan.id, detail.item.id)}>Discard plan</button>}
        </div>
        {selectedPlan && ['PlanReady', 'Approved'].includes(selectedPlan.status) && <p className="plan-help">Uses Codex to generate React/Tailwind preview source from the approved plan and deploys it to Kubernetes.</p>}
        {aiRuns.some((run) => run.status === 'Discarded') && <p>Discarded plans are kept in history but hidden from approval.</p>}
      </div>
    </section>
  );
}

function PreviewPanel({ preview, busy, onRetry }: { preview: PreviewDto; busy: boolean; onRetry: () => Promise<void> }) {
  const status = preview.status;
  const running = status === 'Running';
  const failed = status === 'Failed';
  const waiting = !running && !failed;
  const actionLabel = failed ? 'Preview failed' : waiting ? 'Waiting for healthy preview...' : 'Open demo environment';
  const steps = previewLifecycleSteps(status);
  return (
    <section className="panel compact-panel preview-panel">
      <PanelHeader icon={<ExternalLink size={20} />} title="Preview environment" />
      {running
        ? <a className="demo-link" href={preview.url} target="_blank" rel="noreferrer">Open demo environment <ExternalLink size={16} /></a>
        : <button className="demo-link disabled" disabled>{waiting && <span className="spinner" />}{actionLabel}</button>}
      <ol className="preview-stepper" aria-label="Preview lifecycle">
        {steps.map((step, index) => (
          <li className={`stepper-item ${step.state}`} key={step.key}>
            <span className="stepper-marker">{step.state === 'done' ? <CheckCircle2 size={13} /> : index + 1}</span>
            <span className="stepper-body"><strong>{step.title}</strong><span>{step.description}</span></span>
          </li>
        ))}
      </ol>
      {preview.namespace && <p className="namespace-note">Namespace: <code>{preview.namespace}</code></p>}
      <p className="preview-message">{preview.message ?? preview.phase ?? previewStatusText(preview)}</p>
      {preview.podName && <p className="namespace-note">Pod: <code>{preview.podName}</code></p>}
      {preview.lastCheckedAt && <p className="namespace-note">Last checked {relativeTime(preview.lastCheckedAt)}.</p>}
      <PreviewTerminal lines={preview.terminalLines ?? []} active={waiting} />
      <div className="split-stats"><span>Status<br /><strong>{status}</strong></span><span>TTL<br /><strong>{relativeDays(preview.expiresAt)}</strong></span></div>
      {failed && preview.failureReason && <p className="failure-reason">Reason: {preview.failureReason}</p>}
      {failed && preview.failureLog && <pre className="failure-log">{preview.failureLog}</pre>}
      {failed && <button className="primary-action side-action" disabled={busy} onClick={() => void onRetry()}><Play size={16} />Retry preview setup</button>}
    </section>
  );
}

function PreviewTerminal({ lines, active }: { lines: PreviewTerminalLineDto[]; active: boolean }) {
  const [expanded, setExpanded] = React.useState(false);
  const terminalRef = React.useRef<HTMLDivElement | null>(null);
  React.useEffect(() => {
    terminalRef.current?.scrollTo({ top: terminalRef.current.scrollHeight });
  }, [lines.length]);
  const visibleLines = lines.slice(-160);
  const terminalContent = <TerminalLog lines={visibleLines} terminalRef={terminalRef} />;
  return (
    <>
      <div className="preview-terminal">
        <div className="terminal-head">
          <span><SquareTerminal size={15} />Implementation log</span>
          <div className="terminal-actions">
            {active && <span className="terminal-live"><span className="spinner" />live</span>}
            <button className="terminal-expand" type="button" onClick={() => setExpanded(true)} aria-label="Open larger implementation log"><Maximize2 size={14} />Expand</button>
          </div>
        </div>
        {terminalContent}
      </div>
      {expanded && (
        <ModalFrame title="Implementation log" onClose={() => setExpanded(false)} size="wide">
          <div className="terminal-modal-content">
            <div className="terminal-modal-meta">
              <span>{lines.length} log lines</span>
              {active && <span className="terminal-live"><span className="spinner" />live</span>}
            </div>
            <TerminalLog lines={lines} expanded />
          </div>
        </ModalFrame>
      )}
    </>
  );
}

function TerminalLog({ lines, expanded = false, terminalRef }: { lines: PreviewTerminalLineDto[]; expanded?: boolean; terminalRef?: React.RefObject<HTMLDivElement | null> }) {
  return (
    <div className={expanded ? 'terminal-body expanded' : 'terminal-body'} ref={terminalRef}>
      {lines.length === 0
        ? <p className="terminal-empty">Waiting for Codex output...</p>
        : lines.map((line, index) => (
          <div className={`terminal-line ${line.stream.toLowerCase()}`} key={`${line.createdAt}-${index}`}>
            <span>{terminalTime(line.createdAt)}</span>
            <code><strong>{terminalStreamLabel(line.stream)}</strong>{line.message}</code>
          </div>
        ))}
    </div>
  );
}

function previewLifecycleSteps(status: string) {
  const steps = [
    ['Implementing', 'Implementing preview source', 'Codex generates the React/Tailwind files.'],
    ['Applying', 'Applying Kubernetes resources', 'The API submits namespace, ConfigMap, Deployment, Service and route.'],
    ['Provisioning', 'Waiting for pod readiness', 'Kubernetes starts the preview pod and health checks it.'],
    ['Running', 'Running', 'The deployment is available and the demo link is enabled.']
  ] as const;
  const current = status === 'Failed'
    ? 2
    : Math.max(0, steps.findIndex(([key]) => key === status));

  return steps.map(([key, title, description], index) => ({
    key,
    title,
    description,
    state: status === 'Running'
      ? 'done'
      : status === 'Failed' && index === current
        ? 'blocked'
        : index < current
          ? 'done'
          : index === current
            ? 'active'
            : 'pending'
  }));
}

function terminalStreamLabel(stream: string) {
  const normalized = stream.toLowerCase();
  if (normalized === 'stderr') return 'agent';
  if (normalized === 'stdout') return 'output';
  return normalized || 'system';
}

function previewStatusText(preview: PreviewDto) {
  if (preview.status === 'Running') return 'Preview is healthy and ready.';
  if (preview.status === 'Failed') return 'Preview setup failed. Review the reason below and retry after fixing the issue.';
  if (preview.status === 'Implementing') return 'Generating local React/Tailwind source from the card context.';
  if (preview.status === 'Applying') return 'Applying Kubernetes resources.';
  return 'Kubernetes resources are applied. Waiting for a healthy preview pod.';
}

function isPreviewPendingStatus(status?: string) {
  return status === 'Implementing' || status === 'Applying' || status === 'Provisioning';
}

function developmentStatusText(development: DevelopmentDto) {
  if (development.repository === 'local/vite-react-tailwind' &&
      development.checksStatus.toLowerCase().includes('preview ready')) {
    return 'Local React/Tailwind source generated';
  }

  return development.checksStatus;
}

function CreateWorkItemModal({ board, initialStatus, assigneeOptions, onCreate, onClose }: {
  board: Board;
  initialStatus: string;
  assigneeOptions: AssigneeOption[];
  onCreate: (form: WorkItemForm) => Promise<void>;
  onClose: () => void;
}) {
  const [form, setForm] = React.useState<WorkItemForm>({ ...emptyForm, status: initialStatus, assignee: preferredAssignee(assigneeOptions) });
  return (
    <ModalFrame title="New card" onClose={onClose}>
      <form className="create-form" onSubmit={(event) => {
        event.preventDefault();
        if (!form.title.trim()) return;
        void onCreate(form);
      }}>
        <label>Title<input autoFocus value={form.title} onChange={(event) => setForm({ ...form, title: event.target.value })} /></label>
        <label>Description<textarea value={form.description} onChange={(event) => setForm({ ...form, description: event.target.value })} /></label>
        <div className="form-grid two">
          <label>Type<select value={form.type} onChange={(event) => setForm({ ...form, type: event.target.value })}><option>Feature</option><option>Bug</option><option>Task</option><option>Epic</option></select></label>
          <label>Status<select value={form.status} onChange={(event) => setForm({ ...form, status: event.target.value })}>{board.columns.map((column) => <option key={column.name}>{column.name}</option>)}</select></label>
          <label>Priority<select value={form.priority} onChange={(event) => setForm({ ...form, priority: event.target.value })}><option>Low</option><option>Medium</option><option>High</option></select></label>
          <label>Assignee<AssigneeSelect value={form.assignee} options={assigneeOptions} onChange={(assignee) => setForm({ ...form, assignee })} /></label>
        </div>
        <div className="modal-actions">
          <button className="primary-action" disabled={!form.title.trim()}><Plus size={16} />Create card</button>
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

function CreateBoardModal({ onCreate, onClose }: {
  onCreate: (form: CreateBoardForm) => Promise<void>;
  onClose: () => void;
}) {
  const [form, setForm] = React.useState<CreateBoardForm>(emptyBoardForm);
  const normalizedName = form.name.trim();
  return (
    <ModalFrame title="Add board" onClose={onClose}>
      <form className="create-form" onSubmit={(event) => {
        event.preventDefault();
        if (!normalizedName || !form.repositoryRemoteUrl.trim()) return;
        void onCreate({ ...form, name: normalizedName });
      }}>
        <label>Board name<input autoFocus value={form.name} onChange={(event) => setForm({ ...form, name: event.target.value })} placeholder="Gatewaybound" /></label>
        <div className="form-grid two">
          <label>Provider<select value={form.repositoryProvider} onChange={(event) => setForm({ ...form, repositoryProvider: event.target.value })}><option>Forgejo</option><option>GitHub</option><option>AzureDevOps</option><option>GenericGit</option></select></label>
          <label>Default branch<input value={form.repositoryDefaultBranch} onChange={(event) => setForm({ ...form, repositoryDefaultBranch: event.target.value })} /></label>
          <label>Repository URL<input value={form.repositoryRemoteUrl} onChange={(event) => setForm({ ...form, repositoryRemoteUrl: event.target.value })} placeholder="https://github.com/owner/repo.git" /></label>
        </div>
        <div className="modal-actions">
          <button className="primary-action" disabled={!normalizedName || !form.repositoryRemoteUrl.trim()}><Plus size={16} />Create board</button>
          <button className="secondary" type="button" onClick={onClose}>Cancel</button>
        </div>
      </form>
    </ModalFrame>
  );
}

function SettingsView({ settings, selectedProvider, selectedModel, onProviderChange, onModelChange, onBack }: {
  settings: SettingsDto;
  selectedProvider: string | null;
  selectedModel: string | null;
  onProviderChange: (provider: string) => void;
  onModelChange: (model: string) => void;
  onBack: () => void;
}) {
  const activeProvider = resolveActiveAiProvider(settings, selectedProvider);
  const modelOptions = activeProvider.availableModels.length > 0 ? activeProvider.availableModels : [activeProvider.activeModel];
  const activeModel = resolveActiveAiModel(settings, selectedProvider, selectedModel);
  return (
    <section className="page settings-page">
      <div className="page-heading">
        <div>
          <h1>Settings</h1>
          <p>Configuration surfaced from the API.</p>
        </div>
        <button className="secondary" onClick={onBack}>Back to board</button>
      </div>
      <div className="settings-content">
        <SectionTitle icon={<Github size={22} />} title="GitHub integration" />
        <section className="panel form-panel">
          <div className="connected"><Github size={28} /><div><strong>Connected account</strong><p>{settings.gitHub.account}</p></div><span>{settings.gitHub.connected ? 'Active' : 'Disconnected'}</span></div>
          <label>Target repository<input value={settings.gitHub.targetRepository} readOnly /></label>
          <label>Branch watch patterns<input value={settings.gitHub.branchWatchPatterns} readOnly /></label>
        </section>
        <SectionTitle icon={<Bot size={22} />} title="AI engine" amber />
        <section className="panel form-panel ai-settings">
          <label>Provider<select value={activeProvider.provider} onChange={(event) => onProviderChange(event.target.value)}>{settings.ai.availableProviders.map((provider) => <option value={provider.provider} key={provider.provider} disabled={provider.status === 'Unavailable'}>{provider.displayName} - {provider.status}</option>)}</select></label>
          <label>Planning model<select value={activeModel} onChange={(event) => onModelChange(event.target.value)}>{modelOptions.map((model) => <option value={model} key={model}>{model}</option>)}</select></label>
          <label>Adapter endpoint<input value={activeProvider.endpoint} readOnly /></label>
          <p className={activeProvider.status === 'Ready' ? 'provider-status ready' : 'provider-status'}>{activeProvider.displayName} status: {activeProvider.status === 'LoginRequired' ? 'Login required on server' : activeProvider.status}.</p>
          <label>Auto review pull requests<input value={settings.ai.autoReviewPullRequests ? 'Enabled' : 'Disabled'} readOnly /></label>
        </section>
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
      </div>
    </section>
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

function CommentBody({ body }: { body: string }) {
  const [expanded, setExpanded] = React.useState(false);
  const collapsible = body.length > 520 || body.split(/\r?\n/).length > 6;
  const visible = collapsible && !expanded ? `${body.slice(0, 520).trimEnd()}...` : body;
  return (
    <div
      className={collapsible && !expanded ? 'markdown-body collapsed' : 'markdown-body'}
      onClick={() => collapsible && setExpanded(true)}
      role={collapsible ? 'button' : undefined}
      tabIndex={collapsible ? 0 : undefined}
      onKeyDown={(event) => {
        if (collapsible && (event.key === 'Enter' || event.key === ' ')) {
          setExpanded(true);
        }
      }}
    >
      <MarkdownText text={visible} />
      {collapsible && !expanded && <span className="read-more">Show more</span>}
    </div>
  );
}

function MarkdownText({ text }: { text: string }) {
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
      nodes.push(link ? <a key={nodes.length} href={link[2]} target="_blank" rel="noreferrer">{link[1]}</a> : token);
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

function planTitle(run: AiRun): string {
  const firstLine = (run.plan ?? '')
    .split(/\r?\n/)
    .map((line) => line.replace(/^#+\s*/, '').replace(/\*\*/g, '').trim())
    .find(Boolean);
  if (!firstLine) return 'Untitled plan';
  return firstLine.length > 72 ? `${firstLine.slice(0, 69).trimEnd()}...` : firstLine;
}

function findReferencedPlan(comment: CommentDto, aiRuns: AiRun[]): AiRun | undefined {
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
  if (normalized.includes('pullrequest') || normalized.includes('commit') || normalized.includes('branch')) return 'Git';
  return 'Cards';
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

function formFromDetail(detail: WorkItemDetail): WorkItemForm {
  return {
    title: detail.item.title,
    description: detail.description,
    type: detail.item.type,
    status: detail.item.status,
    priority: detail.item.priority,
    assignee: detail.item.assignee ?? ''
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
  const path = normalized.split(/[/:]/).filter(Boolean).slice(-2).join('/');
  return path || boardName.trim() || 'repository';
}

function webUrlFromRemote(remoteUrl: string) {
  const normalized = remoteUrl.trim().replace(/\.git$/i, '');
  if (normalized.startsWith('http://') || normalized.startsWith('https://')) {
    return normalized;
  }

  const sshMatch = normalized.match(/^ssh:\/\/git@([^/]+)\/(.+)$/i) ?? normalized.match(/^git@([^:]+):(.+)$/i);
  return sshMatch ? `https://${sshMatch[1]}/${sshMatch[2]}` : null;
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
