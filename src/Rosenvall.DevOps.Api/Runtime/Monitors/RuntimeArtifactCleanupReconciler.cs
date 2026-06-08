using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Rosenvall.DevOps.Api;

public sealed class RuntimeArtifactCleanupReconciler(
    DevOpsStore store,
    PipelineJobOrchestrator jobs,
    IRuntimeSecretStore runtimeSecrets,
    ILogger<RuntimeArtifactCleanupReconciler> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CleanupRuntimeArtifactsAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Runtime artifact cleanup reconciler failed.");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task CleanupRuntimeArtifactsAsync(CancellationToken cancellationToken)
    {
        foreach (var artifact in store.GetSensitiveRuntimeArtifactsReadyForCleanup())
        {
            var cleanup = await DeleteArtifactAsync(artifact, cancellationToken);
            if (cleanup.Succeeded)
            {
                store.MarkRuntimeArtifactDeleted(artifact.Id, cleanup.Message);
            }
            else
            {
                store.MarkRuntimeArtifactCleanupFailed(artifact.Id, cleanup.Message);
            }
        }
    }

    private Task<PreviewCleanupResult> DeleteArtifactAsync(RuntimeArtifactDto artifact, CancellationToken cancellationToken)
    {
        if (artifact.ApiVersion.Equals("v1", StringComparison.OrdinalIgnoreCase) &&
            artifact.Kind.Equals("Secret", StringComparison.OrdinalIgnoreCase))
        {
            return runtimeSecrets.DeleteAsync(artifact.Name, artifact.Namespace, cancellationToken);
        }

        return jobs.DeleteAsync(RuntimeArtifactCatalog.RenderDeleteManifest(artifact), cancellationToken);
    }
}
