using Microsoft.Extensions.Logging;

namespace Rosenvall.DevOps.Api;

public static class RunLogScope
{
    public static IDisposable? BeginImplementationRunScope(ILogger logger, ImplementationRunDto run, WorkItemDetailDto detail, string jobName, string? podName = null) =>
        Begin(
            logger,
            BoardId: detail.BoardContext?.BoardId,
            WorkItemId: run.WorkItemId,
            WorkItemKey: run.WorkItemKey,
            AiRunId: run.AiRunId,
            RunId: run.Id,
            RunKind: run.RunKind,
            Provider: run.PullRequestProvider,
            JobName: jobName,
            PodName: podName ?? run.PodName);

    public static IDisposable? BeginRepositoryCleanupRunScope(ILogger logger, RepositoryCleanupRunDto run, WorkItemDetailDto detail, string jobName, string? podName = null) =>
        Begin(
            logger,
            BoardId: detail.BoardContext?.BoardId,
            WorkItemId: run.WorkItemId,
            WorkItemKey: run.WorkItemKey,
            AiRunId: null,
            RunId: run.Id,
            RunKind: "repository-cleanup",
            Provider: null,
            JobName: jobName,
            PodName: podName ?? run.PodName);

    public static IDisposable? BeginProviderSyncRunScope(ILogger logger, PipelineRunDto run, string jobName, string? podName = null) =>
        Begin(
            logger,
            BoardId: run.BoardId,
            WorkItemId: run.WorkItemId,
            WorkItemKey: null,
            AiRunId: null,
            RunId: run.Id,
            RunKind: "provider-sync",
            Provider: null,
            JobName: jobName,
            PodName: podName);

    public static IDisposable? BeginPreviewScope(ILogger logger, PreviewDto preview, string? podName = null) =>
        Begin(
            logger,
            BoardId: null,
            WorkItemId: preview.WorkItemId,
            WorkItemKey: null,
            AiRunId: null,
            RunId: preview.Id,
            RunKind: "preview",
            Provider: "codex",
            JobName: preview.ResourceName,
            PodName: podName ?? preview.PodName);

    public static IDisposable? BeginPublicAppScope(ILogger logger, BoardPublicAppDto app, string? podName = null) =>
        Begin(
            logger,
            BoardId: app.BoardId,
            WorkItemId: app.SourceWorkItemId,
            WorkItemKey: null,
            AiRunId: null,
            RunId: app.SourceImplementationRunId ?? app.SourcePreviewId ?? app.BoardId,
            RunKind: "public-app",
            Provider: null,
            JobName: app.ResourceName,
            PodName: podName);

    private static IDisposable? Begin(
        ILogger logger,
        Guid? BoardId,
        Guid? WorkItemId,
        string? WorkItemKey,
        Guid? AiRunId,
        Guid? RunId,
        string? RunKind,
        string? Provider,
        string? JobName,
        string? PodName) =>
        logger.BeginScope(new Dictionary<string, object?>
        {
            ["boardId"] = BoardId,
            ["workItemId"] = WorkItemId,
            ["workItemKey"] = WorkItemKey,
            ["aiRunId"] = AiRunId,
            ["runId"] = RunId,
            ["runKind"] = RunKind,
            ["provider"] = Provider,
            ["jobName"] = JobName,
            ["podName"] = PodName
        });
}
