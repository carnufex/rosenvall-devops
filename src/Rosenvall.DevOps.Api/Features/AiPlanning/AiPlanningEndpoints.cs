using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;

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

        api.MapPost("/work-items/{workItemId:guid}/ai-plan", async (Guid workItemId, StartAiPlanRequest request, ClaimsPrincipal user, DevOpsStore store, AiPlanProviderRouter planner, IConfiguration configuration, IRealtimeNotifier realtime, CancellationToken cancellationToken) =>
        {
            if (!CanMutateWorkItemRequest(store, workItemId, user))
            {
                return BoardMutationForbidden();
            }

            var actorSubject = EffectiveActorSubject(AuthenticatedSubjectOrNull(user));
            var boardId = store.GetWorkItemBoardId(workItemId);
            var context = store.GetWorkItemDetail(workItemId);
            if (context is null || boardId is null)
            {
                return Results.NotFound();
            }

            string plan;
            ActionStartResultDto? actionStart = null;
            try
            {
                var validated = AiModelPolicy.ValidatePlanningRequest(request, store.GetSettings(configuration, AuthenticatedSubjectOrNull(user)));
                if (validated is null)
                {
                    return Results.Problem("Requested AI provider or model is not configured for planning.", statusCode: StatusCodes.Status400BadRequest);
                }

                var actionKey = AiPlanActionIdempotencyKey(actorSubject, workItemId, validated.Provider, validated.Model, validated.ReasoningEffort);
                var quota = ReadAiPlanningActionQuota(configuration);
                actionStart = store.StartAction(
                    actorSubject,
                    boardId,
                    workItemId,
                    "ai-plan",
                    actionKey,
                    blockAfterRunCreation: false,
                    maxStartsPerActor: quota.Enabled ? quota.MaxStartedPerActor : null,
                    quotaWindow: quota.Window,
                    quotaOperationKinds: ActionLedgerBlockReasons.AiPlanningActionKinds);
                if (!actionStart.Started)
                {
                    if (IsQuotaExceeded(actionStart))
                    {
                        return ActionQuotaExceededResult(actionStart);
                    }

                    return Results.Conflict(new
                    {
                        message = "AI plan generation is already running for this request.",
                        operationId = actionStart.Action?.Id,
                        runId = actionStart.Action?.RunId
                    });
                }

                plan = await planner.GeneratePlanAsync(validated.Provider, validated.Model, validated.ReasoningEffort, context, cancellationToken);
                request = request with
                {
                    Provider = validated.Provider,
                    Model = validated.Model,
                    ReasoningEffort = validated.ReasoningEffort
                };
            }
            catch (AiPlanProviderUnavailableException ex)
            {
                if (actionStart is not null)
                {
                    store.MarkActionFailed(actionStart.Action!.Id, ex.Message);
                }

                return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var run = store.StartAiPlan(workItemId, request.Provider, request.Model, plan, request.ReasoningEffort);
            if (run is null)
            {
                if (actionStart is not null)
                {
                    store.MarkActionFailed(actionStart.Action!.Id, "Work item was not found.");
                }

                return Results.NotFound();
            }

            if (actionStart is not null)
            {
                store.MarkActionRun(actionStart.Action!.Id, run.Id, "Completed");
            }

            await realtime.PublishAsync("aiRunChanged", run);
            return Results.Accepted($"/api/ai-runs/{run.Id}", run);
        });

        api.MapPost("/work-items/{workItemId:guid}/ai-plan/revise", async (Guid workItemId, ReviseAiPlanRequest request, ClaimsPrincipal user, DevOpsStore store, AiPlanProviderRouter planner, IConfiguration configuration, IRealtimeNotifier realtime, CancellationToken cancellationToken) =>
        {
            if (!CanMutateWorkItemRequest(store, workItemId, user))
            {
                return BoardMutationForbidden();
            }

            if (string.IsNullOrWhiteSpace(request.Message))
            {
                return Results.BadRequest("Revision message is required.");
            }

            var actorIdentity = UserIdentityFromClaims(user);
            var actorSubject = EffectiveActorSubject(AuthenticatedSubjectOrNull(user));
            var boardId = store.GetWorkItemBoardId(workItemId);
            var context = store.GetWorkItemDetail(workItemId);
            if (context is null || boardId is null)
            {
                return Results.NotFound();
            }

            string plan;
            ActionStartResultDto? actionStart = null;
            CommentDto? comment = null;
            try
            {
                var validated = AiModelPolicy.ValidatePlanningRequest(new StartAiPlanRequest(request.Provider, request.Model, request.ReasoningEffort), store.GetSettings(configuration, AuthenticatedSubjectOrNull(user)));
                if (validated is null)
                {
                    return Results.Problem("Requested AI provider or model is not configured for planning.", statusCode: StatusCodes.Status400BadRequest);
                }

                var actionKey = AiPlanRevisionActionIdempotencyKey(actorSubject, workItemId, request.AiRunId, request.Message, validated.Provider, validated.Model, validated.ReasoningEffort);
                var quota = ReadAiPlanningActionQuota(configuration);
                actionStart = store.StartAction(
                    actorSubject,
                    boardId,
                    workItemId,
                    "ai-plan-revise",
                    actionKey,
                    blockAfterRunCreation: false,
                    maxStartsPerActor: quota.Enabled ? quota.MaxStartedPerActor : null,
                    quotaWindow: quota.Window,
                    quotaOperationKinds: ActionLedgerBlockReasons.AiPlanningActionKinds);
                if (!actionStart.Started)
                {
                    if (IsQuotaExceeded(actionStart))
                    {
                        return ActionQuotaExceededResult(actionStart);
                    }

                    return Results.Conflict(new
                    {
                        message = "AI plan revision is already running for this request.",
                        operationId = actionStart.Action?.Id,
                        runId = actionStart.Action?.RunId
                    });
                }

                comment = store.AddComment(workItemId, actorIdentity.DisplayName, "Comment", request.Message);
                if (comment is null)
                {
                    store.MarkActionFailed(actionStart.Action!.Id, "Work item was not found.");
                    return Results.NotFound();
                }

                await realtime.PublishAsync("commentAdded", comment);
                context = store.GetWorkItemDetail(workItemId);
                if (context is null)
                {
                    store.MarkActionFailed(actionStart.Action!.Id, "Work item was not found.");
                    return Results.NotFound();
                }

                plan = await planner.GeneratePlanAsync(validated.Provider, validated.Model, validated.ReasoningEffort, context, cancellationToken);
                request = request with
                {
                    Provider = validated.Provider,
                    Model = validated.Model,
                    ReasoningEffort = validated.ReasoningEffort
                };
            }
            catch (AiPlanProviderUnavailableException ex)
            {
                if (actionStart is not null)
                {
                    store.MarkActionFailed(actionStart.Action!.Id, ex.Message);
                }

                return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var run = store.StartAiPlan(workItemId, request.Provider, request.Model, plan, request.ReasoningEffort);
            if (run is null)
            {
                if (actionStart is not null)
                {
                    store.MarkActionFailed(actionStart.Action!.Id, "Work item was not found.");
                }

                return Results.NotFound();
            }

            if (actionStart is not null)
            {
                store.MarkActionRun(actionStart.Action!.Id, run.Id, "Completed");
            }

            await realtime.PublishAsync("aiRunChanged", run);
            return Results.Accepted($"/api/ai-runs/{run.Id}", run);
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

    private static string? AuthenticatedSubjectOrNull(ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true ? UserIdentityFromClaims(user).Subject : null;

    private static string EffectiveActorSubject(string? actorSubject) =>
        string.IsNullOrWhiteSpace(actorSubject) ? "local-dev" : actorSubject;

    private static string AiPlanActionIdempotencyKey(string actor, Guid workItemId, string provider, string model, string? reasoningEffort) =>
        $"ai-plan:{EffectiveActorSubject(actor).Trim()}:{workItemId:N}:{NormalizeActionKeyPart(provider)}:{NormalizeActionKeyPart(model)}:{NormalizeActionKeyPart(reasoningEffort)}";

    private static string AiPlanRevisionActionIdempotencyKey(string actor, Guid workItemId, Guid? aiRunId, string message, string provider, string model, string? reasoningEffort) =>
        $"ai-plan-revise:{EffectiveActorSubject(actor).Trim()}:{workItemId:N}:{aiRunId?.ToString("N") ?? "latest"}:{NormalizeActionKeyPart(provider)}:{NormalizeActionKeyPart(model)}:{NormalizeActionKeyPart(reasoningEffort)}:{ShortActionHash(message)}";

    private static string NormalizeActionKeyPart(string? value)
    {
        var normalized = Regex.Replace((string.IsNullOrWhiteSpace(value) ? "default" : value.Trim()).ToLowerInvariant(), "[^a-z0-9._-]+", "-").Trim('-', '.', '_');
        return string.IsNullOrWhiteSpace(normalized) ? "default" : normalized;
    }

    private static string ShortActionHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim()))).ToLowerInvariant()[..24];

    private static ExpensiveActionQuotaOptions ReadAiPlanningActionQuota(IConfiguration configuration)
    {
        var enabled = configuration.GetValue("Actions:Quotas:AiPlanning:Enabled", true);
        var maxStartedPerActor = Math.Max(1, configuration.GetValue("Actions:Quotas:AiPlanning:MaxStartedPerActor", 12));
        var windowSeconds = Math.Max(60, configuration.GetValue("Actions:Quotas:AiPlanning:WindowSeconds", 600));
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
