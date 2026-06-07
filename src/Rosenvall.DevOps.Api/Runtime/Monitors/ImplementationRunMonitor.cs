using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Rosenvall.DevOps.Api;

public sealed class ImplementationRunMonitor(DevOpsStore store, PipelineJobOrchestrator jobs, IRealtimeNotifier realtime, ILogger<ImplementationRunMonitor> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan RunStuckTimeout = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckImplementationRunsAsync(stoppingToken);
                await CheckRepositoryCleanupRunsAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Implementation run monitor failed.");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task CheckImplementationRunsAsync(CancellationToken cancellationToken)
    {
        foreach (var run in store.GetImplementationRunsAwaitingStatus())
        {
            var detail = store.GetWorkItemDetail(run.WorkItemId);
            if (detail is null)
            {
                continue;
            }

            var jobName = RepositoryImplementationJobManifestRenderer.JobName(run, detail);
            var implementationNamespace = RepositoryImplementationJobManifestRenderer.Namespace;
            using var scope = RunLogScope.BeginImplementationRunScope(logger, run, detail, jobName);
            var logsResult = await jobs.GetOutputAsync(["logs", "-n", implementationNamespace, $"job/{jobName}", "--all-containers", "--tail=160"], cancellationToken);
            var logs = logsResult.Succeeded ? logsResult.Message : string.Empty;
            if (string.IsNullOrWhiteSpace(logs) && DateTimeOffset.UtcNow - run.UpdatedAt > RunStuckTimeout)
            {
                var podName = await FirstKubectlLineAsync(["get", "pods", "-n", implementationNamespace, "-l", $"job-name={jobName}", "-o", "jsonpath={.items[0].metadata.name}"], cancellationToken);
                var jobCondition = await LastJobConditionAsync(implementationNamespace, jobName, cancellationToken);
                var podCondition = string.IsNullOrWhiteSpace(podName)
                    ? null
                    : await PodDiagnosticSummaryAsync(implementationNamespace, podName, cancellationToken);
                var condition = string.Join("; ", new[] { jobCondition, podCondition }.Where(value => !string.IsNullOrWhiteSpace(value)));
                var events = string.IsNullOrWhiteSpace(podName)
                    ? await FirstKubectlLineAsync(["describe", "job", jobName, "-n", implementationNamespace], cancellationToken)
                    : await FirstKubectlLineAsync(["get", "events", "-n", implementationNamespace, "--field-selector", $"involvedObject.name={podName}", "--sort-by=.lastTimestamp", "--no-headers"], cancellationToken);
                var stuck = store.MarkImplementationRunStuck(run.Id, jobName, podName, condition, events);
                if (stuck is not null)
                {
                    await realtime.PublishAsync("implementationRunChanged", stuck, cancellationToken);
                }

                continue;
            }

            var nextStatus = StatusFromLogs(logs, run.Status);

            var jobResult = await jobs.GetOutputAsync(["get", "job", jobName, "-n", implementationNamespace, "-o", "json"], cancellationToken);
            if (jobResult.Succeeded)
            {
                using var document = JsonDocument.Parse(jobResult.Message);
                var succeeded = StatusInt(document.RootElement, "succeeded");
                var failed = StatusInt(document.RootElement, "failed");
                if (succeeded > 0)
                {
                    nextStatus = "PullRequestReady";
                }
                else if (failed > 0)
                {
                    nextStatus = "Failed";
                }
            }
            else if (run.Status is not "Queued")
            {
                logs = string.IsNullOrWhiteSpace(logs) ? jobResult.Message : logs;
                nextStatus = "Failed";
            }

            var failureReason = nextStatus == "Failed"
                ? KubernetesFailureClassifier.Classify(FirstMarkerValue(logs, "RDO_FAILURE=") ?? logsResult.Message ?? "Repository implementation job failed.")
                : null;
            var updated = store.UpdateImplementationRun(run.Id, nextStatus, logs, failureReason);
            if (updated is not null)
            {
                await realtime.PublishAsync("implementationRunChanged", updated, cancellationToken);
            }
        }
    }

    private async Task CheckRepositoryCleanupRunsAsync(CancellationToken cancellationToken)
    {
        foreach (var run in store.GetRepositoryCleanupRunsAwaitingStatus())
        {
            var detail = store.GetWorkItemDetail(run.WorkItemId);
            if (detail is null)
            {
                continue;
            }

            var jobName = RepositoryCleanupJobManifestRenderer.JobName(run, detail);
            var cleanupNamespace = RepositoryCleanupJobManifestRenderer.Namespace;
            using var scope = RunLogScope.BeginRepositoryCleanupRunScope(logger, run, detail, jobName);
            var logsResult = await jobs.GetOutputAsync(["logs", "-n", cleanupNamespace, $"job/{jobName}", "--all-containers", "--tail=160"], cancellationToken);
            var logs = logsResult.Succeeded ? logsResult.Message : string.Empty;
            if (string.IsNullOrWhiteSpace(logs) && DateTimeOffset.UtcNow - run.UpdatedAt > RunStuckTimeout)
            {
                var podName = await FirstKubectlLineAsync(["get", "pods", "-n", cleanupNamespace, "-l", $"job-name={jobName}", "-o", "jsonpath={.items[0].metadata.name}"], cancellationToken);
                var jobCondition = await LastJobConditionAsync(cleanupNamespace, jobName, cancellationToken);
                var podCondition = string.IsNullOrWhiteSpace(podName)
                    ? null
                    : await PodDiagnosticSummaryAsync(cleanupNamespace, podName, cancellationToken);
                var condition = string.Join("; ", new[] { jobCondition, podCondition }.Where(value => !string.IsNullOrWhiteSpace(value)));
                var events = string.IsNullOrWhiteSpace(podName)
                    ? await FirstKubectlLineAsync(["describe", "job", jobName, "-n", cleanupNamespace], cancellationToken)
                    : await FirstKubectlLineAsync(["get", "events", "-n", cleanupNamespace, "--field-selector", $"involvedObject.name={podName}", "--sort-by=.lastTimestamp", "--no-headers"], cancellationToken);
                var stuck = store.MarkRepositoryCleanupRunStuck(run.Id, jobName, podName, condition, events);
                if (stuck is not null)
                {
                    await realtime.PublishAsync("repositoryCleanupRunChanged", stuck, cancellationToken);
                }

                continue;
            }
            var nextStatus = StatusFromLogs(logs, run.Status);

            var jobResult = await jobs.GetOutputAsync(["get", "job", jobName, "-n", cleanupNamespace, "-o", "json"], cancellationToken);
            if (jobResult.Succeeded)
            {
                using var document = JsonDocument.Parse(jobResult.Message);
                var succeeded = StatusInt(document.RootElement, "succeeded");
                var failed = StatusInt(document.RootElement, "failed");
                if (succeeded > 0)
                {
                    nextStatus = "PullRequestReady";
                }
                else if (failed > 0)
                {
                    nextStatus = "Failed";
                }
            }
            else if (run.Status is not "Queued")
            {
                logs = string.IsNullOrWhiteSpace(logs) ? jobResult.Message : logs;
                nextStatus = "Failed";
            }

            var failureReason = nextStatus == "Failed"
                ? KubernetesFailureClassifier.Classify(FirstMarkerValue(logs, "RDO_FAILURE=") ?? logsResult.Message ?? "Repository cleanup job failed.")
                : null;
            var updated = store.UpdateRepositoryCleanupRun(run.Id, nextStatus, logs, failureReason);
            if (updated is not null)
            {
                await realtime.PublishAsync("repositoryCleanupRunChanged", updated, cancellationToken);
            }
        }
    }

    private async Task<string?> FirstKubectlLineAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await jobs.GetOutputAsync(arguments, cancellationToken);
        return result.Succeeded
            ? result.Message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault()
            : result.Message;
    }

    private async Task<string?> LastJobConditionAsync(string cleanupNamespace, string jobName, CancellationToken cancellationToken)
    {
        var jobResult = await jobs.GetOutputAsync(["get", "job", jobName, "-n", cleanupNamespace, "-o", "json"], cancellationToken);
        if (!jobResult.Succeeded)
        {
            return jobResult.Message;
        }

        using var document = JsonDocument.Parse(jobResult.Message);
        if (!document.RootElement.TryGetProperty("status", out var status) ||
            !status.TryGetProperty("conditions", out var conditions) ||
            conditions.ValueKind != JsonValueKind.Array)
        {
            return "No Job condition was reported.";
        }

        return conditions.EnumerateArray()
            .Select(condition =>
            {
                var type = condition.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
                var reason = condition.TryGetProperty("reason", out var reasonElement) ? reasonElement.GetString() : null;
                var message = condition.TryGetProperty("message", out var messageElement) ? messageElement.GetString() : null;
                return string.Join(" - ", new[] { type, reason, message }.Where(value => !string.IsNullOrWhiteSpace(value)));
            })
            .LastOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private async Task<string?> PodDiagnosticSummaryAsync(string podNamespace, string podName, CancellationToken cancellationToken)
    {
        var podResult = await jobs.GetOutputAsync(["get", "pod", podName, "-n", podNamespace, "-o", "json"], cancellationToken);
        if (!podResult.Succeeded)
        {
            return podResult.Message;
        }

        using var document = JsonDocument.Parse(podResult.Message);
        if (!document.RootElement.TryGetProperty("status", out var status))
        {
            return null;
        }

        var parts = new List<string>();
        if (status.TryGetProperty("phase", out var phase) && !string.IsNullOrWhiteSpace(phase.GetString()))
        {
            parts.Add($"Pod phase: {phase.GetString()}");
        }

        AddContainerDiagnostics(parts, status, "initContainerStatuses", "init");
        AddContainerDiagnostics(parts, status, "containerStatuses", "container");
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    private static void AddContainerDiagnostics(List<string> parts, JsonElement status, string propertyName, string label)
    {
        if (!status.TryGetProperty(propertyName, out var statuses) || statuses.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var container in statuses.EnumerateArray())
        {
            var name = container.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : "unknown";
            if (!container.TryGetProperty("state", out var state))
            {
                continue;
            }

            foreach (var stateName in new[] { "waiting", "terminated", "running" })
            {
                if (!state.TryGetProperty(stateName, out var stateValue))
                {
                    continue;
                }

                var reason = stateValue.TryGetProperty("reason", out var reasonElement) ? reasonElement.GetString() : null;
                var message = stateValue.TryGetProperty("message", out var messageElement) ? messageElement.GetString() : null;
                var detail = string.Join(" - ", new[] { reason, message }.Where(value => !string.IsNullOrWhiteSpace(value)));
                parts.Add(string.IsNullOrWhiteSpace(detail)
                    ? $"{label} {name} {stateName}"
                    : $"{label} {name} {stateName}: {detail}");
                break;
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

    private static string StatusFromLogs(string logs, string currentStatus)
    {
        if (logs.Contains("RDO_STEP=PullRequestReady", StringComparison.OrdinalIgnoreCase))
        {
            return "PullRequestReady";
        }

        if (logs.Contains("RDO_STEP=Pushing", StringComparison.OrdinalIgnoreCase))
        {
            return "Pushing";
        }

        if (logs.Contains("RDO_STEP=Validating", StringComparison.OrdinalIgnoreCase))
        {
            return "Validating";
        }

        if (logs.Contains("RDO_STEP=WritingPreviewSource", StringComparison.OrdinalIgnoreCase))
        {
            return "WritingPreviewSource";
        }

        if (logs.Contains("RDO_STEP=Testing", StringComparison.OrdinalIgnoreCase))
        {
            return "Testing";
        }

        if (logs.Contains("RDO_STEP=Implementing", StringComparison.OrdinalIgnoreCase))
        {
            return "Implementing";
        }

        if (logs.Contains("RDO_STEP=Inspecting", StringComparison.OrdinalIgnoreCase))
        {
            return "Inspecting";
        }

        if (logs.Contains("RDO_STEP=Cloning", StringComparison.OrdinalIgnoreCase))
        {
            return "Cloning";
        }

        return currentStatus;
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
