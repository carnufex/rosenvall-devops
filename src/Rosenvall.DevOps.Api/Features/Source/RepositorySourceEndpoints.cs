using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Rosenvall.DevOps.Api;

public static class RepositorySourceEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/boards/{boardId:guid}/source/repositories", (Guid boardId, ClaimsPrincipal user, DevOpsStore store) =>
        {
            if (!CanViewBoardRequest(store, boardId, user))
            {
                return BoardReadForbidden();
            }

            var repositories = RepositorySourceFeature.BuildBoardSourceRepositories(store.GetBoardRepositories(boardId));
            return Results.Ok(repositories);
        });

        api.MapGet("/repositories/{repositoryId:guid}/source/tree", async (Guid repositoryId, string? @ref, string? path, ClaimsPrincipal user, DevOpsStore store, ForgejoRepositoryClient localGit, GitHubRepositoryClient github, CancellationToken cancellationToken) =>
        {
            if (!CanViewRepositoryRequest(store, repositoryId, user))
            {
                return BoardReadForbidden();
            }

            var repository = store.GetRepository(repositoryId);
            if (repository is null)
            {
                return Results.NotFound();
            }

            var reference = RepositorySourceFeature.NormalizeSourceRef(@ref, repository.DefaultBranch);
            if (string.IsNullOrWhiteSpace(reference))
            {
                return RepositorySourceFeature.InvalidSourceRefProblem();
            }

            var sourcePath = RepositorySourceFeature.NormalizeSourcePath(path);
            if (!string.IsNullOrWhiteSpace(path) && string.IsNullOrWhiteSpace(sourcePath))
            {
                return RepositorySourceFeature.Problem(RepositorySourceFeature.InvalidSourcePath());
            }

            if (repository.Provider.Equals("LocalGit", StringComparison.OrdinalIgnoreCase))
            {
                return await RepositorySourceFeature.ReadResultAsync(
                    repository.Provider,
                    () => localGit.GetSourceTreeAsync(repository, reference, sourcePath, cancellationToken),
                    cancellationToken);
            }

            if (!repository.Provider.Equals("GitHub", StringComparison.OrdinalIgnoreCase))
            {
                return RepositorySourceFeature.Problem(RepositorySourceFeature.UnsupportedSourceProvider());
            }

            var token = await ResolveGitHubRepositoryReadTokenAsync(store, github, repository, AuthenticatedSubjectOrNull(user), cancellationToken);
            if (string.IsNullOrWhiteSpace(token))
            {
                return RepositorySourceFeature.Problem(RepositorySourceFeature.GitHubSourceUnavailable());
            }

            return await RepositorySourceFeature.ReadResultAsync(
                repository.Provider,
                () => github.GetSourceTreeAsync(repository, reference, sourcePath, token, cancellationToken),
                cancellationToken);
        });

        api.MapGet("/repositories/{repositoryId:guid}/source/file", async (Guid repositoryId, string? @ref, string? path, ClaimsPrincipal user, DevOpsStore store, ForgejoRepositoryClient localGit, GitHubRepositoryClient github, CancellationToken cancellationToken) =>
        {
            if (!CanViewRepositoryRequest(store, repositoryId, user))
            {
                return BoardReadForbidden();
            }

            var repository = store.GetRepository(repositoryId);
            if (repository is null)
            {
                return Results.NotFound();
            }

            var sourcePath = RepositorySourceFeature.NormalizeSourcePath(path);
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                return RepositorySourceFeature.Problem(RepositorySourceFeature.RequiredSourcePath());
            }

            var reference = RepositorySourceFeature.NormalizeSourceRef(@ref, repository.DefaultBranch);
            if (string.IsNullOrWhiteSpace(reference))
            {
                return RepositorySourceFeature.InvalidSourceRefProblem();
            }

            if (repository.Provider.Equals("LocalGit", StringComparison.OrdinalIgnoreCase))
            {
                return await RepositorySourceFeature.ReadResultAsync(
                    repository.Provider,
                    () => localGit.GetSourceFileAsync(repository, reference, sourcePath, cancellationToken),
                    cancellationToken);
            }

            if (!repository.Provider.Equals("GitHub", StringComparison.OrdinalIgnoreCase))
            {
                return RepositorySourceFeature.Problem(RepositorySourceFeature.UnsupportedSourceProvider());
            }

            var token = await ResolveGitHubRepositoryReadTokenAsync(store, github, repository, AuthenticatedSubjectOrNull(user), cancellationToken);
            if (string.IsNullOrWhiteSpace(token))
            {
                return RepositorySourceFeature.Problem(RepositorySourceFeature.GitHubSourceUnavailable());
            }

            return await RepositorySourceFeature.ReadResultAsync(
                repository.Provider,
                () => github.GetSourceFileAsync(repository, reference, sourcePath, token, cancellationToken),
                cancellationToken);
        });

        api.MapGet("/repositories/{repositoryId:guid}/clone-info", (Guid repositoryId, ClaimsPrincipal user, DevOpsStore store) =>
        {
            if (!CanViewRepositoryRequest(store, repositoryId, user))
            {
                return BoardReadForbidden();
            }

            var repository = store.GetRepository(repositoryId);
            if (repository is null)
            {
                return Results.NotFound();
            }

            return Results.Ok(RepositorySourceFeature.BuildCloneInfoDto(repository));
        });
    }

    private static async Task<string?> ResolveGitHubRepositoryReadTokenAsync(DevOpsStore store, GitHubRepositoryClient github, RepositoryDto repository, string? actorSubject, CancellationToken cancellationToken)
    {
        if (!repository.Provider.Equals("GitHub", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (store.GetGitHubIntegrationForRepository(repository) is { } integration)
        {
            if (!string.IsNullOrWhiteSpace(actorSubject) && !store.CanUseGitHubInstallation(integration.InstallationId, actorSubject))
            {
                return null;
            }

            return await github.CreateInstallationTokenAsync(integration.InstallationId, cancellationToken);
        }

        return github.ConfiguredToken;
    }

    private static bool CanViewBoardRequest(DevOpsStore store, Guid boardId, ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated != true || store.CanViewBoard(boardId, UserIdentityFromClaims(user).Subject);

    private static bool CanViewRepositoryRequest(DevOpsStore store, Guid repositoryId, ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated != true || store.CanViewRepository(repositoryId, UserIdentityFromClaims(user).Subject);

    private static IResult BoardReadForbidden() =>
        Results.Problem("You do not have permission to view this board.", statusCode: StatusCodes.Status403Forbidden);

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
