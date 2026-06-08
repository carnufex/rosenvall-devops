using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Rosenvall.DevOps.Api;

public static class BoardEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/workspaces", (ClaimsPrincipal user, DevOpsStore store) =>
            user.Identity?.IsAuthenticated == true
                ? store.GetWorkspacesForUser(UserIdentityFromClaims(user))
                : store.GetWorkspaces());

        api.MapPost("/workspaces", async (CreateWorkspaceRequest request, ClaimsPrincipal user, DevOpsStore store, IRealtimeNotifier realtime) =>
        {
            if (!CanCreateWorkspaceRequest(store, user))
            {
                return WorkspaceMutationForbidden();
            }

            var actorSubject = UserIdentityFromClaims(user).Subject;
            var workspace = store.CreateWorkspace(request.Name, request.EnvironmentName, request.Region, actorSubject);
            await realtime.PublishUserAsync(actorSubject, "workspaceCreated", workspace);
            return Results.Created($"/api/workspaces/{workspace.Id}", workspace);
        });

        api.MapGet("/workspaces/{workspaceId:guid}/boards", (Guid workspaceId, ClaimsPrincipal user, DevOpsStore store) =>
        {
            var actorSubject = user.Identity?.IsAuthenticated == true
                ? store.GetOrCreateUserWithDemoSandbox(UserIdentityFromClaims(user)).Subject
                : null;
            return store.GetBoards(workspaceId, actorSubject) is { Count: > 0 } boards ? Results.Ok(boards) : Results.NotFound();
        });

        api.MapPost("/workspaces/{workspaceId:guid}/boards", (Guid workspaceId, CreateBoardRequest request, ClaimsPrincipal user, DevOpsStore store) =>
        {
            if (!CanCreateBoardInWorkspaceRequest(store, workspaceId, user))
            {
                return WorkspaceMutationForbidden();
            }

            return store.CreateBoard(workspaceId, request, UserIdentityFromClaims(user).Subject) is { } board ? Results.Created($"/api/boards/{board.Id}", board) : Results.NotFound();
        });

        api.MapGet("/boards/{boardId:guid}", (Guid boardId, ClaimsPrincipal user, DevOpsStore store) =>
        {
            if (!CanViewBoardRequest(store, boardId, user))
            {
                return BoardReadForbidden();
            }

            return store.GetBoard(boardId) is { } board ? Results.Ok(board) : Results.NotFound();
        });

        api.MapPut("/boards/{boardId:guid}/gitops-settings", (Guid boardId, BoardGitOpsSettingsRequest request, ClaimsPrincipal user, DevOpsStore store) =>
        {
            if (!CanMutateBoardRequest(store, boardId, user))
            {
                return BoardMutationForbidden();
            }

            return store.UpsertBoardGitOpsSettings(boardId, request) is { } settings ? Results.Ok(settings) : Results.NotFound();
        });

        api.MapPut("/boards/{boardId:guid}/ai-context", (Guid boardId, BoardAiContextRequest request, ClaimsPrincipal user, DevOpsStore store) =>
        {
            if (!CanMutateBoardRequest(store, boardId, user))
            {
                return BoardMutationForbidden();
            }

            return store.UpsertBoardAiContext(boardId, request) is { } context ? Results.Ok(context) : Results.NotFound();
        });

        api.MapPut("/boards/{boardId:guid}/hosting", (Guid boardId, BoardHostingSettingsRequest request, ClaimsPrincipal user, DevOpsStore store) =>
        {
            if (!CanMutateBoardRequest(store, boardId, user))
            {
                return BoardMutationForbidden();
            }

            return store.UpdateBoardHostingSettings(boardId, request) is { } board ? Results.Ok(board) : Results.NotFound();
        });

        api.MapGet("/boards/{boardId:guid}/timeline", (Guid boardId, ClaimsPrincipal user, DevOpsStore store) =>
        {
            if (!CanViewBoardRequest(store, boardId, user))
            {
                return BoardReadForbidden();
            }

            return store.GetBoard(boardId) is null ? Results.NotFound() : Results.Ok(store.GetTimeline(boardId));
        });
    }

    private static bool CanViewBoardRequest(DevOpsStore store, Guid boardId, ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated != true || store.CanViewBoard(boardId, UserIdentityFromClaims(user).Subject);

    private static bool CanMutateBoardRequest(DevOpsStore store, Guid boardId, ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated != true || store.CanMutateBoard(boardId, UserIdentityFromClaims(user).Subject);

    private static bool CanCreateWorkspaceRequest(DevOpsStore store, ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated != true || store.CanCreateWorkspace(UserIdentityFromClaims(user).Subject);

    private static bool CanCreateBoardInWorkspaceRequest(DevOpsStore store, Guid workspaceId, ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated != true || store.CanCreateBoardInWorkspace(workspaceId, UserIdentityFromClaims(user).Subject);

    private static IResult BoardReadForbidden() =>
        Results.Problem("You do not have permission to view this board.", statusCode: StatusCodes.Status403Forbidden);

    private static IResult BoardMutationForbidden() =>
        Results.Problem("You do not have permission to modify this board.", statusCode: StatusCodes.Status403Forbidden);

    private static IResult WorkspaceMutationForbidden() =>
        Results.Problem("You do not have permission to create workspaces.", statusCode: StatusCodes.Status403Forbidden);

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
