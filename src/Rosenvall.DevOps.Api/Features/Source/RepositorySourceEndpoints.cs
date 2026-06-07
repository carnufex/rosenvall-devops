using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;

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

        api.MapPost("/boards/{boardId:guid}/repositories/sync-to-provider", async (Guid boardId, SyncRepositoryToProviderRequest request, ClaimsPrincipal user, DevOpsStore store, ForgejoRepositoryClient localGit, GitHubRepositoryClient github, GitHubUserAuthorizationTokenStore userTokenStore, IRuntimeSecretStore runtimeSecrets, PipelineJobOrchestrator jobs, IConfiguration configuration, CancellationToken cancellationToken) =>
        {
            if (!CanMutateBoardRequest(store, boardId, user))
            {
                return BoardMutationForbidden();
            }

            var boardRepositories = store.GetBoardRepositories(boardId);
            var source = boardRepositories.SingleOrDefault(link => link.RepositoryId == request.SourceRepositoryId)?.Repository;
            if (source is null)
            {
                return Results.Problem("The selected source repository is not linked to this board.", statusCode: StatusCodes.Status404NotFound);
            }

            var targetProvider = RepositorySourceFeature.NormalizeTargetProvider(request.TargetProvider);
            if (string.IsNullOrWhiteSpace(targetProvider))
            {
                return RepositorySourceFeature.Problem(RepositorySourceFeature.InvalidTargetProvider());
            }

            if (!CanSyncRepositoryToProviderRequest(store, boardId, targetProvider, user))
            {
                return BoardMutationForbidden();
            }

            if (RepositorySourceFeature.SameProvider(source.Provider, targetProvider))
            {
                return Results.Problem("Choose a different target provider for repository sync.", statusCode: StatusCodes.Status400BadRequest);
            }

            var sourceToken = source.Provider.Equals("LocalGit", StringComparison.OrdinalIgnoreCase)
                ? ResolveLocalGitCredential(localGit)
                : await ResolveGitHubRepositoryReadTokenAsync(store, github, source, AuthenticatedSubjectOrNull(user), cancellationToken);
            if (string.IsNullOrWhiteSpace(sourceToken))
            {
                return Results.Problem("Could not resolve source repository credentials for sync.", statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var actorSubject = EffectiveActorSubject(AuthenticatedSubjectOrNull(user));
            var actionKey = RepositorySourceFeature.ProviderSyncActionIdempotencyKey(actorSubject, boardId, source.Id, targetProvider, request.TargetName, request.Private);
            var quota = RepositorySourceFeature.ReadProviderSyncActionQuota(configuration);
            var actionStart = store.StartAction(
                actorSubject,
                boardId,
                null,
                "provider-sync",
                actionKey,
                maxStartsPerActor: quota.Enabled ? quota.MaxStartedPerActor : null,
                quotaWindow: quota.Window,
                quotaOperationKinds: RepositorySourceFeature.ProviderSyncActionKinds);
            if (!actionStart.Started)
            {
                if (IsQuotaExceeded(actionStart))
                {
                    return ActionQuotaExceededResult(actionStart);
                }

                return Results.Conflict(new
                {
                    message = "Provider sync is already queued or running for this request.",
                    operationId = actionStart.Action?.Id,
                    runId = actionStart.Action?.RunId
                });
            }

            RepositoryDto targetTemplate;
            string targetToken;
            if (targetProvider == "LocalGit")
            {
                var localToken = ResolveLocalGitCredential(localGit);
                if (!localGit.IsConfigured() || string.IsNullOrWhiteSpace(localToken))
                {
                    store.MarkActionFailed(actionStart.Action!.Id, "LocalGit is unavailable or missing its service credential.");
                    return Results.Problem("LocalGit is unavailable or missing its service credential.", statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                var creation = await localGit.CreateRepositoryResultAsync(
                    RepositorySourceFeature.BuildLocalGitProviderSyncCreateRequest(request, source),
                    cancellationToken);
                if (!creation.Succeeded || creation.Repository is null)
                {
                    store.MarkActionFailed(actionStart.Action!.Id, creation.Message);
                    return Results.Problem(creation.Message, statusCode: creation.StatusCode is { } status ? (int)status : StatusCodes.Status502BadGateway);
                }

                targetTemplate = creation.Repository;
                targetToken = localToken;
            }
            else
            {
                var installationId = store.GetDefaultGitHubInstallationId(actorSubject);
                if (installationId is null)
                {
                    store.MarkActionFailed(actionStart.Action!.Id, "No personal GitHub installation is available for provider sync.");
                    return Results.Problem("No personal GitHub installation is available for provider sync.", statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                var integration = store.GetGitHubIntegration(installationId.Value);
                if (integration is null || !IsUserAccount(integration))
                {
                    store.MarkActionFailed(actionStart.Action!.Id, GitHubOrganizationRepositoryCreationDisabledMessage);
                    return Results.Problem(GitHubOrganizationRepositoryCreationDisabledMessage, statusCode: StatusCodes.Status403Forbidden);
                }

                var tokenResult = await ResolveRepositoryCreationTokenAsync(store, userTokenStore, integration, actorSubject, cancellationToken);
                if (!tokenResult.Succeeded)
                {
                    store.MarkActionFailed(actionStart.Action!.Id, tokenResult.Message);
                    return Results.Problem(tokenResult.Message, statusCode: tokenResult.StatusCode);
                }

                var creation = await github.CreateRepositoryResultAsync(
                    integration,
                    RepositorySourceFeature.BuildGitHubProviderSyncCreateRequest(installationId.Value, request, source, integration),
                    tokenResult.Token,
                    cancellationToken);
                if (!creation.Succeeded || creation.Repository is null)
                {
                    store.MarkActionFailed(actionStart.Action!.Id, creation.Message);
                    return Results.Problem(creation.Message, statusCode: creation.StatusCode is { } status ? (int)status : StatusCodes.Status502BadGateway);
                }

                targetTemplate = creation.Repository;
                targetToken = tokenResult.Token;
            }

            var target = store.CreateRepository(RepositorySourceFeature.BuildProviderSyncTargetRepositoryCreateRequest(targetTemplate, source));
            _ = store.LinkRepositoryToBoard(boardId, RepositorySourceFeature.BuildProviderSyncBoardLinkRequest(target, source));

            var run = store.RecordPipelineRun(RepositorySourceFeature.BuildProviderSyncPipelineRunRequest(boardId, source, target));
            if (run is null)
            {
                store.MarkBoardRepositorySyncState(boardId, target.Id, "Failed");
                store.MarkActionFailed(actionStart.Action!.Id, "Could not record provider sync run.");
                return Results.NotFound();
            }
            store.MarkActionRun(actionStart.Action!.Id, run.Id, "Queued");

            var secretName = RepositoryProviderSyncJobManifestRenderer.TokenSecretName(run);
            var secretWrite = await runtimeSecrets.StoreAsync(
                secretName,
                RepositoryProviderSyncJobManifestRenderer.TokenSecretData(sourceToken, targetToken),
                RepositoryProviderSyncJobManifestRenderer.TokenSecretLabels(run),
                RepositoryImplementationJobManifestRenderer.Namespace,
                cancellationToken);
            if (!secretWrite.Succeeded)
            {
                var failed = store.MarkPipelineRunFailed(run.Id, "system", secretWrite.Message);
                store.MarkActionFailed(actionStart.Action!.Id, secretWrite.Message);
                return Results.Problem(failed?.Message ?? secretWrite.Message, statusCode: StatusCodes.Status502BadGateway);
            }

            var manifest = RepositoryProviderSyncJobManifestRenderer.Render(
                run,
                source,
                target,
                secretName,
                ForgejoRepositoryClient.RunnerApiBaseUrl(configuration),
                configuration["LocalGit:Username"] ?? configuration["Repositories:Forgejo:Username"] ?? "rdo",
                configuration["Ai:Codex:KubernetesRunnerImage"]);
            var apply = await jobs.ApplyAsync(manifest, cancellationToken);
            if (!apply.Succeeded)
            {
                var failed = store.MarkPipelineRunFailed(run.Id, "system", apply.Message);
                store.MarkActionFailed(actionStart.Action!.Id, apply.Message);
                return Results.Problem(failed?.Message ?? apply.Message, statusCode: StatusCodes.Status502BadGateway);
            }

            var executing = store.MarkPipelineRunExecuting(run.Id, "system") ?? run;
            store.MarkActionRun(actionStart.Action!.Id, executing.Id, "Running");
            return Results.Accepted($"/api/pipeline-runs/{run.Id}", new SyncRepositoryToProviderResponse(target, executing, "Provider sync job queued."));
        });
    }

    private const string GitHubOrganizationRepositoryCreationDisabledMessage = "Organization repository creation is not enabled yet. Link an existing repository instead.";

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

    private static bool CanMutateBoardRequest(DevOpsStore store, Guid boardId, ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated != true || store.CanMutateBoard(boardId, UserIdentityFromClaims(user).Subject);

    private static bool CanSyncRepositoryToProviderRequest(DevOpsStore store, Guid boardId, string targetProvider, ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated != true || store.CanSyncRepositoryToProvider(boardId, targetProvider, UserIdentityFromClaims(user).Subject);

    private static string EffectiveActorSubject(string? actorSubject) =>
        string.IsNullOrWhiteSpace(actorSubject) ? "local-dev" : actorSubject;

    private static IResult BoardReadForbidden() =>
        Results.Problem("You do not have permission to view this board.", statusCode: StatusCodes.Status403Forbidden);

    private static IResult BoardMutationForbidden() =>
        Results.Problem("You do not have permission to modify this board.", statusCode: StatusCodes.Status403Forbidden);

    private static string? AuthenticatedSubjectOrNull(ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true ? UserIdentityFromClaims(user).Subject : null;

    private static string? ResolveLocalGitCredential(ForgejoRepositoryClient localGit) =>
        localGit.ConfiguredToken ?? localGit.ConfiguredPassword;

    private static bool IsQuotaExceeded(ActionStartResultDto startResult) =>
        string.Equals(startResult.BlockReason, ActionLedgerBlockReasons.QuotaExceeded, StringComparison.OrdinalIgnoreCase);

    private static IResult ActionQuotaExceededResult(ActionStartResultDto startResult) =>
        Results.Problem(
            title: "Action quota exceeded",
            detail: startResult.BlockMessage ?? "The actor has reached the quota for this expensive action. Try again later.",
            statusCode: StatusCodes.Status429TooManyRequests,
            extensions: new Dictionary<string, object?>
            {
                ["reason"] = startResult.BlockReason,
                ["retryAfterSeconds"] = startResult.RetryAfterSeconds
            });

    private static bool IsUserAccount(GitHubIntegrationDto integration) =>
        integration.AccountType.Equals("User", StringComparison.OrdinalIgnoreCase);

    private static async Task<RepositoryCreationTokenResult> ResolveRepositoryCreationTokenAsync(DevOpsStore store, GitHubUserAuthorizationTokenStore userTokenStore, GitHubIntegrationDto integration, string actorSubject, CancellationToken cancellationToken)
    {
        if (IsUserAccount(integration))
        {
            var authorization = store.GetGitHubUserAuthorization(integration.InstallationId, actorSubject);
            if (authorization is null)
            {
                return RepositoryCreationTokenResult.Fail($"Authorize GitHub user access before creating repositories under {integration.AccountLogin}.", StatusCodes.Status403Forbidden);
            }

            if (!authorization.GitHubLogin.Equals(integration.AccountLogin, StringComparison.OrdinalIgnoreCase))
            {
                return RepositoryCreationTokenResult.Fail($"This GitHub authorization is connected as {authorization.GitHubLogin}, but the selected installation is {integration.AccountLogin}. Choose your own GitHub installation or authorize that GitHub account.", StatusCodes.Status403Forbidden);
            }

            var userToken = await userTokenStore.ReadAccessTokenAsync(authorization.SecretName, cancellationToken);
            return string.IsNullOrWhiteSpace(userToken)
                ? RepositoryCreationTokenResult.Fail("GitHub user authorization token is unavailable. Reconnect GitHub user authorization before creating repositories.", StatusCodes.Status503ServiceUnavailable)
                : RepositoryCreationTokenResult.Ok(userToken);
        }

        return RepositoryCreationTokenResult.Fail(GitHubOrganizationRepositoryCreationDisabledMessage, StatusCodes.Status403Forbidden);
    }

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
