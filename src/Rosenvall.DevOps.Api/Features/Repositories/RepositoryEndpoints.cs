using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Rosenvall.DevOps.Api;

public static class RepositoryEndpoints
{
    private const string GitHubOrganizationRepositoryCreationDisabledMessage = "Organization repository creation is not enabled yet. Link an existing repository instead.";

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/repositories", (ClaimsPrincipal user, DevOpsStore store) => store.GetRepositories(AuthenticatedSubjectOrNull(user)));

        api.MapPost("/repositories", (CreateRepositoryRequest request, ClaimsPrincipal user, DevOpsStore store) =>
        {
            if (!CanCreateRepositoryRequest(store, user))
            {
                return RepositoryMutationForbidden();
            }

            var repository = store.CreateRepository(request);
            return Results.Created($"/api/repositories/{repository.Id}", repository);
        });

        api.MapPost("/repositories/github/onboarding-draft", async (GitHubRepositoryOnboardingDraftRequest request, RepositoryOnboardingDraftProvider onboarding, CancellationToken cancellationToken) =>
        {
            return Results.Ok(await onboarding.CreateDraftAsync(request, cancellationToken));
        });

        api.MapPost("/boards/{boardId:guid}/repositories", (Guid boardId, LinkBoardRepositoryRequest request, ClaimsPrincipal user, DevOpsStore store) =>
        {
            if (!CanMutateBoardRequest(store, boardId, user))
            {
                return BoardMutationForbidden();
            }

            return store.LinkRepositoryToBoard(boardId, request) is { } board ? Results.Ok(board) : Results.NotFound();
        });

        api.MapPost("/boards/{boardId:guid}/repositories/github", (Guid boardId, SyncGitHubRepositoryRequest request, ClaimsPrincipal user, DevOpsStore store) =>
        {
            if (!CanMutateBoardRequest(store, boardId, user))
            {
                return BoardMutationForbidden();
            }

            if (request.RepositoryId is { } repositoryId)
            {
                return store.LinkRepositoryToBoard(boardId, new LinkBoardRepositoryRequest(repositoryId, true, request.ImplementationProfile)) is { } linked
                    ? Results.Ok(linked)
                    : Results.NotFound();
            }

            if (!request.CreateNew)
            {
                if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.RemoteUrl))
                {
                    return Results.Problem("Linking an existing GitHub repository requires a repository name and clone URL.", statusCode: StatusCodes.Status400BadRequest);
                }

                var linkedRepository = store.CreateRepository(new CreateRepositoryRequest(
                    "GitHub",
                    request.Name,
                    request.RemoteUrl,
                    string.IsNullOrWhiteSpace(request.DefaultBranch) ? "main" : request.DefaultBranch,
                    request.WebUrl,
                    request.Owner,
                    request.ImplementationProfile));
                return store.LinkRepositoryToBoard(boardId, new LinkBoardRepositoryRequest(linkedRepository.Id, true, request.ImplementationProfile ?? linkedRepository.ImplementationProfile)) is { } syncedBoard
                    ? Results.Created($"/api/repositories/{linkedRepository.Id}", syncedBoard)
                    : Results.NotFound();
            }

            return Results.Problem(GitHubOrganizationRepositoryCreationDisabledMessage, statusCode: StatusCodes.Status403Forbidden);
        });

        api.MapPut("/boards/{boardId:guid}/repositories/{repositoryId:guid}/profile", (Guid boardId, Guid repositoryId, RepositoryProfileDto request, ClaimsPrincipal user, DevOpsStore store) =>
        {
            if (!CanMutateBoardRequest(store, boardId, user))
            {
                return BoardMutationForbidden();
            }

            return store.UpsertBoardRepositoryProfile(boardId, repositoryId, request) is { } board ? Results.Ok(board) : Results.NotFound();
        });

        api.MapDelete("/boards/{boardId:guid}/repositories/{repositoryId:guid}", (Guid boardId, Guid repositoryId, ClaimsPrincipal user, DevOpsStore store) =>
        {
            if (!CanMutateBoardRequest(store, boardId, user))
            {
                return BoardMutationForbidden();
            }

            return store.UnlinkRepositoryFromBoard(boardId, repositoryId) ? Results.NoContent() : Results.NotFound();
        });
    }

    private static bool CanMutateBoardRequest(DevOpsStore store, Guid boardId, ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated != true || store.CanMutateBoard(boardId, UserIdentityFromClaims(user).Subject);

    private static bool CanCreateRepositoryRequest(DevOpsStore store, ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated != true || store.CanCreateRepository(UserIdentityFromClaims(user).Subject);

    private static IResult BoardMutationForbidden() =>
        Results.Problem("You do not have permission to modify this board.", statusCode: StatusCodes.Status403Forbidden);

    private static IResult RepositoryMutationForbidden() =>
        Results.Problem("You do not have permission to create repositories for this GitHub installation.", statusCode: StatusCodes.Status403Forbidden);

    private static string? AuthenticatedSubjectOrNull(ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true ? UserIdentityFromClaims(user).Subject : null;

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
