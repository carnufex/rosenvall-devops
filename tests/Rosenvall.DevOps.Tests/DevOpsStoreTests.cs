using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
        Assert.Contains(store.GetWorkItemDetail(item.Id)!.Comments, comment => comment.Kind == "Plan" && comment.Body == "Provider generated React/Tailwind plan.");
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
        Assert.Contains("React/Tailwind preview", manifest);
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

        await Assert.ThrowsAsync<OllamaUnavailableException>(() => provider.GeneratePlanAsync("ollama", "llama3:8b", context, CancellationToken.None));

        Assert.Empty(store.GetAiRuns(item.Id));
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
                ["Ai:AvailableModels:3"] = "llama3.1:8b"
            })
            .Build();

        var settings = fixture.Store.GetSettings(configuration);

        Assert.Equal("qwen3.5:latest", settings.Ai.ActiveModel);
        Assert.Equal(["qwen3.5:latest", "llama3.1:8b"], settings.Ai.AvailableModels);
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
}
