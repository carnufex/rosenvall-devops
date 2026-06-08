using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Rosenvall.DevOps.Api;

public static class AiPlanningEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/work-items/{workItemId:guid}/ai-session", (Guid workItemId, ClaimsPrincipal user, DevOpsStore store) =>
        {
            if (!CanViewWorkItemRequest(store, workItemId, user))
            {
                return BoardReadForbidden();
            }

            return store.GetAiSession(workItemId) is { } session ? Results.Ok(session) : Results.NotFound();
        });

        api.MapPut("/work-items/{workItemId:guid}/ai-session/provider-session", (Guid workItemId, UpdateAiSessionProviderRequest request, ClaimsPrincipal user, DevOpsStore store) =>
        {
            if (!CanMutateWorkItemRequest(store, workItemId, user))
            {
                return BoardMutationForbidden();
            }

            return store.SetAiSessionProviderSession(workItemId, request.ProviderSessionId) is { } session ? Results.Ok(session) : Results.NotFound();
        });

        api.MapPost("/work-items/{workItemId:guid}/ai-plans/{aiRunId:guid}/comments", async (Guid workItemId, Guid aiRunId, CreateAiPlanReviewCommentRequest request, ClaimsPrincipal user, DevOpsStore store, IRealtimeNotifier realtime) =>
        {
            if (!CanMutateAiRunRequest(store, aiRunId, user) || store.GetAiRun(aiRunId)?.WorkItemId != workItemId)
            {
                return BoardMutationForbidden();
            }

            var comment = store.AddAiPlanReviewComment(workItemId, aiRunId, request, UserIdentityFromClaims(user).DisplayName);
            if (comment is null)
            {
                return Results.NotFound();
            }

            await realtime.PublishAsync("aiPlanReviewCommentChanged", comment);
            return Results.Created($"/api/work-items/{workItemId}/ai-plans/{aiRunId}/comments/{comment.Id}", comment);
        });

        api.MapPatch("/work-items/{workItemId:guid}/ai-plans/{aiRunId:guid}/comments/{commentId:guid}", async (Guid workItemId, Guid aiRunId, Guid commentId, UpdateAiPlanReviewCommentRequest request, ClaimsPrincipal user, DevOpsStore store, IRealtimeNotifier realtime) =>
        {
            if (!CanMutateAiRunRequest(store, aiRunId, user) || store.GetAiRun(aiRunId)?.WorkItemId != workItemId)
            {
                return BoardMutationForbidden();
            }

            var comment = store.UpdateAiPlanReviewComment(workItemId, aiRunId, commentId, request, UserIdentityFromClaims(user).DisplayName);
            if (comment is null)
            {
                return Results.NotFound();
            }

            await realtime.PublishAsync("aiPlanReviewCommentChanged", comment);
            return Results.Ok(comment);
        });

        api.MapDelete("/work-items/{workItemId:guid}/ai-plans/{aiRunId:guid}/comments/{commentId:guid}", async (Guid workItemId, Guid aiRunId, Guid commentId, ClaimsPrincipal user, DevOpsStore store, IRealtimeNotifier realtime) =>
        {
            if (!CanMutateAiRunRequest(store, aiRunId, user) || store.GetAiRun(aiRunId)?.WorkItemId != workItemId)
            {
                return BoardMutationForbidden();
            }

            if (!store.DeleteAiPlanReviewComment(workItemId, aiRunId, commentId))
            {
                return Results.NotFound();
            }

            if (store.GetWorkItemBoardId(workItemId) is { } boardId)
            {
                await realtime.PublishBoardAsync(boardId, "aiPlanReviewCommentDeleted", commentId);
            }

            return Results.NoContent();
        });
    }

    private static bool CanViewWorkItemRequest(DevOpsStore store, Guid workItemId, ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated != true || store.CanViewWorkItem(workItemId, UserIdentityFromClaims(user).Subject);

    private static bool CanMutateWorkItemRequest(DevOpsStore store, Guid workItemId, ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated != true || store.CanMutateWorkItem(workItemId, UserIdentityFromClaims(user).Subject);

    private static bool CanMutateAiRunRequest(DevOpsStore store, Guid aiRunId, ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated != true || store.CanMutateAiRun(aiRunId, UserIdentityFromClaims(user).Subject);

    private static IResult BoardReadForbidden() =>
        Results.Problem("You do not have permission to view this board.", statusCode: StatusCodes.Status403Forbidden);

    private static IResult BoardMutationForbidden() =>
        Results.Problem("You do not have permission to modify this board.", statusCode: StatusCodes.Status403Forbidden);

    private static UserIdentityRequest UserIdentityFromClaims(ClaimsPrincipal user)
    {
        var subject = user.FindFirstValue(ClaimTypes.NameIdentifier) ??
            user.FindFirstValue("sub") ??
            "local-dev";
        var email = user.FindFirstValue(ClaimTypes.Email) ??
            user.FindFirstValue("email") ??
            "christopher.rosenvall@gmail.com";
        var displayName = user.FindFirstValue("name") ??
            user.FindFirstValue("preferred_username") ??
            email;
        var avatar = user.FindFirstValue("picture");
        return new UserIdentityRequest(subject, displayName, email, avatar);
    }
}
