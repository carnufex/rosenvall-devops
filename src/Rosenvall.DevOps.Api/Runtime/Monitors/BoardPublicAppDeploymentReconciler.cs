using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Rosenvall.DevOps.Core;

namespace Rosenvall.DevOps.Api;

public sealed class BoardPublicAppDeploymentReconciler(
    DevOpsStore store,
    PreviewEnvironmentOrchestrator previews,
    GitHubRepositoryClient github,
    ForgejoRepositoryClient localGit,
    ILogger<BoardPublicAppDeploymentReconciler> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Board public app deployment reconcile failed.");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        await QueueMergedPreviewPromotionsAsync(cancellationToken);

        foreach (var app in store.GetBoardPublicAppsAwaitingDeployment())
        {
            using var scope = RunLogScope.BeginPublicAppScope(logger, app);
            var manifest = store.RenderBoardPublicAppManifest(app.BoardId);
            if (string.IsNullOrWhiteSpace(manifest))
            {
                store.MarkBoardPublicAppFailed(app.BoardId, "ManifestMissing", "Production app manifest could not be rendered from stored preview source.");
                continue;
            }

            var apply = await previews.ApplyAsync(manifest, cancellationToken);
            if (apply.Succeeded)
            {
                store.MarkBoardPublicAppWaitingForReadiness(app.BoardId, apply.Message);
            }
            else
            {
                store.MarkBoardPublicAppFailed(app.BoardId, "DeployFailed", apply.Message);
            }
        }

        foreach (var app in store.GetBoardPublicAppsAwaitingReadiness())
        {
            using var scope = RunLogScope.BeginPublicAppScope(logger, app);
            var health = await previews.CheckHealthAsync(ToPreviewHealthTarget(app), cancellationToken);
            if (string.Equals(health.Status, "Running", StringComparison.OrdinalIgnoreCase))
            {
                if (app.SourceWorkItemId is { } workItemId && !store.IsPullRequestApproved(workItemId))
                {
                    if (!await MergeLocalGitPullRequestIfNeededAsync(app, workItemId, cancellationToken))
                    {
                        continue;
                    }

                    store.UpdateBoardPublicAppHealth(app.BoardId, health);

                    var previewManifest = store.RenderPreviewManifest(workItemId);
                    if (!string.IsNullOrWhiteSpace(previewManifest))
                    {
                        await previews.DeleteAsync(previewManifest, cancellationToken);
                    }

                    store.ApprovePullRequest(workItemId, "public-app-reconcile");
                }
                else
                {
                    store.UpdateBoardPublicAppHealth(app.BoardId, health);
                }
            }
            else
            {
                store.UpdateBoardPublicAppHealth(app.BoardId, health);
            }
        }
    }

    private async Task<bool> MergeLocalGitPullRequestIfNeededAsync(BoardPublicAppDto app, Guid workItemId, CancellationToken cancellationToken)
    {
        var approvalContext = store.GetPullRequestApprovalContext(workItemId);
        if (approvalContext?.Repository is not { } repository ||
            !repository.Provider.Equals("LocalGit", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var development = approvalContext.Value.Development;
        GitHubPullRequestDto? pullRequest = null;
        if (!string.IsNullOrWhiteSpace(development.PullRequestUrl))
        {
            pullRequest = await localGit.GetPullRequestAsync(repository, development.PullRequestUrl, cancellationToken);
        }

        if (pullRequest is null && development.PullRequestNumber is { } pullRequestNumber)
        {
            pullRequest = await localGit.GetPullRequestAsync(repository, pullRequestNumber, development.PullRequestUrl, cancellationToken);
        }

        if (pullRequest is null)
        {
            var message = "Production app is ready, but the LocalGit pull request could not be read from Forgejo. Retry after Forgejo is available.";
            store.MarkBoardPublicAppFailed(app.BoardId, "MergeFailed", message);
            store.MarkPullRequestMergeState(workItemId, "unknown", false, message);
            return false;
        }

        if (pullRequest.Merged)
        {
            store.MarkPullRequestMergeState(workItemId, "merged", true);
            return true;
        }

        if (!pullRequest.State.Equals("open", StringComparison.OrdinalIgnoreCase))
        {
            var message = $"Production app is ready, but LocalGit pull request is {pullRequest.State} and cannot be merged by RDO.";
            store.MarkBoardPublicAppFailed(app.BoardId, "MergeFailed", message);
            store.MarkPullRequestMergeState(workItemId, pullRequest.State, false, message);
            return false;
        }

        var merged = await localGit.MergePullRequestAsync(pullRequest, cancellationToken);
        if (!merged)
        {
            var message = "Production app is ready, but LocalGit pull request merge failed in Forgejo. Retry after Forgejo is available.";
            store.MarkBoardPublicAppFailed(app.BoardId, "MergeFailed", message);
            store.MarkPullRequestMergeState(workItemId, "merge-failed", false, message);
            return false;
        }

        store.MarkPullRequestMergeState(workItemId, "merged", true);
        return true;
    }

    private static PreviewDto ToPreviewHealthTarget(BoardPublicAppDto app) =>
        new(
            app.BoardId,
            app.SourceWorkItemId ?? Guid.Empty,
            app.Url,
            "",
            app.Status,
            DateTimeOffset.UtcNow.AddDays(1),
            null,
            app.Namespace,
            app.ResourceName,
            "Waiting for app readiness.",
            app.Message);

    private async Task QueueMergedPreviewPromotionsAsync(CancellationToken cancellationToken)
    {
        foreach (var run in store.GetPreviewPromotionRunsAwaitingPublicAppReconcile())
        {
            if (string.IsNullOrWhiteSpace(run.PullRequestUrl))
            {
                continue;
            }

            var repository = store.GetImplementationRunRepository(run.Id);
            if (repository is null)
            {
                continue;
            }

            GitHubPullRequestDto? pullRequest;
            if (repository.Provider.Equals("LocalGit", StringComparison.OrdinalIgnoreCase))
            {
                pullRequest = await localGit.GetPullRequestAsync(repository, run.PullRequestUrl, cancellationToken);
            }
            else
            {
                var integration = store.GetGitHubIntegrationForRepository(repository);
                var token = integration is null ? github.ConfiguredToken : await github.CreateInstallationTokenAsync(integration.InstallationId, cancellationToken);
                if (string.IsNullOrWhiteSpace(token))
                {
                    continue;
                }

                pullRequest = await github.GetPullRequestAsync(run.PullRequestUrl, token, cancellationToken);
            }

            if (pullRequest?.Merged == true)
            {
                var sourceFiles = await ReadDeployablePreviewSourceSnapshotAsync(repository, cancellationToken);
                if (sourceFiles is not { Count: > 0 })
                {
                    logger.LogWarning("Skipping public app deployment for {PullRequestUrl} because merged repository source could not be read.", run.PullRequestUrl);
                    continue;
                }

                store.MarkPullRequestMergeState(run.WorkItemId, "merged", true);
                store.QueueBoardPublicAppDeploymentForPullRequest(run.PullRequestUrl, repository.Provider.Equals("LocalGit", StringComparison.OrdinalIgnoreCase) ? "localgit-reconcile" : "github-reconcile", sourceFiles);
            }
        }
    }

    private async Task<IReadOnlyList<PreviewSourceFile>?> ReadDeployablePreviewSourceSnapshotAsync(RepositoryDto repository, CancellationToken cancellationToken)
    {
        var reference = string.IsNullOrWhiteSpace(repository.DefaultBranch) ? "main" : repository.DefaultBranch;
        if (repository.Provider.Equals("LocalGit", StringComparison.OrdinalIgnoreCase))
        {
            return await DeployablePreviewSourceSnapshotReader.ReadAsync(
                path => localGit.GetSourceTreeAsync(repository, reference, path, cancellationToken),
                path => localGit.GetSourceFileAsync(repository, reference, path, cancellationToken));
        }

        if (!repository.Provider.Equals("GitHub", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var integration = store.GetGitHubIntegrationForRepository(repository);
        var token = integration is null
            ? github.ConfiguredToken
            : await github.CreateInstallationTokenAsync(integration.InstallationId, cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        return await DeployablePreviewSourceSnapshotReader.ReadAsync(
            path => github.GetSourceTreeAsync(repository, reference, path, token, cancellationToken),
            path => github.GetSourceFileAsync(repository, reference, path, token, cancellationToken));
    }
}
