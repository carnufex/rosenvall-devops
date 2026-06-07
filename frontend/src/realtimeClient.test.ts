import assert from 'node:assert/strict';
import test from 'node:test';
import { realtimeRefreshPlan, upsertRealtimeWorkItem, removeRealtimeWorkItem } from './realtimeClient.ts';

const board = {
  id: 'board-1',
  columns: [
    {
      name: 'Todo',
      items: [
        { id: 'task-1', key: 'TASK-1', title: 'One', status: 'Todo', sortOrder: 0 },
        { id: 'task-2', key: 'TASK-2', title: 'Two', status: 'Todo', sortOrder: 1 }
      ]
    },
    { name: 'Done', items: [] }
  ]
};

test('realtime work item changes patch the board and refresh the selected work item only when relevant', () => {
  const plan = realtimeRefreshPlan('workItemChanged', { id: 'task-1' }, 'board-1', 'task-1');

  assert.equal(plan.patchBoardWorkItem, true);
  assert.equal(plan.refreshSelectedWorkItem, true);
  assert.equal(plan.refreshShell, false);
});

test('realtime run changes refresh the open work item without a full shell reload', () => {
  const plan = realtimeRefreshPlan('previewChanged', { workItemId: 'task-1' }, 'board-1', 'task-1');

  assert.equal(plan.patchBoardWorkItem, false);
  assert.equal(plan.refreshSelectedWorkItem, true);
  assert.equal(plan.refreshShell, false);
});

test('realtime board changes use a shell refresh because board metadata can change broadly', () => {
  const plan = realtimeRefreshPlan('boardChanged', { id: 'board-1' }, 'board-1', null);

  assert.equal(plan.refreshShell, true);
  assert.equal(plan.refreshSelectedWorkItem, false);
});

test('upsertRealtimeWorkItem moves a card between status columns', () => {
  const updated = upsertRealtimeWorkItem(board, { id: 'task-1', key: 'TASK-1', title: 'One done', status: 'Done', sortOrder: 0 });

  assert.deepEqual(updated.columns[0].items.map((item) => item.id), ['task-2']);
  assert.deepEqual(updated.columns[1].items.map((item) => item.id), ['task-1']);
  assert.equal(updated.columns[1].items[0]?.title, 'One done');
});

test('removeRealtimeWorkItem removes the card from every column', () => {
  const updated = removeRealtimeWorkItem(board, 'task-1');

  assert.deepEqual(updated.columns[0].items.map((item) => item.id), ['task-2']);
  assert.deepEqual(updated.columns[1].items.map((item) => item.id), []);
});
