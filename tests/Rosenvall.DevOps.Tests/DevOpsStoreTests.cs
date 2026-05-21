using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Rosenvall.DevOps.Api;
using Rosenvall.DevOps.Core;

namespace Rosenvall.DevOps.Tests;

public sealed class DevOpsStoreTests
{
    [Fact]
    public void Work_item_can_be_patched_and_moved_with_sort_order()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var board = store.GetWorkspaces().SelectMany(workspace => store.GetBoards(workspace.Id)).First();
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Original title", "Original description", "Todo", "Medium", null));

        var updated = store.UpdateWorkItem(item.Id, new UpdateWorkItemRequest("Updated title", "Updated description", "Bug", "Todo", "High", "Crille"));
        var moved = store.MoveWorkItem(item.Id, new MoveWorkItemRequest("In Progress", 0));
        var refreshedBoard = store.GetBoard(board.Id)!;

        Assert.NotNull(updated);
        Assert.NotNull(moved);
        Assert.Equal("Updated title", updated.Title);
        Assert.Equal("Bug", updated.Type);
        Assert.Equal("High", updated.Priority);
        Assert.Equal("Crille", updated.Assignee);
        Assert.Equal("In Progress", moved.Status);
        Assert.Equal(item.Id, refreshedBoard.Columns.Single(column => column.Name == "In Progress").Items.First().Id);
    }

    [Fact]
    public void Work_item_keys_are_not_reused_after_delete()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var board = store.GetWorkspaces().SelectMany(workspace => store.GetBoards(workspace.Id)).First();
        var first = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "First", "First card", "Todo", "Medium", null));
        store.DeleteWorkItem(first.Id);

        var second = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Second", "Second card", "Todo", "Medium", null));

        Assert.NotEqual(first.Key, second.Key);
    }

    [Fact]
    public void Deleting_work_item_removes_related_runtime_state()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var board = store.GetWorkspaces().SelectMany(workspace => store.GetBoards(workspace.Id)).First();
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Delete me", "Remove everything", "Todo", "Medium", null));
        var run = store.StartAiPlan(item.Id, "ollama", "llama3:8b")!;
        store.ApproveAiRun(run.Id, "crille");
        store.ApplyGitHubCallback(new GitHubCallbackRequest(item.Id, "rosenvall/demo", "feat/delete-me", "https://github.com/rosenvall/demo/pull/1", "ghcr.io/rosenvall/demo:delete-me", "Checks passed"));

        Assert.True(store.DeleteWorkItem(item.Id));

        Assert.Null(store.GetWorkItemDetail(item.Id));
        Assert.Empty(store.GetAiRuns(item.Id));
        Assert.Null(store.RenderPreviewManifest(item.Id));
        Assert.Contains(store.GetPreviewEvents(), entry => entry.WorkItemId == item.Id && entry.EventType == "Deleted");
        Assert.DoesNotContain(store.GetBoard(board.Id)!.Columns.SelectMany(column => column.Items), remaining => remaining.Id == item.Id);
    }

    [Fact]
    public void Dashboard_metrics_are_derived_from_current_runtime_state()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var board = store.GetBoards(workspace.Id).First();
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Metric card", "Measure real state", "Todo", "Medium", null));

        store.ApplyGitHubCallback(new GitHubCallbackRequest(item.Id, "rosenvall/demo", "feat/metric-card", "https://github.com/rosenvall/demo/pull/3", "ghcr.io/rosenvall/demo:metric-card", "Checks passed"));

        var metrics = store.GetWorkspaces().Single(w => w.Id == workspace.Id);

        Assert.True(metrics.ActiveProjects >= 1);
        Assert.True(metrics.OpenPullRequests >= 1);
        Assert.True(metrics.SuccessfulAiImplementations >= 1);
        Assert.True(metrics.ComputeUsagePercent >= 10);
    }

    [Fact]
    public void Board_can_be_created_for_provider_neutral_repository()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();

        var repository = store.CreateRepository(new CreateRepositoryRequest("Forgejo", "rosenvall-web", "ssh://git.rosenvall.se/rosenvall/web.git", "main", "https://git.rosenvall.se/rosenvall/web"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("rosenvall-web", repository.Id, null, null, null, null, null));
        Assert.NotNull(board);
        var boardRepository = board.Repository;
        Assert.NotNull(boardRepository);

        var boards = store.GetBoards(workspace.Id);

        Assert.Contains(store.GetRepositories(), repo => repo.Id == repository.Id && repo.Provider == "Forgejo");
        Assert.Equal(repository.Id, boardRepository.Id);
        Assert.Equal("rosenvall-web", board.Name);
        Assert.Contains(boards, entry => entry.Id == board.Id && entry.Repository?.RemoteUrl == repository.RemoteUrl);
    }

    [Fact]
    public void Board_creation_can_create_inline_external_repository()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();

        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest(
            "Azure delivery",
            null,
            "AzureDevOps",
            "platform",
            "https://dev.azure.com/rosenvall/platform/_git/platform",
            "https://dev.azure.com/rosenvall/platform/_git/platform",
            "main"));

        Assert.NotNull(board);
        Assert.NotNull(board.Repository);
        var boardRepository = board.Repository;
        Assert.NotNull(boardRepository);
        Assert.Equal("AzureDevOps", boardRepository.Provider);
        Assert.Equal("platform", boardRepository.Name);
        Assert.Contains(store.GetRepositories(), repository => repository.Provider == "AzureDevOps" && repository.RemoteUrl.Contains("dev.azure.com", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Timeline_combines_card_preview_pr_and_pipeline_events_for_board()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest("GitHub", "demo", "https://github.com/rosenvall/demo.git", "main", "https://github.com/rosenvall/demo"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("demo", repository.Id, null, null, null, null, null));
        Assert.NotNull(board);
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Timeline card", "Track all runtime events.", "Todo", "Medium", null));

        store.MoveWorkItem(item.Id, new MoveWorkItemRequest("In Progress", 0));
        var run = store.StartAiPlan(item.Id, "ollama", "llama3:8b")!;
        store.ApproveAiRun(run.Id, "crille");
        store.CompleteLocalReactImplementation(item.Id);
        store.ApplyGitHubCallback(new GitHubCallbackRequest(item.Id, "rosenvall/demo", "feat/timeline", "https://github.com/rosenvall/demo/pull/9", "ghcr.io/rosenvall/demo:timeline", "Checks passed"));
        store.RecordPipelineRun(new RecordPipelineRunRequest(repository.Id, board.Id, item.Id, "Build", "Succeeded", "Kubernetes job completed", "https://ci.rosenvall.se/runs/1"));

        var timeline = store.GetTimeline(board.Id);

        Assert.Contains(timeline, entry => entry.Kind == "CardMoved" && entry.WorkItemId == item.Id);
        Assert.Contains(timeline, entry => entry.Kind == "PreviewCreated" && entry.WorkItemId == item.Id);
        Assert.Contains(timeline, entry => entry.Kind == "PullRequest" && entry.Url == "https://github.com/rosenvall/demo/pull/9");
        Assert.Contains(timeline, entry => entry.Kind == "Pipeline" && entry.Message.Contains("Kubernetes job", StringComparison.OrdinalIgnoreCase));
        Assert.True(timeline.SequenceEqual(timeline.OrderByDescending(entry => entry.CreatedAt)));
    }

    [Fact]
    public void Pipeline_run_can_render_kubernetes_job_manifest()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest("Forgejo", "platform/api", "ssh://git.rosenvall.se/platform/api.git", "main", "https://git.rosenvall.se/platform/api"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("platform-api", repository.Id, null, null, null, null, null));
        Assert.NotNull(board);
        var run = store.RecordPipelineRun(new RecordPipelineRunRequest(repository.Id, board.Id, null, "Build", "Queued", "Build via Kubernetes Job", null));
        Assert.NotNull(run);

        var manifest = store.RenderPipelineJobManifest(run.Id);

        Assert.NotNull(manifest);
        Assert.Contains("kind: Job", manifest);
        Assert.Contains("namespace: rosenvall-devops-pipelines", manifest);
        Assert.Contains("ssh://git.rosenvall.se/platform/api.git", manifest);
        Assert.Contains("ROSENVALL_PIPELINE_RUN_ID", manifest);
    }

    [Fact]
    public void Settings_expose_forgejo_authentik_and_repository_policy()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Repositories:Provider"] = "Forgejo",
                ["Repositories:Mode"] = "LinkExistingFirst",
                ["Repositories:Forgejo:ApiBaseUrl"] = "https://git.rosenvall.se/api/v1",
                ["Repositories:Forgejo:CanCreateRepositories"] = "true",
                ["Authentik:Authority"] = "https://authentik.rosenvall.se",
                ["Authentik:UsersEndpoint"] = "https://authentik.rosenvall.se/api/v3/core/users/"
            })
            .Build();

        var settings = fixture.Store.GetSettings(configuration);

        Assert.Equal("Forgejo", settings.Repositories.Provider);
        Assert.Equal("LinkExistingFirst", settings.Repositories.Mode);
        Assert.True(settings.Repositories.CanCreateRepositories);
        Assert.Equal("https://git.rosenvall.se/api/v1", settings.Repositories.ApiBaseUrl);
        Assert.True(settings.Authentik.Enabled);
        Assert.Equal("https://authentik.rosenvall.se/api/v3/core/users/", settings.Authentik.UsersEndpoint);
    }

    [Fact]
    public void Pipeline_run_execution_marks_run_running_and_records_metrics()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest("Forgejo", "platform/api", "ssh://git.rosenvall.se/platform/api.git", "main", "https://git.rosenvall.se/platform/api"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("platform-api", repository.Id, null, null, null, null, null));
        Assert.NotNull(board);
        var run = store.RecordPipelineRun(new RecordPipelineRunRequest(repository.Id, board.Id, null, "Build", "Queued", "Waiting for Kubernetes", null, 1200, 44, 12));
        Assert.NotNull(run);

        var executing = store.MarkPipelineRunExecuting(run.Id, "crille");
        var metrics = store.GetMetrics(board.Id);

        Assert.NotNull(executing);
        Assert.Equal("Running", executing.Status);
        Assert.Equal("Kubernetes Job submitted by crille.", executing.Message);
        Assert.Equal(1200, metrics.TokensUsed);
        Assert.Equal(44, metrics.CodeAdded);
        Assert.Equal(12, metrics.CodeDeleted);
        Assert.Equal(1, metrics.PipelineRuns);
        Assert.Contains(store.GetTimeline(board.Id), entry => entry.Kind == "Pipeline" && entry.Message.Contains("submitted by crille", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GitHub_repository_picker_normalizes_repositories_for_code_repo_boards()
    {
        using var document = JsonDocument.Parse("""
        [
          {
            "name": "Gatebound",
            "clone_url": "https://github.com/carnufex/Gatebound.git",
            "html_url": "https://github.com/carnufex/Gatebound",
            "default_branch": "main",
            "owner": { "login": "carnufex" }
          }
        ]
        """);

        var repositories = GitHubRepositoryClient.NormalizeRepositories(document.RootElement);

        var repository = Assert.Single(repositories);
        Assert.Equal("GitHub", repository.Provider);
        Assert.Equal("carnufex", repository.Owner);
        Assert.Equal("Gatebound", repository.Name);
        Assert.Equal("main", repository.DefaultBranch);
        Assert.Equal("https://github.com/carnufex/Gatebound.git", repository.RemoteUrl);
        Assert.Equal("https://github.com/carnufex/Gatebound", repository.WebUrl);
        Assert.Equal("code-repo", repository.ImplementationProfile);
    }

    [Fact]
    public void Code_repo_implementation_run_renders_kubernetes_job_without_inline_tokens()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest(
            "GitHub",
            "Gatebound",
            "https://github.com/carnufex/Gatebound.git",
            "main",
            "https://github.com/carnufex/Gatebound",
            "carnufex",
            "unity"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("Gatebound", repository.Id, null, null, null, null, null));
        Assert.NotNull(board);
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Add dash ability", "Implement a small player dash.", "Todo", "Medium", null));
        var aiRun = store.StartAiPlan(item.Id, "codex", "gpt-5.4", "Inspect the Unity project and add a focused dash implementation.")!;
        var implementationRun = store.StartImplementationRun(item.Id, new StartImplementationRunRequest(aiRun.Id, "crille"));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:Codex:Model"] = "gpt-5.4",
                ["GitHub:Token"] = "ghp_secret_that_must_not_render",
                ["GitHub:TokenSecretName"] = "rosenvall-devops-github"
            })
            .Build();

        var manifest = store.RenderImplementationRunManifest(implementationRun!.Id, configuration);

        Assert.NotNull(manifest);
        Assert.Equal("unity", repository.ImplementationProfile);
        Assert.Equal("Queued", implementationRun.Status);
        Assert.Contains("kind: Job", manifest);
        Assert.Contains("codex exec", manifest);
        Assert.Contains("secretKeyRef:", manifest);
        Assert.Contains("rosenvall-devops-github", manifest);
        Assert.Contains("rdo/task-", manifest);
        Assert.Contains("/tmp/rosenvall-workspace", manifest);
        Assert.DoesNotContain("mkdir -p /workspace", manifest);
        Assert.DoesNotContain("ghp_secret_that_must_not_render", manifest);
    }

    [Fact]
    public void React_preview_boards_do_not_start_repository_implementation_runs()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest(
            "Preview board",
            null,
            "local",
            "preview",
            "local://preview",
            null,
            "main",
            null,
            "react-preview"));
        Assert.NotNull(board);
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Demo page", "Build a preview page.", "Todo", "Medium", null));
        var aiRun = store.StartAiPlan(item.Id, "codex", "gpt-5.4", "Build a React preview.")!;

        var implementationRun = store.StartImplementationRun(item.Id, new StartImplementationRunRequest(aiRun.Id, "crille"));

        Assert.Null(implementationRun);
        Assert.Empty(store.GetImplementationRuns(item.Id));
    }

    [Fact]
    public void Assignee_options_include_authentik_users_and_current_board_assignees()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var board = store.GetBoards(workspace.Id).First();
        store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Assigned", "Keep local assignee.", "Todo", "Medium", "Local User"));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentik:Users:0:Id"] = "ak-1",
                ["Authentik:Users:0:DisplayName"] = "Christopher Rosenvall",
                ["Authentik:Users:0:Email"] = "christopher.rosenvall@gmail.com"
            })
            .Build();

        var assignees = store.GetAssignees(board.Id, configuration);

        Assert.Contains(assignees, user => user.DisplayName == "Christopher Rosenvall" && user.Email == "christopher.rosenvall@gmail.com" && user.Source == "Authentik");
        Assert.Contains(assignees, user => user.DisplayName == "Local User" && user.Source == "Board");
    }

    [Fact]
    public void Kubectl_pipe_closure_is_reported_as_recoverable_orchestration_failure()
    {
        Assert.True(PreviewEnvironmentOrchestrator.IsRecoverableKubectlException(new IOException("The pipe is being closed.")));
    }

    [Fact]
    public void Kubectl_namespace_not_found_is_treated_as_preview_failure()
    {
        var message = "Error from server (NotFound): namespaces \"devops-preview-task-4826-cv-hemsida\" not found";

        Assert.True(PreviewEnvironmentOrchestrator.IsMissingPreviewNamespace(message));
    }

    [Fact]
    public async Task Preview_orchestration_reports_missing_kubeconfig_before_starting_kubectl()
    {
        var missingKubeconfig = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "kubeconfig");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Preview:KubectlPath"] = "kubectl",
                ["Preview:KubeconfigPath"] = missingKubeconfig
            })
            .Build();
        var orchestrator = new PreviewEnvironmentOrchestrator(configuration, NullLogger<PreviewEnvironmentOrchestrator>.Instance);

        var result = await orchestrator.ApplyAsync("apiVersion: v1\nkind: Namespace\nmetadata:\n  name: should-not-run\n", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("Configured kubeconfig was not found", result.Message);
        Assert.Contains(missingKubeconfig, result.Message);
        Assert.DoesNotContain("pipe is being closed", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Discarding_ai_run_marks_run_and_removes_active_ai_state()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var board = store.GetWorkspaces().SelectMany(workspace => store.GetBoards(workspace.Id)).First();
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Needs plan", "Ask AI for plan", "Todo", "Medium", null));
        var run = store.StartAiPlan(item.Id, "ollama", "llama3:8b")!;

        var discarded = store.DiscardAiRun(run.Id, "crille");
        var detail = store.GetWorkItemDetail(item.Id)!;

        Assert.NotNull(discarded);
        Assert.Equal(AiRunStatus.Discarded, discarded.Status);
        Assert.Null(detail.Item.AiStatus);
        Assert.Equal("Todo", detail.Item.Status);
        Assert.Contains(detail.Comments, comment => comment.Kind == "Result" && comment.Body.Contains("discarded", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Ai_plan_can_use_provider_generated_plan_text()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var board = store.GetWorkspaces().SelectMany(workspace => store.GetBoards(workspace.Id)).First();
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "hello world", "Create a page.", "Todo", "Medium", null));

        var run = store.StartAiPlan(item.Id, "ollama", "llama3:8b", "Provider generated React/Tailwind plan.");

        Assert.NotNull(run);
        Assert.Equal("Provider generated React/Tailwind plan.", run.Plan);
        Assert.DoesNotContain(store.GetWorkItemDetail(item.Id)!.Comments, comment => comment.Kind == "Plan" && comment.Body == "Provider generated React/Tailwind plan.");
        Assert.Contains(store.GetWorkItemDetail(item.Id)!.Comments, comment => comment.Kind == "Result" && comment.Body.Contains("Created plan #1", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Ai_plans_get_stable_sequence_numbers_and_created_dates()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var board = store.GetWorkspaces().SelectMany(workspace => store.GetBoards(workspace.Id)).First();
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "multi plan", "Create a page.", "Todo", "Medium", null));

        var first = store.StartAiPlan(item.Id, "ollama", "llama3:8b", "First plan.")!;
        var second = store.StartAiPlan(item.Id, "ollama", "llama3:8b", "Second plan.")!;

        Assert.Equal(1, first.SequenceNumber);
        Assert.Equal(2, second.SequenceNumber);
        Assert.True(first.CreatedAt <= second.CreatedAt);
        Assert.Equal([1, 2], store.GetAiRuns(item.Id).Select(run => run.SequenceNumber).ToArray());
    }

    [Fact]
    public void Ai_plan_sequence_and_created_date_survive_snapshot_restore()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var board = store.GetWorkspaces().SelectMany(workspace => store.GetBoards(workspace.Id)).First();
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "persisted plans", "Create a page.", "Todo", "Medium", null));
        var run = store.StartAiPlan(item.Id, "ollama", "llama3:8b", "Persisted plan.")!;

        var restored = fixture.Reopen().GetAiRuns(item.Id).Single();

        Assert.Equal(run.SequenceNumber, restored.SequenceNumber);
        Assert.Equal(run.CreatedAt, restored.CreatedAt);
    }

    [Fact]
    public void Own_human_comments_can_be_edited_and_deleted()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var board = store.GetWorkspaces().SelectMany(workspace => store.GetBoards(workspace.Id)).First();
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "comments", "Edit comments.", "Todo", "Medium", null));
        var comment = store.AddComment(item.Id, "Christopher Rosenvall", "Comment", "Original text.")!;

        var updated = store.UpdateComment(comment.Id, "Christopher Rosenvall", "Updated text.");
        var deleted = store.DeleteComment(comment.Id, "Christopher Rosenvall");

        Assert.NotNull(updated);
        Assert.Equal("Updated text.", updated.Body);
        Assert.True(deleted);
        Assert.DoesNotContain(store.GetWorkItemDetail(item.Id)!.Comments, entry => entry.Id == comment.Id);
    }

    [Fact]
    public void Ai_and_other_user_comments_cannot_be_edited_or_deleted()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var board = store.GetWorkspaces().SelectMany(workspace => store.GetBoards(workspace.Id)).First();
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "comments", "Edit comments.", "Todo", "Medium", null));
        var otherComment = store.AddComment(item.Id, "Other User", "Comment", "Original text.")!;
        store.StartAiPlan(item.Id, "ollama", "llama3:8b", "Plan text.");
        var aiComment = store.GetWorkItemDetail(item.Id)!.Comments.Last(entry => entry.Author == "Rosenvall AI");

        Assert.Throws<InvalidOperationException>(() => store.UpdateComment(otherComment.Id, "Christopher Rosenvall", "Updated text."));
        Assert.Throws<InvalidOperationException>(() => store.DeleteComment(otherComment.Id, "Christopher Rosenvall"));
        Assert.Throws<InvalidOperationException>(() => store.UpdateComment(aiComment.Id, "Rosenvall AI", "Updated text."));
        Assert.Throws<InvalidOperationException>(() => store.DeleteComment(aiComment.Id, "Rosenvall AI"));
    }

    [Fact]
    public void Local_react_implementation_completes_approved_hello_world_with_preview()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var board = store.GetWorkspaces().SelectMany(workspace => store.GetBoards(workspace.Id)).First();
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "hello world", "Create a tiny hello-world page as a React/Tailwind preview.", "Todo", "Medium", null));
        var run = store.StartAiPlan(item.Id, "ollama", "llama3:8b")!;
        store.ApproveAiRun(run.Id, "crille");

        var detail = store.CompleteLocalReactImplementation(run.WorkItemId);
        var manifest = store.RenderPreviewManifest(run.WorkItemId);

        Assert.NotNull(detail);
        Assert.NotNull(detail.Preview);
        Assert.Equal("Implementing", detail.Preview.Status);
        Assert.Equal("Completed", detail.Item.AiStatus);
        Assert.Equal("ghcr.io/carnufex/rosenvall-devops-preview-base:main", detail.Preview.Image);
        Assert.Contains("hello-world", detail.Preview.Url);
        Assert.StartsWith("devops-preview-task-", detail.Preview.Namespace);
        Assert.Contains("hello-world", detail.Preview.Namespace);
        Assert.Contains("Local React/Tailwind implementation completed", detail.Comments.Last().Body);
        Assert.Contains("kind: ConfigMap", manifest);
        Assert.Contains("tailwind.config.ts", manifest);
        Assert.Contains("components.json", manifest);
        Assert.Contains("npm run dev -- --host 0.0.0.0 --port 8080", manifest);
        Assert.DoesNotContain("npm install", manifest);
        Assert.DoesNotContain("chmod -R", manifest);
        Assert.Contains("mkdir -p /workspace/node_modules", manifest);
        Assert.Contains("cp -R /opt/rosenvall-preview/node_modules/. /workspace/node_modules/", manifest);
        Assert.Contains("allowedHosts", manifest);
        Assert.Contains(".rosenvall.se", manifest);
        Assert.Contains("React/Tailwind preview", manifest);
    }

    [Fact]
    public void Preview_implementation_uses_and_persists_ai_generated_source_files()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var board = store.GetWorkspaces().SelectMany(workspace => store.GetBoards(workspace.Id)).First();
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "rit app", "skapa en app dar jag kan rita", "Todo", "Medium", null));
        var run = store.StartAiPlan(item.Id, "ollama", "qwen3.5:latest", "Implement a canvas paint app with color buttons.")!;
        store.ApproveAiRun(run.Id, "crille");
        var generatedSource = LocalReactPreviewProject.ForWorkItem(item.Key, item.Title, "skapa en app dar jag kan rita")
            .Select(file => file.Path == "src/App.tsx"
                ? new PreviewSourceFile(file.Key, file.Path, "export default function App() { return <canvas aria-label=\"Paint canvas\" />; }")
                : file)
            .ToArray();

        var detail = store.CompletePreviewImplementation(item.Id, generatedSource, "codex");
        var manifest = store.RenderPreviewManifest(item.Id);
        var restoredManifest = fixture.Reopen().RenderPreviewManifest(item.Id);

        Assert.NotNull(detail?.Preview);
        Assert.Equal("Completed", detail.Item.AiStatus);
        Assert.Contains("Codex preview source generated", detail.Development?.ChecksStatus);
        Assert.Contains("Paint canvas", manifest);
        Assert.DoesNotContain("React, TypeScript and Tailwind are ready for this ticket slice.", manifest);
        Assert.Contains("Paint canvas", restoredManifest);
    }

    [Fact]
    public void Preview_implementation_exposes_terminal_lines_while_codex_runs()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var board = store.GetWorkspaces().SelectMany(workspace => store.GetBoards(workspace.Id)).First();
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "hamburgare", "Skapa en demo for tva hamburgare.", "Todo", "Medium", null));
        var run = store.StartAiPlan(item.Id, "codex", "gpt-5.4", "Implement two burger cards.")!;
        store.ApproveAiRun(run.Id, "crille");

        var preview = store.BeginPreviewImplementation(item.Id, "codex");
        store.AppendPreviewTerminalLine(item.Id, "stdout", "OpenAI Codex v0.131.0");
        var applying = store.MarkPreviewApplying(item.Id, "Applying Kubernetes resources.")!;

        Assert.NotNull(preview);
        Assert.Equal("Implementing", preview.Status);
        Assert.Contains(applying.Preview!.TerminalLines!, line => line.Message.Contains("OpenAI Codex", StringComparison.Ordinal));
        Assert.Equal("Applying", applying.Preview.Status);
    }

    [Fact]
    public void Interrupted_preview_implementation_is_recoverable_after_restart()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var board = store.GetWorkspaces().SelectMany(workspace => store.GetBoards(workspace.Id)).First();
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "hamburgare", "Skapa en demo for tva hamburgare.", "Todo", "Medium", null));
        var run = store.StartAiPlan(item.Id, "codex", "gpt-5.4", "Implement two burger cards.")!;
        store.ApproveAiRun(run.Id, "crille");
        store.BeginPreviewImplementation(item.Id, "codex");

        var restored = fixture.Reopen().GetWorkItemDetail(item.Id)!;

        Assert.Equal("Failed", restored.Preview?.Status);
        Assert.Equal("ServerRestart", restored.Preview?.FailureReason);
        Assert.Contains(restored.Preview!.TerminalLines!, line => line.Message.Contains("interrupted", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Implementing_preview_is_checked_for_health_recovery()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var board = store.GetWorkspaces().SelectMany(workspace => store.GetBoards(workspace.Id)).First();
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "hamburgare", "Skapa en demo for tva hamburgare.", "Todo", "Medium", null));
        var run = store.StartAiPlan(item.Id, "codex", "gpt-5.4", "Implement two burger cards.")!;
        store.ApproveAiRun(run.Id, "crille");

        store.BeginPreviewImplementation(item.Id, "codex");

        var awaiting = store.GetPreviewsAwaitingHealthCheck();

        Assert.Contains(awaiting, preview => preview.WorkItemId == item.Id && preview.Status == "Implementing");
    }

    [Fact]
    public void Timed_out_preview_with_resources_is_checked_for_health_recovery()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var board = store.GetWorkspaces().SelectMany(workspace => store.GetBoards(workspace.Id)).First();
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "hamburgare", "Skapa en demo for tva hamburgare.", "Todo", "Medium", null));
        var run = store.StartAiPlan(item.Id, "codex", "gpt-5.4", "Implement two burger cards.")!;
        store.ApproveAiRun(run.Id, "crille");
        store.CompleteLocalReactImplementation(item.Id);

        store.UpdatePreviewHealth(item.Id, PreviewHealthCheckResult.Failed("Timeout", "Preview did not become healthy within 3 minutes.", null, null));

        var awaiting = store.GetPreviewsAwaitingHealthCheck();

        Assert.Contains(awaiting, preview => preview.WorkItemId == item.Id && preview.Status == "Failed" && preview.FailureReason == "Timeout");
    }

    [Fact]
    public void Approved_ai_plan_can_be_reimplemented_with_generated_source_files()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var board = store.GetWorkspaces().SelectMany(workspace => store.GetBoards(workspace.Id)).First();
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "rit app", "skapa en app dar jag kan rita", "Todo", "Medium", null));
        var run = store.StartAiPlan(item.Id, "ollama", "qwen3.5:latest", "Implement a canvas paint app.")!;
        store.ApproveAiRun(run.Id, "crille");
        store.CompleteLocalReactImplementation(item.Id);

        var approvedAgain = store.ApproveAiRun(run.Id, "crille");
        var generatedSource = LocalReactPreviewProject.ForWorkItem(item.Key, item.Title, "skapa en app dar jag kan rita")
            .Select(file => file.Path == "src/App.tsx"
                ? new PreviewSourceFile(file.Key, file.Path, "export default function App() { return <canvas aria-label=\"Rebuilt paint canvas\" />; }")
                : file)
            .ToArray();
        var detail = store.CompletePreviewImplementation(item.Id, generatedSource, "codex");
        var manifest = store.RenderPreviewManifest(item.Id);

        Assert.NotNull(approvedAgain);
        Assert.Equal(AiRunStatus.Approved, approvedAgain.Status);
        Assert.NotNull(detail?.Preview);
        Assert.Contains("Rebuilt paint canvas", manifest);
    }

    [Fact]
    public void Failed_preview_apply_keeps_preview_recoverable_without_running_status()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var board = store.GetWorkspaces().SelectMany(workspace => store.GetBoards(workspace.Id)).First();
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Preview failure", "Create React preview.", "Todo", "Medium", null));
        var run = store.StartAiPlan(item.Id, "ollama", "llama3:8b")!;
        store.ApproveAiRun(run.Id, "crille");
        store.CompleteLocalReactImplementation(item.Id);

        store.RecordPreviewFailure(item.Id, "ApplyFailed", "crille", "Preview orchestration failed: The pipe is being closed.");
        var failed = store.GetWorkItemDetail(item.Id)!;
        var failedSummary = store.GetBoard(board.Id)!.Columns.SelectMany(column => column.Items).Single(summary => summary.Id == item.Id);
        var retryManifest = store.RenderPreviewManifest(item.Id);
        var retried = store.UpdatePreviewHealth(item.Id, PreviewHealthCheckResult.Running("task-pod", "Deployment is available."))!;
        var runningSummary = store.GetBoard(board.Id)!.Columns.SelectMany(column => column.Items).Single(summary => summary.Id == item.Id);

        Assert.NotNull(failed.Preview);
        Assert.Equal("Failed", failed.Preview.Status);
        Assert.Null(failedSummary.PreviewUrl);
        Assert.NotNull(retryManifest);
        Assert.Contains("kind: Deployment", retryManifest);
        Assert.Equal("Running", retried.Preview?.Status);
        Assert.NotNull(runningSummary.PreviewUrl);
    }

    [Fact]
    public void Applied_preview_stays_provisioning_until_health_check_marks_running()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var board = store.GetWorkspaces().SelectMany(workspace => store.GetBoards(workspace.Id)).First();
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Preview readiness", "Create React preview.", "Todo", "Medium", null));
        var run = store.StartAiPlan(item.Id, "ollama", "llama3:8b")!;
        store.ApproveAiRun(run.Id, "crille");

        var detail = store.CompleteLocalReactImplementation(item.Id)!;
        var afterApply = store.MarkPreviewProvisioning(item.Id, "kubectl apply succeeded")!;
        var summary = store.GetBoard(board.Id)!.Columns.SelectMany(column => column.Items).Single(entry => entry.Id == item.Id);

        Assert.Equal("Implementing", detail.Preview?.Status);
        Assert.Equal("Provisioning", afterApply.Preview?.Status);
        Assert.Equal("Waiting for pod readiness.", afterApply.Preview?.Phase);
        Assert.Equal("kubectl apply succeeded", afterApply.Preview?.Message);
        Assert.Null(summary.PreviewUrl);
    }

    [Fact]
    public void Preview_health_check_can_mark_running_with_ready_pod()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var board = store.GetWorkspaces().SelectMany(workspace => store.GetBoards(workspace.Id)).First();
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Healthy preview", "Create React preview.", "Todo", "Medium", null));
        var run = store.StartAiPlan(item.Id, "ollama", "llama3:8b")!;
        store.ApproveAiRun(run.Id, "crille");
        store.CompleteLocalReactImplementation(item.Id);

        var updated = store.UpdatePreviewHealth(item.Id, PreviewHealthCheckResult.Running("preview-pod", "Deployment is healthy."))!;
        var summary = store.GetBoard(board.Id)!.Columns.SelectMany(column => column.Items).Single(entry => entry.Id == item.Id);

        Assert.Equal("Running", updated.Preview?.Status);
        Assert.Equal("Ready", updated.Preview?.Phase);
        Assert.Equal("preview-pod", updated.Preview?.PodName);
        Assert.NotNull(summary.PreviewUrl);
    }

    [Fact]
    public void Preview_health_check_can_mark_failed_with_reason_and_logs()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var board = store.GetWorkspaces().SelectMany(workspace => store.GetBoards(workspace.Id)).First();
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Broken preview", "Create React preview.", "Todo", "Medium", null));
        var run = store.StartAiPlan(item.Id, "ollama", "llama3:8b")!;
        store.ApproveAiRun(run.Id, "crille");
        store.CompleteLocalReactImplementation(item.Id);

        var updated = store.UpdatePreviewHealth(item.Id, PreviewHealthCheckResult.Failed("CrashLoopBackOff", "prepare-source failed.", "copy failed", "preview-pod"))!;
        var summary = store.GetBoard(board.Id)!.Columns.SelectMany(column => column.Items).Single(entry => entry.Id == item.Id);

        Assert.Equal("Failed", updated.Preview?.Status);
        Assert.Equal("CrashLoopBackOff", updated.Preview?.FailureReason);
        Assert.Equal("copy failed", updated.Preview?.FailureLog);
        Assert.Null(summary.PreviewUrl);
        Assert.NotNull(store.RenderPreviewManifest(item.Id));
    }

    [Fact]
    public void Preview_health_check_timeout_marks_failed_and_keeps_manifest()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var board = store.GetWorkspaces().SelectMany(workspace => store.GetBoards(workspace.Id)).First();
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Slow preview", "Create React preview.", "Todo", "Medium", null));
        var run = store.StartAiPlan(item.Id, "ollama", "llama3:8b")!;
        store.ApproveAiRun(run.Id, "crille");
        store.CompleteLocalReactImplementation(item.Id);

        var updated = store.UpdatePreviewHealth(item.Id, PreviewHealthCheckResult.Failed("Timeout", "Preview did not become healthy within 3 minutes.", null, null))!;

        Assert.Equal("Failed", updated.Preview?.Status);
        Assert.Equal("Timeout", updated.Preview?.FailureReason);
        Assert.NotNull(store.RenderPreviewManifest(item.Id));
    }

    [Fact]
    public void Local_react_project_preserves_simple_color_requirements()
    {
        var files = LocalReactPreviewProject.ForWorkItem("TASK-1", "test sida", "gor en hello-world hemsida med orange text och gul bakgrund");
        var app = files.Single(file => file.Path == "src/App.tsx").Content;

        Assert.Contains("text-[#ff8a00]", app);
        Assert.Contains("bg-[#ffd84d]", app);
    }

    [Fact]
    public void Local_react_project_allows_dynamic_rosenvall_preview_hosts()
    {
        var files = LocalReactPreviewProject.ForWorkItem("TASK-4825", "rita app", "skapa en app dar jag kan rita");
        var viteConfig = files.Single(file => file.Path == "vite.config.ts").Content;

        Assert.Contains("allowedHosts", viteConfig);
        Assert.Contains(".rosenvall.se", viteConfig);
        Assert.DoesNotContain("allowedHosts: true", viteConfig);
    }

    [Fact]
    public void Local_react_implementation_uses_human_comments_for_richer_preview()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var board = store.GetWorkspaces().SelectMany(workspace => store.GetBoards(workspace.Id)).First();
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Epic", "gör en sida som säljer hamburgare", "Implement a grey demo homepage for burger ordering.", "Todo", "Medium", null));
        store.AddComment(item.Id, "crille", "Comment", "jag tycker man ska kunna välja hur många hamburgare man vill ha av varje typ och klicka på en beställ knapp.");
        var run = store.StartAiPlan(item.Id, "ollama", "qwen3.5:latest", "Plan with burger quantities and order button.")!;
        store.ApproveAiRun(run.Id, "crille");

        var detail = store.CompleteLocalReactImplementation(item.Id);
        var manifest = store.RenderPreviewManifest(item.Id);

        Assert.NotNull(detail?.Preview);
        Assert.Null(detail.Preview.StaticHtml);
        Assert.Contains("Cheeseburgare", manifest);
        Assert.Contains("Fiskburgare", manifest);
        Assert.Contains("Beställ", manifest);
        Assert.Contains("useState", manifest);
    }

    [Fact]
    public async Task Ollama_unavailable_throws_without_generating_fallback_plan()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var board = store.GetWorkspaces().SelectMany(workspace => store.GetBoards(workspace.Id)).First();
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Needs real plan", "Use the exact card requirements.", "Todo", "Medium", null));
        var context = store.GetWorkItemDetail(item.Id)!;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Ai:OllamaEndpoint"] = "http://ollama.local/api" })
            .Build();
        var provider = new OllamaPlanProvider(new HttpClient(new StaticHttpHandler(HttpStatusCode.ServiceUnavailable, "{}")), configuration);

        await Assert.ThrowsAnyAsync<AiPlanProviderUnavailableException>(() => provider.GeneratePlanAsync("llama3:8b", context, CancellationToken.None));

        Assert.Empty(store.GetAiRuns(item.Id));
    }

    [Fact]
    public async Task Ai_plan_router_uses_requested_provider_and_rejects_unknown_provider()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var board = fixture.Store.GetWorkspaces().SelectMany(workspace => fixture.Store.GetBoards(workspace.Id)).First();
        var item = fixture.Store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Provider routing", "Pick the configured provider.", "Todo", "Medium", null));
        var context = fixture.Store.GetWorkItemDetail(item.Id)!;
        var ollama = new FakePlanProvider("ollama", "Ollama plan.");
        var codex = new FakePlanProvider("codex", "Codex plan.");
        var router = new AiPlanProviderRouter([ollama, codex]);

        var codexPlan = await router.GeneratePlanAsync("codex", "gpt-5.4", context, CancellationToken.None);
        var ollamaPlan = await router.GeneratePlanAsync("ollama", "qwen3.5:latest", context, CancellationToken.None);
        var unknown = await Assert.ThrowsAsync<AiPlanProviderUnavailableException>(() => router.GeneratePlanAsync("gemini", "gemini-pro", context, CancellationToken.None));

        Assert.Equal("Codex plan.", codexPlan);
        Assert.Equal("Ollama plan.", ollamaPlan);
        Assert.Equal("gpt-5.4", codex.LastModel);
        Assert.Equal("qwen3.5:latest", ollama.LastModel);
        Assert.Contains("Provider 'gemini' is not configured", unknown.Message);
    }

    [Fact]
    public async Task Codex_cli_provider_reads_last_message_from_configured_executable()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var board = fixture.Store.GetWorkspaces().SelectMany(workspace => fixture.Store.GetBoards(workspace.Id)).First();
        var item = fixture.Store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Codex plan", "Use server-side Codex.", "Todo", "Medium", null));
        var context = fixture.Store.GetWorkItemDetail(item.Id)!;
        var fakeCodex = CreateFakeCodexScript(exitCode: 0, plan: "Plan from fake Codex.");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:Codex:Path"] = fakeCodex,
                ["Ai:Codex:Home"] = Path.Combine(Path.GetTempPath(), $"codex-home-{Guid.NewGuid():N}")
            })
            .Build();
        var provider = new CodexCliPlanProvider(configuration, NullLogger<CodexCliPlanProvider>.Instance);

        var plan = await provider.GeneratePlanAsync("gpt-5.4", context, CancellationToken.None);

        Assert.Equal("Plan from fake Codex.", plan);
    }

    [Fact]
    public async Task Codex_cli_provider_skips_git_repo_check_for_server_workspace()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var board = fixture.Store.GetWorkspaces().SelectMany(workspace => fixture.Store.GetBoards(workspace.Id)).First();
        var item = fixture.Store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Codex plan", "Use server-side Codex outside git.", "Todo", "Medium", null));
        var context = fixture.Store.GetWorkItemDetail(item.Id)!;
        var fakeCodex = CreateFakeCodexScript(exitCode: 0, plan: "Plan from non-git workspace.", requireSkipGitRepoCheck: true);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:Codex:Path"] = fakeCodex,
                ["Ai:Codex:Home"] = Path.Combine(Path.GetTempPath(), $"codex-home-{Guid.NewGuid():N}")
            })
            .Build();
        var provider = new CodexCliPlanProvider(configuration, NullLogger<CodexCliPlanProvider>.Instance);

        var plan = await provider.GeneratePlanAsync("gpt-5.4", context, CancellationToken.None);

        Assert.Equal("Plan from non-git workspace.", plan);
    }

    [Fact]
    public void Codex_executable_resolver_prefers_windows_cmd_shim_over_extensionless_file()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), $"codex-path-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            File.WriteAllText(Path.Combine(directory, "codex"), "not executable");
            File.WriteAllText(Path.Combine(directory, "codex.cmd"), "@echo off");
            Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + originalPath);

            var resolved = CodexExecutableResolver.Resolve("codex");

            Assert.EndsWith("codex.cmd", resolved, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Codex_preview_source_provider_returns_modified_preview_files()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var board = fixture.Store.GetWorkspaces().SelectMany(workspace => fixture.Store.GetBoards(workspace.Id)).First();
        var item = fixture.Store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "rit app", "skapa en app dar jag kan rita", "Todo", "Medium", null));
        var run = fixture.Store.StartAiPlan(item.Id, "ollama", "qwen3.5:latest", "Implement canvas drawing.")!;
        run.Approve("crille");
        var context = fixture.Store.GetWorkItemDetail(item.Id)!;
        var fakeCodex = CreateFakeCodexImplementationScript(exitCode: 0, appSource: "export default function App() { return <canvas aria-label=\"AI paint canvas\" />; }");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:Codex:Path"] = fakeCodex,
                ["Ai:Codex:Home"] = Path.Combine(Path.GetTempPath(), $"codex-home-{Guid.NewGuid():N}")
            })
            .Build();
        var provider = new CodexCliPreviewSourceProvider(configuration, NullLogger<CodexCliPreviewSourceProvider>.Instance);

        var files = await provider.GenerateSourceAsync("gpt-5.4", run, context, null, CancellationToken.None);

        Assert.Contains(files, file => file.Path == "src/App.tsx" && file.Content.Contains("AI paint canvas", StringComparison.Ordinal));
        Assert.Contains(files, file => file.Path == "vite.config.ts" && file.Content.Contains("allowedHosts", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Codex_preview_source_provider_rejects_unchanged_seed_placeholder()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var board = fixture.Store.GetWorkspaces().SelectMany(workspace => fixture.Store.GetBoards(workspace.Id)).First();
        var item = fixture.Store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "cv hemsida", "skapa en hemsida som erbjuder CV mallar", "Todo", "High", null));
        var run = fixture.Store.StartAiPlan(item.Id, "codex", "gpt-5.4", "Build a CV template editor.")!;
        run.Approve("crille");
        var context = fixture.Store.GetWorkItemDetail(item.Id)!;
        var fakeCodex = CreateFakeCodexNoopScript(exitCode: 0);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:Codex:Path"] = fakeCodex,
                ["Ai:Codex:Home"] = Path.Combine(Path.GetTempPath(), $"codex-home-{Guid.NewGuid():N}")
            })
            .Build();
        var provider = new CodexCliPreviewSourceProvider(configuration, NullLogger<CodexCliPreviewSourceProvider>.Instance);

        var error = await Assert.ThrowsAsync<AiPlanProviderUnavailableException>(() => provider.GenerateSourceAsync("gpt-5.4", run, context, null, CancellationToken.None));

        Assert.Contains("seeded placeholder app unchanged", error.Message);
    }

    [Fact]
    public async Task Codex_cli_provider_reports_login_required_without_creating_a_plan()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var board = fixture.Store.GetWorkspaces().SelectMany(workspace => fixture.Store.GetBoards(workspace.Id)).First();
        var item = fixture.Store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Codex login", "Require server login.", "Todo", "Medium", null));
        var context = fixture.Store.GetWorkItemDetail(item.Id)!;
        var fakeCodex = CreateFakeCodexScript(exitCode: 1, plan: "not logged in");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Ai:Codex:Path"] = fakeCodex })
            .Build();
        var provider = new CodexCliPlanProvider(configuration, NullLogger<CodexCliPlanProvider>.Instance);

        var error = await Assert.ThrowsAsync<AiPlanProviderUnavailableException>(() => provider.GeneratePlanAsync("gpt-5.4", context, CancellationToken.None));

        Assert.Contains("Codex provider is not logged in on the server", error.Message);
        Assert.Empty(fixture.Store.GetAiRuns(item.Id));
    }

    [Fact]
    public void Settings_exposes_configured_ai_model_choices()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:DefaultModel"] = "qwen3.5:latest",
                ["Ai:AvailableModels:0"] = "llama3.1:8b",
                ["Ai:AvailableModels:1"] = "qwen3.5:latest",
                ["Ai:AvailableModels:2"] = " ",
                ["Ai:AvailableModels:3"] = "llama3.1:8b",
                ["Ai:Codex:Model"] = "gpt-5.4",
                ["Ai:Codex:AvailableModels:0"] = "gpt-5.4",
                ["Ai:Codex:AvailableModels:1"] = "gpt-5.3-codex"
            })
            .Build();

        var settings = fixture.Store.GetSettings(configuration);

        Assert.Equal("qwen3.5:latest", settings.Ai.ActiveModel);
        Assert.Equal(["qwen3.5:latest", "llama3.1:8b"], settings.Ai.AvailableModels);
        Assert.Contains(settings.Ai.AvailableProviders, provider => provider.Provider == "ollama" && provider.Status == "Ready");
        Assert.Contains(settings.Ai.AvailableProviders, provider => provider.Provider == "codex" && provider.ActiveModel == "gpt-5.4");
    }

    [Fact]
    public void Pull_request_can_be_approved_by_human()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var board = store.GetWorkspaces().SelectMany(workspace => store.GetBoards(workspace.Id)).First();
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "PR approval", "Approve generated PR.", "Review", "Medium", null));
        store.ApplyGitHubCallback(new GitHubCallbackRequest(item.Id, "rosenvall/demo", "feat/pr-approval", "https://github.com/rosenvall/demo/pull/7", "ghcr.io/rosenvall/demo:pr-approval", "Checks passed"));

        var approved = store.ApprovePullRequest(item.Id, "crille");

        Assert.NotNull(approved);
        Assert.Equal("Done", approved.Item.Status);
        Assert.Equal("crille", approved.Development?.PullRequestApprovedBy);
        Assert.Equal("PR approved by crille", approved.Development?.ChecksStatus);
        Assert.Equal("Stopped", approved.Preview?.Status);
        Assert.Contains(approved.Comments, comment => comment.Body.Contains("Pull request approved", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(store.GetPreviewEvents(), entry => entry.WorkItemId == item.Id && entry.EventType == "PrApproved");
        Assert.Contains(store.GetPreviewEvents(), entry => entry.WorkItemId == item.Id && entry.EventType == "Stopped");
    }

    [Fact]
    public void Preview_can_be_stopped_and_started_again_with_history()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var board = store.GetWorkspaces().SelectMany(workspace => store.GetBoards(workspace.Id)).First();
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Restart preview", "Create React preview.", "Todo", "Medium", null));
        var run = store.StartAiPlan(item.Id, "ollama", "llama3:8b")!;
        store.ApproveAiRun(run.Id, "crille");
        store.CompleteLocalReactImplementation(item.Id);

        var stopped = store.StopPreview(item.Id, "crille", "Manual stop")!;
        var restarted = store.MarkPreviewRunning(item.Id, "crille", "Manual start")!;

        Assert.Equal("Stopped", stopped.Preview?.Status);
        Assert.Equal("Running", restarted.Preview?.Status);
        Assert.Contains(store.GetPreviewEvents(), entry => entry.WorkItemId == item.Id && entry.EventType == "Stopped");
        Assert.Contains(store.GetPreviewEvents(), entry => entry.WorkItemId == item.Id && entry.EventType == "Started");
    }

    private sealed class DevOpsStoreFixture : IDisposable
    {
        private readonly string _databasePath;

        private DevOpsStoreFixture(string databasePath, DevOpsStore store)
        {
            _databasePath = databasePath;
            Store = store;
        }

        public DevOpsStore Store { get; }

        public DevOpsStore Reopen()
        {
            var options = new DbContextOptionsBuilder<DevOpsStateDbContext>()
                .UseSqlite($"Data Source={_databasePath}")
                .Options;
            return new DevOpsStore(new TestDbContextFactory(options));
        }

        public static DevOpsStoreFixture Create()
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"devops-store-{Guid.NewGuid():N}.db");
            var options = new DbContextOptionsBuilder<DevOpsStateDbContext>()
                .UseSqlite($"Data Source={databasePath}")
                .Options;
            var factory = new TestDbContextFactory(options);
            using (var db = factory.CreateDbContext())
            {
                db.Database.EnsureCreated();
            }

            return new DevOpsStoreFixture(databasePath, new DevOpsStore(factory));
        }

        public void Dispose()
        {
            try
            {
                File.Delete(_databasePath);
            }
            catch
            {
                // Best-effort cleanup for temp SQLite files used by tests.
            }
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<DevOpsStateDbContext> options) : IDbContextFactory<DevOpsStateDbContext>
    {
        public DevOpsStateDbContext CreateDbContext() => new(options);
    }

    private sealed class StaticHttpHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body)
            });
    }

    private sealed class FakePlanProvider(string providerName, string plan) : IAiPlanProvider
    {
        public string ProviderName => providerName;

        public string? LastModel { get; private set; }

        public Task<string> GeneratePlanAsync(string model, WorkItemDetailDto context, CancellationToken cancellationToken)
        {
            LastModel = model;
            return Task.FromResult(plan);
        }
    }

    private static string CreateFakeCodexScript(int exitCode, string plan, bool requireSkipGitRepoCheck = false)
    {
        if (!OperatingSystem.IsWindows())
        {
            var unixScriptPath = Path.Combine(Path.GetTempPath(), $"fake-codex-{Guid.NewGuid():N}.sh");
            var escapedPlan = plan.Replace("'", "'\"'\"'", StringComparison.Ordinal);
            var requiredSkipCheck = requireSkipGitRepoCheck ? "1" : "0";
            File.WriteAllText(unixScriptPath, $$"""
#!/usr/bin/env sh
last=""
has_skip=0
while [ "$#" -gt 0 ]; do
  if [ "$1" = "--skip-git-repo-check" ]; then
    has_skip=1
  fi
  if [ "$1" = "--output-last-message" ]; then
    shift
    last="$1"
    break
  fi
  shift
done
if [ "{{requiredSkipCheck}}" = "1" ] && [ "$has_skip" != "1" ]; then
  printf '%s\n' 'Not inside a trusted directory and --skip-git-repo-check was not specified.' >&2
  exit 1
fi
if [ -n "$last" ]; then printf '%s\n' '{{escapedPlan}}' > "$last"; fi
exit {{exitCode}}
""");
            File.SetUnixFileMode(unixScriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return unixScriptPath;
        }

        var scriptPath = Path.Combine(Path.GetTempPath(), $"fake-codex-{Guid.NewGuid():N}.cmd");
        var requiredSkipCheckWindows = requireSkipGitRepoCheck ? "1" : "0";
        File.WriteAllText(scriptPath, $"""
@echo off
set last=
set hasSkip=0
:loop
if "%~1"=="" goto done
if "%~1"=="--skip-git-repo-check" set hasSkip=1
if "%~1"=="--output-last-message" goto found
shift
goto loop
:found
shift
set "last=%~1"
:done
if "{requiredSkipCheckWindows}"=="1" if not "%hasSkip%"=="1" (
  echo Not inside a trusted directory and --skip-git-repo-check was not specified. 1>&2
  exit /b 1
)
if defined last echo {plan}> "%last%"
exit /b {exitCode}
""");
        return scriptPath;
    }

    private static string CreateFakeCodexImplementationScript(int exitCode, string appSource)
    {
        if (!OperatingSystem.IsWindows())
        {
            var unixScriptPath = Path.Combine(Path.GetTempPath(), $"fake-codex-implementation-{Guid.NewGuid():N}.sh");
            var escapedAppSource = appSource.Replace("'", "'\"'\"'", StringComparison.Ordinal);
            File.WriteAllText(unixScriptPath, $$"""
#!/usr/bin/env sh
mkdir -p src
printf '%s\n' '{{escapedAppSource}}' > src/App.tsx
exit {{exitCode}}
""");
            File.SetUnixFileMode(unixScriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return unixScriptPath;
        }

        var scriptPath = Path.Combine(Path.GetTempPath(), $"fake-codex-implementation-{Guid.NewGuid():N}.cmd");
        var escapedSource = appSource
            .Replace("^", "^^", StringComparison.Ordinal)
            .Replace("&", "^&", StringComparison.Ordinal)
            .Replace("<", "^<", StringComparison.Ordinal)
            .Replace(">", "^>", StringComparison.Ordinal)
            .Replace("|", "^|", StringComparison.Ordinal);
        File.WriteAllText(scriptPath, $"""
@echo off
if not exist src mkdir src
> src\App.tsx echo {escapedSource}
exit /b {exitCode}
""");
        return scriptPath;
    }

    private static string CreateFakeCodexNoopScript(int exitCode)
    {
        if (!OperatingSystem.IsWindows())
        {
            var unixScriptPath = Path.Combine(Path.GetTempPath(), $"fake-codex-noop-{Guid.NewGuid():N}.sh");
            File.WriteAllText(unixScriptPath, $$"""
#!/usr/bin/env sh
exit {{exitCode}}
""");
            File.SetUnixFileMode(unixScriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return unixScriptPath;
        }

        var scriptPath = Path.Combine(Path.GetTempPath(), $"fake-codex-noop-{Guid.NewGuid():N}.cmd");
        File.WriteAllText(scriptPath, $"""
@echo off
exit /b {exitCode}
""");
        return scriptPath;
    }
}
