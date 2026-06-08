using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;

namespace Rosenvall.DevOps.Api;

public static class LocalPullRequestEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/work-items/{workItemId:guid}/pull-request/diff", async (Guid workItemId, ClaimsPrincipal user, DevOpsStore store, ForgejoRepositoryClient localGit, CancellationToken cancellationToken) =>
        {
            if (!CanViewWorkItemRequest(store, workItemId, user))
            {
                return BoardReadForbidden();
            }

            var approvalContext = store.GetPullRequestApprovalContext(workItemId);
            if (approvalContext is null || string.IsNullOrWhiteSpace(approvalContext.Value.Development.PullRequestUrl))
            {
                return Results.NotFound();
            }

            if (approvalContext.Value.Repository is null)
            {
                return Results.Problem("Pull request repository metadata is missing.", statusCode: StatusCodes.Status409Conflict);
            }

            var repository = approvalContext.Value.Repository;
            if (!repository.Provider.Equals("LocalGit", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Problem("RDO-hosted pull request diff is available for Local Git pull requests. Open GitHub pull requests in GitHub.", statusCode: StatusCodes.Status400BadRequest);
            }

            var development = approvalContext.Value.Development;
            var pullRequest = await localGit.GetPullRequestAsync(repository, development.PullRequestUrl!, cancellationToken);
            if (pullRequest is null && development.PullRequestNumber is { } pullRequestNumber)
            {
                pullRequest = await localGit.GetPullRequestAsync(repository, pullRequestNumber, development.PullRequestUrl, cancellationToken);
            }
            if (pullRequest is null)
            {
                return Results.Problem("Local pull request could not be read from Forgejo. Retry after Forgejo is available.", statusCode: StatusCodes.Status502BadGateway);
            }

            IReadOnlyList<PullRequestDiffFileDto> files;
            string diff;
            try
            {
                files = await localGit.GetPullRequestFilesAsync(pullRequest, cancellationToken) ?? [];
                diff = await localGit.GetPullRequestDiffAsync(pullRequest, cancellationToken) ?? "";
            }
            catch (RepositorySourceProviderException ex)
            {
                return RepositorySourceFeature.Problem(RepositorySourceFeature.ProviderRejectedRequest(ex.Provider, ex.StatusCode is { } status ? (int)status : StatusCodes.Status502BadGateway, ex.Detail));
            }
            catch (JsonException ex)
            {
                return RepositorySourceFeature.Problem(RepositorySourceFeature.ProviderBadResponse("LocalGit", ex.Message));
            }
            catch (HttpRequestException ex)
            {
                return RepositorySourceFeature.Problem(RepositorySourceFeature.ProviderUnavailable("LocalGit", ex.Message));
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                return RepositorySourceFeature.Problem(RepositorySourceFeature.ProviderUnavailable("LocalGit", ex.Message));
            }

            const int maxDiffCharacters = 320_000;
            var truncated = diff.Length > maxDiffCharacters;
            if (truncated)
            {
                diff = diff[..maxDiffCharacters];
            }
            var reviewComments = store.GetPullRequestReviewComments(workItemId, "LocalGit", pullRequest.Number);
            var approvalState = store.GetLocalPullRequestApprovalState(workItemId, pullRequest.State);

            return Results.Ok(new PullRequestDiffDto(
                "LocalGit",
                $"{pullRequest.Owner}/{pullRequest.Repository}",
                pullRequest.Number,
                pullRequest.State,
                pullRequest.BaseRef ?? repository.DefaultBranch,
                pullRequest.HeadRef,
                pullRequest.HtmlUrl,
                files.Count,
                files.Sum(file => file.Additions ?? 0),
                files.Sum(file => file.Deletions ?? 0),
                truncated,
                truncated ? "Diff was truncated for display. The pull request still contains the full change." : null,
                files,
                diff,
                reviewComments,
                approvalState.PullRequestApprovedBy,
                approvalState.PullRequestApprovedAt,
                approvalState.PullRequestMergedAt,
                approvalState.PullRequestFailure,
                approvalState.CanApprove,
                approvalState.Status,
                approvalState.Message));
        });

        api.MapPost("/work-items/{workItemId:guid}/pull-request/comments", (Guid workItemId, CreatePullRequestReviewCommentRequest request, ClaimsPrincipal user, DevOpsStore store) =>
        {
            if (!CanMutateWorkItemRequest(store, workItemId, user))
            {
                return BoardMutationForbidden();
            }

            var comment = store.AddPullRequestReviewComment(workItemId, request, UserIdentityFromClaims(user).DisplayName);
            return comment is null
                ? Results.Problem("Review comments can only be added to Local Git pull requests.", statusCode: StatusCodes.Status409Conflict)
                : Results.Ok(comment);
        });

        api.MapPatch("/work-items/{workItemId:guid}/pull-request/comments/{commentId:guid}", (Guid workItemId, Guid commentId, UpdatePullRequestReviewCommentRequest request, ClaimsPrincipal user, DevOpsStore store) =>
        {
            if (!CanMutateWorkItemRequest(store, workItemId, user))
            {
                return BoardMutationForbidden();
            }

            var comment = store.UpdatePullRequestReviewComment(workItemId, commentId, request, UserIdentityFromClaims(user).DisplayName);
            return comment is null ? Results.NotFound() : Results.Ok(comment);
        });

        api.MapDelete("/work-items/{workItemId:guid}/pull-request/comments/{commentId:guid}", (Guid workItemId, Guid commentId, ClaimsPrincipal user, DevOpsStore store) =>
        {
            if (!CanMutateWorkItemRequest(store, workItemId, user))
            {
                return BoardMutationForbidden();
            }

            return store.DeletePullRequestReviewComment(workItemId, commentId) ? Results.NoContent() : Results.NotFound();
        });

        api.MapPost("/work-items/{workItemId:guid}/pull-request/ai-fix-comments", async (Guid workItemId, StartPullRequestReviewFixRequest request, ClaimsPrincipal user, DevOpsStore store, PipelineJobOrchestrator jobs, ForgejoRepositoryClient localGit, IRuntimeSecretStore runtimeSecrets, IConfiguration configuration, IRealtimeNotifier realtime, CancellationToken cancellationToken) =>
        {
            if (!CanMutateWorkItemRequest(store, workItemId, user))
            {
                return BoardMutationForbidden();
            }

            var resourceDiagnostics = ApiResourceDiagnosticsReader.Read(configuration, store.SnapshotDiagnostics);
            var preflight = ImplementationCapacityPreflight.Evaluate(resourceDiagnostics, configuration.GetValue("RepositoryRuns:ApiMemoryMinHeadroomBytes", 128L * 1024 * 1024));
            if (!preflight.Succeeded)
            {
                return Results.Problem(preflight.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var actor = AuditActorFromClaims(user);
            if (store.GetPendingImplementationRun(workItemId) is { } pendingRun)
            {
                return Results.Problem(
                    $"Review comments cannot be fixed because {pendingRun.WorkItemKey} already has a pending implementation run on branch {pendingRun.Branch}.",
                    statusCode: StatusCodes.Status409Conflict);
            }

            var boardId = store.GetWorkItemBoardId(workItemId);
            if (boardId is null)
            {
                return Results.NotFound();
            }

            var actorSubject = EffectiveActorSubject(AuthenticatedSubjectOrNull(user));
            var actionKey = PullRequestReviewFixActionIdempotencyKey(actorSubject, workItemId, request.ReasoningEffort);
            var quota = ReadPullRequestReviewFixActionQuota(configuration);
            var actionStart = store.StartAction(
                actorSubject,
                boardId,
                workItemId,
                "pr-review-fix",
                actionKey,
                blockAfterRunCreation: false,
                maxStartsPerActor: quota.Enabled ? quota.MaxStartedPerActor : null,
                quotaWindow: quota.Window,
                quotaOperationKinds: ActionLedgerBlockReasons.PullRequestReviewFixActionKinds);
            if (!actionStart.Started)
            {
                if (IsQuotaExceeded(actionStart))
                {
                    return ActionQuotaExceededResult(actionStart);
                }

                return Results.Conflict(new
                {
                    message = "Pull request review fix is already queued or running for this request.",
                    operationId = actionStart.Action?.Id,
                    runId = actionStart.Action?.RunId
                });
            }

            ImplementationRunDto? run;
            try
            {
                run = store.StartPullRequestReviewFixRun(workItemId, request, actor);
            }
            catch (InvalidOperationException ex)
            {
                store.MarkActionFailed(actionStart.Action!.Id, ex.Message);
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
            }

            if (run is null)
            {
                store.MarkActionFailed(actionStart.Action!.Id, "Work item or local pull request was not found.");
                return Results.NotFound();
            }
            store.MarkActionRun(actionStart.Action!.Id, run.Id, "Queued");

            await realtime.PublishAsync("implementationRunChanged", run);
            var localGitCredential = localGit.ConfiguredToken ?? localGit.ConfiguredPassword;
            if (string.IsNullOrWhiteSpace(localGitCredential))
            {
                var failed = store.UpdateImplementationRun(run.Id, "Failed", failureReason: "Could not resolve Local Git credentials for review fix.");
                store.MarkActionFailed(actionStart.Action!.Id, failed?.FailureReason ?? "Could not resolve Local Git credentials for review fix.");
                await realtime.PublishAsync("implementationRunChanged", failed);
                return Results.Problem(failed?.FailureReason ?? "Could not resolve Local Git credentials.", statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var secretName = RepositoryImplementationJobManifestRenderer.RepositoryTokenSecretName(run);
            var tokenSecretWrite = await runtimeSecrets.StoreAsync(
                secretName,
                RepositoryImplementationJobManifestRenderer.TokenSecretData(localGitCredential),
                RepositoryImplementationJobManifestRenderer.TokenSecretLabels(run),
                RepositoryImplementationJobManifestRenderer.Namespace,
                cancellationToken);
            if (!tokenSecretWrite.Succeeded)
            {
                var failure = KubernetesFailureClassifier.Classify(tokenSecretWrite.Message);
                var failed = store.UpdateImplementationRun(run.Id, "Failed", tokenSecretWrite.Message, failure);
                store.MarkActionFailed(actionStart.Action!.Id, failure);
                await realtime.PublishAsync("implementationRunChanged", failed);
                return Results.Problem(failure, statusCode: StatusCodes.Status502BadGateway);
            }

            var manifest = store.RenderImplementationRunManifest(run.Id, configuration, secretName);
            if (manifest is null)
            {
                var failed = store.UpdateImplementationRun(run.Id, "Failed", failureReason: "Review fix manifest could not be rendered.");
                store.MarkActionFailed(actionStart.Action!.Id, failed?.FailureReason ?? "Review fix manifest could not be rendered.");
                return Results.Problem(failed?.FailureReason ?? "Review fix manifest could not be rendered.", statusCode: StatusCodes.Status409Conflict);
            }

            var apply = await jobs.ApplyAsync(manifest, cancellationToken);
            if (!apply.Succeeded)
            {
                var failure = KubernetesFailureClassifier.Classify(apply.Message);
                var failed = store.UpdateImplementationRun(run.Id, "Failed", apply.Message, failure);
                store.MarkActionFailed(actionStart.Action!.Id, failure);
                await realtime.PublishAsync("implementationRunChanged", failed);
                return Results.Problem(failure, statusCode: StatusCodes.Status502BadGateway);
            }

            var updated = store.UpdateImplementationRun(run.Id, "Cloning", apply.Message);
            store.MarkActionRun(actionStart.Action!.Id, run.Id, "Running");
            await realtime.PublishAsync("implementationRunChanged", updated);
            return Results.Accepted($"/api/implementation-runs/{run.Id}", updated);
        });
    }

    private static string EffectiveActorSubject(string? actorSubject) =>
        string.IsNullOrWhiteSpace(actorSubject) ? "local-dev" : actorSubject;

    private static string PullRequestReviewFixActionIdempotencyKey(string actor, Guid workItemId, string? reasoningEffort) =>
        $"pr-review-fix:{EffectiveActorSubject(actor).Trim()}:{workItemId:N}:{NormalizeActionKeyPart(reasoningEffort)}";

    private static string NormalizeActionKeyPart(string? value)
    {
        var normalized = Regex.Replace((string.IsNullOrWhiteSpace(value) ? "default" : value.Trim()).ToLowerInvariant(), "[^a-z0-9._-]+", "-").Trim('-', '.', '_');
        return string.IsNullOrWhiteSpace(normalized) ? "default" : normalized;
    }

    private static ExpensiveActionQuotaOptions ReadPullRequestReviewFixActionQuota(IConfiguration configuration)
    {
        var enabled = configuration.GetValue("Actions:Quotas:PullRequestReviewFix:Enabled", true);
        var maxStartedPerActor = Math.Max(1, configuration.GetValue("Actions:Quotas:PullRequestReviewFix:MaxStartedPerActor", 4));
        var windowSeconds = Math.Max(60, configuration.GetValue("Actions:Quotas:PullRequestReviewFix:WindowSeconds", 900));
        return new ExpensiveActionQuotaOptions(enabled, maxStartedPerActor, TimeSpan.FromSeconds(windowSeconds));
    }

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

    private static bool CanViewWorkItemRequest(DevOpsStore store, Guid workItemId, ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated != true || store.CanViewWorkItem(workItemId, UserIdentityFromClaims(user).Subject);

    private static bool CanMutateWorkItemRequest(DevOpsStore store, Guid workItemId, ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated != true || store.CanMutateWorkItem(workItemId, UserIdentityFromClaims(user).Subject);

    private static IResult BoardReadForbidden() =>
        Results.Problem("You do not have permission to view this board.", statusCode: StatusCodes.Status403Forbidden);

    private static IResult BoardMutationForbidden() =>
        Results.Problem("You do not have permission to modify this board.", statusCode: StatusCodes.Status403Forbidden);

    private static string? AuthenticatedSubjectOrNull(ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true ? UserIdentityFromClaims(user).Subject : null;

    private static string AuditActorFromClaims(ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true ? UserIdentityFromClaims(user).DisplayName : "system";

    private static UserIdentityRequest UserIdentityFromClaims(ClaimsPrincipal user)
    {
        var subject = user.FindFirstValue(ClaimTypes.NameIdentifier) ??
            user.FindFirstValue("sub") ??
            "local-dev";
        var email = user.FindFirstValue(ClaimTypes.Email) ??
            user.FindFirstValue("email") ??
            "christopher.rosenvall@gmail.com";
        var displayName = user.FindFirstValue("name") ?? user.FindFirstValue("preferred_username") ?? email;
        var avatar = user.FindFirstValue("picture");
        return new UserIdentityRequest(subject, displayName, email, avatar);
    }
}
