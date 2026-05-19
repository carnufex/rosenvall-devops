using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;
using Rosenvall.DevOps.Api;
using Rosenvall.DevOps.Core;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddSignalR();
builder.Services.AddHttpClient<OllamaPlanProvider>();
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
app.MapHub<DevOpsHub>("/hubs/devops");

var api = app.MapGroup("/api");

api.MapGet("/workspaces", (DevOpsStore store) => store.GetWorkspaces());
api.MapPost("/workspaces", async (CreateWorkspaceRequest request, DevOpsStore store, IHubContext<DevOpsHub> hub) =>
{
    var workspace = store.CreateWorkspace(request.Name, request.EnvironmentName, request.Region);
    await hub.Clients.All.SendAsync("workspaceCreated", workspace);
    return Results.Created($"/api/workspaces/{workspace.Id}", workspace);
});

api.MapGet("/workspaces/{workspaceId:guid}/boards", (Guid workspaceId, DevOpsStore store) =>
    store.GetBoards(workspaceId) is { Count: > 0 } boards ? Results.Ok(boards) : Results.NotFound());

api.MapGet("/boards/{boardId:guid}", (Guid boardId, DevOpsStore store) =>
    store.GetBoard(boardId) is { } board ? Results.Ok(board) : Results.NotFound());

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
            return Results.Problem(cleanup.Message, statusCode: StatusCodes.Status502BadGateway);
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

api.MapPost("/work-items/{workItemId:guid}/ai-plan", async (Guid workItemId, StartAiPlanRequest request, DevOpsStore store, OllamaPlanProvider planner, IHubContext<DevOpsHub> hub, CancellationToken cancellationToken) =>
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
    catch (OllamaUnavailableException ex)
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

api.MapPost("/ai-runs/{aiRunId:guid}/approve", async (Guid aiRunId, ApproveAiRunRequest request, DevOpsStore store, PreviewEnvironmentOrchestrator previews, IHubContext<DevOpsHub> hub, CancellationToken cancellationToken) =>
{
    var result = store.ApproveAiRun(aiRunId, request.ApprovedBy);
    if (result is null)
    {
        return Results.NotFound();
    }

    await hub.Clients.All.SendAsync("aiRunChanged", result);
    var implementation = store.CompleteLocalNginxImplementation(result.WorkItemId);
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

            store.MarkPreviewRunning(result.WorkItemId, request.ApprovedBy, apply.Message);
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

    var detail = store.MarkPreviewRunning(workItemId, request.Actor, apply.Message);
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

api.MapGet("/previews/{workItemId:guid}/manifest", (Guid workItemId, DevOpsStore store) =>
    store.RenderPreviewManifest(workItemId) is { } manifest ? Results.Text(manifest, "application/yaml") : Results.NotFound());

api.MapGet("/settings", (DevOpsStore store, IConfiguration configuration) => store.GetSettings(configuration));

app.Run();

namespace Rosenvall.DevOps.Api
{
    public sealed class DevOpsHub : Hub;

    public sealed record WorkspaceDto(Guid Id, string Name, string EnvironmentName, string Region, int ActiveProjects, int OpenPullRequests, int SuccessfulAiImplementations, int ComputeUsagePercent);
    public sealed record BoardDto(Guid Id, Guid WorkspaceId, string Name, IReadOnlyList<BoardColumnDto> Columns);
    public sealed record BoardColumnDto(string Name, IReadOnlyList<WorkItemSummaryDto> Items);
    public sealed record WorkItemSummaryDto(Guid Id, string Key, string Type, string Title, string Status, string? Assignee, string Priority, int CommentCount, string? AiStatus, string? PullRequestUrl, int SortOrder, string? PreviewUrl);
    public sealed record WorkItemDetailDto(WorkItemSummaryDto Item, string Description, IReadOnlyList<CommentDto> Comments, PreviewDto? Preview, DevelopmentDto? Development);
    public sealed record CommentDto(Guid Id, Guid WorkItemId, string Author, string Kind, string Body, DateTimeOffset CreatedAt);
    public sealed record PreviewDto(Guid Id, Guid WorkItemId, string Url, string Image, string Status, DateTimeOffset ExpiresAt, string? StaticHtml, string? Namespace = null, string? ResourceName = null);
    public sealed record PreviewEnvironmentDto(Guid Id, Guid? WorkItemId, string WorkItemKey, string WorkItemTitle, string Url, string Namespace, string ResourceName, string Image, string Status, DateTimeOffset ExpiresAt);
    public sealed record PreviewEventDto(Guid Id, Guid? WorkItemId, string WorkItemKey, string WorkItemTitle, string EventType, string? Namespace, string? Url, string Actor, string Message, DateTimeOffset CreatedAt);
    public sealed record PipelineStatusDto(Guid Id, Guid? WorkItemId, string WorkItemKey, string WorkItemTitle, string Stage, string Status, string Message, DateTimeOffset UpdatedAt);
    public sealed record DevelopmentDto(string Repository, string Branch, string? PullRequestUrl, string ChecksStatus, string? PullRequestApprovedBy = null, DateTimeOffset? PullRequestApprovedAt = null);
    public sealed record SettingsDto(GitHubSettingsDto GitHub, AiSettingsDto Ai, PreviewSettingsDto Preview);
    public sealed record GitHubSettingsDto(string Account, string TargetRepository, string BranchWatchPatterns, bool Connected);
    public sealed record AiSettingsDto(string Provider, string Endpoint, string ActiveModel, bool AutoReviewPullRequests);
    public sealed record PreviewSettingsDto(string Domain, int DefaultTtlDays, string Namespace);

    public sealed record CreateWorkspaceRequest(string Name, string EnvironmentName, string Region);
    public sealed record CreateWorkItemRequest(Guid BoardId, string Type, string Title, string Description, string Status, string Priority, string? Assignee);
    public sealed record UpdateWorkItemRequest(string Title, string Description, string Type, string Status, string Priority, string? Assignee);
    public sealed record MoveWorkItemRequest(string Status, int SortOrder);
    public sealed record AddCommentRequest(string Author, string Kind, string Body);
    public sealed record StartAiPlanRequest(string Provider, string Model);
    public sealed record ApproveAiRunRequest(string ApprovedBy);
    public sealed record DiscardAiRunRequest(string DiscardedBy);
    public sealed record ApprovePullRequestRequest(string ApprovedBy);
    public sealed record PreviewActionRequest(string Actor);
    public sealed record GitHubCallbackRequest(Guid WorkItemId, string Repository, string Branch, string? PullRequestUrl, string Image, string ChecksStatus, string? StaticHtml = null);

    public sealed class OllamaUnavailableException(string message) : InvalidOperationException(message);

    public sealed record PreviewCleanupResult(bool Succeeded, string Message)
    {
        public static PreviewCleanupResult Ok(string message) => new(true, message);
        public static PreviewCleanupResult Failed(string message) => new(false, message);
    }

    public sealed class PreviewEnvironmentOrchestrator(IConfiguration configuration, ILogger<PreviewEnvironmentOrchestrator> logger)
    {
        public Task<PreviewCleanupResult> ApplyAsync(string manifest, CancellationToken cancellationToken) =>
            RunKubectlAsync("apply -f -", manifest, cancellationToken);

        public async Task<PreviewCleanupResult> DeleteAsync(string manifest, CancellationToken cancellationToken)
        {
            return await RunKubectlAsync("delete -f - --ignore-not-found=true", manifest, cancellationToken);
        }

        private async Task<PreviewCleanupResult> RunKubectlAsync(string command, string manifest, CancellationToken cancellationToken)
        {
            var kubectlPath = configuration["Preview:KubectlPath"] ?? "kubectl";
            var kubeconfigPath = ResolveKubeconfigPath(configuration["Preview:KubeconfigPath"] ?? "tofu/output/kubeconfig");
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
                await process.StandardInput.WriteAsync(manifest);
                process.StandardInput.Close();
                var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
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
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
            {
                logger.LogWarning(ex, "Preview orchestration failed.");
                return PreviewCleanupResult.Failed($"Preview orchestration failed: {ex.Message}");
            }
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

    public sealed class OllamaPlanProvider(HttpClient httpClient, IConfiguration configuration)
    {
        public async Task<string> GeneratePlanAsync(string provider, string model, WorkItemDetailDto context, CancellationToken cancellationToken)
        {
            httpClient.Timeout = TimeSpan.FromSeconds(configuration.GetValue("Ai:RequestTimeoutSeconds", 120));
            if (!string.Equals(provider, "ollama", StringComparison.OrdinalIgnoreCase))
            {
                throw new OllamaUnavailableException($"Provider '{provider}' is not configured for planning yet; no plan was created.");
            }

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
              - If the task asks for hello world, target an nginx static preview page.
              - Preserve every concrete visual/content requirement from the title, description, and comments.
              - If colors, language, exact text, layout, or behavior are specified, repeat them explicitly in the plan.
              """;

        private sealed record OllamaGenerateResponse([property: JsonPropertyName("response")] string? Response);
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
        private readonly List<BoardRecord> _boards = [];
        private readonly List<WorkItemRecord> _items = [];
        private readonly List<CommentDto> _comments = [];
        private readonly List<AiRun> _aiRuns = [];
        private readonly List<PreviewDto> _previews = [];
        private readonly List<DevelopmentDtoRecord> _development = [];
        private readonly List<PreviewEventDto> _previewEvents = [];
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
                return _aiRuns.Where(run => run.WorkItemId == workItemId).ToArray();
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
                            preview.ExpiresAt);
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
                var run = AiRun.Start(workItemId, provider, model);
                run.PostPlan(string.IsNullOrWhiteSpace(plan) ? BuildPlan(item) : plan);
                _aiRuns.Add(run);
                _comments.Add(new CommentDto(Guid.NewGuid(), workItemId, "Rosenvall AI", "Plan", run.Plan!, DateTimeOffset.UtcNow));
                item.AiStatus = "PlanReady";
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

                run.Approve(approvedBy);
                var item = _items.Single(i => i.Id == run.WorkItemId);
                item.Status = "Review";
                item.AiStatus = "ImplementationRunning";
                _comments.Add(new CommentDto(Guid.NewGuid(), item.Id, "Rosenvall AI", "Result", "Implementation approved. Local nginx implementation runner has started.", DateTimeOffset.UtcNow));
                Persist();
                return run;
            }
        }

        public WorkItemDetailDto? CompleteLocalNginxImplementation(Guid workItemId)
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
                var html = LocalNginxPreviewHtml.ForWorkItem(item.Key, item.Title, item.Description, humanComments);
                var resources = PreviewResourceSet.Create(item.Key, item.Title, LocalNginxPreviewHtml.Image, html);
                item.PullRequestUrl = null;
                item.AiStatus = "Completed";
                item.Status = "Review";

                var preview = new PreviewDto(Guid.NewGuid(), item.Id, $"https://{resources.Hostname}", resources.Image, "Running", DateTimeOffset.UtcNow.AddDays(7), html, resources.Namespace, resources.Name);
                _previews.RemoveAll(p => p.WorkItemId == item.Id);
                _previews.Add(preview);
                AddPreviewEvent(item, preview, "Created", "system", $"Preview created for {item.Key}.");
                _development.RemoveAll(d => d.WorkItemId == item.Id);
                _development.Add(new DevelopmentDtoRecord(item.Id, new DevelopmentDto("local/hello-world-nginx", $"local/{resources.Name}", null, "Local nginx preview ready")));
                _comments.Add(new CommentDto(Guid.NewGuid(), item.Id, "Rosenvall AI", "Result", $"Local nginx implementation completed. Demo is available at {preview.Url}.", DateTimeOffset.UtcNow));
                Persist();
                return GetWorkItemDetail(item.Id);
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
                var running = preview with { Status = "Running", ExpiresAt = DateTimeOffset.UtcNow.AddDays(7) };
                _previews.Add(running);
                AddPreviewEvent(item, running, "Started", actor, message);
                Persist();
                return GetWorkItemDetail(workItemId);
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
                var failed = preview with { Status = "Failed" };
                _previews.Add(failed);
                AddPreviewEvent(item, failed, eventType, actor, message);
                Persist();
            }
        }

        public SettingsDto GetSettings(IConfiguration configuration) =>
            new(
                new GitHubSettingsDto("rosenvall-corp / core-infrastructure", "rosenvall-corp/core-infrastructure", "main, release/*, feat/*", true),
                new AiSettingsDto(
                    configuration["Ai:DefaultProvider"] ?? "ollama",
                    configuration["Ai:OllamaEndpoint"] ?? configuration["Ai:Ollama:Endpoint"] ?? "http://localhost:11434/api",
                    configuration["Ai:DefaultModel"] ?? configuration["Ai:Ollama:Model"] ?? "qwen3.5:latest",
                    true),
                new PreviewSettingsDto("rosenvall.se", 7, "per-preview namespace"));

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

                var resources = PreviewResourceSet.Create(
                    item.Key,
                    item.Title,
                    preview.Image,
                    preview.StaticHtml,
                    preview.Namespace ?? "devops-previews",
                    includeNamespace: preview.Namespace is not null);
                return PreviewManifestRenderer.Render(resources);
            }
        }

        private BoardDto ToBoardDto(BoardRecord board) =>
            new(board.Id, board.WorkspaceId, board.Name,
                board.Columns.Select(column => new BoardColumnDto(column, _items.Where(i => i.BoardId == board.Id && i.Status == column).OrderBy(i => i.SortOrder).ThenBy(i => i.Key).Select(ToSummary).ToArray())).ToArray());

        private WorkItemSummaryDto ToSummary(WorkItemRecord item) =>
            new(item.Id, item.Key, item.Type, item.Title, item.Status, item.Assignee, item.Priority, _comments.Count(c => c.WorkItemId == item.Id), item.AiStatus, item.PullRequestUrl, item.SortOrder, _previews.SingleOrDefault(p => p.WorkItemId == item.Id)?.Url);

        private void AddPreviewEvent(WorkItemRecord item, PreviewDto preview, string eventType, string actor, string message)
        {
            _previewEvents.Add(new PreviewEventDto(Guid.NewGuid(), item.Id, item.Key, item.Title, eventType, preview.Namespace, preview.Url, actor, message, DateTimeOffset.UtcNow));
        }

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
            var board = new BoardRecord(Guid.Parse("6942aca6-5c36-4498-aeaa-c3a2ebe4e8db"), workspace.Id, "Sprint 42", ["Todo", "In Progress", "AI Planning", "Review", "Done"]);
            var task = new WorkItemRecord(Guid.Parse("4f55f9a2-3f05-4ff5-bfd8-a43740bebccb"), board.Id, "TASK-4821", "Feature", "Implement OAuth2 Flow for Partner API Integrations", "Upgrade the current API authentication to support full OAuth2 authorization code flow for third-party partner integrations.", "In Progress", "High", "Sarah J.", 0);
            var aiTask = new WorkItemRecord(Guid.Parse("9d81428e-f407-4689-a0c8-20e6e48175bb"), board.Id, "FE-901", "Feature", "Generate Unit Tests for Auth Module", "Generate a focused suite for authentication edge cases.", "AI Planning", "Medium", null, 0)
            {
                AiStatus = "Planning"
            };

            _workspaces.Add(workspace);
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
            _boards.AddRange(snapshot.Boards.Select(board => new BoardRecord(board.Id, board.WorkspaceId, board.Name, board.Columns)));
            _items.AddRange(snapshot.Items.Select(item => new WorkItemRecord(item.Id, item.BoardId, item.Key, item.Type, item.Title, item.Description, item.Status, item.Priority, item.Assignee, item.SortOrder)
            {
                AiStatus = item.AiStatus,
                PullRequestUrl = item.PullRequestUrl
            }));
            _comments.AddRange(snapshot.Comments);
            _aiRuns.AddRange(snapshot.AiRuns.Select(run => AiRun.Restore(run.Id, run.WorkItemId, run.Provider, run.Model, run.Status, run.Plan, run.ApprovedBy)));
            _previews.AddRange(snapshot.Previews.Select(preview => preview with { Status = NormalizePreviewStatus(preview.Status) }));
            _development.AddRange(snapshot.Development.Select(development => new DevelopmentDtoRecord(development.WorkItemId, development.Development)));
            _previewEvents.AddRange(snapshot.PreviewEvents ?? []);
            _nextTaskNumber = Math.Max(snapshot.NextTaskNumber, NextTaskNumberFromItems());
            return true;
        }

        private void Persist()
        {
            var snapshot = new DevOpsSnapshot(
                _workspaces.ToArray(),
                _boards.Select(board => new BoardSnapshot(board.Id, board.WorkspaceId, board.Name, board.Columns)).ToArray(),
                _items.Select(item => new WorkItemSnapshot(item.Id, item.BoardId, item.Key, item.Type, item.Title, item.Description, item.Status, item.Priority, item.Assignee, item.AiStatus, item.PullRequestUrl, item.SortOrder)).ToArray(),
                _comments.ToArray(),
                _aiRuns.Select(run => new AiRunSnapshot(run.Id, run.WorkItemId, run.Provider, run.Model, run.Status, run.Plan, run.ApprovedBy)).ToArray(),
                _previews.ToArray(),
                _development.Select(development => new DevelopmentSnapshot(development.WorkItemId, development.Development)).ToArray(),
                _nextTaskNumber,
                _previewEvents.ToArray());
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

    internal sealed record BoardRecord(Guid Id, Guid WorkspaceId, string Name, IReadOnlyList<string> Columns);

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
    internal sealed record DevOpsSnapshot(IReadOnlyList<WorkspaceDto> Workspaces, IReadOnlyList<BoardSnapshot> Boards, IReadOnlyList<WorkItemSnapshot> Items, IReadOnlyList<CommentDto> Comments, IReadOnlyList<AiRunSnapshot> AiRuns, IReadOnlyList<PreviewDto> Previews, IReadOnlyList<DevelopmentSnapshot> Development, int NextTaskNumber = 0, IReadOnlyList<PreviewEventDto>? PreviewEvents = null);
    internal sealed record BoardSnapshot(Guid Id, Guid WorkspaceId, string Name, IReadOnlyList<string> Columns);
    internal sealed record WorkItemSnapshot(Guid Id, Guid BoardId, string Key, string Type, string Title, string Description, string Status, string Priority, string? Assignee, string? AiStatus, string? PullRequestUrl, int SortOrder);
    internal sealed record AiRunSnapshot(Guid Id, Guid WorkItemId, string Provider, string Model, AiRunStatus Status, string? Plan, string? ApprovedBy);
    internal sealed record DevelopmentSnapshot(Guid WorkItemId, DevelopmentDto Development);
}
