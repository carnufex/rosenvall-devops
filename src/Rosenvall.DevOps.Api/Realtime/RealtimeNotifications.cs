using Microsoft.AspNetCore.SignalR;
using Rosenvall.DevOps.Core;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace Rosenvall.DevOps.Api;

public sealed class DevOpsHub(DevOpsStore store) : Hub
{
    public async Task SubscribeBoard(Guid boardId)
    {
        if (Context.User?.Identity?.IsAuthenticated == true)
        {
            var actorSubject = ActorSubjectFromClaims(Context.User);
            if (!store.CanViewBoard(boardId, actorSubject))
            {
                throw new HubException("You do not have access to this board.");
            }
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeNotifier.BoardGroup(boardId));
    }

    public Task UnsubscribeBoard(Guid boardId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, RealtimeNotifier.BoardGroup(boardId));

    public async Task SubscribeUser()
    {
        var actorSubject = ActorSubjectFromClaims(Context.User ?? new ClaimsPrincipal());
        await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeNotifier.UserGroup(actorSubject));
    }

    private static string ActorSubjectFromClaims(ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.NameIdentifier) ??
        user.FindFirstValue("sub") ??
        "local-dev";
}

public interface IRealtimeNotifier
{
    Task PublishAsync(string method, object? payload, CancellationToken cancellationToken = default);
    Task PublishBoardAsync(Guid boardId, string method, object? payload, CancellationToken cancellationToken = default);
    Task PublishUserAsync(string? actorSubject, string method, object? payload, CancellationToken cancellationToken = default);
}

public sealed class RealtimeNotifier(IHubContext<DevOpsHub> hub, DevOpsStore store) : IRealtimeNotifier
{
    public static string BoardGroup(Guid boardId) => $"board:{boardId:N}";

    public static string UserGroup(string actorSubject) => $"user:{HashSubject(actorSubject)}";

    public async Task PublishAsync(string method, object? payload, CancellationToken cancellationToken = default)
    {
        if (payload is null)
        {
            return;
        }

        var boardId = ResolveBoardId(payload);
        if (boardId is null)
        {
            return;
        }

        await PublishBoardAsync(boardId.Value, method, payload, cancellationToken);
    }

    public Task PublishBoardAsync(Guid boardId, string method, object? payload, CancellationToken cancellationToken = default) =>
        hub.Clients.Group(RealtimeNotifier.BoardGroup(boardId)).SendAsync(method, payload, cancellationToken);

    public Task PublishUserAsync(string? actorSubject, string method, object? payload, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(actorSubject))
        {
            return Task.CompletedTask;
        }

        return hub.Clients.Group(UserGroup(actorSubject)).SendAsync(method, payload, cancellationToken);
    }

    private Guid? ResolveBoardId(object payload) =>
        payload switch
        {
            BoardDto board => board.Id,
            BoardPublicAppDto app => app.BoardId,
            WorkItemSummaryDto item => store.GetWorkItemBoardId(item.Id),
            WorkItemDetailDto detail => detail.Item.Id == Guid.Empty ? null : store.GetWorkItemBoardId(detail.Item.Id),
            PreviewDto preview => store.GetWorkItemBoardId(preview.WorkItemId),
            AiRun run => store.GetWorkItemBoardId(run.WorkItemId),
            CommentDto comment => store.GetWorkItemBoardId(comment.WorkItemId),
            AiPlanReviewCommentDto comment => store.GetWorkItemBoardId(comment.WorkItemId),
            PullRequestReviewCommentDto comment => store.GetWorkItemBoardId(comment.WorkItemId),
            ImplementationRunDto run => store.GetWorkItemBoardId(run.WorkItemId),
            RepositoryCleanupRunDto run => store.GetWorkItemBoardId(run.WorkItemId),
            EpicRunDto run => store.GetWorkItemBoardId(run.RootWorkItemId),
            EpicGoalRunDto run => store.GetWorkItemBoardId(run.RootWorkItemId),
            PipelineRunDto run => run.BoardId ?? (run.WorkItemId is { } workItemId ? store.GetWorkItemBoardId(workItemId) : null),
            _ => null
        };

    private static string HashSubject(string actorSubject)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(actorSubject.Trim()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

public static class RealtimeMode
{
    public sealed record Settings(bool Enabled, bool UnsafeBroadcastsAllowed);

    public static Settings Resolve(IConfiguration configuration, bool authenticationEnabled)
    {
        var enabled = configuration.GetValue("Realtime:Enabled", false);
        var unsafeBroadcastsAllowed = configuration.GetValue("Realtime:AllowUnsafeBroadcasts", false);

        return new Settings(enabled, unsafeBroadcastsAllowed);
    }
}
