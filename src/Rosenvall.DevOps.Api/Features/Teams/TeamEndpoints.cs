using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Rosenvall.DevOps.Api;

public static class TeamEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/teams", (ClaimsPrincipal user, DevOpsStore store) =>
        {
            var actorSubject = user.Identity?.IsAuthenticated == true
                ? store.GetOrCreateUserWithDemoSandbox(UserIdentityFromClaims(user)).Subject
                : null;
            return store.GetTeams(actorSubject);
        });

        api.MapPost("/teams", (CreateTeamRequest request, ClaimsPrincipal user, DevOpsStore store) =>
        {
            var actor = UserIdentityFromClaims(user);
            store.GetOrCreateUser(actor);
            if (!store.CanCreateTeam(actor.Subject))
            {
                return TeamMutationForbidden();
            }

            var team = store.CreateTeam(request, actor.Subject);
            return Results.Created($"/api/teams/{team.Id}", team);
        });

        api.MapGet("/teams/{teamId:guid}/members", (Guid teamId, ClaimsPrincipal user, DevOpsStore store) =>
        {
            if (!CanViewTeamRequest(store, teamId, user))
            {
                return TeamReadForbidden();
            }

            return store.GetTeams().SingleOrDefault(team => team.Id == teamId)?.Members is { } members ? Results.Ok(members) : Results.NotFound();
        });

        api.MapPut("/teams/{teamId:guid}/members/{userId:guid}", (Guid teamId, Guid userId, UpsertTeamMemberRequest request, ClaimsPrincipal user, DevOpsStore store) =>
        {
            if (!CanMutateTeamRequest(store, teamId, user))
            {
                return TeamMutationForbidden();
            }

            return store.UpsertTeamMember(teamId, request with { UserId = userId }) is { } team ? Results.Ok(team) : Results.NotFound();
        });

        api.MapPost("/teams/{teamId:guid}/members", (Guid teamId, InviteTeamMemberRequest request, ClaimsPrincipal user, DevOpsStore store) =>
        {
            if (!CanMutateTeamRequest(store, teamId, user))
            {
                return TeamMutationForbidden();
            }

            return store.InviteTeamMember(teamId, request) is { } team ? Results.Ok(team) : Results.NotFound();
        });

        api.MapGet("/boards/{boardId:guid}/teams", (Guid boardId, ClaimsPrincipal user, DevOpsStore store) =>
        {
            if (!CanViewBoardRequest(store, boardId, user))
            {
                return BoardReadForbidden();
            }

            return store.GetBoard(boardId) is null ? Results.NotFound() : Results.Ok(store.GetBoardTeamAccess(boardId));
        });

        api.MapPut("/boards/{boardId:guid}/teams/{teamId:guid}", (Guid boardId, Guid teamId, UpsertBoardTeamAccessRequest request, ClaimsPrincipal user, DevOpsStore store) =>
        {
            if (!CanMutateBoardRequest(store, boardId, user))
            {
                return BoardMutationForbidden();
            }

            return store.UpsertBoardTeamAccess(boardId, teamId, request.Role) is { } access ? Results.Ok(access) : Results.NotFound();
        });

        api.MapDelete("/boards/{boardId:guid}/teams/{teamId:guid}", (Guid boardId, Guid teamId, ClaimsPrincipal user, DevOpsStore store) =>
        {
            if (!CanMutateBoardRequest(store, boardId, user))
            {
                return BoardMutationForbidden();
            }

            return store.RemoveBoardTeamAccess(boardId, teamId) ? Results.NoContent() : Results.NotFound();
        });
    }

    private static bool CanViewBoardRequest(DevOpsStore store, Guid boardId, ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated != true || store.CanViewBoard(boardId, UserIdentityFromClaims(user).Subject);

    private static bool CanMutateBoardRequest(DevOpsStore store, Guid boardId, ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated != true || store.CanMutateBoard(boardId, UserIdentityFromClaims(user).Subject);

    private static bool CanViewTeamRequest(DevOpsStore store, Guid teamId, ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated != true || store.CanViewTeam(teamId, UserIdentityFromClaims(user).Subject);

    private static bool CanMutateTeamRequest(DevOpsStore store, Guid teamId, ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated != true || store.CanMutateTeam(teamId, UserIdentityFromClaims(user).Subject);

    private static IResult BoardReadForbidden() =>
        Results.Problem("You do not have permission to view this board.", statusCode: StatusCodes.Status403Forbidden);

    private static IResult BoardMutationForbidden() =>
        Results.Problem("You do not have permission to modify this board.", statusCode: StatusCodes.Status403Forbidden);

    private static IResult TeamReadForbidden() =>
        Results.Problem("You do not have permission to view this team.", statusCode: StatusCodes.Status403Forbidden);

    private static IResult TeamMutationForbidden() =>
        Results.Problem("You do not have permission to modify this team.", statusCode: StatusCodes.Status403Forbidden);

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
