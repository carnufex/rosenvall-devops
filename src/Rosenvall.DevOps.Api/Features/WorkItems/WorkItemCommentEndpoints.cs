using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Rosenvall.DevOps.Api;

public static class WorkItemCommentEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapPost("/work-items/{workItemId:guid}/comments", async (Guid workItemId, AddCommentRequest request, ClaimsPrincipal user, DevOpsStore store, IRealtimeNotifier realtime) =>
        {
            if (!CanMutateWorkItemRequest(store, workItemId, user))
            {
                return BoardMutationForbidden();
            }

            var actor = UserIdentityFromClaims(user);
            var comment = store.AddComment(workItemId, actor.DisplayName, "Comment", request.Body, actor.Subject);
            if (comment is null)
            {
                return Results.NotFound();
            }

            await realtime.PublishAsync("commentAdded", comment);
            return Results.Created($"/api/work-items/{workItemId}", comment);
        });

        api.MapPatch("/comments/{commentId:guid}", async (Guid commentId, UpdateCommentRequest request, ClaimsPrincipal user, DevOpsStore store, IRealtimeNotifier realtime) =>
        {
            if (!CanMutateCommentRequest(store, commentId, user))
            {
                return BoardMutationForbidden();
            }

            try
            {
                var actor = UserIdentityFromClaims(user);
                var comment = store.UpdateComment(commentId, actor.Subject, actor.DisplayName, request.Body);
                if (comment is null)
                {
                    return Results.NotFound();
                }

                await realtime.PublishAsync("commentChanged", comment);
                return Results.Ok(comment);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status403Forbidden);
            }
        });

        api.MapDelete("/comments/{commentId:guid}", async (Guid commentId, ClaimsPrincipal user, DevOpsStore store, IRealtimeNotifier realtime) =>
        {
            if (!CanMutateCommentRequest(store, commentId, user))
            {
                return BoardMutationForbidden();
            }

            try
            {
                var boardId = store.GetCommentBoardId(commentId);
                var actor = UserIdentityFromClaims(user);
                var deleted = store.DeleteComment(commentId, actor.Subject, actor.DisplayName);
                if (!deleted)
                {
                    return Results.NotFound();
                }

                if (boardId is { } scopedBoardId)
                {
                    await realtime.PublishBoardAsync(scopedBoardId, "commentDeleted", commentId);
                }

                return Results.NoContent();
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status403Forbidden);
            }
        });
    }

    private static bool CanMutateWorkItemRequest(DevOpsStore store, Guid workItemId, ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated != true || store.CanMutateWorkItem(workItemId, UserIdentityFromClaims(user).Subject);

    private static bool CanMutateCommentRequest(DevOpsStore store, Guid commentId, ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated != true || store.CanMutateComment(commentId, UserIdentityFromClaims(user).Subject);

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
