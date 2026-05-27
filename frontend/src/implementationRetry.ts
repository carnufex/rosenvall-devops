export type ImplementationRunState = {
  status?: string | null;
  failureReason?: string | null;
  pullRequestUrl?: string | null;
};

export type RepositoryImplementationActionState = {
  canStart: boolean;
  label: string;
  helpText: string;
  retryContext: string | null;
};

export type ImplementationWorkflow = 'direct-pr' | 'preview-then-pr' | 'preview-only';

export function workflowForRepositoryProfile(profile?: string | null, workflow?: string | null): ImplementationWorkflow {
  if (workflow === 'direct-pr' || workflow === 'preview-then-pr' || workflow === 'preview-only') return workflow;
  if (!profile) return 'preview-only';
  if (profile === 'gitops-homelab' || profile === 'unity') return 'direct-pr';
  if (profile === 'react-preview') return 'preview-then-pr';
  return 'direct-pr';
}

export function implementationActionState(options: {
  workflow?: ImplementationWorkflow;
  isRepositoryImplementation?: boolean;
  repositoryProfile: string;
  repositoryCanRunImplementation: boolean;
  gitOpsSettingsReady: boolean;
  hasSelectedPlan: boolean;
  selectedPlanStatus?: string | null;
  latestRun?: ImplementationRunState | null;
  hasPendingRun?: boolean;
  hasReadyPullRequest?: boolean;
  previewBusy: boolean;
  previewRunning: boolean;
  previewHasGeneratedSource: boolean;
}): RepositoryImplementationActionState {
  const workflow = options.workflow ?? (options.isRepositoryImplementation ? 'direct-pr' : 'preview-only');
  const isRepositoryImplementation = workflow === 'direct-pr';
  const isPreviewThenPr = workflow === 'preview-then-pr';
  const pendingRun = options.hasPendingRun ?? isImplementationRunPendingStatus(options.latestRun?.status);
  const hasReadyPullRequest = options.hasReadyPullRequest ?? (options.latestRun?.status === 'PullRequestReady' && !!options.latestRun.pullRequestUrl);
  const retrying = isRepositoryImplementation && options.latestRun?.status === 'Failed';
  const planCanRun = options.hasSelectedPlan && (options.selectedPlanStatus === 'PlanReady' || options.selectedPlanStatus === 'Approved');
  const canStart = planCanRun && (
    isRepositoryImplementation
      ? options.repositoryCanRunImplementation && options.gitOpsSettingsReady && !pendingRun && !hasReadyPullRequest
      : !options.previewBusy && (!options.previewRunning || !options.previewHasGeneratedSource)
  );

  const label = isRepositoryImplementation
    ? retrying
      ? options.repositoryProfile === 'unity'
        ? 'Retry Unity implementation'
        : 'Retry implementation'
      : options.repositoryProfile === 'unity'
        ? 'Implement in Unity repo'
        : 'Implement in GitHub repo'
    : options.selectedPlanStatus === 'Approved'
      ? 'Rebuild with Codex'
      : isPreviewThenPr
        ? 'Build preview'
        : 'Implement plan';

  const helpText = isRepositoryImplementation
    ? !options.repositoryCanRunImplementation
      ? 'Custom URL boards are public clone-only in v1. Connect the repository through the GitHub App to run implementation jobs and create pull requests.'
      : !options.gitOpsSettingsReady
        ? 'GitOps settings are required before this repository can be implemented.'
        : retrying
          ? 'Retry creates a new branch/job attempt from the selected plan.'
          : 'Runs a Kubernetes job that clones the linked repository, asks Codex to make a focused code change, pushes a branch and opens a pull request.'
    : isPreviewThenPr
      ? 'Builds a reviewable preview first. When the preview looks right, approve it to create a pull request from the exact generated source.'
      : 'Uses Codex to generate React/Tailwind preview source from the approved plan and deploys it to Kubernetes.';

  const retryContext = retrying
    ? `Last run failed: ${options.latestRun?.failureReason?.trim() || 'Repository implementation job failed.'} Retry creates a new branch/job attempt from the selected plan.`
    : null;

  return { canStart, label, helpText, retryContext };
}

export function isImplementationRunPendingStatus(status?: string | null) {
  return status === 'Queued' || status === 'Cloning' || status === 'Inspecting' || status === 'Implementing' || status === 'Validating' || status === 'Testing' || status === 'Pushing';
}
