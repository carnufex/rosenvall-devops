using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Rosenvall.DevOps.Api;

public sealed class BoardCleanupRunReconciler(
    DevOpsStore store,
    PreviewEnvironmentOrchestrator previews,
    ForgejoRepositoryClient localGit,
    IConfiguration configuration,
    IRealtimeNotifier realtime,
    ILogger<BoardCleanupRunReconciler> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

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
                logger.LogWarning(ex, "Board cleanup reconciler failed.");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        foreach (var run in store.GetBoardCleanupRunsAwaitingExecution())
        {
            await ProcessRunAsync(run, cancellationToken);
        }
    }

    private async Task ProcessRunAsync(BoardCleanupRunDto run, CancellationToken cancellationToken)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["BoardId"] = run.BoardId,
            ["BoardCleanupRunId"] = run.Id,
            ["BoardCleanupPhase"] = run.Phase
        });

        try
        {
            if (!await DeleteKubernetesResourcesAsync(run, cancellationToken))
            {
                return;
            }

            if (!await DeleteLocalGitRepositoriesAsync(run, cancellationToken))
            {
                return;
            }

            await DeleteBoardMetadataAsync(run, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Board cleanup run failed.");
            FailRun(run, run.Phase, ex.Message);
        }
    }

    private async Task<bool> DeleteKubernetesResourcesAsync(BoardCleanupRunDto run, CancellationToken cancellationToken)
    {
        var updated = store.UpdateBoardCleanupRun(run.Id, "DeletingKubernetes", "Kubernetes", "Rendering Kubernetes cleanup manifest.");
        if (updated is not null)
        {
            await realtime.PublishBoardAsync(updated.BoardId, "boardCleanupRunChanged", updated, cancellationToken);
        }

        var manifest = store.RenderBoardCleanupManifest(run.BoardId, configuration);
        if (manifest is null)
        {
            FailRun(run, "Kubernetes", "Board was not found before cleanup could start.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(manifest))
        {
            updated = store.UpdateBoardCleanupRun(run.Id, "DeletingKubernetes", "Kubernetes", "No Kubernetes resources were recorded for this board.");
            if (updated is not null)
            {
                await realtime.PublishBoardAsync(updated.BoardId, "boardCleanupRunChanged", updated, cancellationToken);
            }

            return true;
        }

        updated = store.UpdateBoardCleanupRun(run.Id, "DeletingKubernetes", "Kubernetes", "Deleting Kubernetes preview, runner, pipeline, secret and public app resources.");
        if (updated is not null)
        {
            await realtime.PublishBoardAsync(updated.BoardId, "boardCleanupRunChanged", updated, cancellationToken);
        }

        var cleanup = await previews.DeleteAsync(manifest, cancellationToken);
        store.AppendBoardCleanupRunLog(run.Id, cleanup.Succeeded ? "system" : "error", cleanup.Message);
        if (!cleanup.Succeeded)
        {
            FailRun(run, "Kubernetes", cleanup.Message);
            return false;
        }

        return true;
    }

    private async Task<bool> DeleteLocalGitRepositoriesAsync(BoardCleanupRunDto run, CancellationToken cancellationToken)
    {
        var repositories = store.GetBoardOwnedLocalGitRepositories(run.BoardId);
        var updated = store.UpdateBoardCleanupRun(
            run.Id,
            "DeletingLocalGit",
            "LocalGit",
            repositories.Count == 0
                ? "No board-owned Local Git repositories were recorded for this board."
                : $"Deleting {repositories.Count} board-owned Local Git repositor{(repositories.Count == 1 ? "y" : "ies")}.");
        if (updated is not null)
        {
            await realtime.PublishBoardAsync(updated.BoardId, "boardCleanupRunChanged", updated, cancellationToken);
        }

        foreach (var repository in repositories)
        {
            store.AppendBoardCleanupRunLog(run.Id, "system", $"Deleting Local Git repository {repository.Owner}/{repository.Name}.");
            var deleted = await localGit.DeleteRepositoryResultAsync(repository, cancellationToken);
            store.AppendBoardCleanupRunLog(run.Id, deleted.Succeeded ? "system" : "error", deleted.Message);
            if (!deleted.Succeeded)
            {
                FailRun(run, "LocalGit", $"Local Git repository {repository.Owner}/{repository.Name} could not be deleted. {deleted.Message}");
                return false;
            }

            store.RecordLocalGitServiceCredentialAudit(
                run.BoardId,
                repository.Id,
                null,
                "LocalGit repository deleted",
                $"Deleted board-owned LocalGit repository {repository.Owner}/{repository.Name} with the RDO service credential.",
                run.Actor,
                repository.WebUrl);
            store.DeleteRepositoryMetadata([repository.Id]);
        }

        return true;
    }

    private async Task DeleteBoardMetadataAsync(BoardCleanupRunDto run, CancellationToken cancellationToken)
    {
        var updated = store.UpdateBoardCleanupRun(run.Id, "DeletingMetadata", "Metadata", "Deleting board metadata from RDO.");
        if (updated is not null)
        {
            await realtime.PublishBoardAsync(updated.BoardId, "boardCleanupRunChanged", updated, cancellationToken);
        }

        if (run.ActionId is { } actionId)
        {
            store.MarkActionCompleted(actionId);
        }

        if (!store.DeleteBoard(run.BoardId, run.Actor))
        {
            FailRun(run, "Metadata", "Board was not found while deleting metadata.");
            return;
        }

        await realtime.PublishBoardAsync(run.BoardId, "boardDeleted", run.BoardId, cancellationToken);
    }

    private void FailRun(BoardCleanupRunDto run, string phase, string failure)
    {
        var updated = store.UpdateBoardCleanupRun(
            run.Id,
            "Failed",
            phase,
            $"Misslyckades att ta bort och köra cleanup, se loggar. {failure}",
            failure,
            completed: true);
        if (run.ActionId is { } actionId)
        {
            store.MarkActionFailed(actionId, failure);
        }

        if (updated is not null)
        {
            logger.LogWarning("Board cleanup failed in phase {Phase}: {Failure}", phase, updated.FailureReason);
        }
    }
}
