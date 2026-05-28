export type ImplementationRunState = {
  status?: string | null;
  failureReason?: string | null;
  pullRequestUrl?: string | null;
};

export type RepositoryRunStep = {
  key: string;
  title: string;
  description: string;
  state: 'done' | 'blocked' | 'active' | 'pending';
};

export type RepositoryRunPresentation = {
  title: string;
  terminalTitle: string;
  ariaLabel: string;
  steps: RepositoryRunStep[];
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
        : 'Implement in repository'
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
  return status === 'Queued' || status === 'Cloning' || status === 'Inspecting' || status === 'Implementing' || status === 'WritingPreviewSource' || status === 'Validating' || status === 'Testing' || status === 'Pushing';
}

export function repositoryRunPresentation(status: string, runKind?: string | null): RepositoryRunPresentation {
  const previewPromotion = runKind === 'preview-promotion';
  return {
    title: previewPromotion ? 'Preview approval PR' : 'Implementation run',
    terminalTitle: previewPromotion ? 'PR creation log' : 'Implementation log',
    ariaLabel: previewPromotion ? 'Preview approval pull request lifecycle' : 'Repository implementation lifecycle',
    steps: repositoryRunSteps(status, previewPromotion)
  };
}

function repositoryRunSteps(status: string, previewPromotion: boolean): RepositoryRunStep[] {
  const steps = previewPromotion
    ? [
        ['Cloning', 'Clone repository', 'The runner checks out the linked repository and creates a task branch.'],
        ['WritingPreviewSource', 'Write approved preview source', 'The runner writes the exact source files from the reviewed preview.'],
        ['Validating', 'Validate changed files', 'Changed files are checked against the board allowed paths.'],
        ['Pushing', 'Push branch', 'Changes are committed and pushed to the linked repository.'],
        ['PullRequestReady', 'Pull request ready', 'A pull request is available for review.']
      ] as const
    : [
        ['Cloning', 'Clone repository', 'The runner checks out the linked repository and creates a task branch.'],
        ['Inspecting', 'Inspect repository', 'The runner records repository state before Codex starts.'],
        ['Implementing', 'Implement with Codex', 'Codex edits the repository according to the selected plan.'],
        ['Validating', 'Validate scope', 'Changed files are checked against the board allowed paths.'],
        ['Testing', 'Run checks', 'The runner executes lightweight tests or builds when they are discoverable.'],
        ['Pushing', 'Push branch', 'Changes are committed and pushed to the linked repository.'],
        ['PullRequestReady', 'Pull request ready', 'A pull request is available for review.']
      ] as const;

  const fallbackFailedKey = previewPromotion ? 'WritingPreviewSource' : 'Implementing';
  const current = status === 'Queued'
    ? 0
    : status === 'Failed'
      ? Math.max(0, steps.findIndex(([key]) => key === fallbackFailedKey))
      : Math.max(0, steps.findIndex(([key]) => key === status || (previewPromotion && status === 'Implementing' && key === 'WritingPreviewSource')));

  return steps.map(([key, title, description], index) => ({
    key,
    title,
    description,
    state: status === 'PullRequestReady'
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
