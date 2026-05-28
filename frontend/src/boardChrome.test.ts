import test from 'node:test';
import assert from 'node:assert/strict';
import { apiUnavailableBannerMessage, applicationUrlLabel, boardPublicAppStatusLabel, boardPublicAppUrl, boardRepositoryUrl, boardSyncLabel, buildPreviewLifecycleSteps, buildTimelineFlow, canCreateRepositoryInInstallation, canSyncBoardToProvider, containedWheelScrollTop, filterTimelineFlowRows, githubUserAuthorizationResultFromUrl, isPreviewTerminalLive, previewDisplayMessage, previewStatusMessage, publicApplicationUrls, repositoryCreatePermissionMessage, safeMarkdownHref, timelineLaneForKind } from './boardChrome.ts';

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

test('public app link is shown only when board app is running', () => {
  assert.equal(boardPublicAppUrl({
    id: 'clock',
    name: 'Demo Klocka',
    publicHostname: 'demo-klocka.rosenvall.se',
    publicApp: { status: 'Running', url: 'https://demo-klocka.rosenvall.se' }
  }), 'https://demo-klocka.rosenvall.se');

  assert.equal(boardPublicAppStatusLabel({
    id: 'clock',
    name: 'Demo Klocka',
    publicHostname: 'demo-klocka.rosenvall.se',
    publicApp: { status: 'Queued', url: 'https://demo-klocka.rosenvall.se' }
  }), 'App deploying');

  assert.equal(boardPublicAppStatusLabel({
    id: 'clock',
    name: 'Demo Klocka',
    publicHostname: 'demo-klocka.rosenvall.se',
    publicApp: { status: 'Failed', url: 'https://demo-klocka.rosenvall.se' }
  }), 'App failed');

  assert.equal(boardPublicAppUrl({
    id: 'clock',
    name: 'Demo Klocka',
    publicHostname: 'demo-klocka.rosenvall.se',
    publicApp: { status: 'Failed', url: 'https://demo-klocka.rosenvall.se' }
  }), null);

  assert.equal(boardPublicAppStatusLabel({
    id: 'clock',
    name: 'Demo Klocka',
    publicHostname: 'demo-klocka.rosenvall.se'
  }), 'App not deployed');
});

test('stopped preview is shown as historical and not live', () => {
  assert.equal(isPreviewTerminalLive('Stopped'), false);
  assert.equal(previewStatusMessage('Stopped'), 'Preview is stopped. The reviewed source is kept so it can be recreated.');
  assert.equal(previewDisplayMessage('Stopped', 'Deployment is available and at least one preview pod is ready.', 'Ready'), 'Preview is stopped. The reviewed source is kept so it can be recreated.');
  assert.deepEqual(buildPreviewLifecycleSteps('Stopped').map((step) => step.state), ['done', 'done', 'done', 'done']);
});

test('repo-less board can sync only when provider capability allows it', () => {
  assert.equal(canSyncBoardToProvider({ id: 'preview', name: 'Preview', providerCapabilities: ['preview', 'sync-github'] }), true);
  assert.equal(canSyncBoardToProvider({ id: 'demo', name: 'Demo', repository: { provider: 'Sample' }, providerCapabilities: ['preview'] }), false);
});

test('github repository creation is disabled in the chrome helpers', () => {
  assert.equal(canCreateRepositoryInInstallation(undefined), false);
  assert.equal(canCreateRepositoryInInstallation({ installationId: 1, accountLogin: 'carnufex' }), false);
  assert.equal(canCreateRepositoryInInstallation({ installationId: 1, accountLogin: 'carnufex', canCreateRepositories: true }), false);
  assert.equal(canCreateRepositoryInInstallation({ installationId: 1, accountLogin: 'carnufex', canCreateRepositories: false }), false);
  assert.equal(canCreateRepositoryInInstallation({ installationId: 1, accountLogin: 'carnufex', canCreateRepositories: true, requiresUserAuthorizationForRepositoryCreation: true, hasUserAuthorization: false }), false);
  assert.equal(canCreateRepositoryInInstallation({ installationId: 1, accountLogin: 'carnufex', canCreateRepositories: true, requiresUserAuthorizationForRepositoryCreation: true, hasUserAuthorization: true }), false);
  assert.equal(repositoryCreatePermissionMessage({ installationId: 1, accountLogin: 'carnufex', canCreateRepositories: false }), 'GitHub repository creation is disabled. Link an existing repository instead.');
});

test('github user authorization callback result is parsed from URL query', () => {
  assert.deepEqual(githubUserAuthorizationResultFromUrl('https://devops.rosenvall.se/?githubUserAuthorization=connected#settings'), {
    kind: 'success',
    message: 'GitHub user authorization connected.'
  });
  assert.deepEqual(githubUserAuthorizationResultFromUrl('https://devops.rosenvall.se/?githubUserAuthorizationError=Token%20store%20failed#settings'), {
    kind: 'error',
    message: 'Token store failed'
  });
  assert.equal(githubUserAuthorizationResultFromUrl('https://devops.rosenvall.se/#settings'), null);
});

test('api unavailable banner message is persistent and specific for 503 or network loss', () => {
  assert.equal(apiUnavailableBannerMessage(new Error('503 Service Unavailable')), 'API is restarting or unavailable. Latest known board state is still shown.');
  assert.equal(apiUnavailableBannerMessage(new Error('Failed to fetch')), 'API is restarting or unavailable. Latest known board state is still shown.');
  assert.equal(apiUnavailableBannerMessage(new Error('Implementation cannot start because Rosenvall DevOps API is memory pressured.')), 'API is memory pressured. Implementation cannot start until capacity recovers.');
  assert.equal(apiUnavailableBannerMessage(new Error('validation failed')), null);
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

test('markdown links only allow safe schemes', () => {
  assert.equal(safeMarkdownHref('https://github.com/carnufex/rosenvall-devops'), 'https://github.com/carnufex/rosenvall-devops');
  assert.equal(safeMarkdownHref('http://localhost:5173/#board'), 'http://localhost:5173/#board');
  assert.equal(safeMarkdownHref('/api/workspaces'), '/api/workspaces');
  assert.equal(safeMarkdownHref('javascript:alert(1)'), null);
  assert.equal(safeMarkdownHref('data:text/html,<script>alert(1)</script>'), null);
  assert.equal(safeMarkdownHref('//evil.example/path'), null);
});

test('gitops app urls keep only unique http links and produce compact labels', () => {
  const urls = publicApplicationUrls([
    'https://matplan.rosenvall.se/',
    'https://matplan.rosenvall.se/',
    'ftp://matplan.rosenvall.se',
    '',
    'https://headlamp.rosenvall.se/dashboard'
  ]);

  assert.deepEqual(urls, ['https://matplan.rosenvall.se/', 'https://headlamp.rosenvall.se/dashboard']);
  assert.equal(applicationUrlLabel(urls[0]), 'matplan.rosenvall.se');
  assert.equal(applicationUrlLabel(urls[1]), 'headlamp.rosenvall.se/dashboard');
});
