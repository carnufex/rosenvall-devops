import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { apiUnavailableBannerMessage, applicationUrlLabel, approvePullRequestActionLabel, boardDeleteCleanupMessage, boardPublicAppStatusLabel, boardPublicAppUrl, boardRepositoryUrl, boardSyncLabel, buildLocalPullRequestApprovalState, buildOverviewDeliverySummary, buildPreviewLifecycleSteps, buildTimelineFlow, canApproveAiPlanWithComments, canApprovePullRequestWithComments, canCreateRepositoryInInstallation, canSyncBoardToProvider, containedWheelScrollTop, dedupeGeneratedActivityComments, defaultPreviewStepKey, filterTimelineFlowRows, githubUserAuthorizationResultFromUrl, isLocalGitDevelopmentRecord, isPreviewTerminalLive, localGitProviderState, parseUnifiedDiffForContinuousReview, planReviewCommentCountsByRun, previewDisplayMessage, previewStatusMessage, previewStepLogsForDisplay, publicApplicationUrls, pullRequestDisplayLabel, repositoryCreatePermissionMessage, reviewCommentCountsByFile, safeMarkdownHref, shouldRenderPlanReferenceActivity, splitAiPlanReviewBlocks, timelineLaneForKind, unresolvedAiPlanReviewCommentCount, unresolvedReviewCommentCount, workItemAutosaveStatusLabel, workItemModalTabs } from './boardChrome.ts';

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

test('local git development copy stays internal to RDO', () => {
  const localDevelopment = {
    repository: 'demo-app',
    branch: 'rdo/task-1',
    pullRequestUrl: 'http://forgejo.local/rdo/demo-app/pulls/7',
    checksStatus: 'Local pull request ready',
    pullRequestProvider: 'LocalGit',
    pullRequestNumber: 7,
    pullRequestState: 'open'
  };

  assert.equal(isLocalGitDevelopmentRecord(localDevelopment), true);
  assert.equal(pullRequestDisplayLabel(localDevelopment), 'Local pull request #7');
  assert.equal(approvePullRequestActionLabel(localDevelopment), 'Merge local PR and deploy app');
  assert.equal(approvePullRequestActionLabel({ ...localDevelopment, pullRequestProvider: 'GitHub' }), 'Approve PR');
});

test('work item modal tabs are stable and focused', () => {
  assert.deepEqual(workItemModalTabs.map((tab) => tab.key), ['overview', 'ai', 'preview', 'pull-request', 'logs']);
  assert.deepEqual(workItemModalTabs.map((tab) => tab.label), ['Overview', 'AI', 'Preview', 'Pull request', 'Logs']);
});

test('work item autosave status copy is compact and action oriented', () => {
  assert.equal(workItemAutosaveStatusLabel('idle'), 'All changes saved');
  assert.equal(workItemAutosaveStatusLabel('dirty'), 'Unsaved changes');
  assert.equal(workItemAutosaveStatusLabel('saving'), 'Saving...');
  assert.equal(workItemAutosaveStatusLabel('saved'), 'Saved');
  assert.equal(workItemAutosaveStatusLabel('error'), 'Autosave failed');
});

test('ai plan review blocks are stable paragraph and list anchors', () => {
  const blocks = splitAiPlanReviewBlocks(`# Implementation Plan

1. Inspect repository
- Keep Swedish copy compact.

Validate with preview.
`);

  assert.deepEqual(blocks.map((block) => [block.anchorKey, block.kind, block.text]), [
    ['block-1', 'heading', 'Implementation Plan'],
    ['block-2', 'list-item', '1. Inspect repository'],
    ['block-3', 'list-item', 'Keep Swedish copy compact.'],
    ['block-4', 'paragraph', 'Validate with preview.']
  ]);
});

test('ai plan review comments count unresolved comments by run and block approval', () => {
  const comments = [
    { aiRunId: 'plan-1', status: 'open' },
    { aiRunId: 'plan-1', status: 'resolved' },
    { aiRunId: 'plan-2', status: 'open' }
  ];

  assert.equal(unresolvedAiPlanReviewCommentCount(comments, 'plan-1'), 1);
  assert.equal(canApproveAiPlanWithComments('plan-1', comments), false);
  assert.equal(canApproveAiPlanWithComments('plan-1', comments.map((comment) => ({ ...comment, status: 'resolved' }))), true);
  assert.deepEqual(planReviewCommentCountsByRun(comments), {
    'plan-1': { total: 2, unresolved: 1 },
    'plan-2': { total: 1, unresolved: 1 }
  });
});

test('ai plan review uses the collapsible markdown surface', () => {
  const appSource = readFileSync(new URL('./App.tsx', import.meta.url), 'utf8');
  const styles = readFileSync(new URL('./styles.css', import.meta.url), 'utf8');

  assert.match(appSource, /<CommentBody\s+body=\{selectedPlan\.plan\}/);
  assert.match(appSource, /review=\{\{/);
  assert.match(styles, /\.plan-markdown/);
  assert.match(styles, /\.markdown-review-block/);
  assert.doesNotMatch(appSource, /AnnotatedPlanBlockText|annotated-plan-markdown|ai-plan-body|plan-review-document|plan-review-block-group|plan-review-composer/);
  assert.doesNotMatch(styles, /\.annotated-plan|\.ai-plan-body|\.plan-review-document|\.plan-review-block-group|\.plan-review-composer/);
});

test('overview delivery summary is compact and keeps delivery links grouped', () => {
  const summary = buildOverviewDeliverySummary({
    board: {
      id: 'demo',
      name: 'Demo app',
      repository: { provider: 'LocalGit', webUrl: null },
      publicHostname: 'demo.rosenvall.se',
      publicApp: { status: 'Running', url: 'https://demo.rosenvall.se' }
    },
    preview: { status: 'Stopped', message: 'Ready' },
    development: {
      repository: 'demo-app',
      branch: 'rdo/task-1',
      pullRequestUrl: 'http://forgejo.local/rdo/demo-app/pulls/1',
      checksStatus: 'Local pull request merged and deployed',
      pullRequestProvider: 'LocalGit',
      pullRequestNumber: 1,
      pullRequestState: 'closed',
      pullRequestApprovedAt: '2026-05-29T10:00:00Z'
    }
  });

  assert.deepEqual(summary.map((item) => item.key), ['app', 'repository', 'preview', 'pull-request', 'logs']);
  assert.equal(summary[0].value, 'App running');
  assert.equal(summary[1].actionLabel, 'RDO managed');
  assert.equal(summary[3].value, 'Local pull request #1 merged');
});

test('overview delete action shares the comment action row', () => {
  const appSource = readFileSync(new URL('./App.tsx', import.meta.url), 'utf8');

  assert.match(appSource, /className="comment-actions"[\s\S]*delete-action-inline/);
  assert.doesNotMatch(appSource, /overview-danger-actions/);
});

test('continuous pull request diff parser exposes file sections and commentable lines', () => {
  const parsed = parseUnifiedDiffForContinuousReview(`diff --git a/src/App.tsx b/src/App.tsx
index 111..222 100644
--- a/src/App.tsx
+++ b/src/App.tsx
@@ -1,2 +1,3 @@
 import React from 'react';
-const mode = 'light';
+const mode = 'dark';
+const saved = true;
diff --git a/src/main.tsx b/src/main.tsx
new file mode 100644
--- /dev/null
+++ b/src/main.tsx
@@ -0,0 +1 @@
+console.log('ready');
`, [
    { path: 'src/App.tsx', status: 'modified', additions: 2, deletions: 1 },
    { path: 'src/main.tsx', status: 'added', additions: 1, deletions: 0 }
  ]);

  assert.deepEqual(parsed.sections.map((section) => section.path), ['src/App.tsx', 'src/main.tsx']);
  assert.equal(parsed.sections[0].lines.find((line) => line.text.includes("mode = 'dark'"))?.newLine, 2);
  assert.equal(parsed.sections[0].lines.find((line) => line.text.includes("mode = 'light'"))?.oldLine, 2);
  assert.equal(parsed.sections[1].lines.find((line) => line.text.includes('ready'))?.side, 'new');
});

test('review comment helpers count unresolved comments by file and gate approval', () => {
  const comments = [
    { id: '1', filePath: 'src/App.tsx', status: 'open' },
    { id: '2', filePath: 'src/App.tsx', status: 'resolved' },
    { id: '3', filePath: 'src/main.tsx', status: 'open' }
  ];

  assert.equal(unresolvedReviewCommentCount(comments), 2);
  assert.equal(canApprovePullRequestWithComments(comments), false);
  assert.equal(canApprovePullRequestWithComments(comments.map((comment) => ({ ...comment, status: 'resolved' }))), true);
  assert.deepEqual(reviewCommentCountsByFile(comments), {
    'src/App.tsx': { total: 2, unresolved: 1 },
    'src/main.tsx': { total: 1, unresolved: 1 }
  });
});

test('local pull request approval state blocks merged closed and commented prs', () => {
  assert.deepEqual(buildLocalPullRequestApprovalState({ state: 'open', unresolvedComments: 0 }).canApprove, true);

  const merged = buildLocalPullRequestApprovalState({
    state: 'closed',
    pullRequestApprovedAt: '2026-05-29T10:00:00Z',
    pullRequestApprovedBy: 'Demo'
  });
  assert.equal(merged.status, 'merged');
  assert.equal(merged.canApprove, false);
  assert.match(merged.message, /Merged and deployed by Demo/);

  const closed = buildLocalPullRequestApprovalState({ state: 'closed', unresolvedComments: 0 });
  assert.equal(closed.status, 'blocked');
  assert.equal(closed.canApprove, false);
  assert.match(closed.message, /closed/);

  const commented = buildLocalPullRequestApprovalState({ state: 'open', unresolvedComments: 2 });
  assert.equal(commented.status, 'blocked');
  assert.equal(commented.canApprove, false);
  assert.match(commented.message, /Resolve 2 review comments/);
});

test('board delete confirmation includes board-owned local git cleanup but excludes github deletion', () => {
  const message = boardDeleteCleanupMessage('Demo app');

  assert.match(message, /board-owned Local Git repositories/);
  assert.match(message, /GitHub repositories and PRs will remain/);
});

test('local git provider state stays visible when forgejo is unavailable', () => {
  assert.deepEqual(localGitProviderState({
    localGitEnabled: true,
    localGitAvailable: false,
    canCreateRepositories: false,
    localGitMessage: 'Forgejo is not deployed.'
  }), {
    visible: true,
    available: false,
    message: 'Forgejo is not deployed.'
  });

  assert.deepEqual(localGitProviderState({
    localGitEnabled: true,
    localGitAvailable: true,
    canCreateRepositories: true
  }), {
    visible: true,
    available: true,
    message: 'Local Git is ready.'
  });
});

test('stopped preview is shown as historical and not live', () => {
  assert.equal(isPreviewTerminalLive('Stopped'), false);
  assert.equal(previewStatusMessage('Stopped'), 'Preview is stopped. The reviewed source is kept so it can be recreated.');
  assert.equal(previewDisplayMessage('Stopped', 'Deployment is available and at least one preview pod is ready.', 'Ready'), 'Preview is stopped. The reviewed source is kept so it can be recreated.');
  assert.deepEqual(buildPreviewLifecycleSteps('Stopped').map((step) => step.state), ['done', 'done', 'done', 'done']);
});

test('preview step logs expose clickable per-step terminal history', () => {
  const steps = previewStepLogsForDisplay({
    status: 'Applying',
    stepLogs: [
      {
        key: 'source',
        title: 'Implementing preview source',
        description: 'Codex generates files.',
        state: 'done',
        terminalLines: [{ createdAt: '2026-05-28T10:00:00Z', stream: 'agent', message: 'OpenAI Codex started.' }]
      },
      {
        key: 'apply',
        title: 'Applying Kubernetes resources',
        description: 'Apply manifests.',
        state: 'active',
        terminalLines: [{ createdAt: '2026-05-28T10:01:00Z', stream: 'system', message: 'kubectl apply started.' }]
      }
    ]
  });

  assert.equal(defaultPreviewStepKey(steps), 'apply');
  assert.deepEqual(steps.map((step) => step.key), ['source', 'apply', 'readiness', 'running']);
  assert.equal(steps[0].logCount, 1);
  assert.equal(steps[1].terminalLines[0].message, 'kubectl apply started.');
});

test('legacy preview logs fall back to source terminal history', () => {
  const steps = previewStepLogsForDisplay({
    status: 'Implementing',
    terminalLines: [{ createdAt: '2026-05-28T10:00:00Z', stream: 'agent', message: 'OpenAI Codex v0.133.0' }]
  });

  assert.equal(defaultPreviewStepKey(steps), 'source');
  assert.equal(steps[0].logCount, 2);
  assert.match(steps[0].terminalLines[0].message, /Legacy combined log/);
});

test('activity comments dedupe generated Rosenvall AI plan results only', () => {
  const comments = dedupeGeneratedActivityComments([
    { workItemId: 'task-1', author: 'Rosenvall AI', kind: 'Result', body: 'Created plan #1: Implementation Plan', createdAt: '2026-05-28T10:00:00Z' },
    { workItemId: 'task-1', author: 'Rosenvall AI', kind: 'Result', body: 'Created plan #1: Implementation Plan', createdAt: '2026-05-28T10:01:00Z' },
    { workItemId: 'task-1', author: 'Rosenvall AI', kind: 'Result', body: 'Created plan #1: Implementation Plan.', createdAt: '2026-05-28T10:01:30Z' },
    { workItemId: 'task-1', author: 'Christopher', kind: 'Comment', body: 'Created plan #1: Implementation Plan', createdAt: '2026-05-28T10:02:00Z' },
    { workItemId: 'task-1', author: 'Christopher', kind: 'Comment', body: 'Created plan #1: Implementation Plan', createdAt: '2026-05-28T10:03:00Z' }
  ]);

  assert.equal(comments.length, 3);
  assert.equal(comments.filter((comment) => comment.author === 'Christopher').length, 2);
});

test('activity renders plan references only for actual plan creation results', () => {
  assert.equal(shouldRenderPlanReferenceActivity({ kind: 'Result', body: 'Created plan #1: Implementation Plan' }), true);
  assert.equal(shouldRenderPlanReferenceActivity({ kind: 'Result', body: 'AI needs input for plan #2: Missing scope.' }), true);
  assert.equal(shouldRenderPlanReferenceActivity({ kind: 'Result', body: 'Implementing plan #1 with Codex preview source generation.' }), false);
  assert.equal(shouldRenderPlanReferenceActivity({ kind: 'Plan', body: 'Legacy plan body' }), true);
});

test('repo-less board can sync only when provider capability allows it', () => {
  assert.equal(canSyncBoardToProvider({ id: 'preview', name: 'Preview', providerCapabilities: ['preview', 'sync-github'] }), true);
  assert.equal(canSyncBoardToProvider({ id: 'demo', name: 'Demo', repository: { provider: 'Sample' }, providerCapabilities: ['preview'] }), false);
});

test('github repository creation requires a matching personal authorization', () => {
  assert.equal(canCreateRepositoryInInstallation(undefined), false);
  assert.equal(canCreateRepositoryInInstallation({ installationId: 1, accountLogin: 'carnufex' }), false);
  assert.equal(canCreateRepositoryInInstallation({ installationId: 1, accountLogin: 'carnufex', canCreateRepositories: true }), true);
  assert.equal(canCreateRepositoryInInstallation({ installationId: 1, accountLogin: 'carnufex', canCreateRepositories: false }), false);
  assert.equal(canCreateRepositoryInInstallation({ installationId: 1, accountLogin: 'carnufex', canCreateRepositories: true, requiresUserAuthorizationForRepositoryCreation: true, hasUserAuthorization: false }), false);
  assert.equal(canCreateRepositoryInInstallation({ installationId: 1, accountLogin: 'carnufex', canCreateRepositories: true, requiresUserAuthorizationForRepositoryCreation: false, hasUserAuthorization: true, authorizedGitHubLogin: 'carnufex' }), true);
  assert.equal(repositoryCreatePermissionMessage({ installationId: 1, accountLogin: 'carnufex', canCreateRepositories: false, requiresUserAuthorizationForRepositoryCreation: true }), 'Authorize GitHub user access before creating repositories under carnufex.');
  assert.equal(repositoryCreatePermissionMessage({ installationId: 1, accountLogin: 'carnufex', canCreateRepositories: false, hasUserAuthorization: true, authorizedGitHubLogin: 'someoneelse' }), 'Connected as someoneelse; repository creation under carnufex is blocked.');
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
  assert.equal(apiUnavailableBannerMessage(new Error('No workspace available for this account.')), null);
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
