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
  GripVertical,
  History,
  LayoutDashboard,
  PanelLeft,
  Play,
  Plus,
  Save,
  Search,
  Settings,
  Sparkles,
  SquareTerminal,
  Square,
  Trash2,
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

type View = 'dashboard' | 'board' | 'settings';

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
  };
  preview: {
    domain: string;
    defaultTtlDays: number;
    namespace: string;
  };
};

type AiRun = {
  id: string;
  workItemId: string;
  provider: string;
  model: string;
  status: string;
  plan?: string | null;
  approvedBy?: string | null;
};

type WorkItemForm = {
  title: string;
  description: string;
  type: string;
  status: string;
  priority: string;
  assignee: string;
};

const emptyForm: WorkItemForm = {
  title: '',
  description: '',
  type: 'Feature',
  status: 'Todo',
  priority: 'Medium',
  assignee: ''
};

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
  const [query, setQuery] = React.useState('');
  const [error, setError] = React.useState<string | null>(null);
  const [busyAction, setBusyAction] = React.useState<string | null>(null);
  const [selectedAiModel, setSelectedAiModel] = React.useState<string | null>(null);

  React.useEffect(() => {
    setAccessTokenProvider(() => auth.status === 'ready' ? auth.accessToken : null);
  }, [auth]);

  const loadShell = React.useCallback(async () => {
    if (auth.status === 'checking') return;
    setShell((current) => current.status === 'ready' ? { ...current, busy: true } : { status: 'loading' });
    try {
      const workspaces = await api.get<Workspace[]>('/api/workspaces');
      const workspace = workspaces[0];
      if (!workspace) throw new Error('No workspace returned by API');
      const boards = await api.get<Board[]>(`/api/workspaces/${workspace.id}/boards`);
      const board = boards[0];
      if (!board) throw new Error('No board returned by API');
      const [settings, previews, events, pipelines] = await Promise.all([
        api.get<SettingsDto>('/api/settings'),
        api.get<PreviewEnvironmentDto[]>('/api/preview-environments'),
        api.get<PreviewEventDto[]>('/api/preview-events'),
        api.get<PipelineStatusDto[]>('/api/pipelines')
      ]);
      setShell({ status: 'ready', workspace, board, settings, previews, events, pipelines, busy: false });
      setError(null);
    } catch (loadError) {
      setShell({ status: 'error', message: loadError instanceof Error ? loadError.message : 'Failed to load API data' });
    }
  }, [auth.status]);

  const loadWorkItem = React.useCallback(async (id: string) => {
    setSelected((current) => current.status === 'open' && current.detail.item.id === id ? { ...current, busy: true } : { status: 'loading', id });
    try {
      const [detail, runs] = await Promise.all([
        api.get<WorkItemDetail>(`/api/work-items/${id}`),
        api.get<AiRun[]>(`/api/work-items/${id}/ai-runs`)
      ]);
      setSelected({ status: 'open', detail, aiRuns: runs, busy: false });
      setError(null);
    } catch (loadError) {
      setSelected({ status: 'closed' });
      setError(loadError instanceof Error ? loadError.message : 'Failed to load work item');
    }
  }, []);

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

  const runAction = React.useCallback(async (label: string, action: () => Promise<void>) => {
    setBusyAction(label);
    setError(null);
    try {
      await action();
    } catch (actionError) {
      setError(actionError instanceof Error ? actionError.message : `${label} failed`);
    } finally {
      setBusyAction(null);
    }
  }, []);

  const actions: BoardActions = {
    openWorkItem: (id) => void loadWorkItem(id),
    openCreateCard: (status) => setCreateStatus(status),
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
        await api.post<AiRun>(`/api/work-items/${id}/ai-plan`, {
          provider: shell.settings.ai.provider,
          model: resolveActiveAiModel(shell.settings, selectedAiModel)
        });
        await refreshAfterChange(id);
      });
    },
    approvePlan: async (runId, workItemId) => {
      await runAction('Approving plan and starting preview', async () => {
        await api.post<WorkItemDetail>(`/api/ai-runs/${runId}/approve`, { approvedBy: 'crille' });
        await refreshAfterChange(workItemId);
      });
    },
    discardPlan: async (runId, workItemId) => {
      await runAction('Discarding plan', async () => {
        await api.post<AiRun>(`/api/ai-runs/${runId}/discard`, { discardedBy: 'crille' });
        await refreshAfterChange(workItemId);
      });
    },
    approvePullRequest: async (workItemId) => {
      await runAction('Approving PR and stopping preview', async () => {
        await api.post<WorkItemDetail>(`/api/work-items/${workItemId}/approve-pr`, { approvedBy: 'crille' });
        await refreshAfterChange(workItemId);
      });
    },
    startPreview: async (workItemId) => {
      await runAction('Starting preview', async () => {
        await api.post<WorkItemDetail>(`/api/work-items/${workItemId}/preview/start`, { actor: 'crille' });
        await refreshAfterChange(workItemId);
      });
    },
    stopPreview: async (workItemId) => {
      await runAction('Stopping preview', async () => {
        await api.post<WorkItemDetail>(`/api/work-items/${workItemId}/preview/stop`, { actor: 'crille' });
        await refreshAfterChange(workItemId);
      });
    },
    addComment: async (id, body) => {
      await runAction('Posting comment', async () => {
        await api.post<CommentDto>(`/api/work-items/${id}/comments`, { author: 'crille', kind: 'Comment', body });
        await refreshAfterChange(id);
      });
    },
    addCommentAndAskAi: async (id, body) => {
      await runAction('Posting comment and asking AI', async () => {
        if (shell.status !== 'ready') return;
        await api.post<CommentDto>(`/api/work-items/${id}/comments`, { author: 'crille', kind: 'Comment', body });
        await api.post<AiRun>(`/api/work-items/${id}/ai-plan`, {
          provider: shell.settings.ai.provider,
          model: resolveActiveAiModel(shell.settings, selectedAiModel)
        });
        await refreshAfterChange(id);
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
        setError(moveError instanceof Error ? moveError.message : 'Could not move card');
      }
    }
  };

  const board = shell.status === 'ready' ? filterBoard(shell.board, query) : null;

  return (
    <div className="app-shell">
      <Sidebar view={view} onChange={setView} onNewCard={() => setCreateStatus('Todo')} />
      <main className="main">
        <Topbar query={query} onQueryChange={setQuery} userName={auth.status === 'ready' ? auth.userName : null} />
        {error && <div className="error-strip">{error}<button onClick={() => setError(null)}>Dismiss</button></div>}
        {auth.status === 'checking' && <Loading message="Checking authentication..." />}
        {auth.status === 'error' && <ErrorPanel message={auth.message} onRetry={() => window.location.reload()} />}
        {shell.status === 'loading' && <Loading />}
        {shell.status === 'error' && <ErrorPanel message={shell.message} onRetry={loadShell} />}
        {shell.status === 'ready' && board && (
          <>
            {(shell.busy || busyAction) && <div className="busy-strip">{busyAction ?? 'Syncing API state'}...</div>}
            {view === 'dashboard' && <DashboardView workspace={shell.workspace} board={shell.board} previews={shell.previews} events={shell.events} pipelines={shell.pipelines} actions={actions} />}
            {view === 'board' && <BoardView board={board} actions={actions} />}
            {view === 'settings' && <SettingsView settings={shell.settings} selectedModel={selectedAiModel} onModelChange={setSelectedAiModel} onBack={() => setView('board')} />}
          </>
        )}
      </main>
      {selected.status === 'loading' && <ModalFrame title="Work item" onClose={() => setSelected({ status: 'closed' })}><div className="modal-loading">Loading work item...</div></ModalFrame>}
      {selected.status === 'open' && (
        <WorkItemModal
          detail={selected.detail}
          aiRuns={selected.aiRuns}
          busy={selected.busy || busyAction !== null}
          board={shell.status === 'ready' ? shell.board : null}
          aiModel={shell.status === 'ready' ? resolveActiveAiModel(shell.settings, selectedAiModel) : null}
          actions={actions}
          onClose={() => setSelected({ status: 'closed' })}
        />
      )}
      {createStatus && shell.status === 'ready' && (
        <CreateWorkItemModal
          board={shell.board}
          initialStatus={createStatus}
          onCreate={actions.createCard}
          onClose={() => setCreateStatus(null)}
        />
      )}
    </div>
  );
}

type AuthState =
  | { status: 'checking' }
  | { status: 'disabled' }
  | { status: 'ready'; accessToken: string; userName: string }
  | { status: 'error'; message: string };

function useAuth(): AuthState {
  const [auth, setAuth] = React.useState<AuthState>(() => authSettings.enabled ? { status: 'checking' } : { status: 'disabled' });

  React.useEffect(() => {
    if (!authSettings.enabled) return;
    let cancelled = false;

    initializeAuth()
      .then((user) => {
        if (!cancelled) setAuth({ status: 'ready', accessToken: user.access_token, userName: user.profile.name || user.profile.preferred_username || user.profile.email || 'Authenticated' });
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
  | { status: 'ready'; workspace: Workspace; board: Board; settings: SettingsDto; previews: PreviewEnvironmentDto[]; events: PreviewEventDto[]; pipelines: PipelineStatusDto[]; busy: boolean };

type SelectedState =
  | { status: 'closed' }
  | { status: 'loading'; id: string }
  | { status: 'open'; detail: WorkItemDetail; aiRuns: AiRun[]; busy: boolean };

type BoardActions = {
  openWorkItem(id: string): void;
  openCreateCard(status: string): void;
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
  moveCard(id: string, status: string, sortOrder: number): Promise<void>;
};

function useStateFromHash(): [View, (view: View) => void] {
  const readHash = () => {
    const next = (window.location.hash.replace('#', '') as View) || 'board';
    return ['dashboard', 'board', 'settings'].includes(next) ? next : 'board';
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

function DashboardView({ workspace, board, previews, events, pipelines, actions }: { workspace: Workspace; board: Board; previews: PreviewEnvironmentDto[]; events: PreviewEventDto[]; pipelines: PipelineStatusDto[]; actions: BoardActions }) {
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
                  {preview.status === 'Running'
                    ? <button className="secondary compact" onClick={() => void actions.stopPreview(preview.workItemId!)}><Square size={13} />Stop</button>
                    : <button className="secondary compact" onClick={() => void actions.startPreview(preview.workItemId!)}><Play size={13} />Start</button>}
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
              <span className={statusClass(pipeline.status)}>{pipeline.stage}: {pipeline.status}</span>
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

function BoardView({ board, actions }: { board: Board; actions: BoardActions }) {
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
          <h1>{board.name}</h1>
          <p>{board.columns.reduce((total, column) => total + column.items.length, 0)} active items</p>
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
    >
      <div className="card-row">
        <span className={item.type === 'Bug' ? 'type bug' : 'type'}>{item.type}</span>
        <button className="drag-handle" aria-label={`Drag ${item.key}`} onClick={(event) => event.stopPropagation()} {...attributes} {...listeners}><GripVertical size={16} /></button>
      </div>
      <code>{item.key}</code>
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

function WorkItemModal({ detail, aiRuns, busy, board, aiModel, actions, onClose }: {
  detail: WorkItemDetail;
  aiRuns: AiRun[];
  busy: boolean;
  board: Board | null;
  aiModel: string | null;
  actions: BoardActions;
  onClose: () => void;
}) {
  const [form, setForm] = React.useState<WorkItemForm>(() => formFromDetail(detail));
  const [comment, setComment] = React.useState('');
  const latestPlan = [...aiRuns].reverse().find((run) => run.status === 'PlanReady');
  const latestActive = [...aiRuns].reverse().find((run) => ['PlanReady', 'Approved', 'ImplementationRunning'].includes(run.status));

  React.useEffect(() => {
    setForm(formFromDetail(detail));
  }, [detail]);

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
            <label>Assignee<input value={form.assignee} onChange={(event) => setForm({ ...form, assignee: event.target.value })} placeholder="Unassigned" /></label>
          </div>
          <div className="modal-actions">
            <button className="primary-action" disabled={!form.title.trim() || busy} onClick={() => void actions.updateCard(detail.item.id, form)}><Save size={16} />Save</button>
            <button className="danger-button" disabled={busy} onClick={() => confirm('Delete this work item and tear down its preview namespace/resources?') && void actions.deleteCard(detail.item.id)}><Trash2 size={16} />Delete and clean up</button>
          </div>
          <AiPlanPanel detail={detail} aiRuns={aiRuns} latestPlan={latestPlan} latestActive={latestActive} busy={busy} aiModel={aiModel} actions={actions} />
          <section className="activity">
            <h2>Activity</h2>
            {detail.comments.length === 0 && <EmptyState>No comments yet.</EmptyState>}
            {detail.comments.map((entry) => entry.kind === 'Plan' ? <AiComment comment={entry} key={entry.id} /> : <Comment comment={entry} key={entry.id} />)}
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
              <p className="status-line"><span />{detail.development.checksStatus}</p>
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
            <section className="panel compact-panel">
              <PanelHeader icon={<ExternalLink size={20} />} title="Preview environment" />
              <a className="primary-action demo-link" href={detail.preview.url} target="_blank" rel="noreferrer">Open demo environment <ExternalLink size={16} /></a>
              <a className="url-box" href={detail.preview.url} target="_blank" rel="noreferrer">{detail.preview.url}<ExternalLink size={16} /></a>
              {detail.preview.namespace && <p className="namespace-note">Namespace: <code>{detail.preview.namespace}</code></p>}
              <div className="split-stats"><span>Status<br /><strong>{detail.preview.status}</strong></span><span>TTL<br /><strong>{relativeDays(detail.preview.expiresAt)}</strong></span></div>
              {detail.preview.status === 'Running'
                ? <button className="secondary side-action" disabled={busy} onClick={() => void actions.stopPreview(detail.item.id)}><Square size={16} />Stop preview</button>
                : <button className="primary-action side-action" disabled={busy} onClick={() => void actions.startPreview(detail.item.id)}><Play size={16} />Start preview</button>}
            </section>
          )}
        </aside>
      </div>
    </ModalFrame>
  );
}

function AiPlanPanel({ detail, aiRuns, latestPlan, latestActive, busy, aiModel, actions }: {
  detail: WorkItemDetail;
  aiRuns: AiRun[];
  latestPlan?: AiRun;
  latestActive?: AiRun;
  busy: boolean;
  aiModel: string | null;
  actions: BoardActions;
}) {
  const canRequestPlan = !latestActive || latestActive.status === 'Approved' || latestActive.status === 'Completed';
  return (
    <section className="panel ai-plan-panel">
      <PanelHeader icon={<Bot size={20} />} title="AI plan" />
      <div className="ai-plan-body">
        {aiModel && <p>Next run model: {aiModel}.</p>}
        {canRequestPlan && <button className="secondary" disabled={busy} onClick={() => void actions.startAiPlan(detail.item.id)}><Sparkles size={16} />{latestActive ? 'Generate revised AI plan' : 'Generate AI plan'}</button>}
        {latestActive && <p>Provider: {latestActive.provider} / {latestActive.model}. Status: {latestActive.status}.</p>}
        {latestPlan?.plan && <pre>{latestPlan.plan}</pre>}
        <div className="approval-row">
          {latestPlan && <button className="primary-action" disabled={busy} onClick={() => void actions.approvePlan(latestPlan.id, detail.item.id)}><CheckCircle2 size={16} />Approve plan</button>}
          {latestPlan && <button className="secondary" disabled={busy} onClick={() => void actions.discardPlan(latestPlan.id, detail.item.id)}>Discard plan</button>}
        </div>
        {aiRuns.some((run) => run.status === 'Discarded') && <p>Discarded plans are kept in history but hidden from approval.</p>}
      </div>
    </section>
  );
}

function CreateWorkItemModal({ board, initialStatus, onCreate, onClose }: {
  board: Board;
  initialStatus: string;
  onCreate: (form: WorkItemForm) => Promise<void>;
  onClose: () => void;
}) {
  const [form, setForm] = React.useState<WorkItemForm>({ ...emptyForm, status: initialStatus });
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
          <label>Assignee<input value={form.assignee} onChange={(event) => setForm({ ...form, assignee: event.target.value })} /></label>
        </div>
        <div className="modal-actions">
          <button className="primary-action" disabled={!form.title.trim()}><Plus size={16} />Create card</button>
          <button className="secondary" type="button" onClick={onClose}>Cancel</button>
        </div>
      </form>
    </ModalFrame>
  );
}

function SettingsView({ settings, selectedModel, onModelChange, onBack }: {
  settings: SettingsDto;
  selectedModel: string | null;
  onModelChange: (model: string) => void;
  onBack: () => void;
}) {
  const modelOptions = settings.ai.availableModels.length > 0 ? settings.ai.availableModels : [settings.ai.activeModel];
  const activeModel = resolveActiveAiModel(settings, selectedModel);
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
          <label>Adapter endpoint<input value={settings.ai.endpoint} readOnly /></label>
          <label>Planning model<select value={activeModel} onChange={(event) => onModelChange(event.target.value)}>{modelOptions.map((model) => <option value={model} key={model}>{model}</option>)}</select></label>
          <label>Provider<input value={settings.ai.provider} readOnly /></label>
          <label>Auto review pull requests<input value={settings.ai.autoReviewPullRequests ? 'Enabled' : 'Disabled'} readOnly /></label>
        </section>
        <SectionTitle icon={<ExternalLink size={22} />} title="Preview environments" />
        <section className="panel form-panel">
          <label>Domain<input value={settings.preview.domain} readOnly /></label>
          <label>Default TTL<input value={`${settings.preview.defaultTtlDays} days`} readOnly /></label>
          <label>Namespace strategy<input value={settings.preview.namespace} readOnly /></label>
        </section>
      </div>
    </section>
  );
}

function ModalFrame({ title, onClose, children }: { title: string; onClose: () => void; children: React.ReactNode }) {
  return (
    <div className="modal-backdrop" role="presentation" onMouseDown={(event) => event.target === event.currentTarget && onClose()}>
      <section className="modal" role="dialog" aria-modal="true" aria-label={title}>
        <header className="modal-head">
          <h2>{title}</h2>
          <button className="icon-button" onClick={onClose} aria-label="Close"><X size={18} /></button>
        </header>
        {children}
      </section>
    </div>
  );
}

function Comment({ comment }: { comment: CommentDto }) {
  return (
    <article className="comment">
      <div className="avatar small">{initials(comment.author)}</div>
      <div><div className="comment-head"><strong>{comment.author}</strong><time>{relativeTime(comment.createdAt)}</time></div><p>{comment.body}</p></div>
    </article>
  );
}

function AiComment({ comment }: { comment: CommentDto }) {
  return (
    <article className={comment.kind === 'Result' ? 'comment result-comment' : 'ai-comment'}>
      <div className="comment-head"><strong>{comment.author}</strong><span>{comment.kind}</span><time>{relativeTime(comment.createdAt)}</time></div>
      <p>{comment.body}</p>
    </article>
  );
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

function resolveActiveAiModel(settings: SettingsDto, selectedModel: string | null) {
  const options = settings.ai.availableModels.length > 0 ? settings.ai.availableModels : [settings.ai.activeModel];
  return selectedModel && options.includes(selectedModel) ? selectedModel : settings.ai.activeModel;
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

export default App;
