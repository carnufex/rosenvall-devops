using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;
using Rosenvall.DevOps.Api;
using Rosenvall.DevOps.Core;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddSignalR();
builder.Services.AddHttpClient<OllamaPlanProvider>();
builder.Services.AddHttpClient<GitHubRepositoryClient>();
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
builder.Services.AddSingleton<GitOpsStatusReader>();
builder.Services.AddSingleton<PreviewImplementationRunner>();
builder.Services.AddHostedService<PreviewImplementationRecoveryService>();
builder.Services.AddHostedService<PreviewHealthMonitor>();
builder.Services.AddHostedService<ImplementationRunMonitor>();
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

var githubManifestStates = new ConcurrentDictionary<string, DateTimeOffset>(StringComparer.Ordinal);
var githubManifestStateLifetime = TimeSpan.FromMinutes(20);

app.MapGet("/integrations/github/manifest/start", (GitHubRepositoryClient github) =>
{
    var state = NewGitHubManifestState(githubManifestStates);
    return Results.Content(github.RenderManifestStartPage(state), "text/html; charset=utf-8");
});

app.MapPost("/integrations/github/webhook", () => Results.Ok());

app.MapGet("/integrations/github/callback", async (string? code, long? installation_id, string? setup_action, string? state, DevOpsStore store, GitHubRepositoryClient github, PipelineJobOrchestrator jobs, CancellationToken cancellationToken) =>
{
    if (!string.IsNullOrWhiteSpace(code))
    {
        if (!TryConsumeGitHubManifestState(githubManifestStates, state, githubManifestStateLifetime))
        {
            return Results.Problem("GitHub App manifest callback state is missing or expired.", statusCode: StatusCodes.Status400BadRequest);
        }

        var app = await github.CreateAppFromManifestAsync(code, cancellationToken);
        if (app is null)
        {
            return Results.Problem("GitHub App manifest conversion failed.", statusCode: StatusCodes.Status502BadGateway);
        }

        var apply = await jobs.ApplyAsync(GitHubAppSecretRenderer.Render(app), cancellationToken);
        if (!apply.Succeeded)
        {
            return Results.Problem(apply.Message, statusCode: StatusCodes.Status502BadGateway);
        }

        var installationState = NewGitHubManifestState(githubManifestStates);
        return Results.Redirect($"https://github.com/apps/{WebUtility.UrlEncode(app.Slug)}/installations/new?state={WebUtility.UrlEncode(installationState)}");
    }

    if (installation_id is { } installationId)
    {
        if (!TryConsumeGitHubManifestState(githubManifestStates, state, githubManifestStateLifetime))
        {
            return Results.Problem("GitHub App installation callback state is missing or expired.", statusCode: StatusCodes.Status400BadRequest);
        }

        var installation = await github.GetAppInstallationAsync(installationId, "github-app", setup_action ?? "Installed", cancellationToken);
        var integration = store.CreateGitHubIntegration(installation ?? new GitHubIntegrationCallbackRequest(
            installationId,
            $"installation-{installationId}",
            "GitHub",
            "github-app",
            Status: setup_action ?? "Installed"));
        return Results.Redirect($"/?githubIntegration={integration.Id}#settings");
    }

    return Results.BadRequest("Missing GitHub manifest code or installation id.");
});

var api = app.MapGroup("/api");
if (!string.IsNullOrWhiteSpace(authority))
{
    api.RequireAuthorization();
}

api.MapGet("/workspaces", (ClaimsPrincipal user, DevOpsStore store) => store.GetWorkspaces(AuthenticatedSubjectOrNull(user)));
api.MapPost("/workspaces", async (CreateWorkspaceRequest request, ClaimsPrincipal user, DevOpsStore store, IHubContext<DevOpsHub> hub) =>
{
    if (!CanCreateWorkspaceRequest(store, user))
    {
        return WorkspaceMutationForbidden();
    }

    var workspace = store.CreateWorkspace(request.Name, request.EnvironmentName, request.Region, UserIdentityFromClaims(user).Subject);
    await hub.Clients.All.SendAsync("workspaceCreated", workspace);
    return Results.Created($"/api/workspaces/{workspace.Id}", workspace);
});

api.MapGet("/workspaces/{workspaceId:guid}/boards", (Guid workspaceId, ClaimsPrincipal user, DevOpsStore store) =>
    store.GetBoards(workspaceId, AuthenticatedSubjectOrNull(user)) is { Count: > 0 } boards ? Results.Ok(boards) : Results.NotFound());

api.MapPost("/workspaces/{workspaceId:guid}/boards", (Guid workspaceId, CreateBoardRequest request, ClaimsPrincipal user, DevOpsStore store) =>
    store.CreateBoard(workspaceId, request, UserIdentityFromClaims(user).Subject) is { } board ? Results.Created($"/api/boards/{board.Id}", board) : Results.NotFound());

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

api.MapGet("/boards/{boardId:guid}/timeline", (Guid boardId, ClaimsPrincipal user, DevOpsStore store) =>
{
    if (!CanViewBoardRequest(store, boardId, user))
    {
        return BoardReadForbidden();
    }

    return store.GetBoard(boardId) is null ? Results.NotFound() : Results.Ok(store.GetTimeline(boardId));
});

api.MapGet("/boards/{boardId:guid}/gitops/applications", async (Guid boardId, ClaimsPrincipal user, DevOpsStore store, GitOpsStatusReader reader, IConfiguration configuration, CancellationToken cancellationToken) =>
{
    if (!CanViewBoardRequest(store, boardId, user))
    {
        return BoardReadForbidden();
    }

    if (store.GetBoard(boardId) is null)
    {
        return Results.NotFound();
    }

    return Results.Ok(await reader.ReadApplicationsAsync(store.GetBoardGitOpsSettings(boardId), configuration, cancellationToken));
});

api.MapGet("/repositories", (ClaimsPrincipal user, DevOpsStore store) => store.GetRepositories(AuthenticatedSubjectOrNull(user)));

api.MapPost("/repositories", (CreateRepositoryRequest request, ClaimsPrincipal user, DevOpsStore store) =>
{
    if (!CanCreateRepositoryRequest(store, user))
    {
        return BoardMutationForbidden();
    }

    var repository = store.CreateRepository(request);
    return Results.Created($"/api/repositories/{repository.Id}", repository);
});

api.MapGet("/me", (ClaimsPrincipal user, DevOpsStore store) =>
{
    var identity = UserIdentityFromClaims(user);
    return Results.Ok(store.GetOrCreateUser(identity));
});

api.MapGet("/teams", (ClaimsPrincipal user, DevOpsStore store) => store.GetTeams(AuthenticatedSubjectOrNull(user)));
api.MapPost("/teams", (CreateTeamRequest request, ClaimsPrincipal user, DevOpsStore store) =>
{
    var actor = UserIdentityFromClaims(user);
    store.GetOrCreateUser(actor);
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

api.MapGet("/integrations/github/install-url", (GitHubRepositoryClient github) => Results.Ok(new GitHubInstallUrlDto(github.GetInstallUrl())));
api.MapGet("/integrations/github/callback", async (long installation_id, string? setup_action, string? account, ClaimsPrincipal user, DevOpsStore store, GitHubRepositoryClient github, CancellationToken cancellationToken) =>
{
    var actor = UserIdentityFromClaims(user);
    var installation = await github.GetAppInstallationAsync(installation_id, actor.Subject, setup_action ?? "Installed", cancellationToken);
    var integration = store.CreateGitHubIntegration(installation ?? new GitHubIntegrationCallbackRequest(installation_id, account ?? $"installation-{installation_id}", "User", actor.Subject, Status: setup_action ?? "Installed"));
    return Results.Ok(integration);
});
api.MapGet("/integrations/github", async (ClaimsPrincipal user, DevOpsStore store, GitHubRepositoryClient github, CancellationToken cancellationToken) =>
{
    var installations = await github.GetAppInstallationsAsync(cancellationToken);
    if (installations.Count > 0)
    {
        store.UpsertGitHubIntegrations(installations);
    }

    return Results.Ok(store.GetGitHubIntegrations(AuthenticatedSubjectOrNull(user)));
});
api.MapPost("/integrations/github/sync", async (ClaimsPrincipal user, DevOpsStore store, GitHubRepositoryClient github, CancellationToken cancellationToken) =>
{
    var installations = await github.GetAppInstallationsAsync(cancellationToken);
    if (installations.Count > 0)
    {
        store.UpsertGitHubIntegrations(installations);
    }

    return Results.Ok(store.GetGitHubIntegrations(AuthenticatedSubjectOrNull(user)));
});
api.MapGet("/integrations/github/repositories", async (long? installationId, ClaimsPrincipal user, DevOpsStore store, GitHubRepositoryClient github, CancellationToken cancellationToken) =>
{
    var actorSubject = AuthenticatedSubjectOrNull(user);
    if (installationId is { } requestedInstallationId && !store.CanUseGitHubInstallation(requestedInstallationId, actorSubject))
    {
        return BoardReadForbidden();
    }

    var resolvedInstallationId = installationId ?? store.GetDefaultGitHubInstallationId(actorSubject);
    if (resolvedInstallationId is null && github.IsAppConfigured())
    {
        var installations = await github.GetAppInstallationsAsync(cancellationToken);
        if (installations.Count > 0)
        {
            store.UpsertGitHubIntegrations(installations);
            resolvedInstallationId = store.GetDefaultGitHubInstallationId(actorSubject);
        }
    }

    if (resolvedInstallationId is null && github.IsAppConfigured())
    {
        return Results.Ok(Array.Empty<RepositoryDto>());
    }

    return Results.Ok(await github.GetRepositoriesAsync(cancellationToken, resolvedInstallationId));
});
api.MapGet("/integrations/github/repository-picker", async (long? installationId, ClaimsPrincipal user, DevOpsStore store, GitHubRepositoryClient github, CancellationToken cancellationToken) =>
{
    if (!github.IsAppConfigured() && string.IsNullOrWhiteSpace(github.ConfiguredToken))
    {
        return Results.Ok(new GitHubRepositoryPickerDto(
            "Error",
            "GitHub App credentials are not mounted on the API pod.",
            []));
    }

    var actorSubject = AuthenticatedSubjectOrNull(user);
    if (installationId is { } requestedInstallationId && !store.CanUseGitHubInstallation(requestedInstallationId, actorSubject))
    {
        return BoardReadForbidden();
    }

    var resolvedInstallationId = installationId ?? store.GetDefaultGitHubInstallationId(actorSubject);
    if (resolvedInstallationId is null && github.IsAppConfigured())
    {
        var installations = await github.GetAppInstallationsAsync(cancellationToken);
        if (installations.Count > 0)
        {
            store.UpsertGitHubIntegrations(installations);
            resolvedInstallationId = store.GetDefaultGitHubInstallationId(actorSubject);
        }
    }

    if (resolvedInstallationId is null && github.IsAppConfigured())
    {
        return Results.Ok(new GitHubRepositoryPickerDto(
            "Empty",
            "GitHub App credentials are configured, but no installation is visible. Sync the existing installation or reinstall the app.",
            [],
            null));
    }

    var result = await github.GetRepositoriesResultAsync(cancellationToken, resolvedInstallationId);
    if (!result.Succeeded)
    {
        return Results.Ok(new GitHubRepositoryPickerDto("Error", result.Message, [], resolvedInstallationId));
    }

    return Results.Ok(new GitHubRepositoryPickerDto(
        result.Repositories.Count == 0 ? "Empty" : "Loaded",
        result.Repositories.Count == 0 ? "The GitHub App installation has zero granted repositories." : null,
        result.Repositories,
        resolvedInstallationId));
});
api.MapGet("/integrations/github/repository-profile", async (string owner, string repo, string? branch, string? mode, long? installationId, ClaimsPrincipal user, DevOpsStore store, GitHubRepositoryClient github, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo))
    {
        return Results.BadRequest("Both owner and repo are required.");
    }

    var actorSubject = AuthenticatedSubjectOrNull(user);
    if (installationId is { } requestedInstallationId && !store.CanUseGitHubInstallation(requestedInstallationId, actorSubject))
    {
        return BoardReadForbidden();
    }

    var result = await github.GetRepositoryProfileAsync(owner, repo, branch, cancellationToken, installationId ?? store.GetDefaultGitHubInstallationId(actorSubject), mode);
    return result is null
        ? Results.Ok(RepositoryProfileClassifier.Classify([], null, $"Could not scan {owner}/{repo}; defaulted to code repo."))
        : Results.Ok(result);
});

api.MapPost("/boards/{boardId:guid}/repositories", (Guid boardId, LinkBoardRepositoryRequest request, ClaimsPrincipal user, DevOpsStore store) =>
{
    if (!CanMutateBoardRequest(store, boardId, user))
    {
        return BoardMutationForbidden();
    }

    return store.LinkRepositoryToBoard(boardId, request) is { } board ? Results.Ok(board) : Results.NotFound();
});
api.MapPost("/boards/{boardId:guid}/repositories/github", async (Guid boardId, SyncGitHubRepositoryRequest request, ClaimsPrincipal user, DevOpsStore store, GitHubRepositoryClient github, CancellationToken cancellationToken) =>
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

    var integration = store.GetGitHubIntegration(request.InstallationId);
    if (integration is null)
    {
        return Results.Problem("No GitHub App installation is available for repository creation.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var token = await github.CreateInstallationTokenAsync(integration.InstallationId, cancellationToken);
    if (string.IsNullOrWhiteSpace(token))
    {
        return Results.Problem($"Could not mint GitHub App installation token for {integration.AccountLogin}.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var createdRepository = await github.CreateRepositoryAsync(integration, request, token, cancellationToken);
    if (createdRepository is null)
    {
        return Results.Problem("GitHub repository creation failed. Check GitHub App permissions for repository administration.", statusCode: StatusCodes.Status502BadGateway);
    }

    var repository = store.CreateRepository(new CreateRepositoryRequest(
        createdRepository.Provider,
        createdRepository.Name,
        createdRepository.RemoteUrl,
        createdRepository.DefaultBranch,
        createdRepository.WebUrl,
        createdRepository.Owner,
        request.ImplementationProfile ?? createdRepository.ImplementationProfile));
    return store.LinkRepositoryToBoard(boardId, new LinkBoardRepositoryRequest(repository.Id, true, request.ImplementationProfile ?? repository.ImplementationProfile)) is { } board
        ? Results.Created($"/api/repositories/{repository.Id}", board)
        : Results.NotFound();
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
api.MapGet("/boards/{boardId:guid}/secrets", (Guid boardId, ClaimsPrincipal user, DevOpsStore store) =>
{
    if (!CanMutateBoardRequest(store, boardId, user))
    {
        return BoardMutationForbidden();
    }

    return Results.Ok(store.GetBoardSecrets(boardId));
});
api.MapPost("/boards/{boardId:guid}/secrets", async (Guid boardId, CreateBoardSecretRequest request, ClaimsPrincipal user, DevOpsStore store, PipelineJobOrchestrator jobs, IConfiguration configuration, CancellationToken cancellationToken) =>
{
    if (!CanMutateBoardRequest(store, boardId, user))
    {
        return BoardMutationForbidden();
    }

    var secret = store.CreateBoardSecret(boardId, request);
    if (secret is null)
    {
        return Results.NotFound();
    }

    var manifest = store.RenderBoardSecretManifest(secret, request.Value, configuration);
    if (manifest is not null)
    {
        var apply = await jobs.ApplyAsync(manifest, cancellationToken);
        if (!apply.Succeeded)
        {
            store.DeleteBoardSecret(boardId, secret.Id);
            return Results.Problem(apply.Message, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    return Results.Created($"/api/boards/{boardId}/secrets/{secret.Id}", secret);
});
api.MapPut("/boards/{boardId:guid}/secrets/{secretId:guid}", async (Guid boardId, Guid secretId, CreateBoardSecretRequest request, ClaimsPrincipal user, DevOpsStore store, PipelineJobOrchestrator jobs, IConfiguration configuration, CancellationToken cancellationToken) =>
{
    if (!CanMutateBoardRequest(store, boardId, user))
    {
        return BoardMutationForbidden();
    }

    var secret = store.UpdateBoardSecret(boardId, secretId);
    if (secret is null)
    {
        return Results.NotFound();
    }

    var manifest = store.RenderBoardSecretManifest(secret, request.Value, configuration);
    if (manifest is not null)
    {
        var apply = await jobs.ApplyAsync(manifest, cancellationToken);
        if (!apply.Succeeded)
        {
            return Results.Problem(apply.Message, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    return Results.Ok(secret);
});
api.MapDelete("/boards/{boardId:guid}/secrets/{secretId:guid}", async (Guid boardId, Guid secretId, ClaimsPrincipal user, DevOpsStore store, PipelineJobOrchestrator jobs, IConfiguration configuration, CancellationToken cancellationToken) =>
{
    if (!CanMutateBoardRequest(store, boardId, user))
    {
        return BoardMutationForbidden();
    }

    var secret = store.GetBoardSecret(boardId, secretId);
    if (secret is null)
    {
        return Results.NotFound();
    }

    var manifest = store.RenderBoardSecretManifest(secret, "redacted", configuration);
    if (manifest is not null)
    {
        var cleanup = await jobs.DeleteAsync(manifest, cancellationToken);
        if (!cleanup.Succeeded)
        {
            return Results.Problem(cleanup.Message, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    return store.DeleteBoardSecret(boardId, secretId) ? Results.NoContent() : Results.NotFound();
});

api.MapGet("/work-items", (ClaimsPrincipal user, DevOpsStore store) => store.GetWorkItems(AuthenticatedSubjectOrNull(user)));
api.MapPost("/work-items", async (CreateWorkItemRequest request, ClaimsPrincipal user, DevOpsStore store, IHubContext<DevOpsHub> hub) =>
{
    if (!CanMutateBoardRequest(store, request.BoardId, user))
    {
        return BoardMutationForbidden();
    }

    var item = store.CreateWorkItem(request);
    if (item is null)
    {
        return Results.Problem("Work item board or status is invalid.", statusCode: StatusCodes.Status400BadRequest);
    }

    await hub.Clients.All.SendAsync("workItemChanged", item);
    return Results.Created($"/api/work-items/{item.Id}", item);
});

api.MapPatch("/work-items/{workItemId:guid}", async (Guid workItemId, UpdateWorkItemRequest request, ClaimsPrincipal user, DevOpsStore store, IHubContext<DevOpsHub> hub) =>
{
    if (!CanMutateWorkItemRequest(store, workItemId, user))
    {
        return BoardMutationForbidden();
    }

    var item = store.UpdateWorkItem(workItemId, request);
    if (item is null)
    {
        return Results.NotFound();
    }

    await hub.Clients.All.SendAsync("workItemChanged", item);
    return Results.Ok(item);
});

api.MapDelete("/work-items/{workItemId:guid}", async (Guid workItemId, ClaimsPrincipal user, DevOpsStore store, PreviewEnvironmentOrchestrator previews, IHubContext<DevOpsHub> hub, CancellationToken cancellationToken) =>
{
    if (!CanMutateWorkItemRequest(store, workItemId, user))
    {
        return BoardMutationForbidden();
    }

    var manifest = store.RenderWorkItemCleanupManifest(workItemId);
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

api.MapPost("/work-items/{workItemId:guid}/delete-and-clean-up", async (Guid workItemId, DeleteAndCleanupRequest request, ClaimsPrincipal user, DevOpsStore store, PreviewEnvironmentOrchestrator previews, PipelineJobOrchestrator jobs, GitHubRepositoryClient github, IConfiguration configuration, IHubContext<DevOpsHub> hub, CancellationToken cancellationToken) =>
{
    if (!CanMutateWorkItemRequest(store, workItemId, user))
    {
        return BoardMutationForbidden();
    }

    var actor = string.IsNullOrWhiteSpace(request.Actor) ? "crille" : request.Actor.Trim();
    var sourceRun = store.GetImplementationRuns(workItemId)
        .Where(run => !string.IsNullOrWhiteSpace(run.PullRequestUrl))
        .OrderByDescending(run => run.UpdatedAt)
        .FirstOrDefault();
    if (sourceRun is null)
    {
        var manifest = store.RenderWorkItemCleanupManifest(workItemId);
        if (manifest is not null)
        {
            var cleanup = await previews.DeleteAsync(manifest, cancellationToken);
            if (!cleanup.Succeeded)
            {
                store.RecordPreviewFailure(workItemId, "CleanupFailed", actor, cleanup.Message);
                return Results.Problem(cleanup.Message, statusCode: StatusCodes.Status502BadGateway);
            }
        }

        if (!store.DeleteWorkItem(workItemId, actor))
        {
            return Results.NotFound();
        }

        await hub.Clients.All.SendAsync("workItemDeleted", workItemId);
        return Results.NoContent();
    }

    var repository = store.GetImplementationRunRepository(sourceRun.Id);
    var integration = repository is null ? null : store.GetGitHubIntegrationForRepository(repository);
    var token = integration is null ? github.ConfiguredToken : await github.CreateInstallationTokenAsync(integration.InstallationId, cancellationToken);
    if (repository is null || string.IsNullOrWhiteSpace(token))
    {
        return Results.Problem("Could not resolve GitHub credentials for repository cleanup.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var pullRequest = await github.GetPullRequestAsync(sourceRun.PullRequestUrl!, token, cancellationToken);
    if (pullRequest is null)
    {
        return Results.Problem("Could not read source pull request state. The card was kept so cleanup can be retried.", statusCode: StatusCodes.Status502BadGateway);
    }

    if (pullRequest.State.Equals("open", StringComparison.OrdinalIgnoreCase) && !pullRequest.Merged)
    {
        var commented = await github.AddPullRequestCommentAsync(pullRequest, $"Closed by Rosenvall DevOps cleanup for {sourceRun.WorkItemKey}. The work item was deleted before this PR was merged.", token, cancellationToken);
        var closed = await github.ClosePullRequestAsync(pullRequest, token, cancellationToken);
        if (!closed)
        {
            return Results.Problem("Could not close the open implementation pull request. The card was kept so cleanup can be retried.", statusCode: StatusCodes.Status502BadGateway);
        }

        var manifest = store.RenderWorkItemCleanupManifest(workItemId);
        if (manifest is not null)
        {
            var cleanup = await previews.DeleteAsync(manifest, cancellationToken);
            if (!cleanup.Succeeded)
            {
                store.RecordPreviewFailure(workItemId, "CleanupFailed", actor, cleanup.Message);
                return Results.Problem(cleanup.Message, statusCode: StatusCodes.Status502BadGateway);
            }
        }

        if (!store.DeleteWorkItem(workItemId, actor))
        {
            return Results.NotFound();
        }

        await hub.Clients.All.SendAsync("workItemDeleted", workItemId);
        return Results.NoContent();
    }

    if (!pullRequest.Merged)
    {
        var manifest = store.RenderWorkItemCleanupManifest(workItemId);
        if (manifest is not null)
        {
            var cleanup = await previews.DeleteAsync(manifest, cancellationToken);
            if (!cleanup.Succeeded)
            {
                store.RecordPreviewFailure(workItemId, "CleanupFailed", actor, cleanup.Message);
                return Results.Problem(cleanup.Message, statusCode: StatusCodes.Status502BadGateway);
            }
        }

        if (!store.DeleteWorkItem(workItemId, actor))
        {
            return Results.NotFound();
        }

        await hub.Clients.All.SendAsync("workItemDeleted", workItemId);
        return Results.NoContent();
    }

    var diff = await github.GetPullRequestDiffAsync(pullRequest, token, cancellationToken) ?? "";
    var cleanupRun = store.StartRepositoryCleanupRun(workItemId, sourceRun.Id, actor, "merged", diff);
    if (cleanupRun is null)
    {
        return Results.NotFound();
    }

    await hub.Clients.All.SendAsync("repositoryCleanupRunChanged", cleanupRun);
    var secretName = RepositoryCleanupJobManifestRenderer.GitHubTokenSecretName(cleanupRun);
    var tokenSecretApply = await jobs.ApplyAsync(RepositoryCleanupJobManifestRenderer.RenderGitHubTokenSecret(cleanupRun, token), cancellationToken);
    if (!tokenSecretApply.Succeeded)
    {
        var failed = store.UpdateRepositoryCleanupRun(cleanupRun.Id, "Failed", tokenSecretApply.Message, tokenSecretApply.Message);
        await hub.Clients.All.SendAsync("repositoryCleanupRunChanged", failed);
        return Results.Problem(tokenSecretApply.Message, statusCode: StatusCodes.Status502BadGateway);
    }

    var cleanupManifest = store.RenderRepositoryCleanupRunManifest(cleanupRun.Id, configuration, secretName);
    if (cleanupManifest is null)
    {
        var failed = store.UpdateRepositoryCleanupRun(cleanupRun.Id, "Failed", failureReason: "Repository cleanup manifest could not be rendered.");
        return Results.Problem(failed?.FailureReason ?? "Repository cleanup manifest could not be rendered.", statusCode: StatusCodes.Status409Conflict);
    }

    var apply = await jobs.ApplyAsync(cleanupManifest, cancellationToken);
    if (!apply.Succeeded)
    {
        var failed = store.UpdateRepositoryCleanupRun(cleanupRun.Id, "Failed", apply.Message, apply.Message);
        await hub.Clients.All.SendAsync("repositoryCleanupRunChanged", failed);
        return Results.Problem(apply.Message, statusCode: StatusCodes.Status502BadGateway);
    }

    var updated = store.UpdateRepositoryCleanupRun(cleanupRun.Id, "Cloning", apply.Message);
    await hub.Clients.All.SendAsync("repositoryCleanupRunChanged", updated);
    var detail = store.GetWorkItemDetail(workItemId);
    if (detail is not null)
    {
        await hub.Clients.All.SendAsync("workItemChanged", detail.Item);
    }

    return Results.Accepted($"/api/repository-cleanup-runs/{cleanupRun.Id}", updated);
});

api.MapPost("/work-items/{workItemId:guid}/cleanup-runs/adopt", async (Guid workItemId, AdoptCleanupPullRequestRequest request, ClaimsPrincipal user, DevOpsStore store, GitHubRepositoryClient github, IHubContext<DevOpsHub> hub, CancellationToken cancellationToken) =>
{
    if (!CanMutateWorkItemRequest(store, workItemId, user))
    {
        return BoardMutationForbidden();
    }

    var actor = string.IsNullOrWhiteSpace(request.Actor) ? "crille" : request.Actor.Trim();
    var sourceRun = request.SourceImplementationRunId is { } sourceImplementationRunId
        ? store.GetImplementationRuns(workItemId).SingleOrDefault(run => run.Id == sourceImplementationRunId)
        : store.GetImplementationRuns(workItemId)
            .Where(run => !string.IsNullOrWhiteSpace(run.PullRequestUrl))
            .OrderByDescending(run => run.UpdatedAt)
            .FirstOrDefault();
    if (sourceRun is null)
    {
        return Results.Problem("Could not find an implementation PR on this card to anchor the adopted cleanup PR.", statusCode: StatusCodes.Status409Conflict);
    }

    var repository = store.GetImplementationRunRepository(sourceRun.Id);
    var integration = repository is null ? null : store.GetGitHubIntegrationForRepository(repository);
    var token = integration is null ? github.ConfiguredToken : await github.CreateInstallationTokenAsync(integration.InstallationId, cancellationToken);
    GitHubPullRequestDto? pullRequest = null;
    if (!string.IsNullOrWhiteSpace(token))
    {
        pullRequest = await github.GetPullRequestAsync(request.PullRequestUrl, token, cancellationToken);
    }

    if (repository is null || string.IsNullOrWhiteSpace(token) || pullRequest is null)
    {
        return Results.Problem("Cleanup pull request could not be verified. Adoption requires a readable GitHub pull request in the source repository.", statusCode: StatusCodes.Status400BadRequest);
    }

    if (!PullRequestMatchesRepository(pullRequest, repository))
    {
        return Results.Problem("Cleanup pull request belongs to a different repository than the source implementation run.", statusCode: StatusCodes.Status400BadRequest);
    }

    var cleanupRun = store.AdoptRepositoryCleanupPullRequest(
        workItemId,
        sourceRun.Id,
        actor,
        request.PullRequestUrl,
        pullRequest?.HeadRef,
        pullRequest?.State,
        pullRequest?.Merged == true);
    if (cleanupRun is null)
    {
        return Results.NotFound();
    }

    await hub.Clients.All.SendAsync("repositoryCleanupRunChanged", cleanupRun);
    var detail = store.GetWorkItemDetail(workItemId);
    if (detail is not null)
    {
        await hub.Clients.All.SendAsync("workItemChanged", detail.Item);
    }

    return Results.Created($"/api/repository-cleanup-runs/{cleanupRun.Id}", cleanupRun);
});

api.MapPost("/work-items/{workItemId:guid}/move", async (Guid workItemId, MoveWorkItemRequest request, ClaimsPrincipal user, DevOpsStore store, IHubContext<DevOpsHub> hub) =>
{
    if (!CanMutateWorkItemRequest(store, workItemId, user))
    {
        return BoardMutationForbidden();
    }

    var item = store.MoveWorkItem(workItemId, request);
    if (item is null)
    {
        return Results.NotFound();
    }

    await hub.Clients.All.SendAsync("workItemChanged", item);
    return Results.Ok(item);
});

api.MapGet("/work-items/{workItemId:guid}", (Guid workItemId, ClaimsPrincipal user, DevOpsStore store) =>
{
    if (!CanViewWorkItemRequest(store, workItemId, user))
    {
        return BoardReadForbidden();
    }

    return store.GetWorkItemDetail(workItemId) is { } item ? Results.Ok(item) : Results.NotFound();
});

api.MapGet("/work-items/{workItemId:guid}/ai-runs", (Guid workItemId, ClaimsPrincipal user, DevOpsStore store) =>
{
    if (!CanViewWorkItemRequest(store, workItemId, user))
    {
        return BoardReadForbidden();
    }

    return Results.Ok(store.GetAiRuns(workItemId));
});

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

api.MapPost("/work-items/{workItemId:guid}/comments", async (Guid workItemId, AddCommentRequest request, ClaimsPrincipal user, DevOpsStore store, IHubContext<DevOpsHub> hub) =>
{
    if (!CanMutateWorkItemRequest(store, workItemId, user))
    {
        return BoardMutationForbidden();
    }

    var comment = store.AddComment(workItemId, request.Author, request.Kind, request.Body);
    if (comment is null)
    {
        return Results.NotFound();
    }

    await hub.Clients.All.SendAsync("commentAdded", comment);
    return Results.Created($"/api/work-items/{workItemId}", comment);
});

api.MapPatch("/comments/{commentId:guid}", async (Guid commentId, UpdateCommentRequest request, ClaimsPrincipal user, DevOpsStore store, IHubContext<DevOpsHub> hub) =>
{
    if (!CanMutateCommentRequest(store, commentId, user))
    {
        return BoardMutationForbidden();
    }

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

api.MapDelete("/comments/{commentId:guid}", async (Guid commentId, string actor, ClaimsPrincipal user, DevOpsStore store, IHubContext<DevOpsHub> hub) =>
{
    if (!CanMutateCommentRequest(store, commentId, user))
    {
        return BoardMutationForbidden();
    }

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

api.MapPost("/work-items/{workItemId:guid}/ai-plan", async (Guid workItemId, StartAiPlanRequest request, ClaimsPrincipal user, DevOpsStore store, AiPlanProviderRouter planner, IConfiguration configuration, IHubContext<DevOpsHub> hub, CancellationToken cancellationToken) =>
{
    if (!CanMutateWorkItemRequest(store, workItemId, user))
    {
        return BoardMutationForbidden();
    }

    var context = store.GetWorkItemDetail(workItemId);
    if (context is null)
    {
        return Results.NotFound();
    }

    string plan;
    try
    {
        var validated = AiModelPolicy.ValidatePlanningRequest(request, store.GetSettings(configuration, AuthenticatedSubjectOrNull(user)));
        if (validated is null)
        {
            return Results.Problem("Requested AI provider or model is not configured for planning.", statusCode: StatusCodes.Status400BadRequest);
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
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var run = store.StartAiPlan(workItemId, request.Provider, request.Model, plan, request.ReasoningEffort);
    if (run is null)
    {
        return Results.NotFound();
    }

    await hub.Clients.All.SendAsync("aiRunChanged", run);
    return Results.Accepted($"/api/ai-runs/{run.Id}", run);
});

api.MapPost("/ai-runs/{aiRunId:guid}/approve", async (Guid aiRunId, ApproveAiRunRequest request, ClaimsPrincipal user, DevOpsStore store, PreviewImplementationRunner previewImplementationRunner, IHubContext<DevOpsHub> hub) =>
{
    if (!CanMutateAiRunRequest(store, aiRunId, user))
    {
        return BoardMutationForbidden();
    }

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
    var preview = store.BeginPreviewImplementation(result.WorkItemId, "codex");
    if (preview is null)
    {
        return Results.NotFound();
    }

    await hub.Clients.All.SendAsync("previewChanged", preview);
    _ = Task.Run(() => previewImplementationRunner.RunAsync(result, request.ApprovedBy, CancellationToken.None), CancellationToken.None);

    var detail = store.GetWorkItemDetail(result.WorkItemId);
    return Results.Accepted($"/api/ai-runs/{aiRunId}", detail ?? (object)result);
});

api.MapPost("/ai-runs/{aiRunId:guid}/discard", async (Guid aiRunId, DiscardAiRunRequest request, ClaimsPrincipal user, DevOpsStore store, IHubContext<DevOpsHub> hub) =>
{
    if (!CanMutateAiRunRequest(store, aiRunId, user))
    {
        return BoardMutationForbidden();
    }

    var result = store.DiscardAiRun(aiRunId, request.DiscardedBy);
    if (result is null)
    {
        return Results.NotFound();
    }

    await hub.Clients.All.SendAsync("aiRunChanged", result);
    return Results.Ok(result);
});

api.MapPost("/integrations/github/callback", async (GitHubCallbackRequest request, ClaimsPrincipal user, DevOpsStore store, IHubContext<DevOpsHub> hub) =>
{
    if (!CanMutateWorkItemRequest(store, request.WorkItemId, user))
    {
        return BoardMutationForbidden();
    }

    var result = store.ApplyGitHubCallback(request);
    if (result is null)
    {
        return Results.NotFound();
    }

    await hub.Clients.All.SendAsync("previewChanged", result.Preview);
    return Results.Ok(result);
});

api.MapPost("/work-items/{workItemId:guid}/approve-pr", async (Guid workItemId, ApprovePullRequestRequest request, ClaimsPrincipal user, DevOpsStore store, PreviewEnvironmentOrchestrator previews, IHubContext<DevOpsHub> hub, CancellationToken cancellationToken) =>
{
    if (!CanMutateWorkItemRequest(store, workItemId, user))
    {
        return BoardMutationForbidden();
    }

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

api.MapPost("/work-items/{workItemId:guid}/preview/start", async (Guid workItemId, PreviewActionRequest request, ClaimsPrincipal user, DevOpsStore store, PreviewEnvironmentOrchestrator previews, IHubContext<DevOpsHub> hub, CancellationToken cancellationToken) =>
{
    if (!CanMutateWorkItemRequest(store, workItemId, user))
    {
        return BoardMutationForbidden();
    }

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

api.MapPost("/work-items/{workItemId:guid}/preview/stop", async (Guid workItemId, PreviewActionRequest request, ClaimsPrincipal user, DevOpsStore store, PreviewEnvironmentOrchestrator previews, IHubContext<DevOpsHub> hub, CancellationToken cancellationToken) =>
{
    if (!CanMutateWorkItemRequest(store, workItemId, user))
    {
        return BoardMutationForbidden();
    }

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

api.MapGet("/preview-environments", (ClaimsPrincipal user, DevOpsStore store) => store.GetPreviewEnvironments(AuthenticatedSubjectOrNull(user)));
api.MapGet("/preview-events", (ClaimsPrincipal user, DevOpsStore store) => store.GetPreviewEvents(AuthenticatedSubjectOrNull(user)));
api.MapGet("/pipelines", (ClaimsPrincipal user, DevOpsStore store) => store.GetPipelineStatuses(AuthenticatedSubjectOrNull(user)));
api.MapGet("/metrics", (Guid? boardId, ClaimsPrincipal user, DevOpsStore store) =>
{
    if (boardId is { } id && !CanViewBoardRequest(store, id, user))
    {
        return BoardReadForbidden();
    }

    return Results.Ok(store.GetMetrics(boardId, AuthenticatedSubjectOrNull(user)));
});
api.MapGet("/assignees", (Guid? boardId, ClaimsPrincipal user, DevOpsStore store, IConfiguration configuration) =>
{
    if (boardId is { } id && !CanViewBoardRequest(store, id, user))
    {
        return BoardReadForbidden();
    }

    return Results.Ok(store.GetAssignees(boardId, configuration, AuthenticatedSubjectOrNull(user)));
});

api.MapPost("/pipeline-runs", (RecordPipelineRunRequest request, ClaimsPrincipal user, DevOpsStore store) =>
{
    if (!CanRecordPipelineRunRequest(store, request, user))
    {
        return BoardMutationForbidden();
    }

    return store.RecordPipelineRun(request) is { } run ? Results.Created($"/api/pipeline-runs/{run.Id}", run) : Results.NotFound();
});

api.MapPost("/pipeline-runs/{pipelineRunId:guid}/execute", async (Guid pipelineRunId, ExecutePipelineRunRequest request, ClaimsPrincipal user, DevOpsStore store, PipelineJobOrchestrator jobs, CancellationToken cancellationToken) =>
{
    if (!CanMutatePipelineRunRequest(store, pipelineRunId, user))
    {
        return BoardMutationForbidden();
    }

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

api.MapGet("/pipeline-runs/{pipelineRunId:guid}/manifest", (Guid pipelineRunId, ClaimsPrincipal user, DevOpsStore store) =>
{
    if (!CanViewPipelineRunRequest(store, pipelineRunId, user))
    {
        return BoardReadForbidden();
    }

    return store.RenderPipelineJobManifest(pipelineRunId) is { } manifest ? Results.Text(manifest, "application/yaml") : Results.NotFound();
});

api.MapGet("/implementation-runs", (Guid? workItemId, ClaimsPrincipal user, DevOpsStore store) =>
{
    if (workItemId is { } id && !CanViewWorkItemRequest(store, id, user))
    {
        return BoardReadForbidden();
    }

    return Results.Ok(store.GetImplementationRuns(workItemId, AuthenticatedSubjectOrNull(user)));
});

api.MapGet("/implementation-runs/{implementationRunId:guid}", (Guid implementationRunId, ClaimsPrincipal user, DevOpsStore store) =>
{
    if (!CanViewImplementationRunRequest(store, implementationRunId, user))
    {
        return BoardReadForbidden();
    }

    return store.GetImplementationRun(implementationRunId) is { } run ? Results.Ok(run) : Results.NotFound();
});

api.MapGet("/implementation-runs/{implementationRunId:guid}/manifest", (Guid implementationRunId, ClaimsPrincipal user, DevOpsStore store, IConfiguration configuration) =>
{
    if (!CanViewImplementationRunRequest(store, implementationRunId, user))
    {
        return BoardReadForbidden();
    }

    return store.RenderImplementationRunManifest(implementationRunId, configuration) is { } manifest ? Results.Text(manifest, "application/yaml") : Results.NotFound();
});

api.MapPost("/work-items/{workItemId:guid}/implementation-runs", async (Guid workItemId, StartImplementationRunRequest request, ClaimsPrincipal user, DevOpsStore store, PipelineJobOrchestrator jobs, GitHubRepositoryClient github, IConfiguration configuration, IHubContext<DevOpsHub> hub, CancellationToken cancellationToken) =>
{
    if (!CanMutateWorkItemRequest(store, workItemId, user))
    {
        return BoardMutationForbidden();
    }

    ImplementationRunDto? run;
    try
    {
        run = store.StartImplementationRun(workItemId, request);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
    }

    if (run is null)
    {
        return Results.NotFound();
    }

    if (store.RenderPreviousImplementationRunCleanupManifest(workItemId, run.Id) is { } retryCleanupManifest)
    {
        var cleanup = await jobs.DeleteAsync(retryCleanupManifest, cancellationToken);
        if (!cleanup.Succeeded)
        {
            var failed = store.UpdateImplementationRun(run.Id, "Failed", cleanup.Message, $"Retry cleanup failed: {cleanup.Message}");
            await hub.Clients.All.SendAsync("implementationRunChanged", failed);
            return Results.Problem(failed?.FailureReason ?? cleanup.Message, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    await hub.Clients.All.SendAsync("implementationRunChanged", run);
    var githubSecretName = configuration["GitHub:TokenSecretName"] ?? "rosenvall-devops-github";
    var repository = store.GetImplementationRunRepository(run.Id);
    var integration = repository is null ? null : store.GetGitHubIntegrationForRepository(repository);
    if (integration is not null)
    {
        var token = await github.CreateInstallationTokenAsync(integration.InstallationId, cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            var failed = store.UpdateImplementationRun(run.Id, "Failed", failureReason: $"Could not mint GitHub App installation token for {integration.AccountLogin}.");
            await hub.Clients.All.SendAsync("implementationRunChanged", failed);
            return Results.Problem(failed?.FailureReason ?? "Could not mint GitHub App installation token.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        githubSecretName = RepositoryImplementationJobManifestRenderer.GitHubTokenSecretName(run);
        var tokenSecretApply = await jobs.ApplyAsync(RepositoryImplementationJobManifestRenderer.RenderGitHubTokenSecret(run, token), cancellationToken);
        if (!tokenSecretApply.Succeeded)
        {
            var failed = store.UpdateImplementationRun(run.Id, "Failed", tokenSecretApply.Message, tokenSecretApply.Message);
            await hub.Clients.All.SendAsync("implementationRunChanged", failed);
            return Results.Problem(tokenSecretApply.Message, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    var manifest = store.RenderImplementationRunManifest(run.Id, configuration, githubSecretName);
    if (manifest is null)
    {
        var failed = store.UpdateImplementationRun(run.Id, "Failed", failureReason: "Implementation manifest could not be rendered.");
        return Results.Problem(failed?.FailureReason ?? "Implementation manifest could not be rendered.", statusCode: StatusCodes.Status409Conflict);
    }

    var apply = await jobs.ApplyAsync(manifest, cancellationToken);
    if (!apply.Succeeded)
    {
        var failed = store.UpdateImplementationRun(run.Id, "Failed", apply.Message, apply.Message);
        await hub.Clients.All.SendAsync("implementationRunChanged", failed);
        return Results.Problem(apply.Message, statusCode: StatusCodes.Status502BadGateway);
    }

    var updated = store.UpdateImplementationRun(run.Id, "Cloning", apply.Message);
    await hub.Clients.All.SendAsync("implementationRunChanged", updated);
    return Results.Accepted($"/api/implementation-runs/{run.Id}", updated);
});

api.MapGet("/previews/{workItemId:guid}/manifest", (Guid workItemId, ClaimsPrincipal user, DevOpsStore store) =>
{
    if (!CanViewWorkItemRequest(store, workItemId, user))
    {
        return BoardReadForbidden();
    }

    return store.RenderPreviewManifest(workItemId) is { } manifest ? Results.Text(manifest, "application/yaml") : Results.NotFound();
});

api.MapGet("/settings", (ClaimsPrincipal user, DevOpsStore store, IConfiguration configuration) =>
    store.GetSettings(configuration, AuthenticatedSubjectOrNull(user)));

app.Run();

static string NewGitHubManifestState(ConcurrentDictionary<string, DateTimeOffset> states)
{
    var state = Guid.NewGuid().ToString("N");
    states[state] = DateTimeOffset.UtcNow;
    return state;
}

static bool TryConsumeGitHubManifestState(ConcurrentDictionary<string, DateTimeOffset> states, string? state, TimeSpan lifetime)
{
    var now = DateTimeOffset.UtcNow;
    foreach (var stale in states.Where(entry => now - entry.Value > lifetime).Select(entry => entry.Key).ToArray())
    {
        states.TryRemove(stale, out _);
    }

    return !string.IsNullOrWhiteSpace(state) &&
        states.TryRemove(state.Trim(), out var createdAt) &&
        now - createdAt <= lifetime;
}

static bool PullRequestMatchesRepository(GitHubPullRequestDto pullRequest, RepositoryDto repository) =>
    string.Equals(pullRequest.Repository, repository.Name, StringComparison.OrdinalIgnoreCase) &&
    (string.IsNullOrWhiteSpace(repository.Owner) || string.Equals(pullRequest.Owner, repository.Owner, StringComparison.OrdinalIgnoreCase));

static bool CanMutateBoardRequest(DevOpsStore store, Guid boardId, ClaimsPrincipal user) =>
    user.Identity?.IsAuthenticated != true || store.CanMutateBoard(boardId, UserIdentityFromClaims(user).Subject);

static bool CanMutateWorkItemRequest(DevOpsStore store, Guid workItemId, ClaimsPrincipal user) =>
    user.Identity?.IsAuthenticated != true || store.CanMutateWorkItem(workItemId, UserIdentityFromClaims(user).Subject);

static bool CanMutateAiRunRequest(DevOpsStore store, Guid aiRunId, ClaimsPrincipal user) =>
    user.Identity?.IsAuthenticated != true || store.CanMutateAiRun(aiRunId, UserIdentityFromClaims(user).Subject);

static bool CanMutateCommentRequest(DevOpsStore store, Guid commentId, ClaimsPrincipal user) =>
    user.Identity?.IsAuthenticated != true || store.CanMutateComment(commentId, UserIdentityFromClaims(user).Subject);

static bool CanViewBoardRequest(DevOpsStore store, Guid boardId, ClaimsPrincipal user) =>
    user.Identity?.IsAuthenticated != true || store.CanViewBoard(boardId, UserIdentityFromClaims(user).Subject);

static bool CanViewWorkItemRequest(DevOpsStore store, Guid workItemId, ClaimsPrincipal user) =>
    user.Identity?.IsAuthenticated != true || store.CanViewWorkItem(workItemId, UserIdentityFromClaims(user).Subject);

static bool CanViewImplementationRunRequest(DevOpsStore store, Guid implementationRunId, ClaimsPrincipal user) =>
    user.Identity?.IsAuthenticated != true || store.CanViewImplementationRun(implementationRunId, UserIdentityFromClaims(user).Subject);

static bool CanViewPipelineRunRequest(DevOpsStore store, Guid pipelineRunId, ClaimsPrincipal user) =>
    user.Identity?.IsAuthenticated != true || store.CanViewPipelineRun(pipelineRunId, UserIdentityFromClaims(user).Subject);

static bool CanRecordPipelineRunRequest(DevOpsStore store, RecordPipelineRunRequest request, ClaimsPrincipal user) =>
    user.Identity?.IsAuthenticated != true || store.CanRecordPipelineRun(request, UserIdentityFromClaims(user).Subject);

static bool CanMutatePipelineRunRequest(DevOpsStore store, Guid pipelineRunId, ClaimsPrincipal user) =>
    user.Identity?.IsAuthenticated != true || store.CanMutatePipelineRun(pipelineRunId, UserIdentityFromClaims(user).Subject);

static bool CanCreateRepositoryRequest(DevOpsStore store, ClaimsPrincipal user) =>
    user.Identity?.IsAuthenticated != true || store.CanCreateRepository(UserIdentityFromClaims(user).Subject);

static bool CanCreateWorkspaceRequest(DevOpsStore store, ClaimsPrincipal user) =>
    user.Identity?.IsAuthenticated != true || store.CanCreateWorkspace(UserIdentityFromClaims(user).Subject);

static bool CanViewTeamRequest(DevOpsStore store, Guid teamId, ClaimsPrincipal user) =>
    user.Identity?.IsAuthenticated != true || store.CanViewTeam(teamId, UserIdentityFromClaims(user).Subject);

static bool CanMutateTeamRequest(DevOpsStore store, Guid teamId, ClaimsPrincipal user) =>
    user.Identity?.IsAuthenticated != true || store.CanMutateTeam(teamId, UserIdentityFromClaims(user).Subject);

static IResult BoardMutationForbidden() =>
    Results.Problem("You do not have permission to modify this board.", statusCode: StatusCodes.Status403Forbidden);

static IResult BoardReadForbidden() =>
    Results.Problem("You do not have permission to view this board.", statusCode: StatusCodes.Status403Forbidden);

static IResult WorkspaceMutationForbidden() =>
    Results.Problem("You do not have permission to create workspaces.", statusCode: StatusCodes.Status403Forbidden);

static IResult TeamReadForbidden() =>
    Results.Problem("You do not have permission to view this team.", statusCode: StatusCodes.Status403Forbidden);

static IResult TeamMutationForbidden() =>
    Results.Problem("You do not have permission to modify this team.", statusCode: StatusCodes.Status403Forbidden);

static string? AuthenticatedSubjectOrNull(ClaimsPrincipal user) =>
    user.Identity?.IsAuthenticated == true ? UserIdentityFromClaims(user).Subject : null;

static UserIdentityRequest UserIdentityFromClaims(ClaimsPrincipal user)
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

namespace Rosenvall.DevOps.Api
{
    public sealed class DevOpsHub : Hub;

    public sealed record WorkspaceDto(Guid Id, string Name, string EnvironmentName, string Region, int ActiveProjects, int OpenPullRequests, int SuccessfulAiImplementations, int ComputeUsagePercent);
    public sealed record UserDto(Guid Id, string DisplayName, string Email, string Subject, string? AvatarUrl = null);
    public sealed record TeamMemberDto(Guid UserId, string Role, string? DisplayName = null, string? Email = null, string? Status = null);
    public sealed record TeamDto(Guid Id, string Name, IReadOnlyList<TeamMemberDto> Members, DateTimeOffset CreatedAt);
    public sealed record RepositoryDto(Guid Id, string Provider, string Name, string RemoteUrl, string? WebUrl, string DefaultBranch, DateTimeOffset CreatedAt, string? Owner = null, string ImplementationProfile = "react-preview");
    public sealed record BoardRepositoryDto(Guid BoardId, Guid RepositoryId, bool IsPrimary, string ImplementationProfile, RepositoryDto Repository, RepositoryProfileDto? Profile = null);
    public sealed record BoardTeamAccessDto(Guid BoardId, Guid TeamId, string TeamName, string Role);
    public sealed record BoardGitOpsSettingsDto(Guid BoardId, IReadOnlyList<string> AllowedPaths, string ArgoNamespace, string ArgoApplicationSelector);
    public sealed record BoardAiContextDto(Guid BoardId, string Instructions, IReadOnlyList<string> EnabledSkills, bool AskWhenUncertain);
    public sealed record BoardPlanningContextDto(Guid BoardId, string RepositoryProfile, BoardGitOpsSettingsDto? GitOpsSettings = null, BoardAiContextDto? AiContext = null, RepositoryProfileDto? RepositoryProfileDraft = null);
    public sealed record BoardDto(Guid Id, Guid WorkspaceId, string Name, IReadOnlyList<BoardColumnDto> Columns, RepositoryDto? Repository = null, IReadOnlyList<BoardRepositoryDto>? Repositories = null, IReadOnlyList<BoardTeamAccessDto>? TeamAccess = null, BoardGitOpsSettingsDto? GitOpsSettings = null, BoardAiContextDto? AiContext = null, string RepositorySyncState = "Preview only", IReadOnlyList<string>? ProviderCapabilities = null);
    public sealed record BoardColumnDto(string Name, IReadOnlyList<WorkItemSummaryDto> Items);
    public sealed record WorkItemSummaryDto(Guid Id, string Key, string Type, string Title, string Status, string? Assignee, string Priority, int CommentCount, string? AiStatus, string? PullRequestUrl, int SortOrder, string? PreviewUrl);
    public sealed record WorkItemDetailDto(WorkItemSummaryDto Item, string Description, IReadOnlyList<CommentDto> Comments, PreviewDto? Preview, DevelopmentDto? Development, IReadOnlyList<ImplementationRunDto>? ImplementationRuns = null, AiSessionDto? AiSession = null, IReadOnlyList<PreviewEventDto>? PreviewEvents = null, IReadOnlyList<AiRun>? PreviewImplementationRunsAwaitingRecovery = null, BoardPlanningContextDto? BoardContext = null, IReadOnlyList<RepositoryCleanupRunDto>? RepositoryCleanupRuns = null);
    public sealed record CommentDto(Guid Id, Guid WorkItemId, string Author, string Kind, string Body, DateTimeOffset CreatedAt);
    public sealed record PreviewDto(Guid Id, Guid WorkItemId, string Url, string Image, string Status, DateTimeOffset ExpiresAt, string? StaticHtml, string? Namespace = null, string? ResourceName = null, string? Phase = null, string? Message = null, DateTimeOffset? LastCheckedAt = null, string? PodName = null, string? FailureReason = null, string? FailureLog = null, IReadOnlyList<PreviewSourceFile>? SourceFiles = null, IReadOnlyList<PreviewTerminalLineDto>? TerminalLines = null);
    public sealed record PreviewEnvironmentDto(Guid Id, Guid? WorkItemId, string WorkItemKey, string WorkItemTitle, string Url, string Namespace, string ResourceName, string Image, string Status, DateTimeOffset ExpiresAt, string? Phase = null, string? Message = null, DateTimeOffset? LastCheckedAt = null, string? PodName = null, string? FailureReason = null, string? FailureLog = null);
    public sealed record PreviewEventDto(Guid Id, Guid? WorkItemId, string WorkItemKey, string WorkItemTitle, string EventType, string? Namespace, string? Url, string Actor, string Message, DateTimeOffset CreatedAt);
    public sealed record PreviewTerminalLineDto(DateTimeOffset CreatedAt, string Stream, string Message);
    public sealed record PipelineStatusDto(Guid Id, Guid? WorkItemId, string WorkItemKey, string WorkItemTitle, string Stage, string Status, string Message, DateTimeOffset UpdatedAt);
    public sealed record PipelineRunDto(Guid Id, Guid RepositoryId, Guid? BoardId, Guid? WorkItemId, string Stage, string Status, string Message, string? Url, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt = null, int TokensUsed = 0, int CodeAdded = 0, int CodeDeleted = 0);
    public sealed record ImplementationRunDto(Guid Id, Guid RepositoryId, Guid WorkItemId, Guid AiRunId, string WorkItemKey, string WorkItemTitle, string Status, string Branch, string? PullRequestUrl, string? CommitSha, string? FailureReason, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, IReadOnlyList<PreviewTerminalLineDto>? TerminalLines = null);
    public sealed record RepositoryCleanupRunDto(Guid Id, Guid RepositoryId, Guid WorkItemId, Guid SourceImplementationRunId, string WorkItemKey, string WorkItemTitle, string Status, string Branch, string SourcePullRequestUrl, string? CleanupPullRequestUrl, string? CommitSha, string? FailureReason, string? SourcePullRequestState, string? SourcePullRequestDiff, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, IReadOnlyList<PreviewTerminalLineDto>? TerminalLines = null, string? JobName = null, string? PodName = null, string? LastCondition = null, string? LastEventSummary = null, bool Adopted = false, DateTimeOffset? MergedAt = null, DateTimeOffset? VerifiedAt = null, string? VerificationFailure = null);
    public sealed record GitHubPullRequestDto(string Owner, string Repository, int Number, string State, bool Merged, string HtmlUrl, string? DiffUrl = null, string? HeadRef = null);
    public sealed record GitOpsApplicationStatusDto(string Name, string Namespace, string SyncStatus, string HealthStatus, string? Revision, string Message, string? Url, DateTimeOffset? UpdatedAt);
    public sealed record GitOpsApplicationsResponseDto(IReadOnlyList<GitOpsApplicationStatusDto> Applications, string? Message = null);
    public sealed record GitHubIntegrationDto(Guid Id, long InstallationId, string AccountLogin, string AccountType, string Status, int RepositoriesCount, string InstalledBy, DateTimeOffset CreatedAt);
    public sealed record GitHubManifestAppDto(long Id, string Slug, string Name, string Pem);
    public sealed record BoardSecretDto(Guid Id, Guid BoardId, Guid? RepositoryId, string Key, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? LastUsedAt = null);
    public sealed record AiSessionDto(Guid Id, Guid WorkItemId, string Provider, string Model, string? ProviderSessionId, string Status, DateTimeOffset LastPromptAt, Guid? RepositoryId = null, Guid? LastRunId = null, string? ContextSummary = null, string? ReasoningEffort = null);
    public sealed record TimelineEventDto(Guid Id, Guid? BoardId, Guid? RepositoryId, Guid? WorkItemId, string Kind, string Title, string Message, string Actor, string? Url, DateTimeOffset CreatedAt);
    public sealed record DevelopmentDto(string Repository, string Branch, string? PullRequestUrl, string ChecksStatus, string? PullRequestApprovedBy = null, DateTimeOffset? PullRequestApprovedAt = null);
    public sealed record SettingsDto(GitHubSettingsDto GitHub, AiSettingsDto Ai, PreviewSettingsDto Preview, RepositoryHostingSettingsDto Repositories, AuthentikSettingsDto Authentik);
    public sealed record GitHubSettingsDto(string Account, string TargetRepository, string BranchWatchPatterns, bool Connected, bool AppConfigured, string? InstallUrl, bool SyncAvailable);
    public sealed record AiSettingsDto(string Provider, string Endpoint, string ActiveModel, IReadOnlyList<string> AvailableModels, bool AutoReviewPullRequests, IReadOnlyList<AiProviderSettingsDto> AvailableProviders);
    public sealed record AiProviderSettingsDto(string Provider, string DisplayName, string Status, string Endpoint, string ActiveModel, IReadOnlyList<string> AvailableModels, IReadOnlyList<string>? AvailableReasoningEfforts = null, string? DefaultReasoningEffort = null);
    public sealed record PreviewSettingsDto(string Domain, int DefaultTtlDays, string Namespace);
    public sealed record RepositoryHostingSettingsDto(string Provider, string Mode, string ApiBaseUrl, bool CanCreateRepositories);
    public sealed record AuthentikSettingsDto(bool Enabled, string Authority, string UsersEndpoint);
    public sealed record MetricsDto(Guid? BoardId, int TokensUsed, int CodeAdded, int CodeDeleted, int PipelineRuns);
    public sealed record AssigneeDto(string Id, string DisplayName, string Email, string Source);
    public sealed record GitHubInstallUrlDto(string Url);
    public sealed record GitHubRepositoryPickerDto(string Status, string? Message, IReadOnlyList<RepositoryDto> Repositories, long? ActiveInstallationId = null);
    public sealed record RepositorySkillDraftDto(string Name, string Description, string Content, bool Enabled = true);
    public sealed record RepositoryProfileDto(
        string ImplementationProfile,
        string DisplayName,
        double Confidence,
        IReadOnlyList<string> EnabledSkills,
        string Instructions,
        IReadOnlyList<string> Signals,
        string Source = "scanner",
        IReadOnlyList<string>? CapabilityTags = null,
        IReadOnlyList<RepositorySkillDraftDto>? SkillDrafts = null,
        string? AnalyzerModel = null,
        DateTimeOffset? AnalyzedAt = null);

    public sealed record UserIdentityRequest(string Subject, string DisplayName, string Email, string? AvatarUrl = null);
    public sealed record CreateTeamRequest(string Name);
    public sealed record UpsertTeamMemberRequest(Guid UserId, string Role);
    public sealed record InviteTeamMemberRequest(string Email, string Role);
    public sealed record CreateWorkspaceRequest(string Name, string EnvironmentName, string Region);
    public sealed record CreateRepositoryRequest(string Provider, string Name, string RemoteUrl, string DefaultBranch, string? WebUrl = null, string? Owner = null, string? ImplementationProfile = null);
    public sealed record CreateBoardRequest(string Name, Guid? RepositoryId, string? RepositoryProvider, string? RepositoryName, string? RepositoryRemoteUrl, string? RepositoryWebUrl, string? RepositoryDefaultBranch, string? RepositoryOwner = null, string? ImplementationProfile = null, string? ProviderMode = null, string? GitHubRepositoryId = null, string? CustomRepositoryUrl = null, IReadOnlyList<Guid>? TeamIds = null, BoardGitOpsSettingsRequest? GitOpsSettings = null, BoardAiContextRequest? AiContext = null, RepositoryProfileDto? RepositoryProfile = null);
    public sealed record BoardGitOpsSettingsRequest(IReadOnlyList<string>? AllowedPaths, string? ArgoNamespace, string? ArgoApplicationSelector);
    public sealed record BoardAiContextRequest(string? Instructions, IReadOnlyList<string>? EnabledSkills, bool? AskWhenUncertain);
    public sealed record LinkBoardRepositoryRequest(Guid RepositoryId, bool IsPrimary, string? ImplementationProfile = null);
    public sealed record SyncGitHubRepositoryRequest(Guid? RepositoryId = null, string? Owner = null, string? Name = null, bool Private = true, string? Description = null, long? InstallationId = null, string? ImplementationProfile = null, bool CreateNew = false, string? RemoteUrl = null, string? WebUrl = null, string? DefaultBranch = null);
    public sealed record UpsertBoardTeamAccessRequest(string Role);
    public sealed record CreateBoardSecretRequest(string Key, string Value, Guid? RepositoryId = null);
    public sealed record CreateWorkItemRequest(Guid BoardId, string Type, string Title, string Description, string Status, string Priority, string? Assignee);
    public sealed record UpdateWorkItemRequest(string Title, string Description, string Type, string Status, string Priority, string? Assignee);
    public sealed record MoveWorkItemRequest(string Status, int SortOrder);
    public sealed record AddCommentRequest(string Author, string Kind, string Body);
    public sealed record UpdateCommentRequest(string Actor, string Body);
    public sealed record StartAiPlanRequest(string Provider, string Model, string? ReasoningEffort = null);
    public sealed record ApproveAiRunRequest(string ApprovedBy, string? ReasoningEffort = null);
    public sealed record DiscardAiRunRequest(string DiscardedBy);
    public sealed record ApprovePullRequestRequest(string ApprovedBy);
    public sealed record PreviewActionRequest(string Actor);
    public sealed record DeleteAndCleanupRequest(string Actor);
    public sealed record AdoptCleanupPullRequestRequest(string Actor, string PullRequestUrl, Guid? SourceImplementationRunId = null);
    public sealed record RecordPipelineRunRequest(Guid RepositoryId, Guid? BoardId, Guid? WorkItemId, string Stage, string Status, string Message, string? Url = null, int TokensUsed = 0, int CodeAdded = 0, int CodeDeleted = 0);
    public sealed record ExecutePipelineRunRequest(string Actor);
    public sealed record StartImplementationRunRequest(Guid AiRunId, string Actor, Guid? RepositoryId = null, string? ReasoningEffort = null);
    public sealed record GitHubIntegrationCallbackRequest(long InstallationId, string AccountLogin, string AccountType, string InstalledBy, int RepositoriesCount = 0, string Status = "Installed");
    public sealed record UpdateAiSessionProviderRequest(string ProviderSessionId);
    public sealed record GitHubCallbackRequest(Guid WorkItemId, string Repository, string Branch, string? PullRequestUrl, string Image, string ChecksStatus, string? StaticHtml = null);

    public class AiPlanProviderUnavailableException(string message) : InvalidOperationException(message);
    public sealed class OllamaUnavailableException(string message) : AiPlanProviderUnavailableException(message);

    public static class PipelineJobManifestRenderer
    {
        public const string Namespace = "rosenvall-devops-pipelines";

        public static string JobName(PipelineRunDto run, RepositoryDto repository) =>
            SafeName($"pipeline-{run.Stage}-{repository.Name}-{run.Id:N}");

        public static string Render(PipelineRunDto run, RepositoryDto repository)
        {
            var name = JobName(run, repository);
            return $$"""
                   apiVersion: batch/v1
                   kind: Job
                   metadata:
                     name: {{name}}
                     namespace: {{Namespace}}
                     labels:
                       app.kubernetes.io/part-of: rosenvall-devops-pipeline
                       rosenvall.devops/repository: {{SafeName(repository.Name)}}
                   spec:
                     backoffLimit: 0
                     activeDeadlineSeconds: 3600
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

    public static class RepositoryImplementationJobManifestRenderer
    {
        public const string Namespace = "rosenvall-devops";

        public static string JobName(ImplementationRunDto run, WorkItemDetailDto context) =>
            SafeName($"impl-{context.Item.Key}-{run.Id:N}");

        public static string GitHubTokenSecretName(ImplementationRunDto run) =>
            SafeName($"github-token-{run.Id:N}");

        public static string RenderGitHubTokenSecret(ImplementationRunDto run, string token) =>
            $$"""
              apiVersion: v1
              kind: Secret
              metadata:
                name: {{GitHubTokenSecretName(run)}}
                namespace: {{Namespace}}
                labels:
                  app.kubernetes.io/part-of: rosenvall-devops-implementation
                  rosenvall.devops/implementation-run: {{run.Id}}
              type: Opaque
              stringData:
                token: "{{Escape(token)}}"
              """;

        public static string Render(ImplementationRunDto run, RepositoryDto repository, AiRun aiRun, WorkItemDetailDto context, string model, string? reasoningEffort, string githubSecretName = "rosenvall-devops-github", AiSessionDto? aiSession = null, IReadOnlyList<BoardSecretDto>? boardSecrets = null)
        {
            var jobName = JobName(run, context);
            var ownerRepo = string.IsNullOrWhiteSpace(repository.Owner) ? repository.Name : $"{repository.Owner}/{repository.Name}";
            var prompt = Convert.ToBase64String(Encoding.UTF8.GetBytes(BuildPrompt(run, repository, aiRun, context)));
            var allowedPaths = Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join('\n', context.BoardContext?.GitOpsSettings?.AllowedPaths ?? [])));
            var secretEnv = RenderSecretEnvironment(boardSecrets ?? []);
            var codexCommand = string.IsNullOrWhiteSpace(aiSession?.ProviderSessionId)
                ? "codex exec --ephemeral --ignore-user-config --ignore-rules --skip-git-repo-check --sandbox workspace-write -c \"approval_policy=\\\"never\\\"\" -m \"$CODEX_MODEL\" -c \"model_reasoning_effort=$CODEX_REASONING_EFFORT\" - < \"$workspace/prompt.md\""
                : "codex exec resume --ephemeral --ignore-user-config --ignore-rules --skip-git-repo-check --sandbox workspace-write -c \"approval_policy=\\\"never\\\"\" -m \"$CODEX_MODEL\" -c \"model_reasoning_effort=$CODEX_REASONING_EFFORT\" \"$ROSENVALL_CODEX_SESSION_ID\" - < \"$workspace/prompt.md\"";
            return $$"""
                   apiVersion: batch/v1
                   kind: Job
                   metadata:
                     name: {{jobName}}
                     namespace: {{Namespace}}
                     labels:
                       app.kubernetes.io/part-of: rosenvall-devops-implementation
                       rosenvall.devops/work-item: {{SafeName(context.Item.Key)}}
                   spec:
                     backoffLimit: 0
                     activeDeadlineSeconds: 3600
                     template:
                       metadata:
                         labels:
                           app.kubernetes.io/name: {{jobName}}
                       spec:
                         automountServiceAccountToken: false
                         restartPolicy: Never
                         securityContext:
                           fsGroup: 1000
                           seccompProfile:
                             type: RuntimeDefault
                         volumes:
                           - name: codex-home
                             emptyDir: {}
                           - name: codex-home-source
                             persistentVolumeClaim:
                               claimName: rosenvall-devops-codex-home
                         initContainers:
                           - name: prepare-codex-home
                             image: ghcr.io/carnufex/rosenvall-devops-api:main
                             imagePullPolicy: Always
                             securityContext:
                               runAsUser: 0
                               runAsGroup: 0
                               allowPrivilegeEscalation: false
                             volumeMounts:
                               - name: codex-home
                                 mountPath: /app/codex-home
                               - name: codex-home-source
                                 mountPath: /codex-home-source
                                 readOnly: true
                             command:
                               - sh
                               - -lc
                               - |
                                 set -eu
                                 mkdir -p /app/codex-home
                                 for file in auth.json config.toml installation_id models_cache.json; do
                                   if [ -f "/codex-home-source/$file" ]; then
                                     cp -a "/codex-home-source/$file" "/app/codex-home/$file"
                                   fi
                                 done
                                 mkdir -p /app/codex-home/tmp
                                 chown -R 1000:1000 /app/codex-home
                                 chmod 700 /app/codex-home/tmp
                                 if [ -f /app/codex-home/auth.json ]; then chmod 600 /app/codex-home/auth.json; fi
                                 if [ -f /app/codex-home/config.toml ]; then chmod 600 /app/codex-home/config.toml; fi
                         containers:
                           - name: runner
                             image: ghcr.io/carnufex/rosenvall-devops-api:main
                             imagePullPolicy: Always
                             securityContext:
                               runAsNonRoot: true
                               runAsUser: 1000
                               runAsGroup: 1000
                               allowPrivilegeEscalation: false
                               capabilities:
                                 drop:
                                   - ALL
                             volumeMounts:
                               - name: codex-home
                                 mountPath: /app/codex-home
                             env:
                               - name: GITHUB_TOKEN
                                 valueFrom:
                                   secretKeyRef:
                                     name: {{githubSecretName}}
                                     key: token
                               - name: HOME
                                 value: /home/ubuntu
                               - name: USER
                                 value: ubuntu
                               - name: SHELL
                                 value: /bin/bash
                               - name: CODEX_HOME
                                 value: /app/codex-home
                               - name: CODEX_MODEL
                                 value: "{{Escape(model)}}"
                               - name: CODEX_REASONING_EFFORT
                                 value: "{{Escape(CodexCliArguments.NormalizeReasoningEffort(reasoningEffort) ?? "high")}}"
                               - name: ROSENVALL_REPOSITORY_URL
                                 value: "{{Escape(repository.RemoteUrl)}}"
                               - name: ROSENVALL_REPOSITORY
                                 value: "{{Escape(ownerRepo)}}"
                               - name: ROSENVALL_DEFAULT_BRANCH
                                 value: "{{Escape(repository.DefaultBranch)}}"
                               - name: ROSENVALL_BRANCH
                                 value: "{{Escape(run.Branch)}}"
                               - name: ROSENVALL_WORK_ITEM_KEY
                                 value: "{{Escape(context.Item.Key)}}"
                               - name: ROSENVALL_WORK_ITEM_TITLE
                                 value: "{{Escape(context.Item.Title)}}"
                               - name: ROSENVALL_PROMPT_B64
                                 value: "{{prompt}}"
                               - name: ROSENVALL_ALLOWED_PATHS_B64
                                 value: "{{allowedPaths}}"
                               - name: ROSENVALL_CODEX_SESSION_ID
                                 value: "{{Escape(aiSession?.ProviderSessionId)}}"
                   {{secretEnv}}
                             command:
                               - sh
                               - -lc
                               - |
                                 set -eu
                                 workspace="/tmp/rosenvall-workspace"
                                 mkdir -p "$workspace"
                                 json_escape() { printf '%s' "$1" | tr '\r\n' '  ' | sed 's/\\/\\\\/g; s/"/\\"/g'; }
                                 echo "RDO_STEP=Cloning"
                                 auth_remote="$(printf '%s' "$ROSENVALL_REPOSITORY_URL" | sed "s#https://github.com/#https://x-access-token:${GITHUB_TOKEN}@github.com/#")"
                                 git clone --branch "$ROSENVALL_DEFAULT_BRANCH" "$auth_remote" "$workspace/repo"
                                 cd "$workspace/repo"
                                  git config user.name "Rosenvall DevOps"
                                  git config user.email "devops@rosenvall.se"
                                  git switch -c "$ROSENVALL_BRANCH"
                                  echo "RDO_STEP=Inspecting"
                                  git status --short
                                  printf '%s' "$ROSENVALL_PROMPT_B64" | base64 -d > "$workspace/prompt.md"
                                  printf '%s' "$ROSENVALL_ALLOWED_PATHS_B64" | base64 -d > "$workspace/allowed-paths.txt"
                                  echo "RDO_STEP=Implementing"
                                  {{codexCommand}}
                                  if [ -f package.json ]; then
                                    echo "RDO_STEP=Testing"
                                    if node -e "const p=require('./package.json'); process.exit(p.scripts && p.scripts.test ? 0 : 1)"; then
                                      npm test || { echo "RDO_FAILURE=npm test failed"; exit 23; }
                                    elif node -e "const p=require('./package.json'); process.exit(p.scripts && p.scripts.build ? 0 : 1)"; then
                                      npm run build || { echo "RDO_FAILURE=npm build failed"; exit 24; }
                                    else
                                      echo "No npm test or build script found; skipping npm checks."
                                    fi
                                  fi
                                  if ls *.sln *.slnx >/dev/null 2>&1; then
                                    echo "RDO_STEP=Testing"
                                    dotnet test --no-restore || { echo "RDO_FAILURE=dotnet test failed"; exit 25; }
                                  fi
                                  echo "RDO_STEP=Validating"
                                  git status --porcelain | sed 's/^...//' | sed 's#.* -> ##' > "$workspace/uncommitted-files.txt"
                                  git diff --name-only "$ROSENVALL_DEFAULT_BRANCH"...HEAD > "$workspace/committed-files.txt"
                                  cat "$workspace/uncommitted-files.txt" "$workspace/committed-files.txt" | sed '/^$/d' | sort -u > "$workspace/changed-files.txt"
                                  if [ ! -s "$workspace/changed-files.txt" ]; then echo "RDO_FAILURE=No changes produced"; exit 20; fi
                                  outside=""
                                  if [ -s "$workspace/allowed-paths.txt" ]; then
                                    while IFS= read -r changed; do
                                      [ -z "$changed" ] && continue
                                      allowed=0
                                      while IFS= read -r prefix; do
                                        [ -z "$prefix" ] && continue
                                        case "$changed" in "$prefix"*) allowed=1 ;; esac
                                      done < "$workspace/allowed-paths.txt"
                                      if [ "$allowed" != "1" ]; then outside="$outside $changed"; fi
                                    done < "$workspace/changed-files.txt"
                                  fi
                                  if [ -n "$outside" ]; then echo "RDO_FAILURE=Changed files outside allowed GitOps paths"; printf '%s\n' "$outside"; exit 22; fi
                                  git status --short
                                 commit_title="$(printf 'Implement %s %s' "$ROSENVALL_WORK_ITEM_KEY" "$ROSENVALL_WORK_ITEM_TITLE" | tr '\r\n' '  ')"
                                 if [ -s "$workspace/uncommitted-files.txt" ]; then
                                   git add -A
                                   git commit -m "$commit_title"
                                 else
                                   echo "No uncommitted changes remain; using existing branch commits."
                                 fi
                                 commit="$(git rev-parse HEAD)"
                                 echo "RDO_COMMIT=$commit"
                                 echo "RDO_STEP=Pushing"
                                 git push --set-upstream origin "$ROSENVALL_BRANCH"
                                 repo_owner="${ROSENVALL_REPOSITORY%%/*}"
                                 existing_pr_url="$(curl -fsS -G "https://api.github.com/repos/$ROSENVALL_REPOSITORY/pulls" -H "Authorization: Bearer $GITHUB_TOKEN" -H "Accept: application/vnd.github+json" --data-urlencode "state=open" --data-urlencode "head=$repo_owner:$ROSENVALL_BRANCH" | sed -n 's/.*"html_url":[[:space:]]*"\([^"]*\)".*/\1/p' | head -1)"
                                 if [ -n "$existing_pr_url" ]; then
                                   pr_url="$existing_pr_url"
                                 else
                                   pr_title="$(json_escape "$commit_title")"
                                   pr_head="$(json_escape "$ROSENVALL_BRANCH")"
                                   pr_base="$(json_escape "$ROSENVALL_DEFAULT_BRANCH")"
                                   pr_payload="{\"title\":\"$pr_title\",\"head\":\"$pr_head\",\"base\":\"$pr_base\",\"body\":\"Generated by Rosenvall DevOps.\"}"
                                   pr_url="$(curl -fsS -X POST "https://api.github.com/repos/$ROSENVALL_REPOSITORY/pulls" -H "Authorization: Bearer $GITHUB_TOKEN" -H "Accept: application/vnd.github+json" -H "Content-Type: application/json" -d "$pr_payload" | sed -n 's/.*"html_url":[[:space:]]*"\([^"]*\)".*/\1/p' | head -1)"
                                 fi
                                 if [ -z "$pr_url" ]; then echo "RDO_FAILURE=Pull request was created or requested, but no PR URL was returned"; exit 21; fi
                                 echo "RDO_STEP=PullRequestReady"
                                 echo "RDO_PULL_REQUEST_URL=$pr_url"
                   """;
        }

        private static string RenderSecretEnvironment(IReadOnlyList<BoardSecretDto> secrets)
        {
            if (secrets.Count == 0)
            {
                return "";
            }

            var builder = new StringBuilder();
            foreach (var secret in secrets.OrderBy(secret => secret.Key, StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine("            - name: " + EnvName(secret.Key));
                builder.AppendLine("              valueFrom:");
                builder.AppendLine("                secretKeyRef:");
                builder.AppendLine("                  name: " + BoardSecretManifestRenderer.SecretName(secret));
                builder.AppendLine("                  key: " + BoardSecretManifestRenderer.SecretDataKey(secret));
            }

            return builder.ToString().TrimEnd();
        }

        private static string EnvName(string key)
        {
            var safe = new string(key.Select(character => char.IsLetterOrDigit(character) ? char.ToUpperInvariant(character) : '_').ToArray()).Trim('_');
            return string.IsNullOrWhiteSpace(safe) ? "BOARD_SECRET" : safe;
        }

        private static string BuildPrompt(ImplementationRunDto run, RepositoryDto repository, AiRun aiRun, WorkItemDetailDto context)
        {
            var unityGuidance = string.Equals(repository.ImplementationProfile, "unity", StringComparison.OrdinalIgnoreCase)
                ? "This repository is a Unity project. Respect Unity folder conventions, avoid destructive scene or asset rewrites, and document any change that requires Unity Editor/MCP validation."
                : string.Equals(repository.ImplementationProfile, "gitops-homelab", StringComparison.OrdinalIgnoreCase)
                    ? "This repository is a GitOps homelab repository. Follow declared allowed paths, keep all cluster changes PR-first, and rely on ArgoCD to reconcile after human review."
                    : "Follow the repository's existing stack and style. Make the smallest coherent production change.";
            return $$"""
                   You are implementing a Rosenvall DevOps work item directly in a Git repository.

                   Work item: {{context.Item.Key}} {{context.Item.Title}}
                   Priority: {{context.Item.Priority}}
                   Description:
                   {{context.Description}}

                   Approved AI plan #{{aiRun.SequenceNumber}}:
                   {{aiRun.Plan}}

                   Repository: {{repository.Provider}} / {{repository.Owner}}/{{repository.Name}}
                   Implementation profile: {{repository.ImplementationProfile}}

                   Board context:
                   {{PromptContextRenderer.RenderImplementationContext(context)}}

                   {{unityGuidance}}

                   Requirements:
                   - Inspect the repository before editing.
                   - Modify source files in the checked-out repository.
                   - Obey the allowed path scope when one is configured.
                   - Keep GitOps and repository implementation PR-first; do not apply changes directly to the cluster.
                   - Do not run git add, git commit, git push, gh pr, or GitHub pull request API calls.
                   - Leave all file changes uncommitted in the current checkout; the runner owns commit, push, and pull request creation.
                   - Do not open a pull request yourself.
                   - Do not build a standalone React preview unless the repository itself is a React app and the work item requires it.
                   - Run the relevant lightweight tests or build command if discoverable.
                   - Leave a focused diff suitable for a pull request.
                   """;
        }

        private static string SafeName(string value)
        {
            var chars = value.ToLowerInvariant()
                .Select(c => char.IsLetterOrDigit(c) ? c : '-')
                .ToArray();
            var normalized = new string(chars).Trim('-');
            while (normalized.Contains("--", StringComparison.Ordinal))
            {
                normalized = normalized.Replace("--", "-", StringComparison.Ordinal);
            }

            return string.IsNullOrWhiteSpace(normalized) ? "implementation-run" : normalized[..Math.Min(normalized.Length, 63)].Trim('-');
        }

        private static string Escape(string? value) =>
            (value ?? "")
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal)
                .Replace("\r", "\\r", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal)
                .Replace("\t", "\\t", StringComparison.Ordinal);
    }

    public static class GitHubAppSecretRenderer
    {
        public static string Render(GitHubManifestAppDto app) =>
            $$"""
              apiVersion: v1
              kind: Secret
              metadata:
                name: rosenvall-devops-github-app
                namespace: rosenvall-devops
                labels:
                  app.kubernetes.io/part-of: rosenvall-devops
              type: Opaque
              stringData:
                app-id: "{{app.Id}}"
                app-slug: "{{Escape(app.Slug)}}"
                private-key: |
              {{IndentBlock(app.Pem, 4)}}
              """;

        private static string IndentBlock(string value, int spaces)
        {
            var prefix = new string(' ', spaces);
            return string.Join("\n", value.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd().Split('\n').Select(line => prefix + line));
        }

        private static string Escape(string value) =>
            value.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal)
                .Replace("\r", "\\r", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal)
                .Replace("\t", "\\t", StringComparison.Ordinal);
    }

    public static class RepositoryCleanupJobManifestRenderer
    {
        public const string Namespace = RepositoryImplementationJobManifestRenderer.Namespace;

        public static string JobName(RepositoryCleanupRunDto run, WorkItemDetailDto context) =>
            SafeName($"cleanup-{context.Item.Key}-{run.Id:N}");

        public static string GitHubTokenSecretName(RepositoryCleanupRunDto run) =>
            SafeName($"github-cleanup-token-{run.Id:N}");

        public static string RenderGitHubTokenSecret(RepositoryCleanupRunDto run, string token) =>
            $$"""
              apiVersion: v1
              kind: Secret
              metadata:
                name: {{GitHubTokenSecretName(run)}}
                namespace: {{Namespace}}
                labels:
                  app.kubernetes.io/part-of: rosenvall-devops-repository-cleanup
                  rosenvall.devops/repository-cleanup-run: {{run.Id}}
              type: Opaque
              stringData:
                token: "{{Escape(token)}}"
              """;

        public static string Render(RepositoryCleanupRunDto run, RepositoryDto repository, WorkItemDetailDto context, string model, string? reasoningEffort, string githubSecretName)
        {
            var jobName = JobName(run, context);
            var ownerRepo = string.IsNullOrWhiteSpace(repository.Owner) ? repository.Name : $"{repository.Owner}/{repository.Name}";
            var prompt = Convert.ToBase64String(Encoding.UTF8.GetBytes(BuildPrompt(run, repository, context)));
            var sourceDiff = Convert.ToBase64String(Encoding.UTF8.GetBytes(run.SourcePullRequestDiff ?? ""));
            var allowedPaths = Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join('\n', context.BoardContext?.GitOpsSettings?.AllowedPaths ?? [])));
            return $$"""
                   apiVersion: batch/v1
                   kind: Job
                   metadata:
                     name: {{jobName}}
                     namespace: {{Namespace}}
                     labels:
                       app.kubernetes.io/part-of: rosenvall-devops-repository-cleanup
                       rosenvall.devops/work-item: {{SafeName(context.Item.Key)}}
                   spec:
                     backoffLimit: 0
                     activeDeadlineSeconds: 3600
                     template:
                       metadata:
                         labels:
                           app.kubernetes.io/name: {{jobName}}
                       spec:
                         automountServiceAccountToken: false
                         restartPolicy: Never
                         securityContext:
                           fsGroup: 1000
                           seccompProfile:
                             type: RuntimeDefault
                         volumes:
                           - name: codex-home
                             emptyDir: {}
                           - name: codex-home-source
                             persistentVolumeClaim:
                               claimName: rosenvall-devops-codex-home
                         initContainers:
                           - name: prepare-codex-home
                             image: ghcr.io/carnufex/rosenvall-devops-api:main
                             imagePullPolicy: Always
                             securityContext:
                               runAsUser: 0
                               runAsGroup: 0
                               allowPrivilegeEscalation: false
                             volumeMounts:
                               - name: codex-home
                                 mountPath: /app/codex-home
                               - name: codex-home-source
                                 mountPath: /codex-home-source
                                 readOnly: true
                             command:
                               - sh
                               - -lc
                               - |
                                 set -eu
                                 mkdir -p /app/codex-home
                                 for file in auth.json config.toml installation_id models_cache.json; do
                                   if [ -f "/codex-home-source/$file" ]; then
                                     cp -a "/codex-home-source/$file" "/app/codex-home/$file"
                                   fi
                                 done
                                 mkdir -p /app/codex-home/tmp
                                 chown -R 1000:1000 /app/codex-home
                                 chmod 700 /app/codex-home/tmp
                                 if [ -f /app/codex-home/auth.json ]; then chmod 600 /app/codex-home/auth.json; fi
                                 if [ -f /app/codex-home/config.toml ]; then chmod 600 /app/codex-home/config.toml; fi
                         containers:
                           - name: runner
                             image: ghcr.io/carnufex/rosenvall-devops-api:main
                             imagePullPolicy: Always
                             securityContext:
                               runAsNonRoot: true
                               runAsUser: 1000
                               runAsGroup: 1000
                               allowPrivilegeEscalation: false
                               capabilities:
                                 drop:
                                   - ALL
                             volumeMounts:
                               - name: codex-home
                                 mountPath: /app/codex-home
                             env:
                               - name: GITHUB_TOKEN
                                 valueFrom:
                                   secretKeyRef:
                                     name: {{githubSecretName}}
                                     key: token
                               - name: HOME
                                 value: /home/ubuntu
                               - name: USER
                                 value: ubuntu
                               - name: SHELL
                                 value: /bin/bash
                               - name: CODEX_HOME
                                 value: /app/codex-home
                               - name: CODEX_MODEL
                                 value: "{{Escape(model)}}"
                               - name: CODEX_REASONING_EFFORT
                                 value: "{{Escape(CodexCliArguments.NormalizeReasoningEffort(reasoningEffort) ?? "high")}}"
                               - name: ROSENVALL_REPOSITORY_URL
                                 value: "{{Escape(repository.RemoteUrl)}}"
                               - name: ROSENVALL_REPOSITORY
                                 value: "{{Escape(ownerRepo)}}"
                               - name: ROSENVALL_DEFAULT_BRANCH
                                 value: "{{Escape(repository.DefaultBranch)}}"
                               - name: ROSENVALL_BRANCH
                                 value: "{{Escape(run.Branch)}}"
                               - name: ROSENVALL_WORK_ITEM_KEY
                                 value: "{{Escape(context.Item.Key)}}"
                               - name: ROSENVALL_WORK_ITEM_TITLE
                                 value: "{{Escape(context.Item.Title)}}"
                               - name: ROSENVALL_SOURCE_PULL_REQUEST_URL
                                 value: "{{Escape(run.SourcePullRequestUrl)}}"
                               - name: ROSENVALL_CLEANUP_PROMPT_B64
                                 value: "{{prompt}}"
                               - name: ROSENVALL_SOURCE_PR_DIFF_B64
                                 value: "{{sourceDiff}}"
                               - name: ROSENVALL_ALLOWED_PATHS_B64
                                 value: "{{allowedPaths}}"
                             command:
                               - sh
                               - -lc
                               - |
                                 set -eu
                                 workspace="/tmp/rosenvall-cleanup-workspace"
                                 mkdir -p "$workspace"
                                 json_escape() { printf '%s' "$1" | tr '\r\n' '  ' | sed 's/\\/\\\\/g; s/"/\\"/g'; }
                                 echo "RDO_STEP=Cloning"
                                 auth_remote="$(printf '%s' "$ROSENVALL_REPOSITORY_URL" | sed "s#https://github.com/#https://x-access-token:${GITHUB_TOKEN}@github.com/#")"
                                 git clone --branch "$ROSENVALL_DEFAULT_BRANCH" "$auth_remote" "$workspace/repo"
                                 cd "$workspace/repo"
                                 git config user.name "Rosenvall DevOps"
                                 git config user.email "devops@rosenvall.se"
                                 git switch -c "$ROSENVALL_BRANCH"
                                 printf '%s' "$ROSENVALL_CLEANUP_PROMPT_B64" | base64 -d > "$workspace/prompt.md"
                                 printf '%s' "$ROSENVALL_SOURCE_PR_DIFF_B64" | base64 -d > "$workspace/source-pr.diff"
                                 printf '%s' "$ROSENVALL_ALLOWED_PATHS_B64" | base64 -d > "$workspace/allowed-paths.txt"
                                 echo "RDO_STEP=Implementing"
                                 codex exec --ephemeral --ignore-user-config --ignore-rules --skip-git-repo-check --sandbox workspace-write -c "approval_policy=\"never\"" -m "$CODEX_MODEL" -c "model_reasoning_effort=$CODEX_REASONING_EFFORT" - < "$workspace/prompt.md"
                                 echo "RDO_STEP=Validating"
                                 git status --porcelain | sed 's/^...//' | sed 's#.* -> ##' > "$workspace/changed-files.txt"
                                 if [ ! -s "$workspace/changed-files.txt" ]; then echo "RDO_FAILURE=No cleanup changes produced"; exit 20; fi
                                 outside=""
                                 if [ -s "$workspace/allowed-paths.txt" ]; then
                                   while IFS= read -r changed; do
                                     [ -z "$changed" ] && continue
                                     allowed=0
                                     while IFS= read -r prefix; do
                                       [ -z "$prefix" ] && continue
                                       case "$changed" in "$prefix"*) allowed=1 ;; esac
                                     done < "$workspace/allowed-paths.txt"
                                     if [ "$allowed" != "1" ]; then outside="$outside $changed"; fi
                                   done < "$workspace/changed-files.txt"
                                 fi
                                 if [ -n "$outside" ]; then echo "RDO_FAILURE=Cleanup changed files outside allowed GitOps paths"; printf '%s\n' "$outside"; exit 22; fi
                                 cleanup_title="$(printf 'Clean up %s %s' "$ROSENVALL_WORK_ITEM_KEY" "$ROSENVALL_WORK_ITEM_TITLE" | tr '\r\n' '  ')"
                                 git add -A
                                 git commit -m "$cleanup_title"
                                 commit="$(git rev-parse HEAD)"
                                 echo "RDO_COMMIT=$commit"
                                 echo "RDO_STEP=Pushing"
                                 git push --set-upstream origin "$ROSENVALL_BRANCH"
                                 repo_owner="${ROSENVALL_REPOSITORY%%/*}"
                                 existing_pr_url="$(curl -fsS -G "https://api.github.com/repos/$ROSENVALL_REPOSITORY/pulls" -H "Authorization: Bearer $GITHUB_TOKEN" -H "Accept: application/vnd.github+json" --data-urlencode "state=open" --data-urlencode "head=$repo_owner:$ROSENVALL_BRANCH" | sed -n 's/.*"html_url":[[:space:]]*"\([^"]*\)".*/\1/p' | head -1)"
                                 if [ -n "$existing_pr_url" ]; then
                                   pr_url="$existing_pr_url"
                                 else
                                   pr_title="$(json_escape "$cleanup_title")"
                                   pr_head="$(json_escape "$ROSENVALL_BRANCH")"
                                   pr_base="$(json_escape "$ROSENVALL_DEFAULT_BRANCH")"
                                   source_pr="$(json_escape "$ROSENVALL_SOURCE_PULL_REQUEST_URL")"
                                   pr_payload="{\"title\":\"$pr_title\",\"head\":\"$pr_head\",\"base\":\"$pr_base\",\"body\":\"Cleanup PR generated by Rosenvall DevOps for source PR $source_pr.\"}"
                                   pr_url="$(curl -fsS -X POST "https://api.github.com/repos/$ROSENVALL_REPOSITORY/pulls" -H "Authorization: Bearer $GITHUB_TOKEN" -H "Accept: application/vnd.github+json" -H "Content-Type: application/json" -d "$pr_payload" | sed -n 's/.*"html_url":[[:space:]]*"\([^"]*\)".*/\1/p' | head -1)"
                                 fi
                                 if [ -z "$pr_url" ]; then echo "RDO_FAILURE=Cleanup pull request was created or requested, but no PR URL was returned"; exit 21; fi
                                 echo "RDO_STEP=PullRequestReady"
                                 echo "RDO_CLEANUP_PULL_REQUEST_URL=$pr_url"
                   """;
        }

        private static string BuildPrompt(RepositoryCleanupRunDto run, RepositoryDto repository, WorkItemDetailDto context) =>
            $$"""
              You are cleaning up repository changes for a Rosenvall DevOps work item.

              Work item: {{context.Item.Key}} {{context.Item.Title}}
              Description:
              {{context.Description}}

              Repository: {{repository.Provider}} / {{repository.Owner}}/{{repository.Name}}
              Source pull request: {{run.SourcePullRequestUrl}}
              Source pull request state: {{run.SourcePullRequestState}}

              Board context:
              {{PromptContextRenderer.RenderImplementationContext(context)}}

              Source PR diff is available at /tmp/rosenvall-cleanup-workspace/source-pr.diff.

              Requirements:
              - Remove or revert repository resources introduced by the source pull request.
              - Obey the allowed path scope when one is configured.
              - Keep GitOps cleanup PR-first; do not apply changes directly to the cluster.
              - Do not run git add, git commit, git push, gh pr, or GitHub pull request API calls.
              - Leave all file changes uncommitted in the current checkout; the runner owns commit, push, and pull request creation.
              - Do not open a pull request yourself.
              - Leave a focused cleanup diff suitable for a pull request.
              """;

        private static string SafeName(string value)
        {
            var chars = value.ToLowerInvariant()
                .Select(c => char.IsLetterOrDigit(c) ? c : '-')
                .ToArray();
            var normalized = new string(chars).Trim('-');
            while (normalized.Contains("--", StringComparison.Ordinal))
            {
                normalized = normalized.Replace("--", "-", StringComparison.Ordinal);
            }

            return string.IsNullOrWhiteSpace(normalized) ? "repository-cleanup-run" : normalized[..Math.Min(normalized.Length, 63)].Trim('-');
        }

        private static string Escape(string? value) =>
            (value ?? "")
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal)
                .Replace("\r", "\\r", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal)
                .Replace("\t", "\\t", StringComparison.Ordinal);
    }

    public static class BoardSecretManifestRenderer
    {
        public static string Namespace(IConfiguration configuration) =>
            configuration["Secrets:Namespace"] ?? configuration["Preview:Namespace"] ?? "rosenvall-devops";

        public static string SecretName(BoardSecretDto secret) =>
            SafeName($"rdo-board-{secret.BoardId:N}-{(secret.RepositoryId?.ToString("N") ?? "board")}-{secret.Key}");

        public static string SecretDataKey(BoardSecretDto secret) => SafeKey(secret.Key);

        public static string Render(BoardSecretDto secret, string value, IConfiguration configuration)
        {
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
            return $$"""
                   apiVersion: v1
                   kind: Secret
                   metadata:
                     name: {{SecretName(secret)}}
                     namespace: {{Namespace(configuration)}}
                     labels:
                       app.kubernetes.io/part-of: rosenvall-devops
                       rosenvall.devops/board-id: "{{secret.BoardId}}"
                   type: Opaque
                   data:
                     {{SecretDataKey(secret)}}: {{encoded}}
                   """;
        }

        private static string SafeName(string value)
        {
            var safe = new string(value.ToLowerInvariant().Select(character => char.IsLetterOrDigit(character) ? character : '-').ToArray()).Trim('-');
            while (safe.Contains("--", StringComparison.Ordinal))
            {
                safe = safe.Replace("--", "-", StringComparison.Ordinal);
            }

            return string.IsNullOrWhiteSpace(safe) ? "rdo-board-secret" : safe[..Math.Min(63, safe.Length)].Trim('-');
        }

        private static string SafeKey(string value)
        {
            var safe = new string(value.Select(character => char.IsLetterOrDigit(character) || character is '_' or '-' or '.' ? character : '_').ToArray()).Trim('_', '-', '.');
            return string.IsNullOrWhiteSpace(safe) ? "value" : safe;
        }
    }

    public sealed record PreviewCleanupResult(bool Succeeded, string Message)
    {
        public static PreviewCleanupResult Ok(string message) => new(true, message);
        public static PreviewCleanupResult Failed(string message) => new(false, message);
    }

    public sealed class PreviewImplementationRunner(
        DevOpsStore store,
        CodexCliPreviewSourceProvider previewSourceProvider,
        PreviewEnvironmentOrchestrator previews,
        IConfiguration configuration,
        IHubContext<DevOpsHub> hub,
        ILogger<PreviewImplementationRunner> logger)
    {
        public async Task RunAsync(AiRun run, string approvedBy, CancellationToken cancellationToken)
        {
            try
            {
                var context = store.GetWorkItemDetail(run.WorkItemId);
                if (context is null)
                {
                    return;
                }

                var implementationModel = run.Model;
                var reasoningEffort = run.ReasoningEffort ?? configuration["Ai:Codex:ReasoningEffort"] ?? "high";
                var sourceFiles = await previewSourceProvider.GenerateSourceAsync(
                    implementationModel,
                    reasoningEffort,
                    run,
                    context,
                    async line =>
                    {
                        var updated = store.AppendPreviewTerminalLine(run.WorkItemId, line.Stream, line.Message, line.CreatedAt);
                        if (updated is not null)
                        {
                            await hub.Clients.All.SendAsync("previewChanged", updated.Preview, cancellationToken);
                        }
                    },
                    cancellationToken);

                store.AppendPreviewTerminalLine(run.WorkItemId, "system", $"Codex generated {sourceFiles.Count} source files.");
                var implementation = store.CompletePreviewImplementation(run.WorkItemId, sourceFiles, "codex");
                await hub.Clients.All.SendAsync("previewChanged", implementation?.Preview, cancellationToken);

                var manifest = store.RenderPreviewManifest(run.WorkItemId);
                if (manifest is null)
                {
                    store.RecordPreviewFailure(run.WorkItemId, "ManifestMissing", approvedBy, "Preview manifest could not be rendered.");
                    await hub.Clients.All.SendAsync("previewChanged", store.GetWorkItemDetail(run.WorkItemId)?.Preview, cancellationToken);
                    return;
                }

                store.MarkPreviewApplying(run.WorkItemId, "Applying Kubernetes resources.");
                store.AppendPreviewTerminalLine(run.WorkItemId, "system", "kubectl apply started.");
                await hub.Clients.All.SendAsync("previewChanged", store.GetWorkItemDetail(run.WorkItemId)?.Preview, cancellationToken);

                var apply = await previews.ApplyAsync(manifest, cancellationToken);
                store.AppendPreviewTerminalLine(run.WorkItemId, apply.Succeeded ? "system" : "stderr", apply.Message);
                if (!apply.Succeeded)
                {
                    store.RecordPreviewFailure(run.WorkItemId, "ApplyFailed", approvedBy, apply.Message);
                    await hub.Clients.All.SendAsync("previewChanged", store.GetWorkItemDetail(run.WorkItemId)?.Preview, cancellationToken);
                    return;
                }

                store.MarkPreviewProvisioning(run.WorkItemId, apply.Message);
                await hub.Clients.All.SendAsync("previewChanged", store.GetWorkItemDetail(run.WorkItemId)?.Preview, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                logger.LogInformation("Preview implementation was cancelled for AI run {AiRunId}.", run.Id);
            }
            catch (AiPlanProviderUnavailableException ex)
            {
                store.RecordImplementationFailure(run.WorkItemId, approvedBy, ex.Message);
                await hub.Clients.All.SendAsync("previewChanged", store.GetWorkItemDetail(run.WorkItemId)?.Preview, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Preview implementation failed for AI run {AiRunId}", run.Id);
                store.RecordImplementationFailure(run.WorkItemId, approvedBy, $"Unexpected preview implementation failure: {ex.Message}");
                await hub.Clients.All.SendAsync("previewChanged", store.GetWorkItemDetail(run.WorkItemId)?.Preview, CancellationToken.None);
            }
        }
    }

    public sealed class PreviewImplementationRecoveryService(
        DevOpsStore store,
        PreviewImplementationRunner runner,
        IHubContext<DevOpsHub> hub,
        ILogger<PreviewImplementationRecoveryService> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                foreach (var run in store.GetPreviewImplementationRunsAwaitingRecovery())
                {
                    var detail = store.AppendPreviewTerminalLine(run.WorkItemId, "system", "Restarting Codex preview implementation after API restart.");
                    await hub.Clients.All.SendAsync("previewChanged", detail?.Preview, stoppingToken);
                    await runner.RunAsync(run, run.ApprovedBy ?? "system", stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                logger.LogInformation("Preview implementation recovery stopped.");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Preview implementation recovery failed.");
            }
        }
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
                if (!string.Equals(preview.Status, "Applying", StringComparison.OrdinalIgnoreCase) &&
                    IsMissingPreviewNamespace(deployment.Message))
                {
                    return PreviewHealthCheckResult.Failed(
                        "NamespaceNotFound",
                        "Preview namespace was not found. Retry preview setup to recreate the Kubernetes resources.",
                        deployment.Message);
                }

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

        public static bool IsMissingPreviewNamespace(string message) =>
            message.Contains("Error from server (NotFound)", StringComparison.OrdinalIgnoreCase) &&
            message.Contains("namespaces", StringComparison.OrdinalIgnoreCase) &&
            message.Contains("not found", StringComparison.OrdinalIgnoreCase);

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

        public Task<PreviewCleanupResult> DeleteAsync(string manifest, CancellationToken cancellationToken) =>
            RunKubectlAsync("delete -f - --ignore-not-found=true", manifest, cancellationToken);

        public Task<PreviewCleanupResult> GetOutputAsync(string command, CancellationToken cancellationToken) =>
            RunKubectlOutputAsync(command, cancellationToken);

        private async Task<PreviewCleanupResult> RunKubectlOutputAsync(string command, CancellationToken cancellationToken)
        {
            var kubectlPath = configuration["Pipelines:KubectlPath"] ?? configuration["Preview:KubectlPath"] ?? "kubectl";
            var kubeconfigPath = ResolveKubeconfigPath(configuration["Pipelines:KubeconfigPath"] ?? configuration["Preview:KubeconfigPath"] ?? "tofu/output/kubeconfig");
            if (!string.IsNullOrWhiteSpace(kubeconfigPath) && !File.Exists(kubeconfigPath))
            {
                return PreviewCleanupResult.Failed($"Pipeline job command failed: Configured kubeconfig was not found: {kubeconfigPath}");
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
                return process.ExitCode == 0
                    ? PreviewCleanupResult.Ok(output)
                    : PreviewCleanupResult.Failed(string.IsNullOrWhiteSpace(error) ? output : error);
            }
            catch (Exception ex) when (PreviewEnvironmentOrchestrator.IsRecoverableKubectlException(ex))
            {
                logger.LogWarning(ex, "Pipeline job command failed.");
                return PreviewCleanupResult.Failed(ex.Message);
            }
        }

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

    public sealed class GitOpsStatusReader(PipelineJobOrchestrator jobs)
    {
        public async Task<GitOpsApplicationsResponseDto> ReadApplicationsAsync(BoardGitOpsSettingsDto? settings, IConfiguration configuration, CancellationToken cancellationToken)
        {
            if (settings is null)
            {
                return new GitOpsApplicationsResponseDto([], "GitOps settings are not configured for this board.");
            }

            var selector = string.IsNullOrWhiteSpace(settings.ArgoApplicationSelector)
                ? ""
                : $" -l \"{settings.ArgoApplicationSelector.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
            var result = await jobs.GetOutputAsync($"get applications.argoproj.io -n {settings.ArgoNamespace}{selector} -o json", cancellationToken);
            if (!result.Succeeded)
            {
                return FromKubectlFailure(result.Message);
            }

            try
            {
                using var document = JsonDocument.Parse(result.Message);
                var applications = ParseApplicationsJson(document.RootElement, configuration["ArgoCD:BaseUrl"]);
                var message = applications.Count == 0
                    ? string.IsNullOrWhiteSpace(settings.ArgoApplicationSelector)
                        ? "No ArgoCD applications were found in the configured namespace."
                        : "No ArgoCD applications matched the configured selector."
                    : null;
                return new GitOpsApplicationsResponseDto(applications, message);
            }
            catch (JsonException)
            {
                return new GitOpsApplicationsResponseDto([], "kubectl returned invalid ArgoCD Application JSON.");
            }
        }

        public static IReadOnlyList<GitOpsApplicationStatusDto> ParseApplicationsJson(JsonElement root, string? argoBaseUrl = null)
        {
            if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return items.EnumerateArray()
                .Select(item =>
                {
                    var metadata = item.TryGetProperty("metadata", out var meta) ? meta : default;
                    var status = item.TryGetProperty("status", out var stat) ? stat : default;
                    var sync = status.ValueKind == JsonValueKind.Object && status.TryGetProperty("sync", out var syncElement) ? syncElement : default;
                    var health = status.ValueKind == JsonValueKind.Object && status.TryGetProperty("health", out var healthElement) ? healthElement : default;
                    var name = JsonString(metadata, "name", "application");
                    return new GitOpsApplicationStatusDto(
                        name,
                        JsonString(metadata, "namespace", "argocd"),
                        JsonString(sync, "status", "Unknown"),
                        JsonString(health, "status", "Unknown"),
                        JsonNullableString(sync, "revision"),
                        FirstNonEmpty(JsonNullableString(health, "message"), JsonNullableString(status, "message"), "Application status read from ArgoCD."),
                        BuildArgoUrl(argoBaseUrl, name),
                        JsonDate(metadata, "creationTimestamp") ?? JsonDate(status, "reconciledAt"));
                })
                .OrderBy(application => application.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        public static GitOpsApplicationsResponseDto FromKubectlFailure(string kubectlError)
        {
            var message = kubectlError ?? "";
            if (message.Contains("doesn't have a resource type", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("the server could not find the requested resource", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("applications.argoproj.io", StringComparison.OrdinalIgnoreCase) && message.Contains("not found", StringComparison.OrdinalIgnoreCase) && !message.Contains("namespaces", StringComparison.OrdinalIgnoreCase))
            {
                return new GitOpsApplicationsResponseDto([], "ArgoCD Application CRD was not found. Install ArgoCD CRDs before reading GitOps application status.");
            }

            if (message.Contains("forbidden", StringComparison.OrdinalIgnoreCase))
            {
                return new GitOpsApplicationsResponseDto([], "The service account lacks access to read ArgoCD Application resources.");
            }

            if (message.Contains("namespaces", StringComparison.OrdinalIgnoreCase) && message.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                return new GitOpsApplicationsResponseDto([], "The configured ArgoCD namespace is missing.");
            }

            return new GitOpsApplicationsResponseDto([], $"Could not read ArgoCD applications: {message}");
        }

        private static string JsonString(JsonElement element, string property, string fallback) =>
            JsonNullableString(element, property) ?? fallback;

        private static string? JsonNullableString(JsonElement element, string property)
        {
            if (element.ValueKind == JsonValueKind.Object &&
                element.TryGetProperty(property, out var value) &&
                value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }

            return null;
        }

        private static DateTimeOffset? JsonDate(JsonElement element, string property) =>
            JsonNullableString(element, property) is { } value && DateTimeOffset.TryParse(value, out var parsed)
                ? parsed
                : null;

        private static string? BuildArgoUrl(string? baseUrl, string name) =>
            string.IsNullOrWhiteSpace(baseUrl)
                ? null
                : $"{baseUrl.TrimEnd('/')}/applications/{WebUtility.UrlEncode(name)}";

        private static string FirstNonEmpty(params string?[] values) =>
            values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
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
                var isTimedOutRecovery = string.Equals(preview.Status, "Failed", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(preview.FailureReason, "Timeout", StringComparison.OrdinalIgnoreCase);
                if (!_startedAt.ContainsKey(preview.Id))
                {
                    _startedAt[preview.Id] = isTimedOutRecovery ? DateTimeOffset.UtcNow : preview.LastCheckedAt ?? DateTimeOffset.UtcNow;
                }

                if (!isTimedOutRecovery && DateTimeOffset.UtcNow - _startedAt[preview.Id] > PreviewTimeout)
                {
                    store.UpdatePreviewHealth(preview.WorkItemId, PreviewHealthCheckResult.Failed("Timeout", "Preview did not become healthy within 3 minutes.", null, preview.PodName));
                    _startedAt.Remove(preview.Id);
                    continue;
                }

                var health = await previews.CheckHealthAsync(preview, cancellationToken);
                if (isTimedOutRecovery && string.Equals(health.Status, "Provisioning", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                store.UpdatePreviewHealth(preview.WorkItemId, health);
                if (!string.Equals(health.Status, "Provisioning", StringComparison.OrdinalIgnoreCase))
                {
                    _startedAt.Remove(preview.Id);
                }
            }
        }
    }

    public sealed class ImplementationRunMonitor(DevOpsStore store, PipelineJobOrchestrator jobs, IHubContext<DevOpsHub> hub, ILogger<ImplementationRunMonitor> logger) : BackgroundService
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(8);
        private static readonly TimeSpan CleanupStuckTimeout = TimeSpan.FromMinutes(10);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CheckImplementationRunsAsync(stoppingToken);
                    await CheckRepositoryCleanupRunsAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Implementation run monitor failed.");
                }

                await Task.Delay(PollInterval, stoppingToken);
            }
        }

        private async Task CheckImplementationRunsAsync(CancellationToken cancellationToken)
        {
            foreach (var run in store.GetImplementationRunsAwaitingStatus())
            {
                var detail = store.GetWorkItemDetail(run.WorkItemId);
                if (detail is null)
                {
                    continue;
                }

                var jobName = RepositoryImplementationJobManifestRenderer.JobName(run, detail);
                var implementationNamespace = RepositoryImplementationJobManifestRenderer.Namespace;
                var logsResult = await jobs.GetOutputAsync($"logs -n {implementationNamespace} job/{jobName} --all-containers --tail=220", cancellationToken);
                var logs = logsResult.Succeeded ? logsResult.Message : string.Empty;
                var nextStatus = StatusFromLogs(logs, run.Status);

                var jobResult = await jobs.GetOutputAsync($"get job {jobName} -n {implementationNamespace} -o json", cancellationToken);
                if (jobResult.Succeeded)
                {
                    using var document = JsonDocument.Parse(jobResult.Message);
                    var succeeded = StatusInt(document.RootElement, "succeeded");
                    var failed = StatusInt(document.RootElement, "failed");
                    if (succeeded > 0)
                    {
                        nextStatus = "PullRequestReady";
                    }
                    else if (failed > 0)
                    {
                        nextStatus = "Failed";
                    }
                }
                else if (run.Status is not "Queued")
                {
                    logs = string.IsNullOrWhiteSpace(logs) ? jobResult.Message : logs;
                    nextStatus = "Failed";
                }

                var failureReason = nextStatus == "Failed"
                    ? FirstMarkerValue(logs, "RDO_FAILURE=") ?? "Repository implementation job failed."
                    : null;
                var updated = store.UpdateImplementationRun(run.Id, nextStatus, logs, failureReason);
                if (updated is not null)
                {
                    await hub.Clients.All.SendAsync("implementationRunChanged", updated, cancellationToken);
                }
            }
        }

        private async Task CheckRepositoryCleanupRunsAsync(CancellationToken cancellationToken)
        {
            foreach (var run in store.GetRepositoryCleanupRunsAwaitingStatus())
            {
                var detail = store.GetWorkItemDetail(run.WorkItemId);
                if (detail is null)
                {
                    continue;
                }

                var jobName = RepositoryCleanupJobManifestRenderer.JobName(run, detail);
                var cleanupNamespace = RepositoryCleanupJobManifestRenderer.Namespace;
                var logsResult = await jobs.GetOutputAsync($"logs -n {cleanupNamespace} job/{jobName} --all-containers --tail=220", cancellationToken);
                var logs = logsResult.Succeeded ? logsResult.Message : string.Empty;
                if (string.IsNullOrWhiteSpace(logs) && DateTimeOffset.UtcNow - run.UpdatedAt > CleanupStuckTimeout)
                {
                    var podName = await FirstKubectlLineAsync($"get pods -n {cleanupNamespace} -l job-name={jobName} -o jsonpath='{{.items[0].metadata.name}}'", cancellationToken);
                    var condition = await LastJobConditionAsync(cleanupNamespace, jobName, cancellationToken);
                    var events = string.IsNullOrWhiteSpace(podName)
                        ? await FirstKubectlLineAsync($"describe job {jobName} -n {cleanupNamespace}", cancellationToken)
                        : await FirstKubectlLineAsync($"get events -n {cleanupNamespace} --field-selector involvedObject.name={podName} --sort-by=.lastTimestamp", cancellationToken);
                    var stuck = store.MarkRepositoryCleanupRunStuck(run.Id, jobName, podName, condition, events);
                    if (stuck is not null)
                    {
                        await hub.Clients.All.SendAsync("repositoryCleanupRunChanged", stuck, cancellationToken);
                    }

                    continue;
                }
                var nextStatus = StatusFromLogs(logs, run.Status);

                var jobResult = await jobs.GetOutputAsync($"get job {jobName} -n {cleanupNamespace} -o json", cancellationToken);
                if (jobResult.Succeeded)
                {
                    using var document = JsonDocument.Parse(jobResult.Message);
                    var succeeded = StatusInt(document.RootElement, "succeeded");
                    var failed = StatusInt(document.RootElement, "failed");
                    if (succeeded > 0)
                    {
                        nextStatus = "PullRequestReady";
                    }
                    else if (failed > 0)
                    {
                        nextStatus = "Failed";
                    }
                }
                else if (run.Status is not "Queued")
                {
                    logs = string.IsNullOrWhiteSpace(logs) ? jobResult.Message : logs;
                    nextStatus = "Failed";
                }

                var failureReason = nextStatus == "Failed"
                    ? FirstMarkerValue(logs, "RDO_FAILURE=") ?? "Repository cleanup job failed."
                    : null;
                var updated = store.UpdateRepositoryCleanupRun(run.Id, nextStatus, logs, failureReason);
                if (updated is not null)
                {
                    await hub.Clients.All.SendAsync("repositoryCleanupRunChanged", updated, cancellationToken);
                }
            }
        }

        private async Task<string?> FirstKubectlLineAsync(string arguments, CancellationToken cancellationToken)
        {
            var result = await jobs.GetOutputAsync(arguments, cancellationToken);
            return result.Succeeded
                ? result.Message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault()
                : result.Message;
        }

        private async Task<string?> LastJobConditionAsync(string cleanupNamespace, string jobName, CancellationToken cancellationToken)
        {
            var jobResult = await jobs.GetOutputAsync($"get job {jobName} -n {cleanupNamespace} -o json", cancellationToken);
            if (!jobResult.Succeeded)
            {
                return jobResult.Message;
            }

            using var document = JsonDocument.Parse(jobResult.Message);
            if (!document.RootElement.TryGetProperty("status", out var status) ||
                !status.TryGetProperty("conditions", out var conditions) ||
                conditions.ValueKind != JsonValueKind.Array)
            {
                return "No Job condition was reported.";
            }

            return conditions.EnumerateArray()
                .Select(condition =>
                {
                    var type = condition.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
                    var reason = condition.TryGetProperty("reason", out var reasonElement) ? reasonElement.GetString() : null;
                    var message = condition.TryGetProperty("message", out var messageElement) ? messageElement.GetString() : null;
                    return string.Join(" - ", new[] { type, reason, message }.Where(value => !string.IsNullOrWhiteSpace(value)));
                })
                .LastOrDefault(value => !string.IsNullOrWhiteSpace(value));
        }

        private static int StatusInt(JsonElement root, string property)
        {
            if (root.TryGetProperty("status", out var status) &&
                status.TryGetProperty(property, out var value) &&
                value.TryGetInt32(out var number))
            {
                return number;
            }

            return 0;
        }

        private static string StatusFromLogs(string logs, string currentStatus)
        {
            if (logs.Contains("RDO_STEP=PullRequestReady", StringComparison.OrdinalIgnoreCase))
            {
                return "PullRequestReady";
            }

            if (logs.Contains("RDO_STEP=Pushing", StringComparison.OrdinalIgnoreCase))
            {
                return "Pushing";
            }

            if (logs.Contains("RDO_STEP=Validating", StringComparison.OrdinalIgnoreCase))
            {
                return "Validating";
            }

            if (logs.Contains("RDO_STEP=Testing", StringComparison.OrdinalIgnoreCase))
            {
                return "Testing";
            }

            if (logs.Contains("RDO_STEP=Implementing", StringComparison.OrdinalIgnoreCase))
            {
                return "Implementing";
            }

            if (logs.Contains("RDO_STEP=Inspecting", StringComparison.OrdinalIgnoreCase))
            {
                return "Inspecting";
            }

            if (logs.Contains("RDO_STEP=Cloning", StringComparison.OrdinalIgnoreCase))
            {
                return "Cloning";
            }

            return currentStatus;
        }

        private static string? FirstMarkerValue(string? logs, string marker)
        {
            if (string.IsNullOrWhiteSpace(logs))
            {
                return null;
            }

            return logs
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.StartsWith(marker, StringComparison.Ordinal))
                .Select(line => line[marker.Length..].Trim())
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
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

    public static class PreviewSourcePolicy
    {
        private const int MaxFiles = 64;
        private const int MaxFileBytes = 128 * 1024;
        private const int MaxTotalBytes = 512 * 1024;
        private static readonly HashSet<string> AllowedRootFiles = new(StringComparer.OrdinalIgnoreCase)
        {
            "package.json",
            "index.html",
            "vite.config.ts",
            "vite.config.js",
            "tsconfig.json",
            "tsconfig.app.json",
            "tsconfig.node.json",
            "postcss.config.js",
            "tailwind.config.ts",
            "tailwind.config.js",
            "components.json"
        };

        public static void Validate(IReadOnlyList<PreviewSourceFile> files)
        {
            if (files.Count == 0)
            {
                throw new ArgumentException("Preview source files are required.", nameof(files));
            }

            if (files.Count > MaxFiles)
            {
                throw new ArgumentException($"Preview source produced {files.Count} files; maximum is {MaxFiles}.", nameof(files));
            }

            var totalBytes = 0;
            foreach (var file in files)
            {
                var normalizedPath = NormalizePath(file.Path);
                if (!IsAllowedPath(normalizedPath))
                {
                    throw new ArgumentException($"Preview source file '{file.Path}' is outside the allowed React preview paths.", nameof(files));
                }

                var bytes = Encoding.UTF8.GetByteCount(file.Content);
                if (bytes > MaxFileBytes)
                {
                    throw new ArgumentException($"Preview source file '{file.Path}' exceeds the {MaxFileBytes} byte limit.", nameof(files));
                }

                totalBytes += bytes;
                if (totalBytes > MaxTotalBytes)
                {
                    throw new ArgumentException($"Preview source exceeds the {MaxTotalBytes} byte total limit.", nameof(files));
                }
            }
        }

        private static string NormalizePath(string path) =>
            path.Replace("\\", "/", StringComparison.Ordinal).Trim();

        private static bool IsAllowedPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) ||
                path.StartsWith("/", StringComparison.Ordinal) ||
                path.Contains("//", StringComparison.Ordinal))
            {
                return false;
            }

            var parts = path.Split("/", StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 ||
                parts.Any(part => part is "." or ".." || part.StartsWith(".", StringComparison.Ordinal)))
            {
                return false;
            }

            return AllowedRootFiles.Contains(path) ||
                path.StartsWith("src/", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("public/", StringComparison.OrdinalIgnoreCase);
        }
    }

    public static class PromptContextRenderer
    {
        public static string RenderPlanningContext(WorkItemDetailDto context)
        {
            var board = context.BoardContext;
            if (board is null)
            {
                return "Repository profile: react-preview";
            }

            var builder = new StringBuilder();
            builder.AppendLine($"Repository profile: {board.RepositoryProfile}");
            AppendRepositoryProfileDraft(builder, board.RepositoryProfileDraft);
            if (board.AiContext is not null)
            {
                builder.AppendLine($"Board instructions: {EmptyAsNone(board.AiContext.Instructions)}");
                builder.AppendLine($"Enabled board skills: {ListOrNone(board.AiContext.EnabledSkills)}");
                builder.AppendLine(board.AiContext.AskWhenUncertain
                    ? "Ask blocking questions only when required facts are missing after applying board instructions, GitOps settings, repository profile signals, and enabled repo skill drafts. Do not ask questions that these skills or conventions answer."
                    : "Ask blocking questions only when implementation would be unsafe without an answer.");
            }

            if (board.GitOpsSettings is not null)
            {
                builder.AppendLine($"Allowed GitOps paths: {ListOrNone(board.GitOpsSettings.AllowedPaths)}");
                builder.AppendLine($"ArgoCD namespace: {board.GitOpsSettings.ArgoNamespace}");
                builder.AppendLine($"ArgoCD Application selector: {EmptyAsNone(board.GitOpsSettings.ArgoApplicationSelector)}");
                builder.AppendLine("GitOps mode is PR-first: generate a plan for repository changes only; do not directly apply Kubernetes manifests.");
                AppendApplicationSetAppRule(builder, board);
            }

            return builder.ToString().TrimEnd();
        }

        public static string RenderImplementationContext(WorkItemDetailDto context)
        {
            var board = context.BoardContext;
            if (board is null)
            {
                return "No board-specific implementation context is configured.";
            }

            var builder = new StringBuilder();
            builder.AppendLine($"Implementation profile: {board.RepositoryProfile}");
            AppendRepositoryProfileDraft(builder, board.RepositoryProfileDraft);
            if (board.AiContext is not null)
            {
                builder.AppendLine($"Board instructions: {EmptyAsNone(board.AiContext.Instructions)}");
                builder.AppendLine($"Enabled board skills: {ListOrNone(board.AiContext.EnabledSkills)}");
                builder.AppendLine("Use these skills when relevant; do not assume unrelated skills are active.");
            }

            if (board.GitOpsSettings is not null)
            {
                builder.AppendLine($"Allowed GitOps paths: {ListOrNone(board.GitOpsSettings.AllowedPaths)}");
                builder.AppendLine($"ArgoCD namespace: {board.GitOpsSettings.ArgoNamespace}");
                builder.AppendLine($"ArgoCD Application selector: {EmptyAsNone(board.GitOpsSettings.ArgoApplicationSelector)}");
                builder.AppendLine("PR-first rule: edit the repository, push a branch, and open a pull request. Do not run kubectl apply, auto-merge, or mutate the cluster directly.");
                AppendApplicationSetAppRule(builder, board);
            }

            return builder.ToString().TrimEnd();
        }

        private static string ListOrNone(IReadOnlyList<string> values) =>
            values.Count == 0 ? "none" : string.Join(", ", values);

        private static string EmptyAsNone(string value) =>
            string.IsNullOrWhiteSpace(value) ? "none" : value.Trim();

        private static void AppendRepositoryProfileDraft(StringBuilder builder, RepositoryProfileDto? profile)
        {
            if (profile is null)
            {
                return;
            }

            builder.AppendLine($"Repository profile name: {profile.DisplayName}");
            if (profile.CapabilityTags is { Count: > 0 })
            {
                builder.AppendLine($"Repository capability tags: {ListOrNone(profile.CapabilityTags)}");
            }

            if (profile.Signals.Count > 0)
            {
                builder.AppendLine($"Repository profile signals: {ListOrNone(profile.Signals)}");
            }

            var enabledDrafts = (profile.SkillDrafts ?? [])
                .Where(draft => draft.Enabled)
                .Take(6)
                .ToArray();
            if (enabledDrafts.Length == 0)
            {
                return;
            }

            builder.AppendLine("Enabled repo skill drafts:");
            foreach (var draft in enabledDrafts)
            {
                builder.AppendLine($"- {draft.Name}: {EmptyAsNone(draft.Description)}");
                builder.AppendLine(TrimSkillContent(draft.Content));
            }
        }

        private static void AppendApplicationSetAppRule(StringBuilder builder, BoardPlanningContextDto board)
        {
            var isGitOpsHomelab = string.Equals(board.RepositoryProfile, "gitops-homelab", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(board.RepositoryProfileDraft?.ImplementationProfile, "gitops-homelab", StringComparison.OrdinalIgnoreCase);
            var usesApplicationSetApps = board.GitOpsSettings?.AllowedPaths.Any(path => path.StartsWith("kubernetes/applications", StringComparison.OrdinalIgnoreCase)) == true ||
                board.RepositoryProfileDraft?.Signals.Any(signal => signal.Contains("kubernetes/applications/application-set.yaml", StringComparison.OrdinalIgnoreCase) || signal.Contains("ApplicationSet", StringComparison.OrdinalIgnoreCase)) == true ||
                board.RepositoryProfileDraft?.CapabilityTags?.Any(tag => tag.Equals("application-set", StringComparison.OrdinalIgnoreCase)) == true;
            if (!isGitOpsHomelab || !usesApplicationSetApps)
            {
                return;
            }

            builder.AppendLine("New ApplicationSet app rule: when adding a new app under kubernetes/applications/<app-name>/, also ensure kubernetes/applications/project.yaml allows destination namespace <app-name> in AppProject/applications.");
            builder.AppendLine("Validation must include kubectl kustomize kubernetes/applications/<app-name> and a check that project.yaml contains namespace: <app-name>.");
        }

        private static string TrimSkillContent(string content)
        {
            var trimmed = string.IsNullOrWhiteSpace(content) ? "No skill content provided." : content.Trim();
            return trimmed.Length <= 1800 ? trimmed : $"{trimmed[..1800].TrimEnd()}\n[truncated]";
        }
    }

    public interface IAiPlanProvider
    {
        string ProviderName { get; }

        Task<string> GeneratePlanAsync(string model, string? reasoningEffort, WorkItemDetailDto context, CancellationToken cancellationToken);
    }

    public sealed class AiPlanProviderRouter(IEnumerable<IAiPlanProvider> providers)
    {
        private readonly IReadOnlyDictionary<string, IAiPlanProvider> _providers = providers
            .GroupBy(provider => provider.ProviderName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        public Task<string> GeneratePlanAsync(string provider, string model, WorkItemDetailDto context, CancellationToken cancellationToken) =>
            GeneratePlanAsync(provider, model, null, context, cancellationToken);

        public Task<string> GeneratePlanAsync(string provider, string model, string? reasoningEffort, WorkItemDetailDto context, CancellationToken cancellationToken)
        {
            if (!_providers.TryGetValue(provider, out var planner))
            {
                throw new AiPlanProviderUnavailableException($"Provider '{provider}' is not configured for planning yet; no plan was created.");
            }

            return planner.GeneratePlanAsync(model, reasoningEffort, context, cancellationToken);
        }
    }

    public static class AiModelPolicy
    {
        public static StartAiPlanRequest? ValidatePlanningRequest(StartAiPlanRequest request, SettingsDto settings)
        {
            var provider = settings.Ai.AvailableProviders.FirstOrDefault(entry =>
                entry.Provider.Equals(request.Provider, StringComparison.OrdinalIgnoreCase) &&
                !entry.Status.Equals("Unavailable", StringComparison.OrdinalIgnoreCase));
            if (provider is null)
            {
                return null;
            }

            var model = provider.AvailableModels.FirstOrDefault(entry => entry.Equals(request.Model, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(model))
            {
                return null;
            }

            var reasoningEffort = CodexCliArguments.NormalizeReasoningEffort(request.ReasoningEffort);
            if (!string.IsNullOrWhiteSpace(request.ReasoningEffort) && string.IsNullOrWhiteSpace(reasoningEffort))
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(reasoningEffort) &&
                provider.AvailableReasoningEfforts is { Count: > 0 } efforts &&
                !efforts.Contains(reasoningEffort, StringComparer.OrdinalIgnoreCase))
            {
                return null;
            }

            return new StartAiPlanRequest(provider.Provider, model, reasoningEffort);
        }
    }

    public sealed record GitHubRepositoryFetchResult(bool Succeeded, string? Message, IReadOnlyList<RepositoryDto> Repositories);

    public static class RepositoryProfileClassifier
    {
        public static RepositoryProfileDto Classify(IEnumerable<string> paths, IReadOnlyDictionary<string, string>? fileContents = null, string? fallbackSignal = null)
        {
            var normalizedPaths = paths
                .Select(path => path.Replace('\\', '/').Trim().TrimStart('/'))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var contents = fileContents is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(fileContents, StringComparer.OrdinalIgnoreCase);

            if (LooksLikeUnity(normalizedPaths))
            {
                return new RepositoryProfileDto(
                    "unity",
                    "Unity",
                    0.98,
                    ["unity", "unity-project", "csharp"],
                    "Unity project detected. Prefer Unity-aware implementation steps, inspect Assets/ and ProjectSettings/, and run Unity tests where available.",
                    MatchingSignals(normalizedPaths, ["ProjectSettings/ProjectVersion.txt", "Assets/", "Packages/manifest.json"]),
                    CapabilityTags: ["unity", "csharp"]);
            }

            if (LooksLikeGitOpsHomelab(normalizedPaths, contents))
            {
                var capabilityTags = GitOpsCapabilityTags(normalizedPaths, contents);
                var repoSkills = RepoSkillDrafts(normalizedPaths, contents);
                var enabledSkills = new[] { "kubernetes", "argocd", "gitops-homelab" }
                    .Concat(capabilityTags.Where(tag => tag is "opentofu" or "talos" or "cloudflare-gateway-routing" or "cluster-diagnostics" or "gitops-app-onboarding"))
                    .Concat(repoSkills.Where(draft => draft.Enabled).Select(draft => draft.Name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return new RepositoryProfileDto(
                    "gitops-homelab",
                    "GitOps homelab",
                    0.94,
                    enabledSkills,
                    "GitOps homelab repository detected. Keep changes scoped to declared app, cluster, or infrastructure paths and prefer pull requests over direct mutation.",
                    MatchingSignals(normalizedPaths, ["apps/", "clusters/", "infrastructure/", "kubernetes/", "kubernetes/applications/application-set.yaml", "tofu/", "bootstrap.ps1", "kustomization.yaml", "Chart.yaml", "kind: Application", "kind: ApplicationSet"]),
                    CapabilityTags: capabilityTags,
                    SkillDrafts: repoSkills);
            }

            if (LooksLikeReactPreview(normalizedPaths, contents))
            {
                return new RepositoryProfileDto(
                    "react-preview",
                    "React preview",
                    0.9,
                    ["react", "vite", "typescript"],
                    "React/Vite project detected. Use the preview implementation flow and verify with the frontend build.",
                    MatchingSignals(normalizedPaths, ["package.json", "vite.config", "src/App"]),
                    CapabilityTags: ["react", "vite", "typescript"]);
            }

            var signals = string.IsNullOrWhiteSpace(fallbackSignal)
                ? ["No framework-specific repository signature was detected."]
                : new[] { fallbackSignal.Trim() };
            return new RepositoryProfileDto(
                "code-repo",
                "Code repo",
                0.55,
                ["code-repo"],
                "Generic code repository detected. Inspect the repository before choosing framework-specific implementation steps.",
                signals,
                CapabilityTags: ["code-repo"]);
        }

        private static bool LooksLikeUnity(IReadOnlyList<string> paths) =>
            HasPath(paths, "ProjectSettings/ProjectVersion.txt") &&
            HasPrefix(paths, "Assets/") &&
            HasPath(paths, "Packages/manifest.json");

        private static bool LooksLikeGitOpsHomelab(IReadOnlyList<string> paths, IReadOnlyDictionary<string, string> contents) =>
            HasPrefix(paths, "clusters/") ||
            HasPrefix(paths, "kubernetes/") ||
            HasPath(paths, "kubernetes/applications/application-set.yaml") ||
            (HasPrefix(paths, "apps/") && HasAnyPathEnding(paths, "kustomization.yaml")) ||
            HasPrefix(paths, "infrastructure/") ||
            HasPrefix(paths, "tofu/") ||
            HasPath(paths, "bootstrap.ps1") ||
            HasAnyPathEnding(paths, "Chart.yaml") ||
            contents.Values.Any(content => ContainsAny(content, "kind: Application", "kind: ApplicationSet") && content.Contains("argoproj.io", StringComparison.OrdinalIgnoreCase)) ||
            contents.Values.Any(content => ContainsAny(content, "GitOps", "Talos", "OpenTofu", "Proxmox", "ExternalSecret", "Gateway API", "Cilium", "Longhorn", "CloudNativePG"));

        private static bool LooksLikeReactPreview(IReadOnlyList<string> paths, IReadOnlyDictionary<string, string> contents)
        {
            var hasViteConfig = paths.Any(path => path.StartsWith("vite.config.", StringComparison.OrdinalIgnoreCase));
            var hasReactSource = paths.Any(path => path.Equals("src/App.tsx", StringComparison.OrdinalIgnoreCase) || path.Equals("src/App.jsx", StringComparison.OrdinalIgnoreCase));
            var packageJson = contents.TryGetValue("package.json", out var packageContent) ? packageContent : "";
            var packageMentionsReact = packageJson.Contains("\"react\"", StringComparison.OrdinalIgnoreCase);
            var packageMentionsVite = packageJson.Contains("\"vite\"", StringComparison.OrdinalIgnoreCase) || packageJson.Contains("@vitejs/plugin-react", StringComparison.OrdinalIgnoreCase);
            return HasPath(paths, "package.json") && (hasViteConfig || hasReactSource || (packageMentionsReact && packageMentionsVite));
        }

        private static IReadOnlyList<string> MatchingSignals(IReadOnlyList<string> paths, IReadOnlyList<string> expected)
        {
            var signals = new List<string>();
            foreach (var signal in expected)
            {
                if (signal.Contains(':', StringComparison.Ordinal))
                {
                    signals.Add(signal);
                }
                else if (signal.EndsWith("/", StringComparison.Ordinal) && HasPrefix(paths, signal))
                {
                    signals.Add(signal);
                }
                else if (signal.Contains('.', StringComparison.Ordinal) && (HasPath(paths, signal) || HasAnyPathEnding(paths, signal)))
                {
                    signals.Add(signal);
                }
                else if (paths.Any(path => path.StartsWith(signal, StringComparison.OrdinalIgnoreCase)))
                {
                    signals.Add(signal);
                }
            }

            return signals.Count == 0 ? expected : signals.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static IReadOnlyList<string> GitOpsCapabilityTags(IReadOnlyList<string> paths, IReadOnlyDictionary<string, string> contents)
        {
            var tags = new List<string> { "kubernetes", "argocd", "gitops" };
            var allContent = string.Join('\n', contents.Values);
            AddIf(tags, "opentofu", HasPrefix(paths, "tofu/") || HasAnyPathEnding(paths, ".tf") || ContainsAny(allContent, "OpenTofu", "terraform"));
            AddIf(tags, "talos", paths.Any(path => path.Contains("talos", StringComparison.OrdinalIgnoreCase)) || ContainsAny(allContent, "Talos", "talos_machine"));
            AddIf(tags, "proxmox", paths.Any(path => path.Contains("proxmox", StringComparison.OrdinalIgnoreCase)) || ContainsAny(allContent, "Proxmox"));
            AddIf(tags, "external-secrets", paths.Any(path => path.Contains("externalsecret", StringComparison.OrdinalIgnoreCase)) || ContainsAny(allContent, "ExternalSecret"));
            AddIf(tags, "gateway-api", paths.Any(path => path.Contains("gateway", StringComparison.OrdinalIgnoreCase)) || ContainsAny(allContent, "gateway.networking.k8s.io", "Gateway API"));
            AddIf(tags, "cilium", paths.Any(path => path.Contains("cilium", StringComparison.OrdinalIgnoreCase)) || ContainsAny(allContent, "Cilium"));
            AddIf(tags, "longhorn", paths.Any(path => path.Contains("longhorn", StringComparison.OrdinalIgnoreCase)) || ContainsAny(allContent, "Longhorn"));
            AddIf(tags, "cloudnativepg", paths.Any(path => path.Contains("cloudnativepg", StringComparison.OrdinalIgnoreCase) || path.Contains("cnpg", StringComparison.OrdinalIgnoreCase)) || ContainsAny(allContent, "CloudNativePG", "postgresql.cnpg.io"));
            AddIf(tags, "application-set", HasPath(paths, "kubernetes/applications/application-set.yaml") || ContainsAny(allContent, "ApplicationSet"));
            foreach (var skill in RepoSkillNames(paths))
            {
                tags.Add(skill);
            }

            return tags.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static IReadOnlyList<RepositorySkillDraftDto> RepoSkillDrafts(IReadOnlyList<string> paths, IReadOnlyDictionary<string, string> contents) =>
            RepoSkillNames(paths)
                .Select(name =>
                {
                    var path = $".codex/skills/{name}/SKILL.md";
                    var content = contents.TryGetValue(path, out var body) ? body.Trim() : "";
                    return new RepositorySkillDraftDto(
                        name,
                        $"Repo-specific skill discovered at {path}.",
                        string.IsNullOrWhiteSpace(content) ? $"Use the repository-local {name} workflow when this board touches matching homelab paths." : content,
                        true);
                })
                .ToArray();

        private static IEnumerable<string> RepoSkillNames(IReadOnlyList<string> paths) =>
            paths
                .Where(path => path.StartsWith(".codex/skills/", StringComparison.OrdinalIgnoreCase) && path.EndsWith("/SKILL.md", StringComparison.OrdinalIgnoreCase))
                .Select(path => path.Split('/', StringSplitOptions.RemoveEmptyEntries))
                .Where(parts => parts.Length >= 3)
                .Select(parts => parts[2])
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase);

        private static void AddIf(List<string> tags, string tag, bool condition)
        {
            if (condition)
            {
                tags.Add(tag);
            }
        }

        private static bool ContainsAny(string value, params string[] needles) =>
            needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));

        private static bool HasPath(IReadOnlyList<string> paths, string expected) =>
            paths.Any(path => path.Equals(expected, StringComparison.OrdinalIgnoreCase));

        private static bool HasPrefix(IReadOnlyList<string> paths, string expectedPrefix) =>
            paths.Any(path => path.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase));

        private static bool HasAnyPathEnding(IReadOnlyList<string> paths, string expectedSuffix) =>
            paths.Any(path => path.EndsWith(expectedSuffix, StringComparison.OrdinalIgnoreCase));
    }

    public static class RepositoryProfileAiParser
    {
        public static RepositoryProfileDto Apply(RepositoryProfileDto scannerProfile, string json, string model)
        {
            try
            {
                using var document = JsonDocument.Parse(ExtractJsonObject(json));
                var root = document.RootElement;
                var implementationProfile = NormalizeImplementationProfile(ReadString(root, "implementationProfile", scannerProfile.ImplementationProfile));
                var displayName = ReadString(root, "displayName", scannerProfile.DisplayName);
                var confidence = ReadDouble(root, "confidence", scannerProfile.Confidence);
                return NormalizeRepositoryProfile(new RepositoryProfileDto(
                    implementationProfile,
                    string.IsNullOrWhiteSpace(displayName) ? scannerProfile.DisplayName : displayName,
                    Math.Clamp(confidence, scannerProfile.Confidence, 1),
                    ReadStringArray(root, "enabledSkills", scannerProfile.EnabledSkills),
                    ReadString(root, "instructions", scannerProfile.Instructions),
                    ReadStringArray(root, "signals", scannerProfile.Signals),
                    "codex",
                    ReadStringArray(root, "capabilityTags", scannerProfile.CapabilityTags ?? []),
                    ReadSkillDrafts(root, scannerProfile.SkillDrafts ?? []),
                    string.IsNullOrWhiteSpace(model) ? "codex" : model.Trim(),
                    DateTimeOffset.UtcNow));
            }
            catch (JsonException)
            {
                return scannerProfile with
                {
                    Source = "scanner",
                    Signals = scannerProfile.Signals
                        .Concat(["Codex profile JSON was invalid; scanner profile kept."])
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray()
                };
            }
        }

        public static RepositoryProfileDto NormalizeRepositoryProfile(RepositoryProfileDto profile)
        {
            var implementationProfile = NormalizeImplementationProfile(profile.ImplementationProfile);
            return profile with
            {
                ImplementationProfile = implementationProfile,
                DisplayName = NormalizeTextValue(profile.DisplayName, ProfileDisplayName(implementationProfile)),
                Confidence = Math.Clamp(profile.Confidence, 0, 1),
                EnabledSkills = NormalizeList(profile.EnabledSkills),
                Instructions = string.IsNullOrWhiteSpace(profile.Instructions)
                    ? "Inspect the repository before choosing framework-specific implementation steps."
                    : profile.Instructions.Trim(),
                Signals = NormalizeList(profile.Signals),
                Source = NormalizeTextValue(profile.Source, "scanner"),
                CapabilityTags = NormalizeList(profile.CapabilityTags ?? []),
                SkillDrafts = NormalizeSkillDrafts(profile.SkillDrafts ?? []),
                AnalyzerModel = string.IsNullOrWhiteSpace(profile.AnalyzerModel) ? null : profile.AnalyzerModel.Trim(),
                AnalyzedAt = profile.AnalyzedAt ?? DateTimeOffset.UtcNow
            };
        }

        private static string ExtractJsonObject(string value)
        {
            var trimmed = value.Trim();
            if (trimmed.StartsWith("{", StringComparison.Ordinal) && trimmed.EndsWith("}", StringComparison.Ordinal))
            {
                return trimmed;
            }

            var start = trimmed.IndexOf('{');
            var end = trimmed.LastIndexOf('}');
            return start >= 0 && end > start ? trimmed[start..(end + 1)] : trimmed;
        }

        private static string ReadString(JsonElement root, string name, string fallback) =>
            root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
                ? element.GetString() ?? fallback
                : fallback;

        private static double ReadDouble(JsonElement root, string name, double fallback) =>
            root.TryGetProperty(name, out var element) && element.TryGetDouble(out var value)
                ? value
                : fallback;

        private static IReadOnlyList<string> ReadStringArray(JsonElement root, string name, IReadOnlyList<string> fallback) =>
            root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Array
                ? NormalizeList(element.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString() ?? ""))
                : NormalizeList(fallback);

        private static IReadOnlyList<RepositorySkillDraftDto> ReadSkillDrafts(JsonElement root, IReadOnlyList<RepositorySkillDraftDto> fallback)
        {
            if (!root.TryGetProperty("skillDrafts", out var draftsElement) || draftsElement.ValueKind != JsonValueKind.Array)
            {
                return NormalizeSkillDrafts(fallback);
            }

            var drafts = new List<RepositorySkillDraftDto>();
            foreach (var item in draftsElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var name = ReadString(item, "name", "");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var enabled = !item.TryGetProperty("enabled", out var enabledElement) || enabledElement.ValueKind != JsonValueKind.False;
                drafts.Add(new RepositorySkillDraftDto(
                    name,
                    ReadString(item, "description", ""),
                    ReadString(item, "content", ""),
                    enabled));
            }

            return NormalizeSkillDrafts(drafts);
        }

        private static IReadOnlyList<string> NormalizeList(IEnumerable<string> values) =>
            values
                .Select(value => value.Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

        private static IReadOnlyList<RepositorySkillDraftDto> NormalizeSkillDrafts(IEnumerable<RepositorySkillDraftDto> drafts) =>
            drafts
                .Where(draft => !string.IsNullOrWhiteSpace(draft.Name))
                .GroupBy(draft => draft.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var draft = group.First();
                    return new RepositorySkillDraftDto(
                        draft.Name.Trim(),
                        string.IsNullOrWhiteSpace(draft.Description) ? $"Repo-specific skill draft for {draft.Name.Trim()}." : draft.Description.Trim(),
                        string.IsNullOrWhiteSpace(draft.Content) ? $"Use the {draft.Name.Trim()} workflow for matching repository tasks." : draft.Content.Trim(),
                        draft.Enabled);
                })
                .ToArray();

        private static string NormalizeImplementationProfile(string? value)
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "code-repo" : value.Trim().ToLowerInvariant();
            return normalized switch
            {
                "unity" => "unity",
                "react" or "react-preview" or "preview" => "react-preview",
                "gitops" or "gitops-homelab" or "homelab" => "gitops-homelab",
                "code" or "code-repo" or "repo" => "code-repo",
                _ => "code-repo"
            };
        }

        private static string NormalizeTextValue(string? value, string fallback) =>
            string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

        private static string ProfileDisplayName(string implementationProfile) => implementationProfile switch
        {
            "gitops-homelab" => "GitOps homelab",
            "unity" => "Unity",
            "react-preview" => "React preview",
            _ => "Code repo"
        };
    }

    public sealed class GitHubRepositoryClient(HttpClient httpClient, IConfiguration configuration)
    {
        public string? ConfiguredToken => configuration["GitHub:Token"];

        public bool IsAppConfigured() =>
            !string.IsNullOrWhiteSpace(configuration["GitHub:AppId"]) &&
            !string.IsNullOrWhiteSpace(configuration["GitHub:AppPrivateKey"]);

        public string GetInstallUrl()
        {
            if (!IsAppConfigured())
            {
                return "/integrations/github/manifest/start";
            }

            var slug = configuration["GitHub:AppSlug"] ?? configuration["GitHub:AppName"] ?? "rosenvall-devops";
            return BuildInstallUrl(slug);
        }

        public static string BuildInstallUrl(string slug)
        {
            var state = Guid.NewGuid().ToString("N");
            return $"https://github.com/apps/{WebUtility.UrlEncode(slug)}/installations/new?state={state}";
        }

        public string RenderManifestStartPage(string state)
        {
            var baseUrl = PublicBaseUrl().TrimEnd('/');
            var callbackUrl = $"{baseUrl}/integrations/github/callback";
            var manifestName = configuration["GitHub:AppName"] ?? $"Rosenvall DevOps {DateTimeOffset.UtcNow:yyyyMMddHHmm}";
            var manifest = JsonSerializer.Serialize(new
            {
                name = manifestName,
                url = baseUrl,
                hook_attributes = new { url = $"{baseUrl}/integrations/github/webhook" },
                redirect_url = callbackUrl,
                callback_urls = new[] { callbackUrl },
                setup_url = callbackUrl,
                setup_on_update = true,
                @public = false,
                default_permissions = new Dictionary<string, string>
                {
                    ["metadata"] = "read",
                    ["contents"] = "write",
                    ["pull_requests"] = "write",
                    ["issues"] = "write",
                    ["actions"] = "read",
                    ["checks"] = "read"
                },
                default_events = new[] { "pull_request", "push" }
            });
            var owner = configuration["GitHub:ManifestOwner"];
            var action = string.IsNullOrWhiteSpace(owner)
                ? "https://github.com/settings/apps/new"
                : $"https://github.com/organizations/{WebUtility.UrlEncode(owner.Trim())}/settings/apps/new";
            return $$"""
                   <!doctype html>
                   <html lang="en">
                   <head><meta charset="utf-8"><title>Install Rosenvall DevOps GitHub App</title></head>
                   <body>
                     <form id="github-app-manifest" action="{{action}}?state={{state}}" method="post">
                       <input type="hidden" name="manifest" value="{{WebUtility.HtmlEncode(manifest)}}">
                     </form>
                     <script>document.getElementById('github-app-manifest').submit();</script>
                     <noscript><button form="github-app-manifest">Continue to GitHub</button></noscript>
                   </body>
                   </html>
                   """;
        }

        public async Task<GitHubManifestAppDto?> CreateAppFromManifestAsync(string code, CancellationToken cancellationToken)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"https://api.github.com/app-manifests/{WebUtility.UrlEncode(code.Trim())}/conversions");
            request.Headers.UserAgent.ParseAdd("rosenvall-devops");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            var id = root.TryGetProperty("id", out var idElement) && idElement.TryGetInt64(out var value) ? value : 0;
            var name = GetString(root, "name");
            var slug = FirstNonEmpty(GetString(root, "slug"), SlugFromHtmlUrl(GetString(root, "html_url")), SafeSlug(name), "rosenvall-devops");
            var pem = GetString(root, "pem");
            return id > 0 && !string.IsNullOrWhiteSpace(pem)
                ? new GitHubManifestAppDto(id, slug, string.IsNullOrWhiteSpace(name) ? slug : name, pem)
                : null;
        }

        public async Task<IReadOnlyList<GitHubIntegrationCallbackRequest>> GetAppInstallationsAsync(CancellationToken cancellationToken)
        {
            if (!IsAppConfigured())
            {
                return [];
            }

            try
            {
                var appId = configuration["GitHub:AppId"]!;
                var privateKey = configuration["GitHub:AppPrivateKey"]!;
                var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/app/installations?per_page=100");
                request.Headers.UserAgent.ParseAdd("rosenvall-devops");
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", CreateAppJwt(appId, privateKey));
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                using var response = await httpClient.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return [];
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                var installations = NormalizeInstallations(document.RootElement, "github-app", "Installed");
                return await WithRepositoryCountsAsync(installations, cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                return [];
            }
        }

        public async Task<GitHubIntegrationCallbackRequest?> GetAppInstallationAsync(long installationId, string installedBy, string status, CancellationToken cancellationToken)
        {
            if (!IsAppConfigured())
            {
                return null;
            }

            try
            {
                var appId = configuration["GitHub:AppId"]!;
                var privateKey = configuration["GitHub:AppPrivateKey"]!;
                var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/app/installations/{installationId}");
                request.Headers.UserAgent.ParseAdd("rosenvall-devops");
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", CreateAppJwt(appId, privateKey));
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                using var response = await httpClient.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                var installation = NormalizeInstallation(document.RootElement, installedBy, status);
                if (installation is null)
                {
                    return null;
                }

                var counted = await WithRepositoryCountsAsync([installation], cancellationToken);
                return counted.FirstOrDefault();
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                return null;
            }
        }

        public async Task<IReadOnlyList<RepositoryDto>> GetRepositoriesAsync(CancellationToken cancellationToken, long? installationId = null)
        {
            var result = await GetRepositoriesResultAsync(cancellationToken, installationId);
            return result.Repositories;
        }

        public async Task<GitHubRepositoryFetchResult> GetRepositoriesResultAsync(CancellationToken cancellationToken, long? installationId = null)
        {
            try
            {
                var token = installationId is { } id
                    ? await CreateInstallationTokenAsync(id, cancellationToken)
                    : configuration["GitHub:Token"];
                if (string.IsNullOrWhiteSpace(token))
                {
                    var message = installationId is null
                        ? "No GitHub token or GitHub App installation is configured."
                        : $"Could not mint a GitHub installation token for installation {installationId}.";
                    return new GitHubRepositoryFetchResult(false, message, []);
                }

                var url = installationId.HasValue
                    ? "https://api.github.com/installation/repositories?per_page=100"
                    : "https://api.github.com/user/repos?per_page=100&sort=updated";
                var request = CreateGitHubRequest(HttpMethod.Get, url, token);
                using var timeout = CreateGitHubTimeout(cancellationToken);
                using var response = await httpClient.SendAsync(request, timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync(cancellationToken);
                    return new GitHubRepositoryFetchResult(false, $"GitHub repository list failed with {(int)response.StatusCode} {response.ReasonPhrase}: {TrimError(error)}", []);
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                var repositories = installationId.HasValue && document.RootElement.TryGetProperty("repositories", out var repositoryElement)
                    ? NormalizeRepositories(repositoryElement)
                    : NormalizeRepositories(document.RootElement);
                return new GitHubRepositoryFetchResult(true, null, repositories);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new GitHubRepositoryFetchResult(false, $"GitHub repository list timed out after {GitHubRequestTimeout().TotalSeconds:0} seconds. Check GitHub App credentials and network access from the API pod.", []);
            }
            catch (HttpRequestException ex)
            {
                return new GitHubRepositoryFetchResult(false, $"GitHub repository list failed: {ex.Message}", []);
            }
        }

        public async Task<RepositoryProfileDto?> GetRepositoryProfileAsync(string owner, string repo, string? branch, CancellationToken cancellationToken, long? installationId = null, string? mode = null)
        {
            try
            {
                var token = installationId is { } id
                    ? await CreateInstallationTokenAsync(id, cancellationToken)
                    : configuration["GitHub:Token"];
                if (string.IsNullOrWhiteSpace(token))
                {
                    return RepositoryProfileClassifier.Classify([], null, "No GitHub token or GitHub App installation token was available for repository profiling.");
                }

                var defaultBranch = string.IsNullOrWhiteSpace(branch) ? await GetRepositoryDefaultBranchAsync(owner, repo, token, cancellationToken) ?? "main" : branch.Trim();
                IReadOnlyList<string> paths;
                try
                {
                    paths = await GetRepositoryTreePathsAsync(owner, repo, defaultBranch, token, cancellationToken);
                }
                catch (HttpRequestException) when (!string.IsNullOrWhiteSpace(branch))
                {
                    defaultBranch = await GetRepositoryDefaultBranchAsync(owner, repo, token, cancellationToken) ?? defaultBranch;
                    paths = await GetRepositoryTreePathsAsync(owner, repo, defaultBranch, token, cancellationToken);
                }

                var contents = await GetRepositoryProfileContentsAsync(owner, repo, defaultBranch, token, paths, cancellationToken);
                var scannerProfile = RepositoryProfileClassifier.Classify(paths, contents);
                if (!string.Equals(mode, "full", StringComparison.OrdinalIgnoreCase))
                {
                    return scannerProfile;
                }

                if (await TryAnalyzeRepositoryProfileWithCodexAsync(owner, repo, defaultBranch, paths, contents, scannerProfile, cancellationToken) is { } aiProfile)
                {
                    return aiProfile;
                }

                return scannerProfile;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return RepositoryProfileClassifier.Classify([], null, $"GitHub repository profile scan timed out after {GitHubRequestTimeout().TotalSeconds:0} seconds.");
            }
            catch (HttpRequestException ex)
            {
                return RepositoryProfileClassifier.Classify([], null, $"GitHub repository profile scan failed: {ex.Message}");
            }
            catch (JsonException ex)
            {
                return RepositoryProfileClassifier.Classify([], null, $"GitHub repository profile response could not be parsed: {ex.Message}");
            }
        }

        private async Task<IReadOnlyDictionary<string, string>> GetRepositoryProfileContentsAsync(string owner, string repo, string branch, string token, IReadOnlyList<string> paths, CancellationToken cancellationToken)
        {
            var contents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var wanted = new List<string>();
            foreach (var path in new[] { "README.md", "AGENTS.md", "package.json", "Packages/manifest.json" })
            {
                if (paths.Any(entry => entry.Equals(path, StringComparison.OrdinalIgnoreCase)))
                {
                    wanted.Add(path);
                }
            }

            wanted.AddRange(paths.Where(path => path.StartsWith(".codex/skills/", StringComparison.OrdinalIgnoreCase) && path.EndsWith("/SKILL.md", StringComparison.OrdinalIgnoreCase)).Take(6));
            wanted.AddRange(paths.Where(IsProfileRelevantYaml).Take(10));
            wanted.AddRange(paths.Where(path => path.StartsWith("tofu/", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".tf", StringComparison.OrdinalIgnoreCase)).Take(4));
            foreach (var path in wanted.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (await GetRepositoryFileContentAsync(owner, repo, path, branch, token, cancellationToken) is { } content)
                {
                    contents[path] = content.Length > 12000 ? content[..12000] : content;
                }
            }

            return contents;
        }

        private async Task<RepositoryProfileDto?> TryAnalyzeRepositoryProfileWithCodexAsync(string owner, string repo, string branch, IReadOnlyList<string> paths, IReadOnlyDictionary<string, string> contents, RepositoryProfileDto scannerProfile, CancellationToken cancellationToken)
        {
            var codexPath = CodexExecutableResolver.Resolve(configuration["Ai:Codex:Path"] ?? "codex");
            var model = NormalizeTextValue(configuration["Ai:Codex:Model"], "gpt-5.5");
            var timeout = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Ai:Codex:ProfileTimeoutSeconds", 25), 5, 90));
            var outputPath = Path.Combine(Path.GetTempPath(), $"rosenvall-codex-repository-profile-{Guid.NewGuid():N}.json");
            try
            {
                using var process = new Process
                {
                    StartInfo = BuildCodexProfileStartInfo(codexPath, model, outputPath),
                    EnableRaisingEvents = true
                };
                if (!process.Start())
                {
                    return null;
                }

                var stdOut = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var stdErr = process.StandardError.ReadToEndAsync(cancellationToken);
                await process.StandardInput.WriteAsync(BuildRepositoryProfilePrompt(owner, repo, branch, paths, contents, scannerProfile).AsMemory(), cancellationToken);
                process.StandardInput.Close();

                using var timeoutCancellation = new CancellationTokenSource(timeout);
                using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCancellation.Token);
                try
                {
                    await process.WaitForExitAsync(linkedCancellation.Token);
                }
                catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    TryKill(process);
                    return null;
                }

                var output = await stdOut;
                var error = await stdErr;
                if (process.ExitCode != 0)
                {
                    return scannerProfile with
                    {
                        Signals = scannerProfile.Signals.Concat([$"Codex profile analysis failed: {FirstUsefulLine(error, output)}"]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                    };
                }

                var json = File.Exists(outputPath) ? await File.ReadAllTextAsync(outputPath, cancellationToken) : output;
                return string.IsNullOrWhiteSpace(json) ? null : RepositoryProfileAiParser.Apply(scannerProfile, json, model);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                return null;
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

        private ProcessStartInfo BuildCodexProfileStartInfo(string codexPath, string model, string outputPath)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = codexPath,
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
            startInfo.ArgumentList.Add("--sandbox");
            startInfo.ArgumentList.Add("read-only");
            startInfo.ArgumentList.Add("--output-last-message");
            startInfo.ArgumentList.Add(outputPath);
            startInfo.ArgumentList.Add("-m");
            startInfo.ArgumentList.Add(model);
            CodexCliArguments.AddReasoningEffort(startInfo, configuration["Ai:Codex:ReasoningEffort"] ?? "high");
            startInfo.ArgumentList.Add("-");
            var codexHome = configuration["Ai:Codex:Home"];
            if (!string.IsNullOrWhiteSpace(codexHome))
            {
                startInfo.Environment["CODEX_HOME"] = codexHome.Trim();
            }

            return startInfo;
        }

        private static string BuildRepositoryProfilePrompt(string owner, string repo, string branch, IReadOnlyList<string> paths, IReadOnlyDictionary<string, string> contents, RepositoryProfileDto scannerProfile)
        {
            var tree = string.Join('\n', paths.Take(600));
            var selectedFiles = string.Join("\n\n", contents.Select(entry => $"--- {entry.Key} ---\n{entry.Value}"));
            return $$"""
              You are Rosenvall DevOps repository profiler. Return strict JSON only, no Markdown.

              Repository: {{owner}}/{{repo}}
              Default branch: {{branch}}
              Scanner profile JSON:
              {{JsonSerializer.Serialize(scannerProfile)}}

              Repository tree sample:
              {{tree}}

              Selected file contents:
              {{selectedFiles}}

              JSON schema:
              {
                "implementationProfile": "gitops-homelab|unity|react-preview|code-repo",
                "displayName": "short readable name",
                "confidence": 0.0,
                "capabilityTags": ["kubernetes", "argocd"],
                "enabledSkills": ["skill-name"],
                "instructions": "board-level implementation guidance",
                "signals": ["concrete evidence"],
                "skillDrafts": [
                  { "name": "skill-name", "description": "what it is for", "content": "editable repository-specific skill guidance", "enabled": true }
                ]
              }

              Rules:
              - Prefer scanner evidence when uncertain.
              - Rosenvalls-Homelab style repos with kubernetes/, ApplicationSet, tofu/, Talos, Proxmox/OpenTofu, ExternalSecrets, Gateway API, Cilium, Longhorn, CloudNativePG, or .codex/skills are gitops-homelab.
              - Do not propose global skill installation or file creation. Return editable drafts only.
              """;
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

        public async Task<string?> CreateInstallationTokenAsync(long installationId, CancellationToken cancellationToken)
        {
            var appId = configuration["GitHub:AppId"];
            var privateKey = configuration["GitHub:AppPrivateKey"];
            if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(privateKey))
            {
                return configuration["GitHub:Token"];
            }

            var jwt = CreateAppJwt(appId, privateKey);
            var request = new HttpRequestMessage(HttpMethod.Post, $"https://api.github.com/app/installations/{installationId}/access_tokens");
            request.Headers.UserAgent.ParseAdd("rosenvall-devops");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", jwt);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            try
            {
                using var timeout = CreateGitHubTimeout(cancellationToken);
                using var response = await httpClient.SendAsync(request, timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                return GetString(document.RootElement, "token");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return null;
            }
            catch (HttpRequestException)
            {
                return null;
            }
        }

        public async Task<RepositoryDto?> CreateRepositoryAsync(GitHubIntegrationDto integration, SyncGitHubRepositoryRequest createRequest, string token, CancellationToken cancellationToken)
        {
            var name = NormalizeRepositoryName(createRequest.Name);
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            var owner = string.IsNullOrWhiteSpace(createRequest.Owner) ? integration.AccountLogin : createRequest.Owner.Trim();
            var organization = integration.AccountType.Equals("Organization", StringComparison.OrdinalIgnoreCase) ||
                integration.AccountType.Equals("Org", StringComparison.OrdinalIgnoreCase);
            var url = organization
                ? $"https://api.github.com/orgs/{Uri.EscapeDataString(owner)}/repos"
                : "https://api.github.com/user/repos";
            using var request = CreateGitHubRequest(HttpMethod.Post, url, token);
            request.Content = JsonContent.Create(new
            {
                name,
                description = string.IsNullOrWhiteSpace(createRequest.Description) ? $"Rosenvall DevOps board repository for {name}." : createRequest.Description.Trim(),
                @private = createRequest.Private,
                auto_init = true
            });
            using var timeout = CreateGitHubTimeout(cancellationToken);
            using var response = await httpClient.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var repository = NormalizeRepositories(JsonSerializer.SerializeToElement(new[] { document.RootElement })).FirstOrDefault();
            return repository is null
                ? null
                : repository with { ImplementationProfile = NormalizeTextValue(createRequest.ImplementationProfile, "code-repo") };
        }

        public async Task<GitHubPullRequestDto?> GetPullRequestAsync(string pullRequestUrl, string token, CancellationToken cancellationToken)
        {
            if (!TryParsePullRequestUrl(pullRequestUrl, out var owner, out var repo, out var number))
            {
                return null;
            }

            var url = $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/pulls/{number}";
            using var request = CreateGitHubRequest(HttpMethod.Get, url, token);
            using var timeout = CreateGitHubTimeout(cancellationToken);
            using var response = await httpClient.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            var headRef = root.TryGetProperty("head", out var head) ? GetString(head, "ref") : "";
            return new GitHubPullRequestDto(
                owner,
                repo,
                number,
                NormalizeTextValue(GetString(root, "state"), "unknown"),
                root.TryGetProperty("merged", out var merged) && merged.ValueKind == JsonValueKind.True,
                NormalizeTextValue(GetString(root, "html_url"), pullRequestUrl),
                string.IsNullOrWhiteSpace(GetString(root, "diff_url")) ? null : GetString(root, "diff_url"),
                string.IsNullOrWhiteSpace(headRef) ? null : headRef);
        }

        public async Task<string?> GetPullRequestDiffAsync(GitHubPullRequestDto pullRequest, string token, CancellationToken cancellationToken)
        {
            var url = pullRequest.DiffUrl ?? $"https://github.com/{pullRequest.Owner}/{pullRequest.Repository}/pull/{pullRequest.Number}.diff";
            using var request = CreateGitHubRequest(HttpMethod.Get, url, token);
            request.Headers.Accept.Clear();
            request.Headers.Accept.ParseAdd("application/vnd.github.diff");
            using var timeout = CreateGitHubTimeout(cancellationToken);
            using var response = await httpClient.SendAsync(request, timeout.Token);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadAsStringAsync(cancellationToken)
                : null;
        }

        public async Task<bool> ClosePullRequestAsync(GitHubPullRequestDto pullRequest, string token, CancellationToken cancellationToken)
        {
            var url = $"https://api.github.com/repos/{Uri.EscapeDataString(pullRequest.Owner)}/{Uri.EscapeDataString(pullRequest.Repository)}/pulls/{pullRequest.Number}";
            using var request = CreateGitHubRequest(HttpMethod.Patch, url, token);
            request.Content = JsonContent.Create(new { state = "closed" });
            using var timeout = CreateGitHubTimeout(cancellationToken);
            using var response = await httpClient.SendAsync(request, timeout.Token);
            return response.IsSuccessStatusCode;
        }

        public async Task<bool> AddPullRequestCommentAsync(GitHubPullRequestDto pullRequest, string body, string token, CancellationToken cancellationToken)
        {
            var url = $"https://api.github.com/repos/{Uri.EscapeDataString(pullRequest.Owner)}/{Uri.EscapeDataString(pullRequest.Repository)}/issues/{pullRequest.Number}/comments";
            using var request = CreateGitHubRequest(HttpMethod.Post, url, token);
            request.Content = JsonContent.Create(new { body });
            using var timeout = CreateGitHubTimeout(cancellationToken);
            using var response = await httpClient.SendAsync(request, timeout.Token);
            return response.IsSuccessStatusCode;
        }

        private static bool TryParsePullRequestUrl(string pullRequestUrl, out string owner, out string repo, out int number)
        {
            owner = "";
            repo = "";
            number = 0;
            if (!Uri.TryCreate(pullRequestUrl, UriKind.Absolute, out var uri))
            {
                return false;
            }

            if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 4 || !segments[2].Equals("pull", StringComparison.OrdinalIgnoreCase) || !int.TryParse(segments[3], out number))
            {
                return false;
            }

            owner = segments[0];
            repo = segments[1];
            return true;
        }

        private async Task<IReadOnlyList<string>> GetRepositoryTreePathsAsync(string owner, string repo, string branch, string token, CancellationToken cancellationToken)
        {
            var url = $"https://api.github.com/repos/{Uri.EscapeDataString(owner.Trim())}/{Uri.EscapeDataString(repo.Trim())}/git/trees/{Uri.EscapeDataString(branch)}?recursive=1";
            using var request = CreateGitHubRequest(HttpMethod.Get, url, token);
            using var timeout = CreateGitHubTimeout(cancellationToken);
            using var response = await httpClient.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new HttpRequestException($"GitHub tree lookup failed with {(int)response.StatusCode} {response.ReasonPhrase}: {TrimError(error)}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("tree", out var tree) || tree.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return tree.EnumerateArray()
                .Select(item => GetString(item, "path"))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private async Task<string?> GetRepositoryDefaultBranchAsync(string owner, string repo, string token, CancellationToken cancellationToken)
        {
            var url = $"https://api.github.com/repos/{Uri.EscapeDataString(owner.Trim())}/{Uri.EscapeDataString(repo.Trim())}";
            using var request = CreateGitHubRequest(HttpMethod.Get, url, token);
            using var timeout = CreateGitHubTimeout(cancellationToken);
            using var response = await httpClient.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var defaultBranch = GetString(document.RootElement, "default_branch");
            return string.IsNullOrWhiteSpace(defaultBranch) ? null : defaultBranch;
        }

        private async Task<string?> GetRepositoryFileContentAsync(string owner, string repo, string path, string branch, string token, CancellationToken cancellationToken)
        {
            var escapedPath = string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
            var url = $"https://api.github.com/repos/{Uri.EscapeDataString(owner.Trim())}/{Uri.EscapeDataString(repo.Trim())}/contents/{escapedPath}?ref={Uri.EscapeDataString(branch)}";
            using var request = CreateGitHubRequest(HttpMethod.Get, url, token);
            using var timeout = CreateGitHubTimeout(cancellationToken);
            using var response = await httpClient.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var content = GetString(document.RootElement, "content");
            if (string.IsNullOrWhiteSpace(content))
            {
                return null;
            }

            var encoding = GetString(document.RootElement, "encoding");
            if (!encoding.Equals("base64", StringComparison.OrdinalIgnoreCase))
            {
                return content;
            }

            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(content.Replace("\n", "", StringComparison.Ordinal)));
            }
            catch (FormatException)
            {
                return null;
            }
        }

        private static HttpRequestMessage CreateGitHubRequest(HttpMethod method, string url, string token)
        {
            var request = new HttpRequestMessage(method, url);
            request.Headers.UserAgent.ParseAdd("rosenvall-devops");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Trim());
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            return request;
        }

        private CancellationTokenSource CreateGitHubTimeout(CancellationToken cancellationToken)
        {
            var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(GitHubRequestTimeout());
            return timeout;
        }

        private TimeSpan GitHubRequestTimeout() =>
            TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("GitHub:RequestTimeoutSeconds", 10), 2, 60));

        private static bool IsProfileRelevantYaml(string path) =>
            (path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)) &&
            (path.Contains("argocd", StringComparison.OrdinalIgnoreCase) ||
             path.StartsWith("apps/", StringComparison.OrdinalIgnoreCase) ||
             path.StartsWith("clusters/", StringComparison.OrdinalIgnoreCase) ||
             path.StartsWith("infrastructure/", StringComparison.OrdinalIgnoreCase) ||
             path.StartsWith("kubernetes/", StringComparison.OrdinalIgnoreCase) ||
             path.Contains("cilium", StringComparison.OrdinalIgnoreCase) ||
             path.Contains("longhorn", StringComparison.OrdinalIgnoreCase) ||
             path.Contains("externalsecret", StringComparison.OrdinalIgnoreCase) ||
             path.Contains("gateway", StringComparison.OrdinalIgnoreCase) ||
             path.Contains("cloudnativepg", StringComparison.OrdinalIgnoreCase));

        private static string CreateAppJwt(string appId, string privateKey)
        {
            static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var now = DateTimeOffset.UtcNow;
            var header = Base64Url(Encoding.UTF8.GetBytes("""{"alg":"RS256","typ":"JWT"}"""));
            var payload = Base64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                iat = now.AddSeconds(-30).ToUnixTimeSeconds(),
                exp = now.AddMinutes(9).ToUnixTimeSeconds(),
                iss = appId
            })));
            using var rsa = RSA.Create();
            rsa.ImportFromPem(privateKey.Replace("\\n", "\n", StringComparison.Ordinal));
            var data = Encoding.ASCII.GetBytes($"{header}.{payload}");
            var signature = Base64Url(rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            return $"{header}.{payload}.{signature}";
        }

        public static IReadOnlyList<RepositoryDto> NormalizeRepositories(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var repositories = new List<RepositoryDto>();
            foreach (var item in root.EnumerateArray())
            {
                var fullName = GetString(item, "full_name");
                var name = GetString(item, "name");
                var owner = item.TryGetProperty("owner", out var ownerElement) ? GetString(ownerElement, "login") : fullName.Split('/').FirstOrDefault() ?? "";
                var cloneUrl = GetString(item, "clone_url");
                var htmlUrl = GetString(item, "html_url");
                var defaultBranch = NormalizeTextValue(GetString(item, "default_branch"), "main");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(cloneUrl))
                {
                    continue;
                }

                repositories.Add(new RepositoryDto(Guid.Empty, "GitHub", name, cloneUrl, htmlUrl, defaultBranch, DateTimeOffset.UtcNow, owner, "code-repo"));
            }

            return repositories;
        }

        public static IReadOnlyList<GitHubIntegrationCallbackRequest> NormalizeInstallations(JsonElement root, string installedBy, string status)
        {
            if (root.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var installations = new List<GitHubIntegrationCallbackRequest>();
            foreach (var item in root.EnumerateArray())
            {
                if (NormalizeInstallation(item, installedBy, status) is { } installation)
                {
                    installations.Add(installation);
                }
            }

            return installations;
        }

        private async Task<IReadOnlyList<GitHubIntegrationCallbackRequest>> WithRepositoryCountsAsync(IReadOnlyList<GitHubIntegrationCallbackRequest> installations, CancellationToken cancellationToken)
        {
            var counted = new List<GitHubIntegrationCallbackRequest>();
            foreach (var installation in installations)
            {
                var repositories = await GetRepositoriesAsync(cancellationToken, installation.InstallationId);
                counted.Add(installation with { RepositoriesCount = repositories.Count });
            }

            return counted;
        }

        private static GitHubIntegrationCallbackRequest? NormalizeInstallation(JsonElement item, string installedBy, string status)
        {
            var id = item.TryGetProperty("id", out var idElement) && idElement.TryGetInt64(out var installationId) ? installationId : 0;
            if (id <= 0)
            {
                return null;
            }

            var account = item.TryGetProperty("account", out var accountElement) ? accountElement : default;
            var login = account.ValueKind == JsonValueKind.Object ? GetString(account, "login") : "";
            var type = account.ValueKind == JsonValueKind.Object ? GetString(account, "type") : "";
            return new GitHubIntegrationCallbackRequest(
                id,
                string.IsNullOrWhiteSpace(login) ? $"installation-{id}" : login,
                string.IsNullOrWhiteSpace(type) ? "GitHub" : type,
                installedBy,
                Status: string.IsNullOrWhiteSpace(status) ? "Installed" : status);
        }

        private static string GetString(JsonElement element, string propertyName) =>
            element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
                ? property.GetString() ?? ""
                : "";

        private static string TrimError(string value)
        {
            var clean = string.IsNullOrWhiteSpace(value) ? "No response body." : value.Trim();
            return clean.Length <= 240 ? clean : clean[..240] + "...";
        }

        private static string NormalizeTextValue(string? value, string fallback) =>
            string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

        private static string NormalizeRepositoryName(string? value)
        {
            var normalized = new string((value ?? "").Trim().Select(character =>
                char.IsLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '-').ToArray()).Trim('-', '.', '_');
            while (normalized.Contains("--", StringComparison.Ordinal))
            {
                normalized = normalized.Replace("--", "-", StringComparison.Ordinal);
            }

            return normalized;
        }

        private string PublicBaseUrl()
        {
            var configured = configuration["PublicBaseUrl"] ?? configuration["GitHub:PublicBaseUrl"];
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return configured.Trim();
            }

            var origin = configuration.GetSection("Frontend:AllowedOrigins").Get<string[]>()?.FirstOrDefault(origin => origin.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
            return string.IsNullOrWhiteSpace(origin) ? "https://devops.rosenvall.se" : origin.Trim();
        }

        private static string FirstNonEmpty(params string?[] values) =>
            values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";

        private static string SlugFromHtmlUrl(string htmlUrl)
        {
            var marker = "/apps/";
            var index = htmlUrl.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            return index >= 0 ? htmlUrl[(index + marker.Length)..].Trim('/') : "";
        }

        private static string SafeSlug(string value)
        {
            var safe = new string(value.ToLowerInvariant().Select(character => char.IsLetterOrDigit(character) ? character : '-').ToArray()).Trim('-');
            while (safe.Contains("--", StringComparison.Ordinal))
            {
                safe = safe.Replace("--", "-", StringComparison.Ordinal);
            }

            return safe;
        }
    }

    public sealed class OllamaPlanProvider(HttpClient httpClient, IConfiguration configuration) : IAiPlanProvider
    {
        public string ProviderName => "ollama";

        public Task<string> GeneratePlanAsync(string model, WorkItemDetailDto context, CancellationToken cancellationToken) =>
            GeneratePlanAsync(model, null, context, cancellationToken);

        public async Task<string> GeneratePlanAsync(string model, string? reasoningEffort, WorkItemDetailDto context, CancellationToken cancellationToken)
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

              Board and repository context:
              {{PromptContextRenderer.RenderPlanningContext(context)}}

              Required output:
              - A concrete plan.
              - Include tests.
              - For repository or GitOps boards, first apply board instructions, GitOps settings, repository profile signals, and enabled repo skill drafts to resolve path, namespace, routing, and validation conventions.
              - Return blocking questions only for facts that cannot be answered by that context, such as user preference, destructive intent, or missing business requirements.
              - For react-preview boards, target a Vite React TypeScript preview app with Tailwind CSS and shadcn-style components.
              - React previews should be containerized and exposed through the existing Kubernetes preview route.
              - Preserve every concrete visual/content requirement from the title, description, and comments.
              - If colors, language, exact text, layout, or behavior are specified, repeat them explicitly in the plan.
              """;

        private sealed record OllamaGenerateResponse([property: JsonPropertyName("response")] string? Response);
    }

    public sealed class CodexCliPlanProvider(IConfiguration configuration, ILogger<CodexCliPlanProvider> logger) : IAiPlanProvider
    {
        public string ProviderName => "codex";

        public Task<string> GeneratePlanAsync(string model, WorkItemDetailDto context, CancellationToken cancellationToken) =>
            GeneratePlanAsync(model, null, context, cancellationToken);

        public async Task<string> GeneratePlanAsync(string model, string? reasoningEffort, WorkItemDetailDto context, CancellationToken cancellationToken)
        {
            var codexPath = CodexExecutableResolver.Resolve(configuration["Ai:Codex:Path"] ?? "codex");
            var timeout = TimeSpan.FromSeconds(configuration.GetValue("Ai:Codex:RequestTimeoutSeconds", configuration.GetValue("Ai:RequestTimeoutSeconds", 120)));
            var outputPath = Path.Combine(Path.GetTempPath(), $"rosenvall-codex-plan-{Guid.NewGuid():N}.md");
            try
            {
                using var process = new Process
                {
                    StartInfo = BuildStartInfo(codexPath, model, reasoningEffort, outputPath),
                    EnableRaisingEvents = true
                };

                var started = process.Start();
                if (!started)
                {
                    throw new AiPlanProviderUnavailableException("Codex provider could not start; no plan was created.");
                }

                var stdOut = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var stdErr = process.StandardError.ReadToEndAsync(cancellationToken);
                try
                {
                    await process.StandardInput.WriteAsync(BuildPrompt(context).AsMemory(), cancellationToken);
                    process.StandardInput.Close();
                }
                catch (IOException ex)
                {
                    logger.LogDebug(ex, "Codex provider closed stdin before the prompt was fully written.");
                }
                catch (InvalidOperationException ex)
                {
                    logger.LogDebug(ex, "Codex provider stdin was unavailable before the prompt was written.");
                }

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

        private ProcessStartInfo BuildStartInfo(string codexPath, string model, string? reasoningEffort, string outputPath)
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
            startInfo.ArgumentList.Add("--skip-git-repo-check");
            startInfo.ArgumentList.Add("--sandbox");
            startInfo.ArgumentList.Add("read-only");
            startInfo.ArgumentList.Add("--output-last-message");
            startInfo.ArgumentList.Add(outputPath);
            startInfo.ArgumentList.Add("-m");
            startInfo.ArgumentList.Add(model);
            CodexCliArguments.AddReasoningEffort(startInfo, reasoningEffort);
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

              Board and repository context:
              {{PromptContextRenderer.RenderPlanningContext(context)}}

              Required output:
              - A concrete plan.
              - Include tests.
              - For repository or GitOps boards, first apply board instructions, GitOps settings, repository profile signals, and enabled repo skill drafts to resolve path, namespace, routing, and validation conventions.
              - Return blocking questions only for facts that cannot be answered by that context, such as user preference, destructive intent, or missing business requirements.
              - For react-preview boards, target a Vite React TypeScript preview app with Tailwind CSS and shadcn-style components.
              - React previews should be containerized and exposed through the existing Kubernetes preview route.
              - Preserve every concrete visual/content requirement from the title, description, and comments.
              - If colors, language, exact text, layout, or behavior are specified, repeat them explicitly in the plan.
              """;
    }

    public static class CodexCliArguments
    {
        public static void AddReasoningEffort(ProcessStartInfo startInfo, string? reasoningEffort)
        {
            var normalized = NormalizeReasoningEffort(reasoningEffort);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return;
            }

            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add($"model_reasoning_effort=\"{normalized}\"");
        }

        public static string? NormalizeReasoningEffort(string? reasoningEffort)
        {
            if (string.IsNullOrWhiteSpace(reasoningEffort))
            {
                return null;
            }

            var normalized = reasoningEffort.Trim().ToLowerInvariant();
            return normalized is "low" or "medium" or "high" or "xhigh" ? normalized : null;
        }
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
        Task<IReadOnlyList<PreviewSourceFile>> GenerateSourceAsync(string model, string? reasoningEffort, AiRun run, WorkItemDetailDto context, Func<PreviewTerminalLineDto, Task>? onTerminalLine, CancellationToken cancellationToken);
    }

    public sealed class CodexCliPreviewSourceProvider(IConfiguration configuration, ILogger<CodexCliPreviewSourceProvider> logger) : IPreviewSourceProvider
    {
        private static readonly string[] SkippedDirectories = ["node_modules", "dist", ".git", ".codex"];

        public Task<IReadOnlyList<PreviewSourceFile>> GenerateSourceAsync(string model, AiRun run, WorkItemDetailDto context, Func<PreviewTerminalLineDto, Task>? onTerminalLine, CancellationToken cancellationToken) =>
            GenerateSourceAsync(model, null, run, context, onTerminalLine, cancellationToken);

        public async Task<IReadOnlyList<PreviewSourceFile>> GenerateSourceAsync(string model, string? reasoningEffort, AiRun run, WorkItemDetailDto context, Func<PreviewTerminalLineDto, Task>? onTerminalLine, CancellationToken cancellationToken)
        {
            var codexPath = CodexExecutableResolver.Resolve(configuration["Ai:Codex:Path"] ?? "codex");
            var timeout = TimeSpan.FromSeconds(configuration.GetValue("Ai:Codex:ImplementationTimeoutSeconds", configuration.GetValue("Ai:Codex:RequestTimeoutSeconds", configuration.GetValue("Ai:RequestTimeoutSeconds", 180))));
            var workspacePath = Path.Combine(Path.GetTempPath(), $"rosenvall-preview-source-{Guid.NewGuid():N}");
            var outputPath = Path.Combine(Path.GetTempPath(), $"rosenvall-codex-preview-{Guid.NewGuid():N}.md");
            try
            {
                Directory.CreateDirectory(workspacePath);
                await ReportTerminalAsync(onTerminalLine, "system", $"Preparing preview workspace at {workspacePath}.");
                await SeedWorkspaceAsync(workspacePath, context, cancellationToken);
                await ReportTerminalAsync(onTerminalLine, "system", $"Starting Codex CLI with model {model}.");

                using var process = new Process
                {
                    StartInfo = BuildStartInfo(codexPath, model, reasoningEffort, workspacePath, outputPath),
                    EnableRaisingEvents = true
                };

                var started = process.Start();
                if (!started)
                {
                    throw new AiPlanProviderUnavailableException("Codex preview source provider could not start; preview source was not generated.");
                }

                await process.StandardInput.WriteAsync(BuildImplementationPrompt(run, context).AsMemory(), cancellationToken);
                process.StandardInput.Close();

                var outputBuilder = new StringBuilder();
                var errorBuilder = new StringBuilder();
                var stdOut = ReadProcessStreamAsync(process.StandardOutput, "stdout", outputBuilder, onTerminalLine, cancellationToken);
                var stdErr = ReadProcessStreamAsync(process.StandardError, "stderr", errorBuilder, onTerminalLine, cancellationToken);
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

                await Task.WhenAll(stdOut, stdErr);
                var output = outputBuilder.ToString();
                var error = errorBuilder.ToString();
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

                try
                {
                    PreviewSourcePolicy.Validate(sourceFiles);
                }
                catch (ArgumentException ex)
                {
                    throw new AiPlanProviderUnavailableException($"{ex.Message} No preview was deployed.");
                }

                var appSource = sourceFiles.First(file => string.Equals(file.Path, "src/App.tsx", StringComparison.OrdinalIgnoreCase)).Content;
                if (LooksLikeSeededPlaceholder(appSource))
                {
                    throw new AiPlanProviderUnavailableException("Codex preview source generation left the seeded placeholder app unchanged; no preview was deployed.");
                }

                await ReportTerminalAsync(onTerminalLine, "system", "Codex source generation finished.");
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

        private static async Task ReadProcessStreamAsync(TextReader reader, string stream, StringBuilder output, Func<PreviewTerminalLineDto, Task>? onTerminalLine, CancellationToken cancellationToken)
        {
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                output.AppendLine(line);
                await ReportTerminalAsync(onTerminalLine, stream, line);
            }
        }

        private static Task ReportTerminalAsync(Func<PreviewTerminalLineDto, Task>? onTerminalLine, string stream, string message)
        {
            var cleanMessage = StripAnsi(message).TrimEnd();
            if (string.IsNullOrWhiteSpace(cleanMessage))
            {
                return Task.CompletedTask;
            }

            return onTerminalLine?.Invoke(new PreviewTerminalLineDto(DateTimeOffset.UtcNow, stream, cleanMessage)) ?? Task.CompletedTask;
        }

        private static string StripAnsi(string value) =>
            System.Text.RegularExpressions.Regex.Replace(value, @"\x1B\[[0-?]*[ -/]*[@-~]", "");

        private ProcessStartInfo BuildStartInfo(string codexPath, string model, string? reasoningEffort, string workspacePath, string outputPath)
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
            if (configuration.GetValue("Ai:Codex:ImplementationBypassSandbox", false))
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
            CodexCliArguments.AddReasoningEffort(startInfo, reasoningEffort);
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

        private static bool LooksLikeSeededPlaceholder(string source) =>
            source.Contains("React, TypeScript and Tailwind are ready for this ticket slice.", StringComparison.OrdinalIgnoreCase) ||
            (source.Contains("\"Plan\"", StringComparison.Ordinal) &&
             source.Contains("\"Build\"", StringComparison.Ordinal) &&
             source.Contains("\"Preview\"", StringComparison.Ordinal));

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
              You must replace the seeded Plan/Build/Preview placeholder UI with a domain-specific product UI for the work item.
              Do not leave any card that says "React, TypeScript and Tailwind are ready for this ticket slice."
              The first viewport must demonstrate the requested product or tool, not project status.
              If the request needs save/export behavior, implement the best browser-only version using existing dependencies and Web APIs.
              If a requested feature cannot be fully implemented without new packages or backend services, still build a convincing interactive frontend approximation and clearly preserve the requested labels, fields, and flow.
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
              - `src/App.tsx` must contain a real implementation of the requested app, including meaningful state and interactions when the work item asks for a tool or workflow.
              - The UI copy should follow the language of the work item unless the plan explicitly says otherwise.
              - Do not add explanatory implementation notes to the rendered UI.
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
        private readonly List<ImplementationRunDto> _implementationRuns = [];
        private readonly List<RepositoryCleanupRunDto> _repositoryCleanupRuns = [];
        private readonly List<TimelineEventDto> _timelineEvents = [];
        private readonly List<UserDto> _users = [];
        private readonly List<TeamDto> _teams = [];
        private readonly List<BoardAccessDtoRecord> _boardAccess = [];
        private readonly List<BoardTeamAccessRecord> _boardTeamAccess = [];
        private readonly List<BoardRepositoryLinkRecord> _boardRepositoryLinks = [];
        private readonly List<BoardRepositoryProfileRecord> _boardRepositoryProfiles = [];
        private readonly List<GitHubIntegrationDto> _githubIntegrations = [];
        private readonly List<BoardSecretDto> _boardSecrets = [];
        private readonly List<AiSessionDto> _aiSessions = [];
        private readonly List<BoardGitOpsSettingsDto> _boardGitOpsSettings = [];
        private readonly List<BoardAiContextDto> _boardAiContexts = [];
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
            else
            {
                RecoverInterruptedPreviewImplementations();
            }
        }

        public IReadOnlyList<WorkspaceDto> GetWorkspaces(string? actorSubject = null)
        {
            lock (_lock)
            {
                var visibleBoardIds = string.IsNullOrWhiteSpace(actorSubject) ? null : VisibleBoardIdsWithoutLock(actorSubject);
                var workspaces = _workspaces.AsEnumerable();
                if (visibleBoardIds is not null)
                {
                    workspaces = workspaces.Where(workspace => _boards.Any(board => board.WorkspaceId == workspace.Id && visibleBoardIds.Contains(board.Id)));
                }

                var items = _items
                    .Where(item => visibleBoardIds is null || visibleBoardIds.Contains(item.BoardId))
                    .ToArray();
                var activeAi = items.Count(item => item.AiStatus is "Planning" or "ImplementationRunning");
                var openPrs = _development.Count(development =>
                    !string.IsNullOrWhiteSpace(development.Development.PullRequestUrl) &&
                    development.Development.PullRequestApprovedAt is null &&
                    _items.Any(item => item.Id == development.WorkItemId && (visibleBoardIds is null || visibleBoardIds.Contains(item.BoardId))));
                var completed = items.Count(item => string.Equals(item.AiStatus, "Completed", StringComparison.OrdinalIgnoreCase));
                return workspaces
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

        public WorkspaceDto CreateWorkspace(string name, string environmentName, string region, string? actorSubject = null)
        {
            lock (_lock)
            {
                var workspace = new WorkspaceDto(Guid.NewGuid(), name, environmentName, region, 0, 0, 0, 0);
                var board = new BoardRecord(Guid.NewGuid(), workspace.Id, "Delivery Board", ["Todo", "In Progress", "AI Planning", "Review", "Done"]);
                _workspaces.Add(workspace);
                _boards.Add(board);
                GrantActorBoardOwnerAccessWithoutLock(board.Id, actorSubject);
                Persist();
                return workspace;
            }
        }

        public UserDto GetOrCreateUser(UserIdentityRequest request)
        {
            lock (_lock)
            {
                var subject = NormalizeText(request.Subject, "local-dev");
                var existing = _users.SingleOrDefault(user => user.Subject.Equals(subject, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    return existing;
                }

                var email = NormalizeEmail(request.Email);
                var pendingIndex = _users.FindIndex(user =>
                    user.Subject.StartsWith("pending:", StringComparison.OrdinalIgnoreCase) &&
                    user.Email.Equals(email, StringComparison.OrdinalIgnoreCase));
                if (pendingIndex >= 0)
                {
                    var pending = _users[pendingIndex];
                    var linked = pending with
                    {
                        Subject = subject,
                        DisplayName = NormalizeText(request.DisplayName, request.Email),
                        Email = NormalizeText(request.Email, $"{subject}@local"),
                        AvatarUrl = string.IsNullOrWhiteSpace(request.AvatarUrl) ? pending.AvatarUrl : request.AvatarUrl.Trim()
                    };
                    _users[pendingIndex] = linked;
                    Persist();
                    return linked;
                }

                var user = new UserDto(
                    Guid.NewGuid(),
                    NormalizeText(request.DisplayName, request.Email),
                    NormalizeText(request.Email, $"{subject}@local"),
                    subject,
                    string.IsNullOrWhiteSpace(request.AvatarUrl) ? null : request.AvatarUrl.Trim());
                _users.Add(user);
                if (_teams.Count == 0)
                {
                    var team = new TeamDto(Guid.NewGuid(), "Rosenvall", [new TeamMemberDto(user.Id, "Owner")], DateTimeOffset.UtcNow);
                    _teams.Add(team);
                }

                Persist();
                return user;
            }
        }

        public IReadOnlyList<UserDto> GetUsers()
        {
            lock (_lock)
            {
                return _users.OrderBy(user => user.DisplayName).ToArray();
            }
        }

        public IReadOnlyList<TeamDto> GetTeams(string? actorSubject = null)
        {
            lock (_lock)
            {
                var teams = _teams.AsEnumerable();
                if (!string.IsNullOrWhiteSpace(actorSubject))
                {
                    teams = teams.Where(team => CanViewTeamWithoutLock(team.Id, actorSubject));
                }

                return teams.Select(EnrichTeam).OrderBy(team => team.Name).ToArray();
            }
        }

        public TeamDto CreateTeam(CreateTeamRequest request, string actorSubject)
        {
            lock (_lock)
            {
                var actor = _users.FirstOrDefault(user => user.Subject.Equals(actorSubject, StringComparison.OrdinalIgnoreCase)) ??
                    GetOrCreateUser(new UserIdentityRequest(actorSubject, actorSubject, $"{actorSubject}@local"));
                var team = new TeamDto(Guid.NewGuid(), NormalizeText(request.Name, "Team"), [new TeamMemberDto(actor.Id, "Owner")], DateTimeOffset.UtcNow);
                _teams.Add(team);
                Persist();
                return EnrichTeam(team);
            }
        }

        public TeamDto? UpsertTeamMember(Guid teamId, UpsertTeamMemberRequest request)
        {
            lock (_lock)
            {
                var index = _teams.FindIndex(team => team.Id == teamId);
                if (index < 0 || _users.All(user => user.Id != request.UserId))
                {
                    return null;
                }

                var team = _teams[index];
                var members = team.Members.Where(member => member.UserId != request.UserId).Append(new TeamMemberDto(request.UserId, NormalizeRole(request.Role))).ToArray();
                var updated = team with { Members = members };
                _teams[index] = updated;
                Persist();
                return EnrichTeam(updated);
            }
        }

        public TeamDto? InviteTeamMember(Guid teamId, InviteTeamMemberRequest request)
        {
            lock (_lock)
            {
                var email = NormalizeEmail(request.Email);
                if (string.IsNullOrWhiteSpace(email))
                {
                    return null;
                }

                var user = _users.SingleOrDefault(entry => entry.Email.Equals(email, StringComparison.OrdinalIgnoreCase));
                if (user is null)
                {
                    user = new UserDto(Guid.NewGuid(), email, email, $"pending:{email}");
                    _users.Add(user);
                }

                return UpsertTeamMember(teamId, new UpsertTeamMemberRequest(user.Id, request.Role));
            }
        }

        public bool CanViewTeam(Guid teamId, string actorSubject)
        {
            lock (_lock)
            {
                return CanViewTeamWithoutLock(teamId, actorSubject);
            }
        }

        public bool CanMutateTeam(Guid teamId, string actorSubject)
        {
            lock (_lock)
            {
                var user = _users.SingleOrDefault(entry => entry.Subject.Equals(actorSubject, StringComparison.OrdinalIgnoreCase));
                if (user is null)
                {
                    return !HasAnyTeamOrAccess();
                }

                var team = _teams.SingleOrDefault(entry => entry.Id == teamId);
                var member = team?.Members.SingleOrDefault(entry => entry.UserId == user.Id);
                return member is not null && NormalizeRole(member.Role) is "Owner" or "Admin";
            }
        }

        public bool CanMutateBoard(Guid boardId, string actorSubject)
        {
            lock (_lock)
            {
                return CanMutateBoardWithoutLock(boardId, actorSubject);
            }
        }

        public bool CanMutateWorkItem(Guid workItemId, string actorSubject)
        {
            lock (_lock)
            {
                var item = _items.SingleOrDefault(entry => entry.Id == workItemId);
                return item is not null && CanMutateBoard(item.BoardId, actorSubject);
            }
        }

        public bool CanMutateAiRun(Guid aiRunId, string actorSubject)
        {
            lock (_lock)
            {
                var run = _aiRuns.SingleOrDefault(entry => entry.Id == aiRunId);
                return run is not null && CanMutateWorkItem(run.WorkItemId, actorSubject);
            }
        }

        public bool CanMutateComment(Guid commentId, string actorSubject)
        {
            lock (_lock)
            {
                var comment = _comments.SingleOrDefault(entry => entry.Id == commentId);
                return comment is not null && CanMutateWorkItem(comment.WorkItemId, actorSubject);
            }
        }

        public bool CanViewBoard(Guid boardId, string actorSubject)
        {
            lock (_lock)
            {
                return CanViewBoardWithoutLock(boardId, actorSubject);
            }
        }

        public bool CanViewWorkItem(Guid workItemId, string actorSubject)
        {
            lock (_lock)
            {
                var item = _items.SingleOrDefault(entry => entry.Id == workItemId);
                return item is not null && CanViewBoardWithoutLock(item.BoardId, actorSubject);
            }
        }

        public bool CanViewImplementationRun(Guid implementationRunId, string actorSubject)
        {
            lock (_lock)
            {
                var run = _implementationRuns.SingleOrDefault(entry => entry.Id == implementationRunId);
                return run is not null && CanViewWorkItem(run.WorkItemId, actorSubject);
            }
        }

        public bool CanViewPipelineRun(Guid pipelineRunId, string actorSubject)
        {
            lock (_lock)
            {
                var run = _pipelineRuns.SingleOrDefault(entry => entry.Id == pipelineRunId);
                if (run is null)
                {
                    return false;
                }

                if (run.WorkItemId is { } workItemId)
                {
                    var item = _items.SingleOrDefault(entry => entry.Id == workItemId);
                    return item is not null && CanViewBoardWithoutLock(item.BoardId, actorSubject);
                }

                return VisibleRepositoryIdsWithoutLock(actorSubject).Contains(run.RepositoryId);
            }
        }

        public bool CanRecordPipelineRun(RecordPipelineRunRequest request, string actorSubject)
        {
            lock (_lock)
            {
                if (!TryResolvePipelineRunTargetWithoutLock(request.RepositoryId, request.BoardId, request.WorkItemId, out var boardId))
                {
                    return false;
                }

                return boardId is { } id
                    ? CanMutateBoardWithoutLock(id, actorSubject)
                    : CanMutateRepositoryOnlyPipelineWithoutLock(request.RepositoryId, actorSubject);
            }
        }

        public bool CanMutatePipelineRun(Guid pipelineRunId, string actorSubject)
        {
            lock (_lock)
            {
                var run = _pipelineRuns.SingleOrDefault(entry => entry.Id == pipelineRunId);
                if (run is null ||
                    !TryResolvePipelineRunTargetWithoutLock(run.RepositoryId, run.BoardId, run.WorkItemId, out var boardId))
                {
                    return false;
                }

                return boardId is { } id
                    ? CanMutateBoardWithoutLock(id, actorSubject)
                    : CanMutateRepositoryOnlyPipelineWithoutLock(run.RepositoryId, actorSubject);
            }
        }

        public bool CanCreateRepository(string actorSubject)
        {
            lock (_lock)
            {
                return CanCreateBoardScopedResourceWithoutLock(actorSubject);
            }
        }

        public bool CanCreateWorkspace(string actorSubject)
        {
            lock (_lock)
            {
                return CanCreateBoardScopedResourceWithoutLock(actorSubject);
            }
        }

        public IReadOnlyList<BoardTeamAccessDto> GetBoardTeamAccess(Guid boardId)
        {
            lock (_lock)
            {
                return BoardTeamAccessFor(boardId);
            }
        }

        public BoardTeamAccessDto? UpsertBoardTeamAccess(Guid boardId, Guid teamId, string role)
        {
            lock (_lock)
            {
                if (_boards.All(board => board.Id != boardId) || _teams.All(team => team.Id != teamId))
                {
                    return null;
                }

                UpsertBoardTeamAccessWithoutLock(boardId, teamId, role);
                Persist();
                return BoardTeamAccessFor(boardId).Single(access => access.TeamId == teamId);
            }
        }

        public bool RemoveBoardTeamAccess(Guid boardId, Guid teamId)
        {
            lock (_lock)
            {
                var removed = _boardTeamAccess.RemoveAll(access => access.BoardId == boardId && access.TeamId == teamId) > 0;
                if (removed)
                {
                    Persist();
                }

                return removed;
            }
        }

        public IReadOnlyList<RepositoryDto> GetRepositories(string? actorSubject = null)
        {
            lock (_lock)
            {
                var repositories = _repositories.AsEnumerable();
                if (!string.IsNullOrWhiteSpace(actorSubject))
                {
                    var visibleRepositoryIds = VisibleRepositoryIdsWithoutLock(actorSubject);
                    repositories = repositories.Where(repository => visibleRepositoryIds.Contains(repository.Id));
                }

                return repositories
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
                    DateTimeOffset.UtcNow,
                    string.IsNullOrWhiteSpace(request.Owner) ? OwnerFromRepositoryName(request.Name) : request.Owner.Trim(),
                    NormalizeImplementationProfile(request.ImplementationProfile));
                _repositories.Add(repository);
                Persist();
                return repository;
            }
        }

        public BoardDto? CreateBoard(Guid workspaceId, CreateBoardRequest request, string? actorSubject = null)
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
                if (repository is not null)
                {
                    var implementationProfile = NormalizeImplementationProfile(request.RepositoryProfile?.ImplementationProfile ?? request.ImplementationProfile ?? repository.ImplementationProfile);
                    UpsertBoardRepositoryLinkWithoutLock(board.Id, repository.Id, true, implementationProfile);
                    if (request.RepositoryProfile is not null)
                    {
                        UpsertBoardRepositoryProfileWithoutLock(board.Id, repository.Id, request.RepositoryProfile with { ImplementationProfile = implementationProfile });
                    }
                }
                var boardProfile = BoardImplementationProfile(board.Id);
                if (request.GitOpsSettings is not null || string.Equals(boardProfile, "gitops-homelab", StringComparison.OrdinalIgnoreCase))
                {
                    _boardGitOpsSettings.Add(CreateGitOpsSettings(board.Id, request.GitOpsSettings));
                }
                if (request.AiContext is not null || string.Equals(boardProfile, "gitops-homelab", StringComparison.OrdinalIgnoreCase))
                {
                    _boardAiContexts.Add(CreateAiContext(board.Id, request.AiContext, defaultAskWhenUncertain: string.Equals(boardProfile, "gitops-homelab", StringComparison.OrdinalIgnoreCase)));
                }
                var teamIds = request.TeamIds?.Where(teamId => _teams.Any(team => team.Id == teamId)).Distinct().ToArray() ?? [];
                if (teamIds.Length == 0 && !string.IsNullOrWhiteSpace(actorSubject))
                {
                    var actor = _users.FirstOrDefault(user => user.Subject.Equals(actorSubject, StringComparison.OrdinalIgnoreCase));
                    teamIds = actor is null
                        ? []
                        : _teams
                            .Where(team => team.Members.Any(member => member.UserId == actor.Id && IsMutatingRole(member.Role)))
                            .Select(team => team.Id)
                            .Take(1)
                            .ToArray();
                }
                foreach (var teamId in teamIds)
                {
                    UpsertBoardTeamAccessWithoutLock(board.Id, teamId, "Owner");
                }
                if (teamIds.Length == 0)
                {
                    GrantActorBoardOwnerAccessWithoutLock(board.Id, actorSubject);
                }
                AddTimelineEvent(board.Id, repository?.Id, null, "BoardCreated", board.Name, repository is null ? "Board created." : $"Board created for {repository.Name}.", "system", repository?.WebUrl);
                Persist();
                return ToBoardDto(board);
            }
        }

        public BoardDto? LinkRepositoryToBoard(Guid boardId, LinkBoardRepositoryRequest request)
        {
            lock (_lock)
            {
                var board = _boards.SingleOrDefault(entry => entry.Id == boardId);
                var repository = _repositories.SingleOrDefault(entry => entry.Id == request.RepositoryId);
                if (board is null || repository is null)
                {
                    return null;
                }

                UpsertBoardRepositoryLinkWithoutLock(boardId, repository.Id, request.IsPrimary, NormalizeImplementationProfile(request.ImplementationProfile ?? repository.ImplementationProfile));
                if (request.IsPrimary)
                {
                    board.RepositoryId = repository.Id;
                }

                AddTimelineEvent(board.Id, repository.Id, null, "RepositoryLinked", repository.Name, $"Linked {repository.Name} to board {board.Name}.", "system", repository.WebUrl);
                Persist();
                return ToBoardDto(board);
            }
        }

        public BoardDto? UpsertBoardRepositoryProfile(Guid boardId, Guid repositoryId, RepositoryProfileDto profile)
        {
            lock (_lock)
            {
                var board = _boards.SingleOrDefault(entry => entry.Id == boardId);
                var repository = _repositories.SingleOrDefault(entry => entry.Id == repositoryId);
                var link = _boardRepositoryLinks.SingleOrDefault(entry => entry.BoardId == boardId && entry.RepositoryId == repositoryId);
                if (board is null || repository is null || link is null)
                {
                    return null;
                }

                var normalized = RepositoryProfileAiParser.NormalizeRepositoryProfile(profile);
                UpsertBoardRepositoryLinkWithoutLock(boardId, repositoryId, link.IsPrimary, normalized.ImplementationProfile);
                UpsertBoardRepositoryProfileWithoutLock(boardId, repositoryId, normalized);
                _boardAiContexts.RemoveAll(existing => existing.BoardId == boardId);
                _boardAiContexts.Add(CreateAiContext(boardId, new BoardAiContextRequest(normalized.Instructions, normalized.EnabledSkills, true), defaultAskWhenUncertain: string.Equals(normalized.ImplementationProfile, "gitops-homelab", StringComparison.OrdinalIgnoreCase)));
                if (string.Equals(normalized.ImplementationProfile, "gitops-homelab", StringComparison.OrdinalIgnoreCase) && _boardGitOpsSettings.All(settings => settings.BoardId != boardId))
                {
                    _boardGitOpsSettings.Add(CreateGitOpsSettings(boardId, null));
                }

                Persist();
                return ToBoardDto(board);
            }
        }

        public bool UnlinkRepositoryFromBoard(Guid boardId, Guid repositoryId)
        {
            lock (_lock)
            {
                var board = _boards.SingleOrDefault(entry => entry.Id == boardId);
                if (board is null)
                {
                    return false;
                }

                _boardRepositoryLinks.RemoveAll(link => link.BoardId == boardId && link.RepositoryId == repositoryId);
                _boardRepositoryProfiles.RemoveAll(profile => profile.BoardId == boardId && profile.RepositoryId == repositoryId);
                if (board.RepositoryId == repositoryId)
                {
                    var nextPrimary = _boardRepositoryLinks.FirstOrDefault(link => link.BoardId == boardId);
                    board.RepositoryId = nextPrimary?.RepositoryId;
                    if (nextPrimary is not null)
                    {
                        UpsertBoardRepositoryLinkWithoutLock(boardId, nextPrimary.RepositoryId, true, nextPrimary.ImplementationProfile);
                    }
                }

                Persist();
                return true;
            }
        }

        public IReadOnlyList<BoardRepositoryDto> GetBoardRepositories(Guid boardId)
        {
            lock (_lock)
            {
                return BoardRepositoriesFor(boardId);
            }
        }

        public BoardGitOpsSettingsDto? GetBoardGitOpsSettings(Guid boardId)
        {
            lock (_lock)
            {
                return GitOpsSettingsFor(boardId);
            }
        }

        public BoardGitOpsSettingsDto? UpsertBoardGitOpsSettings(Guid boardId, BoardGitOpsSettingsRequest request)
        {
            lock (_lock)
            {
                if (_boards.All(board => board.Id != boardId))
                {
                    return null;
                }

                var settings = CreateGitOpsSettings(boardId, request);
                _boardGitOpsSettings.RemoveAll(existing => existing.BoardId == boardId);
                _boardGitOpsSettings.Add(settings);
                Persist();
                return settings;
            }
        }

        public BoardAiContextDto? GetBoardAiContext(Guid boardId)
        {
            lock (_lock)
            {
                return AiContextFor(boardId);
            }
        }

        public BoardAiContextDto? UpsertBoardAiContext(Guid boardId, BoardAiContextRequest request)
        {
            lock (_lock)
            {
                if (_boards.All(board => board.Id != boardId))
                {
                    return null;
                }

                var context = CreateAiContext(boardId, request);
                _boardAiContexts.RemoveAll(existing => existing.BoardId == boardId);
                _boardAiContexts.Add(context);
                Persist();
                return context;
            }
        }

        public IReadOnlyList<GitHubIntegrationDto> GetGitHubIntegrations(string? actorSubject = null)
        {
            lock (_lock)
            {
                return _githubIntegrations
                    .Where(integration => CanUseGitHubIntegrationWithoutLock(integration, actorSubject))
                    .OrderBy(integration => integration.AccountLogin)
                    .ToArray();
            }
        }

        public IReadOnlyList<GitHubIntegrationDto> UpsertGitHubIntegrations(IReadOnlyList<GitHubIntegrationCallbackRequest> requests)
        {
            lock (_lock)
            {
                foreach (var request in requests)
                {
                    _githubIntegrations.RemoveAll(integration => integration.InstallationId == request.InstallationId);
                    _githubIntegrations.Add(CreateGitHubIntegrationDto(request));
                }

                if (requests.Count > 0)
                {
                    Persist();
                }

                return _githubIntegrations.OrderBy(integration => integration.AccountLogin).ToArray();
            }
        }

        public long? GetDefaultGitHubInstallationId(string? actorSubject = null)
        {
            lock (_lock)
            {
                return _githubIntegrations
                    .Where(integration => CanUseGitHubIntegrationWithoutLock(integration, actorSubject))
                    .Where(integration => integration.Status.Equals("Installed", StringComparison.OrdinalIgnoreCase) ||
                        integration.Status.Equals("Active", StringComparison.OrdinalIgnoreCase) ||
                        integration.Status.Equals("install", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(integration => integration.CreatedAt)
                    .Select(integration => (long?)integration.InstallationId)
                    .FirstOrDefault() ??
                    _githubIntegrations
                        .Where(integration => CanUseGitHubIntegrationWithoutLock(integration, actorSubject))
                        .OrderByDescending(integration => integration.CreatedAt)
                        .Select(integration => (long?)integration.InstallationId)
                        .FirstOrDefault();
            }
        }

        public bool CanUseGitHubInstallation(long installationId, string? actorSubject)
        {
            lock (_lock)
            {
                var integration = _githubIntegrations.SingleOrDefault(entry => entry.InstallationId == installationId);
                return integration is not null && CanUseGitHubIntegrationWithoutLock(integration, actorSubject);
            }
        }

        public GitHubIntegrationDto? GetGitHubIntegrationForRepository(RepositoryDto repository)
        {
            lock (_lock)
            {
                if (!repository.Provider.Equals("GitHub", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                return _githubIntegrations
                    .Where(integration => repository.Owner is not null && integration.AccountLogin.Equals(repository.Owner, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(integration => integration.CreatedAt)
                    .FirstOrDefault() ??
                    _githubIntegrations
                        .OrderByDescending(integration => integration.CreatedAt)
                        .FirstOrDefault();
            }
        }

        public GitHubIntegrationDto? GetGitHubIntegration(long? installationId = null)
        {
            lock (_lock)
            {
                return installationId is { } id
                    ? _githubIntegrations.SingleOrDefault(integration => integration.InstallationId == id)
                    : _githubIntegrations
                        .OrderByDescending(integration => integration.CreatedAt)
                        .FirstOrDefault();
            }
        }

        public GitHubIntegrationDto CreateGitHubIntegration(GitHubIntegrationCallbackRequest request)
        {
            lock (_lock)
            {
                _githubIntegrations.RemoveAll(integration => integration.InstallationId == request.InstallationId);
                var integration = CreateGitHubIntegrationDto(request);
                _githubIntegrations.Add(integration);
                Persist();
                return integration;
            }
        }

        private static GitHubIntegrationDto CreateGitHubIntegrationDto(GitHubIntegrationCallbackRequest request) =>
            new(
                Guid.NewGuid(),
                request.InstallationId,
                NormalizeText(request.AccountLogin, $"installation-{request.InstallationId}"),
                NormalizeText(request.AccountType, "User"),
                NormalizeText(request.Status, "Installed"),
                Math.Max(0, request.RepositoriesCount),
                NormalizeText(request.InstalledBy, "system"),
                DateTimeOffset.UtcNow);

        public IReadOnlyList<BoardSecretDto> GetBoardSecrets(Guid boardId)
        {
            lock (_lock)
            {
                return _boardSecrets
                    .Where(secret => secret.BoardId == boardId)
                    .OrderBy(secret => secret.RepositoryId.HasValue)
                    .ThenBy(secret => secret.Key)
                    .ToArray();
            }
        }

        public BoardSecretDto? GetBoardSecret(Guid boardId, Guid secretId)
        {
            lock (_lock)
            {
                return _boardSecrets.SingleOrDefault(secret => secret.Id == secretId && secret.BoardId == boardId);
            }
        }

        public BoardSecretDto? CreateBoardSecret(Guid boardId, CreateBoardSecretRequest request)
        {
            lock (_lock)
            {
                if (_boards.All(board => board.Id != boardId) ||
                    request.RepositoryId is { } repositoryId && !BoardRepositoriesFor(boardId).Any(link => link.RepositoryId == repositoryId))
                {
                    return null;
                }

                var key = NormalizeSecretKey(request.Key);
                _boardSecrets.RemoveAll(secret => secret.BoardId == boardId && secret.RepositoryId == request.RepositoryId && secret.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
                var now = DateTimeOffset.UtcNow;
                var secret = new BoardSecretDto(Guid.NewGuid(), boardId, request.RepositoryId, key, now, now);
                _boardSecrets.Add(secret);
                Persist();
                return secret;
            }
        }

        public BoardSecretDto? UpdateBoardSecret(Guid boardId, Guid secretId)
        {
            lock (_lock)
            {
                var index = _boardSecrets.FindIndex(secret => secret.Id == secretId && secret.BoardId == boardId);
                if (index < 0)
                {
                    return null;
                }

                var updated = _boardSecrets[index] with { UpdatedAt = DateTimeOffset.UtcNow };
                _boardSecrets[index] = updated;
                Persist();
                return updated;
            }
        }

        public bool DeleteBoardSecret(Guid boardId, Guid secretId)
        {
            lock (_lock)
            {
                var removed = _boardSecrets.RemoveAll(secret => secret.Id == secretId && secret.BoardId == boardId) > 0;
                if (removed)
                {
                    Persist();
                }

                return removed;
            }
        }

        public string? RenderBoardSecretManifest(BoardSecretDto secret, string value, IConfiguration configuration)
        {
            lock (_lock)
            {
                return _boardSecrets.Any(entry => entry.Id == secret.Id)
                    ? BoardSecretManifestRenderer.Render(secret, value, configuration)
                    : null;
            }
        }

        public AiSessionDto? GetAiSession(Guid workItemId)
        {
            lock (_lock)
            {
                return _aiSessions.SingleOrDefault(session => session.WorkItemId == workItemId);
            }
        }

        public AiSessionDto? EnsureAiSession(Guid workItemId, string provider, string model, Guid? repositoryId = null, string? reasoningEffort = null)
        {
            lock (_lock)
            {
                var item = _items.SingleOrDefault(entry => entry.Id == workItemId);
                if (item is null)
                {
                    return null;
                }

                var now = DateTimeOffset.UtcNow;
                var existingIndex = _aiSessions.FindIndex(session => session.WorkItemId == workItemId);
                if (existingIndex >= 0)
                {
                    var existing = _aiSessions[existingIndex];
                    var updated = existing with
                    {
                        Provider = NormalizeText(provider, existing.Provider),
                        Model = NormalizeText(model, existing.Model),
                        RepositoryId = repositoryId ?? existing.RepositoryId,
                        LastPromptAt = now,
                        Status = "Active",
                        ContextSummary = BuildAiSessionContext(item),
                        ReasoningEffort = CodexCliArguments.NormalizeReasoningEffort(reasoningEffort) ?? existing.ReasoningEffort
                    };
                    _aiSessions[existingIndex] = updated;
                    Persist();
                    return updated;
                }

                var session = new AiSessionDto(Guid.NewGuid(), workItemId, NormalizeText(provider, "codex"), NormalizeText(model, "gpt-5.4"), null, "Active", now, repositoryId, null, BuildAiSessionContext(item), CodexCliArguments.NormalizeReasoningEffort(reasoningEffort));
                _aiSessions.Add(session);
                Persist();
                return session;
            }
        }

        public AiSessionDto? SetAiSessionProviderSession(Guid workItemId, string providerSessionId)
        {
            lock (_lock)
            {
                var index = _aiSessions.FindIndex(session => session.WorkItemId == workItemId);
                if (index < 0)
                {
                    return null;
                }

                var updated = _aiSessions[index] with { ProviderSessionId = NormalizeText(providerSessionId, ""), Status = "Active", LastPromptAt = DateTimeOffset.UtcNow };
                _aiSessions[index] = updated;
                Persist();
                return updated;
            }
        }

        public IReadOnlyList<BoardDto> GetBoards(Guid workspaceId, string? actorSubject = null)
        {
            lock (_lock)
            {
                var boards = _boards.Where(b => b.WorkspaceId == workspaceId);
                if (!string.IsNullOrWhiteSpace(actorSubject))
                {
                    boards = boards.Where(board => CanViewBoardWithoutLock(board.Id, actorSubject));
                }

                return boards.Select(ToBoardDto).ToArray();
            }
        }

        public BoardDto? GetBoard(Guid boardId)
        {
            lock (_lock)
            {
                return _boards.Where(b => b.Id == boardId).Select(ToBoardDto).SingleOrDefault();
            }
        }

        public IReadOnlyList<WorkItemSummaryDto> GetWorkItems(string? actorSubject = null)
        {
            lock (_lock)
            {
                var items = _items.AsEnumerable();
                if (!string.IsNullOrWhiteSpace(actorSubject))
                {
                    var visibleBoardIds = VisibleBoardIdsWithoutLock(actorSubject);
                    items = items.Where(item => visibleBoardIds.Contains(item.BoardId));
                }

                return items.Select(ToSummary).ToArray();
            }
        }

        public WorkItemSummaryDto? CreateWorkItem(CreateWorkItemRequest request)
        {
            lock (_lock)
            {
                var board = _boards.SingleOrDefault(board => board.Id == request.BoardId);
                if (board is null || !board.Columns.Contains(request.Status, StringComparer.OrdinalIgnoreCase))
                {
                    return null;
                }

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
                    if (!IsBoardStatus(item.BoardId, request.Status))
                    {
                        return null;
                    }

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

                if (!IsBoardStatus(item.BoardId, request.Status))
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
                _aiSessions.RemoveAll(session => session.WorkItemId == workItemId);
                _previews.RemoveAll(p => p.WorkItemId == workItemId);
                _development.RemoveAll(d => d.WorkItemId == workItemId);
                _implementationRuns.RemoveAll(run => run.WorkItemId == workItemId);
                _repositoryCleanupRuns.RemoveAll(run => run.WorkItemId == workItemId);
                _pipelineRuns.RemoveAll(run => run.WorkItemId == workItemId);
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
                    _development.SingleOrDefault(d => d.WorkItemId == item.Id)?.Development,
                    _implementationRuns.Where(run => run.WorkItemId == item.Id).OrderByDescending(run => run.CreatedAt).ToArray(),
                    _aiSessions.SingleOrDefault(session => session.WorkItemId == item.Id),
                    _previewEvents.Where(entry => entry.WorkItemId == item.Id).OrderBy(entry => entry.CreatedAt).ToArray(),
                    PreviewImplementationRunsAwaitingRecoveryForWorkItem(item.Id),
                    new BoardPlanningContextDto(item.BoardId, BoardImplementationProfile(item.BoardId), GitOpsSettingsFor(item.BoardId), AiContextFor(item.BoardId), PrimaryRepositoryProfileFor(item.BoardId)),
                    _repositoryCleanupRuns.Where(run => run.WorkItemId == item.Id).OrderByDescending(run => run.CreatedAt).ToArray());
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

        public IReadOnlyList<AiRun> GetPreviewImplementationRunsAwaitingRecovery()
        {
            lock (_lock)
            {
                return _previews
                    .Where(IsPreviewImplementationAwaitingRecovery)
                    .Select(preview => PreviewImplementationRunsAwaitingRecoveryForWorkItem(preview.WorkItemId).FirstOrDefault())
                    .Where(run => run is not null)
                    .Select(run => run!)
                    .OrderBy(run => run.CreatedAt)
                    .ToArray();
            }
        }

        public IReadOnlyList<PreviewEnvironmentDto> GetPreviewEnvironments(string? actorSubject = null)
        {
            lock (_lock)
            {
                var visibleBoardIds = string.IsNullOrWhiteSpace(actorSubject) ? null : VisibleBoardIdsWithoutLock(actorSubject);
                return _previews
                    .Where(preview => visibleBoardIds is null || _items.Any(item => item.Id == preview.WorkItemId && visibleBoardIds.Contains(item.BoardId)))
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

        public IReadOnlyList<PreviewEventDto> GetPreviewEvents(string? actorSubject = null)
        {
            lock (_lock)
            {
                var events = _previewEvents.AsEnumerable();
                if (!string.IsNullOrWhiteSpace(actorSubject))
                {
                    var visibleBoardIds = VisibleBoardIdsWithoutLock(actorSubject);
                    events = events.Where(entry => _items.Any(item => item.Id == entry.WorkItemId && visibleBoardIds.Contains(item.BoardId)));
                }

                return events.OrderByDescending(e => e.CreatedAt).Take(50).ToArray();
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
                if (!TryResolvePipelineRunTargetWithoutLock(request.RepositoryId, request.BoardId, request.WorkItemId, out var boardId))
                {
                    return null;
                }

                var now = DateTimeOffset.UtcNow;
                var run = new PipelineRunDto(
                    Guid.NewGuid(),
                    request.RepositoryId,
                    boardId,
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

        public ImplementationRunDto? StartImplementationRun(Guid workItemId, StartImplementationRunRequest request)
        {
            lock (_lock)
            {
                var item = _items.SingleOrDefault(entry => entry.Id == workItemId);
                var aiRun = _aiRuns.SingleOrDefault(run => run.Id == request.AiRunId && run.WorkItemId == workItemId);
                if (item is null || aiRun is null)
                {
                    return null;
                }

                var repositoryId = request.RepositoryId ?? RepositoryIdForBoard(item.BoardId);
                var repository = repositoryId is null ? null : _repositories.SingleOrDefault(entry => entry.Id == repositoryId.Value);
                if (repository is null ||
                    !BoardRepositoriesFor(item.BoardId).Any(link => link.RepositoryId == repository.Id) ||
                    string.Equals(repository.ImplementationProfile, "react-preview", StringComparison.OrdinalIgnoreCase) ||
                    !repository.Provider.Equals("GitHub", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                if (aiRun.Status != AiRunStatus.Approved)
                {
                    aiRun.Approve(NormalizeText(request.Actor, "system"));
                }

                var now = DateTimeOffset.UtcNow;
                var attemptNumber = _implementationRuns.Count(run => run.WorkItemId == item.Id && run.RepositoryId == repository.Id && run.AiRunId == aiRun.Id) + 1;
                var branchBase = $"rdo/{item.Key.ToLowerInvariant()}-{SlugifyRepositoryName(item.Title)}";
                var branch = attemptNumber == 1 ? branchBase : $"{branchBase}-retry-{attemptNumber}";
                var runDto = new ImplementationRunDto(
                    Guid.NewGuid(),
                    repository.Id,
                    item.Id,
                    aiRun.Id,
                    item.Key,
                    item.Title,
                    "Queued",
                    branch,
                    null,
                    null,
                    null,
                    now,
                    now,
                    [
                        new PreviewTerminalLineDto(now, "system", $"Queued repository implementation for {item.Key}."),
                        new PreviewTerminalLineDto(now, "system", $"Repository: {repository.Provider} / {repository.Owner}/{repository.Name}.")
                    ]);
                _implementationRuns.Add(runDto);
                EnsureAiSession(item.Id, aiRun.Provider, aiRun.Model, repository.Id, request.ReasoningEffort ?? aiRun.ReasoningEffort);
                item.AiStatus = "ImplementationRunning";
                item.Status = "AI Planning";
                var queueMessage = attemptNumber == 1
                    ? $"Repository implementation queued for {repository.Name}."
                    : $"Repository implementation attempt {attemptNumber} queued for {repository.Name}.";
                AddTimelineForItem(item, "ImplementationRunQueued", item.Key, queueMessage, request.Actor, repository.WebUrl);
                Persist();
                return runDto;
            }
        }

        public IReadOnlyList<ImplementationRunDto> GetImplementationRuns(Guid? workItemId = null, string? actorSubject = null)
        {
            lock (_lock)
            {
                var visibleBoardIds = string.IsNullOrWhiteSpace(actorSubject) ? null : VisibleBoardIdsWithoutLock(actorSubject);
                return _implementationRuns
                    .Where(run => (workItemId is null || run.WorkItemId == workItemId) &&
                        (visibleBoardIds is null || _items.Any(item => item.Id == run.WorkItemId && visibleBoardIds.Contains(item.BoardId))))
                    .OrderByDescending(run => run.CreatedAt)
                    .ToArray();
            }
        }

        public ImplementationRunDto? GetImplementationRun(Guid implementationRunId)
        {
            lock (_lock)
            {
                return _implementationRuns.SingleOrDefault(run => run.Id == implementationRunId);
            }
        }

        public IReadOnlyList<ImplementationRunDto> GetImplementationRunsAwaitingStatus()
        {
            lock (_lock)
            {
                return _implementationRuns
                    .Where(run => run.Status is "Queued" or "Cloning" or "Implementing" or "Testing" or "Pushing")
                    .ToArray();
            }
        }

        public ImplementationRunDto? UpdateImplementationRun(Guid implementationRunId, string status, string? logs = null, string? failureReason = null)
        {
            lock (_lock)
            {
                var index = _implementationRuns.FindIndex(run => run.Id == implementationRunId);
                if (index < 0)
                {
                    return null;
                }

                var existing = _implementationRuns[index];
                var lines = string.IsNullOrWhiteSpace(logs)
                    ? existing.TerminalLines ?? []
                    : logs.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Select(line => new PreviewTerminalLineDto(DateTimeOffset.UtcNow, "runner", RedactTerminalMessage(line.TrimEnd())))
                        .TakeLast(200)
                        .ToArray();
                var pullRequestUrl = FirstMarkerValue(logs, "RDO_PULL_REQUEST_URL=") ?? existing.PullRequestUrl;
                var commitSha = FirstMarkerValue(logs, "RDO_COMMIT=") ?? existing.CommitSha;
                var updated = existing with
                {
                    Status = NormalizeText(status, existing.Status),
                    UpdatedAt = DateTimeOffset.UtcNow,
                    PullRequestUrl = string.IsNullOrWhiteSpace(pullRequestUrl) ? existing.PullRequestUrl : pullRequestUrl,
                    CommitSha = string.IsNullOrWhiteSpace(commitSha) ? existing.CommitSha : commitSha,
                    FailureReason = string.IsNullOrWhiteSpace(failureReason) ? existing.FailureReason : failureReason,
                    TerminalLines = lines
                };
                _implementationRuns[index] = updated;

                if (updated.Status is "PullRequestReady" or "Failed")
                {
                    var item = _items.SingleOrDefault(entry => entry.Id == updated.WorkItemId);
                    if (item is not null)
                    {
                        item.AiStatus = updated.Status == "PullRequestReady" ? "Completed" : "Failed";
                        item.Status = updated.Status == "PullRequestReady" ? "Review" : item.Status;
                        if (!string.IsNullOrWhiteSpace(updated.PullRequestUrl))
                        {
                            item.PullRequestUrl = updated.PullRequestUrl;
                        }
                        if (updated.Status == "PullRequestReady")
                        {
                            var repository = _repositories.SingleOrDefault(entry => entry.Id == updated.RepositoryId);
                            _development.RemoveAll(entry => entry.WorkItemId == item.Id);
                            _development.Add(new DevelopmentDtoRecord(item.Id, new DevelopmentDto(
                                repository?.Name ?? updated.RepositoryId.ToString(),
                                updated.Branch,
                                updated.PullRequestUrl,
                                "Pull request ready")));
                        }
                        AddTimelineForItem(item, updated.Status == "PullRequestReady" ? "PullRequest" : "ImplementationFailed", item.Key, updated.Status == "PullRequestReady" ? $"Pull request ready for {item.Key}." : updated.FailureReason ?? "Implementation failed.", "runner", updated.PullRequestUrl);
                    }
                }

                Persist();
                return updated;
            }
        }

        public RepositoryCleanupRunDto? StartRepositoryCleanupRun(Guid workItemId, Guid sourceImplementationRunId, string actor, string sourcePullRequestState, string? sourcePullRequestDiff)
        {
            lock (_lock)
            {
                var item = _items.SingleOrDefault(entry => entry.Id == workItemId);
                var sourceRun = _implementationRuns.SingleOrDefault(run => run.Id == sourceImplementationRunId && run.WorkItemId == workItemId);
                if (item is null || sourceRun is null || string.IsNullOrWhiteSpace(sourceRun.PullRequestUrl))
                {
                    return null;
                }

                var pending = _repositoryCleanupRuns
                    .Where(run => run.WorkItemId == item.Id && run.SourceImplementationRunId == sourceRun.Id)
                    .OrderByDescending(run => run.CreatedAt)
                    .FirstOrDefault(run => IsRepositoryCleanupPendingStatus(run.Status));
                if (pending is not null)
                {
                    return pending;
                }

                var attemptNumber = _repositoryCleanupRuns.Count(run => run.WorkItemId == item.Id && run.SourceImplementationRunId == sourceRun.Id) + 1;
                var branchBase = $"rdo/{item.Key.ToLowerInvariant()}-{SlugifyRepositoryName(item.Title)}-cleanup";
                var branch = attemptNumber == 1 ? branchBase : $"{branchBase}-retry-{attemptNumber}";
                var now = DateTimeOffset.UtcNow;
                var runDto = new RepositoryCleanupRunDto(
                    Guid.NewGuid(),
                    sourceRun.RepositoryId,
                    item.Id,
                    sourceRun.Id,
                    item.Key,
                    item.Title,
                    "Queued",
                    branch,
                    sourceRun.PullRequestUrl,
                    null,
                    null,
                    null,
                    sourcePullRequestState,
                    sourcePullRequestDiff,
                    now,
                    now,
                    [
                        new PreviewTerminalLineDto(now, "system", $"Queued repository cleanup for {item.Key}."),
                        new PreviewTerminalLineDto(now, "system", $"Source pull request: {sourceRun.PullRequestUrl}.")
                    ]);
                _repositoryCleanupRuns.Add(runDto);
                item.AiStatus = "CleanupRunning";
                item.Status = "AI Planning";
                AddTimelineForItem(item, "RepositoryCleanupQueued", item.Key, attemptNumber == 1 ? "Repository cleanup queued." : $"Repository cleanup attempt {attemptNumber} queued.", actor, sourceRun.PullRequestUrl);
                Persist();
                return runDto;
            }
        }

        public RepositoryCleanupRunDto? AdoptRepositoryCleanupPullRequest(Guid workItemId, Guid sourceImplementationRunId, string actor, string cleanupPullRequestUrl, string? branch, string? state, bool merged)
        {
            lock (_lock)
            {
                var item = _items.SingleOrDefault(entry => entry.Id == workItemId);
                var sourceRun = _implementationRuns.SingleOrDefault(run => run.Id == sourceImplementationRunId && run.WorkItemId == workItemId);
                if (item is null || sourceRun is null || string.IsNullOrWhiteSpace(sourceRun.PullRequestUrl) || string.IsNullOrWhiteSpace(cleanupPullRequestUrl))
                {
                    return null;
                }

                var existing = _repositoryCleanupRuns
                    .FirstOrDefault(run => run.WorkItemId == workItemId &&
                        string.Equals(run.CleanupPullRequestUrl, cleanupPullRequestUrl.Trim(), StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    return existing;
                }

                var now = DateTimeOffset.UtcNow;
                var runDto = new RepositoryCleanupRunDto(
                    Guid.NewGuid(),
                    sourceRun.RepositoryId,
                    item.Id,
                    sourceRun.Id,
                    item.Key,
                    item.Title,
                    merged ? "Merged" : "PullRequestReady",
                    string.IsNullOrWhiteSpace(branch) ? $"adopted-cleanup-pr-{now:yyyyMMddHHmmss}" : branch.Trim(),
                    sourceRun.PullRequestUrl,
                    cleanupPullRequestUrl.Trim(),
                    null,
                    null,
                    string.IsNullOrWhiteSpace(state) ? "adopted" : state.Trim(),
                    null,
                    now,
                    now,
                    [
                        new PreviewTerminalLineDto(now, "system", $"Adopted cleanup pull request for {item.Key}."),
                        new PreviewTerminalLineDto(now, "system", $"Cleanup pull request: {cleanupPullRequestUrl.Trim()}.")
                    ],
                    Adopted: true,
                    MergedAt: merged ? now : null);
                _repositoryCleanupRuns.Add(runDto);
                item.AiStatus = merged ? "CleanupMerged" : "CleanupReady";
                item.Status = "Review";
                item.PullRequestUrl = cleanupPullRequestUrl.Trim();
                AddTimelineForItem(item, merged ? "RepositoryCleanupMerged" : "RepositoryCleanupPullRequest", item.Key, merged ? "Adopted cleanup pull request is merged." : "Adopted cleanup pull request is ready for review.", actor, cleanupPullRequestUrl.Trim());
                Persist();
                return runDto;
            }
        }

        public IReadOnlyList<RepositoryCleanupRunDto> GetRepositoryCleanupRuns(Guid? workItemId = null)
        {
            lock (_lock)
            {
                return _repositoryCleanupRuns
                    .Where(run => workItemId is null || run.WorkItemId == workItemId)
                    .OrderByDescending(run => run.CreatedAt)
                    .ToArray();
            }
        }

        public IReadOnlyList<RepositoryCleanupRunDto> GetRepositoryCleanupRunsAwaitingStatus()
        {
            lock (_lock)
            {
                return _repositoryCleanupRuns
                    .Where(run => run.Status is "Queued" or "Cloning" or "Implementing" or "Validating" or "Pushing")
                    .ToArray();
            }
        }

        public RepositoryCleanupRunDto? UpdateRepositoryCleanupRun(Guid cleanupRunId, string status, string? logs = null, string? failureReason = null)
        {
            lock (_lock)
            {
                var index = _repositoryCleanupRuns.FindIndex(run => run.Id == cleanupRunId);
                if (index < 0)
                {
                    return null;
                }

                var existing = _repositoryCleanupRuns[index];
                var lines = string.IsNullOrWhiteSpace(logs)
                    ? existing.TerminalLines ?? []
                    : logs.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Select(line => new PreviewTerminalLineDto(DateTimeOffset.UtcNow, "runner", RedactTerminalMessage(line.TrimEnd())))
                        .TakeLast(200)
                        .ToArray();
                var pullRequestUrl = FirstMarkerValue(logs, "RDO_CLEANUP_PULL_REQUEST_URL=") ?? existing.CleanupPullRequestUrl;
                var commitSha = FirstMarkerValue(logs, "RDO_COMMIT=") ?? existing.CommitSha;
                var updated = existing with
                {
                    Status = NormalizeText(status, existing.Status),
                    UpdatedAt = DateTimeOffset.UtcNow,
                    CleanupPullRequestUrl = string.IsNullOrWhiteSpace(pullRequestUrl) ? existing.CleanupPullRequestUrl : pullRequestUrl,
                    CommitSha = string.IsNullOrWhiteSpace(commitSha) ? existing.CommitSha : commitSha,
                    FailureReason = string.IsNullOrWhiteSpace(failureReason) ? existing.FailureReason : failureReason,
                    TerminalLines = lines
                };
                _repositoryCleanupRuns[index] = updated;

                if (updated.Status is "PullRequestReady" or "Failed")
                {
                    var item = _items.SingleOrDefault(entry => entry.Id == updated.WorkItemId);
                    if (item is not null)
                    {
                        item.AiStatus = updated.Status == "PullRequestReady" ? "CleanupReady" : "CleanupFailed";
                        item.Status = updated.Status == "PullRequestReady" ? "Review" : item.Status;
                        if (!string.IsNullOrWhiteSpace(updated.CleanupPullRequestUrl))
                        {
                            item.PullRequestUrl = updated.CleanupPullRequestUrl;
                        }

                        AddTimelineForItem(item, updated.Status == "PullRequestReady" ? "RepositoryCleanupPullRequest" : "RepositoryCleanupFailed", item.Key, updated.Status == "PullRequestReady" ? $"Cleanup pull request ready for {item.Key}." : updated.FailureReason ?? "Repository cleanup failed.", "runner", updated.CleanupPullRequestUrl);
                    }
                }

                Persist();
                return updated;
            }
        }

        public RepositoryCleanupRunDto? MarkRepositoryCleanupRunStuck(Guid cleanupRunId, string jobName, string? podName, string? condition, string? eventSummary)
        {
            lock (_lock)
            {
                var index = _repositoryCleanupRuns.FindIndex(run => run.Id == cleanupRunId);
                if (index < 0)
                {
                    return null;
                }

                var existing = _repositoryCleanupRuns[index];
                var message = "Cleanup job produced no runner output before timeout.";
                var now = DateTimeOffset.UtcNow;
                var lines = (existing.TerminalLines ?? [])
                    .Concat([
                        new PreviewTerminalLineDto(now, "system", message),
                        new PreviewTerminalLineDto(now, "system", $"Job: {jobName}. Pod: {podName ?? "unknown"}. Condition: {condition ?? "unknown"}."),
                        new PreviewTerminalLineDto(now, "system", $"Last event: {eventSummary ?? "No Kubernetes event summary was available."}")
                    ])
                    .TakeLast(200)
                    .ToArray();
                var updated = existing with
                {
                    Status = "Failed",
                    FailureReason = message,
                    UpdatedAt = now,
                    TerminalLines = lines,
                    JobName = jobName,
                    PodName = podName,
                    LastCondition = condition,
                    LastEventSummary = eventSummary
                };
                _repositoryCleanupRuns[index] = updated;

                var item = _items.SingleOrDefault(entry => entry.Id == updated.WorkItemId);
                if (item is not null)
                {
                    item.AiStatus = "CleanupFailed";
                    AddTimelineForItem(item, "RepositoryCleanupFailed", item.Key, message, "runner", updated.CleanupPullRequestUrl);
                }

                Persist();
                return updated;
            }
        }

        public RepositoryDto? GetImplementationRunRepository(Guid implementationRunId)
        {
            lock (_lock)
            {
                var run = _implementationRuns.SingleOrDefault(entry => entry.Id == implementationRunId);
                return run is null ? null : _repositories.SingleOrDefault(entry => entry.Id == run.RepositoryId);
            }
        }

        public RepositoryDto? GetRepositoryCleanupRunRepository(Guid cleanupRunId)
        {
            lock (_lock)
            {
                var run = _repositoryCleanupRuns.SingleOrDefault(entry => entry.Id == cleanupRunId);
                return run is null ? null : _repositories.SingleOrDefault(entry => entry.Id == run.RepositoryId);
            }
        }

        public string? RenderImplementationRunManifest(Guid implementationRunId, IConfiguration configuration, string? githubSecretName = null)
        {
            lock (_lock)
            {
                var run = _implementationRuns.SingleOrDefault(entry => entry.Id == implementationRunId);
                if (run is null)
                {
                    return null;
                }

                var repository = _repositories.SingleOrDefault(entry => entry.Id == run.RepositoryId);
                var aiRun = _aiRuns.SingleOrDefault(entry => entry.Id == run.AiRunId);
                var context = GetWorkItemDetail(run.WorkItemId);
                if (repository is null || aiRun is null || context is null)
                {
                    return null;
                }

                var aiSession = _aiSessions.SingleOrDefault(session => session.WorkItemId == run.WorkItemId);
                var model = aiRun.Model;
                var reasoningEffort = aiRun.ReasoningEffort ?? aiSession?.ReasoningEffort ?? configuration["Ai:Codex:ReasoningEffort"] ?? "high";
                var secret = string.IsNullOrWhiteSpace(githubSecretName)
                    ? configuration["GitHub:TokenSecretName"] ?? "rosenvall-devops-github"
                    : githubSecretName.Trim();
                var item = _items.SingleOrDefault(entry => entry.Id == run.WorkItemId);
                var boardSecrets = item is null
                    ? []
                    : _boardSecrets
                        .Where(boardSecret => boardSecret.BoardId == item.BoardId && (boardSecret.RepositoryId is null || boardSecret.RepositoryId == repository.Id))
                        .ToArray();
                var now = DateTimeOffset.UtcNow;
                for (var index = 0; index < boardSecrets.Length; index++)
                {
                    var secretIndex = _boardSecrets.FindIndex(entry => entry.Id == boardSecrets[index].Id);
                    if (secretIndex >= 0)
                    {
                        _boardSecrets[secretIndex] = _boardSecrets[secretIndex] with { LastUsedAt = now };
                        boardSecrets[index] = _boardSecrets[secretIndex];
                    }
                }

                Persist();
                return RepositoryImplementationJobManifestRenderer.Render(run, repository, aiRun, context, model, reasoningEffort, secret, aiSession, boardSecrets);
            }
        }

        public string? RenderRepositoryCleanupRunManifest(Guid cleanupRunId, IConfiguration configuration, string? githubSecretName = null)
        {
            lock (_lock)
            {
                var run = _repositoryCleanupRuns.SingleOrDefault(entry => entry.Id == cleanupRunId);
                if (run is null)
                {
                    return null;
                }

                var repository = _repositories.SingleOrDefault(entry => entry.Id == run.RepositoryId);
                var context = GetWorkItemDetail(run.WorkItemId);
                if (repository is null || context is null)
                {
                    return null;
                }

                var model = configuration["Ai:Codex:Model"] ?? "gpt-5.5";
                var reasoningEffort = configuration["Ai:Codex:ReasoningEffort"] ?? "high";
                var secret = string.IsNullOrWhiteSpace(githubSecretName)
                    ? configuration["GitHub:TokenSecretName"] ?? "rosenvall-devops-github"
                    : githubSecretName.Trim();
                return RepositoryCleanupJobManifestRenderer.Render(run, repository, context, model, reasoningEffort, secret);
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

        public MetricsDto GetMetrics(Guid? boardId = null, string? actorSubject = null)
        {
            lock (_lock)
            {
                var runs = _pipelineRuns.AsEnumerable();
                if (boardId is not null)
                {
                    runs = runs.Where(run => run.BoardId == boardId);
                }

                if (!string.IsNullOrWhiteSpace(actorSubject))
                {
                    var visibleBoardIds = VisibleBoardIdsWithoutLock(actorSubject);
                    var visibleRepositoryIds = VisibleRepositoryIdsWithoutLock(actorSubject);
                    runs = runs.Where(run =>
                        (run.BoardId is { } runBoardId && visibleBoardIds.Contains(runBoardId)) ||
                        (run.WorkItemId is { } workItemId && _items.Any(item => item.Id == workItemId && visibleBoardIds.Contains(item.BoardId))) ||
                        (run.BoardId is null && visibleRepositoryIds.Contains(run.RepositoryId)));
                }

                var scopedRuns = runs.ToArray();
                return new MetricsDto(
                    boardId,
                    scopedRuns.Sum(run => run.TokensUsed),
                    scopedRuns.Sum(run => run.CodeAdded),
                    scopedRuns.Sum(run => run.CodeDeleted),
                    scopedRuns.Length);
            }
        }

        public IReadOnlyList<AssigneeDto> GetAssignees(Guid? boardId, IConfiguration configuration, string? actorSubject = null)
        {
            lock (_lock)
            {
                var visibleBoardIds = string.IsNullOrWhiteSpace(actorSubject) ? null : VisibleBoardIdsWithoutLock(actorSubject);
                var assignees = new Dictionary<string, AssigneeDto>(StringComparer.OrdinalIgnoreCase);
                foreach (var configured in configuration.GetSection("Authentik:Users").Get<ConfiguredAuthentikUser[]>() ?? [])
                {
                    var displayName = NormalizeText(configured.DisplayName ?? configured.Name ?? configured.Username, configured.Email ?? "Authentik user");
                    var email = NormalizeText(configured.Email, displayName);
                    assignees[email] = new AssigneeDto(NormalizeText(configured.Id, email), displayName, email, "Authentik");
                }

                foreach (var assignee in _items
                    .Where(item =>
                        (boardId is null || item.BoardId == boardId) &&
                        (visibleBoardIds is null || visibleBoardIds.Contains(item.BoardId)))
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

        public IReadOnlyList<PipelineStatusDto> GetPipelineStatuses(string? actorSubject = null)
        {
            lock (_lock)
            {
                var visibleBoardIds = string.IsNullOrWhiteSpace(actorSubject) ? null : VisibleBoardIdsWithoutLock(actorSubject);
                var visibleRepositoryIds = string.IsNullOrWhiteSpace(actorSubject) ? null : VisibleRepositoryIdsWithoutLock(actorSubject);
                var pipelines = new List<PipelineStatusDto>();
                foreach (var item in _items.Where(item => visibleBoardIds is null || visibleBoardIds.Contains(item.BoardId)).OrderBy(i => i.Key))
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

                foreach (var run in _pipelineRuns.Where(run =>
                    visibleBoardIds is null ||
                    (run.WorkItemId is { } workItemId && _items.Any(item => item.Id == workItemId && visibleBoardIds.Contains(item.BoardId))) ||
                    (run.WorkItemId is null && visibleRepositoryIds is not null && visibleRepositoryIds.Contains(run.RepositoryId))).OrderByDescending(run => run.StartedAt))
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

        public AiRun? StartAiPlan(Guid workItemId, string provider, string model, string? plan = null, string? reasoningEffort = null)
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
                var normalizedReasoningEffort = CodexCliArguments.NormalizeReasoningEffort(reasoningEffort);
                var session = EnsureAiSession(workItemId, provider, model, RepositoryIdForBoard(item.BoardId), normalizedReasoningEffort);
                var sequenceNumber = _aiRuns
                    .Where(existing => existing.WorkItemId == workItemId)
                    .Select(existing => existing.SequenceNumber)
                    .DefaultIfEmpty(0)
                    .Max() + 1;
                var run = AiRun.Start(workItemId, provider, model, sequenceNumber, DateTimeOffset.UtcNow, normalizedReasoningEffort);
                var planBody = string.IsNullOrWhiteSpace(plan) ? BuildPlan(item) : plan;
                if (IsNeedsInputPlan(planBody))
                {
                    run.PostQuestions(planBody);
                }
                else
                {
                    run.PostPlan(planBody);
                }
                _aiRuns.Add(run);
                _comments.Add(new CommentDto(Guid.NewGuid(), workItemId, "Rosenvall AI", "Result", run.Status == AiRunStatus.NeedsInput ? $"AI needs input for plan #{run.SequenceNumber}: {item.Title}." : $"Created plan #{run.SequenceNumber}: {item.Title}.", run.CreatedAt));
                item.AiStatus = run.Status == AiRunStatus.NeedsInput ? "NeedsInput" : "PlanReady";
                if (session is not null)
                {
                    var sessionIndex = _aiSessions.FindIndex(entry => entry.Id == session.Id);
                    if (sessionIndex >= 0)
                    {
                        _aiSessions[sessionIndex] = _aiSessions[sessionIndex] with { LastRunId = run.Id, LastPromptAt = run.CreatedAt, Status = run.Status.ToString() };
                    }
                }
                AddTimelineForItem(item, run.Status == AiRunStatus.NeedsInput ? "AiNeedsInput" : "AiPlanReady", item.Key, run.Status == AiRunStatus.NeedsInput ? $"AI needs input for {item.Title}." : $"AI plan ready for {item.Title}.", "Rosenvall AI");
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
                else if (run.Status == AiRunStatus.NeedsInput)
                {
                    throw new InvalidOperationException("This AI plan needs input and cannot be implemented until a revised plan is generated.");
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

        public PreviewDto? BeginPreviewImplementation(Guid workItemId, string implementationProvider)
        {
            lock (_lock)
            {
                var item = _items.SingleOrDefault(i => i.Id == workItemId);
                if (item is null)
                {
                    return null;
                }

                var resources = PreviewResourceSet.Create(item.Key, item.Title, LocalReactPreviewProject.Image);
                var providerName = NormalizeText(implementationProvider, "codex");
                var now = DateTimeOffset.UtcNow;
                var preview = new PreviewDto(
                    Guid.NewGuid(),
                    item.Id,
                    $"https://{resources.Hostname}",
                    resources.Image,
                    "Implementing",
                    now.AddDays(7),
                    null,
                    resources.Namespace,
                    resources.Name,
                    "Implementing preview source",
                    $"{providerName} is generating React/Tailwind preview source from the approved plan.",
                    TerminalLines:
                    [
                        new PreviewTerminalLineDto(now, "system", $"Queued preview implementation for {item.Key}."),
                        new PreviewTerminalLineDto(now, "system", $"Provider: {providerName}.")
                    ]);
                _previews.RemoveAll(p => p.WorkItemId == item.Id);
                _previews.Add(preview);
                AddPreviewEvent(item, preview, "Implementing", providerName, $"Preview implementation started for {item.Key}.");
                Persist();
                return preview;
            }
        }

        public WorkItemDetailDto? AppendPreviewTerminalLine(Guid workItemId, string stream, string message, DateTimeOffset? createdAt = null)
        {
            lock (_lock)
            {
                var preview = _previews.SingleOrDefault(p => p.WorkItemId == workItemId);
                if (preview is null || string.IsNullOrWhiteSpace(message))
                {
                    return null;
                }

                var lines = (preview.TerminalLines ?? [])
                    .Concat([new PreviewTerminalLineDto(createdAt ?? DateTimeOffset.UtcNow, NormalizeText(stream, "system"), RedactTerminalMessage(message.Trim()))])
                    .TakeLast(200)
                    .ToArray();
                _previews.Remove(preview);
                _previews.Add(preview with { TerminalLines = lines });
                Persist();
                return GetWorkItemDetail(workItemId);
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

                var existingPreview = _previews.SingleOrDefault(p => p.WorkItemId == item.Id);
                var preview = new PreviewDto(Guid.NewGuid(), item.Id, $"https://{resources.Hostname}", resources.Image, "Implementing", DateTimeOffset.UtcNow.AddDays(7), null, resources.Namespace, resources.Name, "Implementing preview source", "Generating local React/Tailwind source from the card context.", SourceFiles: sourceFiles, TerminalLines: existingPreview?.TerminalLines);
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

                PreviewSourcePolicy.Validate(sourceFiles);

                var resources = PreviewResourceSet.Create(item.Key, item.Title, LocalReactPreviewProject.Image, sourceFiles: sourceFiles);
                item.PullRequestUrl = null;
                item.AiStatus = "Completed";
                item.Status = "Review";

                var providerName = NormalizeText(implementationProvider, "codex");
                var existingPreview = _previews.SingleOrDefault(p => p.WorkItemId == item.Id);
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
                    SourceFiles: sourceFiles,
                    TerminalLines: existingPreview?.TerminalLines);
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
                var preview = _previews.SingleOrDefault(p => p.WorkItemId == workItemId);
                if (preview is not null)
                {
                    var failedLines = (preview.TerminalLines ?? [])
                        .Concat([new PreviewTerminalLineDto(DateTimeOffset.UtcNow, "stderr", message)])
                        .TakeLast(200)
                        .ToArray();
                    _previews.Remove(preview);
                    _previews.Add(preview with
                    {
                        Status = "Failed",
                        Phase = "Failed",
                        Message = message,
                        LastCheckedAt = DateTimeOffset.UtcNow,
                        FailureReason = "ImplementationFailed",
                        FailureLog = message,
                        TerminalLines = failedLines
                    });
                    AddPreviewEvent(item, preview, "ImplementationFailed", actor, message);
                }
                _comments.Add(new CommentDto(Guid.NewGuid(), item.Id, "Rosenvall AI", "Result", $"Preview source implementation failed: {message}", DateTimeOffset.UtcNow));
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

        public WorkItemDetailDto? MarkPreviewApplying(Guid workItemId, string message)
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
                var applying = preview with
                {
                    Status = "Applying",
                    Phase = "Applying Kubernetes resources",
                    Message = NormalizeText(message, "Applying Kubernetes resources."),
                    LastCheckedAt = DateTimeOffset.UtcNow
                };
                _previews.Add(applying);
                AddPreviewEvent(item, applying, "Applying", "system", applying.Message ?? "Applying Kubernetes resources.");
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
                         string.Equals(preview.Status, "Running", StringComparison.OrdinalIgnoreCase) ||
                         (string.Equals(preview.Status, "Failed", StringComparison.OrdinalIgnoreCase) &&
                          string.Equals(preview.FailureReason, "Timeout", StringComparison.OrdinalIgnoreCase) &&
                          !string.IsNullOrWhiteSpace(preview.Namespace) &&
                          !string.IsNullOrWhiteSpace(preview.ResourceName))))
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
                var terminalLines = (preview.TerminalLines ?? [])
                    .Concat([new PreviewTerminalLineDto(DateTimeOffset.UtcNow, "stderr", message)])
                    .TakeLast(200)
                    .ToArray();
                var failed = preview with
                {
                    Status = "Failed",
                    Phase = "Failed",
                    Message = message,
                    LastCheckedAt = DateTimeOffset.UtcNow,
                    FailureReason = eventType,
                    FailureLog = null,
                    TerminalLines = terminalLines
                };
                _previews.Add(failed);
                AddPreviewEvent(item, failed, eventType, actor, message);
                Persist();
            }
        }

        public SettingsDto GetSettings(IConfiguration configuration, string? actorSubject = null)
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
                .Concat(["gpt-5.5", "gpt-5.4", "gpt-5.4-mini", "gpt-5.3-codex", "gpt-5.3-codex-spark", "gpt-5.2", "codex-auto-review"])
                .Concat(configuration.GetSection("Ai:Codex:AvailableModels").Get<string[]>() ?? [])
                .Where(model => !string.IsNullOrWhiteSpace(model))
                .Select(model => model.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var codexReasoningEffort = CodexCliArguments.NormalizeReasoningEffort(configuration["Ai:Codex:ReasoningEffort"]) ?? "high";
            var codexReasoningEfforts = new[] { "low", "medium", "high", "xhigh" };
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
                    availableModels,
                    [],
                    null),
                new AiProviderSettingsDto(
                    "codex",
                    "Codex",
                    codexStatus,
                    codexPath,
                    codexActiveModel,
                    codexModels,
                    codexReasoningEfforts,
                    codexReasoningEffort)
            };

            var githubIntegration = _githubIntegrations
                .Where(integration => CanUseGitHubIntegrationWithoutLock(integration, actorSubject))
                .OrderByDescending(integration => integration.CreatedAt)
                .FirstOrDefault();
            var githubAccount = githubIntegration is null
                ? configuration["GitHub:Account"] ?? "No GitHub App installation"
                : $"{githubIntegration.AccountLogin} ({githubIntegration.AccountType})";
            var githubTarget = githubIntegration is null
                ? configuration["GitHub:TargetRepository"] ?? "Install the GitHub App to list repositories"
                : $"{githubIntegration.RepositoriesCount} repositories granted";
            var githubAppConfigured = !string.IsNullOrWhiteSpace(configuration["GitHub:AppId"]) &&
                !string.IsNullOrWhiteSpace(configuration["GitHub:AppPrivateKey"]);
            var githubConnected = githubIntegration is not null || !string.IsNullOrWhiteSpace(configuration["GitHub:Token"]);
            var githubInstallUrl = githubAppConfigured
                ? GitHubRepositoryClient.BuildInstallUrl(configuration["GitHub:AppSlug"] ?? configuration["GitHub:AppName"] ?? "rosenvall-devops")
                : "/integrations/github/manifest/start";

            return new(
                new GitHubSettingsDto(githubAccount, githubTarget, "GitHub App installation permissions", githubConnected, githubAppConfigured, githubInstallUrl, githubAppConfigured),
                new AiSettingsDto(
                    configuration["Ai:DefaultProvider"] ?? "ollama",
                    configuration["Ai:OllamaEndpoint"] ?? configuration["Ai:Ollama:Endpoint"] ?? "http://localhost:11434/api",
                    activeModel,
                    availableModels,
                    true,
                    providers),
                new PreviewSettingsDto("rosenvall.se", 7, "per-preview namespace"),
                new RepositoryHostingSettingsDto(
                    githubAppConfigured ? "GitHub" : configuration["Repositories:Provider"] ?? "Forgejo",
                    githubAppConfigured ? "GitHubApp" : configuration["Repositories:Mode"] ?? "LinkExistingFirst",
                    githubAppConfigured ? "https://api.github.com" : configuration["Repositories:Forgejo:ApiBaseUrl"] ?? "https://git.rosenvall.se/api/v1",
                    githubAppConfigured || configuration.GetValue("Repositories:Forgejo:CanCreateRepositories", false)),
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

                var hasStoredSourceFiles = preview.SourceFiles is { Count: > 0 };
                var isLocalReactPreview = string.Equals(preview.Image, LocalReactPreviewProject.Image, StringComparison.OrdinalIgnoreCase);
                if (!hasStoredSourceFiles &&
                    isLocalReactPreview &&
                    preview.StaticHtml is null &&
                    RequiresGeneratedPreviewSource(preview))
                {
                    return null;
                }

                var sourceFiles = hasStoredSourceFiles
                    ? preview.SourceFiles!
                    : isLocalReactPreview
                        ? LocalReactPreviewProject.ForWorkItem(
                            item.Key,
                            item.Title,
                            item.Description,
                            _comments
                                .Where(comment => comment.WorkItemId == item.Id && !string.Equals(comment.Author, "Rosenvall AI", StringComparison.OrdinalIgnoreCase))
                                .OrderBy(comment => comment.CreatedAt)
                                .Select(comment => comment.Body))
                        : Array.Empty<PreviewSourceFile>();
                if (sourceFiles.Count > 0)
                {
                    try
                    {
                        PreviewSourcePolicy.Validate(sourceFiles);
                    }
                    catch (ArgumentException)
                    {
                        return null;
                    }
                }

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

        private static bool RequiresGeneratedPreviewSource(PreviewDto preview) =>
            string.Equals(preview.Status, "Implementing", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(preview.Status, "Applying", StringComparison.OrdinalIgnoreCase) ||
            (string.Equals(preview.Status, "Failed", StringComparison.OrdinalIgnoreCase) &&
             (string.Equals(preview.FailureReason, "ImplementationFailed", StringComparison.OrdinalIgnoreCase) ||
              string.Equals(preview.FailureReason, "ServerRestart", StringComparison.OrdinalIgnoreCase) ||
              string.Equals(preview.FailureReason, "ManifestMissing", StringComparison.OrdinalIgnoreCase)));

        public string? RenderWorkItemCleanupManifest(Guid workItemId)
        {
            lock (_lock)
            {
                var item = _items.SingleOrDefault(i => i.Id == workItemId);
                if (item is null)
                {
                    return null;
                }

                var documents = new List<string>();
                var previewManifest = RenderPreviewManifest(workItemId);
                if (!string.IsNullOrWhiteSpace(previewManifest))
                {
                    documents.Add(previewManifest);
                }

                documents.AddRange(RenderImplementationRunCleanupDocuments(item, _implementationRuns.Where(run => run.WorkItemId == workItemId).OrderBy(run => run.CreatedAt)));
                documents.AddRange(RenderRepositoryCleanupRunCleanupDocuments(item, _repositoryCleanupRuns.Where(run => run.WorkItemId == workItemId).OrderBy(run => run.CreatedAt)));

                foreach (var run in _pipelineRuns.Where(run => run.WorkItemId == workItemId).OrderBy(run => run.StartedAt))
                {
                    var repository = _repositories.SingleOrDefault(repository => repository.Id == run.RepositoryId);
                    if (repository is not null)
                    {
                        documents.Add(RenderDeleteStub("batch/v1", "Job", PipelineJobManifestRenderer.JobName(run, repository), PipelineJobManifestRenderer.Namespace));
                    }
                }

                return documents.Count == 0 ? null : string.Join("\n---\n", documents);
            }
        }

        public string? RenderPreviousImplementationRunCleanupManifest(Guid workItemId, Guid currentImplementationRunId)
        {
            lock (_lock)
            {
                var item = _items.SingleOrDefault(i => i.Id == workItemId);
                var currentRun = _implementationRuns.SingleOrDefault(run => run.Id == currentImplementationRunId && run.WorkItemId == workItemId);
                if (item is null || currentRun is null)
                {
                    return null;
                }

                var documents = RenderImplementationRunCleanupDocuments(
                    item,
                    _implementationRuns
                        .Where(run => run.WorkItemId == workItemId && run.CreatedAt < currentRun.CreatedAt)
                        .OrderBy(run => run.CreatedAt));
                return documents.Count == 0 ? null : string.Join("\n---\n", documents);
            }
        }

        private IReadOnlyList<string> RenderImplementationRunCleanupDocuments(WorkItemRecord item, IEnumerable<ImplementationRunDto> runs)
        {
            var documents = new List<string>();
            var cleanupContext = ToWorkItemDetailForCleanup(item);
            foreach (var run in runs)
            {
                var jobName = RepositoryImplementationJobManifestRenderer.JobName(run, cleanupContext);
                var tokenSecretName = RepositoryImplementationJobManifestRenderer.GitHubTokenSecretName(run);
                documents.Add(RenderDeleteStub("batch/v1", "Job", jobName, RepositoryImplementationJobManifestRenderer.Namespace));
                documents.Add(RenderDeleteStub("v1", "Secret", tokenSecretName, RepositoryImplementationJobManifestRenderer.Namespace));
                documents.Add(RenderDeleteStub("batch/v1", "Job", jobName, PipelineJobManifestRenderer.Namespace));
                documents.Add(RenderDeleteStub("v1", "Secret", tokenSecretName, PipelineJobManifestRenderer.Namespace));
            }

            return documents;
        }

        private IReadOnlyList<string> RenderRepositoryCleanupRunCleanupDocuments(WorkItemRecord item, IEnumerable<RepositoryCleanupRunDto> runs)
        {
            var documents = new List<string>();
            var cleanupContext = ToWorkItemDetailForCleanup(item);
            foreach (var run in runs)
            {
                var jobName = RepositoryCleanupJobManifestRenderer.JobName(run, cleanupContext);
                var tokenSecretName = RepositoryCleanupJobManifestRenderer.GitHubTokenSecretName(run);
                documents.Add(RenderDeleteStub("batch/v1", "Job", jobName, RepositoryCleanupJobManifestRenderer.Namespace));
                documents.Add(RenderDeleteStub("v1", "Secret", tokenSecretName, RepositoryCleanupJobManifestRenderer.Namespace));
            }

            return documents;
        }

        private WorkItemDetailDto ToWorkItemDetailForCleanup(WorkItemRecord item) =>
            new(
                ToSummary(item),
                item.Description,
                [],
                null,
                null);

        private static string RenderDeleteStub(string apiVersion, string kind, string name, string @namespace) =>
            $$"""
              apiVersion: {{apiVersion}}
              kind: {{kind}}
              metadata:
                name: {{name}}
                namespace: {{@namespace}}
              """;

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
                RepositoryIdForBoard(board.Id) is { } repositoryId ? _repositories.SingleOrDefault(repository => repository.Id == repositoryId) : null,
                BoardRepositoriesFor(board.Id),
                BoardTeamAccessFor(board.Id),
                GitOpsSettingsFor(board.Id),
                AiContextFor(board.Id),
                BoardRepositorySyncState(board.Id),
                BoardProviderCapabilities(board.Id));

        private string BoardRepositorySyncState(Guid boardId)
        {
            var repository = RepositoryIdForBoard(boardId) is { } repositoryId
                ? _repositories.SingleOrDefault(entry => entry.Id == repositoryId)
                : null;
            if (repository is null)
            {
                return "Preview only";
            }

            if (repository.Provider.Equals("Sample", StringComparison.OrdinalIgnoreCase))
            {
                return "Demo board";
            }

            return string.Equals(BoardImplementationProfile(boardId), "gitops-homelab", StringComparison.OrdinalIgnoreCase)
                ? "GitOps board"
                : repository.Provider.Equals("GitHub", StringComparison.OrdinalIgnoreCase)
                    ? "Synced to GitHub"
                    : "Synced to provider";
        }

        private IReadOnlyList<string> BoardProviderCapabilities(Guid boardId)
        {
            var capabilities = new List<string> { "preview" };
            if (RepositoryIdForBoard(boardId) is null)
            {
                capabilities.Add("sync-github");
                return capabilities;
            }

            var repository = _repositories.SingleOrDefault(entry => entry.Id == RepositoryIdForBoard(boardId));
            if (repository?.Provider.Equals("Sample", StringComparison.OrdinalIgnoreCase) == true)
            {
                capabilities.Add("demo");
                return capabilities;
            }

            capabilities.Add("repository-implementation");
            if (string.Equals(BoardImplementationProfile(boardId), "gitops-homelab", StringComparison.OrdinalIgnoreCase))
            {
                capabilities.Add("gitops");
            }

            return capabilities;
        }

        private TeamDto EnrichTeam(TeamDto team) =>
            team with
            {
                Members = team.Members
                    .Select(member =>
                    {
                        var user = _users.SingleOrDefault(entry => entry.Id == member.UserId);
                        return member with
                        {
                            DisplayName = user?.DisplayName,
                            Email = user?.Email,
                            Status = user?.Subject.StartsWith("pending:", StringComparison.OrdinalIgnoreCase) == true ? "Pending" : "Active"
                        };
                    })
                    .OrderBy(member => member.DisplayName ?? member.Email ?? member.UserId.ToString())
                    .ToArray()
            };

        private IReadOnlyList<BoardTeamAccessDto> BoardTeamAccessFor(Guid boardId) =>
            _boardTeamAccess
                .Where(access => access.BoardId == boardId)
                .Select(access => (access, team: _teams.SingleOrDefault(team => team.Id == access.TeamId)))
                .Where(entry => entry.team is not null)
                .Select(entry => new BoardTeamAccessDto(entry.access.BoardId, entry.access.TeamId, entry.team!.Name, entry.access.Role))
                .OrderBy(access => access.TeamName)
                .ToArray();

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

            var remoteUrl = !string.IsNullOrWhiteSpace(request.CustomRepositoryUrl)
                ? request.CustomRepositoryUrl
                : request.RepositoryRemoteUrl;
            var provider = string.Equals(request.ProviderMode, "CustomUrl", StringComparison.OrdinalIgnoreCase)
                ? "GenericGit"
                : NormalizeText(request.RepositoryProvider, "Forgejo");
            var name = NormalizeText(request.RepositoryName, NormalizeText(request.Name, "repository"));
            var owner = string.IsNullOrWhiteSpace(request.RepositoryOwner) ? OwnerFromRepositoryName(name) : request.RepositoryOwner.Trim();
            if (string.IsNullOrWhiteSpace(remoteUrl) && string.IsNullOrWhiteSpace(request.RepositoryName))
            {
                return null;
            }

            var existing = _repositories.FirstOrDefault(repository =>
                repository.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase) &&
                repository.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(repository.Owner ?? "", owner ?? "", StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                return existing;
            }

            var repository = new RepositoryDto(
                Guid.NewGuid(),
                provider,
                name,
                NormalizeText(remoteUrl, $"ssh://git.rosenvall.se/{SlugifyRepositoryName(request.RepositoryName ?? request.Name)}.git"),
                string.IsNullOrWhiteSpace(request.RepositoryWebUrl) ? null : request.RepositoryWebUrl.Trim(),
                NormalizeText(request.RepositoryDefaultBranch, "main"),
                DateTimeOffset.UtcNow,
                owner,
                NormalizeImplementationProfile(request.ImplementationProfile));
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
                DateTimeOffset.UtcNow,
                "local",
                "react-preview");
            _repositories.Add(repository);
            return repository;
        }

        private Guid? RepositoryIdForBoard(Guid boardId) =>
            _boardRepositoryLinks.SingleOrDefault(link => link.BoardId == boardId && link.IsPrimary)?.RepositoryId ??
            _boards.SingleOrDefault(board => board.Id == boardId)?.RepositoryId;

        private RepositoryProfileDto? PrimaryRepositoryProfileFor(Guid boardId)
        {
            var repositoryId = RepositoryIdForBoard(boardId);
            return repositoryId is null
                ? null
                : _boardRepositoryProfiles.SingleOrDefault(profile => profile.BoardId == boardId && profile.RepositoryId == repositoryId)?.Profile;
        }

        private IReadOnlyList<BoardRepositoryDto> BoardRepositoriesFor(Guid boardId)
        {
            var links = _boardRepositoryLinks.Where(link => link.BoardId == boardId).ToArray();
            if (links.Length == 0 && _boards.SingleOrDefault(board => board.Id == boardId)?.RepositoryId is { } legacyRepositoryId)
            {
                var legacyRepository = _repositories.SingleOrDefault(repository => repository.Id == legacyRepositoryId);
                if (legacyRepository is not null)
                {
                    links = [new BoardRepositoryLinkRecord(boardId, legacyRepositoryId, true, legacyRepository.ImplementationProfile)];
                }
            }

            return links
                .Select(link => (link, repository: _repositories.SingleOrDefault(repository => repository.Id == link.RepositoryId)))
                .Where(entry => entry.repository is not null)
                .Select(entry => new BoardRepositoryDto(
                    entry.link.BoardId,
                    entry.link.RepositoryId,
                    entry.link.IsPrimary,
                    entry.link.ImplementationProfile,
                    entry.repository!,
                    _boardRepositoryProfiles.SingleOrDefault(profile => profile.BoardId == entry.link.BoardId && profile.RepositoryId == entry.link.RepositoryId)?.Profile))
                .OrderByDescending(entry => entry.IsPrimary)
                .ThenBy(entry => entry.Repository.Name)
                .ToArray();
        }

        private void UpsertBoardRepositoryLinkWithoutLock(Guid boardId, Guid repositoryId, bool isPrimary, string implementationProfile)
        {
            if (isPrimary)
            {
                for (var index = 0; index < _boardRepositoryLinks.Count; index++)
                {
                    var link = _boardRepositoryLinks[index];
                    if (link.BoardId == boardId && link.IsPrimary)
                    {
                        _boardRepositoryLinks[index] = link with { IsPrimary = false };
                    }
                }
            }

            _boardRepositoryLinks.RemoveAll(link => link.BoardId == boardId && link.RepositoryId == repositoryId);
            _boardRepositoryLinks.Add(new BoardRepositoryLinkRecord(boardId, repositoryId, isPrimary, NormalizeImplementationProfile(implementationProfile)));
        }

        private void UpsertBoardRepositoryProfileWithoutLock(Guid boardId, Guid repositoryId, RepositoryProfileDto profile)
        {
            var normalized = RepositoryProfileAiParser.NormalizeRepositoryProfile(profile);
            _boardRepositoryProfiles.RemoveAll(existing => existing.BoardId == boardId && existing.RepositoryId == repositoryId);
            _boardRepositoryProfiles.Add(new BoardRepositoryProfileRecord(boardId, repositoryId, normalized));
        }

        private void UpsertBoardTeamAccessWithoutLock(Guid boardId, Guid teamId, string role)
        {
            _boardTeamAccess.RemoveAll(access => access.BoardId == boardId && access.TeamId == teamId);
            _boardTeamAccess.Add(new BoardTeamAccessRecord(boardId, teamId, NormalizeRole(role)));
        }

        private void GrantActorBoardOwnerAccessWithoutLock(Guid boardId, string? actorSubject)
        {
            if (string.IsNullOrWhiteSpace(actorSubject) || !HasAnyTeamOrAccess())
            {
                return;
            }

            var actor = _users.FirstOrDefault(user => user.Subject.Equals(actorSubject, StringComparison.OrdinalIgnoreCase));
            if (actor is null)
            {
                actor = new UserDto(Guid.NewGuid(), actorSubject.Trim(), actorSubject.Trim(), actorSubject.Trim());
                _users.Add(actor);
            }

            _boardAccess.RemoveAll(access => access.BoardId == boardId && access.UserId == actor.Id);
            _boardAccess.Add(new BoardAccessDtoRecord(boardId, actor.Id, "Owner"));
        }

        private void BackfillBoardTeamAccessWithoutLock()
        {
            if (_boardTeamAccess.Count > 0 || _teams.Count == 0)
            {
                return;
            }

            var defaultTeam = _teams
                .OrderByDescending(team => team.Members.Any(member => NormalizeRole(member.Role) == "Owner"))
                .ThenBy(team => team.Name)
                .FirstOrDefault();
            if (defaultTeam is null)
            {
                return;
            }

            foreach (var board in _boards)
            {
                _boardTeamAccess.Add(new BoardTeamAccessRecord(board.Id, defaultTeam.Id, "Owner"));
            }
        }

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

        private static bool IsRepositoryCleanupPendingStatus(string status) =>
            status is "Queued" or "Cloning" or "Implementing" or "Validating" or "Pushing";

        private static string NormalizeText(string? value, string fallback) =>
            string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

        private static string RedactTerminalMessage(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            var redacted = Regex.Replace(value, @"x-access-token:[^@\s]+@github\.com", "x-access-token:[redacted]@github.com", RegexOptions.IgnoreCase);
            redacted = Regex.Replace(redacted, @"\b(?:ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9_]{20,}\b", "[redacted-github-token]");
            redacted = Regex.Replace(redacted, @"\bgithub_pat_[A-Za-z0-9_]{20,}\b", "[redacted-github-token]", RegexOptions.IgnoreCase);
            redacted = Regex.Replace(redacted, @"(?i)\b([A-Z0-9_]*(?:TOKEN|SECRET|PASSWORD|PRIVATE_KEY)[A-Z0-9_]*)=([^\s]+)", "$1=[redacted]");
            redacted = Regex.Replace(redacted, @"(?i)\bAuthorization:\s*Bearer\s+[A-Za-z0-9._~+/=-]+", "Authorization: Bearer [redacted]");
            return redacted;
        }

        private static string NormalizeImplementationProfile(string? value)
        {
            var normalized = NormalizeText(value, "react-preview").ToLowerInvariant();
            return normalized is "code-repo" or "unity" or "react-preview" or "gitops-homelab" ? normalized : "react-preview";
        }

        private static string NormalizeRole(string? value)
        {
            var normalized = NormalizeText(value, "Viewer");
            return normalized.Equals("Owner", StringComparison.OrdinalIgnoreCase) ? "Owner" :
                normalized.Equals("Admin", StringComparison.OrdinalIgnoreCase) ? "Admin" :
                normalized.Equals("Member", StringComparison.OrdinalIgnoreCase) ? "Member" :
                "Viewer";
        }

        private static bool IsMutatingRole(string? role) =>
            NormalizeRole(role) is "Owner" or "Admin" or "Member";

        private static int RoleRank(string? role) =>
            NormalizeRole(role) switch
            {
                "Owner" => 3,
                "Admin" => 2,
                "Member" => 1,
                _ => 0
            };

        private static string RoleFromRank(int rank) =>
            rank >= 3 ? "Owner" :
            rank == 2 ? "Admin" :
            rank == 1 ? "Member" :
            "Viewer";

        private static string MostRestrictiveRole(string? left, string? right) =>
            RoleFromRank(Math.Min(RoleRank(left), RoleRank(right)));

        private bool HasAnyTeamOrAccess() => _teams.Count > 0 || _boardAccess.Count > 0 || _boardTeamAccess.Count > 0;

        private bool CanCreateBoardScopedResourceWithoutLock(string actorSubject) =>
            !HasAnyTeamOrAccess() || _boards.Any(board => CanMutateBoardWithoutLock(board.Id, actorSubject));

        private bool CanMutateBoardWithoutLock(Guid boardId, string actorSubject)
        {
            if (_boards.All(board => board.Id != boardId))
            {
                return false;
            }

            var user = _users.SingleOrDefault(entry => entry.Subject.Equals(actorSubject, StringComparison.OrdinalIgnoreCase));
            if (user is null)
            {
                return !HasAnyTeamOrAccess();
            }

            if (_boardAccess.Any(access => access.BoardId == boardId && access.UserId == user.Id && IsMutatingRole(access.Role)))
            {
                return true;
            }

            return _boardTeamAccess.Any(access =>
            {
                var team = _teams.SingleOrDefault(entry => entry.Id == access.TeamId);
                var member = team?.Members.SingleOrDefault(entry => entry.UserId == user.Id);
                return access.BoardId == boardId &&
                    member is not null &&
                    IsMutatingRole(MostRestrictiveRole(access.Role, member.Role));
            });
        }

        private bool CanViewBoardWithoutLock(Guid boardId, string actorSubject)
        {
            if (_boards.All(board => board.Id != boardId))
            {
                return false;
            }

            if (!HasAnyTeamOrAccess())
            {
                return true;
            }

            var user = _users.SingleOrDefault(entry => entry.Subject.Equals(actorSubject, StringComparison.OrdinalIgnoreCase));
            if (user is null)
            {
                return false;
            }

            if (_boardAccess.Any(access => access.BoardId == boardId && access.UserId == user.Id && RoleRank(access.Role) > 0))
            {
                return true;
            }

            return _boardTeamAccess.Any(access =>
            {
                var team = _teams.SingleOrDefault(entry => entry.Id == access.TeamId);
                var member = team?.Members.SingleOrDefault(entry => entry.UserId == user.Id);
                return access.BoardId == boardId &&
                    member is not null &&
                    RoleRank(MostRestrictiveRole(access.Role, member.Role)) > 0;
            });
        }

        private HashSet<Guid> VisibleBoardIdsWithoutLock(string actorSubject) =>
            _boards.Where(board => CanViewBoardWithoutLock(board.Id, actorSubject)).Select(board => board.Id).ToHashSet();

        private HashSet<Guid> VisibleRepositoryIdsWithoutLock(string actorSubject)
        {
            var visibleBoardIds = VisibleBoardIdsWithoutLock(actorSubject);
            var repositoryIds = _boards
                .Where(board => board.RepositoryId is not null && visibleBoardIds.Contains(board.Id))
                .Select(board => board.RepositoryId!.Value)
                .ToHashSet();

            foreach (var link in _boardRepositoryLinks.Where(link => visibleBoardIds.Contains(link.BoardId)))
            {
                repositoryIds.Add(link.RepositoryId);
            }

            return repositoryIds;
        }

        private bool TryResolvePipelineRunTargetWithoutLock(Guid repositoryId, Guid? boardId, Guid? workItemId, out Guid? targetBoardId)
        {
            targetBoardId = null;
            if (_repositories.All(repository => repository.Id != repositoryId))
            {
                return false;
            }

            if (workItemId is { } id)
            {
                var item = _items.SingleOrDefault(entry => entry.Id == id);
                if (item is null || (boardId is { } explicitBoardId && explicitBoardId != item.BoardId))
                {
                    return false;
                }

                targetBoardId = item.BoardId;
                return IsPipelineRepositoryAllowedForBoardWithoutLock(item.BoardId, repositoryId);
            }

            if (boardId is { } runBoardId)
            {
                if (_boards.All(board => board.Id != runBoardId))
                {
                    return false;
                }

                targetBoardId = runBoardId;
                return IsPipelineRepositoryAllowedForBoardWithoutLock(runBoardId, repositoryId);
            }

            return true;
        }

        private bool IsPipelineRepositoryAllowedForBoardWithoutLock(Guid boardId, Guid repositoryId) =>
            RepositoryIdForBoard(boardId) == repositoryId ||
            BoardRepositoriesFor(boardId).Any(link => link.RepositoryId == repositoryId) ||
            (RepositoryIdForBoard(boardId) is null && IsLocalPreviewRepository(repositoryId));

        private bool IsLocalPreviewRepository(Guid repositoryId) =>
            _repositories.Any(repository =>
                repository.Id == repositoryId &&
                repository.Name.Equals("local/vite-react-tailwind", StringComparison.OrdinalIgnoreCase));

        private bool CanMutateRepositoryOnlyPipelineWithoutLock(Guid repositoryId, string actorSubject) =>
            !HasAnyTeamOrAccess() || VisibleRepositoryIdsWithoutLock(actorSubject).Contains(repositoryId);

        private static bool CanUseGitHubIntegrationWithoutLock(GitHubIntegrationDto integration, string? actorSubject) =>
            string.IsNullOrWhiteSpace(actorSubject) ||
            integration.InstalledBy.Equals(actorSubject, StringComparison.OrdinalIgnoreCase) ||
            integration.InstalledBy.Equals("github-app", StringComparison.OrdinalIgnoreCase);

        private bool CanViewTeamWithoutLock(Guid teamId, string actorSubject)
        {
            if (!HasAnyTeamOrAccess())
            {
                return true;
            }

            var user = _users.SingleOrDefault(entry => entry.Subject.Equals(actorSubject, StringComparison.OrdinalIgnoreCase));
            if (user is null)
            {
                return false;
            }

            return _teams.SingleOrDefault(team => team.Id == teamId)?.Members.Any(member => member.UserId == user.Id) == true;
        }

        private bool IsBoardStatus(Guid boardId, string status) =>
            _boards.SingleOrDefault(board => board.Id == boardId)?.Columns.Contains(status, StringComparer.OrdinalIgnoreCase) == true;

        private static string NormalizeEmail(string? value) =>
            NormalizeText(value, "").ToLowerInvariant();

        private static string NormalizeSecretKey(string? value)
        {
            var normalized = new string(NormalizeText(value, "SECRET").Select(character =>
                char.IsLetterOrDigit(character) || character is '_' or '-' or '.' ? character : '_').ToArray()).Trim('_', '-', '.');
            return string.IsNullOrWhiteSpace(normalized) ? "SECRET" : normalized;
        }

        private static string BuildAiSessionContext(WorkItemRecord item) =>
            $"{item.Key} {item.Title}\nStatus: {item.Status}\nPriority: {item.Priority}\n{item.Description}".Trim();

        private static string? OwnerFromRepositoryName(string? repositoryName)
        {
            var normalized = NormalizeText(repositoryName, "");
            var slash = normalized.IndexOf('/', StringComparison.Ordinal);
            return slash > 0 ? normalized[..slash] : null;
        }

        private static string? FirstMarkerValue(string? logs, string marker) =>
            logs?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => line.StartsWith(marker, StringComparison.Ordinal))
                .Select(line => line[marker.Length..].Trim())
                .LastOrDefault();

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

        private IReadOnlyList<AiRun> PreviewImplementationRunsAwaitingRecoveryForWorkItem(Guid workItemId)
        {
            var preview = _previews.SingleOrDefault(p => p.WorkItemId == workItemId);
            if (preview is null || !IsPreviewImplementationAwaitingRecovery(preview))
            {
                return [];
            }

            return _aiRuns
                .Where(run => run.WorkItemId == workItemId && run.Status == AiRunStatus.Approved)
                .OrderByDescending(run => run.SequenceNumber)
                .ThenByDescending(run => run.CreatedAt)
                .Take(1)
                .ToArray();
        }

        private string BoardImplementationProfile(Guid boardId) =>
            BoardRepositoriesFor(boardId).FirstOrDefault(entry => entry.IsPrimary)?.ImplementationProfile ??
            (RepositoryIdForBoard(boardId) is { } repositoryId
                ? _repositories.SingleOrDefault(repository => repository.Id == repositoryId)?.ImplementationProfile
                : null) ??
            "react-preview";

        private BoardGitOpsSettingsDto? GitOpsSettingsFor(Guid boardId)
        {
            var configured = _boardGitOpsSettings.SingleOrDefault(settings => settings.BoardId == boardId);
            if (configured is not null)
            {
                return string.Equals(BoardImplementationProfile(boardId), "gitops-homelab", StringComparison.OrdinalIgnoreCase) &&
                    IsLegacyDefaultGitOpsPaths(configured.AllowedPaths)
                    ? configured with { AllowedPaths = DefaultGitOpsAllowedPaths }
                    : configured;
            }

            return string.Equals(BoardImplementationProfile(boardId), "gitops-homelab", StringComparison.OrdinalIgnoreCase)
                ? DefaultGitOpsSettings(boardId)
                : null;
        }

        private BoardAiContextDto? AiContextFor(Guid boardId)
        {
            var configured = _boardAiContexts.SingleOrDefault(context => context.BoardId == boardId);
            if (configured is not null)
            {
                return configured;
            }

            return string.Equals(BoardImplementationProfile(boardId), "gitops-homelab", StringComparison.OrdinalIgnoreCase)
                ? DefaultAiContext(boardId)
                : null;
        }

        private static readonly string[] LegacyDefaultGitOpsAllowedPaths = ["apps/", "clusters/", "infrastructure/"];
        private static readonly string[] DefaultGitOpsAllowedPaths = ["apps/", "clusters/", "infrastructure/", "kubernetes/", "tofu/"];

        private static BoardGitOpsSettingsDto DefaultGitOpsSettings(Guid boardId) =>
            new(boardId, DefaultGitOpsAllowedPaths, "argocd", "");

        private static bool IsLegacyDefaultGitOpsPaths(IReadOnlyList<string> paths) =>
            paths.Count == LegacyDefaultGitOpsAllowedPaths.Length &&
            paths.Zip(LegacyDefaultGitOpsAllowedPaths).All(pair => string.Equals(pair.First, pair.Second, StringComparison.OrdinalIgnoreCase));

        private static BoardAiContextDto DefaultAiContext(Guid boardId) =>
            new(boardId, "", [], true);

        private static BoardGitOpsSettingsDto CreateGitOpsSettings(Guid boardId, BoardGitOpsSettingsRequest? request)
        {
            var defaults = DefaultGitOpsSettings(boardId);
            var paths = NormalizePaths(request?.AllowedPaths);
            return new BoardGitOpsSettingsDto(
                boardId,
                paths.Count == 0 ? defaults.AllowedPaths : paths,
                NormalizeKubernetesNamespace(request?.ArgoNamespace, defaults.ArgoNamespace),
                NormalizeLabelSelector(request?.ArgoApplicationSelector));
        }

        private static BoardAiContextDto CreateAiContext(Guid boardId, BoardAiContextRequest? request, bool defaultAskWhenUncertain = true) =>
            new(
                boardId,
                string.IsNullOrWhiteSpace(request?.Instructions) ? "" : request.Instructions.Trim(),
                NormalizeSkills(request?.EnabledSkills),
                request?.AskWhenUncertain ?? defaultAskWhenUncertain);

        private static IReadOnlyList<string> NormalizePaths(IEnumerable<string>? paths) =>
            (paths ?? [])
                .Select(path => path.Replace('\\', '/').Trim())
                .Where(IsSafeRelativeGitOpsPath)
                .Select(path => path.EndsWith("/", StringComparison.Ordinal) ? path : $"{path}/")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

        private static bool IsSafeRelativeGitOpsPath(string path) =>
            !string.IsNullOrWhiteSpace(path) &&
            !path.StartsWith("/", StringComparison.Ordinal) &&
            !path.Contains("../", StringComparison.Ordinal) &&
            !path.Equals("..", StringComparison.Ordinal) &&
            !path.Contains(':', StringComparison.Ordinal) &&
            Regex.IsMatch(path, @"^[A-Za-z0-9._\-/]+$", RegexOptions.CultureInvariant);

        private static string NormalizeKubernetesNamespace(string? value, string fallback)
        {
            var normalized = NormalizeText(value, fallback).ToLowerInvariant();
            return Regex.IsMatch(normalized, @"^[a-z0-9]([-a-z0-9]*[a-z0-9])?$", RegexOptions.CultureInvariant) && normalized.Length <= 63
                ? normalized
                : fallback;
        }

        private static string NormalizeLabelSelector(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "";
            }

            var normalized = value.Trim();
            return normalized.Length <= 200 &&
                Regex.IsMatch(normalized, @"^[A-Za-z0-9_.\-/=,!()]+$", RegexOptions.CultureInvariant)
                ? normalized
                : "";
        }

        private static IReadOnlyList<string> NormalizeSkills(IEnumerable<string>? skills) =>
            (skills ?? [])
                .Select(skill => skill.Trim())
                .Where(skill => !string.IsNullOrWhiteSpace(skill))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

        private static bool IsPreviewImplementationAwaitingRecovery(PreviewDto preview) =>
            string.Equals(preview.Status, "Implementing", StringComparison.OrdinalIgnoreCase) ||
            (string.Equals(preview.Status, "Failed", StringComparison.OrdinalIgnoreCase) &&
             string.Equals(preview.FailureReason, "ServerRestart", StringComparison.OrdinalIgnoreCase));

        private void RecoverInterruptedPreviewImplementations()
        {
            var recovered = false;
            for (var index = 0; index < _previews.Count; index++)
            {
                var preview = _previews[index];
                if (!IsPreviewImplementationAwaitingRecovery(preview) &&
                    !string.Equals(preview.Status, "Applying", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (string.Equals(preview.Status, "Applying", StringComparison.OrdinalIgnoreCase) &&
                    (preview.SourceFiles is { Count: > 0 } || preview.StaticHtml is not null))
                {
                    var applyingRecoveryMessage = "API restarted while Kubernetes resources were being applied. Continuing preview readiness checks.";
                    var applyingTerminalLines = (preview.TerminalLines ?? [])
                        .Concat([new PreviewTerminalLineDto(DateTimeOffset.UtcNow, "system", applyingRecoveryMessage)])
                        .TakeLast(200)
                        .ToArray();
                    _previews[index] = preview with
                    {
                        Status = "Provisioning",
                        Phase = "Waiting for pod readiness.",
                        Message = applyingRecoveryMessage,
                        LastCheckedAt = DateTimeOffset.UtcNow,
                        FailureReason = null,
                        FailureLog = null,
                        TerminalLines = applyingTerminalLines
                    };
                    recovered = true;
                    continue;
                }

                if (IsPreviewImplementationAwaitingRecovery(preview))
                {
                    var message = "API restarted while Codex was generating preview source. Restarting preview implementation automatically.";
                    var terminalLines = (preview.TerminalLines ?? [])
                        .Concat([new PreviewTerminalLineDto(DateTimeOffset.UtcNow, "system", message)])
                        .TakeLast(200)
                        .ToArray();
                    var recoveredPreview = preview with
                    {
                        Status = "Implementing",
                        Phase = "Restarting preview source",
                        Message = message,
                        LastCheckedAt = DateTimeOffset.UtcNow,
                        FailureReason = null,
                        FailureLog = null,
                        TerminalLines = terminalLines
                    };
                    _previews[index] = recoveredPreview;
                    var item = _items.SingleOrDefault(i => i.Id == preview.WorkItemId);
                    if (item is not null)
                    {
                        item.AiStatus = "ImplementationRunning";
                        AddPreviewEvent(item, recoveredPreview, "ImplementationRestarting", "system", message);
                    }
                    recovered = true;
                    continue;
                }

                var failedMessage = "Preview implementation was interrupted before Kubernetes resources could be recovered. Retry the plan implementation to start a fresh Codex session.";
                var failedTerminalLines = (preview.TerminalLines ?? [])
                    .Concat([new PreviewTerminalLineDto(DateTimeOffset.UtcNow, "stderr", failedMessage)])
                    .TakeLast(200)
                    .ToArray();
                _previews[index] = preview with
                {
                    Status = "Failed",
                    Phase = "Failed",
                    Message = failedMessage,
                    LastCheckedAt = DateTimeOffset.UtcNow,
                    FailureReason = "ServerRestart",
                    FailureLog = failedMessage,
                    TerminalLines = failedTerminalLines
                };
                recovered = true;
            }

            if (recovered)
            {
                Persist();
            }
        }

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

        private static bool IsNeedsInputPlan(string plan)
        {
            var normalized = plan.TrimStart();
            return normalized.StartsWith("Questions:", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("Needs input:", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("\nQuestions:", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("\nNeeds input:", StringComparison.OrdinalIgnoreCase);
        }

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
            var repository = new RepositoryDto(Guid.Parse("8a5bca6d-69d1-4a58-bb4f-a629243a9337"), "Sample", "auth-service demo", "sample://rosenvall/auth-service", null, "main", DateTimeOffset.UtcNow.AddDays(-7), "sample", "code-repo");
            var board = new BoardRecord(Guid.Parse("6942aca6-5c36-4498-aeaa-c3a2ebe4e8db"), workspace.Id, "Demo Sprint 42", ["Todo", "In Progress", "AI Planning", "Review", "Done"], repository.Id);
            var task = new WorkItemRecord(Guid.Parse("4f55f9a2-3f05-4ff5-bfd8-a43740bebccb"), board.Id, "TASK-4821", "Feature", "Implement OAuth2 Flow for Partner API Integrations", "Upgrade the current API authentication to support full OAuth2 authorization code flow for third-party partner integrations.", "In Progress", "High", "Sarah J.", 0);
            var aiTask = new WorkItemRecord(Guid.Parse("9d81428e-f407-4689-a0c8-20e6e48175bb"), board.Id, "FE-901", "Feature", "Generate Unit Tests for Auth Module", "Generate a focused suite for authentication edge cases.", "AI Planning", "Medium", null, 0)
            {
                AiStatus = "Planning"
            };

            _workspaces.Add(workspace);
            _repositories.Add(repository);
            _boards.Add(board);
            _boardRepositoryLinks.Add(new BoardRepositoryLinkRecord(board.Id, repository.Id, true, repository.ImplementationProfile));
            var owner = new UserDto(Guid.Parse("4c1097a1-7f25-4ae2-a9e4-6851cf5cf435"), "Christopher Rosenvall", "christopher.rosenvall@gmail.com", "local-dev");
            _users.Add(owner);
            _teams.Add(new TeamDto(Guid.Parse("5d067d32-142e-4435-b6c6-d96278be6225"), "Rosenvall", [new TeamMemberDto(owner.Id, "Owner")], DateTimeOffset.UtcNow));
            _boardTeamAccess.Add(new BoardTeamAccessRecord(board.Id, Guid.Parse("5d067d32-142e-4435-b6c6-d96278be6225"), "Owner"));
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
            _development.Add(new DevelopmentDtoRecord(task.Id, new DevelopmentDto("sample/auth-service", "feat/oauth2-flow", null, "Demo data")));
            var seededPreview = new PreviewDto(Guid.NewGuid(), task.Id, "https://feat-auth.rosenvall.se", "ghcr.io/rosenvall/auth-service@sha256:demo", "Running", DateTimeOffset.UtcNow.AddDays(7), null, seededResources.Namespace, seededResources.Name);
            _previews.Add(seededPreview);
            AddPreviewEvent(task, seededPreview, "Created", "seed", "Seed preview created.");
            AddTimelineEvent(board.Id, repository.Id, task.Id, "Commit", task.Key, "Sample auth-service history imported.", "seed", null, DateTimeOffset.UtcNow.AddHours(-3));
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
                    run.CreatedAt ?? DateTimeOffset.UtcNow.AddTicks(index),
                    run.ReasoningEffort))));
            _previews.AddRange(snapshot.Previews.Select(preview => preview with { Status = NormalizePreviewStatus(preview.Status) }));
            _development.AddRange(snapshot.Development.Select(development => new DevelopmentDtoRecord(development.WorkItemId, development.Development)));
            _previewEvents.AddRange(snapshot.PreviewEvents ?? []);
            _pipelineRuns.AddRange(snapshot.PipelineRuns ?? []);
            _implementationRuns.AddRange(snapshot.ImplementationRuns ?? []);
            _repositoryCleanupRuns.AddRange(snapshot.RepositoryCleanupRuns ?? []);
            _timelineEvents.AddRange(snapshot.TimelineEvents ?? []);
            _users.AddRange(snapshot.Users ?? []);
            _teams.AddRange(snapshot.Teams ?? []);
            _boardAccess.AddRange(snapshot.BoardAccess ?? []);
            _boardTeamAccess.AddRange(snapshot.BoardTeamAccess ?? []);
            _boardRepositoryLinks.AddRange(snapshot.BoardRepositoryLinks ?? []);
            _boardRepositoryProfiles.AddRange(snapshot.BoardRepositoryProfiles ?? []);
            BackfillBoardTeamAccessWithoutLock();
            foreach (var board in _boards.Where(board => board.RepositoryId is not null && _boardRepositoryLinks.All(link => link.BoardId != board.Id)))
            {
                var repository = _repositories.SingleOrDefault(entry => entry.Id == board.RepositoryId);
                if (repository is not null)
                {
                    _boardRepositoryLinks.Add(new BoardRepositoryLinkRecord(board.Id, repository.Id, true, repository.ImplementationProfile));
                }
            }
            _githubIntegrations.AddRange(snapshot.GitHubIntegrations ?? []);
            _boardSecrets.AddRange(snapshot.BoardSecrets ?? []);
            _aiSessions.AddRange(snapshot.AiSessions ?? []);
            _boardGitOpsSettings.AddRange(snapshot.BoardGitOpsSettings ?? []);
            _boardAiContexts.AddRange(snapshot.BoardAiContexts ?? []);
            _nextTaskNumber = Math.Max(snapshot.NextTaskNumber, NextTaskNumberFromItems());
            if (BackfillDemoSeedWithoutLock())
            {
                Persist();
            }
            return true;
        }

        private bool BackfillDemoSeedWithoutLock()
        {
            var changed = false;
            var demoBoardId = Guid.Parse("6942aca6-5c36-4498-aeaa-c3a2ebe4e8db");
            var demoRepositoryId = Guid.Parse("8a5bca6d-69d1-4a58-bb4f-a629243a9337");
            var boardIndex = _boards.FindIndex(board => board.Id == demoBoardId && board.Name == "Sprint 42");
            if (boardIndex >= 0)
            {
                var board = _boards[boardIndex];
                _boards[boardIndex] = new BoardRecord(board.Id, board.WorkspaceId, "Demo Sprint 42", board.Columns, board.RepositoryId);
                changed = true;
            }

            var repositoryIndex = _repositories.FindIndex(repository => repository.Id == demoRepositoryId && repository.Provider == "GitHub" && repository.Name == "auth-service");
            if (repositoryIndex >= 0)
            {
                _repositories[repositoryIndex] = new RepositoryDto(demoRepositoryId, "Sample", "auth-service demo", "sample://rosenvall/auth-service", null, "main", _repositories[repositoryIndex].CreatedAt, "sample", "code-repo");
                changed = true;
            }

            for (var index = 0; index < _development.Count; index++)
            {
                var development = _development[index];
                if (development.Development.Repository == "rosenvall/auth-service" ||
                    development.Development.PullRequestUrl?.Contains("github.com/rosenvall/auth-service", StringComparison.OrdinalIgnoreCase) == true)
                {
                    _development[index] = development with { Development = development.Development with { Repository = "sample/auth-service", PullRequestUrl = null, ChecksStatus = "Demo data" } };
                    changed = true;
                }
            }

            for (var index = 0; index < _timelineEvents.Count; index++)
            {
                var entry = _timelineEvents[index];
                if (entry.Url?.Contains("github.com/rosenvall/auth-service", StringComparison.OrdinalIgnoreCase) == true)
                {
                    _timelineEvents[index] = entry with { Url = null, Message = entry.Message.Replace("Initial auth-service history imported.", "Sample auth-service history imported.", StringComparison.Ordinal) };
                    changed = true;
                }
            }

            return changed;
        }

        private void Persist()
        {
            var snapshot = new DevOpsSnapshot(
                _workspaces.ToArray(),
                _boards.Select(board => new BoardSnapshot(board.Id, board.WorkspaceId, board.Name, board.Columns, board.RepositoryId)).ToArray(),
                _items.Select(item => new WorkItemSnapshot(item.Id, item.BoardId, item.Key, item.Type, item.Title, item.Description, item.Status, item.Priority, item.Assignee, item.AiStatus, item.PullRequestUrl, item.SortOrder)).ToArray(),
                _comments.ToArray(),
                _aiRuns.Select(run => new AiRunSnapshot(run.Id, run.WorkItemId, run.Provider, run.Model, run.Status, run.Plan, run.ApprovedBy, run.SequenceNumber, run.CreatedAt, run.ReasoningEffort)).ToArray(),
                _previews.ToArray(),
                _development.Select(development => new DevelopmentSnapshot(development.WorkItemId, development.Development)).ToArray(),
                _nextTaskNumber,
                _previewEvents.ToArray(),
                _repositories.ToArray(),
                _pipelineRuns.ToArray(),
                _timelineEvents.ToArray(),
                _implementationRuns.ToArray(),
                _repositoryCleanupRuns.ToArray(),
                _users.ToArray(),
                _teams.ToArray(),
                _boardAccess.ToArray(),
                _boardTeamAccess.ToArray(),
                _boardRepositoryLinks.ToArray(),
                _githubIntegrations.ToArray(),
                _boardSecrets.ToArray(),
                _aiSessions.ToArray(),
                _boardGitOpsSettings.ToArray(),
                _boardAiContexts.ToArray(),
                _boardRepositoryProfiles.ToArray());
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

    internal sealed class BoardRecord(Guid id, Guid workspaceId, string name, IReadOnlyList<string> columns, Guid? repositoryId = null)
    {
        public Guid Id { get; } = id;
        public Guid WorkspaceId { get; } = workspaceId;
        public string Name { get; } = name;
        public IReadOnlyList<string> Columns { get; } = columns;
        public Guid? RepositoryId { get; set; } = repositoryId;
    }
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
    internal sealed record BoardAccessDtoRecord(Guid BoardId, Guid UserId, string Role);
    internal sealed record BoardTeamAccessRecord(Guid BoardId, Guid TeamId, string Role);
    internal sealed record BoardRepositoryLinkRecord(Guid BoardId, Guid RepositoryId, bool IsPrimary, string ImplementationProfile);
    internal sealed record BoardRepositoryProfileRecord(Guid BoardId, Guid RepositoryId, RepositoryProfileDto Profile);
    internal sealed record DevOpsSnapshot(IReadOnlyList<WorkspaceDto> Workspaces, IReadOnlyList<BoardSnapshot> Boards, IReadOnlyList<WorkItemSnapshot> Items, IReadOnlyList<CommentDto> Comments, IReadOnlyList<AiRunSnapshot> AiRuns, IReadOnlyList<PreviewDto> Previews, IReadOnlyList<DevelopmentSnapshot> Development, int NextTaskNumber = 0, IReadOnlyList<PreviewEventDto>? PreviewEvents = null, IReadOnlyList<RepositoryDto>? Repositories = null, IReadOnlyList<PipelineRunDto>? PipelineRuns = null, IReadOnlyList<TimelineEventDto>? TimelineEvents = null, IReadOnlyList<ImplementationRunDto>? ImplementationRuns = null, IReadOnlyList<RepositoryCleanupRunDto>? RepositoryCleanupRuns = null, IReadOnlyList<UserDto>? Users = null, IReadOnlyList<TeamDto>? Teams = null, IReadOnlyList<BoardAccessDtoRecord>? BoardAccess = null, IReadOnlyList<BoardTeamAccessRecord>? BoardTeamAccess = null, IReadOnlyList<BoardRepositoryLinkRecord>? BoardRepositoryLinks = null, IReadOnlyList<GitHubIntegrationDto>? GitHubIntegrations = null, IReadOnlyList<BoardSecretDto>? BoardSecrets = null, IReadOnlyList<AiSessionDto>? AiSessions = null, IReadOnlyList<BoardGitOpsSettingsDto>? BoardGitOpsSettings = null, IReadOnlyList<BoardAiContextDto>? BoardAiContexts = null, IReadOnlyList<BoardRepositoryProfileRecord>? BoardRepositoryProfiles = null);
    internal sealed record BoardSnapshot(Guid Id, Guid WorkspaceId, string Name, IReadOnlyList<string> Columns, Guid? RepositoryId = null);
    internal sealed record WorkItemSnapshot(Guid Id, Guid BoardId, string Key, string Type, string Title, string Description, string Status, string Priority, string? Assignee, string? AiStatus, string? PullRequestUrl, int SortOrder);
    internal sealed record AiRunSnapshot(Guid Id, Guid WorkItemId, string Provider, string Model, AiRunStatus Status, string? Plan, string? ApprovedBy, int SequenceNumber = 0, DateTimeOffset? CreatedAt = null, string? ReasoningEffort = null);
    internal sealed record DevelopmentSnapshot(Guid WorkItemId, DevelopmentDto Development);
}
