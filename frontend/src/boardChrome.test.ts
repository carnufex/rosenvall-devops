import test from 'node:test';
import assert from 'node:assert/strict';
import { boardRepositoryUrl, boardSyncLabel, buildTimelineFlow, canSyncBoardToProvider, containedWheelScrollTop, filterTimelineFlowRows, timelineLaneForKind } from './boardChrome.ts';

test('sample board is displayed as demo and has no repository link', () => {
  const board = {
    id: 'demo',
    name: 'Demo Sprint 42',
    repositorySyncState: 'Demo board',
    repository: { provider: 'Sample', webUrl: null }
  };

  assert.equal(boardSyncLabel(board), 'Demo board');
  assert.equal(boardRepositoryUrl(board), null);
});

test('github board exposes a direct repository link', () => {
  const board = {
    id: 'homelab',
    name: 'Rosenvalls-Homelab',
    repositorySyncState: 'GitOps board',
    repository: { provider: 'GitHub', webUrl: 'https://github.com/carnufex/Rosenvalls-Homelab' }
  };

  assert.equal(boardSyncLabel(board), 'GitOps board');
  assert.equal(boardRepositoryUrl(board), 'https://github.com/carnufex/Rosenvalls-Homelab');
});

test('repo-less board can sync only when provider capability allows it', () => {
  assert.equal(canSyncBoardToProvider({ id: 'preview', name: 'Preview', providerCapabilities: ['preview', 'sync-github'] }), true);
  assert.equal(canSyncBoardToProvider({ id: 'demo', name: 'Demo', repository: { provider: 'Sample' }, providerCapabilities: ['preview'] }), false);
});

test('timeline lanes classify implementation, cleanup, preview, pipeline and card events', () => {
  assert.equal(timelineLaneForKind('PullRequestReady'), 'Implementation PR');
  assert.equal(timelineLaneForKind('CleanupFailed'), 'Cleanup');
  assert.equal(timelineLaneForKind('PreviewStarted'), 'Preview');
  assert.equal(timelineLaneForKind('PipelineSucceeded'), 'Pipeline');
  assert.equal(timelineLaneForKind('CardCreated'), 'Card');
});

test('timeline flow groups work item events in chronological lane order', () => {
  const rows = buildTimelineFlow([
    { id: '2', workItemId: 'task-1', kind: 'CleanupQueued', title: 'TASK-1 test', createdAt: '2026-05-25T10:02:00Z' },
    { id: '1', workItemId: 'task-1', kind: 'CardCreated', title: 'TASK-1 test', createdAt: '2026-05-25T10:00:00Z' },
    { id: '3', workItemId: 'task-2', kind: 'PreviewStarted', title: 'TASK-2 preview', createdAt: '2026-05-25T10:01:00Z' }
  ]);

  assert.equal(rows.length, 2);
  assert.deepEqual(rows[0].nodes.map((node) => node.id), ['1', '2']);
  assert.deepEqual(rows[0].nodes.map((node) => node.lane), ['Card', 'Cleanup']);
  assert.equal(rows[1].nodes[0].lane, 'Preview');
});

test('timeline flow row separates topic from task key when title only contains the task key', () => {
  const rows = buildTimelineFlow([
    { id: '1', workItemId: 'task-4824', kind: 'CardCreated', title: 'TASK-4824', message: 'Created test project.', createdAt: '2026-05-25T10:00:00Z' },
    { id: '2', workItemId: 'task-4824', kind: 'ImplementationFailed', title: 'TASK-4824', message: 'Repository implementation job failed.', createdAt: '2026-05-25T10:01:00Z' }
  ]);

  assert.equal(rows[0].topic, 'test project');
  assert.equal(rows[0].taskKey, 'TASK-4824');
});

test('timeline flow search matches topic, task key and node text', () => {
  const rows = buildTimelineFlow([
    { id: '1', workItemId: 'task-4824', kind: 'CardCreated', title: 'TASK-4824', message: 'Created test project.', createdAt: '2026-05-25T10:00:00Z' },
    { id: '2', workItemId: 'task-4826', kind: 'RepositoryCleanupMerged', title: 'TASK-4826', message: 'Adopted cleanup pull request is merged.', createdAt: '2026-05-25T10:01:00Z' }
  ]);

  assert.equal(filterTimelineFlowRows(rows, 'test project').length, 1);
  assert.equal(filterTimelineFlowRows(rows, '4826')[0].taskKey, 'TASK-4826');
  assert.equal(filterTimelineFlowRows(rows, 'cleanup pull')[0].taskKey, 'TASK-4826');
});

test('contained wheel scroll clamps inside flow list bounds', () => {
  assert.equal(containedWheelScrollTop(100, 40, 240), 140);
  assert.equal(containedWheelScrollTop(230, 40, 240), 240);
  assert.equal(containedWheelScrollTop(10, -40, 240), 0);
  assert.equal(containedWheelScrollTop(10, 40, 0), 10);
});
