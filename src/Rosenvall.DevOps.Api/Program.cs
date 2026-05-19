using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;
using Rosenvall.DevOps.Api;
using Rosenvall.DevOps.Core;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddSignalR();
builder.Services.AddHttpClient<OllamaPlanProvider>();
builder.Services.AddTransient<IAiPlanProvider>(services => services.GetRequiredService<OllamaPlanProvider>());
builder.Services.AddSingleton<IAiPlanProvider, CodexCliPlanProvider>();
builder.Services.AddSingleton<AiPlanProviderRouter>();
builder.Services.AddSingleton<CodexCliPreviewSourceProvider>();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
var devOpsConnectionString = builder.Configuration.GetConnectionString("DevOps");
builder.Services.AddDbContextFactory<DevOpsStateDbContext>(options =>
{
    if (string.IsNullOrWhiteSpace(devOpsConnectionString))
    {
        var sqlitePath = builder.Configuration["Storage:SqlitePath"] ?? "devops-state.db";
        options.UseSqlite($"Data Source={sqlitePath}");
    }
    else
    {
        options.UseNpgsql(devOpsConnectionString);
    }
});
builder.Services.AddSingleton<DevOpsStore>();
builder.Services.AddSingleton<PreviewEnvironmentOrchestrator>();
builder.Services.AddSingleton<PipelineJobOrchestrator>();
builder.Services.AddHostedService<PreviewHealthMonitor>();
builder.Services.AddCors(options =>
{
    options.AddPolicy("frontend", policy =>
        policy.WithOrigins(builder.Configuration.GetSection("Frontend:AllowedOrigins").Get<string[]>() ?? ["http://localhost:5173"])
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials());
});

var authority = builder.Configuration["Authentication:Authority"];
if (!string.IsNullOrWhiteSpace(authority))
{
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.Authority = authority;
            options.Audience = builder.Configuration["Authentication:Audience"];
            options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        });
    builder.Services.AddAuthorization();
}

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<DevOpsStateDbContext>>();
    await using var db = await dbFactory.CreateDbContextAsync();
    await db.Database.EnsureCreatedAsync();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler();
}

app.UseCors("frontend");

if (!string.IsNullOrWhiteSpace(authority))
{
    app.UseAuthentication();
    app.UseAuthorization();
}

app.MapHealthChecks("/healthz");
var hub = app.MapHub<DevOpsHub>("/hubs/devops");
if (!string.IsNullOrWhiteSpace(authority))
{
    hub.RequireAuthorization();
}

var api = app.MapGroup("/api");
if (!string.IsNullOrWhiteSpace(authority))
{
    api.RequireAuthorization();
}

api.MapGet("/workspaces", (DevOpsStore store) => store.GetWorkspaces());
api.MapPost("/workspaces", async (CreateWorkspaceRequest request, DevOpsStore store, IHubContext<DevOpsHub> hub) =>
{
    var workspace = store.CreateWorkspace(request.Name, request.EnvironmentName, request.Region);
    await hub.Clients.All.SendAsync("workspaceCreated", workspace);
    return Results.Created($"/api/workspaces/{workspace.Id}", workspace);
});

api.MapGet("/workspaces/{workspaceId:guid}/boards", (Guid workspaceId, DevOpsStore store) =>
    store.GetBoards(workspaceId) is { Count: > 0 } boards ? Results.Ok(boards) : Results.NotFound());

api.MapPost("/workspaces/{workspaceId:guid}/boards", (Guid workspaceId, CreateBoardRequest request, DevOpsStore store) =>
    store.CreateBoard(workspaceId, request) is { } board ? Results.Created($"/api/boards/{board.Id}", board) : Results.NotFound());

api.MapGet("/boards/{boardId:guid}", (Guid boardId, DevOpsStore store) =>
    store.GetBoard(boardId) is { } board ? Results.Ok(board) : Results.NotFound());

api.MapGet("/boards/{boardId:guid}/timeline", (Guid boardId, DevOpsStore store) =>
    store.GetBoard(boardId) is null ? Results.NotFound() : Results.Ok(store.GetTimeline(boardId)));

api.MapGet("/repositories", (DevOpsStore store) => store.GetRepositories());

api.MapPost("/repositories", (CreateRepositoryRequest request, DevOpsStore store) =>
{
    var repository = store.CreateRepository(request);
    return Results.Created($"/api/repositories/{repository.Id}", repository);
});

api.MapGet("/work-items", (DevOpsStore store) => store.GetWorkItems());
api.MapPost("/work-items", async (CreateWorkItemRequest request, DevOpsStore store, IHubContext<DevOpsHub> hub) =>
{
    var item = store.CreateWorkItem(request);
    await hub.Clients.All.SendAsync("workItemChanged", item);
    return Results.Created($"/api/work-items/{item.Id}", item);
});

api.MapPatch("/work-items/{workItemId:guid}", async (Guid workItemId, UpdateWorkItemRequest request, DevOpsStore store, IHubContext<DevOpsHub> hub) =>
{
    var item = store.UpdateWorkItem(workItemId, request);
    if (item is null)
    {
        return Results.NotFound();
    }

    await hub.Clients.All.SendAsync("workItemChanged", item);
    return Results.Ok(item);
});

api.MapDelete("/work-items/{workItemId:guid}", async (Guid workItemId, DevOpsStore store, PreviewEnvironmentOrchestrator previews, IHubContext<DevOpsHub> hub, CancellationToken cancellationToken) =>
{
    var manifest = store.RenderPreviewManifest(workItemId);
    if (manifest is not null)
    {
        var cleanup = await previews.DeleteAsync(manifest, cancellationToken);
        if (!cleanup.Succeeded)
        {
            store.RecordPreviewFailure(workItemId, "CleanupFailed", "crille", cleanup.Message);
        }
    }

    if (!store.DeleteWorkItem(workItemId, "crille"))
    {
        return Results.NotFound();
    }

    await hub.Clients.All.SendAsync("workItemDeleted", workItemId);
    return Results.NoContent();
});

api.MapPost("/work-items/{workItemId:guid}/move", async (Guid workItemId, MoveWorkItemRequest request, DevOpsStore store, IHubContext<DevOpsHub> hub) =>
{
    var item = store.MoveWorkItem(workItemId, request);
    if (item is null)
    {
        return Results.NotFound();
    }

    await hub.Clients.All.SendAsync("workItemChanged", item);
    return Results.Ok(item);
});

api.MapGet("/work-items/{workItemId:guid}", (Guid workItemId, DevOpsStore store) =>
    store.GetWorkItemDetail(workItemId) is { } item ? Results.Ok(item) : Results.NotFound());

api.MapGet("/work-items/{workItemId:guid}/ai-runs", (Guid workItemId, DevOpsStore store) =>
    store.GetAiRuns(workItemId));

api.MapPost("/work-items/{workItemId:guid}/comments", async (Guid workItemId, AddCommentRequest request, DevOpsStore store, IHubContext<DevOpsHub> hub) =>
{
    var comment = store.AddComment(workItemId, request.Author, request.Kind, request.Body);
    if (comment is null)
    {
        return Results.NotFound();
    }

    await hub.Clients.All.SendAsync("commentAdded", comment);
    return Results.Created($"/api/work-items/{workItemId}", comment);
});

api.MapPatch("/comments/{commentId:guid}", async (Guid commentId, UpdateCommentRequest request, DevOpsStore store, IHubContext<DevOpsHub> hub) =>
{
    try
    {
        var comment = store.UpdateComment(commentId, request.Actor, request.Body);
        if (comment is null)
        {
            return Results.NotFound();
        }

        await hub.Clients.All.SendAsync("commentChanged", comment);
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

api.MapDelete("/comments/{commentId:guid}", async (Guid commentId, string actor, DevOpsStore store, IHubContext<DevOpsHub> hub) =>
{
    try
    {
        var deleted = store.DeleteComment(commentId, actor);
        if (!deleted)
        {
            return Results.NotFound();
        }

        await hub.Clients.All.SendAsync("commentDeleted", commentId);
        return Results.NoContent();
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status403Forbidden);
    }
});

api.MapPost("/work-items/{workItemId:guid}/ai-plan", async (Guid workItemId, StartAiPlanRequest request, DevOpsStore store, AiPlanProviderRouter planner, IHubContext<DevOpsHub> hub, CancellationToken cancellationToken) =>
{
    var context = store.GetWorkItemDetail(workItemId);
    if (context is null)
    {
        return Results.NotFound();
    }

    string plan;
    try
    {
        plan = await planner.GeneratePlanAsync(request.Provider, request.Model, context, cancellationToken);
    }
    catch (AiPlanProviderUnavailableException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var run = store.StartAiPlan(workItemId, request.Provider, request.Model, plan);
    if (run is null)
    {
        return Results.NotFound();
    }

    await hub.Clients.All.SendAsync("aiRunChanged", run);
    return Results.Accepted($"/api/ai-runs/{run.Id}", run);
});

api.MapPost("/ai-runs/{aiRunId:guid}/approve", async (Guid aiRunId, ApproveAiRunRequest request, DevOpsStore store, CodexCliPreviewSourceProvider previewSourceProvider, PreviewEnvironmentOrchestrator previews, IConfiguration configuration, IHubContext<DevOpsHub> hub, CancellationToken cancellationToken) =>
{
    AiRun? result;
    try
    {
        result = store.ApproveAiRun(aiRunId, request.ApprovedBy);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
    }

    if (result is null)
    {
        return Results.NotFound();
    }

    await hub.Clients.All.SendAsync("aiRunChanged", result);
    var context = store.GetWorkItemDetail(result.WorkItemId);
    if (context is null)
    {
        return Results.NotFound();
    }

    IReadOnlyList<PreviewSourceFile> sourceFiles;
    try
    {
        var implementationModel = configuration["Ai:Codex:Model"] ?? "gpt-5.4";
        sourceFiles = await previewSourceProvider.GenerateSourceAsync(implementationModel, result, context, cancellationToken);
    }
    catch (AiPlanProviderUnavailableException ex)
    {
        store.RecordImplementationFailure(result.WorkItemId, request.ApprovedBy, ex.Message);
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var implementation = store.CompletePreviewImplementation(result.WorkItemId, sourceFiles, "codex");
    if (implementation is not null)
    {
        var manifest = store.RenderPreviewManifest(result.WorkItemId);
        if (manifest is not null)
        {
            var apply = await previews.ApplyAsync(manifest, cancellationToken);
            if (!apply.Succeeded)
            {
                store.RecordPreviewFailure(result.WorkItemId, "ApplyFailed", request.ApprovedBy, apply.Message);
                return Results.Problem(apply.Message, statusCode: StatusCodes.Status502BadGateway);
            }

            store.MarkPreviewProvisioning(result.WorkItemId, apply.Message);
            implementation = store.GetWorkItemDetail(result.WorkItemId);
        }

        await hub.Clients.All.SendAsync("previewChanged", implementation?.Preview);
    }

    return Results.Accepted($"/api/ai-runs/{aiRunId}", implementation ?? (object)result);
});

api.MapPost("/ai-runs/{aiRunId:guid}/discard", async (Guid aiRunId, DiscardAiRunRequest request, DevOpsStore store, IHubContext<DevOpsHub> hub) =>
{
    var result = store.DiscardAiRun(aiRunId, request.DiscardedBy);
    if (result is null)
    {
        return Results.NotFound();
    }

    await hub.Clients.All.SendAsync("aiRunChanged", result);
    return Results.Ok(result);
});

api.MapPost("/integrations/github/callback", async (GitHubCallbackRequest request, DevOpsStore store, IHubContext<DevOpsHub> hub) =>
{
    var result = store.ApplyGitHubCallback(request);
    if (result is null)
    {
        return Results.NotFound();
    }

    await hub.Clients.All.SendAsync("previewChanged", result.Preview);
    return Results.Ok(result);
});

api.MapPost("/work-items/{workItemId:guid}/approve-pr", async (Guid workItemId, ApprovePullRequestRequest request, DevOpsStore store, PreviewEnvironmentOrchestrator previews, IHubContext<DevOpsHub> hub, CancellationToken cancellationToken) =>
{
    var manifest = store.RenderPreviewManifest(workItemId);
    if (manifest is not null)
    {
        var cleanup = await previews.DeleteAsync(manifest, cancellationToken);
        if (!cleanup.Succeeded)
        {
            store.RecordPreviewFailure(workItemId, "CleanupFailed", request.ApprovedBy, cleanup.Message);
            return Results.Problem(cleanup.Message, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    var result = store.ApprovePullRequest(workItemId, request.ApprovedBy);
    if (result is null)
    {
        return Results.NotFound();
    }

    await hub.Clients.All.SendAsync("workItemChanged", result.Item);
    return Results.Ok(result);
});

api.MapPost("/work-items/{workItemId:guid}/preview/start", async (Guid workItemId, PreviewActionRequest request, DevOpsStore store, PreviewEnvironmentOrchestrator previews, IHubContext<DevOpsHub> hub, CancellationToken cancellationToken) =>
{
    var manifest = store.RenderPreviewManifest(workItemId);
    if (manifest is null)
    {
        return Results.NotFound();
    }

    var apply = await previews.ApplyAsync(manifest, cancellationToken);
    if (!apply.Succeeded)
    {
        store.RecordPreviewFailure(workItemId, "ApplyFailed", request.Actor, apply.Message);
        return Results.Problem(apply.Message, statusCode: StatusCodes.Status502BadGateway);
    }

    var detail = store.MarkPreviewProvisioning(workItemId, apply.Message);
    await hub.Clients.All.SendAsync("previewChanged", detail?.Preview);
    return detail is null ? Results.NotFound() : Results.Ok(detail);
});

api.MapPost("/work-items/{workItemId:guid}/preview/stop", async (Guid workItemId, PreviewActionRequest request, DevOpsStore store, PreviewEnvironmentOrchestrator previews, IHubContext<DevOpsHub> hub, CancellationToken cancellationToken) =>
{
    var manifest = store.RenderPreviewManifest(workItemId);
    if (manifest is null)
    {
        return Results.NotFound();
    }

    var cleanup = await previews.DeleteAsync(manifest, cancellationToken);
    if (!cleanup.Succeeded)
    {
        store.RecordPreviewFailure(workItemId, "CleanupFailed", request.Actor, cleanup.Message);
        return Results.Problem(cleanup.Message, statusCode: StatusCodes.Status502BadGateway);
    }

    var detail = store.StopPreview(workItemId, request.Actor, cleanup.Message);
    await hub.Clients.All.SendAsync("previewChanged", detail?.Preview);
    return detail is null ? Results.NotFound() : Results.Ok(detail);
});

api.MapGet("/preview-environments", (DevOpsStore store) => store.GetPreviewEnvironments());
api.MapGet("/preview-events", (DevOpsStore store) => store.GetPreviewEvents());
api.MapGet("/pipelines", (DevOpsStore store) => store.GetPipelineStatuses());
api.MapGet("/metrics", (Guid? boardId, DevOpsStore store) => store.GetMetrics(boardId));
api.MapGet("/assignees", (Guid? boardId, DevOpsStore store, IConfiguration configuration) => store.GetAssignees(boardId, configuration));

api.MapPost("/pipeline-runs", (RecordPipelineRunRequest request, DevOpsStore store) =>
    store.RecordPipelineRun(request) is { } run ? Results.Created($"/api/pipeline-runs/{run.Id}", run) : Results.NotFound());

api.MapPost("/pipeline-runs/{pipelineRunId:guid}/execute", async (Guid pipelineRunId, ExecutePipelineRunRequest request, DevOpsStore store, PipelineJobOrchestrator jobs, CancellationToken cancellationToken) =>
{
    var manifest = store.RenderPipelineJobManifest(pipelineRunId);
    if (manifest is null)
    {
        return Results.NotFound();
    }

    var apply = await jobs.ApplyAsync(manifest, cancellationToken);
    if (!apply.Succeeded)
    {
        var failed = store.MarkPipelineRunFailed(pipelineRunId, request.Actor, apply.Message);
        return failed is null ? Results.NotFound() : Results.Problem(apply.Message, statusCode: StatusCodes.Status502BadGateway);
    }

    var executing = store.MarkPipelineRunExecuting(pipelineRunId, request.Actor);
    return executing is null ? Results.NotFound() : Results.Accepted($"/api/pipeline-runs/{pipelineRunId}", executing);
});

api.MapGet("/pipeline-runs/{pipelineRunId:guid}/manifest", (Guid pipelineRunId, DevOpsStore store) =>
    store.RenderPipelineJobManifest(pipelineRunId) is { } manifest ? Results.Text(manifest, "application/yaml") : Results.NotFound());

api.MapGet("/previews/{workItemId:guid}/manifest", (Guid workItemId, DevOpsStore store) =>
    store.RenderPreviewManifest(workItemId) is { } manifest ? Results.Text(manifest, "application/yaml") : Results.NotFound());

api.MapGet("/settings", (DevOpsStore store, IConfiguration configuration) => store.GetSettings(configuration));

app.Run();

namespace Rosenvall.DevOps.Api
{
    public sealed class DevOpsHub : Hub;

    public sealed record WorkspaceDto(Guid Id, string Name, string EnvironmentName, string Region, int ActiveProjects, int OpenPullRequests, int SuccessfulAiImplementations, int ComputeUsagePercent);
    public sealed record RepositoryDto(Guid Id, string Provider, string Name, string RemoteUrl, string? WebUrl, string DefaultBranch, DateTimeOffset CreatedAt);
    public sealed record BoardDto(Guid Id, Guid WorkspaceId, string Name, IReadOnlyList<BoardColumnDto> Columns, RepositoryDto? Repository = null);
    public sealed record BoardColumnDto(string Name, IReadOnlyList<WorkItemSummaryDto> Items);
    public sealed record WorkItemSummaryDto(Guid Id, string Key, string Type, string Title, string Status, string? Assignee, string Priority, int CommentCount, string? AiStatus, string? PullRequestUrl, int SortOrder, string? PreviewUrl);
    public sealed record WorkItemDetailDto(WorkItemSummaryDto Item, string Description, IReadOnlyList<CommentDto> Comments, PreviewDto? Preview, DevelopmentDto? Development);
    public sealed record CommentDto(Guid Id, Guid WorkItemId, string Author, string Kind, string Body, DateTimeOffset CreatedAt);
    public sealed record PreviewDto(Guid Id, Guid WorkItemId, string Url, string Image, string Status, DateTimeOffset ExpiresAt, string? StaticHtml, string? Namespace = null, string? ResourceName = null, string? Phase = null, string? Message = null, DateTimeOffset? LastCheckedAt = null, string? PodName = null, string? FailureReason = null, string? FailureLog = null, IReadOnlyList<PreviewSourceFile>? SourceFiles = null);
    public sealed record PreviewEnvironmentDto(Guid Id, Guid? WorkItemId, string WorkItemKey, string WorkItemTitle, string Url, string Namespace, string ResourceName, string Image, string Status, DateTimeOffset ExpiresAt, string? Phase = null, string? Message = null, DateTimeOffset? LastCheckedAt = null, string? PodName = null, string? FailureReason = null, string? FailureLog = null);
    public sealed record PreviewEventDto(Guid Id, Guid? WorkItemId, string WorkItemKey, string WorkItemTitle, string EventType, string? Namespace, string? Url, string Actor, string Message, DateTimeOffset CreatedAt);
    public sealed record PipelineStatusDto(Guid Id, Guid? WorkItemId, string WorkItemKey, string WorkItemTitle, string Stage, string Status, string Message, DateTimeOffset UpdatedAt);
    public sealed record PipelineRunDto(Guid Id, Guid RepositoryId, Guid? BoardId, Guid? WorkItemId, string Stage, string Status, string Message, string? Url, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt = null, int TokensUsed = 0, int CodeAdded = 0, int CodeDeleted = 0);
    public sealed record TimelineEventDto(Guid Id, Guid? BoardId, Guid? RepositoryId, Guid? WorkItemId, string Kind, string Title, string Message, string Actor, string? Url, DateTimeOffset CreatedAt);
    public sealed record DevelopmentDto(string Repository, string Branch, string? PullRequestUrl, string ChecksStatus, string? PullRequestApprovedBy = null, DateTimeOffset? PullRequestApprovedAt = null);
    public sealed record SettingsDto(GitHubSettingsDto GitHub, AiSettingsDto Ai, PreviewSettingsDto Preview, RepositoryHostingSettingsDto Repositories, AuthentikSettingsDto Authentik);
    public sealed record GitHubSettingsDto(string Account, string TargetRepository, string BranchWatchPatterns, bool Connected);
    public sealed record AiSettingsDto(string Provider, string Endpoint, string ActiveModel, IReadOnlyList<string> AvailableModels, bool AutoReviewPullRequests, IReadOnlyList<AiProviderSettingsDto> AvailableProviders);
    public sealed record AiProviderSettingsDto(string Provider, string DisplayName, string Status, string Endpoint, string ActiveModel, IReadOnlyList<string> AvailableModels);
    public sealed record PreviewSettingsDto(string Domain, int DefaultTtlDays, string Namespace);
    public sealed record RepositoryHostingSettingsDto(string Provider, string Mode, string ApiBaseUrl, bool CanCreateRepositories);
    public sealed record AuthentikSettingsDto(bool Enabled, string Authority, string UsersEndpoint);
    public sealed record MetricsDto(Guid? BoardId, int TokensUsed, int CodeAdded, int CodeDeleted, int PipelineRuns);
    public sealed record AssigneeDto(string Id, string DisplayName, string Email, string Source);

    public sealed record CreateWorkspaceRequest(string Name, string EnvironmentName, string Region);
    public sealed record CreateRepositoryRequest(string Provider, string Name, string RemoteUrl, string DefaultBranch, string? WebUrl = null);
    public sealed record CreateBoardRequest(string Name, Guid? RepositoryId, string? RepositoryProvider, string? RepositoryName, string? RepositoryRemoteUrl, string? RepositoryWebUrl, string? RepositoryDefaultBranch);
    public sealed record CreateWorkItemRequest(Guid BoardId, string Type, string Title, string Description, string Status, string Priority, string? Assignee);
    public sealed record UpdateWorkItemRequest(string Title, string Description, string Type, string Status, string Priority, string? Assignee);
    public sealed record MoveWorkItemRequest(string Status, int SortOrder);
    public sealed record AddCommentRequest(string Author, string Kind, string Body);
    public sealed record UpdateCommentRequest(string Actor, string Body);
    public sealed record StartAiPlanRequest(string Provider, string Model);
    public sealed record ApproveAiRunRequest(string ApprovedBy);
    public sealed record DiscardAiRunRequest(string DiscardedBy);
    public sealed record ApprovePullRequestRequest(string ApprovedBy);
    public sealed record PreviewActionRequest(string Actor);
    public sealed record RecordPipelineRunRequest(Guid RepositoryId, Guid? BoardId, Guid? WorkItemId, string Stage, string Status, string Message, string? Url = null, int TokensUsed = 0, int CodeAdded = 0, int CodeDeleted = 0);
    public sealed record ExecutePipelineRunRequest(string Actor);
    public sealed record GitHubCallbackRequest(Guid WorkItemId, string Repository, string Branch, string? PullRequestUrl, string Image, string ChecksStatus, string? StaticHtml = null);

    public class AiPlanProviderUnavailableException(string message) : InvalidOperationException(message);
    public sealed class OllamaUnavailableException(string message) : AiPlanProviderUnavailableException(message);

    public static class PipelineJobManifestRenderer
    {
        public static string Render(PipelineRunDto run, RepositoryDto repository)
        {
            var name = SafeName($"pipeline-{run.Stage}-{repository.Name}-{run.Id:N}");
            return $$"""
                   apiVersion: batch/v1
                   kind: Job
                   metadata:
                     name: {{name}}
                     namespace: rosenvall-devops-pipelines
                     labels:
                       app.kubernetes.io/part-of: rosenvall-devops-pipeline
                       rosenvall.devops/repository: {{SafeName(repository.Name)}}
                   spec:
                     backoffLimit: 0
                     template:
                       metadata:
                         labels:
                           app.kubernetes.io/name: {{name}}
                       spec:
                         restartPolicy: Never
                         securityContext:
                           runAsNonRoot: true
                           runAsUser: 1000
                           runAsGroup: 1000
                           seccompProfile:
                             type: RuntimeDefault
                         containers:
                           - name: runner
                             image: alpine/git:2.47.2
                             securityContext:
                               allowPrivilegeEscalation: false
                               capabilities:
                                 drop:
                                   - ALL
                             env:
                               - name: ROSENVALL_PIPELINE_RUN_ID
                                 value: "{{run.Id}}"
                               - name: ROSENVALL_REPOSITORY_PROVIDER
                                 value: "{{repository.Provider}}"
                               - name: ROSENVALL_REPOSITORY_URL
                                 value: "{{repository.RemoteUrl}}"
                               - name: ROSENVALL_DEFAULT_BRANCH
                                 value: "{{repository.DefaultBranch}}"
                             command:
                               - sh
                               - -c
                               - git clone --depth 1 --branch "$ROSENVALL_DEFAULT_BRANCH" "$ROSENVALL_REPOSITORY_URL" /workspace/repo && git -C /workspace/repo log --oneline -5
                   """;
        }

        private static string SafeName(string value)
        {
            var chars = value.ToLowerInvariant()
                .Select(character => char.IsLetterOrDigit(character) ? character : '-')
                .ToArray();
            var safe = new string(chars).Trim('-');
            while (safe.Contains("--", StringComparison.Ordinal))
            {
                safe = safe.Replace("--", "-", StringComparison.Ordinal);
            }

            return safe.Length <= 52 ? safe : safe[..52].Trim('-');
        }
    }

    public sealed record PreviewCleanupResult(bool Succeeded, string Message)
    {
        public static PreviewCleanupResult Ok(string message) => new(true, message);
        public static PreviewCleanupResult Failed(string message) => new(false, message);
    }

    public sealed record PreviewHealthCheckResult(string Status, string Phase, string Message, string? PodName = null, string? FailureReason = null, string? FailureLog = null)
    {
        public static PreviewHealthCheckResult Provisioning(string phase, string message, string? podName = null) =>
            new("Provisioning", phase, message, podName);

        public static PreviewHealthCheckResult Running(string? podName, string message) =>
            new("Running", "Ready", message, podName);

        public static PreviewHealthCheckResult Failed(string reason, string message, string? failureLog = null, string? podName = null) =>
            new("Failed", "Failed", message, podName, reason, failureLog);
    }

    public sealed class PreviewEnvironmentOrchestrator(IConfiguration configuration, ILogger<PreviewEnvironmentOrchestrator> logger)
    {
        public Task<PreviewCleanupResult> ApplyAsync(string manifest, CancellationToken cancellationToken) =>
            RunKubectlAsync("apply -f -", manifest, cancellationToken);

        public async Task<PreviewCleanupResult> DeleteAsync(string manifest, CancellationToken cancellationToken)
        {
            return await RunKubectlAsync("delete -f - --ignore-not-found=true", manifest, cancellationToken);
        }

        public async Task<PreviewHealthCheckResult> CheckHealthAsync(PreviewDto preview, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(preview.Namespace) || string.IsNullOrWhiteSpace(preview.ResourceName))
            {
                return PreviewHealthCheckResult.Failed("MissingPreviewMetadata", "Preview namespace or resource name is missing.");
            }

            var deployment = await RunKubectlOutputAsync($"get deployment {preview.ResourceName} -n {preview.Namespace} -o json", cancellationToken);
            if (!deployment.Succeeded)
            {
                return PreviewHealthCheckResult.Provisioning("Waiting for deployment.", deployment.Message);
            }

            var pods = await RunKubectlOutputAsync($"get pods -n {preview.Namespace} -l app.kubernetes.io/name={preview.ResourceName} -o json", cancellationToken);
            if (!pods.Succeeded)
            {
                return PreviewHealthCheckResult.Provisioning("Waiting for pod.", pods.Message);
            }

            return await AnalyzeHealthAsync(preview, deployment.Message, pods.Message, cancellationToken);
        }

        private async Task<PreviewCleanupResult> RunKubectlAsync(string command, string manifest, CancellationToken cancellationToken)
        {
            var kubectlPath = configuration["Preview:KubectlPath"] ?? "kubectl";
            var kubeconfigPath = ResolveKubeconfigPath(configuration["Preview:KubeconfigPath"] ?? "tofu/output/kubeconfig");
            if (!string.IsNullOrWhiteSpace(kubeconfigPath) && !File.Exists(kubeconfigPath))
            {
                return PreviewCleanupResult.Failed($"Preview orchestration failed: Configured kubeconfig was not found: {kubeconfigPath}");
            }

            var arguments = string.IsNullOrWhiteSpace(kubeconfigPath)
                ? command
                : $"--kubeconfig \"{kubeconfigPath}\" {command}";

            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = kubectlPath,
                        Arguments = arguments,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
                try
                {
                    await process.StandardInput.WriteAsync(manifest);
                    process.StandardInput.Close();
                }
                catch (IOException ex)
                {
                    logger.LogWarning(ex, "Preview orchestration stdin closed before manifest was written.");
                    var earlyExit = await ReadKubectlExitAsync(process, outputTask, errorTask, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(earlyExit))
                    {
                        return PreviewCleanupResult.Failed($"Preview orchestration failed: {earlyExit}");
                    }

                    throw;
                }

                await process.WaitForExitAsync(cancellationToken);

                var output = (await outputTask).Trim();
                var error = (await errorTask).Trim();
                if (process.ExitCode == 0)
                {
                    return PreviewCleanupResult.Ok(string.IsNullOrWhiteSpace(output) ? "Preview orchestration completed." : output);
                }

                var message = string.IsNullOrWhiteSpace(error) ? output : error;
                return PreviewCleanupResult.Failed($"Preview orchestration failed: {message}");
            }
            catch (Exception ex) when (IsRecoverableKubectlException(ex))
            {
                logger.LogWarning(ex, "Preview orchestration failed.");
                return PreviewCleanupResult.Failed($"Preview orchestration failed: {ex.Message}");
            }
        }

        private async Task<PreviewCleanupResult> RunKubectlOutputAsync(string command, CancellationToken cancellationToken)
        {
            var kubectlPath = configuration["Preview:KubectlPath"] ?? "kubectl";
            var kubeconfigPath = ResolveKubeconfigPath(configuration["Preview:KubeconfigPath"] ?? "tofu/output/kubeconfig");
            if (!string.IsNullOrWhiteSpace(kubeconfigPath) && !File.Exists(kubeconfigPath))
            {
                return PreviewCleanupResult.Failed($"Configured kubeconfig was not found: {kubeconfigPath}");
            }

            var arguments = string.IsNullOrWhiteSpace(kubeconfigPath)
                ? command
                : $"--kubeconfig \"{kubeconfigPath}\" {command}";

            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = kubectlPath,
                        Arguments = arguments,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken);
                var output = (await outputTask).Trim();
                var error = (await errorTask).Trim();
                var message = string.IsNullOrWhiteSpace(error) ? output : error;
                return process.ExitCode == 0
                    ? PreviewCleanupResult.Ok(output)
                    : PreviewCleanupResult.Failed(message);
            }
            catch (Exception ex) when (IsRecoverableKubectlException(ex))
            {
                logger.LogWarning(ex, "Preview health check failed.");
                return PreviewCleanupResult.Failed(ex.Message);
            }
        }

        private async Task<PreviewHealthCheckResult> AnalyzeHealthAsync(PreviewDto preview, string deploymentJson, string podsJson, CancellationToken cancellationToken)
        {
            using var deploymentDocument = JsonDocument.Parse(deploymentJson);
            using var podsDocument = JsonDocument.Parse(podsJson);
            var availableReplicas = GetInt(deploymentDocument.RootElement, "status", "availableReplicas");
            var desiredReplicas = GetInt(deploymentDocument.RootElement, "spec", "replicas");
            var items = podsDocument.RootElement.TryGetProperty("items", out var podItems) && podItems.ValueKind == JsonValueKind.Array
                ? podItems.EnumerateArray().ToArray()
                : [];

            foreach (var pod in items)
            {
                var podName = GetString(pod, "metadata", "name");
                var ready = IsPodReady(pod);
                var waiting = FindBadContainerState(pod);
                if (waiting is not null)
                {
                    var log = await TryReadContainerLogAsync(preview.Namespace!, podName, waiting.Value.ContainerName, cancellationToken);
                    return PreviewHealthCheckResult.Failed(waiting.Value.Reason, $"{waiting.Value.ContainerName} is {waiting.Value.Reason}.", log, podName);
                }

                if (availableReplicas >= Math.Max(1, desiredReplicas) && ready)
                {
                    return PreviewHealthCheckResult.Running(podName, "Deployment is available and at least one preview pod is ready.");
                }
            }

            var podSummary = items.Length == 0 ? "No preview pod has been created yet." : $"Deployment has {availableReplicas}/{Math.Max(1, desiredReplicas)} available replicas.";
            return PreviewHealthCheckResult.Provisioning("Waiting for pod readiness.", podSummary, items.Select(pod => GetString(pod, "metadata", "name")).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)));
        }

        private async Task<string?> TryReadContainerLogAsync(string @namespace, string? podName, string containerName, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(podName))
            {
                return null;
            }

            var previous = await RunKubectlOutputAsync($"logs -n {@namespace} {podName} -c {containerName} --tail=40 --previous", cancellationToken);
            if (previous.Succeeded && !string.IsNullOrWhiteSpace(previous.Message))
            {
                return previous.Message;
            }

            var current = await RunKubectlOutputAsync($"logs -n {@namespace} {podName} -c {containerName} --tail=40", cancellationToken);
            return current.Succeeded && !string.IsNullOrWhiteSpace(current.Message) ? current.Message : previous.Message;
        }

        private static (string ContainerName, string Reason)? FindBadContainerState(JsonElement pod)
        {
            foreach (var statusProperty in new[] { "initContainerStatuses", "containerStatuses" })
            {
                if (!pod.TryGetProperty("status", out var status) ||
                    !status.TryGetProperty(statusProperty, out var statuses) ||
                    statuses.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var container in statuses.EnumerateArray())
                {
                    if (!container.TryGetProperty("state", out var state) ||
                        !state.TryGetProperty("waiting", out var waiting) ||
                        !waiting.TryGetProperty("reason", out var reasonElement))
                    {
                        continue;
                    }

                    var reason = reasonElement.GetString() ?? "";
                    if (IsFailureReason(reason))
                    {
                        return (container.GetProperty("name").GetString() ?? "container", reason);
                    }
                }
            }

            return null;
        }

        private static bool IsFailureReason(string reason) =>
            new[] { "CrashLoopBackOff", "ImagePullBackOff", "ErrImagePull", "CreateContainerConfigError", "RunContainerError", "Error" }
                .Contains(reason, StringComparer.OrdinalIgnoreCase);

        private static bool IsPodReady(JsonElement pod)
        {
            if (!pod.TryGetProperty("status", out var status) ||
                !status.TryGetProperty("conditions", out var conditions) ||
                conditions.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            return conditions.EnumerateArray().Any(condition =>
                GetString(condition, "type") == "Ready" &&
                GetString(condition, "status") == "True");
        }

        private static int GetInt(JsonElement root, params string[] path)
        {
            var current = root;
            foreach (var segment in path)
            {
                if (!current.TryGetProperty(segment, out current))
                {
                    return 0;
                }
            }

            return current.ValueKind == JsonValueKind.Number && current.TryGetInt32(out var value) ? value : 0;
        }

        private static string? GetString(JsonElement root, params string[] path)
        {
            var current = root;
            foreach (var segment in path)
            {
                if (!current.TryGetProperty(segment, out current))
                {
                    return null;
                }
            }

            return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
        }

        private static async Task<string> ReadKubectlExitAsync(Process process, Task<string> outputTask, Task<string> errorTask, CancellationToken cancellationToken)
        {
            await process.WaitForExitAsync(cancellationToken);
            var output = (await outputTask).Trim();
            var error = (await errorTask).Trim();
            return string.IsNullOrWhiteSpace(error) ? output : error;
        }

        public static bool IsRecoverableKubectlException(Exception ex) =>
            ex is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException or IOException;

        private static string? ResolveKubeconfigPath(string? configuredPath)
        {
            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                return null;
            }

            if (Path.IsPathRooted(configuredPath))
            {
                return configuredPath;
            }

            foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            {
                var directory = new DirectoryInfo(start);
                while (directory is not null)
                {
                    var candidate = Path.Combine(directory.FullName, configuredPath);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }

                    directory = directory.Parent;
                }
            }

            return configuredPath;
        }
    }

    public sealed class PipelineJobOrchestrator(IConfiguration configuration, ILogger<PipelineJobOrchestrator> logger)
    {
        public Task<PreviewCleanupResult> ApplyAsync(string manifest, CancellationToken cancellationToken) =>
            RunKubectlAsync("apply -f -", manifest, cancellationToken);

        private async Task<PreviewCleanupResult> RunKubectlAsync(string command, string manifest, CancellationToken cancellationToken)
        {
            var kubectlPath = configuration["Pipelines:KubectlPath"] ?? configuration["Preview:KubectlPath"] ?? "kubectl";
            var kubeconfigPath = ResolveKubeconfigPath(configuration["Pipelines:KubeconfigPath"] ?? configuration["Preview:KubeconfigPath"] ?? "tofu/output/kubeconfig");
            if (!string.IsNullOrWhiteSpace(kubeconfigPath) && !File.Exists(kubeconfigPath))
            {
                return PreviewCleanupResult.Failed($"Pipeline job submission failed: Configured kubeconfig was not found: {kubeconfigPath}");
            }

            var arguments = string.IsNullOrWhiteSpace(kubeconfigPath)
                ? command
                : $"--kubeconfig \"{kubeconfigPath}\" {command}";

            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = kubectlPath,
                        Arguments = arguments,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
                try
                {
                    await process.StandardInput.WriteAsync(manifest);
                    process.StandardInput.Close();
                }
                catch (IOException ex)
                {
                    logger.LogWarning(ex, "Pipeline job submission stdin closed before manifest was written.");
                    var earlyExit = await ReadKubectlExitAsync(process, outputTask, errorTask, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(earlyExit))
                    {
                        return PreviewCleanupResult.Failed($"Pipeline job submission failed: {earlyExit}");
                    }

                    throw;
                }

                await process.WaitForExitAsync(cancellationToken);

                var output = (await outputTask).Trim();
                var error = (await errorTask).Trim();
                if (process.ExitCode == 0)
                {
                    return PreviewCleanupResult.Ok(string.IsNullOrWhiteSpace(output) ? "Pipeline job submitted." : output);
                }

                var message = string.IsNullOrWhiteSpace(error) ? output : error;
                return PreviewCleanupResult.Failed($"Pipeline job submission failed: {message}");
            }
            catch (Exception ex) when (PreviewEnvironmentOrchestrator.IsRecoverableKubectlException(ex))
            {
                logger.LogWarning(ex, "Pipeline job submission failed.");
                return PreviewCleanupResult.Failed($"Pipeline job submission failed: {ex.Message}");
            }
        }

        private static async Task<string> ReadKubectlExitAsync(Process process, Task<string> outputTask, Task<string> errorTask, CancellationToken cancellationToken)
        {
            await process.WaitForExitAsync(cancellationToken);
            var output = (await outputTask).Trim();
            var error = (await errorTask).Trim();
            return string.IsNullOrWhiteSpace(error) ? output : error;
        }

        private static string? ResolveKubeconfigPath(string? configuredPath)
        {
            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                return null;
            }

            if (Path.IsPathRooted(configuredPath))
            {
                return configuredPath;
            }

            foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            {
                var directory = new DirectoryInfo(start);
                while (directory is not null)
                {
                    var candidate = Path.Combine(directory.FullName, configuredPath);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }

                    directory = directory.Parent;
                }
            }

            return configuredPath;
        }
    }

    public sealed class PreviewHealthMonitor(DevOpsStore store, PreviewEnvironmentOrchestrator previews, ILogger<PreviewHealthMonitor> logger) : BackgroundService
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan PreviewTimeout = TimeSpan.FromMinutes(3);
        private readonly Dictionary<Guid, DateTimeOffset> _startedAt = [];

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CheckPendingPreviewsAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Preview health monitor failed.");
                }

                await Task.Delay(PollInterval, stoppingToken);
            }
        }

        private async Task CheckPendingPreviewsAsync(CancellationToken cancellationToken)
        {
            foreach (var preview in store.GetPreviewsAwaitingHealthCheck())
            {
                if (!_startedAt.ContainsKey(preview.Id))
                {
                    _startedAt[preview.Id] = preview.LastCheckedAt ?? DateTimeOffset.UtcNow;
                }

                if (DateTimeOffset.UtcNow - _startedAt[preview.Id] > PreviewTimeout)
                {
                    store.UpdatePreviewHealth(preview.WorkItemId, PreviewHealthCheckResult.Failed("Timeout", "Preview did not become healthy within 3 minutes.", null, preview.PodName));
                    _startedAt.Remove(preview.Id);
                    continue;
                }

                var health = await previews.CheckHealthAsync(preview, cancellationToken);
                store.UpdatePreviewHealth(preview.WorkItemId, health);
                if (!string.Equals(health.Status, "Provisioning", StringComparison.OrdinalIgnoreCase))
                {
                    _startedAt.Remove(preview.Id);
                }
            }
        }
    }

    public sealed class DevOpsStateDbContext(DbContextOptions<DevOpsStateDbContext> options) : DbContext(options)
    {
        public DbSet<DevOpsStateDocument> Documents => Set<DevOpsStateDocument>();
    }

    public sealed class DevOpsStateDocument
    {
        public string Id { get; set; } = "default";
        public string Json { get; set; } = "{}";
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    }

    public static class LocalNginxPreviewHtml
    {
        public const string Image = "nginxinc/nginx-unprivileged:1.27-alpine";

        public static string ForWorkItem(string key, string title, string description) =>
            ForWorkItem(key, title, description, []);

        public static string ForWorkItem(string key, string title, string description, IEnumerable<string> comments)
        {
            var context = string.Join("\n", new[] { title, description }.Concat(comments));
            if (ContainsAny(context, "hamburg", "burgare", "cheeseburgare", "fiskburgare", "burger"))
            {
                return BurgerOrderPage(key, title, description, context);
            }

            var heading = title.Contains("hello", StringComparison.OrdinalIgnoreCase) ? "Hello world" : title;
            var textColor = ContainsAny(context, "orange", "orange text", "orangefärgad", "orangefargad") ? "#ff8a00" : "#dae2fd";
            var background = ContainsAny(context, "gul bakgrund", "yellow background", "gul") ? "#ffd84d" : "#0b1326";
            var panel = ContainsAny(context, "gul bakgrund", "yellow background", "gul") ? "#fff3a6" : "#171f33";
            var paragraphColor = ContainsAny(context, "gul bakgrund", "yellow background", "gul") ? "#3e2400" : "#c2c6d6";
            return $$"""
                   <!doctype html>
                   <html lang="en">
                   <head>
                     <meta charset="utf-8">
                     <meta name="viewport" content="width=device-width, initial-scale=1">
                     <title>{{WebUtility.HtmlEncode(heading)}}</title>
                     <style>
                       body { margin: 0; font-family: system-ui, sans-serif; background: {{background}}; color: {{textColor}}; display: grid; min-height: 100vh; place-items: center; }
                       main { border: 1px solid #424754; background: {{panel}}; padding: 32px; max-width: 720px; }
                       h1 { margin: 0 0 12px; font-size: 40px; }
                       p { color: {{paragraphColor}}; line-height: 1.6; }
                       code { color: {{textColor}}; }
                     </style>
                   </head>
                   <body>
                     <main>
                       <h1>{{WebUtility.HtmlEncode(heading)}}</h1>
                       <p>{{WebUtility.HtmlEncode(description)}}</p>
                       <code>{{WebUtility.HtmlEncode(key)}} served by nginx</code>
                     </main>
                   </body>
                   </html>
                   """;
        }

        private static string BurgerOrderPage(string key, string title, string description, string context)
        {
            var background = ContainsAny(context, "grå", "gra", "grey", "gray") ? "#2d3449" : "#0b1326";
            var panel = ContainsAny(context, "grå", "gra", "grey", "gray") ? "#171f33" : "#1d2333";
            return $$"""
                   <!doctype html>
                   <html lang="sv">
                   <head>
                     <meta charset="utf-8">
                     <meta name="viewport" content="width=device-width, initial-scale=1">
                     <title>{{WebUtility.HtmlEncode(title)}}</title>
                     <style>
                       body { margin: 0; font-family: system-ui, sans-serif; background: {{background}}; color: #f4f7ff; min-height: 100vh; }
                       main { max-width: 980px; margin: 0 auto; padding: 40px 20px; }
                       header { border-bottom: 1px solid #424754; padding-bottom: 20px; margin-bottom: 24px; }
                       h1 { margin: 0 0 10px; font-size: 40px; }
                       p { color: #c2c6d6; line-height: 1.6; }
                       .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(240px, 1fr)); gap: 16px; }
                       .product, .summary { border: 1px solid #424754; background: {{panel}}; padding: 18px; }
                       .product h2 { margin: 0 0 8px; font-size: 22px; }
                       .price { color: #ffb95f; font-weight: 700; }
                       label { display: grid; gap: 8px; margin-top: 16px; color: #dae2fd; font-weight: 700; }
                       input { height: 40px; border: 1px solid #424754; background: #060e20; color: #dae2fd; padding: 0 10px; font: inherit; }
                       button { margin-top: 18px; min-height: 42px; border: 0; background: #adc6ff; color: #001a42; padding: 0 16px; font-weight: 800; cursor: pointer; }
                       .summary { margin-top: 18px; }
                       #result { color: #4edea3; font-weight: 700; }
                       code { color: #adc6ff; }
                     </style>
                   </head>
                   <body>
                     <main>
                       <header>
                         <h1>{{WebUtility.HtmlEncode(title)}}</h1>
                         <p>{{WebUtility.HtmlEncode(description)}}</p>
                         <code>{{WebUtility.HtmlEncode(key)}} preview</code>
                       </header>
                       <section class="grid" aria-label="Meny">
                         <article class="product">
                           <h2>Cheeseburgare</h2>
                           <p>Ost, dressing, sallad och rostat bröd.</p>
                           <div class="price">89 kr</div>
                           <label>Antal cheeseburgare<input id="cheese" type="number" min="0" value="0"></label>
                         </article>
                         <article class="product">
                           <h2>Fiskburgare</h2>
                           <p>Panerad fisk, citronkräm och krispig sallad.</p>
                           <div class="price">99 kr</div>
                           <label>Antal fiskburgare<input id="fish" type="number" min="0" value="0"></label>
                         </article>
                       </section>
                       <section class="summary">
                         <button id="order" type="button">Beställ</button>
                         <p id="result">Välj antal och klicka på Beställ.</p>
                       </section>
                     </main>
                     <script>
                       const prices = { cheese: 89, fish: 99 };
                       document.getElementById('order').addEventListener('click', () => {
                         const cheese = Number(document.getElementById('cheese').value || 0);
                         const fish = Number(document.getElementById('fish').value || 0);
                         const total = cheese * prices.cheese + fish * prices.fish;
                         const count = cheese + fish;
                         document.getElementById('result').textContent = count > 0
                           ? `Beställning klar: ${cheese} cheeseburgare och ${fish} fiskburgare. Totalt ${total} kr.`
                           : 'Välj minst en hamburgare innan du beställer.';
                       });
                     </script>
                   </body>
                   </html>
                   """;
        }

        private static bool ContainsAny(string value, params string[] needles) =>
            needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    public static class LocalReactPreviewProject
    {
        public const string Image = "ghcr.io/carnufex/rosenvall-devops-preview-base:main";

        public static IReadOnlyList<PreviewSourceFile> ForWorkItem(string key, string title, string description) =>
            ForWorkItem(key, title, description, []);

        public static IReadOnlyList<PreviewSourceFile> ForWorkItem(string key, string title, string description, IEnumerable<string> comments)
        {
            var context = string.Join("\n", new[] { title, description }.Concat(comments));
            return
            [
                File("package-json", "package.json", PackageJson()),
                File("index-html", "index.html", IndexHtml(title)),
                File("vite-config-ts", "vite.config.ts", ViteConfig()),
                File("tsconfig-json", "tsconfig.json", TsConfig()),
                File("tsconfig-app-json", "tsconfig.app.json", TsConfigApp()),
                File("tsconfig-node-json", "tsconfig.node.json", TsConfigNode()),
                File("postcss-config-js", "postcss.config.js", PostCssConfig()),
                File("tailwind-config-ts", "tailwind.config.ts", TailwindConfig()),
                File("components-json", "components.json", ComponentsJson()),
                File("src-index-css", "src/index.css", IndexCss()),
                File("src-main-tsx", "src/main.tsx", MainTsx()),
                File("src-app-tsx", "src/App.tsx", AppTsx(key, title, description, context)),
                File("src-lib-utils-ts", "src/lib/utils.ts", UtilsTs()),
                File("src-components-ui-button-tsx", "src/components/ui/button.tsx", ButtonTsx()),
                File("src-components-ui-card-tsx", "src/components/ui/card.tsx", CardTsx())
            ];
        }

        private static PreviewSourceFile File(string key, string path, string content) =>
            new(key, path, content.Trim());

        private static string PackageJson() =>
            """
            {
              "name": "rosenvall-ticket-preview",
              "private": true,
              "version": "0.0.0",
              "type": "module",
              "scripts": {
                "dev": "vite",
                "build": "tsc -b && vite build",
                "preview": "vite preview"
              },
              "dependencies": {
                "@radix-ui/react-slot": "^1.1.0",
                "class-variance-authority": "^0.7.1",
                "clsx": "^2.1.1",
                "lucide-react": "^0.462.0",
                "react": "^18.3.1",
                "react-dom": "^18.3.1",
                "tailwind-merge": "^2.5.2",
                "tailwindcss-animate": "^1.0.7"
              },
              "devDependencies": {
                "@types/node": "^22.5.5",
                "@types/react": "^18.3.3",
                "@types/react-dom": "^18.3.0",
                "@vitejs/plugin-react": "^5.0.0",
                "autoprefixer": "^10.4.20",
                "postcss": "^8.4.47",
                "tailwindcss": "^3.4.11",
                "typescript": "^5.5.3",
                "vite": "^5.4.1"
              }
            }
            """;

        private static string IndexHtml(string title) =>
            $$"""
            <!doctype html>
            <html lang="en">
              <head>
                <meta charset="UTF-8" />
                <meta name="viewport" content="width=device-width, initial-scale=1.0" />
                <title>{{WebUtility.HtmlEncode(title)}}</title>
              </head>
              <body>
                <div id="root"></div>
                <script type="module" src="/src/main.tsx"></script>
              </body>
            </html>
            """;

        private static string ViteConfig() =>
            """
            import { defineConfig } from "vite";
            import react from "@vitejs/plugin-react";
            import path from "path";

            export default defineConfig({
              server: {
                host: "0.0.0.0",
                port: 8080,
                allowedHosts: [".rosenvall.se"]
              },
              resolve: {
                alias: {
                  "@": path.resolve(__dirname, "./src")
                }
              },
              plugins: [react()]
            });
            """;

        private static string TsConfig() =>
            """
            {
              "files": [],
              "references": [
                { "path": "./tsconfig.app.json" },
                { "path": "./tsconfig.node.json" }
              ],
              "compilerOptions": {
                "baseUrl": ".",
                "paths": {
                  "@/*": ["./src/*"]
                }
              }
            }
            """;

        private static string TsConfigApp() =>
            """
            {
              "compilerOptions": {
                "target": "ES2020",
                "useDefineForClassFields": true,
                "lib": ["ES2020", "DOM", "DOM.Iterable"],
                "allowJs": false,
                "skipLibCheck": true,
                "esModuleInterop": true,
                "allowSyntheticDefaultImports": true,
                "strict": true,
                "forceConsistentCasingInFileNames": true,
                "module": "ESNext",
                "moduleResolution": "Node",
                "resolveJsonModule": true,
                "isolatedModules": true,
                "noEmit": true,
                "jsx": "react-jsx"
              },
              "include": ["src"]
            }
            """;

        private static string TsConfigNode() =>
            """
            {
              "compilerOptions": {
                "composite": true,
                "skipLibCheck": true,
                "module": "ESNext",
                "moduleResolution": "Node",
                "allowSyntheticDefaultImports": true
              },
              "include": ["vite.config.ts"]
            }
            """;

        private static string PostCssConfig() =>
            """
            export default {
              plugins: {
                tailwindcss: {},
                autoprefixer: {}
              }
            };
            """;

        private static string TailwindConfig() =>
            """
            import type { Config } from "tailwindcss";

            export default {
              darkMode: ["class"],
              content: ["./index.html", "./src/**/*.{ts,tsx}"],
              prefix: "",
              theme: {
                container: {
                  center: true,
                  padding: "2rem",
                  screens: {
                    "2xl": "1400px"
                  }
                },
                extend: {
                  colors: {
                    border: "hsl(var(--border))",
                    input: "hsl(var(--input))",
                    ring: "hsl(var(--ring))",
                    background: "hsl(var(--background))",
                    foreground: "hsl(var(--foreground))",
                    primary: {
                      DEFAULT: "hsl(var(--primary))",
                      foreground: "hsl(var(--primary-foreground))"
                    },
                    secondary: {
                      DEFAULT: "hsl(var(--secondary))",
                      foreground: "hsl(var(--secondary-foreground))"
                    },
                    muted: {
                      DEFAULT: "hsl(var(--muted))",
                      foreground: "hsl(var(--muted-foreground))"
                    },
                    accent: {
                      DEFAULT: "hsl(var(--accent))",
                      foreground: "hsl(var(--accent-foreground))"
                    },
                    card: {
                      DEFAULT: "hsl(var(--card))",
                      foreground: "hsl(var(--card-foreground))"
                    }
                  },
                  borderRadius: {
                    lg: "var(--radius)",
                    md: "calc(var(--radius) - 2px)",
                    sm: "calc(var(--radius) - 4px)"
                  }
                }
              },
              plugins: [require("tailwindcss-animate")]
            } satisfies Config;
            """;

        private static string ComponentsJson() =>
            """
            {
              "$schema": "https://ui.shadcn.com/schema.json",
              "style": "default",
              "rsc": false,
              "tsx": true,
              "tailwind": {
                "config": "tailwind.config.ts",
                "css": "src/index.css",
                "baseColor": "slate",
                "cssVariables": true,
                "prefix": ""
              },
              "aliases": {
                "components": "@/components",
                "utils": "@/lib/utils",
                "ui": "@/components/ui",
                "lib": "@/lib",
                "hooks": "@/hooks"
              }
            }
            """;

        private static string IndexCss() =>
            """
            @tailwind base;
            @tailwind components;
            @tailwind utilities;

            @layer base {
              :root {
                --background: 222.2 84% 4.9%;
                --foreground: 210 40% 98%;
                --card: 222.2 84% 4.9%;
                --card-foreground: 210 40% 98%;
                --primary: 210 40% 98%;
                --primary-foreground: 222.2 47.4% 11.2%;
                --secondary: 217.2 32.6% 17.5%;
                --secondary-foreground: 210 40% 98%;
                --muted: 217.2 32.6% 17.5%;
                --muted-foreground: 215 20.2% 65.1%;
                --accent: 217.2 32.6% 17.5%;
                --accent-foreground: 210 40% 98%;
                --border: 217.2 32.6% 17.5%;
                --input: 217.2 32.6% 17.5%;
                --ring: 212.7 26.8% 83.9%;
                --radius: 0.5rem;
              }

              * {
                @apply border-border;
              }

              body {
                @apply bg-background text-foreground;
              }
            }
            """;

        private static string MainTsx() =>
            """
            import React from "react";
            import ReactDOM from "react-dom/client";
            import App from "./App";
            import "./index.css";

            document.documentElement.classList.add("dark");

            ReactDOM.createRoot(document.getElementById("root")!).render(
              <React.StrictMode>
                <App />
              </React.StrictMode>
            );
            """;

        private static string AppTsx(string key, string title, string description, string context)
        {
            var isBurger = ContainsAny(context, "hamburg", "burgare", "cheeseburgare", "fiskburgare", "burger");
            var textClass = ContainsAny(context, "orange", "orange text", "orangefärgad", "orangefargad", "orangefÃ¤rgad") ? "text-[#ff8a00]" : "text-foreground";
            var backgroundClass = ContainsAny(context, "gul bakgrund", "yellow background", "gul") ? "bg-[#ffd84d]" : "bg-background";
            var panelClass = ContainsAny(context, "gul bakgrund", "yellow background", "gul") ? "bg-[#fff3a6] text-slate-950" : "bg-card";
            var mode = isBurger ? "burger" : "default";
            return $$"""
            import { Sparkles, ShoppingCart } from "lucide-react";
            import { Button } from "@/components/ui/button";
            import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
            import { useMemo, useState } from "react";

            const workItem = {
              key: {{Json(key)}},
              title: {{Json(title)}},
              description: {{Json(description)}},
              mode: {{Json(mode)}},
              textClass: {{Json(textClass)}},
              backgroundClass: {{Json(backgroundClass)}},
              panelClass: {{Json(panelClass)}}
            };

            const burgers = [
              { id: "cheese", name: "Cheeseburgare", description: "Ost, dressing, sallad och rostat bröd.", price: 89 },
              { id: "fish", name: "Fiskburgare", description: "Panerad fisk, citronkräm och krispig sallad.", price: 99 }
            ];

            export default function App() {
              const [quantities, setQuantities] = useState<Record<string, number>>({ cheese: 0, fish: 0 });
              const total = useMemo(
                () => burgers.reduce((sum, burger) => sum + burger.price * (quantities[burger.id] ?? 0), 0),
                [quantities]
              );
              const count = useMemo(
                () => burgers.reduce((sum, burger) => sum + (quantities[burger.id] ?? 0), 0),
                [quantities]
              );

              return (
                <main className={`min-h-screen ${workItem.backgroundClass} ${workItem.textClass}`}>
                  <section className="container grid min-h-screen content-center gap-8 py-10">
                    <div className="flex items-center gap-2 text-sm font-medium text-muted-foreground">
                      <Sparkles className="h-4 w-4" />
                      {workItem.key} React/Tailwind preview
                    </div>
                    <Card className={`${workItem.panelClass} border-border/80`}>
                      <CardHeader>
                        <CardTitle className="text-4xl">{workItem.title}</CardTitle>
                        <p className="max-w-3xl text-base leading-7 text-muted-foreground">{workItem.description}</p>
                      </CardHeader>
                      <CardContent>
                        {workItem.mode === "burger" ? (
                          <div className="grid gap-4 md:grid-cols-[1fr_320px]">
                            <div className="grid gap-4 md:grid-cols-2">
                              {burgers.map((burger) => (
                                <Card key={burger.id} className="bg-background/60">
                                  <CardHeader>
                                    <CardTitle className="text-xl">{burger.name}</CardTitle>
                                    <p className="text-sm text-muted-foreground">{burger.description}</p>
                                  </CardHeader>
                                  <CardContent className="grid gap-3">
                                    <strong className="text-amber-300">{burger.price} kr</strong>
                                    <label className="grid gap-2 text-sm font-medium">
                                      Antal {burger.name.toLowerCase()}
                                      <input
                                        className="h-10 rounded-md border border-input bg-background px-3"
                                        min={0}
                                        type="number"
                                        value={quantities[burger.id] ?? 0}
                                        onChange={(event) =>
                                          setQuantities((current) => ({ ...current, [burger.id]: Number(event.target.value || 0) }))
                                        }
                                      />
                                    </label>
                                  </CardContent>
                                </Card>
                              ))}
                            </div>
                            <Card className="bg-background/60">
                              <CardHeader>
                                <CardTitle className="flex items-center gap-2 text-xl">
                                  <ShoppingCart className="h-5 w-5" />
                                  Beställning
                                </CardTitle>
                              </CardHeader>
                              <CardContent className="grid gap-4">
                                <p className="text-sm text-muted-foreground">
                                  {count > 0
                                    ? `${count} hamburgare valda. Totalt ${total} kr.`
                                    : "Välj antal och klicka på Beställ."}
                                </p>
                                <Button>Beställ</Button>
                              </CardContent>
                            </Card>
                          </div>
                        ) : (
                          <div className="grid gap-4 md:grid-cols-3">
                            {["Plan", "Build", "Preview"].map((step) => (
                              <Card key={step} className="bg-background/60">
                                <CardHeader>
                                  <CardTitle className="text-xl">{step}</CardTitle>
                                </CardHeader>
                                <CardContent className="text-sm text-muted-foreground">
                                  React, TypeScript and Tailwind are ready for this ticket slice.
                                </CardContent>
                              </Card>
                            ))}
                          </div>
                        )}
                      </CardContent>
                    </Card>
                  </section>
                </main>
              );
            }
            """;
        }

        private static string UtilsTs() =>
            """
            import { type ClassValue, clsx } from "clsx";
            import { twMerge } from "tailwind-merge";

            export function cn(...inputs: ClassValue[]) {
              return twMerge(clsx(inputs));
            }
            """;

        private static string ButtonTsx() =>
            """
            import * as React from "react";
            import { Slot } from "@radix-ui/react-slot";
            import { cva, type VariantProps } from "class-variance-authority";
            import { cn } from "@/lib/utils";

            const buttonVariants = cva(
              "inline-flex h-10 items-center justify-center gap-2 rounded-md px-4 text-sm font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:pointer-events-none disabled:opacity-50",
              {
                variants: {
                  variant: {
                    default: "bg-primary text-primary-foreground hover:bg-primary/90",
                    secondary: "bg-secondary text-secondary-foreground hover:bg-secondary/80",
                    outline: "border border-input bg-background hover:bg-accent hover:text-accent-foreground"
                  }
                },
                defaultVariants: {
                  variant: "default"
                }
              }
            );

            export interface ButtonProps
              extends React.ButtonHTMLAttributes<HTMLButtonElement>,
                VariantProps<typeof buttonVariants> {
              asChild?: boolean;
            }

            const Button = React.forwardRef<HTMLButtonElement, ButtonProps>(
              ({ className, variant, asChild = false, ...props }, ref) => {
                const Comp = asChild ? Slot : "button";
                return <Comp className={cn(buttonVariants({ variant, className }))} ref={ref} {...props} />;
              }
            );
            Button.displayName = "Button";

            export { Button, buttonVariants };
            """;

        private static string CardTsx() =>
            """
            import * as React from "react";
            import { cn } from "@/lib/utils";

            const Card = React.forwardRef<HTMLDivElement, React.HTMLAttributes<HTMLDivElement>>(
              ({ className, ...props }, ref) => (
                <div ref={ref} className={cn("rounded-lg border bg-card text-card-foreground shadow-sm", className)} {...props} />
              )
            );
            Card.displayName = "Card";

            const CardHeader = React.forwardRef<HTMLDivElement, React.HTMLAttributes<HTMLDivElement>>(
              ({ className, ...props }, ref) => (
                <div ref={ref} className={cn("flex flex-col space-y-1.5 p-6", className)} {...props} />
              )
            );
            CardHeader.displayName = "CardHeader";

            const CardTitle = React.forwardRef<HTMLHeadingElement, React.HTMLAttributes<HTMLHeadingElement>>(
              ({ className, ...props }, ref) => (
                <h3 ref={ref} className={cn("font-semibold leading-none tracking-normal", className)} {...props} />
              )
            );
            CardTitle.displayName = "CardTitle";

            const CardContent = React.forwardRef<HTMLDivElement, React.HTMLAttributes<HTMLDivElement>>(
              ({ className, ...props }, ref) => <div ref={ref} className={cn("p-6 pt-0", className)} {...props} />
            );
            CardContent.displayName = "CardContent";

            export { Card, CardHeader, CardTitle, CardContent };
            """;

        private static string Json(string value) => JsonSerializer.Serialize(value);

        private static bool ContainsAny(string value, params string[] needles) =>
            needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    public interface IAiPlanProvider
    {
        string ProviderName { get; }

        Task<string> GeneratePlanAsync(string model, WorkItemDetailDto context, CancellationToken cancellationToken);
    }

    public sealed class AiPlanProviderRouter(IEnumerable<IAiPlanProvider> providers)
    {
        private readonly IReadOnlyDictionary<string, IAiPlanProvider> _providers = providers
            .GroupBy(provider => provider.ProviderName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        public Task<string> GeneratePlanAsync(string provider, string model, WorkItemDetailDto context, CancellationToken cancellationToken)
        {
            if (!_providers.TryGetValue(provider, out var planner))
            {
                throw new AiPlanProviderUnavailableException($"Provider '{provider}' is not configured for planning yet; no plan was created.");
            }

            return planner.GeneratePlanAsync(model, context, cancellationToken);
        }
    }

    public sealed class OllamaPlanProvider(HttpClient httpClient, IConfiguration configuration) : IAiPlanProvider
    {
        public string ProviderName => "ollama";

        public async Task<string> GeneratePlanAsync(string model, WorkItemDetailDto context, CancellationToken cancellationToken)
        {
            httpClient.Timeout = TimeSpan.FromSeconds(configuration.GetValue("Ai:RequestTimeoutSeconds", 120));
            var endpoint = configuration["Ai:OllamaEndpoint"] ?? configuration["Ai:Ollama:Endpoint"] ?? "http://localhost:11434/api";
            try
            {
                var response = await httpClient.PostAsJsonAsync(
                    BuildGenerateUri(endpoint),
                    new
                    {
                        model,
                        stream = false,
                        prompt = BuildPrompt(context)
                    },
                    cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync(cancellationToken);
                    throw new OllamaUnavailableException($"Ollama rejected model '{model}' at {endpoint}: {ExtractOllamaError(error)} No plan was created.");
                }

                var payload = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>(cancellationToken);
                if (!string.IsNullOrWhiteSpace(payload?.Response))
                {
                    return payload.Response.Trim();
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                throw new OllamaUnavailableException($"Ollama is unavailable at {endpoint}; no plan was created.");
            }

            throw new OllamaUnavailableException("Ollama returned an empty plan; no plan was created.");
        }

        private static string ExtractOllamaError(string error)
        {
            if (string.IsNullOrWhiteSpace(error))
            {
                return "request failed.";
            }

            try
            {
                using var document = JsonDocument.Parse(error);
                if (document.RootElement.TryGetProperty("error", out var element))
                {
                    if (element.ValueKind == JsonValueKind.String)
                    {
                        return element.GetString() ?? "request failed.";
                    }

                    if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("message", out var message))
                    {
                        return message.GetString() ?? "request failed.";
                    }
                }
            }
            catch (JsonException)
            {
                // Return raw text below.
            }

            return error.Trim();
        }

        private static Uri BuildGenerateUri(string endpoint)
        {
            var normalized = endpoint.TrimEnd('/');
            return new Uri(normalized.EndsWith("/api", StringComparison.OrdinalIgnoreCase)
                ? $"{normalized}/generate"
                : $"{normalized}/api/generate");
        }

        private static string BuildPrompt(WorkItemDetailDto context) =>
            $$"""
              You are Rosenvall DevOps AI. Produce a concise implementation plan for this work item.

              Work item: {{context.Item.Key}} {{context.Item.Title}}
              Type: {{context.Item.Type}}
              Status: {{context.Item.Status}}
              Priority: {{context.Item.Priority}}
              Description: {{context.Description}}
              Comments:
              {{string.Join("\n", context.Comments.Select(comment => $"- {comment.Author} ({comment.Kind}): {comment.Body}"))}}

              Required output:
              - A concrete plan.
              - Include tests.
              - Target a Vite React TypeScript preview app with Tailwind CSS and shadcn-style components.
              - The preview should be containerized and exposed through the existing Kubernetes preview route.
              - Preserve every concrete visual/content requirement from the title, description, and comments.
              - If colors, language, exact text, layout, or behavior are specified, repeat them explicitly in the plan.
              """;

        private sealed record OllamaGenerateResponse([property: JsonPropertyName("response")] string? Response);
    }

    public sealed class CodexCliPlanProvider(IConfiguration configuration, ILogger<CodexCliPlanProvider> logger) : IAiPlanProvider
    {
        public string ProviderName => "codex";

        public async Task<string> GeneratePlanAsync(string model, WorkItemDetailDto context, CancellationToken cancellationToken)
        {
            var codexPath = CodexExecutableResolver.Resolve(configuration["Ai:Codex:Path"] ?? "codex");
            var timeout = TimeSpan.FromSeconds(configuration.GetValue("Ai:Codex:RequestTimeoutSeconds", configuration.GetValue("Ai:RequestTimeoutSeconds", 120)));
            var outputPath = Path.Combine(Path.GetTempPath(), $"rosenvall-codex-plan-{Guid.NewGuid():N}.md");
            try
            {
                using var process = new Process
                {
                    StartInfo = BuildStartInfo(codexPath, model, outputPath),
                    EnableRaisingEvents = true
                };

                var started = process.Start();
                if (!started)
                {
                    throw new AiPlanProviderUnavailableException("Codex provider could not start; no plan was created.");
                }

                await process.StandardInput.WriteAsync(BuildPrompt(context).AsMemory(), cancellationToken);
                process.StandardInput.Close();

                var stdOut = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var stdErr = process.StandardError.ReadToEndAsync(cancellationToken);
                using var timeoutCancellation = new CancellationTokenSource(timeout);
                using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCancellation.Token);
                try
                {
                    await process.WaitForExitAsync(linkedCancellation.Token);
                }
                catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    TryKill(process);
                    throw new AiPlanProviderUnavailableException($"Codex provider timed out after {timeout.TotalSeconds:0} seconds; no plan was created.");
                }

                var output = await stdOut;
                var error = await stdErr;
                if (process.ExitCode != 0)
                {
                    var detail = FirstUsefulLine(error, output);
                    logger.LogWarning("Codex provider failed with exit code {ExitCode}: {Detail}", process.ExitCode, detail);
                    throw new AiPlanProviderUnavailableException($"Codex provider is not logged in on the server or failed to run: {detail} No plan was created.");
                }

                if (!File.Exists(outputPath))
                {
                    throw new AiPlanProviderUnavailableException("Codex provider did not return a plan; no plan was created.");
                }

                var plan = (await File.ReadAllTextAsync(outputPath, cancellationToken)).Trim();
                if (string.IsNullOrWhiteSpace(plan))
                {
                    throw new AiPlanProviderUnavailableException("Codex provider returned an empty plan; no plan was created.");
                }

                return plan;
            }
            catch (Exception ex) when (ex is not AiPlanProviderUnavailableException && (ex is System.ComponentModel.Win32Exception or InvalidOperationException))
            {
                throw new AiPlanProviderUnavailableException($"Codex provider is unavailable at '{codexPath}': {ex.Message} No plan was created.");
            }
            finally
            {
                try
                {
                    File.Delete(outputPath);
                }
                catch
                {
                    // Best-effort cleanup for transient Codex output.
                }
            }
        }

        private ProcessStartInfo BuildStartInfo(string codexPath, string model, string outputPath)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = codexPath
            };
            startInfo.RedirectStandardInput = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.StandardInputEncoding = Encoding.UTF8;
            startInfo.StandardOutputEncoding = Encoding.UTF8;
            startInfo.StandardErrorEncoding = Encoding.UTF8;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;

            startInfo.ArgumentList.Add("exec");
            startInfo.ArgumentList.Add("--ephemeral");
            startInfo.ArgumentList.Add("--ignore-user-config");
            startInfo.ArgumentList.Add("--ignore-rules");
            startInfo.ArgumentList.Add("--sandbox");
            startInfo.ArgumentList.Add("read-only");
            startInfo.ArgumentList.Add("--output-last-message");
            startInfo.ArgumentList.Add(outputPath);
            startInfo.ArgumentList.Add("-m");
            startInfo.ArgumentList.Add(model);
            startInfo.ArgumentList.Add("-");

            var codexHome = configuration["Ai:Codex:Home"];
            if (!string.IsNullOrWhiteSpace(codexHome))
            {
                startInfo.Environment["CODEX_HOME"] = codexHome.Trim();
            }

            return startInfo;
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Best-effort timeout cleanup.
            }
        }

        private static string FirstUsefulLine(string error, string output)
        {
            var line = (error + "\n" + output)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            return string.IsNullOrWhiteSpace(line) ? "unknown Codex CLI error." : line;
        }

        private static string BuildPrompt(WorkItemDetailDto context) =>
            $$"""
              You are Rosenvall DevOps AI. Produce a concise implementation plan for this work item.

              Work item: {{context.Item.Key}} {{context.Item.Title}}
              Type: {{context.Item.Type}}
              Status: {{context.Item.Status}}
              Priority: {{context.Item.Priority}}
              Description: {{context.Description}}
              Comments:
              {{string.Join("\n", context.Comments.Select(comment => $"- {comment.Author} ({comment.Kind}): {comment.Body}"))}}

              Required output:
              - A concrete plan.
              - Include tests.
              - Target a Vite React TypeScript preview app with Tailwind CSS and shadcn-style components.
              - The preview should be containerized and exposed through the existing Kubernetes preview route.
              - Preserve every concrete visual/content requirement from the title, description, and comments.
              - If colors, language, exact text, layout, or behavior are specified, repeat them explicitly in the plan.
              """;
    }

    public static class CodexExecutableResolver
    {
        private static readonly string[] PreferredWindowsExtensions = [".exe", ".cmd", ".bat", ".com"];

        public static string Resolve(string configuredPath)
        {
            var candidate = string.IsNullOrWhiteSpace(configuredPath) ? "codex" : configuredPath.Trim();
            if (Path.IsPathRooted(candidate) || candidate.Contains(Path.DirectorySeparatorChar) || candidate.Contains(Path.AltDirectorySeparatorChar))
            {
                return PreferExecutableSibling(candidate);
            }

            var pathEntries = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var directory in pathEntries)
            {
                var resolved = PreferExecutableSibling(Path.Combine(directory, candidate));
                if (File.Exists(resolved))
                {
                    return resolved;
                }
            }

            return candidate;
        }

        private static string PreferExecutableSibling(string candidate)
        {
            if (!OperatingSystem.IsWindows())
            {
                return candidate;
            }

            if (!string.IsNullOrWhiteSpace(Path.GetExtension(candidate)))
            {
                return candidate;
            }

            foreach (var extension in PreferredWindowsExtensions)
            {
                var withExtension = candidate + extension;
                if (File.Exists(withExtension))
                {
                    return withExtension;
                }
            }

            return candidate;
        }
    }

    public interface IPreviewSourceProvider
    {
        Task<IReadOnlyList<PreviewSourceFile>> GenerateSourceAsync(string model, AiRun run, WorkItemDetailDto context, CancellationToken cancellationToken);
    }

    public sealed class CodexCliPreviewSourceProvider(IConfiguration configuration, ILogger<CodexCliPreviewSourceProvider> logger) : IPreviewSourceProvider
    {
        private static readonly string[] SkippedDirectories = ["node_modules", "dist", ".git", ".codex"];

        public async Task<IReadOnlyList<PreviewSourceFile>> GenerateSourceAsync(string model, AiRun run, WorkItemDetailDto context, CancellationToken cancellationToken)
        {
            var codexPath = CodexExecutableResolver.Resolve(configuration["Ai:Codex:Path"] ?? "codex");
            var timeout = TimeSpan.FromSeconds(configuration.GetValue("Ai:Codex:ImplementationTimeoutSeconds", configuration.GetValue("Ai:Codex:RequestTimeoutSeconds", configuration.GetValue("Ai:RequestTimeoutSeconds", 180))));
            var workspacePath = Path.Combine(Path.GetTempPath(), $"rosenvall-preview-source-{Guid.NewGuid():N}");
            var outputPath = Path.Combine(Path.GetTempPath(), $"rosenvall-codex-preview-{Guid.NewGuid():N}.md");
            try
            {
                Directory.CreateDirectory(workspacePath);
                await SeedWorkspaceAsync(workspacePath, context, cancellationToken);

                using var process = new Process
                {
                    StartInfo = BuildStartInfo(codexPath, model, workspacePath, outputPath),
                    EnableRaisingEvents = true
                };

                var started = process.Start();
                if (!started)
                {
                    throw new AiPlanProviderUnavailableException("Codex preview source provider could not start; preview source was not generated.");
                }

                await process.StandardInput.WriteAsync(BuildImplementationPrompt(run, context).AsMemory(), cancellationToken);
                process.StandardInput.Close();

                var stdOut = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var stdErr = process.StandardError.ReadToEndAsync(cancellationToken);
                using var timeoutCancellation = new CancellationTokenSource(timeout);
                using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCancellation.Token);
                try
                {
                    await process.WaitForExitAsync(linkedCancellation.Token);
                }
                catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    TryKill(process);
                    throw new AiPlanProviderUnavailableException($"Codex preview source generation timed out after {timeout.TotalSeconds:0} seconds; no preview was deployed.");
                }

                var output = await stdOut;
                var error = await stdErr;
                if (process.ExitCode != 0)
                {
                    var detail = FirstUsefulLine(error, output);
                    logger.LogWarning("Codex preview source provider failed with exit code {ExitCode}: {Detail}", process.ExitCode, detail);
                    throw new AiPlanProviderUnavailableException($"Codex preview source provider is not logged in on the server or failed to run: {detail} No preview was deployed.");
                }

                var sourceFiles = await CollectWorkspaceSourceFilesAsync(workspacePath, cancellationToken);
                if (sourceFiles.All(file => !string.Equals(file.Path, "src/App.tsx", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new AiPlanProviderUnavailableException("Codex preview source generation did not produce src/App.tsx; no preview was deployed.");
                }

                return sourceFiles;
            }
            catch (Exception ex) when (ex is not AiPlanProviderUnavailableException && (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException))
            {
                throw new AiPlanProviderUnavailableException($"Codex preview source provider is unavailable at '{codexPath}': {ex.Message} No preview was deployed.");
            }
            finally
            {
                try
                {
                    File.Delete(outputPath);
                    if (Directory.Exists(workspacePath))
                    {
                        Directory.Delete(workspacePath, recursive: true);
                    }
                }
                catch
                {
                    // Best-effort cleanup for transient Codex preview workspace.
                }
            }
        }

        private ProcessStartInfo BuildStartInfo(string codexPath, string model, string workspacePath, string outputPath)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = codexPath,
                WorkingDirectory = workspacePath,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = Encoding.UTF8,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            startInfo.ArgumentList.Add("exec");
            startInfo.ArgumentList.Add("--ephemeral");
            startInfo.ArgumentList.Add("--ignore-user-config");
            startInfo.ArgumentList.Add("--ignore-rules");
            startInfo.ArgumentList.Add("--skip-git-repo-check");
            startInfo.ArgumentList.Add("-C");
            startInfo.ArgumentList.Add(workspacePath);
            if (configuration.GetValue("Ai:Codex:ImplementationBypassSandbox", OperatingSystem.IsWindows()))
            {
                startInfo.ArgumentList.Add("--dangerously-bypass-approvals-and-sandbox");
            }
            else
            {
                startInfo.ArgumentList.Add("--sandbox");
                startInfo.ArgumentList.Add("workspace-write");
            }
            startInfo.ArgumentList.Add("--output-last-message");
            startInfo.ArgumentList.Add(outputPath);
            startInfo.ArgumentList.Add("-m");
            startInfo.ArgumentList.Add(model);
            startInfo.ArgumentList.Add("-");

            var codexHome = configuration["Ai:Codex:Home"];
            if (!string.IsNullOrWhiteSpace(codexHome))
            {
                startInfo.Environment["CODEX_HOME"] = codexHome.Trim();
            }

            return startInfo;
        }

        private static async Task SeedWorkspaceAsync(string workspacePath, WorkItemDetailDto context, CancellationToken cancellationToken)
        {
            var humanComments = context.Comments
                .Where(comment => !string.Equals(comment.Author, "Rosenvall AI", StringComparison.OrdinalIgnoreCase))
                .OrderBy(comment => comment.CreatedAt)
                .Select(comment => comment.Body);
            foreach (var file in LocalReactPreviewProject.ForWorkItem(context.Item.Key, context.Item.Title, context.Description, humanComments))
            {
                var targetPath = Path.Combine(workspacePath, file.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                await File.WriteAllTextAsync(targetPath, file.Content, cancellationToken);
            }
        }

        private static async Task<IReadOnlyList<PreviewSourceFile>> CollectWorkspaceSourceFilesAsync(string workspacePath, CancellationToken cancellationToken)
        {
            var files = new List<PreviewSourceFile>();
            foreach (var path in Directory.EnumerateFiles(workspacePath, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var relativePath = Path.GetRelativePath(workspacePath, path).Replace('\\', '/');
                if (ShouldSkip(relativePath))
                {
                    continue;
                }

                var content = await File.ReadAllTextAsync(path, cancellationToken);
                files.Add(new PreviewSourceFile(SourceKeyForPath(relativePath), relativePath, content.TrimEnd()));
            }

            return files;
        }

        private static bool ShouldSkip(string relativePath) =>
            relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Any(part => SkippedDirectories.Contains(part, StringComparer.OrdinalIgnoreCase));

        private static string SourceKeyForPath(string path)
        {
            var key = new string(path.Select(character => char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '-').ToArray()).Trim('-');
            while (key.Contains("--", StringComparison.Ordinal))
            {
                key = key.Replace("--", "-", StringComparison.Ordinal);
            }

            return string.IsNullOrWhiteSpace(key) ? "source-file" : key;
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Best-effort timeout cleanup.
            }
        }

        private static string FirstUsefulLine(string error, string output)
        {
            var line = (error + "\n" + output)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            return string.IsNullOrWhiteSpace(line) ? "unknown Codex CLI error." : line;
        }

        private static string BuildImplementationPrompt(AiRun run, WorkItemDetailDto context) =>
            $$"""
              You are implementing a Rosenvall DevOps preview source workspace.

              Modify the existing Vite + React + TypeScript + Tailwind files in this current working directory.
              Do not install packages, do not run external services, and do not remove the Vite allowedHosts configuration for .rosenvall.se.
              Keep dependencies limited to the packages already present in package.json.
              Implement the approved AI plan as actual interactive React source, not a placeholder summary.
              Preserve concrete language, visual, behavior, and content requirements from the work item and comments.

              Work item: {{context.Item.Key}} {{context.Item.Title}}
              Type: {{context.Item.Type}}
              Priority: {{context.Item.Priority}}
              Description:
              {{context.Description}}

              Approved plan #{{run.SequenceNumber}}:
              {{run.Plan}}

              Activity context:
              {{string.Join("\n", context.Comments.Select(comment => $"- {comment.Author} ({comment.Kind}): {comment.Body}"))}}

              Required result:
              - Update source files in this workspace.
              - The app must compile with the seeded Vite React TypeScript project.
              - Return only a short implementation summary in your final message; the server reads source files from disk.
              """;
    }

    public sealed class DevOpsStore
    {
        private const string DocumentId = "default";
        private static readonly JsonSerializerOptions SnapshotJsonOptions = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = false,
            Converters = { new JsonStringEnumConverter() }
        };
        private readonly IDbContextFactory<DevOpsStateDbContext> _dbFactory;
        private readonly object _lock = new();
        private readonly List<WorkspaceDto> _workspaces = [];
        private readonly List<RepositoryDto> _repositories = [];
        private readonly List<BoardRecord> _boards = [];
        private readonly List<WorkItemRecord> _items = [];
        private readonly List<CommentDto> _comments = [];
        private readonly List<AiRun> _aiRuns = [];
        private readonly List<PreviewDto> _previews = [];
        private readonly List<DevelopmentDtoRecord> _development = [];
        private readonly List<PreviewEventDto> _previewEvents = [];
        private readonly List<PipelineRunDto> _pipelineRuns = [];
        private readonly List<TimelineEventDto> _timelineEvents = [];
        private int _nextTaskNumber = 4821;

        public DevOpsStore(IDbContextFactory<DevOpsStateDbContext> dbFactory)
        {
            _dbFactory = dbFactory;
            if (!TryLoad())
            {
                Seed();
                _nextTaskNumber = NextTaskNumberFromItems();
                Persist();
            }
        }

        public IReadOnlyList<WorkspaceDto> GetWorkspaces()
        {
            lock (_lock)
            {
                var items = _items.ToArray();
                var activeAi = items.Count(item => item.AiStatus is "Planning" or "ImplementationRunning");
                var openPrs = _development.Count(development => !string.IsNullOrWhiteSpace(development.Development.PullRequestUrl) && development.Development.PullRequestApprovedAt is null);
                var completed = items.Count(item => string.Equals(item.AiStatus, "Completed", StringComparison.OrdinalIgnoreCase));
                return _workspaces
                    .Select(workspace => workspace with
                    {
                        ActiveProjects = items.Length,
                        OpenPullRequests = openPrs,
                        SuccessfulAiImplementations = completed,
                        ComputeUsagePercent = Math.Min(100, _previews.Count(preview => preview.Status == "Running") * 10)
                    })
                    .ToArray();
            }
        }

        public WorkspaceDto CreateWorkspace(string name, string environmentName, string region)
        {
            lock (_lock)
            {
                var workspace = new WorkspaceDto(Guid.NewGuid(), name, environmentName, region, 0, 0, 0, 0);
                _workspaces.Add(workspace);
                _boards.Add(new BoardRecord(Guid.NewGuid(), workspace.Id, "Delivery Board", ["Todo", "In Progress", "AI Planning", "Review", "Done"]));
                Persist();
                return workspace;
            }
        }

        public IReadOnlyList<RepositoryDto> GetRepositories()
        {
            lock (_lock)
            {
                return _repositories
                    .OrderBy(repository => repository.Provider)
                    .ThenBy(repository => repository.Name)
                    .ToArray();
            }
        }

        public RepositoryDto CreateRepository(CreateRepositoryRequest request)
        {
            lock (_lock)
            {
                var repository = new RepositoryDto(
                    Guid.NewGuid(),
                    NormalizeText(request.Provider, "Forgejo"),
                    NormalizeText(request.Name, "repository"),
                    NormalizeText(request.RemoteUrl, "ssh://git.rosenvall.se/repository.git"),
                    string.IsNullOrWhiteSpace(request.WebUrl) ? null : request.WebUrl.Trim(),
                    NormalizeText(request.DefaultBranch, "main"),
                    DateTimeOffset.UtcNow);
                _repositories.Add(repository);
                Persist();
                return repository;
            }
        }

        public BoardDto? CreateBoard(Guid workspaceId, CreateBoardRequest request)
        {
            lock (_lock)
            {
                if (_workspaces.All(workspace => workspace.Id != workspaceId))
                {
                    return null;
                }

                var repository = ResolveBoardRepository(request);
                var board = new BoardRecord(
                    Guid.NewGuid(),
                    workspaceId,
                    NormalizeText(request.Name, repository?.Name ?? "Delivery Board"),
                    ["Todo", "In Progress", "AI Planning", "Review", "Done"],
                    repository?.Id);
                _boards.Add(board);
                AddTimelineEvent(board.Id, repository?.Id, null, "BoardCreated", board.Name, repository is null ? "Board created." : $"Board created for {repository.Name}.", "system", repository?.WebUrl);
                Persist();
                return ToBoardDto(board);
            }
        }

        public IReadOnlyList<BoardDto> GetBoards(Guid workspaceId)
        {
            lock (_lock)
            {
                return _boards.Where(b => b.WorkspaceId == workspaceId).Select(ToBoardDto).ToArray();
            }
        }

        public BoardDto? GetBoard(Guid boardId)
        {
            lock (_lock)
            {
                return _boards.Where(b => b.Id == boardId).Select(ToBoardDto).SingleOrDefault();
            }
        }

        public IReadOnlyList<WorkItemSummaryDto> GetWorkItems()
        {
            lock (_lock)
            {
                return _items.Select(ToSummary).ToArray();
            }
        }

        public WorkItemSummaryDto CreateWorkItem(CreateWorkItemRequest request)
        {
            lock (_lock)
            {
                var index = _nextTaskNumber++;
                var sortOrder = _items.Where(i => i.BoardId == request.BoardId && i.Status == request.Status)
                    .Select(i => i.SortOrder)
                    .DefaultIfEmpty(-1)
                    .Max() + 1;
                var item = new WorkItemRecord(Guid.NewGuid(), request.BoardId, $"TASK-{index}", request.Type, request.Title, request.Description, request.Status, request.Priority, request.Assignee, sortOrder);
                _items.Add(item);
                AddTimelineForItem(item, "CardCreated", item.Key, $"Created {item.Title}.", "system");
                Persist();
                return ToSummary(item);
            }
        }

        public WorkItemSummaryDto? UpdateWorkItem(Guid workItemId, UpdateWorkItemRequest request)
        {
            lock (_lock)
            {
                var item = _items.SingleOrDefault(i => i.Id == workItemId);
                if (item is null)
                {
                    return null;
                }

                item.Title = request.Title.Trim();
                item.Description = request.Description.Trim();
                item.Type = request.Type.Trim();
                item.Priority = request.Priority.Trim();
                item.Assignee = string.IsNullOrWhiteSpace(request.Assignee) ? null : request.Assignee.Trim();
                if (!string.Equals(item.Status, request.Status, StringComparison.Ordinal))
                {
                    PlaceItem(item, request.Status, int.MaxValue);
                }

                AddTimelineForItem(item, "CardUpdated", item.Key, $"Updated {item.Title}.", "system");
                Persist();
                return ToSummary(item);
            }
        }

        public WorkItemSummaryDto? MoveWorkItem(Guid workItemId, MoveWorkItemRequest request)
        {
            lock (_lock)
            {
                var item = _items.SingleOrDefault(i => i.Id == workItemId);
                if (item is null)
                {
                    return null;
                }

                PlaceItem(item, request.Status, request.SortOrder);
                AddTimelineForItem(item, "CardMoved", item.Key, $"Moved {item.Title} to {item.Status}.", "system");
                Persist();
                return ToSummary(item);
            }
        }

        public bool DeleteWorkItem(Guid workItemId, string actor = "system")
        {
            lock (_lock)
            {
                var item = _items.SingleOrDefault(i => i.Id == workItemId);
                if (item is null)
                {
                    return false;
                }

                var boardId = item.BoardId;
                var preview = _previews.SingleOrDefault(p => p.WorkItemId == workItemId);
                if (preview is not null)
                {
                    AddPreviewEvent(item, preview, "Deleted", actor, $"Preview resources deleted for {item.Key}.");
                }

                _items.Remove(item);
                _comments.RemoveAll(c => c.WorkItemId == workItemId);
                _aiRuns.RemoveAll(r => r.WorkItemId == workItemId);
                _previews.RemoveAll(p => p.WorkItemId == workItemId);
                _development.RemoveAll(d => d.WorkItemId == workItemId);
                AddTimelineForItem(item, "CardDeleted", item.Key, $"Deleted {item.Title}.", actor);
                NormalizeBoard(boardId);
                Persist();
                return true;
            }
        }

        public WorkItemDetailDto? GetWorkItemDetail(Guid workItemId)
        {
            lock (_lock)
            {
                var item = _items.SingleOrDefault(i => i.Id == workItemId);
                if (item is null)
                {
                    return null;
                }

                return new WorkItemDetailDto(
                    ToSummary(item),
                    item.Description,
                    _comments.Where(c => c.WorkItemId == item.Id).OrderBy(c => c.CreatedAt).ToArray(),
                    _previews.SingleOrDefault(p => p.WorkItemId == item.Id),
                    _development.SingleOrDefault(d => d.WorkItemId == item.Id)?.Development);
            }
        }

        public IReadOnlyList<AiRun> GetAiRuns(Guid workItemId)
        {
            lock (_lock)
            {
                return _aiRuns
                    .Where(run => run.WorkItemId == workItemId)
                    .OrderBy(run => run.SequenceNumber)
                    .ThenBy(run => run.CreatedAt)
                    .ToArray();
            }
        }

        public IReadOnlyList<PreviewEnvironmentDto> GetPreviewEnvironments()
        {
            lock (_lock)
            {
                return _previews
                    .Select(preview =>
                    {
                        var item = _items.SingleOrDefault(i => i.Id == preview.WorkItemId);
                        return new PreviewEnvironmentDto(
                            preview.Id,
                            item?.Id ?? preview.WorkItemId,
                            item?.Key ?? "deleted",
                            item?.Title ?? "Deleted work item",
                            preview.Url,
                            preview.Namespace ?? "",
                            preview.ResourceName ?? "",
                            preview.Image,
                            NormalizePreviewStatus(preview.Status),
                            preview.ExpiresAt,
                            preview.Phase,
                            preview.Message,
                            preview.LastCheckedAt,
                            preview.PodName,
                            preview.FailureReason,
                            preview.FailureLog);
                    })
                    .OrderBy(environment => environment.WorkItemKey)
                    .ToArray();
            }
        }

        public IReadOnlyList<PreviewEventDto> GetPreviewEvents()
        {
            lock (_lock)
            {
                return _previewEvents.OrderByDescending(e => e.CreatedAt).Take(50).ToArray();
            }
        }

        public IReadOnlyList<TimelineEventDto> GetTimeline(Guid? boardId = null)
        {
            lock (_lock)
            {
                return _timelineEvents
                    .Where(entry => boardId is null || entry.BoardId == boardId)
                    .OrderByDescending(entry => entry.CreatedAt)
                    .Take(100)
                    .ToArray();
            }
        }

        public PipelineRunDto? RecordPipelineRun(RecordPipelineRunRequest request)
        {
            lock (_lock)
            {
                if (_repositories.All(repository => repository.Id != request.RepositoryId))
                {
                    return null;
                }

                var now = DateTimeOffset.UtcNow;
                var run = new PipelineRunDto(
                    Guid.NewGuid(),
                    request.RepositoryId,
                    request.BoardId,
                    request.WorkItemId,
                    NormalizeText(request.Stage, "Pipeline"),
                    NormalizeText(request.Status, "Running"),
                    NormalizeText(request.Message, "Pipeline run recorded."),
                    string.IsNullOrWhiteSpace(request.Url) ? null : request.Url.Trim(),
                    now,
                    IsTerminalPipelineStatus(request.Status) ? now : null,
                    Math.Max(0, request.TokensUsed),
                    Math.Max(0, request.CodeAdded),
                    Math.Max(0, request.CodeDeleted));
                _pipelineRuns.Add(run);
                AddTimelineEvent(run.BoardId, run.RepositoryId, run.WorkItemId, "Pipeline", run.Stage, run.Message, "system", run.Url, now);
                Persist();
                return run;
            }
        }

        public PipelineRunDto? MarkPipelineRunExecuting(Guid pipelineRunId, string actor)
        {
            lock (_lock)
            {
                var index = _pipelineRuns.FindIndex(run => run.Id == pipelineRunId);
                if (index < 0)
                {
                    return null;
                }

                var existing = _pipelineRuns[index];
                var updated = existing with
                {
                    Status = "Running",
                    Message = $"Kubernetes Job submitted by {NormalizeText(actor, "system")}.",
                    CompletedAt = null
                };
                _pipelineRuns[index] = updated;
                AddTimelineEvent(updated.BoardId, updated.RepositoryId, updated.WorkItemId, "Pipeline", updated.Stage, updated.Message, NormalizeText(actor, "system"), updated.Url);
                Persist();
                return updated;
            }
        }

        public PipelineRunDto? MarkPipelineRunFailed(Guid pipelineRunId, string actor, string message)
        {
            lock (_lock)
            {
                var index = _pipelineRuns.FindIndex(run => run.Id == pipelineRunId);
                if (index < 0)
                {
                    return null;
                }

                var existing = _pipelineRuns[index];
                var updated = existing with
                {
                    Status = "Failed",
                    Message = NormalizeText(message, "Pipeline job failed."),
                    CompletedAt = DateTimeOffset.UtcNow
                };
                _pipelineRuns[index] = updated;
                AddTimelineEvent(updated.BoardId, updated.RepositoryId, updated.WorkItemId, "Pipeline", updated.Stage, updated.Message, NormalizeText(actor, "system"), updated.Url);
                Persist();
                return updated;
            }
        }

        public MetricsDto GetMetrics(Guid? boardId = null)
        {
            lock (_lock)
            {
                var runs = _pipelineRuns
                    .Where(run => boardId is null || run.BoardId == boardId)
                    .ToArray();
                return new MetricsDto(
                    boardId,
                    runs.Sum(run => run.TokensUsed),
                    runs.Sum(run => run.CodeAdded),
                    runs.Sum(run => run.CodeDeleted),
                    runs.Length);
            }
        }

        public IReadOnlyList<AssigneeDto> GetAssignees(Guid? boardId, IConfiguration configuration)
        {
            lock (_lock)
            {
                var assignees = new Dictionary<string, AssigneeDto>(StringComparer.OrdinalIgnoreCase);
                foreach (var configured in configuration.GetSection("Authentik:Users").Get<ConfiguredAuthentikUser[]>() ?? [])
                {
                    var displayName = NormalizeText(configured.DisplayName ?? configured.Name ?? configured.Username, configured.Email ?? "Authentik user");
                    var email = NormalizeText(configured.Email, displayName);
                    assignees[email] = new AssigneeDto(NormalizeText(configured.Id, email), displayName, email, "Authentik");
                }

                foreach (var assignee in _items
                    .Where(item => boardId is null || item.BoardId == boardId)
                    .Select(item => item.Assignee)
                    .Where(assignee => !string.IsNullOrWhiteSpace(assignee))
                    .Select(assignee => assignee!.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!assignees.ContainsKey(assignee))
                    {
                        assignees[assignee] = new AssigneeDto(assignee, assignee, assignee, "Board");
                    }
                }

                return assignees.Values
                    .OrderBy(assignee => assignee.DisplayName)
                    .ToArray();
            }
        }

        public IReadOnlyList<PipelineStatusDto> GetPipelineStatuses()
        {
            lock (_lock)
            {
                var pipelines = new List<PipelineStatusDto>();
                foreach (var item in _items.OrderBy(i => i.Key))
                {
                    if (!string.IsNullOrWhiteSpace(item.AiStatus))
                    {
                        pipelines.Add(new PipelineStatusDto(Guid.NewGuid(), item.Id, item.Key, item.Title, "AI", item.AiStatus!, $"AI state is {item.AiStatus}.", DateTimeOffset.UtcNow));
                    }

                    var preview = _previews.SingleOrDefault(p => p.WorkItemId == item.Id);
                    if (preview is not null)
                    {
                        pipelines.Add(new PipelineStatusDto(Guid.NewGuid(), item.Id, item.Key, item.Title, "Preview", NormalizePreviewStatus(preview.Status), preview.Url, DateTimeOffset.UtcNow));
                    }

                    var development = _development.SingleOrDefault(d => d.WorkItemId == item.Id)?.Development;
                    if (development is not null)
                    {
                        pipelines.Add(new PipelineStatusDto(Guid.NewGuid(), item.Id, item.Key, item.Title, "PR", development.PullRequestApprovedAt is null ? development.ChecksStatus : "Approved", development.PullRequestUrl ?? development.ChecksStatus, development.PullRequestApprovedAt ?? DateTimeOffset.UtcNow));
                    }
                }

                foreach (var run in _pipelineRuns.OrderByDescending(run => run.StartedAt))
                {
                    var item = run.WorkItemId is null ? null : _items.SingleOrDefault(i => i.Id == run.WorkItemId.Value);
                    pipelines.Add(new PipelineStatusDto(
                        run.Id,
                        run.WorkItemId,
                        item?.Key ?? "repo",
                        item?.Title ?? RepositoryName(run.RepositoryId),
                        run.Stage,
                        run.Status,
                        run.Message,
                        run.CompletedAt ?? run.StartedAt));
                }

                return pipelines.ToArray();
            }
        }

        public CommentDto? AddComment(Guid workItemId, string author, string kind, string body)
        {
            lock (_lock)
            {
                if (_items.All(i => i.Id != workItemId))
                {
                    return null;
                }

                var comment = new CommentDto(Guid.NewGuid(), workItemId, author, kind, body, DateTimeOffset.UtcNow);
                _comments.Add(comment);
                Persist();
                return comment;
            }
        }

        public CommentDto? UpdateComment(Guid commentId, string actor, string body)
        {
            lock (_lock)
            {
                var index = _comments.FindIndex(comment => comment.Id == commentId);
                if (index < 0)
                {
                    return null;
                }

                var existing = _comments[index];
                EnsureEditableHumanComment(existing, actor);
                if (string.IsNullOrWhiteSpace(body))
                {
                    throw new ArgumentException("Comment body is required.", nameof(body));
                }

                var updated = existing with { Body = body.Trim() };
                _comments[index] = updated;
                Persist();
                return updated;
            }
        }

        public bool DeleteComment(Guid commentId, string actor)
        {
            lock (_lock)
            {
                var index = _comments.FindIndex(comment => comment.Id == commentId);
                if (index < 0)
                {
                    return false;
                }

                EnsureEditableHumanComment(_comments[index], actor);
                _comments.RemoveAt(index);
                Persist();
                return true;
            }
        }

        public AiRun? StartAiPlan(Guid workItemId, string provider, string model, string? plan = null)
        {
            lock (_lock)
            {
                var item = _items.SingleOrDefault(i => i.Id == workItemId);
                if (item is null)
                {
                    return null;
                }

                item.Status = "AI Planning";
                item.AiStatus = "Planning";
                var sequenceNumber = _aiRuns
                    .Where(existing => existing.WorkItemId == workItemId)
                    .Select(existing => existing.SequenceNumber)
                    .DefaultIfEmpty(0)
                    .Max() + 1;
                var run = AiRun.Start(workItemId, provider, model, sequenceNumber, DateTimeOffset.UtcNow);
                run.PostPlan(string.IsNullOrWhiteSpace(plan) ? BuildPlan(item) : plan);
                _aiRuns.Add(run);
                _comments.Add(new CommentDto(Guid.NewGuid(), workItemId, "Rosenvall AI", "Result", $"Created plan #{run.SequenceNumber}: {item.Title}.", run.CreatedAt));
                item.AiStatus = "PlanReady";
                AddTimelineForItem(item, "AiPlanReady", item.Key, $"AI plan ready for {item.Title}.", "Rosenvall AI");
                Persist();
                return run;
            }
        }

        public AiRun? ApproveAiRun(Guid aiRunId, string approvedBy)
        {
            lock (_lock)
            {
                var run = _aiRuns.SingleOrDefault(r => r.Id == aiRunId);
                if (run is null)
                {
                    return null;
                }

                if (run.Status == AiRunStatus.PlanReady)
                {
                    run.Approve(approvedBy);
                }
                else if (run.Status != AiRunStatus.Approved)
                {
                    throw new InvalidOperationException("Only ready or approved AI plans can be implemented.");
                }

                var item = _items.Single(i => i.Id == run.WorkItemId);
                item.Status = "Review";
                item.AiStatus = "ImplementationRunning";
                _comments.Add(new CommentDto(Guid.NewGuid(), item.Id, "Rosenvall AI", "Result", $"Implementing plan #{run.SequenceNumber} with Codex preview source generation.", DateTimeOffset.UtcNow));
                AddTimelineForItem(item, "AiPlanApproved", item.Key, $"AI plan approved by {approvedBy}.", approvedBy);
                Persist();
                return run;
            }
        }

        public WorkItemDetailDto? CompleteLocalReactImplementation(Guid workItemId)
        {
            lock (_lock)
            {
                var item = _items.SingleOrDefault(i => i.Id == workItemId);
                if (item is null)
                {
                    return null;
                }

                var humanComments = _comments
                    .Where(comment => comment.WorkItemId == item.Id && !string.Equals(comment.Author, "Rosenvall AI", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(comment => comment.CreatedAt)
                    .Select(comment => comment.Body);
                var sourceFiles = LocalReactPreviewProject.ForWorkItem(item.Key, item.Title, item.Description, humanComments);
                var resources = PreviewResourceSet.Create(item.Key, item.Title, LocalReactPreviewProject.Image, sourceFiles: sourceFiles);
                item.PullRequestUrl = null;
                item.AiStatus = "Completed";
                item.Status = "Review";

                var preview = new PreviewDto(Guid.NewGuid(), item.Id, $"https://{resources.Hostname}", resources.Image, "Implementing", DateTimeOffset.UtcNow.AddDays(7), null, resources.Namespace, resources.Name, "Implementing preview source", "Generating local React/Tailwind source from the card context.", SourceFiles: sourceFiles);
                _previews.RemoveAll(p => p.WorkItemId == item.Id);
                _previews.Add(preview);
                AddPreviewEvent(item, preview, "Created", "system", $"Preview created for {item.Key}.");
                _development.RemoveAll(d => d.WorkItemId == item.Id);
                _development.Add(new DevelopmentDtoRecord(item.Id, new DevelopmentDto("local/vite-react-tailwind", $"local/{resources.Name}", null, "Local React/Tailwind source generated")));
                _comments.Add(new CommentDto(Guid.NewGuid(), item.Id, "Rosenvall AI", "Result", $"Local React/Tailwind implementation completed. Preview provisioning started for {preview.Url}.", DateTimeOffset.UtcNow));
                RecordPipelineRunWithoutLock(new RecordPipelineRunRequest(RepositoryIdForBoard(item.BoardId) ?? EnsureLocalRepository().Id, item.BoardId, item.Id, "Preview", "Succeeded", "Kubernetes preview job completed", preview.Url));
                Persist();
                return GetWorkItemDetail(item.Id);
            }
        }

        public WorkItemDetailDto? CompletePreviewImplementation(Guid workItemId, IReadOnlyList<PreviewSourceFile> sourceFiles, string implementationProvider)
        {
            lock (_lock)
            {
                var item = _items.SingleOrDefault(i => i.Id == workItemId);
                if (item is null)
                {
                    return null;
                }

                if (sourceFiles.Count == 0)
                {
                    throw new ArgumentException("Preview source files are required.", nameof(sourceFiles));
                }

                var resources = PreviewResourceSet.Create(item.Key, item.Title, LocalReactPreviewProject.Image, sourceFiles: sourceFiles);
                item.PullRequestUrl = null;
                item.AiStatus = "Completed";
                item.Status = "Review";

                var providerName = NormalizeText(implementationProvider, "codex");
                var preview = new PreviewDto(
                    Guid.NewGuid(),
                    item.Id,
                    $"https://{resources.Hostname}",
                    resources.Image,
                    "Implementing",
                    DateTimeOffset.UtcNow.AddDays(7),
                    null,
                    resources.Namespace,
                    resources.Name,
                    "Implementing preview source",
                    $"{providerName} generated React/Tailwind preview source from the approved plan.",
                    SourceFiles: sourceFiles);
                _previews.RemoveAll(p => p.WorkItemId == item.Id);
                _previews.Add(preview);
                AddPreviewEvent(item, preview, "Created", providerName, $"Preview source generated for {item.Key}.");
                _development.RemoveAll(d => d.WorkItemId == item.Id);
                _development.Add(new DevelopmentDtoRecord(item.Id, new DevelopmentDto($"local/{providerName}-preview-source", $"local/{resources.Name}", null, "Codex preview source generated")));
                _comments.Add(new CommentDto(Guid.NewGuid(), item.Id, "Rosenvall AI", "Result", $"Codex preview source generated from the approved plan. Preview provisioning started for {preview.Url}.", DateTimeOffset.UtcNow));
                RecordPipelineRunWithoutLock(new RecordPipelineRunRequest(RepositoryIdForBoard(item.BoardId) ?? EnsureLocalRepository().Id, item.BoardId, item.Id, "Preview", "Succeeded", "Kubernetes preview job completed", preview.Url));
                Persist();
                return GetWorkItemDetail(item.Id);
            }
        }

        public void RecordImplementationFailure(Guid workItemId, string actor, string message)
        {
            lock (_lock)
            {
                var item = _items.SingleOrDefault(i => i.Id == workItemId);
                if (item is null)
                {
                    return;
                }

                item.AiStatus = "ImplementationFailed";
                _comments.Add(new CommentDto(Guid.NewGuid(), item.Id, "Rosenvall AI", "Result", $"Preview source implementation failed: {message}", DateTimeOffset.UtcNow));
                AddTimelineForItem(item, "AiImplementationFailed", item.Key, message, actor);
                Persist();
            }
        }

        public AiRun? DiscardAiRun(Guid aiRunId, string discardedBy)
        {
            lock (_lock)
            {
                var run = _aiRuns.SingleOrDefault(r => r.Id == aiRunId);
                if (run is null)
                {
                    return null;
                }

                run.Discard(discardedBy);
                var item = _items.SingleOrDefault(i => i.Id == run.WorkItemId);
                if (item is not null)
                {
                    if (item.Status == "AI Planning")
                    {
                        item.Status = "Todo";
                        item.SortOrder = _items.Where(i => i.BoardId == item.BoardId && i.Status == item.Status && i.Id != item.Id)
                            .Select(i => i.SortOrder)
                            .DefaultIfEmpty(-1)
                            .Max() + 1;
                    }

                    item.AiStatus = null;
                    _comments.Add(new CommentDto(Guid.NewGuid(), item.Id, "Rosenvall AI", "Result", $"AI plan discarded by {discardedBy}.", DateTimeOffset.UtcNow));
                    AddTimelineForItem(item, "AiPlanDiscarded", item.Key, $"AI plan discarded by {discardedBy}.", discardedBy);
                    NormalizeBoard(item.BoardId);
                }

                Persist();
                return run;
            }
        }

        public WorkItemDetailDto? ApplyGitHubCallback(GitHubCallbackRequest request)
        {
            lock (_lock)
            {
                var item = _items.SingleOrDefault(i => i.Id == request.WorkItemId);
                if (item is null)
                {
                    return null;
                }

                item.PullRequestUrl = request.PullRequestUrl;
                item.AiStatus = "Completed";
                var resources = PreviewResourceSet.Create(item.Key, item.Title, request.Image, request.StaticHtml);
                var preview = new PreviewDto(Guid.NewGuid(), item.Id, $"https://{resources.Hostname}", resources.Image, "Running", DateTimeOffset.UtcNow.AddDays(7), request.StaticHtml, resources.Namespace, resources.Name);
                _previews.RemoveAll(p => p.WorkItemId == item.Id);
                _previews.Add(preview);
                AddPreviewEvent(item, preview, "Created", "github", $"Preview created from callback for {item.Key}.");
                _development.RemoveAll(d => d.WorkItemId == item.Id);
                _development.Add(new DevelopmentDtoRecord(item.Id, new DevelopmentDto(request.Repository, request.Branch, request.PullRequestUrl, request.ChecksStatus)));
                var resultMessage = string.IsNullOrWhiteSpace(request.PullRequestUrl)
                    ? $"Feature implemented by {request.Repository}. Demo is available at {preview.Url}."
                    : $"Feature implemented in {request.PullRequestUrl}. Demo is available at {preview.Url}.";
                _comments.Add(new CommentDto(Guid.NewGuid(), item.Id, "Rosenvall AI", "Result", resultMessage, DateTimeOffset.UtcNow));
                var repositoryId = ResolveRepositoryForDevelopment(item.BoardId, request.Repository);
                AddTimelineEvent(item.BoardId, repositoryId, item.Id, "PullRequest", item.Key, string.IsNullOrWhiteSpace(request.PullRequestUrl) ? $"Implementation callback from {request.Repository}." : $"Pull request opened for {item.Key}.", request.Repository, request.PullRequestUrl);
                RecordPipelineRunWithoutLock(new RecordPipelineRunRequest(repositoryId ?? EnsureLocalRepository().Id, item.BoardId, item.Id, "Checks", request.ChecksStatus, request.ChecksStatus, request.PullRequestUrl));
                Persist();
                return GetWorkItemDetail(item.Id);
            }
        }

        public WorkItemDetailDto? ApprovePullRequest(Guid workItemId, string approvedBy)
        {
            lock (_lock)
            {
                var item = _items.SingleOrDefault(i => i.Id == workItemId);
                var currentDevelopment = _development.SingleOrDefault(d => d.WorkItemId == workItemId);
                if (item is null || currentDevelopment is null || string.IsNullOrWhiteSpace(currentDevelopment.Development.PullRequestUrl))
                {
                    return null;
                }

                var approvedAt = DateTimeOffset.UtcNow;
                var preview = _previews.SingleOrDefault(p => p.WorkItemId == workItemId);
                if (preview is not null)
                {
                    _previews.Remove(preview);
                    var stopped = preview with { Status = "Stopped" };
                    _previews.Add(stopped);
                    AddPreviewEvent(item, stopped, "Stopped", approvedBy, "Preview stopped after PR approval.");
                    AddPreviewEvent(item, stopped, "PrApproved", approvedBy, $"Pull request approved by {approvedBy}.");
                }

                _development.Remove(currentDevelopment);
                _development.Add(new DevelopmentDtoRecord(
                    workItemId,
                    currentDevelopment.Development with
                    {
                        ChecksStatus = $"PR approved by {approvedBy}",
                        PullRequestApprovedBy = approvedBy,
                        PullRequestApprovedAt = approvedAt
                    }));
                item.Status = "Done";
                _comments.Add(new CommentDto(Guid.NewGuid(), item.Id, approvedBy, "Result", $"Pull request approved by {approvedBy}.", approvedAt));
                AddTimelineForItem(item, "CardClosed", item.Key, $"Closed {item.Title}.", approvedBy, currentDevelopment.Development.PullRequestUrl, approvedAt);
                NormalizeBoard(item.BoardId);
                Persist();
                return GetWorkItemDetail(workItemId);
            }
        }

        public WorkItemDetailDto? MarkPreviewRunning(Guid workItemId, string actor, string message)
        {
            lock (_lock)
            {
                var item = _items.SingleOrDefault(i => i.Id == workItemId);
                var preview = _previews.SingleOrDefault(p => p.WorkItemId == workItemId);
                if (item is null || preview is null)
                {
                    return null;
                }

                _previews.Remove(preview);
                var running = preview with { Status = "Running", Phase = "Ready", Message = message, LastCheckedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7), FailureReason = null, FailureLog = null };
                _previews.Add(running);
                AddPreviewEvent(item, running, "Started", actor, message);
                Persist();
                return GetWorkItemDetail(workItemId);
            }
        }

        public WorkItemDetailDto? MarkPreviewProvisioning(Guid workItemId, string message)
        {
            lock (_lock)
            {
                var item = _items.SingleOrDefault(i => i.Id == workItemId);
                var preview = _previews.SingleOrDefault(p => p.WorkItemId == workItemId);
                if (item is null || preview is null)
                {
                    return null;
                }

                _previews.Remove(preview);
                var provisioning = preview with
                {
                    Status = "Provisioning",
                    Phase = "Waiting for pod readiness.",
                    Message = NormalizeText(message, "Kubernetes resources applied. Waiting for preview pod to become ready."),
                    LastCheckedAt = DateTimeOffset.UtcNow,
                    FailureReason = null,
                    FailureLog = null
                };
                _previews.Add(provisioning);
                AddPreviewEvent(item, provisioning, "Provisioning", "system", provisioning.Message ?? "Waiting for preview pod readiness.");
                Persist();
                return GetWorkItemDetail(workItemId);
            }
        }

        public WorkItemDetailDto? UpdatePreviewHealth(Guid workItemId, PreviewHealthCheckResult health)
        {
            lock (_lock)
            {
                var item = _items.SingleOrDefault(i => i.Id == workItemId);
                var preview = _previews.SingleOrDefault(p => p.WorkItemId == workItemId);
                if (item is null || preview is null)
                {
                    return null;
                }

                _previews.Remove(preview);
                var updated = preview with
                {
                    Status = health.Status,
                    Phase = health.Phase,
                    Message = health.Message,
                    LastCheckedAt = DateTimeOffset.UtcNow,
                    PodName = health.PodName,
                    FailureReason = health.FailureReason,
                    FailureLog = health.FailureLog,
                    ExpiresAt = string.Equals(health.Status, "Running", StringComparison.OrdinalIgnoreCase) ? DateTimeOffset.UtcNow.AddDays(7) : preview.ExpiresAt
                };
                _previews.Add(updated);
                if (string.Equals(health.Status, "Running", StringComparison.OrdinalIgnoreCase))
                {
                    AddPreviewEvent(item, updated, "Started", "health-check", health.Message);
                }
                else if (string.Equals(health.Status, "Failed", StringComparison.OrdinalIgnoreCase))
                {
                    AddPreviewEvent(item, updated, "HealthFailed", "health-check", health.Message);
                }
                Persist();
                return GetWorkItemDetail(workItemId);
            }
        }

        public IReadOnlyList<PreviewDto> GetPreviewsAwaitingHealthCheck()
        {
            lock (_lock)
            {
                return _previews
                    .Where(preview => preview.WorkItemId != Guid.Empty &&
                        (string.Equals(preview.Status, "Provisioning", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(preview.Status, "Applying", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(preview.Status, "Running", StringComparison.OrdinalIgnoreCase)))
                    .ToArray();
            }
        }

        public WorkItemDetailDto? StopPreview(Guid workItemId, string actor, string message)
        {
            lock (_lock)
            {
                var item = _items.SingleOrDefault(i => i.Id == workItemId);
                var preview = _previews.SingleOrDefault(p => p.WorkItemId == workItemId);
                if (item is null || preview is null)
                {
                    return null;
                }

                _previews.Remove(preview);
                var stopped = preview with { Status = "Stopped" };
                _previews.Add(stopped);
                AddPreviewEvent(item, stopped, "Stopped", actor, message);
                Persist();
                return GetWorkItemDetail(workItemId);
            }
        }

        public void RecordPreviewFailure(Guid workItemId, string eventType, string actor, string message)
        {
            lock (_lock)
            {
                var item = _items.SingleOrDefault(i => i.Id == workItemId);
                var preview = _previews.SingleOrDefault(p => p.WorkItemId == workItemId);
                if (item is null || preview is null)
                {
                    return;
                }

                _previews.Remove(preview);
                var failed = preview with
                {
                    Status = "Failed",
                    Phase = "Failed",
                    Message = message,
                    LastCheckedAt = DateTimeOffset.UtcNow,
                    FailureReason = eventType,
                    FailureLog = null
                };
                _previews.Add(failed);
                AddPreviewEvent(item, failed, eventType, actor, message);
                Persist();
            }
        }

        public SettingsDto GetSettings(IConfiguration configuration)
        {
            var configuredActiveModel = configuration["Ai:DefaultModel"] ?? configuration["Ai:Ollama:Model"];
            var activeModel = string.IsNullOrWhiteSpace(configuredActiveModel) ? "qwen3.5:latest" : configuredActiveModel.Trim();
            var configuredModels = configuration.GetSection("Ai:AvailableModels").Get<string[]>() ?? [];
            var availableModels = new[] { activeModel }
                .Concat(configuredModels)
                .Where(model => !string.IsNullOrWhiteSpace(model))
                .Select(model => model.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var codexActiveModel = NormalizeText(configuration["Ai:Codex:Model"], "gpt-5.4");
            var codexModels = new[] { codexActiveModel }
                .Concat(configuration.GetSection("Ai:Codex:AvailableModels").Get<string[]>() ?? [])
                .Where(model => !string.IsNullOrWhiteSpace(model))
                .Select(model => model.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var codexPath = configuration["Ai:Codex:Path"] ?? "codex";
            var configuredCodexHome = configuration["Ai:Codex:Home"];
            var codexHome = !string.IsNullOrWhiteSpace(configuredCodexHome)
                ? configuredCodexHome
                : Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            var codexStatus = ResolveCodexStatus(codexPath, codexHome);
            var providers = new[]
            {
                new AiProviderSettingsDto(
                    "ollama",
                    "Ollama",
                    "Ready",
                    configuration["Ai:OllamaEndpoint"] ?? configuration["Ai:Ollama:Endpoint"] ?? "http://localhost:11434/api",
                    activeModel,
                    availableModels),
                new AiProviderSettingsDto(
                    "codex",
                    "Codex",
                    codexStatus,
                    codexPath,
                    codexActiveModel,
                    codexModels)
            };

            return new(
                new GitHubSettingsDto("rosenvall-corp / core-infrastructure", "rosenvall-corp/core-infrastructure", "main, release/*, feat/*", true),
                new AiSettingsDto(
                    configuration["Ai:DefaultProvider"] ?? "ollama",
                    configuration["Ai:OllamaEndpoint"] ?? configuration["Ai:Ollama:Endpoint"] ?? "http://localhost:11434/api",
                    activeModel,
                    availableModels,
                    true,
                    providers),
                new PreviewSettingsDto("rosenvall.se", 7, "per-preview namespace"),
                new RepositoryHostingSettingsDto(
                    configuration["Repositories:Provider"] ?? "Forgejo",
                    configuration["Repositories:Mode"] ?? "LinkExistingFirst",
                    configuration["Repositories:Forgejo:ApiBaseUrl"] ?? "https://git.rosenvall.se/api/v1",
                    configuration.GetValue("Repositories:Forgejo:CanCreateRepositories", false)),
                new AuthentikSettingsDto(
                    !string.IsNullOrWhiteSpace(configuration["Authentik:Authority"]) || !string.IsNullOrWhiteSpace(configuration["Authentik:UsersEndpoint"]),
                    configuration["Authentik:Authority"] ?? configuration["Authentication:Authority"] ?? "https://authentik.rosenvall.se",
                    configuration["Authentik:UsersEndpoint"] ?? "https://authentik.rosenvall.se/api/v3/core/users/"));
        }

        private static string ResolveCodexStatus(string codexPath, string? codexHome)
        {
            if (Path.IsPathRooted(codexPath) && !File.Exists(codexPath))
            {
                return "Unavailable";
            }

            if (string.IsNullOrWhiteSpace(codexHome))
            {
                return "LoginRequired";
            }

            var authPath = Path.Combine(codexHome, "auth.json");
            return File.Exists(authPath) ? "Ready" : "LoginRequired";
        }

        public string? RenderPreviewManifest(Guid workItemId)
        {
            lock (_lock)
            {
                var item = _items.SingleOrDefault(i => i.Id == workItemId);
                var preview = _previews.SingleOrDefault(p => p.WorkItemId == workItemId);
                if (item is null || preview is null)
                {
                    return null;
                }

                var sourceFiles = preview.SourceFiles is { Count: > 0 }
                    ? preview.SourceFiles
                    : string.Equals(preview.Image, LocalReactPreviewProject.Image, StringComparison.OrdinalIgnoreCase)
                        ? LocalReactPreviewProject.ForWorkItem(
                            item.Key,
                            item.Title,
                            item.Description,
                            _comments
                                .Where(comment => comment.WorkItemId == item.Id && !string.Equals(comment.Author, "Rosenvall AI", StringComparison.OrdinalIgnoreCase))
                                .OrderBy(comment => comment.CreatedAt)
                                .Select(comment => comment.Body))
                        : Array.Empty<PreviewSourceFile>();
                var resources = PreviewResourceSet.Create(
                    item.Key,
                    item.Title,
                    preview.Image,
                    preview.StaticHtml,
                    preview.Namespace ?? "devops-previews",
                    includeNamespace: preview.Namespace is not null,
                    sourceFiles: sourceFiles);
                return PreviewManifestRenderer.Render(resources);
            }
        }

        public string? RenderPipelineJobManifest(Guid pipelineRunId)
        {
            lock (_lock)
            {
                var run = _pipelineRuns.SingleOrDefault(entry => entry.Id == pipelineRunId);
                if (run is null)
                {
                    return null;
                }

                var repository = _repositories.SingleOrDefault(entry => entry.Id == run.RepositoryId);
                if (repository is null)
                {
                    return null;
                }

                return PipelineJobManifestRenderer.Render(run, repository);
            }
        }

        private BoardDto ToBoardDto(BoardRecord board) =>
            new(board.Id, board.WorkspaceId, board.Name,
                board.Columns.Select(column => new BoardColumnDto(column, _items.Where(i => i.BoardId == board.Id && i.Status == column).OrderBy(i => i.SortOrder).ThenBy(i => i.Key).Select(ToSummary).ToArray())).ToArray(),
                board.RepositoryId is null ? null : _repositories.SingleOrDefault(repository => repository.Id == board.RepositoryId.Value));

        private WorkItemSummaryDto ToSummary(WorkItemRecord item)
        {
            var runningPreview = _previews.SingleOrDefault(preview =>
                preview.WorkItemId == item.Id &&
                string.Equals(preview.Status, "Running", StringComparison.OrdinalIgnoreCase));
            return new(item.Id, item.Key, item.Type, item.Title, item.Status, item.Assignee, item.Priority, _comments.Count(c => c.WorkItemId == item.Id), item.AiStatus, item.PullRequestUrl, item.SortOrder, runningPreview?.Url);
        }

        private void AddPreviewEvent(WorkItemRecord item, PreviewDto preview, string eventType, string actor, string message)
        {
            _previewEvents.Add(new PreviewEventDto(Guid.NewGuid(), item.Id, item.Key, item.Title, eventType, preview.Namespace, preview.Url, actor, message, DateTimeOffset.UtcNow));
            AddTimelineForItem(item, eventType == "Created" ? "PreviewCreated" : $"Preview{eventType}", item.Key, message, actor, preview.Url);
        }

        private RepositoryDto? ResolveBoardRepository(CreateBoardRequest request)
        {
            if (request.RepositoryId is { } repositoryId)
            {
                return _repositories.SingleOrDefault(repository => repository.Id == repositoryId);
            }

            if (string.IsNullOrWhiteSpace(request.RepositoryRemoteUrl) && string.IsNullOrWhiteSpace(request.RepositoryName))
            {
                return null;
            }

            var repository = new RepositoryDto(
                Guid.NewGuid(),
                NormalizeText(request.RepositoryProvider, "Forgejo"),
                NormalizeText(request.RepositoryName, NormalizeText(request.Name, "repository")),
                NormalizeText(request.RepositoryRemoteUrl, $"ssh://git.rosenvall.se/{SlugifyRepositoryName(request.RepositoryName ?? request.Name)}.git"),
                string.IsNullOrWhiteSpace(request.RepositoryWebUrl) ? null : request.RepositoryWebUrl.Trim(),
                NormalizeText(request.RepositoryDefaultBranch, "main"),
                DateTimeOffset.UtcNow);
            _repositories.Add(repository);
            return repository;
        }

        private RepositoryDto EnsureLocalRepository()
        {
            var existing = _repositories.FirstOrDefault(repository => repository.Provider == "Forgejo" && repository.Name == "local/vite-react-tailwind");
            if (existing is not null)
            {
                return existing;
            }

            var repository = new RepositoryDto(
                Guid.NewGuid(),
                "Forgejo",
                "local/vite-react-tailwind",
                "ssh://git.rosenvall.se/local/vite-react-tailwind.git",
                "https://git.rosenvall.se/local/vite-react-tailwind",
                "main",
                DateTimeOffset.UtcNow);
            _repositories.Add(repository);
            return repository;
        }

        private Guid? RepositoryIdForBoard(Guid boardId) =>
            _boards.SingleOrDefault(board => board.Id == boardId)?.RepositoryId;

        private Guid? ResolveRepositoryForDevelopment(Guid boardId, string repositoryName)
        {
            var boardRepositoryId = RepositoryIdForBoard(boardId);
            if (boardRepositoryId is not null)
            {
                return boardRepositoryId;
            }

            return _repositories.FirstOrDefault(repository =>
                repository.Name.Equals(repositoryName, StringComparison.OrdinalIgnoreCase) ||
                repository.RemoteUrl.Contains(repositoryName, StringComparison.OrdinalIgnoreCase))?.Id;
        }

        private void RecordPipelineRunWithoutLock(RecordPipelineRunRequest request)
        {
            var now = DateTimeOffset.UtcNow;
            var run = new PipelineRunDto(
                Guid.NewGuid(),
                request.RepositoryId,
                request.BoardId,
                request.WorkItemId,
                NormalizeText(request.Stage, "Pipeline"),
                    NormalizeText(request.Status, "Running"),
                    NormalizeText(request.Message, "Pipeline run recorded."),
                    string.IsNullOrWhiteSpace(request.Url) ? null : request.Url.Trim(),
                    now,
                    IsTerminalPipelineStatus(request.Status) ? now : null,
                    Math.Max(0, request.TokensUsed),
                    Math.Max(0, request.CodeAdded),
                    Math.Max(0, request.CodeDeleted));
            _pipelineRuns.Add(run);
            AddTimelineEvent(run.BoardId, run.RepositoryId, run.WorkItemId, "Pipeline", run.Stage, run.Message, "system", run.Url, now);
        }

        private void AddTimelineForItem(WorkItemRecord item, string kind, string title, string message, string actor, string? url = null, DateTimeOffset? createdAt = null) =>
            AddTimelineEvent(item.BoardId, RepositoryIdForBoard(item.BoardId), item.Id, kind, title, message, actor, url, createdAt);

        private void AddTimelineEvent(Guid? boardId, Guid? repositoryId, Guid? workItemId, string kind, string title, string message, string actor, string? url = null, DateTimeOffset? createdAt = null)
        {
            _timelineEvents.Add(new TimelineEventDto(
                Guid.NewGuid(),
                boardId,
                repositoryId,
                workItemId,
                NormalizeText(kind, "Event"),
                NormalizeText(title, "Event"),
                NormalizeText(message, "Event recorded."),
                NormalizeText(actor, "system"),
                string.IsNullOrWhiteSpace(url) ? null : url.Trim(),
                createdAt ?? DateTimeOffset.UtcNow));
        }

        private string RepositoryName(Guid repositoryId) =>
            _repositories.SingleOrDefault(repository => repository.Id == repositoryId)?.Name ?? "Repository";

        private static bool IsTerminalPipelineStatus(string status) =>
            status.Contains("succeed", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("complete", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("cancel", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("approved", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("passed", StringComparison.OrdinalIgnoreCase);

        private static string NormalizeText(string? value, string fallback) =>
            string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

        private static void EnsureEditableHumanComment(CommentDto comment, string actor)
        {
            if (!string.Equals(comment.Kind, "Comment", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(comment.Author, NormalizeText(actor, ""), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Only your own comments can be edited or deleted.");
            }
        }

        private static string SlugifyRepositoryName(string value) =>
            string.Join('-', NormalizeText(value, "repository").Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

        private static string NormalizePreviewStatus(string status) =>
            string.Equals(status, "Healthy", StringComparison.OrdinalIgnoreCase) ? "Running" : status;

        private void PlaceItem(WorkItemRecord item, string status, int requestedSortOrder)
        {
            var previousStatus = item.Status;
            item.Status = status;
            var siblings = _items
                .Where(i => i.BoardId == item.BoardId && i.Status == status && i.Id != item.Id)
                .OrderBy(i => i.SortOrder)
                .ThenBy(i => i.Key)
                .ToList();
            var insertAt = Math.Clamp(requestedSortOrder, 0, siblings.Count);
            siblings.Insert(insertAt, item);
            for (var index = 0; index < siblings.Count; index++)
            {
                siblings[index].SortOrder = index;
            }

            if (!string.Equals(previousStatus, status, StringComparison.Ordinal))
            {
                NormalizeBoard(item.BoardId, status);
                return;
            }

            NormalizeBoard(item.BoardId, status);
        }

        private void NormalizeBoard(Guid boardId, string? exceptStatus = null)
        {
            var board = _boards.SingleOrDefault(b => b.Id == boardId);
            if (board is null)
            {
                return;
            }

            foreach (var column in board.Columns.Where(column => !string.Equals(column, exceptStatus, StringComparison.Ordinal)))
            {
                var items = _items
                    .Where(i => i.BoardId == boardId && i.Status == column)
                    .OrderBy(i => i.SortOrder)
                    .ThenBy(i => i.Key)
                    .ToArray();
                for (var index = 0; index < items.Length; index++)
                {
                    items[index].SortOrder = index;
                }
            }
        }

        private static string BuildPlan(WorkItemRecord item) =>
            $"Based on {item.Key}, implement {item.Title}. Plan: 1. confirm API and data contracts, 2. add focused tests, 3. implement the smallest production slice, 4. build a preview image, 5. post the PR and demo URL for human review.";

        private static int TryGetTaskNumber(string key)
        {
            if (key.StartsWith("TASK-", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(key["TASK-".Length..], out var number))
            {
                return number;
            }

            return 4820;
        }

        private int NextTaskNumberFromItems() =>
            _items.Select(item => TryGetTaskNumber(item.Key)).DefaultIfEmpty(4820).Max() + 1;

        private void Seed()
        {
            var workspace = new WorkspaceDto(Guid.Parse("3a469909-9487-4bf5-9fd4-5bd2e4d59fb5"), "Frontend Re-architecture", "Production Environment", "us-east-1", 14, 32, 847, 64);
            var repository = new RepositoryDto(Guid.Parse("8a5bca6d-69d1-4a58-bb4f-a629243a9337"), "GitHub", "rosenvall/auth-service", "https://github.com/rosenvall/auth-service.git", "https://github.com/rosenvall/auth-service", "main", DateTimeOffset.UtcNow.AddDays(-7));
            var board = new BoardRecord(Guid.Parse("6942aca6-5c36-4498-aeaa-c3a2ebe4e8db"), workspace.Id, "Sprint 42", ["Todo", "In Progress", "AI Planning", "Review", "Done"], repository.Id);
            var task = new WorkItemRecord(Guid.Parse("4f55f9a2-3f05-4ff5-bfd8-a43740bebccb"), board.Id, "TASK-4821", "Feature", "Implement OAuth2 Flow for Partner API Integrations", "Upgrade the current API authentication to support full OAuth2 authorization code flow for third-party partner integrations.", "In Progress", "High", "Sarah J.", 0);
            var aiTask = new WorkItemRecord(Guid.Parse("9d81428e-f407-4689-a0c8-20e6e48175bb"), board.Id, "FE-901", "Feature", "Generate Unit Tests for Auth Module", "Generate a focused suite for authentication edge cases.", "AI Planning", "Medium", null, 0)
            {
                AiStatus = "Planning"
            };

            _workspaces.Add(workspace);
            _repositories.Add(repository);
            _boards.Add(board);
            _items.AddRange([
                task,
                aiTask,
                new WorkItemRecord(Guid.NewGuid(), board.Id, "FE-892", "Feature", "Implement Global Theme Switcher", "Add dark and light theme switching.", "Todo", "Medium", "FE", 0),
                new WorkItemRecord(Guid.NewGuid(), board.Id, "FE-885", "Feature", "Migrate State Management to Zustand", "Simplify state boundaries.", "In Progress", "Medium", "Ava", 1)
            ]);
            _comments.Add(new CommentDto(Guid.NewGuid(), task.Id, "Mike R.", "Comment", "I've started scaffolding the initial authorize endpoint. Will need DevOps to provision token storage in staging soon.", DateTimeOffset.UtcNow.AddHours(-2)));
            _comments.Add(new CommentDto(Guid.NewGuid(), task.Id, "Rosenvall AI", "Plan", "Based on the requirements and current architecture, here is a structured implementation plan. I identified authentication, configuration, and deployment updates required before application logic can be released.", DateTimeOffset.UtcNow));
            var seededRun = AiRun.Start(task.Id, "ollama", "llama3:8b");
            seededRun.PostPlan("Based on the requirements and current architecture, implement OAuth2 support behind tests, then publish a PR and preview URL for review.");
            _aiRuns.Add(seededRun);
            var seededResources = PreviewResourceSet.Create(task.Key, task.Title, "ghcr.io/rosenvall/auth-service@sha256:demo");
            _development.Add(new DevelopmentDtoRecord(task.Id, new DevelopmentDto("rosenvall/auth-service", "feat/oauth2-flow", "https://github.com/rosenvall/auth-service/pull/142", "Checks passed")));
            var seededPreview = new PreviewDto(Guid.NewGuid(), task.Id, "https://feat-auth.rosenvall.se", "ghcr.io/rosenvall/auth-service@sha256:demo", "Running", DateTimeOffset.UtcNow.AddDays(7), null, seededResources.Namespace, seededResources.Name);
            _previews.Add(seededPreview);
            AddPreviewEvent(task, seededPreview, "Created", "seed", "Seed preview created.");
            AddTimelineEvent(board.Id, repository.Id, task.Id, "Commit", task.Key, "Initial auth-service history imported.", "seed", "https://github.com/rosenvall/auth-service/commit/demo", DateTimeOffset.UtcNow.AddHours(-3));
        }

        private bool TryLoad()
        {
            using var db = _dbFactory.CreateDbContext();
            var document = db.Documents.AsNoTracking().SingleOrDefault(d => d.Id == DocumentId);
            if (document is null)
            {
                return false;
            }

            var snapshot = JsonSerializer.Deserialize<DevOpsSnapshot>(document.Json, SnapshotJsonOptions);
            if (snapshot is null)
            {
                return false;
            }

            _workspaces.AddRange(snapshot.Workspaces);
            _repositories.AddRange(snapshot.Repositories ?? []);
            _boards.AddRange(snapshot.Boards.Select(board => new BoardRecord(board.Id, board.WorkspaceId, board.Name, board.Columns, board.RepositoryId)));
            _items.AddRange(snapshot.Items.Select(item => new WorkItemRecord(item.Id, item.BoardId, item.Key, item.Type, item.Title, item.Description, item.Status, item.Priority, item.Assignee, item.SortOrder)
            {
                AiStatus = item.AiStatus,
                PullRequestUrl = item.PullRequestUrl
            }));
            _comments.AddRange(snapshot.Comments);
            _aiRuns.AddRange(snapshot.AiRuns
                .GroupBy(run => run.WorkItemId)
                .SelectMany(group => group.Select((run, index) =>
                    AiRun.Restore(
                        run.Id,
                        run.WorkItemId,
                        run.Provider,
                        run.Model,
                        run.Status,
                        run.Plan,
                        run.ApprovedBy,
                        run.SequenceNumber > 0 ? run.SequenceNumber : index + 1,
                        run.CreatedAt ?? DateTimeOffset.UtcNow.AddTicks(index)))));
            _previews.AddRange(snapshot.Previews.Select(preview => preview with { Status = NormalizePreviewStatus(preview.Status) }));
            _development.AddRange(snapshot.Development.Select(development => new DevelopmentDtoRecord(development.WorkItemId, development.Development)));
            _previewEvents.AddRange(snapshot.PreviewEvents ?? []);
            _pipelineRuns.AddRange(snapshot.PipelineRuns ?? []);
            _timelineEvents.AddRange(snapshot.TimelineEvents ?? []);
            _nextTaskNumber = Math.Max(snapshot.NextTaskNumber, NextTaskNumberFromItems());
            return true;
        }

        private void Persist()
        {
            var snapshot = new DevOpsSnapshot(
                _workspaces.ToArray(),
                _boards.Select(board => new BoardSnapshot(board.Id, board.WorkspaceId, board.Name, board.Columns, board.RepositoryId)).ToArray(),
                _items.Select(item => new WorkItemSnapshot(item.Id, item.BoardId, item.Key, item.Type, item.Title, item.Description, item.Status, item.Priority, item.Assignee, item.AiStatus, item.PullRequestUrl, item.SortOrder)).ToArray(),
                _comments.ToArray(),
                _aiRuns.Select(run => new AiRunSnapshot(run.Id, run.WorkItemId, run.Provider, run.Model, run.Status, run.Plan, run.ApprovedBy, run.SequenceNumber, run.CreatedAt)).ToArray(),
                _previews.ToArray(),
                _development.Select(development => new DevelopmentSnapshot(development.WorkItemId, development.Development)).ToArray(),
                _nextTaskNumber,
                _previewEvents.ToArray(),
                _repositories.ToArray(),
                _pipelineRuns.ToArray(),
                _timelineEvents.ToArray());
            var json = JsonSerializer.Serialize(snapshot, SnapshotJsonOptions);

            using var db = _dbFactory.CreateDbContext();
            var document = db.Documents.SingleOrDefault(d => d.Id == DocumentId);
            if (document is null)
            {
                db.Documents.Add(new DevOpsStateDocument { Id = DocumentId, Json = json, UpdatedAt = DateTimeOffset.UtcNow });
            }
            else
            {
                document.Json = json;
                document.UpdatedAt = DateTimeOffset.UtcNow;
            }

            db.SaveChanges();
        }
    }

    internal sealed record BoardRecord(Guid Id, Guid WorkspaceId, string Name, IReadOnlyList<string> Columns, Guid? RepositoryId = null);
    internal sealed class ConfiguredAuthentikUser
    {
        public string? Id { get; set; }
        public string? Username { get; set; }
        public string? Name { get; set; }
        public string? DisplayName { get; set; }
        public string? Email { get; set; }
    }

    internal sealed class WorkItemRecord(Guid id, Guid boardId, string key, string type, string title, string description, string status, string priority, string? assignee, int sortOrder)
    {
        public Guid Id { get; } = id;
        public Guid BoardId { get; } = boardId;
        public string Key { get; } = key;
        public string Type { get; set; } = type;
        public string Title { get; set; } = title;
        public string Description { get; set; } = description;
        public string Status { get; set; } = status;
        public string Priority { get; set; } = priority;
        public string? Assignee { get; set; } = assignee;
        public int SortOrder { get; set; } = sortOrder;
        public string? AiStatus { get; set; }
        public string? PullRequestUrl { get; set; }
    }

    internal sealed record DevelopmentDtoRecord(Guid WorkItemId, DevelopmentDto Development);
    internal sealed record DevOpsSnapshot(IReadOnlyList<WorkspaceDto> Workspaces, IReadOnlyList<BoardSnapshot> Boards, IReadOnlyList<WorkItemSnapshot> Items, IReadOnlyList<CommentDto> Comments, IReadOnlyList<AiRunSnapshot> AiRuns, IReadOnlyList<PreviewDto> Previews, IReadOnlyList<DevelopmentSnapshot> Development, int NextTaskNumber = 0, IReadOnlyList<PreviewEventDto>? PreviewEvents = null, IReadOnlyList<RepositoryDto>? Repositories = null, IReadOnlyList<PipelineRunDto>? PipelineRuns = null, IReadOnlyList<TimelineEventDto>? TimelineEvents = null);
    internal sealed record BoardSnapshot(Guid Id, Guid WorkspaceId, string Name, IReadOnlyList<string> Columns, Guid? RepositoryId = null);
    internal sealed record WorkItemSnapshot(Guid Id, Guid BoardId, string Key, string Type, string Title, string Description, string Status, string Priority, string? Assignee, string? AiStatus, string? PullRequestUrl, int SortOrder);
    internal sealed record AiRunSnapshot(Guid Id, Guid WorkItemId, string Provider, string Model, AiRunStatus Status, string? Plan, string? ApprovedBy, int SequenceNumber = 0, DateTimeOffset? CreatedAt = null);
    internal sealed record DevelopmentSnapshot(Guid WorkItemId, DevelopmentDto Development);
}
