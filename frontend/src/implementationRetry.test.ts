import test from 'node:test';
import assert from 'node:assert/strict';
import { implementationActionState, isImplementationRunPendingStatus, repositoryRunPresentation, workflowForRepositoryProfile } from './implementationRetry.ts';

test('labels failed repository implementation as retry and keeps it startable', () => {
  const action = implementationActionState({
    isRepositoryImplementation: true,
    repositoryProfile: 'gitops-homelab',
    repositoryCanRunImplementation: true,
    gitOpsSettingsReady: true,
    hasSelectedPlan: true,
    selectedPlanStatus: 'Approved',
    latestRun: { status: 'Failed', failureReason: 'Codex CLI failed' },
    previewBusy: false,
    previewRunning: false,
    previewHasGeneratedSource: false
  });

  assert.equal(action.canStart, true);
  assert.equal(action.label, 'Retry implementation');
  assert.match(action.retryContext!, /Last run failed: Codex CLI failed/);
});

test('does not offer retry while a repository implementation is pending', () => {
  const action = implementationActionState({
    isRepositoryImplementation: true,
    repositoryProfile: 'gitops-homelab',
    repositoryCanRunImplementation: true,
    gitOpsSettingsReady: true,
    hasSelectedPlan: true,
    selectedPlanStatus: 'Approved',
    latestRun: { status: 'Implementing' },
    previewBusy: false,
    previewRunning: false,
    previewHasGeneratedSource: false
  });

  assert.equal(action.canStart, false);
  assert.equal(action.label, 'Implement in repository');
});

test('hides implementation action when a pull request is ready', () => {
  const action = implementationActionState({
    isRepositoryImplementation: true,
    repositoryProfile: 'gitops-homelab',
    repositoryCanRunImplementation: true,
    gitOpsSettingsReady: true,
    hasSelectedPlan: true,
    selectedPlanStatus: 'Approved',
    latestRun: { status: 'PullRequestReady', pullRequestUrl: 'https://github.com/carnufex/Rosenvalls-Homelab/pull/1' },
    previewBusy: false,
    previewRunning: false,
    previewHasGeneratedSource: false
  });

  assert.equal(action.canStart, false);
});

test('hides retry when any relevant pull request is ready', () => {
  const action = implementationActionState({
    isRepositoryImplementation: true,
    repositoryProfile: 'gitops-homelab',
    repositoryCanRunImplementation: true,
    gitOpsSettingsReady: true,
    hasSelectedPlan: true,
    selectedPlanStatus: 'Approved',
    latestRun: { status: 'Failed' },
    hasReadyPullRequest: true,
    previewBusy: false,
    previewRunning: false,
    previewHasGeneratedSource: false
  });

  assert.equal(action.canStart, false);
  assert.equal(action.label, 'Retry implementation');
});

test('labels failed Unity implementation as Unity retry', () => {
  const action = implementationActionState({
    isRepositoryImplementation: true,
    repositoryProfile: 'unity',
    repositoryCanRunImplementation: true,
    gitOpsSettingsReady: true,
    hasSelectedPlan: true,
    selectedPlanStatus: 'Approved',
    latestRun: { status: 'Failed' },
    previewBusy: false,
    previewRunning: false,
    previewHasGeneratedSource: false
  });

  assert.equal(action.label, 'Retry Unity implementation');
});

test('labels previewable repository plans as build preview', () => {
  const action = implementationActionState({
    workflow: 'preview-then-pr',
    repositoryProfile: 'react-preview',
    repositoryCanRunImplementation: true,
    gitOpsSettingsReady: true,
    hasSelectedPlan: true,
    selectedPlanStatus: 'PlanReady',
    previewBusy: false,
    previewRunning: false,
    previewHasGeneratedSource: false
  });

  assert.equal(action.canStart, true);
  assert.equal(action.label, 'Build preview');
  assert.match(action.helpText, /approve it to create a pull request/);
});

test('workflow helper keeps GitOps direct and React preview gated', () => {
  assert.equal(workflowForRepositoryProfile('gitops-homelab'), 'direct-pr');
  assert.equal(workflowForRepositoryProfile('react-preview'), 'preview-then-pr');
  assert.equal(workflowForRepositoryProfile(null), 'preview-only');
});

test('preview promotion runs use pull request creation presentation', () => {
  const presentation = repositoryRunPresentation('WritingPreviewSource', 'preview-promotion');

  assert.equal(presentation.title, 'Preview approval PR');
  assert.equal(presentation.terminalTitle, 'PR creation log');
  assert.deepEqual(presentation.steps.map((step) => step.title), [
    'Clone repository',
    'Write approved preview source',
    'Validate changed files',
    'Push branch',
    'Pull request ready'
  ]);
  assert.equal(presentation.steps.find((step) => step.key === 'WritingPreviewSource')?.state, 'active');
  assert.equal(presentation.steps.some((step) => step.title === 'Implement with Codex'), false);
  assert.equal(presentation.steps.some((step) => step.title === 'Run checks'), false);
});

test('legacy preview promotion implementing status renders as writing preview source', () => {
  const presentation = repositoryRunPresentation('Implementing', 'preview-promotion');

  assert.equal(presentation.steps.find((step) => step.key === 'WritingPreviewSource')?.state, 'active');
  assert.equal(isImplementationRunPendingStatus('WritingPreviewSource'), true);
});
