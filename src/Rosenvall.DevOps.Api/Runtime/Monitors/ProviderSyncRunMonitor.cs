using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Rosenvall.DevOps.Api;

public sealed class ProviderSyncRunMonitor(DevOpsStore store, PipelineJobOrchestrator jobs, ILogger<ProviderSyncRunMonitor> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(8);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckProviderSyncRunsAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Provider sync run monitor failed.");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task CheckProviderSyncRunsAsync(CancellationToken cancellationToken)
    {
        foreach (var run in store.GetProviderSyncPipelineRunsAwaitingStatus())
        {
            var jobName = RepositoryProviderSyncJobManifestRenderer.JobName(run);
            var jobNamespace = RepositoryImplementationJobManifestRenderer.Namespace;
            using var scope = RunLogScope.BeginProviderSyncRunScope(logger, run, jobName);
            var jobResult = await jobs.GetOutputAsync(["get", "job", jobName, "-n", jobNamespace, "-o", "json"], cancellationToken);
            if (!jobResult.Succeeded)
            {
                if (!string.Equals(run.Status, "Queued", StringComparison.OrdinalIgnoreCase))
                {
                    store.MarkPipelineRunFailed(run.Id, "provider-sync-monitor", jobResult.Message);
                }

                continue;
            }

            var logsResult = await jobs.GetOutputAsync(["logs", "-n", jobNamespace, $"job/{jobName}", "--all-containers", "--tail=160"], cancellationToken);
            var logs = logsResult.Succeeded ? logsResult.Message : string.Empty;
            using var document = JsonDocument.Parse(jobResult.Message);
            var succeeded = StatusInt(document.RootElement, "succeeded");
            var failed = StatusInt(document.RootElement, "failed");
            if (succeeded > 0)
            {
                store.MarkPipelineRunSucceeded(run.Id, "provider-sync-monitor", "Provider sync completed.");
            }
            else if (failed > 0)
            {
                store.MarkPipelineRunFailed(run.Id, "provider-sync-monitor", FirstMarkerValue(logs, "RDO_FAILURE=") ?? logsResult.Message ?? "Provider sync job failed.");
            }
        }
    }

    private static int StatusInt(JsonElement root, string property)
    {
        if (root.TryGetProperty("status", out var status) &&
            status.TryGetProperty(property, out var value) &&
            value.TryGetInt32(out var number))
        {
            return number;
        }

        return 0;
    }

    private static string? FirstMarkerValue(string? logs, string marker)
    {
        if (string.IsNullOrWhiteSpace(logs))
        {
            return null;
        }

        return logs
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.StartsWith(marker, StringComparison.Ordinal))
            .Select(line => line[marker.Length..].Trim())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }
}
