import type { HubConnection } from '@microsoft/signalr';

export type RealtimeEventName =
  | 'workspaceCreated'
  | 'boardChanged'
  | 'boardDeleted'
  | 'workItemChanged'
  | 'workItemDeleted'
  | 'commentAdded'
  | 'commentChanged'
  | 'commentDeleted'
  | 'aiPlanReviewCommentChanged'
  | 'aiPlanReviewCommentDeleted'
  | 'pullRequestReviewCommentChanged'
  | 'pullRequestReviewCommentDeleted'
  | 'aiRunChanged'
  | 'previewChanged'
  | 'implementationRunChanged'
  | 'repositoryCleanupRunChanged'
  | 'epicRunChanged'
  | 'epicGoalChanged';

export type RealtimeEvent = {
  name: RealtimeEventName;
  payload: unknown;
};

export type RealtimeRefreshPlan = {
  patchBoardWorkItem: boolean;
  removeBoardWorkItemId: string | null;
  refreshSelectedWorkItem: boolean;
  refreshShell: boolean;
};

export type RealtimeBoardLike<TItem extends RealtimeWorkItemLike = RealtimeWorkItemLike> = {
  columns: Array<{
    name: string;
    items: TItem[];
  }>;
};

export type RealtimeWorkItemLike = {
  id: string;
  status?: string | null;
  sortOrder?: number | null;
};

export type RealtimeClient = {
  stop: () => Promise<void>;
};

const realtimeEventNames: RealtimeEventName[] = [
  'workspaceCreated',
  'boardChanged',
  'boardDeleted',
  'workItemChanged',
  'workItemDeleted',
  'commentAdded',
  'commentChanged',
  'commentDeleted',
  'aiPlanReviewCommentChanged',
  'aiPlanReviewCommentDeleted',
  'pullRequestReviewCommentChanged',
  'pullRequestReviewCommentDeleted',
  'aiRunChanged',
  'previewChanged',
  'implementationRunChanged',
  'repositoryCleanupRunChanged',
  'epicRunChanged',
  'epicGoalChanged'
];

export function realtimeRefreshPlan(eventName: RealtimeEventName, payload: unknown, activeBoardId: string | null, selectedWorkItemId: string | null): RealtimeRefreshPlan {
  const workItemId = workItemIdFromRealtimePayload(eventName, payload);
  const boardId = boardIdFromRealtimePayload(eventName, payload);
  const isActiveBoardEvent = !boardId || !activeBoardId || boardId === activeBoardId;

  if (!isActiveBoardEvent) return emptyRealtimePlan();

  if (eventName === 'workspaceCreated') {
    return { ...emptyRealtimePlan(), refreshShell: true };
  }

  if (eventName === 'boardChanged' || eventName === 'boardDeleted') {
    return { ...emptyRealtimePlan(), refreshShell: true };
  }

  if (eventName === 'workItemChanged') {
    return {
      ...emptyRealtimePlan(),
      patchBoardWorkItem: true,
      refreshSelectedWorkItem: Boolean(workItemId && selectedWorkItemId === workItemId)
    };
  }

  if (eventName === 'workItemDeleted') {
    return {
      ...emptyRealtimePlan(),
      removeBoardWorkItemId: typeof payload === 'string' ? payload : workItemId,
      refreshSelectedWorkItem: Boolean(workItemId && selectedWorkItemId === workItemId)
    };
  }

  if (workItemId) {
    return {
      ...emptyRealtimePlan(),
      refreshSelectedWorkItem: selectedWorkItemId === workItemId
    };
  }

  if (eventName === 'commentDeleted' || eventName === 'aiPlanReviewCommentDeleted' || eventName === 'pullRequestReviewCommentDeleted') {
    return {
      ...emptyRealtimePlan(),
      refreshSelectedWorkItem: Boolean(selectedWorkItemId)
    };
  }

  return emptyRealtimePlan();
}

export function upsertRealtimeWorkItem<TBoard extends RealtimeBoardLike<TItem>, TItem extends RealtimeWorkItemLike>(board: TBoard, item: TItem): TBoard {
  const targetStatus = item.status ?? board.columns.find((column) => column.items.some((entry) => entry.id === item.id))?.name;
  if (!targetStatus) return board;

  return {
    ...board,
    columns: board.columns.map((column) => {
      const withoutItem = column.items.filter((entry) => entry.id !== item.id);
      if (column.name !== targetStatus) return { ...column, items: withoutItem };

      const insertAt = typeof item.sortOrder === 'number'
        ? Math.min(Math.max(item.sortOrder, 0), withoutItem.length)
        : withoutItem.length;
      const nextItems = [...withoutItem];
      nextItems.splice(insertAt, 0, item);
      return { ...column, items: nextItems.map((entry, index) => ({ ...entry, sortOrder: index })) };
    })
  };
}

export function removeRealtimeWorkItem<TBoard extends RealtimeBoardLike<TItem>, TItem extends RealtimeWorkItemLike>(board: TBoard, workItemId: string): TBoard {
  return {
    ...board,
    columns: board.columns.map((column) => ({
      ...column,
      items: column.items
        .filter((item) => item.id !== workItemId)
        .map((item, index) => ({ ...item, sortOrder: index }))
    }))
  };
}

export function createBoardRealtimeClient(options: {
  boardId: string;
  getAccessToken: () => string | null | Promise<string | null>;
  onEvent: (event: RealtimeEvent) => void;
  onStatus?: (status: 'connecting' | 'connected' | 'reconnecting' | 'disconnected' | 'failed') => void;
}): RealtimeClient {
  let connection: HubConnection | null = null;
  let stopped = false;
  options.onStatus?.('connecting');

  void import('@microsoft/signalr')
    .then(({ HubConnectionBuilder, LogLevel }) => {
      if (stopped) return null;
      connection = new HubConnectionBuilder()
        .withUrl('/hubs/devops', {
          accessTokenFactory: async () => await options.getAccessToken() ?? ''
        })
        .withAutomaticReconnect()
        .configureLogging(LogLevel.Warning)
        .build();

      for (const eventName of realtimeEventNames) {
        connection.on(eventName, (payload) => options.onEvent({ name: eventName, payload }));
      }

      connection.onreconnecting(() => options.onStatus?.('reconnecting'));
      connection.onclose(() => options.onStatus?.('disconnected'));
      connection.onreconnected(() => {
        options.onStatus?.('connected');
        if (connection) void subscribe(connection, options.boardId);
      });

      return connection;
    })
    .then(async (loadedConnection) => {
      if (!loadedConnection || stopped) return;
      await loadedConnection.start();
      if (stopped) {
        await loadedConnection.stop();
        return;
      }
      options.onStatus?.('connected');
      await subscribe(loadedConnection, options.boardId);
    })
    .catch((error) => {
      console.warn('Failed to connect realtime updates', error);
      options.onStatus?.('failed');
    });

  return {
    stop: async () => {
      stopped = true;
      if (connection) {
        await connection.stop();
      }
    }
  };
}

async function subscribe(connection: HubConnection, boardId: string) {
  await connection.invoke('SubscribeBoard', boardId);
  await connection.invoke('SubscribeUser');
}

function emptyRealtimePlan(): RealtimeRefreshPlan {
  return {
    patchBoardWorkItem: false,
    removeBoardWorkItemId: null,
    refreshSelectedWorkItem: false,
    refreshShell: false
  };
}

function workItemIdFromRealtimePayload(eventName: RealtimeEventName, payload: unknown): string | null {
  if (eventName === 'workItemDeleted' && typeof payload === 'string') return payload;
  if (!isRecord(payload)) return null;

  const direct = stringValue(payload.id) ?? stringValue(payload.workItemId) ?? stringValue(payload.rootWorkItemId);
  return direct;
}

function boardIdFromRealtimePayload(eventName: RealtimeEventName, payload: unknown): string | null {
  if (!isRecord(payload)) return null;
  if (eventName === 'boardChanged' || eventName === 'boardDeleted') {
    return stringValue(payload.id);
  }

  return stringValue(payload.boardId);
}

function stringValue(value: unknown): string | null {
  return typeof value === 'string' && value.trim() ? value : null;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
