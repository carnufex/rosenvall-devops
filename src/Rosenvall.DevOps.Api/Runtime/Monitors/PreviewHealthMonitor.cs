using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Rosenvall.DevOps.Api;

public sealed class PreviewHealthMonitor(DevOpsStore store, PreviewEnvironmentOrchestrator previews, ILogger<PreviewHealthMonitor> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PreviewTimeout = TimeSpan.FromMinutes(3);
    private readonly Dictionary<Guid, DateTimeOffset> _startedAt = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckPendingPreviewsAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Preview health monitor failed.");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task CheckPendingPreviewsAsync(CancellationToken cancellationToken)
    {
        foreach (var preview in store.GetPreviewsAwaitingHealthCheck())
        {
            using var scope = RunLogScope.BeginPreviewScope(logger, preview);
            var isTimedOutRecovery = string.Equals(preview.Status, "Failed", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(preview.FailureReason, "Timeout", StringComparison.OrdinalIgnoreCase);
            if (!_startedAt.ContainsKey(preview.Id))
            {
                _startedAt[preview.Id] = isTimedOutRecovery ? DateTimeOffset.UtcNow : preview.LastCheckedAt ?? DateTimeOffset.UtcNow;
            }

            if (!isTimedOutRecovery && DateTimeOffset.UtcNow - _startedAt[preview.Id] > PreviewTimeout)
            {
                store.UpdatePreviewHealth(preview.WorkItemId, PreviewHealthCheckResult.Failed("Timeout", "Preview did not become healthy within 3 minutes.", null, preview.PodName));
                _startedAt.Remove(preview.Id);
                continue;
            }

            var health = await previews.CheckHealthAsync(preview, cancellationToken);
            if (isTimedOutRecovery && string.Equals(health.Status, "Provisioning", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            store.UpdatePreviewHealth(preview.WorkItemId, health);
            if (!string.Equals(health.Status, "Provisioning", StringComparison.OrdinalIgnoreCase))
            {
                _startedAt.Remove(preview.Id);
            }
        }
    }
}
