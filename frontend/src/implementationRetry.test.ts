import test from 'node:test';
import assert from 'node:assert/strict';
import { implementationActionState } from './implementationRetry.ts';

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
  assert.equal(action.label, 'Implement in GitHub repo');
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
