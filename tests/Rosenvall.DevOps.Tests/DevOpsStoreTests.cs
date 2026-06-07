using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Rosenvall.DevOps.Api;
using Rosenvall.DevOps.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

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
        Assert.Equal("Task", updated.Type);
        Assert.True(updated.IsBug);
        Assert.Equal("High", updated.Priority);
        Assert.Equal("Crille", updated.Assignee);
        Assert.Equal("In Progress", moved.Status);
        Assert.Equal(item.Id, refreshedBoard.Columns.Single(column => column.Name == "In Progress").Items.First().Id);
    }

    [Fact]
    public void Work_items_support_hierarchy_and_bug_marker()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var board = store.GetWorkspaces().SelectMany(workspace => store.GetBoards(workspace.Id)).First();
        var epic = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Epic", "Build planner", "Coordinate child work.", "Todo", "High", null))!;
        var feature = store.CreateChildWorkItem(epic.Id, new CreateChildWorkItemRequest("Feature", "Plan review", "Review plans in RDO.", "Todo", "Medium", null, false))!;
        var bug = store.CreateChildWorkItem(feature.Id, new CreateChildWorkItemRequest("Task", "Fix duplicate activity", "Created plan appears twice.", "Todo", "High", null, true))!;

        var detail = store.GetWorkItemDetail(epic.Id)!;
        var bugDetail = store.GetWorkItemDetail(bug.Id)!;

        Assert.Equal("Epic", epic.Type);
        Assert.False(epic.IsBug);
        Assert.Equal(epic.Id, feature.ParentWorkItemId);
        Assert.Equal(epic.Id, feature.RootWorkItemId);
        Assert.Equal(feature.Id, bug.ParentWorkItemId);
        Assert.Equal(epic.Id, bug.RootWorkItemId);
        Assert.True(bug.IsBug);
        Assert.Equal("Task", bug.Type);
        Assert.Single(detail.Children);
        Assert.Equal(feature.Id, detail.Children[0].Id);
        Assert.Contains("Build planner", bugDetail.Item.HierarchyPath);
        Assert.Equal(2, detail.Item.ChildCount);
        Assert.Equal(0, detail.Item.DoneChildCount);
    }

    [Fact]
    public void Work_item_hierarchy_rejects_cross_board_parent_cycles_and_invalid_levels()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var board = store.GetBoards(workspace.Id).First();
        var otherBoard = store.CreateBoard(workspace.Id, new CreateBoardRequest("Other board", null, null, null, null, null, null))!;
        var epic = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Epic", "Epic", "Root.", "Todo", "Medium", null))!;
        var feature = store.CreateChildWorkItem(epic.Id, new CreateChildWorkItemRequest("Feature", "Feature", "Child.", "Todo", "Medium", null, false))!;
        var task = store.CreateChildWorkItem(feature.Id, new CreateChildWorkItemRequest("Task", "Task", "Leaf.", "Todo", "Medium", null, false))!;
        var otherEpic = store.CreateWorkItem(new CreateWorkItemRequest(otherBoard.Id, "Epic", "Other", "Root.", "Todo", "Medium", null))!;

        Assert.Null(store.UpdateWorkItemHierarchy(epic.Id, new UpdateWorkItemHierarchyRequest(task.Id)));
        Assert.Null(store.UpdateWorkItemHierarchy(feature.Id, new UpdateWorkItemHierarchyRequest(otherEpic.Id)));
        Assert.Null(store.CreateChildWorkItem(task.Id, new CreateChildWorkItemRequest("Feature", "Invalid feature", "Cannot nest feature under task.", "Todo", "Medium", null, false)));
        Assert.Null(store.UpdateWorkItem(epic.Id, new UpdateWorkItemRequest("Epic", "Root.", "Task", "Todo", "Medium", null, null, false)));
    }

    [Fact]
    public void Legacy_bug_work_items_are_restored_as_task_bug_marker()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var board = store.GetWorkspaces().SelectMany(workspace => store.GetBoards(workspace.Id)).First();
        var legacy = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Bug", "Legacy bug", "Old type.", "Todo", "High", null))!;

        var reopened = fixture.Reopen();
        var restored = reopened.GetWorkItemDetail(legacy.Id)!.Item;

        Assert.Equal("Task", restored.Type);
        Assert.True(restored.IsBug);
    }

    [Fact]
    public void Epic_run_and_goal_track_child_feature_agents()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var board = store.GetWorkspaces().SelectMany(workspace => store.GetBoards(workspace.Id)).First();
        var epic = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Epic", "Redesign RDO", "Coordinate a larger product change.", "Todo", "High", null))!;
        var featureA = store.CreateChildWorkItem(epic.Id, new CreateChildWorkItemRequest("Feature", "Hierarchy", "Add parent/child cards.", "Todo", "High", null))!;
        var featureB = store.CreateChildWorkItem(epic.Id, new CreateChildWorkItemRequest("Feature", "Goal runner", "Drive the epic.", "Todo", "Medium", null))!;
        store.CreateChildWorkItem(featureA.Id, new CreateChildWorkItemRequest("Task", "Backend", "Add APIs.", "Todo", "Medium", null));

        var run = store.StartEpicRun(epic.Id, "crille")!;
        var goal = store.StartEpicGoal(epic.Id, "crille")!;

        Assert.Equal(epic.Id, run.RootWorkItemId);
        Assert.Equal("Running", run.Status);
        Assert.Equal(2, run.Children.Count);
        Assert.Contains(run.Children, child => child.WorkItemId == featureA.Id && child.AgentRole == "Feature agent");
        Assert.Contains(run.Children, child => child.WorkItemId == featureB.Id && child.Status == "Queued");
        Assert.Equal(run.Id, goal.EpicRunId);
        Assert.Equal("Running", goal.Status);

        var cancelled = store.CancelEpicGoal(goal.Id, "crille")!;
        Assert.Equal("Cancelled", cancelled.Status);
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
    public void Work_item_cleanup_manifest_is_scoped_to_the_deleted_card()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest("GitHub", "rosenvalls-homelab", "https://github.com/carnufex/Rosenvalls-Homelab.git", "master", "https://github.com/carnufex/Rosenvalls-Homelab", "carnufex", "gitops-homelab"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("Homelab", repository.Id, null, null, null, null, null, ImplementationProfile: "gitops-homelab"))!;
        var deletedItem = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "delete me", "Create a preview and runner.", "Todo", "Medium", null));
        var keptItem = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "keep me", "Keep this card alive.", "Todo", "Medium", null));
        var deletedPlan = store.StartAiPlan(deletedItem.Id, "codex", "gpt-5.5", "Plan deleted card.")!;
        var keptPlan = store.StartAiPlan(keptItem.Id, "codex", "gpt-5.5", "Plan kept card.")!;
        store.ApproveAiRun(deletedPlan.Id, "crille");
        store.CompleteLocalReactImplementation(deletedItem.Id);
        var deletedRun = store.StartImplementationRun(deletedItem.Id, new StartImplementationRunRequest(deletedPlan.Id, "crille", repository.Id))!;
        var keptRun = store.StartImplementationRun(keptItem.Id, new StartImplementationRunRequest(keptPlan.Id, "crille", repository.Id))!;
        var deletedPipeline = store.RecordPipelineRun(new RecordPipelineRunRequest(repository.Id, board.Id, deletedItem.Id, "Build", "Queued", "Build deleted card", null))!;
        var keptPipeline = store.RecordPipelineRun(new RecordPipelineRunRequest(repository.Id, board.Id, keptItem.Id, "Build", "Queued", "Build kept card", null))!;

        var manifest = store.RenderWorkItemCleanupManifest(deletedItem.Id);

        Assert.NotNull(manifest);
        Assert.Contains("devops-preview-task-", manifest);
        Assert.Contains(RepositoryImplementationJobManifestRenderer.JobName(deletedRun, store.GetWorkItemDetail(deletedItem.Id)!), manifest);
        Assert.Contains(RepositoryImplementationJobManifestRenderer.GitHubTokenSecretName(deletedRun), manifest);
        Assert.Contains(PipelineJobManifestRenderer.JobName(deletedPipeline, repository), manifest);
        Assert.DoesNotContain(RepositoryImplementationJobManifestRenderer.JobName(keptRun, store.GetWorkItemDetail(keptItem.Id)!), manifest);
        Assert.DoesNotContain(RepositoryImplementationJobManifestRenderer.GitHubTokenSecretName(keptRun), manifest);
        Assert.DoesNotContain(PipelineJobManifestRenderer.JobName(keptPipeline, repository), manifest);
        Assert.DoesNotContain("rosenvall-devops-codex-home", manifest);
    }

    [Fact]
    public void Work_item_cleanup_manifest_includes_provider_sync_job_and_token_secret()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest("LocalGit", "demo-app", "http://forgejo/rdo/demo-app.git", "main", null, "rdo", "react-preview"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("Provider sync cleanup", repository.Id, null, null, null, null, null))!;
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Delete sync", "Remove provider sync runtime.", "Todo", "Medium", null));
        var providerSync = store.RecordPipelineRun(new RecordPipelineRunRequest(repository.Id, board.Id, item.Id, "ProviderSync", "Running", "Syncing repository.", null))!;
        var genericPipeline = store.RecordPipelineRun(new RecordPipelineRunRequest(repository.Id, board.Id, item.Id, "Build", "Running", "Running legacy pipeline.", null))!;

        var manifest = store.RenderWorkItemCleanupManifest(item.Id)!;

        Assert.Contains(RepositoryProviderSyncJobManifestRenderer.JobName(providerSync), manifest);
        Assert.Contains(RepositoryProviderSyncJobManifestRenderer.TokenSecretName(providerSync), manifest);
        Assert.Contains(PipelineJobManifestRenderer.JobName(genericPipeline, repository), manifest);
        Assert.DoesNotContain(PipelineJobManifestRenderer.JobName(providerSync, repository), manifest);
    }

    [Fact]
    public void Provider_sync_token_secret_payload_has_metadata_labels_and_data()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest("LocalGit", "demo-app", "http://forgejo/rdo/demo-app.git", "main", null, "rdo", "react-preview"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("Provider sync", repository.Id, null, null, null, null, null))!;
        var run = store.RecordPipelineRun(new RecordPipelineRunRequest(repository.Id, board.Id, null, "ProviderSync", "Queued", "Syncing repository.", null))!;

        var payload = RepositoryProviderSyncJobManifestRenderer.RenderTokenSecret(run, "source-token", "target-token");
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var metadata = root.GetProperty("metadata");
        var labels = metadata.GetProperty("labels");
        var data = root.GetProperty("data");

        Assert.Equal("v1", root.GetProperty("apiVersion").GetString());
        Assert.Equal("Secret", root.GetProperty("kind").GetString());
        Assert.Equal(RepositoryProviderSyncJobManifestRenderer.TokenSecretName(run), metadata.GetProperty("name").GetString());
        Assert.Equal(RepositoryImplementationJobManifestRenderer.Namespace, metadata.GetProperty("namespace").GetString());
        Assert.Equal("rosenvall-devops-provider-sync", labels.GetProperty("app.kubernetes.io/part-of").GetString());
        Assert.Equal(run.Id.ToString(), labels.GetProperty("rosenvall.devops/pipeline-run").GetString());
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("source-token")), data.GetProperty("source-token").GetString());
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("target-token")), data.GetProperty("target-token").GetString());
        Assert.DoesNotContain("stringData", payload);
        Assert.DoesNotContain("kubectl.kubernetes.io/last-applied-configuration", payload);
    }

    [Fact]
    public void Deleting_work_item_removes_only_that_cards_runtime_runs()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest("GitHub", "rosenvalls-homelab", "https://github.com/carnufex/Rosenvalls-Homelab.git", "master", "https://github.com/carnufex/Rosenvalls-Homelab", "carnufex", "gitops-homelab"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("Homelab", repository.Id, null, null, null, null, null, ImplementationProfile: "gitops-homelab"))!;
        var deletedItem = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "delete me", "Create a runner.", "Todo", "Medium", null));
        var keptItem = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "keep me", "Keep this card alive.", "Todo", "Medium", null));
        var deletedPlan = store.StartAiPlan(deletedItem.Id, "codex", "gpt-5.5", "Plan deleted card.")!;
        var keptPlan = store.StartAiPlan(keptItem.Id, "codex", "gpt-5.5", "Plan kept card.")!;
        store.StartImplementationRun(deletedItem.Id, new StartImplementationRunRequest(deletedPlan.Id, "crille", repository.Id));
        var keptRun = store.StartImplementationRun(keptItem.Id, new StartImplementationRunRequest(keptPlan.Id, "crille", repository.Id))!;
        store.RecordPipelineRun(new RecordPipelineRunRequest(repository.Id, board.Id, deletedItem.Id, "Build", "Queued", "Build deleted card", null));
        var keptPipeline = store.RecordPipelineRun(new RecordPipelineRunRequest(repository.Id, board.Id, keptItem.Id, "Build", "Queued", "Build kept card", null))!;

        Assert.True(store.DeleteWorkItem(deletedItem.Id, "crille"));

        Assert.Empty(store.GetImplementationRuns(deletedItem.Id));
        Assert.Contains(store.GetImplementationRuns(keptItem.Id), run => run.Id == keptRun.Id);
        Assert.DoesNotContain(store.GetPipelineStatuses(), run => run.WorkItemId == deletedItem.Id);
        Assert.Contains(store.GetPipelineStatuses(), run => run.Id == keptPipeline.Id && run.WorkItemId == keptItem.Id);
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
    public void Website_board_uses_preview_then_pr_workflow_and_public_hostname()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest("GitHub", "clock-app", "https://github.com/carnufex/clock-app.git", "main", "https://github.com/carnufex/clock-app", "carnufex", "react-preview"));

        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("Demo Klocka", repository.Id, null, null, null, null, null, ImplementationProfile: "react-preview"))!;

        Assert.Equal("preview-then-pr", board.ImplementationWorkflow);
        Assert.Equal("demo-klocka.rosenvall.se", board.PublicHostname);
        Assert.Contains("preview", board.ProviderCapabilities ?? []);
    }

    [Fact]
    public void Existing_previewable_board_backfills_workflow_and_public_hostname_from_board_name()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest("GitHub", "clock-app", "https://github.com/carnufex/clock-app.git", "main", "https://github.com/carnufex/clock-app", "carnufex", "react-preview"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("Demo Klocka", repository.Id, null, null, null, null, null, ImplementationProfile: "react-preview"))!;
        fixture.UpdateSnapshotJson(json =>
        {
            var root = JsonNode.Parse(json)!.AsObject();
            var boardNode = root["boards"]!.AsArray().Single(node =>
                string.Equals(node?["id"]?.GetValue<string>(), board.Id.ToString(), StringComparison.OrdinalIgnoreCase))!.AsObject();
            boardNode["publicHostname"] = null;
            boardNode["implementationWorkflow"] = "";
            return root.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        });

        var reopened = fixture.Reopen();
        var upgraded = reopened.GetBoard(board.Id)!;

        Assert.Equal("preview-then-pr", upgraded.ImplementationWorkflow);
        Assert.Equal("demo-klocka.rosenvall.se", upgraded.PublicHostname);
        Assert.Contains("preview", upgraded.ProviderCapabilities ?? []);
    }

    [Fact]
    public void Approving_preview_for_pr_creates_preview_promotion_run_without_codex()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest("GitHub", "clock-app", "https://github.com/carnufex/clock-app.git", "main", "https://github.com/carnufex/clock-app", "carnufex", "react-preview"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("Clock app", repository.Id, null, null, null, null, null, ImplementationProfile: "react-preview"))!;
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Clock toggle", "Build a web clock.", "Todo", "Medium", null));
        var aiRun = store.StartAiPlan(item.Id, "codex", "gpt-5.5", "Build preview.")!;
        store.ApproveAiRun(aiRun.Id, "crille");
        store.CompletePreviewImplementation(item.Id, [
            new PreviewSourceFile("index", "index.html", "<div id=\"root\"></div>"),
            new PreviewSourceFile("app", "src/App.tsx", "export default function App(){return <main>Clock</main>}")
        ], "codex");
        store.MarkPreviewRunning(item.Id, "test", "Preview is running.");

        var run = store.StartPreviewPromotionRun(item.Id, "crille")!;
        var manifest = store.RenderImplementationRunManifest(run.Id, new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:Codex:KubernetesRunnerImage"] = "ghcr.io/carnufex/rosenvall-devops-api@sha256:promotiondigest"
            })
            .Build(), "github-token")!;

        Assert.Equal("preview-promotion", run.RunKind);
        Assert.Contains("base64 -d > 'src/App.tsx'", manifest);
        Assert.Contains("RDO_PULL_REQUEST_URL", manifest);
        Assert.Contains("RDO_STEP=WritingPreviewSource", manifest);
        Assert.Contains("git clone --depth 1 --branch \"$ROSENVALL_DEFAULT_BRANCH\"", manifest);
        Assert.Contains("git status --porcelain", manifest);
        Assert.Contains("git commit -m", manifest);
        Assert.Contains("git push --set-upstream origin", manifest);
        Assert.Contains("ROSENVALL_PUBLIC_HOSTNAME", manifest);
        Assert.Contains("Production hostname:", manifest);
        Assert.Contains("image: ghcr.io/carnufex/rosenvall-devops-api@sha256:promotiondigest", manifest);
        Assert.DoesNotContain("image: ghcr.io/carnufex/rosenvall-devops-api:main", manifest);
        AssertPreviewPromotionRunnerSecurityContextIsWellFormed(manifest);
        Assert.DoesNotContain("codex exec", manifest);
        Assert.DoesNotContain("RDO_STEP=Implementing", manifest);
        Assert.DoesNotContain("RDO_STEP=Testing", manifest);
        Assert.DoesNotContain("npm test", manifest);
        Assert.DoesNotContain("npm run build", manifest);
        Assert.DoesNotContain("npm install", manifest);
        Assert.DoesNotContain("npm ci", manifest);
        Assert.DoesNotContain("node_modules", manifest);
        Assert.DoesNotContain("/opt/rosenvall-preview", manifest);
        Assert.DoesNotContain("rm -rf dist", manifest);
        Assert.Equal("Running", store.GetWorkItemDetail(item.Id)!.Preview!.Status);
    }

    [Fact]
    public void Local_git_preview_promotion_uses_forgejo_pr_api_without_codex_or_builds()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest(
            "LocalGit",
            "clock-app",
            "http://rosenvall-devops-forgejo.rosenvall-devops.svc.cluster.local:3000/rdo/clock-app.git",
            "main",
            null,
            "rdo",
            "react-preview",
            "preview-then-pr"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("Clock app", repository.Id, null, null, null, null, null, ImplementationProfile: "react-preview"))!;
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Clock toggle", "Build a web clock.", "Todo", "Medium", null));
        var aiRun = store.StartAiPlan(item.Id, "codex", "gpt-5.5", "Build preview.")!;
        store.ApproveAiRun(aiRun.Id, "crille");
        store.CompletePreviewImplementation(item.Id, [
            new PreviewSourceFile("app", "src/App.tsx", "export default function App(){return <main>Clock</main>}")
        ], "codex");
        store.MarkPreviewRunning(item.Id, "test", "Preview is running.");

        var run = store.StartPreviewPromotionRun(item.Id, "crille")!;
        var manifest = store.RenderImplementationRunManifest(run.Id, new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LocalGit:ApiBaseUrl"] = "http://localhost:3001/api/v1",
                ["LocalGit:RunnerApiBaseUrl"] = "http://forgejo.local/api/v1",
                ["LocalGit:Username"] = "rdo",
                ["Ai:Codex:KubernetesRunnerImage"] = "ghcr.io/carnufex/rosenvall-devops-api@sha256:localpromotiondigest"
            })
            .Build(), "repository-token")!;

        Assert.Contains("image: ghcr.io/carnufex/rosenvall-devops-api@sha256:localpromotiondigest", manifest);
        Assert.DoesNotContain("image: ghcr.io/carnufex/rosenvall-devops-api:main", manifest);
        Assert.Contains("ROSENVALL_REPOSITORY_PROVIDER", manifest);
        Assert.Contains("value: \"LocalGit\"", manifest);
        Assert.Contains("ROSENVALL_FORGEJO_API_BASE_URL", manifest);
        Assert.Contains("value: \"http://forgejo.local/api/v1\"", manifest);
        Assert.DoesNotContain("value: \"http://localhost:3001/api/v1\"", manifest);
        Assert.Contains("forgejo_auth=\"$(printf '%s:%s' \"$ROSENVALL_LOCAL_GIT_USERNAME\" \"$ROSENVALL_GIT_TOKEN\" | base64 | tr -d '\\n')\"", manifest);
        Assert.Contains("\"$ROSENVALL_FORGEJO_API_BASE_URL/repos/$ROSENVALL_REPOSITORY/pulls\"", manifest);
        Assert.Contains("pr_http_code=\"$(curl -sS -o \"$pr_response_file\" -w \"%{http_code}\" -X POST \"$ROSENVALL_FORGEJO_API_BASE_URL/repos/$ROSENVALL_REPOSITORY/pulls\"", manifest);
        Assert.Contains("jq -r '.message // .error // .errors[0].message // empty'", manifest);
        Assert.Contains("pr_number=\"$(jq -r '.number // empty' \"$pr_response_file\")\"", manifest);
        Assert.Contains("pr_state=\"$(jq -r '.state // empty' \"$pr_response_file\")\"", manifest);
        Assert.Contains("pr_url=\"$(jq -r '.html_url // empty' \"$pr_response_file\")\"", manifest);
        Assert.Contains("RDO_FAILURE=Forgejo pull request API failed", manifest);
        Assert.DoesNotContain("pr_response=\"$(curl -sS -X POST", manifest);
        Assert.DoesNotContain("pr_response\" | sed -n", manifest);
        Assert.Contains("pr_base_url=\"${ROSENVALL_FORGEJO_API_BASE_URL%/api/v1}\"", manifest);
        Assert.Contains("pr_url=\"$pr_base_url/$ROSENVALL_REPOSITORY/pulls/$pr_number\"", manifest);
        Assert.Contains("-H \"Authorization: Basic $forgejo_auth\"", manifest);
        Assert.Contains("base64 -d > 'src/App.tsx'", manifest);
        Assert.Contains("git clone --depth 1 --branch \"$ROSENVALL_DEFAULT_BRANCH\"", manifest);
        AssertPreviewPromotionRunnerSecurityContextIsWellFormed(manifest);
        Assert.DoesNotContain("name: GITHUB_TOKEN", manifest);
        Assert.DoesNotContain("codex exec", manifest);
        Assert.DoesNotContain("npm run build", manifest);
        Assert.DoesNotContain("npm test", manifest);
    }

    [Fact]
    public void Preview_promotion_start_is_idempotent_for_same_actor_and_source_preview()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest(
            "LocalGit",
            "clock-app",
            "http://rosenvall-devops-forgejo.rosenvall-devops.svc.cluster.local:3000/rdo/clock-app.git",
            "main",
            null,
            "rdo",
            "react-preview",
            "preview-then-pr"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("Clock app", repository.Id, null, null, null, null, null, ImplementationProfile: "react-preview"))!;
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Clock toggle", "Build a web clock.", "Todo", "Medium", null));
        var aiRun = store.StartAiPlan(item.Id, "codex", "gpt-5.5", "Build preview.")!;
        store.ApproveAiRun(aiRun.Id, "crille");
        store.CompletePreviewImplementation(item.Id, [
            new PreviewSourceFile("app", "src/App.tsx", "export default function App(){return <main>Clock</main>}")
        ], "codex");
        store.MarkPreviewRunning(item.Id, "test", "Preview is running.");

        var first = store.StartPreviewPromotionRun(item.Id, "crille")!;
        var second = store.StartPreviewPromotionRun(item.Id, "crille")!;
        var action = Assert.Single(store.GetActionLedger(), entry => entry.OperationKind == "preview-promotion");

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("crille", action.ActorSubject);
        Assert.Equal(board.Id, action.BoardId);
        Assert.Equal(item.Id, action.WorkItemId);
        Assert.Equal(first.Id, action.RunId);
        Assert.Contains(item.Id.ToString("N"), action.IdempotencyKey);
        Assert.Contains("preview-promotion", action.IdempotencyKey);
        Assert.Single(store.GetImplementationRuns(item.Id), run => run.RunKind == "preview-promotion");
        Assert.Contains(fixture.Reopen().GetActionLedger(), entry => entry.Id == action.Id && entry.RunId == first.Id);
    }

    [Fact]
    public void Action_ledger_blocks_duplicate_provider_sync_until_failed_before_run_creation()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var source = store.CreateRepository(new CreateRepositoryRequest(
            "LocalGit",
            "source-app",
            "http://forgejo.local/rdo/source-app.git",
            "main",
            null,
            "rdo",
            "react-preview",
            "preview-then-pr"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("Source app", source.Id, null, null, null, null, null, ImplementationProfile: "react-preview"))!;
        var key = $"provider-sync:demo:{board.Id:N}:{source.Id:N}:github:source-app-copy:true";

        var first = store.StartAction("demo", board.Id, null, "provider-sync", key);
        var duplicate = store.StartAction("demo", board.Id, null, "provider-sync", key);

        Assert.True(first.Started);
        Assert.False(duplicate.Started);
        Assert.Equal(first.Action.Id, duplicate.Action.Id);
        Assert.Single(store.GetActionLedger(), entry => entry.OperationKind == "provider-sync");

        store.MarkActionFailed(first.Action.Id, "GitHub authorization is missing.");
        var retry = store.StartAction("demo", board.Id, null, "provider-sync", key);

        Assert.True(retry.Started);
        Assert.NotEqual(first.Action.Id, retry.Action.Id);

        var run = store.RecordPipelineRun(new RecordPipelineRunRequest(source.Id, board.Id, null, "ProviderSync", "Queued", "Queued provider sync.", null))!;
        store.MarkActionRun(retry.Action.Id, run.Id, "Started");
        store.MarkActionFailed(retry.Action.Id, "Job submission failed.");
        var blockedAfterRun = store.StartAction("demo", board.Id, null, "provider-sync", key);

        Assert.False(blockedAfterRun.Started);
        Assert.Equal(retry.Action.Id, blockedAfterRun.Action.Id);
        Assert.Equal(run.Id, blockedAfterRun.Action.RunId);
        Assert.Contains(fixture.Reopen().GetActionLedger(), entry => entry.Id == retry.Action.Id && entry.RunId == run.Id);
    }

    [Fact]
    public void Provider_sync_action_ledger_quota_blocks_new_sync_without_blocking_idempotent_duplicate()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var source = store.CreateRepository(new CreateRepositoryRequest(
            "LocalGit",
            "source-app",
            "http://forgejo.local/rdo/source-app.git",
            "main",
            null,
            "rdo",
            "react-preview",
            "preview-then-pr"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("Source app", source.Id, null, null, null, null, null, ImplementationProfile: "react-preview"))!;
        var firstKey = $"provider-sync:demo:{board.Id:N}:{source.Id:N}:github:first:true";
        var secondKey = $"provider-sync:demo:{board.Id:N}:{source.Id:N}:github:second:true";

        var first = store.StartAction("demo", board.Id, null, "provider-sync", firstKey, maxStartsPerActor: 1, quotaWindow: TimeSpan.FromMinutes(15), quotaOperationKinds: new[] { "provider-sync" });
        var duplicate = store.StartAction("demo", board.Id, null, "provider-sync", firstKey, maxStartsPerActor: 1, quotaWindow: TimeSpan.FromMinutes(15), quotaOperationKinds: new[] { "provider-sync" });
        var overQuota = store.StartAction("demo", board.Id, null, "provider-sync", secondKey, maxStartsPerActor: 1, quotaWindow: TimeSpan.FromMinutes(15), quotaOperationKinds: new[] { "provider-sync" });

        Assert.True(first.Started);
        Assert.False(duplicate.Started);
        Assert.Equal(first.Action!.Id, duplicate.Action!.Id);
        Assert.False(overQuota.Started);
        Assert.Null(overQuota.Action);
        Assert.Equal("QuotaExceeded", overQuota.BlockReason);
        Assert.Single(store.GetActionLedger(), action => action.OperationKind == "provider-sync");
    }

    [Fact]
    public void Provider_sync_endpoint_uses_action_quota_before_creating_target_resources()
    {
        var program = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "Rosenvall.DevOps.Api", "Program.cs"));
        var endpoint = EndpointSnippet(program, "api.MapPost(\"/boards/{boardId:guid}/repositories/sync-to-provider\"", "api.MapGet(\"/boards/{boardId:guid}/teams\"");

        Assert.Contains("ReadProviderSyncActionQuota(configuration)", endpoint);
        Assert.Contains("\"provider-sync\"", endpoint);
        Assert.Contains("maxStartsPerActor", endpoint);
        Assert.Contains("ActionLedgerBlockReasons.ProviderSyncActionKinds", endpoint);
        Assert.Contains("ActionQuotaExceededResult(actionStart)", endpoint);
        Assert.True(endpoint.IndexOf("store.StartAction", StringComparison.Ordinal) < endpoint.IndexOf("CreateRepositoryResultAsync", StringComparison.Ordinal));
    }

    [Fact]
    public void Repository_implementation_action_ledger_allows_failed_retry_but_blocks_duplicate_before_run()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var board = store.GetBoards(workspace.Id).First();
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Implementation quota", "Avoid duplicate repository runs.", "Todo", "Medium", null));
        var aiRunId = Guid.NewGuid();
        var repositoryId = Guid.NewGuid();
        var key = $"repository-implementation:demo:{item.Id:N}:{aiRunId:N}:{repositoryId:N}";

        var first = store.StartAction("demo", board.Id, item.Id, "repository-implementation", key, blockAfterRunCreation: false);
        var duplicateBeforeRun = store.StartAction("demo", board.Id, item.Id, "repository-implementation", key, blockAfterRunCreation: false);

        Assert.True(first.Started);
        Assert.False(duplicateBeforeRun.Started);
        Assert.Equal(first.Action!.Id, duplicateBeforeRun.Action!.Id);

        store.MarkActionFailed(first.Action.Id, "Runner failed before run creation.");
        var retry = store.StartAction("demo", board.Id, item.Id, "repository-implementation", key, blockAfterRunCreation: false);

        Assert.True(retry.Started);
        Assert.NotEqual(first.Action.Id, retry.Action!.Id);
    }

    [Fact]
    public void Repository_implementation_action_ledger_enforces_per_actor_quota()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var board = store.GetBoards(workspace.Id).First();
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Implementation quota", "Avoid duplicate repository runs.", "Todo", "Medium", null));
        var firstKey = $"repository-implementation:demo:{item.Id:N}:{Guid.NewGuid():N}:default";
        var secondKey = $"repository-implementation:demo:{item.Id:N}:{Guid.NewGuid():N}:default";

        var first = store.StartAction("demo", board.Id, item.Id, "repository-implementation", firstKey, blockAfterRunCreation: false, maxStartsPerActor: 1, quotaWindow: TimeSpan.FromMinutes(15), quotaOperationKinds: new[] { "repository-implementation" });
        var duplicate = store.StartAction("demo", board.Id, item.Id, "repository-implementation", firstKey, blockAfterRunCreation: false, maxStartsPerActor: 1, quotaWindow: TimeSpan.FromMinutes(15), quotaOperationKinds: new[] { "repository-implementation" });
        var overQuota = store.StartAction("demo", board.Id, item.Id, "repository-implementation", secondKey, blockAfterRunCreation: false, maxStartsPerActor: 1, quotaWindow: TimeSpan.FromMinutes(15), quotaOperationKinds: new[] { "repository-implementation" });

        Assert.True(first.Started);
        Assert.False(duplicate.Started);
        Assert.Equal(first.Action!.Id, duplicate.Action!.Id);
        Assert.False(overQuota.Started);
        Assert.Null(overQuota.Action);
        Assert.Equal("QuotaExceeded", overQuota.BlockReason);
    }

    [Fact]
    public void Repository_implementation_endpoint_uses_action_quota_before_starting_run()
    {
        var program = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "Rosenvall.DevOps.Api", "Program.cs"));
        var endpoint = EndpointSnippet(program, "api.MapPost(\"/work-items/{workItemId:guid}/implementation-runs\"", "api.MapGet(\"/previews/{workItemId:guid}/manifest\"");

        Assert.Contains("ReadRepositoryImplementationActionQuota(configuration)", endpoint);
        Assert.Contains("\"repository-implementation\"", endpoint);
        Assert.Contains("RepositoryImplementationActionIdempotencyKey(actorSubject, workItemId, request.AiRunId, request.RepositoryId)", endpoint);
        Assert.Contains("ActionLedgerBlockReasons.RepositoryImplementationActionKinds", endpoint);
        Assert.Contains("ActionQuotaExceededResult(actionStart)", endpoint);
        Assert.Contains("blockAfterRunCreation: false", endpoint);
        Assert.True(endpoint.IndexOf("store.StartAction", StringComparison.Ordinal) < endpoint.IndexOf("store.StartImplementationRun", StringComparison.Ordinal));
        Assert.Contains("store.MarkActionRun(actionStart.Action!.Id, run.Id, \"Queued\")", endpoint);
        Assert.Contains("store.MarkActionRun(actionStart.Action!.Id, run.Id, \"Running\")", endpoint);
    }

    [Fact]
    public void Pull_request_review_fix_action_ledger_allows_failed_retry_but_blocks_duplicate_before_run()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var board = store.GetBoards(workspace.Id).First();
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Review fix quota", "Avoid duplicate review fix runs.", "Todo", "Medium", null));
        var key = $"pr-review-fix:demo:{item.Id:N}:high";

        var first = store.StartAction("demo", board.Id, item.Id, "pr-review-fix", key, blockAfterRunCreation: false);
        var duplicateBeforeRun = store.StartAction("demo", board.Id, item.Id, "pr-review-fix", key, blockAfterRunCreation: false);

        Assert.True(first.Started);
        Assert.False(duplicateBeforeRun.Started);
        Assert.Equal(first.Action!.Id, duplicateBeforeRun.Action!.Id);

        store.MarkActionFailed(first.Action.Id, "Runner failed before run creation.");
        var retry = store.StartAction("demo", board.Id, item.Id, "pr-review-fix", key, blockAfterRunCreation: false);

        Assert.True(retry.Started);
        Assert.NotEqual(first.Action.Id, retry.Action!.Id);
    }

    [Fact]
    public void Pull_request_review_fix_action_ledger_enforces_per_actor_quota()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var board = store.GetBoards(workspace.Id).First();
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Review fix quota", "Avoid duplicate review fix runs.", "Todo", "Medium", null));

        var first = store.StartAction("demo", board.Id, item.Id, "pr-review-fix", $"pr-review-fix:demo:{item.Id:N}:high", blockAfterRunCreation: false, maxStartsPerActor: 1, quotaWindow: TimeSpan.FromMinutes(15), quotaOperationKinds: new[] { "pr-review-fix" });
        var duplicate = store.StartAction("demo", board.Id, item.Id, "pr-review-fix", $"pr-review-fix:demo:{item.Id:N}:high", blockAfterRunCreation: false, maxStartsPerActor: 1, quotaWindow: TimeSpan.FromMinutes(15), quotaOperationKinds: new[] { "pr-review-fix" });
        var overQuota = store.StartAction("demo", board.Id, item.Id, "pr-review-fix", $"pr-review-fix:demo:{item.Id:N}:medium", blockAfterRunCreation: false, maxStartsPerActor: 1, quotaWindow: TimeSpan.FromMinutes(15), quotaOperationKinds: new[] { "pr-review-fix" });

        Assert.True(first.Started);
        Assert.False(duplicate.Started);
        Assert.Equal(first.Action!.Id, duplicate.Action!.Id);
        Assert.False(overQuota.Started);
        Assert.Null(overQuota.Action);
        Assert.Equal("QuotaExceeded", overQuota.BlockReason);
    }

    [Fact]
    public void Pull_request_review_fix_endpoint_uses_action_quota_before_starting_run()
    {
        var program = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "Rosenvall.DevOps.Api", "Program.cs"));
        var endpoint = EndpointSnippet(program, "api.MapPost(\"/work-items/{workItemId:guid}/pull-request/ai-fix-comments\"", "api.MapGet(\"/work-items/{workItemId:guid}/ai-session\"");

        Assert.Contains("ReadPullRequestReviewFixActionQuota(configuration)", endpoint);
        Assert.Contains("\"pr-review-fix\"", endpoint);
        Assert.Contains("PullRequestReviewFixActionIdempotencyKey(actorSubject, workItemId, request.ReasoningEffort)", endpoint);
        Assert.Contains("ActionLedgerBlockReasons.PullRequestReviewFixActionKinds", endpoint);
        Assert.Contains("ActionQuotaExceededResult(actionStart)", endpoint);
        Assert.Contains("blockAfterRunCreation: false", endpoint);
        Assert.True(endpoint.IndexOf("store.StartAction", StringComparison.Ordinal) < endpoint.IndexOf("store.StartPullRequestReviewFixRun", StringComparison.Ordinal));
        Assert.Contains("store.MarkActionRun(actionStart.Action!.Id, run.Id, \"Queued\")", endpoint);
        Assert.Contains("store.MarkActionRun(actionStart.Action!.Id, run.Id, \"Running\")", endpoint);
    }

    [Fact]
    public void Ai_plan_action_ledger_blocks_only_in_flight_duplicate_provider_calls()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var board = store.GetBoards(workspace.Id).First();
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Plan quota", "Avoid duplicate AI calls.", "Todo", "Medium", null));
        var key = $"ai-plan:demo:{item.Id:N}:codex:gpt-5.5:high";

        var first = store.StartAction("demo", board.Id, item.Id, "ai-plan", key, blockAfterRunCreation: false);
        var duplicateWhileGenerating = store.StartAction("demo", board.Id, item.Id, "ai-plan", key, blockAfterRunCreation: false);

        Assert.True(first.Started);
        Assert.False(duplicateWhileGenerating.Started);
        Assert.Equal(first.Action.Id, duplicateWhileGenerating.Action.Id);

        var run = store.StartAiPlan(item.Id, "codex", "gpt-5.5", "Generated plan.", "high")!;
        store.MarkActionRun(first.Action.Id, run.Id, "Completed");
        var deliberateNewRequest = store.StartAction("demo", board.Id, item.Id, "ai-plan", key, blockAfterRunCreation: false);

        Assert.True(deliberateNewRequest.Started);
        Assert.NotEqual(first.Action.Id, deliberateNewRequest.Action.Id);
    }

    [Fact]
    public void Action_ledger_enforces_per_actor_quota_after_idempotency_check()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var board = store.GetBoards(workspace.Id).First();
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Plan quota", "Avoid duplicate AI calls.", "Todo", "Medium", null));
        var operationKinds = new[] { "ai-plan", "ai-plan-revise" };

        var first = store.StartAction("demo", board.Id, item.Id, "ai-plan", "ai-plan:first", maxStartsPerActor: 1, quotaWindow: TimeSpan.FromMinutes(10), quotaOperationKinds: operationKinds);
        var duplicate = store.StartAction("demo", board.Id, item.Id, "ai-plan", "ai-plan:first", maxStartsPerActor: 1, quotaWindow: TimeSpan.FromMinutes(10), quotaOperationKinds: operationKinds);
        var overQuota = store.StartAction("demo", board.Id, item.Id, "ai-plan-revise", "ai-plan-revise:second", maxStartsPerActor: 1, quotaWindow: TimeSpan.FromMinutes(10), quotaOperationKinds: operationKinds);

        Assert.True(first.Started);
        Assert.False(duplicate.Started);
        Assert.Equal(first.Action!.Id, duplicate.Action!.Id);
        Assert.False(overQuota.Started);
        Assert.Null(overQuota.Action);
        Assert.Equal("QuotaExceeded", overQuota.BlockReason);
        Assert.True(overQuota.RetryAfterSeconds is > 0);
        Assert.Single(store.GetActionLedger(), action => action.ActorSubject == "demo");
    }

    [Fact]
    public void Ai_plan_endpoints_use_action_ledger_without_storing_revision_message_in_key()
    {
        var program = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "Rosenvall.DevOps.Api", "Program.cs"));
        var planEndpoint = EndpointSnippet(program, "api.MapPost(\"/work-items/{workItemId:guid}/ai-plan\"", "api.MapPost(\"/work-items/{workItemId:guid}/ai-plan/revise\"");
        var reviseEndpoint = EndpointSnippet(program, "api.MapPost(\"/work-items/{workItemId:guid}/ai-plan/revise\"", "api.MapPost(\"/ai-runs/{aiRunId:guid}/approve\"");

        Assert.Contains("\"ai-plan\"", planEndpoint);
        Assert.Contains("maxStartsPerActor", planEndpoint);
        Assert.Contains("ActionQuotaExceededResult", planEndpoint);
        Assert.Contains("ActionLedgerBlockReasons.AiPlanningActionKinds", planEndpoint);
        Assert.Contains("blockAfterRunCreation: false", planEndpoint);
        Assert.Contains("store.MarkActionRun(actionStart.Action!.Id, run.Id, \"Completed\")", planEndpoint);
        Assert.Contains("store.MarkActionFailed(actionStart.Action!.Id, ex.Message)", planEndpoint);
        Assert.Contains("\"ai-plan-revise\"", reviseEndpoint);
        Assert.Contains("maxStartsPerActor", reviseEndpoint);
        Assert.Contains("ActionQuotaExceededResult", reviseEndpoint);
        Assert.Contains("ActionLedgerBlockReasons.AiPlanningActionKinds", reviseEndpoint);
        Assert.Contains("ShortActionHash(message)", program);
        Assert.Contains("request.Message", reviseEndpoint);
        Assert.DoesNotContain(":{message}", program);
    }

    [Fact]
    public void Local_git_implementation_run_clones_pushes_and_creates_forgejo_pull_request()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest(
            "LocalGit",
            "demo-app",
            "http://rosenvall-devops-forgejo.rosenvall-devops.svc.cluster.local:3000/rdo/demo-app.git",
            "main",
            null,
            "rdo",
            "code-repo",
            "direct-pr"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("Demo app", repository.Id, null, null, null, null, null, ImplementationProfile: "code-repo", ImplementationWorkflow: "direct-pr"))!;
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Implement app", "Build the app.", "Todo", "Medium", null));
        var aiRun = store.StartAiPlan(item.Id, "codex", "gpt-5.5", "Build implementation.")!;
        store.ApproveAiRun(aiRun.Id, "crille");

        var run = store.StartImplementationRun(item.Id, new StartImplementationRunRequest(aiRun.Id, "crille", repository.Id))!;
        var manifest = store.RenderImplementationRunManifest(run.Id, new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LocalGit:ApiBaseUrl"] = "http://localhost:3001/api/v1",
                ["LocalGit:RunnerApiBaseUrl"] = "http://forgejo.local/api/v1",
                ["LocalGit:Username"] = "rdo"
            })
            .Build(), "repository-token")!;

        Assert.Contains("value: \"LocalGit\"", manifest);
        Assert.Contains("value: \"http://forgejo.local/api/v1\"", manifest);
        Assert.DoesNotContain("value: \"http://localhost:3001/api/v1\"", manifest);
        Assert.Contains("ROSENVALL_GIT_TOKEN", manifest);
        Assert.Contains("git clone --depth 1 --branch \"$ROSENVALL_DEFAULT_BRANCH\"", manifest);
        Assert.Contains("codex exec", manifest);
        Assert.Contains("git push --set-upstream origin", manifest);
        Assert.Contains("forgejo_auth=\"$(printf '%s:%s' \"$ROSENVALL_LOCAL_GIT_USERNAME\" \"$ROSENVALL_GIT_TOKEN\" | base64 | tr -d '\\n')\"", manifest);
        Assert.Contains("\"$ROSENVALL_FORGEJO_API_BASE_URL/repos/$ROSENVALL_REPOSITORY/pulls\"", manifest);
        Assert.Contains("pr_http_code=\"$(curl -sS -o \"$pr_response_file\" -w \"%{http_code}\" -X POST \"$ROSENVALL_FORGEJO_API_BASE_URL/repos/$ROSENVALL_REPOSITORY/pulls\"", manifest);
        Assert.Contains("jq -r '.message // .error // .errors[0].message // empty'", manifest);
        Assert.Contains("pr_number=\"$(jq -r '.number // empty' \"$pr_response_file\")\"", manifest);
        Assert.Contains("pr_state=\"$(jq -r '.state // empty' \"$pr_response_file\")\"", manifest);
        Assert.Contains("pr_url=\"$(jq -r '.html_url // empty' \"$pr_response_file\")\"", manifest);
        Assert.Contains("RDO_FAILURE=Forgejo pull request API failed", manifest);
        Assert.DoesNotContain("pr_response=\"$(curl -sS -X POST", manifest);
        Assert.DoesNotContain("pr_response\" | sed -n", manifest);
        Assert.Contains("pr_base_url=\"${ROSENVALL_FORGEJO_API_BASE_URL%/api/v1}\"", manifest);
        Assert.Contains("pr_url=\"$pr_base_url/$ROSENVALL_REPOSITORY/pulls/$pr_number\"", manifest);
        Assert.Contains("-H \"Authorization: Basic $forgejo_auth\"", manifest);
        Assert.DoesNotContain("name: GITHUB_TOKEN", manifest);
    }

    [Fact]
    public void Local_git_settings_distinguish_enabled_from_available()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var ready = fixture.Store.GetSettings(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LocalGit:Enabled"] = "true",
                ["LocalGit:ApiBaseUrl"] = "http://localhost:3001/api/v1",
                ["LocalGit:RunnerApiBaseUrl"] = "http://forgejo.local/api/v1",
                ["LocalGit:Password"] = "service-token"
            })
            .Build(),
            localGitReadiness: new LocalGitReadinessDto(true, true, true, "http://localhost:3001/api/v1", "Local Git is ready.", 200));
        var unverified = fixture.Store.GetSettings(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LocalGit:Enabled"] = "true",
                ["LocalGit:ApiBaseUrl"] = "http://localhost:3001/api/v1",
                ["LocalGit:Password"] = "service-token"
            })
            .Build());
        var unavailable = fixture.Store.GetSettings(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LocalGit:Enabled"] = "true",
                ["LocalGit:ApiBaseUrl"] = "http://localhost:3001/api/v1",
                ["LocalGit:Password"] = "service-token",
                ["LocalGit:UnavailableReason"] = "Forgejo is not deployed."
            })
            .Build(),
            localGitReadiness: new LocalGitReadinessDto(true, true, true, "http://localhost:3001/api/v1", "Local Git is ready.", 200));

        Assert.Equal("LocalGit", ready.Repositories.Provider);
        Assert.Equal("http://localhost:3001/api/v1", ready.Repositories.ApiBaseUrl);
        Assert.True(ready.Repositories.LocalGitEnabled);
        Assert.True(ready.Repositories.LocalGitAvailable);
        Assert.True(ready.Repositories.CanCreateRepositories);

        Assert.True(unverified.Repositories.LocalGitEnabled);
        Assert.False(unverified.Repositories.LocalGitAvailable);
        Assert.False(unverified.Repositories.CanCreateRepositories);
        Assert.Equal("Local Git is enabled, but Forgejo readiness has not been verified yet.", unverified.Repositories.LocalGitMessage);

        Assert.True(unavailable.Repositories.LocalGitEnabled);
        Assert.False(unavailable.Repositories.LocalGitAvailable);
        Assert.False(unavailable.Repositories.CanCreateRepositories);
        Assert.Equal("Forgejo is not deployed.", unavailable.Repositories.LocalGitMessage);
    }

    [Fact]
    public async Task Forgejo_readiness_probe_authenticates_service_credential()
    {
        HttpRequestMessage? observed = null;
        using var httpClient = new HttpClient(new RoutingHttpMessageHandler(request =>
        {
            observed = request;
            return JsonResponse("""{"login":"rdo"}""");
        }));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LocalGit:Enabled"] = "true",
                ["LocalGit:ApiBaseUrl"] = "http://forgejo.local/api/v1",
                ["LocalGit:Username"] = "rdo",
                ["LocalGit:Password"] = "service-token"
            })
            .Build();
        var client = new ForgejoRepositoryClient(httpClient, configuration);

        var readiness = await client.CheckReadinessAsync(CancellationToken.None);

        Assert.True(readiness.Available);
        Assert.Equal("Local Git is ready.", readiness.Message);
        Assert.Equal("http://forgejo.local/api/v1/user", observed?.RequestUri?.ToString());
        Assert.Equal("Basic", observed?.Headers.Authorization?.Scheme);
        var credentials = Encoding.UTF8.GetString(Convert.FromBase64String(observed!.Headers.Authorization!.Parameter!));
        Assert.Equal("rdo:service-token", credentials);
    }

    [Fact]
    public async Task Forgejo_readiness_probe_reports_rejected_credentials()
    {
        using var httpClient = new HttpClient(new RoutingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"message":"bad credentials"}""")
        }));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LocalGit:Enabled"] = "true",
                ["LocalGit:ApiBaseUrl"] = "http://forgejo.local/api/v1",
                ["LocalGit:Token"] = "bad-token"
            })
            .Build();
        var client = new ForgejoRepositoryClient(httpClient, configuration);

        var readiness = await client.CheckReadinessAsync(CancellationToken.None);

        Assert.False(readiness.Available);
        Assert.True(readiness.Configured);
        Assert.Equal(401, readiness.StatusCode);
        Assert.Equal("Local Git is enabled, but Forgejo rejected the service credential.", readiness.Message);
    }

    [Fact]
    public void Local_git_pull_request_metadata_is_stored_on_run_and_development()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest(
            "LocalGit",
            "demo-app",
            "http://rosenvall-devops-forgejo.rosenvall-devops.svc.cluster.local:3000/rdo/demo-app.git",
            "main",
            null,
            "rdo",
            "react-preview",
            "preview-then-pr"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("Demo app", repository.Id, null, null, null, null, null, ImplementationProfile: "react-preview"))!;
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Implement app", "Build the app.", "Todo", "Medium", null));
        var aiRun = store.StartAiPlan(item.Id, "codex", "gpt-5.5", "Build implementation.")!;
        store.ApproveAiRun(aiRun.Id, "crille");
        store.CompletePreviewImplementation(item.Id, [
            new PreviewSourceFile("app", "src/App.tsx", "export default function App(){return <main>Clock</main>}")
        ], "codex");
        store.MarkPreviewRunning(item.Id, "test", "Preview is running.");

        var run = store.StartPreviewPromotionRun(item.Id, "crille")!;
        var updated = store.UpdateImplementationRun(run.Id, "PullRequestReady", "RDO_COMMIT=abc123\nRDO_PULL_REQUEST_URL=http://forgejo.local/rdo/demo-app/pulls/7\nRDO_PULL_REQUEST_NUMBER=7\nRDO_PULL_REQUEST_STATE=open")!;
        var detail = store.GetWorkItemDetail(item.Id)!;

        Assert.Equal("LocalGit", updated.PullRequestProvider);
        Assert.Equal(7, updated.PullRequestNumber);
        Assert.Equal("open", updated.PullRequestState);
        Assert.Equal("LocalGit", detail.Development!.PullRequestProvider);
        Assert.Equal(repository.Id, detail.Development.RepositoryId);
        Assert.Equal(7, detail.Development.PullRequestNumber);
        Assert.Equal("Local pull request ready", detail.Development.ChecksStatus);
    }

    [Fact]
    public void Local_git_pull_request_review_comments_are_persisted_and_scoped_to_current_pr()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var (_, item, _) = CreateLocalGitPreviewPullRequest(store);

        var comment = store.AddPullRequestReviewComment(item.Id, new CreatePullRequestReviewCommentRequest(
            "src/App.tsx",
            "new",
            42,
            "+ const title = 'Demo';",
            "Use Swedish product wording here."), "crille")!;

        var comments = store.GetPullRequestReviewComments(item.Id, "LocalGit", 7);

        Assert.Single(comments);
        Assert.Equal(comment.Id, comments[0].Id);
        Assert.Equal("open", comments[0].Status);
        Assert.Equal("crille", comments[0].Author);
        Assert.True(store.HasUnresolvedPullRequestReviewComments(item.Id));

        var reopened = fixture.Reopen();
        var reopenedComment = reopened.GetPullRequestReviewComments(item.Id, "LocalGit", 7).Single();
        var resolved = reopened.UpdatePullRequestReviewComment(item.Id, reopenedComment.Id, new UpdatePullRequestReviewCommentRequest(Status: "resolved"), "crille")!;

        Assert.Equal("resolved", resolved.Status);
        Assert.False(reopened.HasUnresolvedPullRequestReviewComments(item.Id));
        Assert.Empty(reopened.GetPullRequestReviewComments(item.Id, "LocalGit", 8));
    }

    [Fact]
    public void Ai_plan_review_comments_are_persisted_and_scoped_to_the_selected_plan()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var board = store.GetWorkspaces().SelectMany(workspace => store.GetBoards(workspace.Id)).First();
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Plan review", "Create a focused demo.", "Todo", "Medium", null));
        var firstPlan = store.StartAiPlan(item.Id, "codex", "gpt-5.5", "Plan:\n1. Build the app.\n2. Add tests.")!;
        var secondPlan = store.StartAiPlan(item.Id, "codex", "gpt-5.5", "Plan:\n1. Revised app.")!;

        var comment = store.AddAiPlanReviewComment(item.Id, firstPlan.Id, new CreateAiPlanReviewCommentRequest(
            "block-1",
            "1. Build the app.",
            "Clarify the production hostname before implementation."), "crille")!;

        var comments = store.GetAiPlanReviewComments(item.Id, firstPlan.Id);

        Assert.Single(comments);
        Assert.Equal(comment.Id, comments[0].Id);
        Assert.Equal("open", comments[0].Status);
        Assert.Equal("crille", comments[0].Author);
        Assert.Equal("block-1", comments[0].AnchorKey);
        Assert.True(store.HasUnresolvedAiPlanReviewComments(firstPlan.Id));
        Assert.Empty(store.GetAiPlanReviewComments(item.Id, secondPlan.Id));

        var reopened = fixture.Reopen();
        var reopenedComment = reopened.GetAiPlanReviewComments(item.Id, firstPlan.Id).Single();
        var resolved = reopened.UpdateAiPlanReviewComment(item.Id, firstPlan.Id, reopenedComment.Id, new UpdateAiPlanReviewCommentRequest(Status: "resolved"), "crille")!;

        Assert.Equal("resolved", resolved.Status);
        Assert.Equal("crille", resolved.ResolvedBy);
        Assert.False(reopened.HasUnresolvedAiPlanReviewComments(firstPlan.Id));
        Assert.True(reopened.DeleteAiPlanReviewComment(item.Id, firstPlan.Id, reopenedComment.Id));
        Assert.Empty(reopened.GetAiPlanReviewComments(item.Id, firstPlan.Id));
    }

    [Fact]
    public void Ai_plan_review_comments_block_preview_and_direct_repository_implementation_until_resolved()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var previewBoard = store.GetBoards(workspace.Id).First();
        var previewItem = store.CreateWorkItem(new CreateWorkItemRequest(previewBoard.Id, "Feature", "Preview plan", "Create a page.", "Todo", "Medium", null));
        var previewPlan = store.StartAiPlan(previewItem.Id, "codex", "gpt-5.5", "Plan:\n1. Build preview.")!;
        store.AddAiPlanReviewComment(previewItem.Id, previewPlan.Id, new CreateAiPlanReviewCommentRequest("block-1", "1. Build preview.", "Answer this first."), "crille");

        var previewFailure = Assert.Throws<InvalidOperationException>(() => store.ApproveAiRun(previewPlan.Id, "crille"));

        Assert.Contains("Resolve all AI plan review comments", previewFailure.Message);

        var repository = store.CreateRepository(new CreateRepositoryRequest("LocalGit", "demo-app", "http://forgejo.local/rdo/demo-app.git", "main", null, "rdo", "code-repo", "direct-pr"));
        var directBoard = store.CreateBoard(workspace.Id, new CreateBoardRequest("Direct repo", repository.Id, null, null, null, null, null, ImplementationProfile: "code-repo", ImplementationWorkflow: "direct-pr"))!;
        var directItem = store.CreateWorkItem(new CreateWorkItemRequest(directBoard.Id, "Feature", "Direct plan", "Change repo files.", "Todo", "Medium", null));
        var directPlan = store.StartAiPlan(directItem.Id, "codex", "gpt-5.5", "Plan:\n1. Edit source.")!;
        store.AddAiPlanReviewComment(directItem.Id, directPlan.Id, new CreateAiPlanReviewCommentRequest("block-1", "1. Edit source.", "Clarify scope."), "crille");

        var directFailure = Assert.Throws<InvalidOperationException>(() => store.StartImplementationRun(directItem.Id, new StartImplementationRunRequest(directPlan.Id, "crille", repository.Id)));

        Assert.Contains("Resolve all AI plan review comments", directFailure.Message);
        Assert.Empty(store.GetImplementationRuns(directItem.Id));
    }

    [Fact]
    public void Local_git_pull_request_approval_is_blocked_while_review_comments_are_unresolved()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var (_, item, _) = CreateLocalGitPreviewPullRequest(store);
        store.AddPullRequestReviewComment(item.Id, new CreatePullRequestReviewCommentRequest("src/App.tsx", "new", 12, "+ <main />", "Fix this before merge."), "crille");

        var failure = Assert.Throws<InvalidOperationException>(() => store.ApprovePullRequest(item.Id, "crille"));

        Assert.Contains("Resolve all review comments", failure.Message);
        Assert.Equal("Review", store.GetWorkItemDetail(item.Id)!.Item.Status);
    }

    [Fact]
    public void Local_git_pr_review_fix_run_reuses_existing_pull_request_branch_and_comments()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var (repository, item, _) = CreateLocalGitPreviewPullRequest(store);
        store.AddPullRequestReviewComment(item.Id, new CreatePullRequestReviewCommentRequest("src/App.tsx", "new", 12, "+ const mode = 'light';", "Add dark mode handling."), "crille");

        var run = store.StartPullRequestReviewFixRun(item.Id, new StartPullRequestReviewFixRequest("crille", "high"))!;
        var developmentBranch = store.GetWorkItemDetail(item.Id)!.Development!.Branch;
        var manifest = store.RenderImplementationRunManifest(run.Id, new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LocalGit:RunnerApiBaseUrl"] = "http://forgejo.local/api/v1",
                ["LocalGit:Username"] = "rdo",
                ["Ai:Codex:KubernetesRunnerImage"] = "ghcr.io/carnufex/rosenvall-devops-api@sha256:reviewfixdigest"
            })
            .Build(), "repository-token")!;

        AssertCodexRepositoryJobYamlContract(manifest, "ghcr.io/carnufex/rosenvall-devops-api@sha256:reviewfixdigest", "ROSENVALL_GIT_TOKEN");
        Assert.Equal("pr-review-fix", run.RunKind);
        Assert.Equal(repository.Id, run.RepositoryId);
        Assert.Equal(developmentBranch, run.Branch);
        var prompt = DecodeManifestEnvironmentValue(manifest, "ROSENVALL_PROMPT_B64");
        Assert.Contains("RDO_STEP=FixingReviewComments", manifest);
        Assert.Contains("image: ghcr.io/carnufex/rosenvall-devops-api@sha256:reviewfixdigest", manifest);
        Assert.DoesNotContain("image: ghcr.io/carnufex/rosenvall-devops-api:main", manifest);
        Assert.Contains("Add dark mode handling.", prompt);
        Assert.Contains("git clone --depth 1 --branch \"$ROSENVALL_BRANCH\"", manifest);
        Assert.Contains("git push origin \"$ROSENVALL_BRANCH\"", manifest);
        Assert.Contains("name: ROSENVALL_PULL_REQUEST_NUMBER", manifest);
        Assert.Contains("value: \"7\"", manifest);
        Assert.Contains("RDO_PULL_REQUEST_NUMBER=$ROSENVALL_PULL_REQUEST_NUMBER", manifest);
        Assert.Contains("repository_token_for_runner=\"$ROSENVALL_GIT_TOKEN\"", manifest);
        Assert.Contains("unset ROSENVALL_GIT_TOKEN GITHUB_TOKEN", manifest);
        Assert.True(manifest.IndexOf("unset ROSENVALL_GIT_TOKEN GITHUB_TOKEN", StringComparison.Ordinal) < manifest.IndexOf("codex exec", StringComparison.Ordinal));
        Assert.Contains("codex_pid=$!", manifest);
        Assert.Contains("rm -f \"$CODEX_HOME/auth.json\"", manifest);
        Assert.Contains("wait \"$codex_pid\"", manifest);
        Assert.True(manifest.IndexOf("rm -f \"$CODEX_HOME/auth.json\"", StringComparison.Ordinal) > manifest.IndexOf("codex exec", StringComparison.Ordinal));
        Assert.True(manifest.IndexOf("rm -f \"$CODEX_HOME/auth.json\"", StringComparison.Ordinal) < manifest.IndexOf("wait \"$codex_pid\"", StringComparison.Ordinal));
        Assert.Contains("ROSENVALL_GIT_TOKEN=\"$repository_token_for_runner\"", manifest);
        Assert.DoesNotContain("/pulls\" -H", manifest);
        Assert.DoesNotContain("npm install", manifest);
    }

    [Fact]
    public void Local_git_pull_request_merge_state_is_preserved_when_pr_is_approved()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest(
            "LocalGit",
            "demo-app",
            "http://rosenvall-devops-forgejo.rosenvall-devops.svc.cluster.local:3000/rdo/demo-app.git",
            "main",
            null,
            "rdo",
            "react-preview",
            "preview-then-pr"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("Demo app", repository.Id, null, null, null, null, null, ImplementationProfile: "react-preview"))!;
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Implement app", "Build the app.", "Todo", "Medium", null));
        var aiRun = store.StartAiPlan(item.Id, "codex", "gpt-5.5", "Build implementation.")!;
        store.ApproveAiRun(aiRun.Id, "crille");
        store.CompletePreviewImplementation(item.Id, [
            new PreviewSourceFile("app", "src/App.tsx", "export default function App(){return <main>Clock</main>}")
        ], "codex");
        store.MarkPreviewRunning(item.Id, "test", "Preview is running.");
        var run = store.StartPreviewPromotionRun(item.Id, "crille")!;
        store.UpdateImplementationRun(run.Id, "PullRequestReady", "RDO_PULL_REQUEST_URL=http://forgejo.local/rdo/demo-app/pulls/7\nRDO_PULL_REQUEST_NUMBER=7\nRDO_PULL_REQUEST_STATE=open");

        Assert.False(store.IsPullRequestApproved(item.Id));

        store.MarkPullRequestMergeState(item.Id, "merged", true);
        var approved = store.ApprovePullRequest(item.Id, "crille")!;

        Assert.Equal("Done", approved.Item.Status);
        Assert.True(store.IsPullRequestApproved(item.Id));
        Assert.Equal("merged", approved.Development!.PullRequestState);
        Assert.NotNull(approved.Development.PullRequestMergedAt);
        Assert.Equal("crille", approved.Development.PullRequestApprovedBy);
        Assert.Null(approved.Development.PullRequestFailure);
    }

    [Fact]
    public void Local_git_approve_pr_waits_for_public_app_readiness_before_merge_and_completion()
    {
        var program = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "Rosenvall.DevOps.Api", "Program.cs"));
        var endpointStart = program.IndexOf("api.MapPost(\"/work-items/{workItemId:guid}/approve-pr\"", StringComparison.Ordinal);
        var endpointEnd = program.IndexOf("api.MapPost(\"/work-items/{workItemId:guid}/preview/start\"", endpointStart, StringComparison.Ordinal);
        var endpoint = program[endpointStart..endpointEnd];
        var reconcilerStart = program.IndexOf("public sealed class BoardPublicAppDeploymentReconciler", StringComparison.Ordinal);
        var reconcilerEnd = program.IndexOf("public sealed class ProviderSyncRunMonitor", reconcilerStart, StringComparison.Ordinal);
        var reconciler = program[reconcilerStart..reconcilerEnd];

        var sourceReadIndex = endpoint.IndexOf("approvedPrSourceFiles = await ReadDeployablePreviewSourceSnapshotAsync", StringComparison.Ordinal);
        var applyIndex = endpoint.IndexOf("var productionApply = await previews.ApplyAsync", StringComparison.Ordinal);
        var waitIndex = endpoint.IndexOf("store.MarkBoardPublicAppWaitingForReadiness", StringComparison.Ordinal);
        var mergeGateIndex = reconciler.IndexOf("if (!await MergeLocalGitPullRequestIfNeededAsync", StringComparison.Ordinal);
        var markRunningIndex = reconciler.IndexOf("store.UpdateBoardPublicAppHealth(app.BoardId, health);", mergeGateIndex, StringComparison.Ordinal);

        Assert.True(sourceReadIndex >= 0, "LocalGit approval should read PR source before deploying.");
        Assert.True(applyIndex >= 0, "LocalGit approval should apply the production app before merging.");
        Assert.True(waitIndex >= 0, "LocalGit approval should wait for readiness after Kubernetes accepts the manifest.");
        Assert.True(mergeGateIndex >= 0, "Readiness reconcile should run the LocalGit merge gate after the production app is healthy.");
        Assert.True(markRunningIndex >= 0, "Readiness reconcile should mark the public app running after merge checks.");
        Assert.Contains("var merged = await localGit.MergePullRequestAsync", reconciler);
        Assert.DoesNotContain("localGit.MergePullRequestAsync", endpoint);
        Assert.DoesNotContain("store.MarkBoardPublicAppRunning", endpoint);
        Assert.True(applyIndex < waitIndex, "Production apply must happen before readiness waiting is recorded.");
        Assert.True(mergeGateIndex < markRunningIndex, "RDO should only mark the production app running after any LocalGit merge succeeds.");
    }

    [Fact]
    public void GitHub_webhook_queues_public_app_readiness_instead_of_marking_running_after_apply()
    {
        var program = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "Rosenvall.DevOps.Api", "Program.cs"));
        var endpointStart = program.IndexOf("app.MapPost(\"/integrations/github/webhook\"", StringComparison.Ordinal);
        var endpointEnd = program.IndexOf("app.MapGet(\"/integrations/github/user-authorization/callback\"", endpointStart, StringComparison.Ordinal);
        var endpoint = program[endpointStart..endpointEnd];

        var applyIndex = endpoint.IndexOf("var apply = await previews.ApplyAsync", StringComparison.Ordinal);
        var waitIndex = endpoint.IndexOf("store.MarkBoardPublicAppWaitingForReadiness", StringComparison.Ordinal);

        Assert.True(applyIndex >= 0, "GitHub webhook should still apply the production app manifest.");
        Assert.True(waitIndex >= 0, "GitHub webhook should hand off to readiness monitoring after apply succeeds.");
        Assert.True(applyIndex < waitIndex, "Kubernetes apply must happen before readiness waiting is recorded.");
        Assert.DoesNotContain("store.MarkBoardPublicAppRunning", endpoint);
        Assert.DoesNotContain("store.ApprovePullRequest", endpoint);
    }

    [Fact]
    public void Delivery_mutation_endpoints_derive_audit_actor_from_claims()
    {
        var program = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "Rosenvall.DevOps.Api", "Program.cs"));

        Assert.Contains("static string AuditActorFromClaims(ClaimsPrincipal user)", program);
        Assert.Contains("store.StartImplementationRun(workItemId, request with { Actor = actor })", program);
        Assert.Contains("store.StartPullRequestReviewFixRun(workItemId, request with { Actor = actor })", program);
        Assert.Contains("store.ApproveAiRun(aiRunId, actor)", program);
        Assert.Contains("previewImplementationRunner.RunAsync(result, actor, CancellationToken.None)", program);
        Assert.Contains("store.ApprovePullRequest(workItemId, actor)", program);
        Assert.Contains("store.StartPreviewPromotionRun(workItemId, actor)", program);
        Assert.Contains("store.MarkPipelineRunExecuting(pipelineRunId, actor)", program);

        var approvePlanEndpoint = EndpointSnippet(program, "api.MapPost(\"/ai-runs/{aiRunId:guid}/approve\"", "api.MapPost(\"/ai-runs/{aiRunId:guid}/discard\"");
        Assert.Contains("var actor = AuditActorFromClaims(user);", approvePlanEndpoint);
        Assert.DoesNotContain("request.ApprovedBy", approvePlanEndpoint);

        var approvePrEndpoint = EndpointSnippet(program, "api.MapPost(\"/work-items/{workItemId:guid}/approve-pr\"", "api.MapPost(\"/work-items/{workItemId:guid}/preview/start\"");
        Assert.Contains("var actor = AuditActorFromClaims(user);", approvePrEndpoint);
        Assert.DoesNotContain("request.ApprovedBy", approvePrEndpoint);

        var previewStartEndpoint = EndpointSnippet(program, "api.MapPost(\"/work-items/{workItemId:guid}/preview/start\"", "api.MapPost(\"/work-items/{workItemId:guid}/preview/stop\"");
        Assert.Contains("var actor = AuditActorFromClaims(user);", previewStartEndpoint);
        Assert.DoesNotContain("request.Actor", previewStartEndpoint);

        var pipelineEndpoint = EndpointSnippet(program, "api.MapPost(\"/pipeline-runs/{pipelineRunId:guid}/execute\"", "api.MapGet(\"/pipeline-runs/{pipelineRunId:guid}/manifest\"");
        Assert.Contains("var actor = AuditActorFromClaims(user);", pipelineEndpoint);
        Assert.DoesNotContain("request.Actor", pipelineEndpoint);
    }

    [Fact]
    public void Local_git_pull_request_approval_state_blocks_after_rdo_approval()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var (_, item, _) = CreateLocalGitPreviewPullRequest(store);

        store.MarkPullRequestMergeState(item.Id, "merged", true);
        store.ApprovePullRequest(item.Id, "Demo");

        var state = store.GetLocalPullRequestApprovalState(item.Id, "closed");

        Assert.False(state.CanApprove);
        Assert.Equal("merged", state.Status);
        Assert.Contains("Merged and deployed by Demo", state.Message);
        Assert.NotNull(state.PullRequestApprovedAt);
        Assert.Equal("Demo", state.PullRequestApprovedBy);
    }

    [Fact]
    public void Local_git_pull_request_approval_state_blocks_closed_unapproved_prs_and_unresolved_comments()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var (_, item, _) = CreateLocalGitPreviewPullRequest(store);

        var closed = store.GetLocalPullRequestApprovalState(item.Id, "closed");

        Assert.False(closed.CanApprove);
        Assert.Equal("blocked", closed.Status);
        Assert.Contains("closed", closed.Message, StringComparison.OrdinalIgnoreCase);

        store.AddPullRequestReviewComment(item.Id, new CreatePullRequestReviewCommentRequest("src/App.tsx", "new", 12, "+ <main />", "Fix this before merge."), "crille");
        var commented = store.GetLocalPullRequestApprovalState(item.Id, "open");

        Assert.False(commented.CanApprove);
        Assert.Equal("blocked", commented.Status);
        Assert.Contains("Resolve 1 review comment", commented.Message);
    }

    [Fact]
    public void Board_owned_local_git_repositories_are_identified_for_cleanup()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var localRepository = store.CreateRepository(new CreateRepositoryRequest("LocalGit", "demo-app", "http://forgejo/rdo/demo-app.git", "main", null, "rdo", "react-preview", "preview-then-pr"));
        var sharedRepository = store.CreateRepository(new CreateRepositoryRequest("LocalGit", "shared-app", "http://forgejo/rdo/shared-app.git", "main", null, "rdo", "react-preview", "preview-then-pr"));
        var githubRepository = store.CreateRepository(new CreateRepositoryRequest("GitHub", "github-app", "https://github.com/carnufex/github-app.git", "main", "https://github.com/carnufex/github-app", "carnufex", "react-preview", "preview-then-pr"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("Demo app", localRepository.Id, null, null, null, null, null, ImplementationProfile: "react-preview"))!;
        var otherBoard = store.CreateBoard(workspace.Id, new CreateBoardRequest("Other app", sharedRepository.Id, null, null, null, null, null, ImplementationProfile: "react-preview"))!;
        store.CreateBoard(workspace.Id, new CreateBoardRequest("Shared app", sharedRepository.Id, null, null, null, null, null, ImplementationProfile: "react-preview"));
        store.CreateBoard(workspace.Id, new CreateBoardRequest("GitHub app", githubRepository.Id, null, null, null, null, null, ImplementationProfile: "react-preview"));
        store.LinkRepositoryToBoard(board.Id, new LinkBoardRepositoryRequest(githubRepository.Id, false, "react-preview"));
        store.CreateBoardSecret(board.Id, new CreateBoardSecretRequest("LOCAL_TOKEN", "value", localRepository.Id));
        store.CreateBoardSecret(board.Id, new CreateBoardSecretRequest("GITHUB_TOKEN", "value", githubRepository.Id));

        var owned = store.GetBoardOwnedLocalGitRepositories(board.Id);
        var shared = store.GetBoardOwnedLocalGitRepositories(otherBoard.Id);

        Assert.Contains(owned, repository => repository.Id == localRepository.Id);
        Assert.DoesNotContain(owned, repository => repository.Id == githubRepository.Id);
        Assert.Empty(shared);

        store.DeleteRepositoryMetadata(owned.Select(repository => repository.Id));

        Assert.DoesNotContain(store.GetRepositories(), repository => repository.Id == localRepository.Id);
        Assert.Contains(store.GetRepositories(), repository => repository.Id == sharedRepository.Id);
        Assert.Contains(store.GetRepositories(), repository => repository.Id == githubRepository.Id);
        Assert.DoesNotContain(store.GetBoardSecrets(board.Id), secret => secret.RepositoryId == localRepository.Id);
        Assert.Contains(store.GetBoardSecrets(board.Id), secret => secret.RepositoryId == githubRepository.Id);
    }

    [Fact]
    public void Preview_promotion_writing_source_status_is_pending()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest("GitHub", "clock-app", "https://github.com/carnufex/clock-app.git", "main", "https://github.com/carnufex/clock-app", "carnufex", "react-preview"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("Clock app", repository.Id, null, null, null, null, null, ImplementationProfile: "react-preview"))!;
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Clock toggle", "Build a web clock.", "Todo", "Medium", null));
        var aiRun = store.StartAiPlan(item.Id, "codex", "gpt-5.5", "Build preview.")!;
        store.ApproveAiRun(aiRun.Id, "crille");
        store.CompletePreviewImplementation(item.Id, [
            new PreviewSourceFile("index", "index.html", "<div id=\"root\"></div>"),
            new PreviewSourceFile("app", "src/App.tsx", "export default function App(){return <main>Clock</main>}")
        ], "codex");
        store.MarkPreviewRunning(item.Id, "test", "Preview is running.");

        var run = store.StartPreviewPromotionRun(item.Id, "crille")!;
        store.UpdateImplementationRun(run.Id, "WritingPreviewSource", "RDO_STEP=WritingPreviewSource");

        Assert.Equal(run.Id, store.GetPendingImplementationRun(item.Id)!.Id);
    }

    [Fact]
    public void Production_app_manifest_uses_stored_preview_source_and_board_hostname()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest("GitHub", "clock-app", "https://github.com/carnufex/clock-app.git", "main", "https://github.com/carnufex/clock-app", "carnufex", "react-preview"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("Demo Klocka", repository.Id, null, null, null, null, null, ImplementationProfile: "react-preview"))!;
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Clock toggle", "Build a web clock.", "Todo", "Medium", null));
        var aiRun = store.StartAiPlan(item.Id, "codex", "gpt-5.5", "Build preview.")!;
        store.ApproveAiRun(aiRun.Id, "crille");
        store.CompletePreviewImplementation(item.Id, [
            new PreviewSourceFile("app", "src/App.tsx", "export default function App(){return <main>Clock</main>}")
        ], "codex");
        store.MarkPreviewRunning(item.Id, "test", "Preview is running.");
        var run = store.StartPreviewPromotionRun(item.Id, "crille")!;
        store.UpdateImplementationRun(run.Id, "PullRequestReady", "RDO_COMMIT=abc123\nRDO_PULL_REQUEST_URL=https://github.com/carnufex/clock-app/pull/2");

        var app = store.QueueBoardPublicAppDeployment(item.Id, "crille")!;
        var manifest = store.RenderBoardPublicAppManifest(board.Id)!;
        var waitingBoard = store.MarkBoardPublicAppWaitingForReadiness(board.Id, "deployment.apps/demo configured")!;
        var provisioningBoard = store.UpdateBoardPublicAppHealth(board.Id, PreviewHealthCheckResult.Provisioning("Waiting for pod readiness.", "Deployment has 0/1 available replicas."))!;
        var updatedBoard = store.UpdateBoardPublicAppHealth(board.Id, PreviewHealthCheckResult.Running("demo-pod", "Deployment is available and at least one app pod is ready."))!;

        Assert.Equal("demo-klocka.rosenvall.se", app.Hostname);
        Assert.Equal("https://demo-klocka.rosenvall.se", app.Url);
        Assert.Equal("devops-app-demo-klocka", app.Namespace);
        Assert.Equal(run.Id, app.SourceImplementationRunId);
        Assert.Equal("WaitingForReadiness", waitingBoard.PublicApp!.Status);
        Assert.Contains("deployment.apps/demo configured", waitingBoard.PublicApp.Message);
        Assert.Equal("WaitingForReadiness", provisioningBoard.PublicApp!.Status);
        Assert.Equal("Deployment has 0/1 available replicas.", provisioningBoard.PublicApp.Message);
        Assert.Equal("Running", updatedBoard.PublicApp!.Status);
        Assert.Equal("https://demo-klocka.rosenvall.se", updatedBoard.PublicApp.Url);
        Assert.NotNull(updatedBoard.PublicApp.LastDeployedAt);
        Assert.Contains("namespace: devops-app-demo-klocka", manifest);
        Assert.Contains("app.kubernetes.io/part-of: rosenvall-devops-board-app", manifest);
        Assert.Contains("- demo-klocka.rosenvall.se", manifest);
        Assert.Contains("export default function App()", manifest);
        Assert.DoesNotContain("app.kubernetes.io/part-of: rosenvall-devops-preview", manifest);
        Assert.DoesNotContain("npm run build", manifest);
        Assert.DoesNotContain("codex exec", manifest);
    }

    [Fact]
    public void Production_app_deployment_waits_for_readiness_after_apply()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var (repository, item, _) = CreateLocalGitPreviewPullRequest(store);
        var board = store.GetBoards(store.GetWorkspaces().First().Id).Single(entry => entry.Repository?.Id == repository.Id);

        store.QueueBoardPublicAppDeployment(item.Id, "crille");
        Assert.Single(store.GetBoardPublicAppsAwaitingDeployment());
        Assert.Empty(store.GetBoardPublicAppsAwaitingReadiness());

        var waiting = store.MarkBoardPublicAppWaitingForReadiness(board.Id, "kubectl apply completed.")!;
        Assert.Equal("WaitingForReadiness", waiting.PublicApp!.Status);
        Assert.Empty(store.GetBoardPublicAppsAwaitingDeployment());
        Assert.Single(store.GetBoardPublicAppsAwaitingReadiness());

        var failed = store.UpdateBoardPublicAppHealth(board.Id, PreviewHealthCheckResult.Failed("ImagePullBackOff", "app container is ImagePullBackOff."))!;
        Assert.Equal("Failed", failed.PublicApp!.Status);
        Assert.Equal("ImagePullBackOff", failed.PublicApp.FailureReason);
        Assert.Empty(store.GetBoardPublicAppsAwaitingReadiness());
    }

    [Fact]
    public void Runtime_health_requires_service_cluster_ip_and_ports()
    {
        using var ready = JsonDocument.Parse("""
        {
          "spec": {
            "clusterIP": "10.43.1.10",
            "ports": [{ "port": 80 }]
          }
        }
        """);
        using var pending = JsonDocument.Parse("""
        {
          "spec": {
            "clusterIP": "",
            "ports": []
          }
        }
        """);

        Assert.True(PreviewEnvironmentOrchestrator.IsServiceReady(ready.RootElement));
        Assert.False(PreviewEnvironmentOrchestrator.IsServiceReady(pending.RootElement));
    }

    [Fact]
    public void Runtime_health_requires_accepted_http_route()
    {
        using var accepted = JsonDocument.Parse("""
        {
          "status": {
            "parents": [{
              "conditions": [{
                "type": "Accepted",
                "status": "True"
              }]
            }]
          }
        }
        """);
        using var rejected = JsonDocument.Parse("""
        {
          "status": {
            "parents": [{
              "conditions": [{
                "type": "Accepted",
                "status": "False",
                "reason": "NoMatchingParent",
                "message": "gateway listener did not match"
              }]
            }]
          }
        }
        """);

        var acceptedResult = PreviewEnvironmentOrchestrator.AnalyzeHttpRouteReadiness(accepted.RootElement);
        var rejectedResult = PreviewEnvironmentOrchestrator.AnalyzeHttpRouteReadiness(rejected.RootElement);

        Assert.True(acceptedResult.Ready);
        Assert.False(rejectedResult.Ready);
        Assert.Contains("NoMatchingParent", rejectedResult.Message);
    }

    [Fact]
    public void Production_app_manifest_uses_latest_approved_pr_source_after_review_fix()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var (repository, item, promotionRun) = CreateLocalGitPreviewPullRequest(store);
        var board = store.GetBoards(store.GetWorkspaces().First().Id).Single(entry => entry.Repository?.Id == repository.Id);
        var reviewComment = store.AddPullRequestReviewComment(item.Id, new CreatePullRequestReviewCommentRequest(
            "src/App.tsx",
            "new",
            1,
            "+ export default function App(){return <main>Old preview</main>}",
            "Use the revised PR source for production."),
            "crille");
        var fixRun = store.StartPullRequestReviewFixRun(item.Id, new StartPullRequestReviewFixRequest("crille", "high"))!;
        store.UpdateImplementationRun(fixRun.Id, "PullRequestReady", "RDO_COMMIT=def456\nRDO_PULL_REQUEST_URL=http://forgejo.local/rdo/demo-app/pulls/7\nRDO_PULL_REQUEST_NUMBER=7\nRDO_PULL_REQUEST_STATE=open");
        store.UpdatePullRequestReviewComment(item.Id, reviewComment.Id, new UpdatePullRequestReviewCommentRequest(Status: "resolved"), "crille");
        var approvedPrSource = new[]
        {
            new PreviewSourceFile("app", "src/App.tsx", "export default function App(){return <main>Fixed PR source</main>}")
        };

        var app = store.QueueBoardPublicAppDeployment(item.Id, "crille", approvedPrSource, fixRun.CommitSha)!;
        var manifest = store.RenderBoardPublicAppManifest(board.Id)!;
        var reopened = fixture.Reopen();
        var reopenedManifest = reopened.RenderBoardPublicAppManifest(board.Id)!;

        Assert.Equal(fixRun.Id, app.SourceImplementationRunId);
        Assert.Equal("def456", app.CommitSha);
        Assert.Contains("Fixed PR source", manifest);
        Assert.DoesNotContain("Clock</main>", manifest);
        Assert.Contains("Fixed PR source", reopenedManifest);
    }

    [Fact]
    public async Task Deployable_source_snapshot_reader_collects_allowed_files_and_ignores_docs()
    {
        var repositoryId = Guid.NewGuid();
        var trees = new Dictionary<string, RepositorySourceTreeDto>(StringComparer.OrdinalIgnoreCase)
        {
            [""] = new RepositorySourceTreeDto(repositoryId, "LocalGit", "main", "", [
                new RepositorySourceEntryDto("src", "src", "directory"),
                new RepositorySourceEntryDto("README.md", "README.md", "file"),
                new RepositorySourceEntryDto("package.json", "package.json", "file")
            ]),
            ["src"] = new RepositorySourceTreeDto(repositoryId, "LocalGit", "main", "src", [
                new RepositorySourceEntryDto("App.tsx", "src/App.tsx", "file")
            ])
        };
        var files = new Dictionary<string, RepositorySourceFileDto>(StringComparer.OrdinalIgnoreCase)
        {
            ["README.md"] = new RepositorySourceFileDto(repositoryId, "LocalGit", "main", "README.md", "# demo", "utf-8", 6, false, false),
            ["package.json"] = new RepositorySourceFileDto(repositoryId, "LocalGit", "main", "package.json", "{\"scripts\":{\"build\":\"vite build\"}}", "utf-8", 33, false, false),
            ["src/App.tsx"] = new RepositorySourceFileDto(repositoryId, "LocalGit", "main", "src/App.tsx", "export default function App(){return <main/>}", "utf-8", 42, false, false)
        };

        var source = await DeployablePreviewSourceSnapshotReader.ReadAsync(
            path => Task.FromResult(trees.GetValueOrDefault(path)),
            path => Task.FromResult(files.GetValueOrDefault(path)));

        Assert.NotNull(source);
        Assert.Contains(source, file => file.Path == "package.json");
        Assert.Contains(source, file => file.Path == "src/App.tsx");
        Assert.DoesNotContain(source, file => file.Path == "README.md");
    }

    [Fact]
    public async Task Deployable_source_snapshot_reader_rejects_truncated_allowed_files()
    {
        var repositoryId = Guid.NewGuid();
        var tree = new RepositorySourceTreeDto(repositoryId, "LocalGit", "main", "", [
            new RepositorySourceEntryDto("src", "src", "directory")
        ]);
        var srcTree = new RepositorySourceTreeDto(repositoryId, "LocalGit", "main", "src", [
            new RepositorySourceEntryDto("App.tsx", "src/App.tsx", "file")
        ]);

        var source = await DeployablePreviewSourceSnapshotReader.ReadAsync(
            path => Task.FromResult<RepositorySourceTreeDto?>(path == "src" ? srcTree : tree),
            path => Task.FromResult<RepositorySourceFileDto?>(new RepositorySourceFileDto(repositoryId, "LocalGit", "main", path, null, "base64", 2_000_000, false, true)));

        Assert.Null(source);
    }

    [Fact]
    public void Board_cleanup_manifest_includes_production_app_resources()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest("GitHub", "clock-app", "https://github.com/carnufex/clock-app.git", "main", "https://github.com/carnufex/clock-app", "carnufex", "react-preview"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("Demo Klocka", repository.Id, null, null, null, null, null, ImplementationProfile: "react-preview"))!;
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Clock toggle", "Build a web clock.", "Todo", "Medium", null));
        var aiRun = store.StartAiPlan(item.Id, "codex", "gpt-5.5", "Build preview.")!;
        store.ApproveAiRun(aiRun.Id, "crille");
        store.CompletePreviewImplementation(item.Id, [
            new PreviewSourceFile("app", "src/App.tsx", "export default function App(){return <main>Clock</main>}")
        ], "codex");
        store.MarkPreviewRunning(item.Id, "test", "Preview is running.");
        var run = store.StartPreviewPromotionRun(item.Id, "crille")!;
        store.UpdateImplementationRun(run.Id, "PullRequestReady", "RDO_PULL_REQUEST_URL=https://github.com/carnufex/clock-app/pull/2");
        store.QueueBoardPublicAppDeployment(item.Id, "crille");

        var manifest = store.RenderBoardCleanupManifest(board.Id, new ConfigurationBuilder().Build())!;

        Assert.Contains("name: devops-app-demo-klocka", manifest);
        Assert.Contains("kind: Namespace", manifest);
        Assert.Contains("kind: HTTPRoute", manifest);
        Assert.Contains("kind: NetworkPolicy", manifest);
        Assert.Contains("name: demo-klocka-source", manifest);
        Assert.DoesNotContain("rosenvall-devops-codex-home", manifest);
        Assert.DoesNotContain("kind: Repository", manifest);
    }

    [Fact]
    public void Existing_previewable_board_backfills_public_app_from_stored_preview_source()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest("GitHub", "clock-app", "https://github.com/carnufex/clock-app.git", "main", "https://github.com/carnufex/clock-app", "carnufex", "react-preview"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("Demo Klocka", repository.Id, null, null, null, null, null, ImplementationProfile: "react-preview"))!;
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Clock toggle", "Build a web clock.", "Done", "Medium", null));
        var aiRun = store.StartAiPlan(item.Id, "codex", "gpt-5.5", "Build preview.")!;
        store.ApproveAiRun(aiRun.Id, "crille");
        store.CompletePreviewImplementation(item.Id, [
            new PreviewSourceFile("app", "src/App.tsx", "export default function App(){return <main>Clock</main>}")
        ], "codex");
        store.MarkPreviewRunning(item.Id, "test", "Preview is running.");
        var run = store.StartPreviewPromotionRun(item.Id, "crille")!;
        store.UpdateImplementationRun(run.Id, "PullRequestReady", "RDO_PULL_REQUEST_URL=https://github.com/carnufex/clock-app/pull/2");
        store.ApprovePullRequest(item.Id, "github");

        var reopened = fixture.Reopen();
        var upgraded = reopened.GetBoard(board.Id)!;
        var manifest = reopened.RenderBoardPublicAppManifest(board.Id)!;

        Assert.Equal("Queued", upgraded.PublicApp!.Status);
        Assert.Equal("demo-klocka.rosenvall.se", upgraded.PublicApp.Hostname);
        Assert.Contains("namespace: devops-app-demo-klocka", manifest);
        Assert.Contains("export default function App()", manifest);
    }

    [Fact]
    public void Active_review_preview_does_not_backfill_public_app_on_restart()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest("GitHub", "clock-app", "https://github.com/carnufex/clock-app.git", "main", "https://github.com/carnufex/clock-app", "carnufex", "react-preview"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("Demo Klocka", repository.Id, null, null, null, null, null, ImplementationProfile: "react-preview"))!;
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Clock toggle", "Build a web clock.", "Review", "Medium", null));
        var aiRun = store.StartAiPlan(item.Id, "codex", "gpt-5.5", "Build preview.")!;
        store.ApproveAiRun(aiRun.Id, "crille");
        store.CompletePreviewImplementation(item.Id, [
            new PreviewSourceFile("app", "src/App.tsx", "export default function App(){return <main>Clock</main>}")
        ], "codex");
        store.MarkPreviewRunning(item.Id, "test", "Preview is running.");
        var run = store.StartPreviewPromotionRun(item.Id, "crille")!;
        store.UpdateImplementationRun(run.Id, "PullRequestReady", "RDO_PULL_REQUEST_URL=https://github.com/carnufex/clock-app/pull/2");

        var reopened = fixture.Reopen();
        var refreshed = reopened.GetBoard(board.Id)!;

        Assert.Equal("NotDeployed", refreshed.PublicApp!.Status);
        Assert.Null(reopened.RenderBoardPublicAppManifest(board.Id));
        Assert.Single(reopened.GetPreviewPromotionRunsAwaitingPublicAppReconcile());
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
    public void GitOps_settings_and_board_ai_context_are_defaulted_upserted_and_persisted()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest("GitHub", "rosenvalls-homelab", "https://github.com/rosenvall/rosenvalls-homelab.git", "main", "https://github.com/rosenvall/rosenvalls-homelab", "rosenvall", "gitops-homelab"));

        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest(
            "rosenvalls-homelab",
            repository.Id,
            null,
            null,
            null,
            null,
            null,
            ImplementationProfile: "gitops-homelab"))!;

        Assert.Equal(["apps/", "clusters/", "infrastructure/", "kubernetes/", "tofu/"], board.GitOpsSettings!.AllowedPaths);
        Assert.Equal("argocd", board.GitOpsSettings.ArgoNamespace);
        Assert.True(board.AiContext!.AskWhenUncertain);

        var gitops = store.UpsertBoardGitOpsSettings(board.Id, new BoardGitOpsSettingsRequest(["clusters/prod/", "apps/home/"], "argocd-prod", "app.kubernetes.io/part-of=homelab"));
        var aiContext = store.UpsertBoardAiContext(board.Id, new BoardAiContextRequest("Never delete resources without explicit approval.", ["kubernetes", "argocd", "gitops-homelab"], true));
        var reopened = fixture.Reopen();
        var reopenedBoard = reopened.GetBoard(board.Id)!;

        Assert.Equal(["clusters/prod/", "apps/home/"], gitops!.AllowedPaths);
        Assert.Equal("app.kubernetes.io/part-of=homelab", reopened.GetBoardGitOpsSettings(board.Id)!.ArgoApplicationSelector);
        Assert.Equal("Never delete resources without explicit approval.", aiContext!.Instructions);
        Assert.Equal(["kubernetes", "argocd", "gitops-homelab"], reopened.GetBoardAiContext(board.Id)!.EnabledSkills);
        Assert.Equal("argocd-prod", reopenedBoard.GitOpsSettings!.ArgoNamespace);
        Assert.Contains("argocd", reopenedBoard.AiContext!.EnabledSkills);
    }

    [Fact]
    public void Legacy_gitops_default_paths_are_upgraded_without_overriding_custom_paths()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest("GitHub", "rosenvalls-homelab", "https://github.com/rosenvall/rosenvalls-homelab.git", "main", "https://github.com/rosenvall/rosenvalls-homelab", "rosenvall", "gitops-homelab"));
        var legacyBoard = store.CreateBoard(workspace.Id, new CreateBoardRequest(
            "legacy homelab",
            repository.Id,
            null,
            null,
            null,
            null,
            null,
            ImplementationProfile: "gitops-homelab",
            GitOpsSettings: new BoardGitOpsSettingsRequest(["apps/", "clusters/", "infrastructure/"], "argocd", "")))!;
        var customBoard = store.CreateBoard(workspace.Id, new CreateBoardRequest(
            "custom homelab",
            repository.Id,
            null,
            null,
            null,
            null,
            null,
            ImplementationProfile: "gitops-homelab",
            GitOpsSettings: new BoardGitOpsSettingsRequest(["clusters/prod/"], "argocd", "")))!;

        Assert.Equal(["apps/", "clusters/", "infrastructure/", "kubernetes/", "tofu/"], store.GetBoardGitOpsSettings(legacyBoard.Id)!.AllowedPaths);
        Assert.Equal(["clusters/prod/"], store.GetBoardGitOpsSettings(customBoard.Id)!.AllowedPaths);
    }

    [Fact]
    public void GitOps_settings_reject_unsafe_paths_namespace_and_selector_values()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest("GitHub", "rosenvalls-homelab", "https://github.com/rosenvall/rosenvalls-homelab.git", "main", "https://github.com/rosenvall/rosenvalls-homelab", "rosenvall", "gitops-homelab"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("homelab", repository.Id, null, null, null, null, null, ImplementationProfile: "gitops-homelab"))!;

        var settings = store.UpsertBoardGitOpsSettings(board.Id, new BoardGitOpsSettingsRequest(
            ["kubernetes/applications", "../escape", "/absolute", "clusters\\prod", "bad:path"],
            "argocd;rm-rf",
            "app=$(whoami)"))!;

        Assert.Equal(["kubernetes/applications/", "clusters/prod/"], settings.AllowedPaths);
        Assert.Equal("argocd", settings.ArgoNamespace);
        Assert.Equal("", settings.ArgoApplicationSelector);
    }

    [Fact]
    public void Create_board_persists_detected_profile_and_ai_skill_suggestions()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest("GitHub", "Gatebound", "https://github.com/carnufex/Gatebound.git", "main", "https://github.com/carnufex/Gatebound", "carnufex", "unity"));

        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest(
            "Gatebound",
            repository.Id,
            null,
            null,
            null,
            null,
            null,
            ImplementationProfile: "unity",
            AiContext: new BoardAiContextRequest("Unity project detected.", ["unity", "unity-project", "csharp"], true)))!;

        var reopened = fixture.Reopen();
        var reopenedBoard = reopened.GetBoard(board.Id)!;

        Assert.Equal("unity", reopenedBoard.Repositories!.Single(entry => entry.IsPrimary).ImplementationProfile);
        Assert.Equal("Unity project detected.", reopenedBoard.AiContext!.Instructions);
        Assert.Contains("unity-project", reopenedBoard.AiContext.EnabledSkills);
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
        Assert.Contains("activeDeadlineSeconds: 3600", manifest);
        Assert.Contains("ssh://git.rosenvall.se/platform/api.git", manifest);
        Assert.Contains("ROSENVALL_PIPELINE_RUN_ID", manifest);
    }

    [Fact]
    public void Pipeline_runs_validate_board_repository_work_item_and_authorization_scope()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repositoryA = store.CreateRepository(new CreateRepositoryRequest("GitHub", "org/app-a", "https://github.com/org/app-a.git", "main", "https://github.com/org/app-a"));
        var repositoryB = store.CreateRepository(new CreateRepositoryRequest("GitHub", "org/app-b", "https://github.com/org/app-b.git", "main", "https://github.com/org/app-b"));
        var boardA = store.CreateBoard(workspace.Id, new CreateBoardRequest("App A", repositoryA.Id, null, null, null, null, null))!;
        var boardB = store.CreateBoard(workspace.Id, new CreateBoardRequest("App B", repositoryB.Id, null, null, null, null, null))!;
        var itemA = store.CreateWorkItem(new CreateWorkItemRequest(boardA.Id, "Feature", "Build A", "Run scoped build.", "Todo", "Medium", null))!;
        var validRequest = new RecordPipelineRunRequest(repositoryA.Id, boardA.Id, itemA.Id, "Build", "Queued", "Build A");

        var run = store.RecordPipelineRun(validRequest);

        Assert.NotNull(run);
        Assert.Null(store.RecordPipelineRun(new RecordPipelineRunRequest(repositoryB.Id, boardA.Id, itemA.Id, "Build", "Queued", "Wrong repository")));
        Assert.Null(store.RecordPipelineRun(new RecordPipelineRunRequest(repositoryA.Id, boardB.Id, itemA.Id, "Build", "Queued", "Wrong board")));

        var owner = store.GetOrCreateUser(new UserIdentityRequest("authentik|owner", "Owner", "owner@example.com"));
        var guest = store.GetOrCreateUser(new UserIdentityRequest("authentik|guest", "Guest", "guest@example.com"));
        var team = store.CreateTeam(new CreateTeamRequest("App A team"), owner.Subject);
        store.UpsertBoardTeamAccess(boardA.Id, team.Id, "Member");

        Assert.True(store.CanRecordPipelineRun(validRequest, owner.Subject));
        Assert.False(store.CanRecordPipelineRun(validRequest, guest.Subject));
        Assert.True(store.CanMutatePipelineRun(run.Id, owner.Subject));
        Assert.False(store.CanMutatePipelineRun(run.Id, guest.Subject));
        Assert.Equal(1, store.GetMetrics(boardA.Id, owner.Subject).PipelineRuns);
        Assert.Equal(0, store.GetMetrics(boardA.Id, guest.Subject).PipelineRuns);
    }

    [Fact]
    public void Repository_only_pipeline_runs_require_mutating_repository_access()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest("LocalGit", "rdo/app", "http://forgejo.local/rdo/app.git", "main", null, "rdo"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("App", repository.Id, null, null, null, null, null))!;
        var owner = store.GetOrCreateUser(new UserIdentityRequest("authentik|owner", "Owner", "owner@example.com"));
        var viewer = store.GetOrCreateUser(new UserIdentityRequest("authentik|viewer", "Viewer", "viewer@example.com"));
        var member = store.GetOrCreateUser(new UserIdentityRequest("authentik|member", "Member", "member@example.com"));
        var team = store.CreateTeam(new CreateTeamRequest("App team"), owner.Subject);
        store.UpsertTeamMember(team.Id, new UpsertTeamMemberRequest(viewer.Id, "Viewer"));
        store.UpsertTeamMember(team.Id, new UpsertTeamMemberRequest(member.Id, "Member"));
        store.UpsertBoardTeamAccess(board.Id, team.Id, "Owner");
        var repositoryOnlyRequest = new RecordPipelineRunRequest(repository.Id, null, null, "Build", "Queued", "Repository-only build.");

        Assert.True(store.CanViewRepository(repository.Id, viewer.Subject));
        Assert.False(store.CanActOnRepository(repository.Id, viewer.Subject));
        Assert.False(store.CanRecordPipelineRun(repositoryOnlyRequest, viewer.Subject));
        Assert.True(store.CanRecordPipelineRun(repositoryOnlyRequest, member.Subject));

        var run = store.RecordPipelineRun(repositoryOnlyRequest)!;
        Assert.False(store.CanMutatePipelineRun(run.Id, viewer.Subject));
        Assert.True(store.CanMutatePipelineRun(run.Id, member.Subject));
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
                ["Repositories:Forgejo:Password"] = "service-token",
                ["Authentik:Authority"] = "https://authentik.rosenvall.se",
                ["Authentik:UsersEndpoint"] = "https://authentik.rosenvall.se/api/v3/core/users/"
            })
            .Build();

        var settings = fixture.Store.GetSettings(
            configuration,
            localGitReadiness: new LocalGitReadinessDto(true, true, true, "https://git.rosenvall.se/api/v1", "Local Git is ready.", 200));

        Assert.Equal("LocalGit", settings.Repositories.Provider);
        Assert.Equal("InternalForgejo", settings.Repositories.Mode);
        Assert.True(settings.Repositories.CanCreateRepositories);
        Assert.True(settings.Repositories.LocalGitEnabled);
        Assert.True(settings.Repositories.LocalGitAvailable);
        Assert.Equal("https://git.rosenvall.se/api/v1", settings.Repositories.ApiBaseUrl);
        Assert.True(settings.Authentik.Enabled);
        Assert.Equal("https://authentik.rosenvall.se/api/v3/core/users/", settings.Authentik.UsersEndpoint);
    }

    [Fact]
    public void Appsettings_base_is_kubernetes_safe_and_development_carries_local_kubeconfig_discovery()
    {
        var root = FindRepositoryRoot();
        var baseSettingsPath = Path.Combine(root, "src", "Rosenvall.DevOps.Api", "appsettings.json");
        var developmentSettingsPath = Path.Combine(root, "src", "Rosenvall.DevOps.Api", "appsettings.Development.json");
        using var baseSettings = JsonDocument.Parse(File.ReadAllText(baseSettingsPath));
        using var developmentSettings = JsonDocument.Parse(File.ReadAllText(developmentSettingsPath));

        var baseRoot = baseSettings.RootElement;
        var developmentRoot = developmentSettings.RootElement;

        Assert.Equal("", baseRoot.GetProperty("Preview").GetProperty("KubeconfigPath").GetString());
        Assert.Equal("", baseRoot.GetProperty("Pipelines").GetProperty("KubeconfigPath").GetString());
        Assert.Equal("tofu/output/kubeconfig", developmentRoot.GetProperty("Preview").GetProperty("KubeconfigPath").GetString());
        Assert.Equal("tofu/output/kubeconfig", developmentRoot.GetProperty("Pipelines").GetProperty("KubeconfigPath").GetString());
        Assert.DoesNotContain("https://git.rosenvall.se", File.ReadAllText(baseSettingsPath), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"Provider\": \"Forgejo\"", File.ReadAllText(baseSettingsPath), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"Mode\": \"LinkExistingFirst\"", File.ReadAllText(baseSettingsPath), StringComparison.OrdinalIgnoreCase);
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
    public void GitHub_app_installation_sync_normalizes_account_metadata()
    {
        using var document = JsonDocument.Parse("""
        [
          {
            "id": 3800502,
            "account": { "login": "carnufex", "type": "User" }
          },
          {
            "id": 3800503,
            "account": { "login": "rosenvall-corp", "type": "Organization" }
          }
        ]
        """);

        var installations = GitHubRepositoryClient.NormalizeInstallations(document.RootElement, "github-app", "Installed");

        Assert.Collection(installations,
            first =>
            {
                Assert.Equal(3800502, first.InstallationId);
                Assert.Equal("carnufex", first.AccountLogin);
                Assert.Equal("User", first.AccountType);
            },
            second =>
            {
                Assert.Equal(3800503, second.InstallationId);
                Assert.Equal("rosenvall-corp", second.AccountLogin);
                Assert.Equal("Organization", second.AccountType);
            });
    }

    [Fact]
    public void GitHub_webhook_signature_accepts_valid_sha256_signature()
    {
        var payload = Encoding.UTF8.GetBytes("""{"action":"closed"}""");
        var secret = "webhook-secret";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var signature = "sha256=" + Convert.ToHexString(hmac.ComputeHash(payload)).ToLowerInvariant();

        Assert.True(GitHubWebhookSignatureVerifier.Verify(payload, signature, secret));
    }

    [Theory]
    [InlineData(null, "webhook-secret")]
    [InlineData("", "webhook-secret")]
    [InlineData("sha1=deadbeef", "webhook-secret")]
    [InlineData("sha256=not-hex", "webhook-secret")]
    [InlineData("sha256=0000000000000000000000000000000000000000000000000000000000000000", "webhook-secret")]
    [InlineData("sha256=0000000000000000000000000000000000000000000000000000000000000000", null)]
    public void GitHub_webhook_signature_rejects_missing_malformed_or_mismatched_signature(string? signature, string? secret)
    {
        var payload = Encoding.UTF8.GetBytes("""{"action":"closed"}""");

        Assert.False(GitHubWebhookSignatureVerifier.Verify(payload, signature, secret));
    }

    [Fact]
    public async Task GitHub_repository_picker_returns_error_result_when_github_times_out()
    {
        using var httpClient = new HttpClient(new DelayedHttpMessageHandler())
        {
            Timeout = TimeSpan.FromMilliseconds(20)
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GitHub:Token"] = "ghp_test"
            })
            .Build();
        var github = new GitHubRepositoryClient(httpClient, configuration);

        var result = await github.GetRepositoriesResultAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Empty(result.Repositories);
        Assert.Contains("timed out", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GitHub_repository_profile_retries_default_branch_when_requested_branch_is_wrong()
    {
        using var httpClient = new HttpClient(new RoutingHttpMessageHandler(request =>
        {
            var url = request.RequestUri?.ToString() ?? "";
            if (url.Contains("/git/trees/main", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"message":"Not Found"}""") };
            }

            if (url.EndsWith("/repos/carnufex/Rosenvalls-Homelab", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse("""{"default_branch":"master"}""");
            }

            if (url.Contains("/git/trees/master", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse("""
                {"tree":[
                  {"path":"kubernetes/applications/application-set.yaml"},
                  {"path":"tofu/talos.tf"},
                  {"path":".codex/skills/cluster-diagnostics/SKILL.md"}
                ]}
                """);
            }

            if (url.Contains("/contents/kubernetes/applications/application-set.yaml", StringComparison.OrdinalIgnoreCase))
            {
                return JsonContent("kind: ApplicationSet\napiVersion: argoproj.io/v1alpha1");
            }

            if (url.Contains("/contents/tofu/talos.tf", StringComparison.OrdinalIgnoreCase))
            {
                return JsonContent("resource \"talos_machine_configuration_apply\" \"worker\" {}");
            }

            if (url.Contains("/contents/.codex/skills/cluster-diagnostics/SKILL.md", StringComparison.OrdinalIgnoreCase))
            {
                return JsonContent("# Cluster Diagnostics\nCheck ArgoCD sync first.");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"message":"Not Found"}""") };
        }));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GitHub:Token"] = "ghp_test"
            })
            .Build();
        var github = new GitHubRepositoryClient(httpClient, configuration);

        var profile = await github.GetRepositoryProfileAsync("carnufex", "Rosenvalls-Homelab", "main", CancellationToken.None);

        Assert.NotNull(profile);
        Assert.Equal("gitops-homelab", profile.ImplementationProfile);
        Assert.Contains("cluster-diagnostics", profile.EnabledSkills);
    }

    [Fact]
    public void Repository_profile_classifier_detects_known_project_shapes()
    {
        var unity = RepositoryProfileClassifier.Classify([
            "Assets/Scripts/PlayerController.cs",
            "Packages/manifest.json",
            "ProjectSettings/ProjectVersion.txt"
        ]);
        var gitops = RepositoryProfileClassifier.Classify([
            "apps/grafana/kustomization.yaml",
            "clusters/homelab/apps.yaml",
            "infrastructure/argocd/application.yaml"
        ], new Dictionary<string, string>
        {
            ["infrastructure/argocd/application.yaml"] = "kind: Application\napiVersion: argoproj.io/v1alpha1"
        });
        var react = RepositoryProfileClassifier.Classify([
            "package.json",
            "src/App.tsx",
            "vite.config.ts"
        ], new Dictionary<string, string>
        {
            ["package.json"] = """{"dependencies":{"@vitejs/plugin-react":"latest","react":"latest"},"devDependencies":{"vite":"latest"}}"""
        });
        var generic = RepositoryProfileClassifier.Classify([
            "README.md",
            "src/service.cs"
        ]);

        Assert.Equal("unity", unity.ImplementationProfile);
        Assert.Contains("unity", unity.EnabledSkills);
        Assert.Equal("gitops-homelab", gitops.ImplementationProfile);
        Assert.Contains("argocd", gitops.EnabledSkills);
        Assert.Equal("react-preview", react.ImplementationProfile);
        Assert.Contains("react", react.EnabledSkills);
        Assert.Equal("code-repo", generic.ImplementationProfile);
    }

    [Fact]
    public void Repository_profile_classifier_detects_rosenvalls_homelab_shape()
    {
        var profile = RepositoryProfileClassifier.Classify([
            ".codex/skills/cloudflare-gateway-routing/SKILL.md",
            ".codex/skills/cluster-diagnostics/SKILL.md",
            ".codex/skills/gitops-app-onboarding/SKILL.md",
            "AGENTS.md",
            "README.md",
            "bootstrap.ps1",
            "kubernetes/applications/application-set.yaml",
            "kubernetes/applications/bikepal/kustomization.yaml",
            "kubernetes/infrastructure/cilium/values.yaml",
            "kubernetes/infrastructure/longhorn/values.yaml",
            "tofu/main.tf",
            "tofu/talos.tf"
        ], new Dictionary<string, string>
        {
            ["README.md"] = "A Proxmox-hosted Talos Kubernetes homelab managed through GitOps. GitOps: ArgoCD syncs kubernetes/ from origin.",
            ["kubernetes/applications/application-set.yaml"] = "kind: ApplicationSet\napiVersion: argoproj.io/v1alpha1",
            ["kubernetes/applications/bikepal/externalsecret.yaml"] = "kind: ExternalSecret",
            ["tofu/talos.tf"] = "resource \"talos_machine_configuration_apply\" \"worker\" {}"
        });

        Assert.Equal("gitops-homelab", profile.ImplementationProfile);
        Assert.Contains("kubernetes", profile.CapabilityTags!);
        Assert.Contains("opentofu", profile.CapabilityTags!);
        Assert.Contains("talos", profile.CapabilityTags!);
        Assert.Contains("cloudflare-gateway-routing", profile.EnabledSkills);
        Assert.Contains("cluster-diagnostics", profile.EnabledSkills);
        Assert.Contains("gitops-app-onboarding", profile.EnabledSkills);
        Assert.Contains(profile.SkillDrafts!, draft => draft.Name == "gitops-app-onboarding" && draft.Enabled);
    }

    [Fact]
    public void Codex_profile_parser_merges_valid_json_into_scanner_profile()
    {
        var scanner = RepositoryProfileClassifier.Classify(["kubernetes/applications/application-set.yaml"], new Dictionary<string, string>
        {
            ["kubernetes/applications/application-set.yaml"] = "kind: ApplicationSet\napiVersion: argoproj.io/v1alpha1"
        });

        var profile = RepositoryProfileAiParser.Apply(scanner, """
        {
          "implementationProfile": "gitops-homelab",
          "displayName": "Talos GitOps homelab",
          "confidence": 0.97,
          "capabilityTags": ["kubernetes", "argocd", "talos", "opentofu"],
          "enabledSkills": ["argocd", "cluster-diagnostics"],
          "instructions": "Keep every cluster change PR-first and let ArgoCD reconcile.",
          "signals": ["ApplicationSet", "Talos", "OpenTofu"],
          "skillDrafts": [
            {
              "name": "cluster-diagnostics",
              "description": "Diagnose cluster health using kubectl, ArgoCD and app manifests.",
              "content": "Check ArgoCD sync and Kubernetes events before changing manifests.",
              "enabled": true
            }
          ]
        }
        """, "gpt-5.5");

        Assert.Equal("gitops-homelab", profile.ImplementationProfile);
        Assert.Equal("Talos GitOps homelab", profile.DisplayName);
        Assert.Equal("codex", profile.Source);
        Assert.Equal("gpt-5.5", profile.AnalyzerModel);
        Assert.Contains("talos", profile.CapabilityTags!);
        Assert.Contains("cluster-diagnostics", profile.EnabledSkills);
        var draft = Assert.Single(profile.SkillDrafts!);
        Assert.Equal("cluster-diagnostics", draft.Name);
        Assert.True(draft.Enabled);
    }

    [Fact]
    public void Codex_profile_parser_keeps_scanner_profile_when_json_is_invalid()
    {
        var scanner = RepositoryProfileClassifier.Classify(["kubernetes/applications/application-set.yaml"], new Dictionary<string, string>
        {
            ["kubernetes/applications/application-set.yaml"] = "kind: ApplicationSet\napiVersion: argoproj.io/v1alpha1"
        });

        var profile = RepositoryProfileAiParser.Apply(scanner, "not-json", "gpt-5.5");

        Assert.Equal(scanner.ImplementationProfile, profile.ImplementationProfile);
        Assert.Equal("scanner", profile.Source);
        Assert.Contains(profile.Signals, signal => signal.Contains("Codex profile JSON was invalid", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Create_board_persists_modified_repository_profile_draft()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest("GitHub", "Rosenvalls-Homelab", "https://github.com/carnufex/Rosenvalls-Homelab.git", "master", "https://github.com/carnufex/Rosenvalls-Homelab", "carnufex", "gitops-homelab"));
        var profile = new RepositoryProfileDto(
            "gitops-homelab",
            "Talos GitOps homelab",
            0.97,
            ["argocd", "cluster-diagnostics"],
            "Keep every cluster change PR-first.",
            ["kubernetes/", "ApplicationSet"],
            "codex",
            ["kubernetes", "argocd", "talos", "opentofu"],
            [new RepositorySkillDraftDto("cluster-diagnostics", "Cluster diagnostics", "Check ArgoCD sync first.", true)],
            "gpt-5.5");

        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest(
            "Rosenvalls-Homelab",
            repository.Id,
            null,
            null,
            null,
            null,
            null,
            ImplementationProfile: "gitops-homelab",
            AiContext: new BoardAiContextRequest(profile.Instructions, profile.EnabledSkills, true),
            RepositoryProfile: profile))!;
        var reopenedBoard = fixture.Reopen().GetBoard(board.Id)!;

        var linked = Assert.Single(reopenedBoard.Repositories!);
        Assert.Equal("gitops-homelab", linked.ImplementationProfile);
        Assert.NotNull(linked.Profile);
        Assert.Equal("Talos GitOps homelab", linked.Profile!.DisplayName);
        Assert.Contains("talos", linked.Profile.CapabilityTags!);
        Assert.Contains(linked.Profile.SkillDrafts!, draft => draft.Name == "cluster-diagnostics");
        Assert.Contains("cluster-diagnostics", reopenedBoard.AiContext!.EnabledSkills);
    }

    [Fact]
    public void Settings_update_persists_modified_repository_profile_draft()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest("GitHub", "Rosenvalls-Homelab", "https://github.com/carnufex/Rosenvalls-Homelab.git", "master", "https://github.com/carnufex/Rosenvalls-Homelab", "carnufex", "code-repo"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("Rosenvalls-Homelab", repository.Id, null, null, null, null, null))!;
        var profile = new RepositoryProfileDto(
            "gitops-homelab",
            "Talos GitOps homelab",
            0.96,
            ["argocd", "gitops-app-onboarding"],
            "Use PR-first GitOps changes.",
            ["ApplicationSet"],
            "user",
            ["kubernetes", "argocd"],
            [new RepositorySkillDraftDto("gitops-app-onboarding", "App onboarding", "Create kustomization and namespace first.", true)]);

        var updated = store.UpsertBoardRepositoryProfile(board.Id, repository.Id, profile);
        var reopenedBoard = fixture.Reopen().GetBoard(board.Id)!;

        Assert.NotNull(updated);
        var linked = Assert.Single(reopenedBoard.Repositories!);
        Assert.Equal("gitops-homelab", linked.ImplementationProfile);
        Assert.Equal("Talos GitOps homelab", linked.Profile!.DisplayName);
        Assert.Contains("gitops-app-onboarding", reopenedBoard.AiContext!.EnabledSkills);
        Assert.Contains(linked.Profile.SkillDrafts!, draft => draft.Name == "gitops-app-onboarding");
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
                ["GitHub:TokenSecretName"] = "rosenvall-devops-github",
                ["Ai:Codex:KubernetesRunnerImage"] = "ghcr.io/carnufex/rosenvall-devops-api@sha256:implementationdigest"
            })
            .Build();

        var manifest = store.RenderImplementationRunManifest(implementationRun!.Id, configuration);

        Assert.NotNull(manifest);
        AssertCodexRepositoryJobYamlContract(manifest, "ghcr.io/carnufex/rosenvall-devops-api@sha256:implementationdigest", "ROSENVALL_GIT_TOKEN");
        Assert.Equal("unity", repository.ImplementationProfile);
        Assert.Equal("Queued", implementationRun.Status);
        Assert.Contains("kind: Job", manifest);
        Assert.Contains("namespace: rosenvall-devops", manifest);
        Assert.Contains("automountServiceAccountToken: false", manifest);
        Assert.Contains("initContainers:", manifest);
        Assert.Contains("affinity:", manifest);
        Assert.Contains("podAffinity:", manifest);
        Assert.Contains("app.kubernetes.io/name: rosenvall-devops-api", manifest);
        Assert.Contains("topologyKey: kubernetes.io/hostname", manifest);
        Assert.Contains("name: prepare-codex-home", manifest);
        Assert.Contains("image: ghcr.io/carnufex/rosenvall-devops-api@sha256:implementationdigest", manifest);
        Assert.DoesNotContain("image: ghcr.io/carnufex/rosenvall-devops-api:main", manifest);
        Assert.Contains("name: codex-home-source", manifest);
        Assert.Contains("claimName: rosenvall-devops-codex-home", manifest);
        Assert.Contains("readOnly: true", manifest);
        Assert.Contains("emptyDir: {}", manifest);
        Assert.Contains("cp -a \"/codex-home-source/$file\" \"/app/codex-home/$file\"", manifest);
        Assert.Contains("chown -R 1000:1000 /app/codex-home", manifest);
        Assert.Contains("chmod 600 /app/codex-home/auth.json", manifest);
        Assert.Contains("mountPath: /app/codex-home", manifest);
        Assert.Contains("git remote set-url origin \"$ROSENVALL_REPOSITORY_URL\"", manifest);
        Assert.Contains("repository_token_for_runner=\"$ROSENVALL_GIT_TOKEN\"", manifest);
        Assert.Contains("unset ROSENVALL_GIT_TOKEN GITHUB_TOKEN", manifest);
        Assert.True(manifest.IndexOf("unset ROSENVALL_GIT_TOKEN GITHUB_TOKEN", StringComparison.Ordinal) < manifest.IndexOf("codex exec", StringComparison.Ordinal));
        Assert.Contains("codex_pid=$!", manifest);
        Assert.Contains("rm -f \"$CODEX_HOME/auth.json\"", manifest);
        Assert.Contains("wait \"$codex_pid\"", manifest);
        Assert.True(manifest.IndexOf("rm -f \"$CODEX_HOME/auth.json\"", StringComparison.Ordinal) > manifest.IndexOf("codex exec", StringComparison.Ordinal));
        Assert.True(manifest.IndexOf("rm -f \"$CODEX_HOME/auth.json\"", StringComparison.Ordinal) < manifest.IndexOf("wait \"$codex_pid\"", StringComparison.Ordinal));
        Assert.Contains("ROSENVALL_GIT_TOKEN=\"$repository_token_for_runner\"", manifest);
        Assert.Contains("git remote set-url origin \"$auth_remote\"", manifest);
        Assert.DoesNotContain("name: codex-home\n                             persistentVolumeClaim:", manifest);
        Assert.Contains("codex exec", manifest);
        Assert.Contains("codex exec --ephemeral", manifest);
        Assert.Contains("--sandbox danger-full-access", manifest);
        Assert.Contains("codex-output.log", manifest);
        Assert.Contains("RDO_FAILURE=Codex runner sandbox is unavailable in this Kubernetes runner", manifest);
        Assert.DoesNotContain("--dangerously-bypass-approvals-and-sandbox", manifest);
        Assert.Contains("RDO_FAILURE=npm test failed", manifest);
        Assert.Contains("RDO_FAILURE=npm build failed", manifest);
        Assert.Contains("RDO_FAILURE=dotnet test failed", manifest);
        Assert.DoesNotContain("|| true", manifest);
        Assert.Contains("secretKeyRef:", manifest);
        Assert.Contains("rosenvall-devops-github", manifest);
        Assert.Contains("name: HOME", manifest);
        Assert.Contains("value: /home/ubuntu", manifest);
        Assert.Contains("name: USER", manifest);
        Assert.Contains("value: ubuntu", manifest);
        Assert.Contains("name: SHELL", manifest);
        Assert.Contains("value: /bin/bash", manifest);
        Assert.Contains("rdo/task-", manifest);
        Assert.Contains("/tmp/rosenvall-workspace", manifest);
        Assert.DoesNotContain("mkdir -p /workspace", manifest);
        Assert.DoesNotContain("ghp_secret_that_must_not_render", manifest);
    }

    [Fact]
    public void Repository_runner_manifest_escapes_control_characters_in_quoted_values()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest(
            "GitHub",
            "demo",
            "https://github.com/rosenvall/demo.git\nmalicious: true",
            "main",
            "https://github.com/rosenvall/demo",
            "rosenvall",
            "code-repo"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("demo", repository.Id, null, null, null, null, null))!;
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "escape yaml", "Ensure manifest values stay quoted.", "Todo", "Medium", null))!;
        var aiRun = store.StartAiPlan(item.Id, "codex", "gpt-5.5", "Plan.")!;
        var implementationRun = store.StartImplementationRun(item.Id, new StartImplementationRunRequest(aiRun.Id, "crille", repository.Id))!;

        var manifest = store.RenderImplementationRunManifest(implementationRun.Id, new ConfigurationBuilder().Build())!;

        Assert.Contains("https://github.com/rosenvall/demo.git\\nmalicious: true", manifest);
        Assert.DoesNotContain("https://github.com/rosenvall/demo.git\nmalicious: true", manifest);
    }

    [Fact]
    public void Repository_runner_can_reference_per_run_github_app_token_secret()
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
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("Gatebound", repository.Id, null, null, null, null, null))!;
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Add dash ability", "Implement a small player dash.", "Todo", "Medium", null));
        var aiRun = store.StartAiPlan(item.Id, "codex", "gpt-5.4", "Inspect the Unity project and add a focused dash implementation.")!;
        var implementationRun = store.StartImplementationRun(item.Id, new StartImplementationRunRequest(aiRun.Id, "crille"))!;
        var tokenSecretName = RepositoryImplementationJobManifestRenderer.GitHubTokenSecretName(implementationRun);

        var secretManifest = RepositoryImplementationJobManifestRenderer.RenderGitHubTokenSecret(implementationRun, "ghs_short_lived_installation_token");
        var runnerManifest = store.RenderImplementationRunManifest(implementationRun.Id, new ConfigurationBuilder().Build(), tokenSecretName);
        using var document = JsonDocument.Parse(secretManifest);
        var root = document.RootElement;
        var metadata = root.GetProperty("metadata");
        var labels = metadata.GetProperty("labels");
        var data = root.GetProperty("data");

        Assert.Equal("Secret", root.GetProperty("kind").GetString());
        Assert.Equal("rosenvall-devops", metadata.GetProperty("namespace").GetString());
        Assert.Equal(tokenSecretName, metadata.GetProperty("name").GetString());
        Assert.Equal("rosenvall-devops-implementation", labels.GetProperty("app.kubernetes.io/part-of").GetString());
        Assert.Equal(implementationRun.Id.ToString(), labels.GetProperty("rosenvall.devops/implementation-run").GetString());
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("ghs_short_lived_installation_token")), data.GetProperty("token").GetString());
        Assert.DoesNotContain("ghs_short_lived_installation_token", secretManifest);
        Assert.DoesNotContain("stringData", secretManifest);
        Assert.DoesNotContain("kubectl.kubernetes.io/last-applied-configuration", secretManifest);
        Assert.Contains(tokenSecretName, runnerManifest);
        Assert.DoesNotContain("ghs_short_lived_installation_token", runnerManifest);
    }

    [Fact]
    public void GitHub_app_secret_payload_persists_manifest_credentials_without_string_data()
    {
        var payload = GitHubAppSecretRenderer.Render(new GitHubManifestAppDto(
            12345,
            "rosenvall-devops",
            "Rosenvall DevOps",
            "-----BEGIN RSA PRIVATE KEY-----\nprivate-key-body\n-----END RSA PRIVATE KEY-----\n"));
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var metadata = root.GetProperty("metadata");
        var labels = metadata.GetProperty("labels");
        var data = root.GetProperty("data");

        Assert.Equal("Secret", root.GetProperty("kind").GetString());
        Assert.Equal(GitHubAppSecretRenderer.SecretName, metadata.GetProperty("name").GetString());
        Assert.Equal(GitHubAppSecretRenderer.Namespace, metadata.GetProperty("namespace").GetString());
        Assert.Equal("rosenvall-devops", labels.GetProperty("app.kubernetes.io/part-of").GetString());
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("12345")), data.GetProperty("app-id").GetString());
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("rosenvall-devops")), data.GetProperty("app-slug").GetString());
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("-----BEGIN RSA PRIVATE KEY-----\nprivate-key-body\n-----END RSA PRIVATE KEY-----\n")), data.GetProperty("private-key").GetString());
        Assert.DoesNotContain("stringData", payload);
        Assert.DoesNotContain("private-key-body", payload);
        Assert.DoesNotContain("kubectl.kubernetes.io/last-applied-configuration", payload);
    }

    [Fact]
    public void GitHub_app_secret_payload_persists_webhook_secret_when_returned_by_manifest_conversion()
    {
        var payload = GitHubAppSecretRenderer.Render(new GitHubManifestAppDto(
            12345,
            "rosenvall-devops",
            "Rosenvall DevOps",
            "-----BEGIN RSA PRIVATE KEY-----\nprivate-key-body\n-----END RSA PRIVATE KEY-----\n",
            WebhookSecret: "github-webhook-secret"));
        using var document = JsonDocument.Parse(payload);

        Assert.Equal(
            Convert.ToBase64String(Encoding.UTF8.GetBytes("github-webhook-secret")),
            document.RootElement.GetProperty("data").GetProperty("webhook-secret").GetString());
        Assert.DoesNotContain("github-webhook-secret", payload);
    }

    [Fact]
    public async Task GitHub_client_reads_webhook_secret_from_manifest_conversion()
    {
        using var httpClient = new HttpClient(new RoutingHttpMessageHandler(_ => JsonResponse("""
        {
          "id": 12345,
          "name": "Rosenvall DevOps",
          "slug": "rosenvall-devops",
          "pem": "-----BEGIN RSA PRIVATE KEY-----\nprivate-key-body\n-----END RSA PRIVATE KEY-----\n",
          "webhook_secret": "github-webhook-secret",
          "client_id": "client-id",
          "client_secret": "client-secret"
        }
        """)));
        var github = new GitHubRepositoryClient(httpClient, new ConfigurationBuilder().Build());

        var app = await github.CreateAppFromManifestAsync("temporary-code", CancellationToken.None);

        Assert.NotNull(app);
        Assert.Equal("github-webhook-secret", app.WebhookSecret);
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
    public void NeedsInput_plans_cannot_be_approved_or_implemented_and_revised_plans_can_be_ready()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest("GitHub", "rosenvalls-homelab", "https://github.com/rosenvall/rosenvalls-homelab.git", "main", "https://github.com/rosenvall/rosenvalls-homelab", "rosenvall", "gitops-homelab"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("Homelab", repository.Id, null, null, null, null, null, ImplementationProfile: "gitops-homelab"))!;
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Move Grafana", "Move Grafana to a different namespace.", "Todo", "Medium", null));

        var needsInput = store.StartAiPlan(item.Id, "codex", "gpt-5.4", "Questions:\n- Which namespace should Grafana move to?")!;
        var approveError = Assert.Throws<InvalidOperationException>(() => store.ApproveAiRun(needsInput.Id, "crille"));
        var implementationError = Assert.Throws<InvalidOperationException>(() => store.StartImplementationRun(item.Id, new StartImplementationRunRequest(needsInput.Id, "crille", repository.Id)));
        store.AddComment(item.Id, "crille", "Comment", "Use namespace observability and keep the old release until ArgoCD is healthy.");
        var revised = store.StartAiPlan(item.Id, "codex", "gpt-5.4", "Plan:\n1. Edit clusters/prod/grafana namespace.\n2. Validate manifests.")!;

        Assert.Equal(AiRunStatus.NeedsInput, needsInput.Status);
        Assert.Contains("cannot be implemented", approveError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cannot be implemented", implementationError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AiRunStatus.PlanReady, revised.Status);
    }

    [Fact]
    public void Custom_url_boards_are_public_clone_only_and_do_not_start_repository_pr_runs()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest(
            "Public demo",
            null,
            "GenericGit",
            "public-demo",
            "https://github.com/rosenvall/public-demo.git",
            "https://github.com/rosenvall/public-demo",
            "main",
            ImplementationProfile: "code-repo",
            ProviderMode: "CustomUrl",
            CustomRepositoryUrl: "https://github.com/rosenvall/public-demo.git"))!;
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Update demo", "Change public repo.", "Todo", "Medium", null));
        var aiRun = store.StartAiPlan(item.Id, "codex", "gpt-5.4", "Update demo.")!;

        var implementationRun = store.StartImplementationRun(item.Id, new StartImplementationRunRequest(aiRun.Id, "crille"));

        Assert.Null(implementationRun);
        Assert.Empty(store.GetImplementationRuns(item.Id));
    }

    [Fact]
    public void Users_teams_and_roles_are_persisted_for_board_authorization()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var board = store.GetWorkspaces().SelectMany(workspace => store.GetBoards(workspace.Id)).First();
        var user = store.GetOrCreateUser(new UserIdentityRequest("authentik|crille", "Christopher Rosenvall", "christopher.rosenvall@gmail.com"));
        var guest = store.GetOrCreateUser(new UserIdentityRequest("authentik|guest", "Guest", "guest@example.com"));
        var team = store.CreateTeam(new CreateTeamRequest("Gatebound"), user.Subject);

        var updated = store.UpsertTeamMember(team.Id, new UpsertTeamMemberRequest(user.Id, "Admin"));
        store.UpsertBoardTeamAccess(board.Id, team.Id, "Member");
        var reopened = fixture.Reopen();

        Assert.NotNull(updated);
        Assert.Contains(reopened.GetUsers(), entry => entry.Subject == "authentik|crille");
        Assert.Contains(reopened.GetTeams(), entry => entry.Name == "Gatebound" && entry.Members.Any(member => member.UserId == user.Id && member.Role == "Admin"));
        Assert.Contains(reopened.GetTeams(user.Subject), entry => entry.Id == team.Id);
        Assert.DoesNotContain(reopened.GetTeams(guest.Subject), entry => entry.Id == team.Id);
        Assert.True(reopened.CanViewTeam(team.Id, user.Subject));
        Assert.True(reopened.CanMutateTeam(team.Id, user.Subject));
        Assert.False(reopened.CanViewTeam(team.Id, guest.Subject));
        Assert.False(reopened.CanMutateTeam(team.Id, guest.Subject));
        Assert.True(reopened.CanMutateBoard(board.Id, user.Subject));
    }

    [Fact]
    public void Invited_team_member_can_see_assigned_board_after_first_login()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("Invited board", null, null, null, null, null, null))!;
        var owner = store.GetOrCreateUser(new UserIdentityRequest("authentik|owner", "Owner", "owner@example.com"));
        var team = store.CreateTeam(new CreateTeamRequest("Invited team"), owner.Subject);

        var invitedTeam = store.InviteTeamMember(team.Id, new InviteTeamMemberRequest("invited@example.com", "Member"));
        store.UpsertBoardTeamAccess(board.Id, team.Id, "Member");

        var visibleWorkspaces = store.GetWorkspacesForUser(new UserIdentityRequest("authentik|invited", "Invited User", "invited@example.com"));
        var invited = store.GetOrCreateUser(new UserIdentityRequest("authentik|invited", "Invited User", "invited@example.com"));

        Assert.NotNull(invitedTeam);
        Assert.Contains(visibleWorkspaces, entry => entry.Id == workspace.Id);
        Assert.Contains(store.GetTeams(invited.Subject), entry => entry.Id == team.Id);
        Assert.Contains(store.GetBoards(workspace.Id, invited.Subject), entry => entry.Id == board.Id);
        Assert.True(store.CanViewBoard(board.Id, invited.Subject));
        Assert.True(store.CanMutateBoard(board.Id, invited.Subject));
    }

    [Fact]
    public void Team_membership_does_not_mutate_unassigned_boards()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("Unassigned", null, "GenericGit", "public-demo", "https://github.com/rosenvall/public-demo.git", "https://github.com/rosenvall/public-demo", "main"))!;
        var user = store.GetOrCreateUser(new UserIdentityRequest("authentik|guest", "Guest User", "guest@example.com"));
        var team = store.CreateTeam(new CreateTeamRequest("External"), user.Subject);
        store.UpsertTeamMember(team.Id, new UpsertTeamMemberRequest(user.Id, "Admin"));

        Assert.False(store.CanMutateBoard(board.Id, user.Subject));

        store.UpsertBoardTeamAccess(board.Id, team.Id, "Member");

        Assert.True(store.CanMutateBoard(board.Id, user.Subject));
    }

    [Fact]
    public void Work_item_ai_run_and_comment_mutation_follow_board_authorization()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("Restricted", null, "Sample", null, null, null, null))!;
        var owner = store.GetOrCreateUser(new UserIdentityRequest("authentik|owner", "Owner", "owner@example.com"));
        var guest = store.GetOrCreateUser(new UserIdentityRequest("authentik|guest", "Guest", "guest@example.com"));
        var team = store.CreateTeam(new CreateTeamRequest("Restricted Team"), owner.Subject);
        store.UpsertBoardTeamAccess(board.Id, team.Id, "Member");
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Secure card", "Protect this.", "Todo", "Medium", null))!;
        var aiRun = store.StartAiPlan(item.Id, "codex", "gpt-5.5", "Plan.")!;
        var comment = store.AddComment(item.Id, "Owner", "Comment", "Human note.")!;

        Assert.True(store.CanMutateWorkItem(item.Id, owner.Subject));
        Assert.True(store.CanMutateAiRun(aiRun.Id, owner.Subject));
        Assert.True(store.CanMutateComment(comment.Id, owner.Subject));
        Assert.False(store.CanMutateWorkItem(item.Id, guest.Subject));
        Assert.False(store.CanMutateAiRun(aiRun.Id, guest.Subject));
        Assert.False(store.CanMutateComment(comment.Id, guest.Subject));
        Assert.True(store.CanViewBoard(board.Id, owner.Subject));
        Assert.True(store.CanViewWorkItem(item.Id, owner.Subject));
        Assert.False(store.CanViewBoard(board.Id, guest.Subject));
        Assert.False(store.CanViewWorkItem(item.Id, guest.Subject));
        Assert.True(store.CanCreateRepository(owner.Subject));
        Assert.False(store.CanCreateRepository(guest.Subject));
        Assert.True(store.CanCreateWorkspace(owner.Subject));
        Assert.False(store.CanCreateWorkspace(guest.Subject));
        Assert.Contains(store.GetWorkspaces(owner.Subject), entry => entry.Id == workspace.Id);
        Assert.DoesNotContain(store.GetWorkspaces(guest.Subject), entry => entry.Id == workspace.Id);
        Assert.Contains(store.GetBoards(workspace.Id, owner.Subject), entry => entry.Id == board.Id);
        Assert.DoesNotContain(store.GetBoards(workspace.Id, guest.Subject), entry => entry.Id == board.Id);
        Assert.Contains(store.GetWorkItems(owner.Subject), entry => entry.Id == item.Id);
        Assert.DoesNotContain(store.GetWorkItems(guest.Subject), entry => entry.Id == item.Id);

        var createdWorkspace = store.CreateWorkspace("Owner Workspace", "Development", "local", owner.Subject);
        var createdBoard = store.GetBoards(createdWorkspace.Id, owner.Subject).Single();
        Assert.Contains(store.GetWorkspaces(owner.Subject), entry => entry.Id == createdWorkspace.Id);
        Assert.DoesNotContain(store.GetWorkspaces(guest.Subject), entry => entry.Id == createdWorkspace.Id);
        Assert.True(store.CanMutateBoard(createdBoard.Id, owner.Subject));
        Assert.False(store.CanViewBoard(createdBoard.Id, guest.Subject));
    }

    [Fact]
    public void Demo_user_bootstrap_is_restricted_to_demo_sandbox()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var owner = store.GetOrCreateUser(new UserIdentityRequest("authentik|owner", "Owner", "owner@example.com"));
        var realWorkspace = store.CreateWorkspace("Real Workspace", "Production", "local", owner.Subject);
        var ownerIntegration = store.CreateGitHubIntegration(new GitHubIntegrationCallbackRequest(9001, "carnufex", "User", owner.Subject));
        var visibleWorkspaces = store.GetWorkspacesForUser(new UserIdentityRequest("authentik|demo", "Demo", "demo@rosenvall.local"));
        var demo = store.GetOrCreateUserWithDemoSandbox(new UserIdentityRequest("authentik|demo", "Demo", "demo@rosenvall.local"));
        var demoIntegration = store.CreateGitHubIntegration(new GitHubIntegrationCallbackRequest(9002, "demo-user", "User", demo.Subject));
        var githubRepository = store.CreateRepository(new CreateRepositoryRequest("GitHub", "demo-user/web", "https://github.com/demo-user/web.git", "main", "https://github.com/demo-user/web", "demo-user", "react-preview", "preview-then-pr"));
        var localRepository = store.CreateRepository(new CreateRepositoryRequest("LocalGit", "demo-web", "http://rosenvall-devops-forgejo.rosenvall-devops.svc.cluster.local:3000/rdo/demo-web.git", "main", null, "rdo", "react-preview", "preview-then-pr"));
        var demoWorkspace = visibleWorkspaces.Single(workspace => workspace.Name == "Demo Sandbox");
        var realBoard = store.CreateBoard(realWorkspace.Id, new CreateBoardRequest("Real GitHub App", githubRepository.Id, "GitHub", "demo-user/web", githubRepository.RemoteUrl, githubRepository.WebUrl, "main"), owner.Subject);

        Assert.DoesNotContain(visibleWorkspaces, workspace => workspace.Id == realWorkspace.Id);
        Assert.False(store.CanCreateWorkspace(demo.Subject));
        Assert.True(store.CanCreateBoardInWorkspace(demoWorkspace.Id, demo.Subject));
        Assert.False(store.CanCreateBoardInWorkspace(realWorkspace.Id, demo.Subject));
        Assert.NotNull(store.CreateBoard(demoWorkspace.Id, new CreateBoardRequest("Demo Website", null, null, null, null, null, null), demo.Subject));
        var demoBoard = store.CreateBoard(demoWorkspace.Id, new CreateBoardRequest("Demo LocalGit Website", localRepository.Id, "LocalGit", "rdo/demo-web", localRepository.RemoteUrl, null, "main", ImplementationProfile: "react-preview", ImplementationWorkflow: "preview-then-pr"), demo.Subject);
        Assert.NotNull(demoBoard);
        Assert.Null(store.CreateBoard(demoWorkspace.Id, new CreateBoardRequest("Blocked GitHub Website", githubRepository.Id, "GitHub", "demo-user/web", githubRepository.RemoteUrl, githubRepository.WebUrl, "main", ImplementationProfile: "react-preview", ImplementationWorkflow: "preview-then-pr"), demo.Subject));
        Assert.Null(store.CreateBoard(realWorkspace.Id, new CreateBoardRequest("Not Allowed", null, null, null, null, null, null), demo.Subject));
        Assert.NotNull(realBoard);
        Assert.True(store.CanViewRepository(localRepository.Id, demo.Subject));
        Assert.False(store.CanViewRepository(githubRepository.Id, demo.Subject));
        Assert.True(store.CanSyncRepositoryToProvider(demoBoard.Id, "LocalGit", demo.Subject));
        Assert.False(store.CanSyncRepositoryToProvider(demoBoard.Id, "GitHub", demo.Subject));
        Assert.False(store.CanUseGitHubInstallation(ownerIntegration.InstallationId, demo.Subject));
        Assert.False(store.CanUseGitHubInstallation(demoIntegration.InstallationId, demo.Subject));
        Assert.Empty(store.GetGitHubIntegrations(demo.Subject));
    }

    [Fact]
    public void Demo_sandbox_policy_status_reports_restricted_demo_isolation()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var owner = store.GetOrCreateUser(new UserIdentityRequest("authentik|owner", "Owner", "owner@example.com"));
        var realWorkspace = store.CreateWorkspace("Real Workspace", "Production", "local", owner.Subject);
        var repository = store.CreateRepository(new CreateRepositoryRequest("GitHub", "carnufex/web", "https://github.com/carnufex/web.git", "main", "https://github.com/carnufex/web", "carnufex"));
        _ = store.CreateBoard(realWorkspace.Id, new CreateBoardRequest("Real App", repository.Id, "GitHub", "carnufex/web", repository.RemoteUrl, repository.WebUrl, "main"), owner.Subject);
        _ = store.GetOrCreateUserWithDemoSandbox(new UserIdentityRequest("authentik|demo", "Demo", "demo@rosenvall.local"));

        var status = store.GetDemoSandboxPolicyStatus();

        Assert.True(status.Enabled);
        Assert.True(status.DemoUserPresent);
        Assert.True(status.Isolated);
        Assert.Equal("demo@rosenvall.local", status.DemoEmail);
        Assert.Equal("Demo Sandbox", status.WorkspaceName);
        Assert.Contains("isolated", status.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GitHub_organization_repository_creation_policy_does_not_enable_v1_creation()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var owner = store.GetOrCreateUser(new UserIdentityRequest("authentik|owner", "Owner", "owner@example.com"));
        var member = store.GetOrCreateUser(new UserIdentityRequest("authentik|member", "Member", "member@example.com"));
        var guest = store.GetOrCreateUser(new UserIdentityRequest("authentik|guest", "Guest", "guest@example.com"));
        var team = store.CreateTeam(new CreateTeamRequest("Repo creators"), owner.Subject);
        store.UpsertTeamMember(team.Id, new UpsertTeamMemberRequest(member.Id, "Member"));
        var integration = store.CreateGitHubIntegration(new GitHubIntegrationCallbackRequest(1234, "rosenvall-corp", "Organization", owner.Subject));

        Assert.False(store.CanCreateGitHubRepository(integration.InstallationId, owner.Subject));
        Assert.False(store.CanCreateGitHubRepository(integration.InstallationId, member.Subject));
        Assert.False(store.CanCreateGitHubRepository(integration.InstallationId, guest.Subject));

        var saved = store.UpsertGitHubRepositoryCreationPolicy(integration.InstallationId, new UpdateGitHubRepositoryCreationPolicyRequest([team.Id]), owner.Subject);

        Assert.NotNull(saved);
        Assert.False(store.CanCreateGitHubRepository(integration.InstallationId, owner.Subject));
        Assert.False(store.CanCreateGitHubRepository(integration.InstallationId, member.Subject));
        Assert.False(store.CanCreateGitHubRepository(integration.InstallationId, guest.Subject));
        Assert.DoesNotContain(store.GetGitHubIntegrations(member.Subject), entry => entry.InstallationId == integration.InstallationId);
        Assert.Contains(store.GetGitHubIntegrations(owner.Subject), entry => entry.InstallationId == integration.InstallationId && entry.CanManageRepositoryCreationPolicy);
    }

    [Fact]
    public void GitHub_personal_repository_creation_is_allowed_after_matching_user_authorization()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var user = store.GetOrCreateUser(new UserIdentityRequest("authentik|crille", "Crille", "crille@example.com"));
        var integration = store.CreateGitHubIntegration(new GitHubIntegrationCallbackRequest(4444, "carnufex", "User", "github-app"));

        Assert.False(store.CanCreateGitHubRepository(integration.InstallationId, user.Subject));

        store.UpsertGitHubUserAuthorization(new GitHubUserAuthorizationDto(Guid.NewGuid(), user.Subject, integration.InstallationId, integration.AccountLogin, "carnufex", "Connected", "github-user-token-test", DateTimeOffset.UtcNow));

        Assert.True(store.CanCreateGitHubRepository(integration.InstallationId, user.Subject));
        Assert.Contains(store.GetGitHubIntegrations(user.Subject), entry =>
            entry.InstallationId == integration.InstallationId &&
            entry.HasUserAuthorization &&
            !entry.RequiresUserAuthorizationForRepositoryCreation &&
            entry.CanCreateRepositories);
    }

    [Fact]
    public void GitHub_personal_repository_creation_policy_does_not_delegate_to_team_members()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var owner = store.GetOrCreateUser(new UserIdentityRequest("authentik|owner", "Owner", "owner@example.com"));
        var member = store.GetOrCreateUser(new UserIdentityRequest("authentik|member", "Member", "member@example.com"));
        var team = store.CreateTeam(new CreateTeamRequest("Repo creators"), owner.Subject);
        store.UpsertTeamMember(team.Id, new UpsertTeamMemberRequest(member.Id, "Member"));
        var integration = store.CreateGitHubIntegration(new GitHubIntegrationCallbackRequest(4321, "carnufex", "User", owner.Subject));

        var saved = store.UpsertGitHubRepositoryCreationPolicy(integration.InstallationId, new UpdateGitHubRepositoryCreationPolicyRequest([team.Id]), owner.Subject);

        Assert.NotNull(saved);
        Assert.False(store.CanCreateGitHubRepository(integration.InstallationId, owner.Subject));
        store.UpsertGitHubUserAuthorization(new GitHubUserAuthorizationDto(Guid.NewGuid(), owner.Subject, integration.InstallationId, integration.AccountLogin, "carnufex", "Connected", "github-user-token-owner", DateTimeOffset.UtcNow));
        Assert.True(store.CanCreateGitHubRepository(integration.InstallationId, owner.Subject));
        Assert.False(store.CanCreateGitHubRepository(integration.InstallationId, member.Subject));
    }

    [Fact]
    public void GitHub_repository_creation_policy_is_persisted()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var owner = store.GetOrCreateUser(new UserIdentityRequest("authentik|owner", "Owner", "owner@example.com"));
        var member = store.GetOrCreateUser(new UserIdentityRequest("authentik|member", "Member", "member@example.com"));
        var team = store.CreateTeam(new CreateTeamRequest("Repo creators"), owner.Subject);
        store.UpsertTeamMember(team.Id, new UpsertTeamMemberRequest(member.Id, "Member"));
        var integration = store.CreateGitHubIntegration(new GitHubIntegrationCallbackRequest(5678, "rosenvall-corp", "Organization", owner.Subject));
        store.UpsertGitHubRepositoryCreationPolicy(integration.InstallationId, new UpdateGitHubRepositoryCreationPolicyRequest([team.Id]), owner.Subject);

        var reopened = fixture.Reopen();

        Assert.False(reopened.CanCreateGitHubRepository(integration.InstallationId, member.Subject));
        Assert.Contains(reopened.GetGitHubIntegrations(owner.Subject), entry =>
            entry.InstallationId == integration.InstallationId &&
            entry.RepositoryCreatorTeamIds.Contains(team.Id));
    }

    [Fact]
    public void Legacy_github_app_installation_policy_can_be_bootstrapped_by_team_admin()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var admin = store.GetOrCreateUser(new UserIdentityRequest("authentik|admin", "Admin", "admin@example.com"));
        var member = store.GetOrCreateUser(new UserIdentityRequest("authentik|member", "Member", "member@example.com"));
        var guest = store.GetOrCreateUser(new UserIdentityRequest("authentik|guest", "Guest", "guest@example.com"));
        var team = store.CreateTeam(new CreateTeamRequest("Platform"), admin.Subject);
        store.UpsertTeamMember(team.Id, new UpsertTeamMemberRequest(admin.Id, "Admin"));
        store.UpsertTeamMember(team.Id, new UpsertTeamMemberRequest(member.Id, "Member"));
        var integration = store.CreateGitHubIntegration(new GitHubIntegrationCallbackRequest(9012, "rosenvall-corp", "Organization", "github-app"));

        Assert.True(store.CanManageGitHubRepositoryCreationPolicy(integration.InstallationId, admin.Subject));
        Assert.False(store.CanManageGitHubRepositoryCreationPolicy(integration.InstallationId, member.Subject));

        var saved = store.UpsertGitHubRepositoryCreationPolicy(integration.InstallationId, new UpdateGitHubRepositoryCreationPolicyRequest([team.Id]), admin.Subject);

        Assert.NotNull(saved);
        Assert.False(store.CanCreateGitHubRepository(integration.InstallationId, member.Subject));
        Assert.False(store.CanCreateGitHubRepository(integration.InstallationId, guest.Subject));
    }

    [Fact]
    public void Work_item_creation_rejects_unknown_board_and_unknown_status()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var board = store.GetWorkspaces().SelectMany(workspace => store.GetBoards(workspace.Id)).First();

        var unknownBoard = store.CreateWorkItem(new CreateWorkItemRequest(Guid.NewGuid(), "Feature", "Invalid board", "Should not persist.", "Todo", "Medium", null));
        var unknownStatus = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Invalid status", "Should not persist.", "Not A Column", "Medium", null));

        Assert.Null(unknownBoard);
        Assert.Null(unknownStatus);
        Assert.DoesNotContain(store.GetWorkItems(), item => item.Title is "Invalid board" or "Invalid status");
    }

    [Fact]
    public void Work_item_update_and_move_reject_unknown_status()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var board = store.GetWorkspaces().SelectMany(workspace => store.GetBoards(workspace.Id)).First();
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Valid", "Existing item.", "Todo", "Medium", null))!;

        var updated = store.UpdateWorkItem(item.Id, new UpdateWorkItemRequest("Valid", "Existing item.", "Feature", "Not A Column", "Medium", null));
        var moved = store.MoveWorkItem(item.Id, new MoveWorkItemRequest("Also Not A Column", 0));

        Assert.Null(updated);
        Assert.Null(moved);
        Assert.Equal("Todo", store.GetWorkItemDetail(item.Id)!.Item.Status);
    }

    [Fact]
    public void Board_can_link_multiple_repositories_with_one_primary()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var api = store.CreateRepository(new CreateRepositoryRequest("GitHub", "api", "https://github.com/rosenvall/api.git", "main", "https://github.com/rosenvall/api", "rosenvall", "code-repo"));
        var unity = store.CreateRepository(new CreateRepositoryRequest("GitHub", "Gatebound", "https://github.com/carnufex/Gatebound.git", "main", "https://github.com/carnufex/Gatebound", "carnufex", "unity"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("Product", api.Id, null, null, null, null, null))!;

        var linked = store.LinkRepositoryToBoard(board.Id, new LinkBoardRepositoryRequest(unity.Id, true, "unity"))!;

        Assert.Equal(unity.Id, linked.Repository!.Id);
        Assert.Equal(2, linked.Repositories!.Count);
        Assert.Single(linked.Repositories, repository => repository.IsPrimary);
        Assert.Contains(linked.Repositories, repository => repository.RepositoryId == api.Id && !repository.IsPrimary);
        Assert.Contains(linked.Repositories, repository => repository.RepositoryId == unity.Id && repository.IsPrimary && repository.ImplementationProfile == "unity");
    }

    [Fact]
    public void Board_can_start_without_repository_and_sync_to_github_later()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("Preview only", null, null, null, null, null, null))!;
        var repository = store.CreateRepository(new CreateRepositoryRequest("GitHub", "new-private-board", "https://github.com/carnufex/new-private-board.git", "main", "https://github.com/carnufex/new-private-board", "carnufex", "code-repo"));

        var linked = store.LinkRepositoryToBoard(board.Id, new LinkBoardRepositoryRequest(repository.Id, true, "code-repo"))!;

        Assert.Null(board.Repository);
        Assert.Equal("Preview only", board.RepositorySyncState);
        Assert.Contains("sync-github", board.ProviderCapabilities!);
        Assert.Equal(repository.Id, linked.Repository!.Id);
        Assert.Equal("Synced to GitHub", linked.RepositorySyncState);
        Assert.Contains("repository-implementation", linked.ProviderCapabilities!);
    }

    [Fact]
    public void Seeded_sample_board_is_marked_as_demo_not_synced_provider()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();

        var board = store.GetBoards(workspace.Id).Single(entry => entry.Name == "Demo Sprint 42");

        Assert.Equal("Demo board", board.RepositorySyncState);
        Assert.Equal("Sample", board.Repository!.Provider);
        Assert.Null(board.Repository.WebUrl);
        Assert.Contains("preview", board.ProviderCapabilities!);
        Assert.Contains("demo", board.ProviderCapabilities!);
        Assert.DoesNotContain("repository-implementation", board.ProviderCapabilities!);
    }

    [Fact]
    public void Board_secrets_store_metadata_only_and_runner_references_kubernetes_secret()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest("GitHub", "Gatebound", "https://github.com/carnufex/Gatebound.git", "main", "https://github.com/carnufex/Gatebound", "carnufex", "unity"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("Gatebound", repository.Id, null, null, null, null, null))!;
        var secret = store.CreateBoardSecret(board.Id, new CreateBoardSecretRequest("UNITY_LICENSE", "super-secret-license", repository.Id))!;
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Add dash", "Implement dash in Unity.", "Todo", "Medium", null));
        var aiRun = store.StartAiPlan(item.Id, "codex", "gpt-5.4", "Add a focused dash implementation.")!;
        var implementationRun = store.StartImplementationRun(item.Id, new StartImplementationRunRequest(aiRun.Id, "crille", repository.Id))!;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GitHub:TokenSecretName"] = "rosenvall-devops-github",
                ["Secrets:Namespace"] = "rosenvall-devops",
                ["RepositoryRuns:ExposeBoardSecretsToCodex"] = "true"
            })
            .Build();

        var secretManifest = BoardSecretManifestRenderer.Render(secret, "super-secret-license", configuration);
        var runnerManifest = store.RenderImplementationRunManifest(implementationRun.Id, configuration);
        var listed = store.GetBoardSecrets(board.Id);
        var fetched = store.GetBoardSecret(board.Id, secret.Id);

        Assert.Single(listed);
        Assert.NotNull(fetched);
        Assert.Equal("UNITY_LICENSE", listed[0].Key);
        Assert.DoesNotContain("super-secret-license", JsonSerializer.Serialize(listed));
        Assert.Contains("UNITY_LICENSE", secretManifest);
        Assert.Contains(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("super-secret-license")), secretManifest);
        Assert.DoesNotContain("stringData", secretManifest);
        Assert.DoesNotContain("kubectl.kubernetes.io/last-applied-configuration", secretManifest);
        Assert.Contains("UNITY_LICENSE", runnerManifest);
        Assert.Contains("secretKeyRef:", runnerManifest);
        Assert.Contains("board_secret_UNITY_LICENSE=\"$UNITY_LICENSE\"", runnerManifest);
        Assert.Contains("unset UNITY_LICENSE", runnerManifest);
        Assert.True(runnerManifest.IndexOf("unset UNITY_LICENSE", StringComparison.Ordinal) < runnerManifest.IndexOf("codex exec", StringComparison.Ordinal));
        Assert.True(runnerManifest.IndexOf("UNITY_LICENSE=\"$board_secret_UNITY_LICENSE\"", StringComparison.Ordinal) > runnerManifest.IndexOf("codex exec", StringComparison.Ordinal));
        Assert.DoesNotContain("super-secret-license", runnerManifest);
    }

    [Fact]
    public void Board_secret_create_keeps_existing_metadata_until_runtime_secret_write_is_committed()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest("LocalGit", "demo-app", "http://forgejo/rdo/demo-app.git", "main", null, "rdo", "react-preview"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("Secret replace", repository.Id, null, null, null, null, null))!;
        var existing = store.CreateBoardSecret(board.Id, new CreateBoardSecretRequest("TOKEN", "old-secret", repository.Id))!;

        var prepared = store.PrepareBoardSecretCreate(board.Id, new CreateBoardSecretRequest("TOKEN", "new-secret", repository.Id))!;

        Assert.NotEqual(existing.Id, prepared.Id);
        Assert.Single(store.GetBoardSecrets(board.Id));
        Assert.Equal(existing.Id, store.GetBoardSecret(board.Id, existing.Id)!.Id);

        var committed = store.CommitBoardSecretCreate(prepared)!;

        Assert.Equal(prepared.Id, committed.Id);
        Assert.Single(store.GetBoardSecrets(board.Id));
        Assert.Null(store.GetBoardSecret(board.Id, existing.Id));
        Assert.Equal(prepared.Id, store.GetBoardSecrets(board.Id).Single().Id);
    }

    [Fact]
    public void Board_secret_update_keeps_existing_metadata_until_runtime_secret_write_is_committed()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest("LocalGit", "demo-app", "http://forgejo/rdo/demo-app.git", "main", null, "rdo", "react-preview"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("Secret update", repository.Id, null, null, null, null, null))!;
        var existing = store.CreateBoardSecret(board.Id, new CreateBoardSecretRequest("TOKEN", "old-secret", repository.Id))!;

        var prepared = store.PrepareBoardSecretUpdate(board.Id, existing.Id)!;

        Assert.Equal(existing.UpdatedAt, store.GetBoardSecret(board.Id, existing.Id)!.UpdatedAt);

        var committed = store.CommitBoardSecretUpdate(prepared)!;

        Assert.Equal(prepared.UpdatedAt, committed.UpdatedAt);
        Assert.Equal(prepared.UpdatedAt, store.GetBoardSecret(board.Id, existing.Id)!.UpdatedAt);
    }

    [Fact]
    public void Board_ai_context_persists_agent_instructions_and_includes_them_in_planning_context()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var board = store.GetWorkspaces().SelectMany(workspace => store.GetBoards(workspace.Id)).First();
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Plan with agents", "Use board guidance.", "Todo", "Medium", null));

        var context = store.UpsertBoardAiContext(board.Id, new BoardAiContextRequest(
            "Use the board conventions.",
            ["clock-skill"],
            true,
            "Prefer AGENTS.md style acceptance criteria."));
        var detail = store.GetWorkItemDetail(item.Id)!;
        var rendered = PromptContextRenderer.RenderPlanningContext(detail);

        Assert.NotNull(context);
        Assert.Equal("Prefer AGENTS.md style acceptance criteria.", context.AgentInstructions);
        Assert.Contains("Board agent instructions: Prefer AGENTS.md style acceptance criteria.", rendered);
        Assert.Equal("Prefer AGENTS.md style acceptance criteria.", fixture.Reopen().GetBoardAiContext(board.Id)!.AgentInstructions);
    }

    [Fact]
    public void Board_cleanup_manifest_includes_work_item_runtime_and_board_secret_but_not_shared_credentials()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest("GitHub", "Gatebound", "https://github.com/carnufex/Gatebound.git", "main", "https://github.com/carnufex/Gatebound", "carnufex", "unity"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("Gatebound cleanup", repository.Id, null, null, null, null, null))!;
        var secret = store.CreateBoardSecret(board.Id, new CreateBoardSecretRequest("UNITY_LICENSE", "super-secret-license", repository.Id))!;
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Cleanup runtime", "Create runtime artifacts.", "Todo", "Medium", null));
        var aiRun = store.StartAiPlan(item.Id, "codex", "gpt-5.4", "Plan")!;
        var implementationRun = store.StartImplementationRun(item.Id, new StartImplementationRunRequest(aiRun.Id, "crille", repository.Id))!;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Secrets:Namespace"] = "rosenvall-devops"
            })
            .Build();

        var manifest = store.RenderBoardCleanupManifest(board.Id, configuration)!;

        Assert.Contains(RepositoryImplementationJobManifestRenderer.JobName(implementationRun, store.GetWorkItemDetail(item.Id)!), manifest);
        Assert.Contains(RepositoryImplementationJobManifestRenderer.GitHubTokenSecretName(implementationRun), manifest);
        Assert.Contains(BoardSecretManifestRenderer.SecretName(secret), manifest);
        Assert.DoesNotContain("rosenvall-devops-codex-home", manifest);
        Assert.DoesNotContain("rosenvall-devops-github-app", manifest);
        Assert.DoesNotContain("github-user-token-", manifest);
    }

    [Fact]
    public void Board_cleanup_manifest_includes_provider_sync_job_and_token_secret()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var source = store.CreateRepository(new CreateRepositoryRequest("LocalGit", "demo-app", "http://forgejo/rdo/demo-app.git", "main", null, "rdo", "react-preview"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("Provider sync cleanup", source.Id, null, null, null, null, null))!;
        var providerSync = store.RecordPipelineRun(new RecordPipelineRunRequest(source.Id, board.Id, null, "ProviderSync", "Running", "Syncing repository.", null))!;
        var genericPipeline = store.RecordPipelineRun(new RecordPipelineRunRequest(source.Id, board.Id, null, "Build", "Running", "Running legacy pipeline.", null))!;

        var manifest = store.RenderBoardCleanupManifest(board.Id, new ConfigurationBuilder().Build())!;

        Assert.Contains(RepositoryProviderSyncJobManifestRenderer.JobName(providerSync), manifest);
        Assert.Contains(RepositoryProviderSyncJobManifestRenderer.TokenSecretName(providerSync), manifest);
        Assert.Contains(PipelineJobManifestRenderer.JobName(genericPipeline, source), manifest);
        Assert.DoesNotContain(PipelineJobManifestRenderer.JobName(providerSync, source), manifest);
    }

    [Fact]
    public void Delete_board_removes_board_scoped_records_but_keeps_repositories_and_user_authorizations()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest("GitHub", "Gatebound", "https://github.com/carnufex/Gatebound.git", "main", "https://github.com/carnufex/Gatebound", "carnufex", "unity"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("Delete board", repository.Id, null, null, null, null, null))!;
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Remove me", "Delete board data.", "Todo", "Medium", null));
        var integration = store.CreateGitHubIntegration(new GitHubIntegrationCallbackRequest(7777, "carnufex", "User", "github-app"));
        store.UpsertGitHubUserAuthorization(new GitHubUserAuthorizationDto(Guid.NewGuid(), "authentik|crille", integration.InstallationId, integration.AccountLogin, "carnufex", "Connected", "github-user-token-keep", DateTimeOffset.UtcNow));
        store.CreateBoardSecret(board.Id, new CreateBoardSecretRequest("TOKEN", "value"));
        store.UpsertBoardAiContext(board.Id, new BoardAiContextRequest("Instruction", ["skill"], true, "Agent"));

        Assert.True(store.DeleteBoard(board.Id, "crille"));

        Assert.Null(store.GetBoard(board.Id));
        Assert.Null(store.GetWorkItemDetail(item.Id));
        Assert.Empty(store.GetBoardSecrets(board.Id));
        Assert.Null(store.GetBoardAiContext(board.Id));
        Assert.Contains(store.GetRepositories(), entry => entry.Id == repository.Id);
        Assert.NotNull(store.GetGitHubUserAuthorization(integration.InstallationId, "authentik|crille"));
    }

    [Fact]
    public void Repository_runner_does_not_expose_board_secrets_to_codex_by_default()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest("GitHub", "Gatebound", "https://github.com/carnufex/Gatebound.git", "main", "https://github.com/carnufex/Gatebound", "carnufex", "unity"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("Gatebound", repository.Id, null, null, null, null, null))!;
        var secret = store.CreateBoardSecret(board.Id, new CreateBoardSecretRequest("UNITY_LICENSE", "super-secret-license", repository.Id))!;
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Add dash", "Implement dash in Unity.", "Todo", "Medium", null));
        var aiRun = store.StartAiPlan(item.Id, "codex", "gpt-5.4", "Add a focused dash implementation.")!;
        var implementationRun = store.StartImplementationRun(item.Id, new StartImplementationRunRequest(aiRun.Id, "crille", repository.Id))!;

        var runnerManifest = store.RenderImplementationRunManifest(implementationRun.Id, new ConfigurationBuilder().Build())!;
        var fetched = store.GetBoardSecret(board.Id, secret.Id);

        Assert.DoesNotContain("UNITY_LICENSE", runnerManifest);
        Assert.DoesNotContain(BoardSecretManifestRenderer.SecretName(secret), runnerManifest);
        Assert.NotNull(fetched);
        Assert.Null(fetched.LastUsedAt);
    }

    [Fact]
    public void Repository_runner_manifest_keeps_environment_entries_in_one_yaml_list()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest("GitHub", "rosenvalls-homelab", "https://github.com/carnufex/Rosenvalls-Homelab.git", "master", "https://github.com/carnufex/Rosenvalls-Homelab", "carnufex", "gitops-homelab"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("Homelab", repository.Id, null, null, null, null, null, ImplementationProfile: "gitops-homelab"))!;
        store.CreateBoardSecret(board.Id, new CreateBoardSecretRequest("CLOUDFLARE_API_TOKEN", "secret-value", repository.Id));
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Add nginx test", "Create namespace test and expose test.rosenvall.se.", "Todo", "Medium", null));
        var aiRun = store.StartAiPlan(item.Id, "codex", "gpt-5.5", "Plan:\n1. Add apps/test.\n2. Use Gateway API.")!;
        var implementationRun = store.StartImplementationRun(item.Id, new StartImplementationRunRequest(aiRun.Id, "crille", repository.Id))!;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RepositoryRuns:ExposeBoardSecretsToCodex"] = "true"
            })
            .Build();

        var manifest = store.RenderImplementationRunManifest(implementationRun.Id, configuration)!;

        AssertImplementationManifestEnvListIsWellFormed(manifest);
        Assert.Contains("ROSENVALL_PROMPT_B64", manifest);
        Assert.Contains("CLOUDFLARE_API_TOKEN", manifest);
        Assert.Contains("unset CLOUDFLARE_API_TOKEN", manifest);
        Assert.True(manifest.IndexOf("unset CLOUDFLARE_API_TOKEN", StringComparison.Ordinal) < manifest.IndexOf("codex exec", StringComparison.Ordinal));
    }

    [Fact]
    public void Failed_repository_implementation_can_be_retried_and_cleanup_includes_both_attempts()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest("GitHub", "rosenvalls-homelab", "https://github.com/carnufex/Rosenvalls-Homelab.git", "master", "https://github.com/carnufex/Rosenvalls-Homelab", "carnufex", "gitops-homelab"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("Homelab", repository.Id, null, null, null, null, null, ImplementationProfile: "gitops-homelab"))!;
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Add nginx test", "Create namespace test and expose test.rosenvall.se.", "Todo", "Medium", null));
        var aiRun = store.StartAiPlan(item.Id, "codex", "gpt-5.5", "Plan:\n1. Add apps/test.")!;

        var failedAttempt = store.StartImplementationRun(item.Id, new StartImplementationRunRequest(aiRun.Id, "crille", repository.Id))!;
        store.UpdateImplementationRun(failedAttempt.Id, "Failed", "RDO_FAILURE=Codex CLI failed", "Codex CLI failed");
        var retryAttempt = store.StartImplementationRun(item.Id, new StartImplementationRunRequest(aiRun.Id, "crille", repository.Id))!;
        var runs = store.GetImplementationRuns(item.Id);
        var retryCleanup = store.RenderPreviousImplementationRunCleanupManifest(item.Id, retryAttempt.Id)!;
        var cleanup = store.RenderWorkItemCleanupManifest(item.Id)!;

        Assert.Equal(2, runs.Count);
        Assert.EndsWith("-retry-2", retryAttempt.Branch, StringComparison.Ordinal);
        Assert.Contains(runs, run => run.Id == failedAttempt.Id && run.Status == "Failed");
        Assert.Contains(runs, run => run.Id == retryAttempt.Id && run.Status == "Queued");
        Assert.Contains(RepositoryImplementationJobManifestRenderer.JobName(failedAttempt, store.GetWorkItemDetail(item.Id)!), retryCleanup);
        Assert.Contains(RepositoryImplementationJobManifestRenderer.GitHubTokenSecretName(failedAttempt), retryCleanup);
        Assert.DoesNotContain(RepositoryImplementationJobManifestRenderer.JobName(retryAttempt, store.GetWorkItemDetail(item.Id)!), retryCleanup);
        Assert.DoesNotContain(RepositoryImplementationJobManifestRenderer.GitHubTokenSecretName(retryAttempt), retryCleanup);
        Assert.DoesNotContain("rosenvall-devops-codex-home", retryCleanup);
        Assert.Contains(RepositoryImplementationJobManifestRenderer.JobName(failedAttempt, store.GetWorkItemDetail(item.Id)!), cleanup);
        Assert.Contains(RepositoryImplementationJobManifestRenderer.JobName(retryAttempt, store.GetWorkItemDetail(item.Id)!), cleanup);
        Assert.Contains(RepositoryImplementationJobManifestRenderer.GitHubTokenSecretName(failedAttempt), cleanup);
        Assert.Contains(RepositoryImplementationJobManifestRenderer.GitHubTokenSecretName(retryAttempt), cleanup);
        Assert.Contains(store.GetTimeline(board.Id), entry => entry.WorkItemId == item.Id && entry.Message.Contains("attempt 2", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Work_item_cleanup_manifest_includes_preview_source_runtime_artifacts()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var board = store.GetWorkspaces().SelectMany(workspace => store.GetBoards(workspace.Id)).First();
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "klocka", "gör en hemsida med en klocka", "Todo", "Medium", null));
        var run = store.StartAiPlan(item.Id, "codex", "gpt-5.4", "Build a clock preview.")!;
        run.Approve("crille");
        var context = store.GetWorkItemDetail(item.Id)!;

        var manifest = store.RenderWorkItemCleanupManifest(item.Id)!;

        Assert.Contains(PreviewSourceJobManifestRenderer.JobName(run, context), manifest);
        Assert.Contains(PreviewSourceJobManifestRenderer.ResultConfigMapName(run), manifest);
        Assert.Contains("kind: Job", manifest);
        Assert.Contains("kind: ConfigMap", manifest);
        Assert.DoesNotContain("kind: PersistentVolumeClaim", manifest);
        Assert.DoesNotContain("rosenvall-devops-codex-home", manifest);
    }

    [Fact]
    public async Task Planning_prompt_includes_board_ai_context_gitops_settings_and_skill_references_only()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest("GitHub", "rosenvalls-homelab", "https://github.com/rosenvall/rosenvalls-homelab.git", "main", "https://github.com/rosenvall/rosenvalls-homelab", "rosenvall", "gitops-homelab"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest(
            "Homelab",
            repository.Id,
            null,
            null,
            null,
            null,
            null,
            ImplementationProfile: "gitops-homelab",
            GitOpsSettings: new BoardGitOpsSettingsRequest(["clusters/prod/"], "argocd", "homelab=true"),
            AiContext: new BoardAiContextRequest("Only edit production after checking overlays.", ["fake-skill-body-marker", "argocd"], true)))!;
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Update app", "Change the GitOps app.", "Todo", "Medium", null));
        var context = store.GetWorkItemDetail(item.Id)!;
        var promptCapture = Path.Combine(Path.GetTempPath(), $"codex-prompt-{Guid.NewGuid():N}.md");
        var fakeCodex = CreateFakeCodexScript(exitCode: 0, plan: "Plan from fake Codex.", promptCapturePath: promptCapture);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:Codex:Path"] = fakeCodex,
                ["Ai:Codex:Home"] = Path.Combine(Path.GetTempPath(), $"codex-home-{Guid.NewGuid():N}")
            })
            .Build();
        var provider = new CodexCliPlanProvider(configuration, NullLogger<CodexCliPlanProvider>.Instance);

        await provider.GeneratePlanAsync("gpt-5.4", context, CancellationToken.None);

        var prompt = await File.ReadAllTextAsync(promptCapture);
        Assert.Contains("Only edit production after checking overlays.", prompt);
        Assert.Contains("Enabled board skills: fake-skill-body-marker, argocd", prompt);
        Assert.Contains("Allowed GitOps paths: clusters/prod/", prompt);
        Assert.Contains("ArgoCD namespace: argocd", prompt);
        Assert.Contains("Ask blocking questions", prompt);
        Assert.DoesNotContain("FULL FAKE SKILL BODY", prompt);
    }

    [Fact]
    public async Task Planning_prompt_includes_repo_skill_drafts_before_asking_blocking_questions()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest("GitHub", "Rosenvalls-Homelab", "https://github.com/carnufex/Rosenvalls-Homelab.git", "master", "https://github.com/carnufex/Rosenvalls-Homelab", "carnufex", "gitops-homelab"));
        var profile = new RepositoryProfileDto(
            "gitops-homelab",
            "Talos GitOps homelab",
            0.97,
            ["argocd", "gitops-app-onboarding", "cloudflare-gateway-routing"],
            "Use repo-local homelab conventions before asking the user.",
            ["kubernetes/applications/application-set.yaml", "tofu/"],
            "scanner",
            ["kubernetes", "argocd", "gateway-api"],
            [
                new RepositorySkillDraftDto("gitops-app-onboarding", "Each app under kubernetes/applications gets its own namespace.", "Use kubernetes/applications/<app-name>/ and namespace <app-name>. Wire it through the ApplicationSet pattern.", true),
                new RepositorySkillDraftDto("cloudflare-gateway-routing", "Public routing uses Cloudflare tunnel to Gateway API.", "Expose public HTTP apps with HTTPRoute/Gateway API unless the request says internal-only.", true)
            ]);
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest(
            "Rosenvalls-Homelab",
            repository.Id,
            null,
            null,
            null,
            null,
            null,
            ImplementationProfile: "gitops-homelab",
            GitOpsSettings: new BoardGitOpsSettingsRequest(["kubernetes/applications/", "kubernetes/infrastructure/", "tofu/"], "argocd", ""),
            AiContext: new BoardAiContextRequest(profile.Instructions, profile.EnabledSkills, true),
            RepositoryProfile: profile))!;
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Hello World site", "Create a public Hello World app at test.rosenvall.se.", "Todo", "Medium", null));
        var context = store.GetWorkItemDetail(item.Id)!;
        var promptCapture = Path.Combine(Path.GetTempPath(), $"codex-prompt-{Guid.NewGuid():N}.md");
        var fakeCodex = CreateFakeCodexScript(exitCode: 0, plan: "Plan from fake Codex.", promptCapturePath: promptCapture);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:Codex:Path"] = fakeCodex,
                ["Ai:Codex:Home"] = Path.Combine(Path.GetTempPath(), $"codex-home-{Guid.NewGuid():N}")
            })
            .Build();
        var provider = new CodexCliPlanProvider(configuration, NullLogger<CodexCliPlanProvider>.Instance);

        await provider.GeneratePlanAsync("gpt-5.5", context, CancellationToken.None);

        var prompt = await File.ReadAllTextAsync(promptCapture);
        Assert.Contains("Enabled repo skill drafts:", prompt);
        Assert.Contains("gitops-app-onboarding", prompt);
        Assert.Contains("Use kubernetes/applications/<app-name>/ and namespace <app-name>", prompt);
        Assert.Contains("AppProject", prompt);
        Assert.Contains("destination namespace", prompt);
        Assert.Contains("cloudflare-gateway-routing", prompt);
        Assert.Contains("HTTPRoute/Gateway API", prompt);
        Assert.Contains("Return blocking questions only for facts that cannot be answered by that context", prompt);
        Assert.Contains("Do not ask questions that these skills or conventions answer", prompt);
    }

    [Fact]
    public async Task Planning_prompt_includes_human_discussion_and_unresolved_plan_review_comments_without_system_result_spam()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var board = store.GetWorkspaces().SelectMany(workspace => store.GetBoards(workspace.Id)).First();
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Plan revision", "Create a clock app.", "Todo", "Medium", null));
        var firstPlan = store.StartAiPlan(item.Id, "codex", "gpt-5.5", "Plan:\n1. Build a clock.\n2. Add a preview.")!;
        store.AddComment(item.Id, "Christopher", "Comment", "Use Swedish wording and keep the interface compact.");
        store.AddAiPlanReviewComment(item.Id, firstPlan.Id, new CreateAiPlanReviewCommentRequest("block-2", "2. Add a preview.", "Mention the public hostname in the plan."), "Christopher");
        var context = store.GetWorkItemDetail(item.Id)!;
        var promptCapture = Path.Combine(Path.GetTempPath(), $"codex-prompt-{Guid.NewGuid():N}.md");
        var fakeCodex = CreateFakeCodexScript(exitCode: 0, plan: "Revised plan from fake Codex.", promptCapturePath: promptCapture);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:Codex:Path"] = fakeCodex,
                ["Ai:Codex:Home"] = Path.Combine(Path.GetTempPath(), $"codex-home-{Guid.NewGuid():N}")
            })
            .Build();
        var provider = new CodexCliPlanProvider(configuration, NullLogger<CodexCliPlanProvider>.Instance);

        await provider.GeneratePlanAsync("gpt-5.5", context, CancellationToken.None);

        var prompt = await File.ReadAllTextAsync(promptCapture);
        Assert.Contains("Work item discussion:", prompt);
        Assert.Contains("Christopher: Use Swedish wording and keep the interface compact.", prompt);
        Assert.Contains("Unresolved plan review comments:", prompt);
        Assert.Contains("Plan #1 block-2", prompt);
        Assert.Contains("Mention the public hostname in the plan.", prompt);
        Assert.DoesNotContain("Created plan #1", prompt);
    }

    [Fact]
    public void Implementation_manifest_prompt_includes_board_context_and_allowed_path_validation()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest("GitHub", "rosenvalls-homelab", "https://github.com/rosenvall/rosenvalls-homelab.git", "main", "https://github.com/rosenvall/rosenvalls-homelab", "rosenvall", "gitops-homelab"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest(
            "Homelab",
            repository.Id,
            null,
            null,
            null,
            null,
            null,
            ImplementationProfile: "gitops-homelab",
            GitOpsSettings: new BoardGitOpsSettingsRequest(["clusters/prod/", "apps/home/"], "argocd", "homelab=true"),
            AiContext: new BoardAiContextRequest("Use app-of-apps conventions.", ["argocd", "kubernetes"], true)))!;
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Update app", "Change the GitOps app.", "Todo", "Medium", null));
        var aiRun = store.StartAiPlan(item.Id, "codex", "gpt-5.4", "Plan:\n1. Update apps/home.")!;
        var implementationRun = store.StartImplementationRun(item.Id, new StartImplementationRunRequest(aiRun.Id, "crille", repository.Id))!;

        var manifest = store.RenderImplementationRunManifest(implementationRun.Id, new ConfigurationBuilder().Build())!;
        var prompt = DecodeManifestEnvironmentValue(manifest, "ROSENVALL_PROMPT_B64");

        Assert.Contains("Implementation profile: gitops-homelab", prompt);
        Assert.Contains("Use app-of-apps conventions.", prompt);
        Assert.Contains("Enabled board skills: argocd, kubernetes", prompt);
        Assert.Contains("Allowed GitOps paths: clusters/prod/, apps/home/", prompt);
        Assert.Contains("PR-first", prompt);
        Assert.Contains("ROSENVALL_ALLOWED_PATHS_B64", manifest);
        Assert.Contains("RDO_STEP=Inspecting", manifest);
        Assert.Contains("RDO_STEP=Validating", manifest);
        Assert.Contains("activeDeadlineSeconds: 3600", manifest);
        Assert.Contains("RDO_FAILURE=Changed files outside allowed GitOps paths", manifest);
        Assert.Contains("git status --porcelain | sed 's/^...//' | sed 's#.* -> ##' > \"$workspace/uncommitted-files.txt\"", manifest);
        Assert.Contains("git diff --name-only \"$ROSENVALL_DEFAULT_BRANCH\"...HEAD > \"$workspace/committed-files.txt\"", manifest);
        Assert.Contains("sort -u > \"$workspace/changed-files.txt\"", manifest);
        Assert.Contains("if [ ! -s \"$workspace/changed-files.txt\" ]; then echo \"RDO_FAILURE=No changes produced\"; exit 20; fi", manifest);
        Assert.Contains("if [ -s \"$workspace/uncommitted-files.txt\" ]; then", manifest);
        Assert.Contains("json_escape() {", manifest);
        Assert.Contains("ROSENVALL_WORK_ITEM_TITLE", manifest);
        Assert.Contains("git commit -m \"$commit_title\"", manifest);
        Assert.Contains("pr_payload=\"{\\\"title\\\":\\\"$pr_title\\\"", manifest);
        Assert.Contains("No uncommitted changes remain; using existing branch commits.", manifest);
        Assert.Contains("curl -fsS -G \"https://api.github.com/repos/$ROSENVALL_REPOSITORY/pulls\"", manifest);
        Assert.Contains("--data-urlencode \"head=$repo_owner:$ROSENVALL_BRANCH\"", manifest);
        Assert.Contains("if [ -n \"$existing_pr_url\" ]; then", manifest);
        Assert.Contains("RDO_PULL_REQUEST_URL=$pr_url", manifest);
        Assert.True(manifest.IndexOf("RDO_STEP=PullRequestReady", StringComparison.Ordinal) < manifest.IndexOf("RDO_PULL_REQUEST_URL=$pr_url", StringComparison.Ordinal));

        Assert.Contains("Do not run git add, git commit, git push, gh pr, or GitHub pull request API calls.", prompt);
        Assert.Contains("Leave all file changes uncommitted in the current checkout; the runner owns commit, push, and pull request creation.", prompt);
        Assert.Contains("Do not open a pull request yourself.", prompt);
    }

    [Fact]
    public void Gitops_homelab_implementation_prompt_requires_appproject_destination_for_new_application_namespaces()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest("GitHub", "rosenvalls-homelab", "https://github.com/carnufex/Rosenvalls-Homelab.git", "master", "https://github.com/carnufex/Rosenvalls-Homelab", "carnufex", "gitops-homelab"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest(
            "Homelab",
            repository.Id,
            null,
            null,
            null,
            null,
            null,
            ImplementationProfile: "gitops-homelab",
            GitOpsSettings: new BoardGitOpsSettingsRequest(["kubernetes/applications/", "tofu/"], "argocd", ""),
            AiContext: new BoardAiContextRequest("Use ApplicationSet conventions.", ["argocd", "gitops-app-onboarding"], true)))!;
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Add demo app", "Create a new app namespace called demo.", "Todo", "Medium", null));
        var aiRun = store.StartAiPlan(item.Id, "codex", "gpt-5.5", "Add kubernetes/applications/demo.")!;
        var implementationRun = store.StartImplementationRun(item.Id, new StartImplementationRunRequest(aiRun.Id, "crille", repository.Id))!;

        var manifest = store.RenderImplementationRunManifest(implementationRun.Id, new ConfigurationBuilder().Build())!;
        var prompt = DecodeManifestEnvironmentValue(manifest, "ROSENVALL_PROMPT_B64");

        Assert.Contains("New ApplicationSet app rule", prompt);
        Assert.Contains("kubernetes/applications/<app-name>/", prompt);
        Assert.Contains("kubernetes/applications/project.yaml", prompt);
        Assert.Contains("destination namespace <app-name>", prompt);
        Assert.Contains("kubectl kustomize kubernetes/applications/<app-name>", prompt);
    }

    [Fact]
    public void Implementation_manifest_uses_upgraded_gitops_paths_for_legacy_homelab_defaults()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest("GitHub", "rosenvalls-homelab", "https://github.com/rosenvall/rosenvalls-homelab.git", "main", "https://github.com/rosenvall/rosenvalls-homelab", "rosenvall", "gitops-homelab"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest(
            "Homelab",
            repository.Id,
            null,
            null,
            null,
            null,
            null,
            ImplementationProfile: "gitops-homelab",
            GitOpsSettings: new BoardGitOpsSettingsRequest(["apps/", "clusters/", "infrastructure/"], "argocd", "")))!;
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Add Kubernetes app", "Add kubernetes/applications/test.", "Todo", "Medium", null));
        var aiRun = store.StartAiPlan(item.Id, "codex", "gpt-5.5", "Plan:\n1. Add kubernetes/applications/test.")!;
        var implementationRun = store.StartImplementationRun(item.Id, new StartImplementationRunRequest(aiRun.Id, "crille", repository.Id))!;

        var manifest = store.RenderImplementationRunManifest(implementationRun.Id, new ConfigurationBuilder().Build())!;
        var prompt = DecodeManifestEnvironmentValue(manifest, "ROSENVALL_PROMPT_B64");
        var allowedPaths = DecodeManifestEnvironmentValue(manifest, "ROSENVALL_ALLOWED_PATHS_B64");

        Assert.Contains("Allowed GitOps paths: apps/, clusters/, infrastructure/, kubernetes/, tofu/", prompt);
        Assert.Contains("kubernetes/", allowedPaths);
        Assert.Contains("tofu/", allowedPaths);
    }

    [Fact]
    public void Repository_cleanup_run_manifest_includes_source_pr_context_and_runner_contract()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest("GitHub", "rosenvalls-homelab", "https://github.com/carnufex/Rosenvalls-Homelab.git", "master", "https://github.com/carnufex/Rosenvalls-Homelab", "carnufex", "gitops-homelab"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("Homelab", repository.Id, null, null, null, null, null, ImplementationProfile: "gitops-homelab"))!;
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "test", "Create test.rosenvall.se.", "Review", "Medium", null));
        var aiRun = store.StartAiPlan(item.Id, "codex", "gpt-5.5", "Plan:\n1. Add kubernetes/applications/test.")!;
        var implementationRun = store.StartImplementationRun(item.Id, new StartImplementationRunRequest(aiRun.Id, "crille", repository.Id))!;
        store.UpdateImplementationRun(implementationRun.Id, "PullRequestReady", "RDO_COMMIT=abc123\nRDO_PULL_REQUEST_URL=https://github.com/carnufex/Rosenvalls-Homelab/pull/33");

        var cleanupRun = store.StartRepositoryCleanupRun(item.Id, implementationRun.Id, "crille", "merged", "diff --git a/kubernetes/applications/test/kustomization.yaml b/kubernetes/applications/test/kustomization.yaml")!;
        var manifest = store.RenderRepositoryCleanupRunManifest(cleanupRun.Id, new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:Codex:KubernetesRunnerImage"] = "ghcr.io/carnufex/rosenvall-devops-api@sha256:cleanupdigest"
            })
            .Build(), "github-token-cleanup")!;
        var prompt = DecodeManifestEnvironmentValue(manifest, "ROSENVALL_CLEANUP_PROMPT_B64");
        var sourceDiff = DecodeManifestEnvironmentValue(manifest, "ROSENVALL_SOURCE_PR_DIFF_B64");

        AssertCodexRepositoryJobYamlContract(manifest, "ghcr.io/carnufex/rosenvall-devops-api@sha256:cleanupdigest", "GITHUB_TOKEN");
        Assert.EndsWith("-test-cleanup", cleanupRun.Branch, StringComparison.Ordinal);
        Assert.Contains("Source pull request: https://github.com/carnufex/Rosenvalls-Homelab/pull/33", prompt);
        Assert.Contains("Remove or revert repository resources introduced by the source pull request.", prompt);
        Assert.Contains("Allowed GitOps paths: apps/, clusters/, infrastructure/, kubernetes/, tofu/", prompt);
        Assert.Contains("Do not run git add, git commit, git push, gh pr, or GitHub pull request API calls.", prompt);
        Assert.Contains("kubernetes/applications/test/kustomization.yaml", sourceDiff);
        Assert.Contains("codex exec --ephemeral", manifest);
        Assert.Contains("image: ghcr.io/carnufex/rosenvall-devops-api@sha256:cleanupdigest", manifest);
        Assert.DoesNotContain("image: ghcr.io/carnufex/rosenvall-devops-api:main", manifest);
        Assert.Contains("--sandbox danger-full-access", manifest);
        Assert.Contains("codex-output.log", manifest);
        Assert.Contains("RDO_FAILURE=Codex runner sandbox is unavailable in this Kubernetes runner", manifest);
        Assert.Contains("git remote set-url origin \"$ROSENVALL_REPOSITORY_URL\"", manifest);
        Assert.Contains("unset GITHUB_TOKEN", manifest);
        Assert.Contains("codex_pid=$!", manifest);
        Assert.Contains("rm -f \"$CODEX_HOME/auth.json\"", manifest);
        Assert.Contains("wait \"$codex_pid\"", manifest);
        Assert.True(manifest.IndexOf("rm -f \"$CODEX_HOME/auth.json\"", StringComparison.Ordinal) > manifest.IndexOf("codex exec", StringComparison.Ordinal));
        Assert.True(manifest.IndexOf("rm -f \"$CODEX_HOME/auth.json\"", StringComparison.Ordinal) < manifest.IndexOf("wait \"$codex_pid\"", StringComparison.Ordinal));
        Assert.Contains("GITHUB_TOKEN=\"$github_token_for_runner\"", manifest);
        Assert.Contains("git remote set-url origin \"$auth_remote\"", manifest);
        Assert.DoesNotContain("--dangerously-bypass-approvals-and-sandbox", manifest);
        Assert.Contains("activeDeadlineSeconds: 3600", manifest);
        Assert.Contains("ROSENVALL_SOURCE_PR_DIFF_B64", manifest);
        Assert.Contains("curl -fsS -G \"https://api.github.com/repos/$ROSENVALL_REPOSITORY/pulls\"", manifest);
        Assert.Contains("--data-urlencode \"head=$repo_owner:$ROSENVALL_BRANCH\"", manifest);
        Assert.Contains("if [ -n \"$existing_pr_url\" ]; then", manifest);
        Assert.Contains("json_escape() {", manifest);
        Assert.Contains("ROSENVALL_WORK_ITEM_TITLE", manifest);
        Assert.Contains("git commit -m \"$cleanup_title\"", manifest);
        Assert.Contains("pr_payload=\"{\\\"title\\\":\\\"$pr_title\\\"", manifest);
        Assert.Contains("RDO_CLEANUP_PULL_REQUEST_URL=", manifest);
        Assert.True(manifest.IndexOf("RDO_STEP=PullRequestReady", StringComparison.Ordinal) < manifest.IndexOf("RDO_CLEANUP_PULL_REQUEST_URL=$pr_url", StringComparison.Ordinal));
        Assert.Contains("github-token-cleanup", manifest);
        Assert.Contains("app.kubernetes.io/name: rosenvall-devops-api", manifest);
        Assert.Contains("topologyKey: kubernetes.io/hostname", manifest);
        Assert.DoesNotContain("rosenvall-devops-codex-home\n                             mountPath: /app/codex-home", manifest);
    }

    [Fact]
    public void Repository_cleanup_run_retries_use_cleanup_retry_branch_and_are_persisted()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest("GitHub", "rosenvalls-homelab", "https://github.com/carnufex/Rosenvalls-Homelab.git", "master", "https://github.com/carnufex/Rosenvalls-Homelab", "carnufex", "gitops-homelab"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("Homelab", repository.Id, null, null, null, null, null, ImplementationProfile: "gitops-homelab"))!;
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "test", "Create test.rosenvall.se.", "Review", "Medium", null));
        var aiRun = store.StartAiPlan(item.Id, "codex", "gpt-5.5", "Plan")!;
        var implementationRun = store.StartImplementationRun(item.Id, new StartImplementationRunRequest(aiRun.Id, "crille", repository.Id))!;
        store.UpdateImplementationRun(implementationRun.Id, "PullRequestReady", "RDO_PULL_REQUEST_URL=https://github.com/carnufex/Rosenvalls-Homelab/pull/33");

        var first = store.StartRepositoryCleanupRun(item.Id, implementationRun.Id, "crille", "merged", "diff")!;
        store.UpdateRepositoryCleanupRun(first.Id, "Failed", "RDO_FAILURE=Cleanup failed", "Cleanup failed");
        var second = store.StartRepositoryCleanupRun(item.Id, implementationRun.Id, "crille", "merged", "diff")!;
        var reopened = fixture.Reopen();
        var persisted = reopened.GetRepositoryCleanupRuns(item.Id);

        Assert.EndsWith("-test-cleanup", first.Branch, StringComparison.Ordinal);
        Assert.EndsWith("-test-cleanup-retry-2", second.Branch, StringComparison.Ordinal);
        Assert.Contains(persisted, run => run.Id == first.Id && run.Status == "Failed");
        Assert.Contains(persisted, run => run.Id == second.Id && run.Status == "Queued");
        Assert.NotNull(reopened.GetWorkItemDetail(item.Id)!.RepositoryCleanupRuns);
    }

    [Fact]
    public void Repository_implementation_branches_slug_spaces_and_invalid_ref_characters()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest("GitHub", "demo", "https://github.com/carnufex/demo.git", "main", "https://github.com/carnufex/demo", "carnufex", "code-repo"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("Demo", repository.Id, null, null, null, null, null))!;
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "implement app: clock/site?", "Create a clock app.", "Todo", "Medium", null));
        var aiRun = store.StartAiPlan(item.Id, "codex", "gpt-5.5", "Plan")!;

        var first = store.StartImplementationRun(item.Id, new StartImplementationRunRequest(aiRun.Id, "crille", repository.Id))!;
        store.UpdateImplementationRun(first.Id, "Failed", "RDO_FAILURE=failed", "failed");
        var retry = store.StartImplementationRun(item.Id, new StartImplementationRunRequest(aiRun.Id, "crille", repository.Id))!;

        Assert.EndsWith("-implement-app-clock-site", first.Branch, StringComparison.Ordinal);
        Assert.EndsWith("-implement-app-clock-site-retry-2", retry.Branch, StringComparison.Ordinal);
        Assert.DoesNotContain(' ', retry.Branch);
        Assert.DoesNotContain(':', retry.Branch);
        Assert.DoesNotContain('?', retry.Branch);
    }

    [Fact]
    public void Repository_cleanup_run_does_not_duplicate_while_existing_attempt_is_pending()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest("GitHub", "rosenvalls-homelab", "https://github.com/carnufex/Rosenvalls-Homelab.git", "master", "https://github.com/carnufex/Rosenvalls-Homelab", "carnufex", "gitops-homelab"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("Homelab", repository.Id, null, null, null, null, null, ImplementationProfile: "gitops-homelab"))!;
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "test", "Create test.rosenvall.se.", "Review", "Medium", null));
        var aiRun = store.StartAiPlan(item.Id, "codex", "gpt-5.5", "Plan")!;
        var implementationRun = store.StartImplementationRun(item.Id, new StartImplementationRunRequest(aiRun.Id, "crille", repository.Id))!;
        store.UpdateImplementationRun(implementationRun.Id, "PullRequestReady", "RDO_PULL_REQUEST_URL=https://github.com/carnufex/Rosenvalls-Homelab/pull/34");

        var first = store.StartRepositoryCleanupRun(item.Id, implementationRun.Id, "crille", "merged", "diff")!;
        var second = store.StartRepositoryCleanupRun(item.Id, implementationRun.Id, "crille", "merged", "diff")!;

        Assert.Equal(first.Id, second.Id);
        Assert.Single(store.GetRepositoryCleanupRuns(item.Id));
    }

    [Fact]
    public void External_cleanup_pull_request_can_be_adopted_on_same_card()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest("GitHub", "rosenvalls-homelab", "https://github.com/carnufex/Rosenvalls-Homelab.git", "master", "https://github.com/carnufex/Rosenvalls-Homelab", "carnufex", "gitops-homelab"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("Homelab", repository.Id, null, null, null, null, null, ImplementationProfile: "gitops-homelab"))!;
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "test", "Create test.rosenvall.se.", "Done", "Medium", null));
        var aiRun = store.StartAiPlan(item.Id, "codex", "gpt-5.5", "Plan")!;
        var implementationRun = store.StartImplementationRun(item.Id, new StartImplementationRunRequest(aiRun.Id, "crille", repository.Id))!;
        store.UpdateImplementationRun(implementationRun.Id, "PullRequestReady", "RDO_PULL_REQUEST_URL=https://github.com/carnufex/Rosenvalls-Homelab/pull/34");

        var adopted = store.AdoptRepositoryCleanupPullRequest(item.Id, implementationRun.Id, "crille", "https://github.com/carnufex/Rosenvalls-Homelab/pull/35", "rdo/cleanup-test-namespace", "open", false)!;
        var detail = store.GetWorkItemDetail(item.Id)!;

        Assert.True(adopted.Adopted);
        Assert.Equal("PullRequestReady", adopted.Status);
        Assert.Equal("rdo/cleanup-test-namespace", adopted.Branch);
        Assert.Equal("https://github.com/carnufex/Rosenvalls-Homelab/pull/35", detail.Item.PullRequestUrl);
        Assert.Equal("CleanupReady", detail.Item.AiStatus);
        Assert.Contains(store.GetTimeline(board.Id), entry => entry.Kind == "RepositoryCleanupPullRequest" && entry.Url == adopted.CleanupPullRequestUrl);
    }

    [Fact]
    public void Stuck_cleanup_run_records_diagnostics_and_fails_card_cleanup()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest("GitHub", "rosenvalls-homelab", "https://github.com/carnufex/Rosenvalls-Homelab.git", "master", "https://github.com/carnufex/Rosenvalls-Homelab", "carnufex", "gitops-homelab"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("Homelab", repository.Id, null, null, null, null, null, ImplementationProfile: "gitops-homelab"))!;
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "test", "Create test.rosenvall.se.", "Review", "Medium", null));
        var aiRun = store.StartAiPlan(item.Id, "codex", "gpt-5.5", "Plan")!;
        var implementationRun = store.StartImplementationRun(item.Id, new StartImplementationRunRequest(aiRun.Id, "crille", repository.Id))!;
        store.UpdateImplementationRun(implementationRun.Id, "PullRequestReady", "RDO_PULL_REQUEST_URL=https://github.com/carnufex/Rosenvalls-Homelab/pull/34");
        var cleanupRun = store.StartRepositoryCleanupRun(item.Id, implementationRun.Id, "crille", "merged", "diff")!;

        var failed = store.MarkRepositoryCleanupRunStuck(cleanupRun.Id, "cleanup-task-4826", "cleanup-task-4826-pod", "ContainersNotReady", "pod has unbound immediate PersistentVolumeClaims")!;

        Assert.Equal("Failed", failed.Status);
        Assert.Equal("cleanup-task-4826", failed.JobName);
        Assert.Equal("cleanup-task-4826-pod", failed.PodName);
        Assert.Contains("PersistentVolumeClaims", failed.LastEventSummary);
        Assert.Equal("CleanupFailed", store.GetWorkItemDetail(item.Id)!.Item.AiStatus);
    }

    [Fact]
    public void Implementation_run_update_is_noop_when_status_reason_and_terminal_tail_are_unchanged()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest("GitHub", "rosenvalls-homelab", "https://github.com/carnufex/Rosenvalls-Homelab.git", "master", "https://github.com/carnufex/Rosenvalls-Homelab", "carnufex", "gitops-homelab"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("Homelab", repository.Id, null, null, null, null, null, ImplementationProfile: "gitops-homelab"))!;
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "clock", "Create a clock app.", "Todo", "Medium", null));
        var aiRun = store.StartAiPlan(item.Id, "codex", "gpt-5.5", "Plan.")!;
        var implementationRun = store.StartImplementationRun(item.Id, new StartImplementationRunRequest(aiRun.Id, "crille", repository.Id))!;

        var first = store.UpdateImplementationRun(implementationRun.Id, "Cloning", "RDO_STEP=Cloning\nCloning into repo...")!;
        var second = store.UpdateImplementationRun(implementationRun.Id, "Cloning", "RDO_STEP=Cloning\nCloning into repo...")!;

        Assert.Equal(first.UpdatedAt, second.UpdatedAt);
        Assert.Equal(first.TerminalLines!.Select(line => line.Message), second.TerminalLines!.Select(line => line.Message));
    }

    [Fact]
    public void GitHub_integration_sync_is_noop_when_installation_payload_is_unchanged()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var request = new GitHubIntegrationCallbackRequest(12345, "carnufex", "User", "github-app", 7, "Installed");

        store.UpsertGitHubIntegrations([request]);
        var writesAfterFirstSync = store.SnapshotPersistWriteCount;
        var first = store.GetGitHubIntegration(12345)!;

        store.UpsertGitHubIntegrations([request]);
        var second = store.GetGitHubIntegration(12345)!;

        Assert.Equal(writesAfterFirstSync, store.SnapshotPersistWriteCount);
        Assert.Equal(first.CreatedAt, second.CreatedAt);
    }

    [Fact]
    public void Preview_terminal_lines_are_deduplicated_capped_and_truncated()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var board = store.GetWorkspaces().SelectMany(workspace => store.GetBoards(workspace.Id)).First();
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "preview logs", "Generate lots of preview output.", "Todo", "Medium", null));
        store.BeginPreviewImplementation(item.Id, "codex");
        var longLine = new string('x', 1500);
        var first = store.AppendPreviewTerminalLine(item.Id, "agent", longLine)!;
        var writesAfterFirstAppend = store.SnapshotPersistWriteCount;

        var duplicate = store.AppendPreviewTerminalLine(item.Id, "agent", longLine)!;
        for (var index = 0; index < 250; index++)
        {
            store.AppendPreviewTerminalLine(item.Id, "agent", $"line-{index:D3}-{longLine}");
        }

        var detail = store.GetWorkItemDetail(item.Id)!;

        Assert.Equal(writesAfterFirstAppend, store.SnapshotPersistWriteCount - 250);
        Assert.Equal(first.Preview!.TerminalLines!.Select(line => line.Message), duplicate.Preview!.TerminalLines!.Select(line => line.Message));
        Assert.Equal(200, detail.Preview!.TerminalLines!.Count);
        Assert.All(detail.Preview!.TerminalLines!, line => Assert.True(line.Message.Length <= 1000));
        Assert.Contains(detail.Preview!.TerminalLines!, line => line.Message.StartsWith("line-249-", StringComparison.Ordinal));
    }

    [Fact]
    public void Preview_terminal_lines_are_split_by_lifecycle_step()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var board = store.GetWorkspaces().SelectMany(workspace => store.GetBoards(workspace.Id)).First();
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "preview steps", "Generate preview step logs.", "Todo", "Medium", null));
        store.BeginPreviewImplementation(item.Id, "codex");
        store.AppendPreviewTerminalLine(item.Id, "agent", "OpenAI Codex started.", stepKey: "source");
        store.AppendPreviewTerminalLine(item.Id, "system", "Codex generated 2 source files.", stepKey: "source");
        var sourceFiles = LocalReactPreviewProject.ForWorkItem(item.Key, item.Title, "Generate preview step logs.")
            .Select(file => file.Path == "src/App.tsx"
                ? new PreviewSourceFile(file.Key, file.Path, "export default function App() { return <main>Step logs</main>; }")
                : file)
            .ToArray();

        store.CompletePreviewImplementation(item.Id, sourceFiles, "codex");
        store.MarkPreviewApplying(item.Id, "Applying Kubernetes resources.");
        store.AppendPreviewTerminalLine(item.Id, "system", "kubectl apply started.", stepKey: "apply");
        store.MarkPreviewProvisioning(item.Id, "deployment/task-preview configured");
        store.UpdatePreviewHealth(item.Id, PreviewHealthCheckResult.Running("task-preview-pod", "Deployment is available and at least one preview pod is ready."));

        var preview = store.GetWorkItemDetail(item.Id)!.Preview!;
        var source = preview.StepLogs!.Single(step => step.Key == "source");
        var apply = preview.StepLogs!.Single(step => step.Key == "apply");
        var readiness = preview.StepLogs!.Single(step => step.Key == "readiness");
        var running = preview.StepLogs!.Single(step => step.Key == "running");

        Assert.Contains(source.TerminalLines!, line => line.Message.Contains("OpenAI Codex", StringComparison.Ordinal));
        Assert.Contains(apply.TerminalLines!, line => line.Message.Contains("kubectl apply", StringComparison.Ordinal));
        Assert.Contains(readiness.TerminalLines!, line => line.Message.Contains("deployment/task-preview", StringComparison.Ordinal));
        Assert.Contains(running.TerminalLines!, line => line.Message.Contains("Deployment is available", StringComparison.Ordinal));
        Assert.Equal("done", source.State);
        Assert.Equal("done", apply.State);
        Assert.Equal("done", readiness.State);
        Assert.Equal("done", running.State);
        Assert.Contains(preview.TerminalLines!, line => line.Message.Contains("kubectl apply", StringComparison.Ordinal));
    }

    [Fact]
    public void Legacy_preview_terminal_tail_is_backfilled_into_step_logs_on_snapshot_load()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var board = store.GetWorkspaces().SelectMany(workspace => store.GetBoards(workspace.Id)).First();
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "legacy preview", "Load old preview logs.", "Todo", "Medium", null));
        store.BeginPreviewImplementation(item.Id, "codex");
        store.AppendPreviewTerminalLine(item.Id, "agent", "OpenAI Codex v0.133.0");
        store.AppendPreviewTerminalLine(item.Id, "system", "kubectl apply started.");
        fixture.UpdateSnapshotJson(RemovePreviewStepLogs);

        var reopened = fixture.Reopen();
        var preview = reopened.GetWorkItemDetail(item.Id)!.Preview!;

        Assert.NotNull(preview.StepLogs);
        Assert.Contains(preview.StepLogs!.Single(step => step.Key == "source").TerminalLines!, line => line.Message.Contains("Legacy combined log", StringComparison.Ordinal));
        Assert.Contains(preview.StepLogs!.Single(step => step.Key == "source").TerminalLines!, line => line.Message.Contains("OpenAI Codex", StringComparison.Ordinal));
        Assert.Contains(preview.StepLogs!.Single(step => step.Key == "apply").TerminalLines!, line => line.Message.Contains("kubectl apply", StringComparison.Ordinal));
    }

    [Fact]
    public void Snapshot_load_deduplicates_exact_rosenvall_ai_result_comments()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var board = store.GetWorkspaces().SelectMany(workspace => store.GetBoards(workspace.Id)).First();
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "plan duplicate", "Generate one plan.", "Todo", "Medium", null));
        store.StartAiPlan(item.Id, "codex", "gpt-5.4", "Implementation Plan");
        fixture.UpdateSnapshotJson(DuplicateFirstRosenvallAiResultComment);

        var reopened = fixture.Reopen();
        var detail = reopened.GetWorkItemDetail(item.Id)!;

        Assert.Equal(1, detail.Comments.Count(comment =>
            comment.Author == "Rosenvall AI" &&
            comment.Kind == "Result" &&
            comment.Body.Contains("Created plan #1", StringComparison.Ordinal)));
    }

    [Fact]
    public void Repository_run_terminal_tails_are_capped_and_long_lines_are_truncated()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest("GitHub", "rosenvalls-homelab", "https://github.com/carnufex/Rosenvalls-Homelab.git", "master", "https://github.com/carnufex/Rosenvalls-Homelab", "carnufex", "gitops-homelab"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("Homelab", repository.Id, null, null, null, null, null, ImplementationProfile: "gitops-homelab"))!;
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "clock", "Create a clock app.", "Todo", "Medium", null));
        var aiRun = store.StartAiPlan(item.Id, "codex", "gpt-5.5", "Plan.")!;
        var implementationRun = store.StartImplementationRun(item.Id, new StartImplementationRunRequest(aiRun.Id, "crille", repository.Id))!;
        var longLine = new string('x', 1500);
        var logs = string.Join('\n', Enumerable.Range(0, 175).Select(index => $"line-{index:D3}-{longLine}"));

        var updated = store.UpdateImplementationRun(implementationRun.Id, "Implementing", logs)!;

        Assert.Equal(150, updated.TerminalLines!.Count);
        Assert.All(updated.TerminalLines!, line => Assert.True(line.Message.Length <= 1000));
        Assert.DoesNotContain(updated.TerminalLines!, line => line.Message.StartsWith("line-000-", StringComparison.Ordinal));
        Assert.Contains(updated.TerminalLines!, line => line.Message.StartsWith("line-174-", StringComparison.Ordinal));
    }

    [Fact]
    public void Stuck_implementation_run_records_diagnostics_and_fails_card()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest("GitHub", "rosenvalls-homelab", "https://github.com/carnufex/Rosenvalls-Homelab.git", "master", "https://github.com/carnufex/Rosenvalls-Homelab", "carnufex", "gitops-homelab"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("Homelab", repository.Id, null, null, null, null, null, ImplementationProfile: "gitops-homelab"))!;
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "clock", "Create a clock app.", "Todo", "Medium", null));
        var aiRun = store.StartAiPlan(item.Id, "codex", "gpt-5.5", "Plan.")!;
        var implementationRun = store.StartImplementationRun(item.Id, new StartImplementationRunRequest(aiRun.Id, "crille", repository.Id))!;

        var failed = store.MarkImplementationRunStuck(implementationRun.Id, "impl-task-4826", "impl-task-4826-pod", "FailedScheduling - Insufficient memory", "0/4 nodes are available: 4 Insufficient memory.")!;

        Assert.Equal("Failed", failed.Status);
        Assert.Contains("Implementation job produced no runner output before timeout.", failed.FailureReason);
        Assert.Contains("insufficient memory", failed.FailureReason);
        Assert.Equal("impl-task-4826", failed.JobName);
        Assert.Equal("impl-task-4826-pod", failed.PodName);
        Assert.Contains("Insufficient memory", failed.LastEventSummary);
        Assert.Equal("Failed", store.GetWorkItemDetail(item.Id)!.Item.AiStatus);
    }

    [Fact]
    public void Stuck_implementation_run_classifies_multi_attach_volume_diagnostics()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest("GitHub", "demo", "https://github.com/carnufex/demo.git", "main", "https://github.com/carnufex/demo", "carnufex", "code-repo"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("Demo", repository.Id, null, null, null, null, null))!;
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "clock app", "Create a clock app.", "Todo", "Medium", null));
        var aiRun = store.StartAiPlan(item.Id, "codex", "gpt-5.5", "Plan.")!;
        var implementationRun = store.StartImplementationRun(item.Id, new StartImplementationRunRequest(aiRun.Id, "crille", repository.Id))!;

        var failed = store.MarkImplementationRunStuck(
            implementationRun.Id,
            "impl-task-4833",
            "impl-task-4833-pod",
            "Pod phase: Pending; init prepare-codex-home waiting: PodInitializing; container runner waiting: PodInitializing",
            "Warning FailedAttachVolume Multi-Attach error for volume pvc-123 Volume is already used by pod(s) rosenvall-devops-api");

        Assert.Contains("RWO PVC", failed.FailureReason);
        Assert.Contains("FailedAttachVolume", failed.LastEventSummary);
        Assert.Contains("prepare-codex-home", failed.LastCondition);
    }

    [Fact]
    public void Implementation_capacity_preflight_blocks_when_api_memory_headroom_is_low()
    {
        var diagnostics = new ApiResourceDiagnosticsDto(
            ProcessRssBytes: 900 * 1024 * 1024,
            MemoryCurrentBytes: 950 * 1024 * 1024,
            MemoryLimitBytes: 1024L * 1024 * 1024,
            MemoryAvailableBytes: 74L * 1024 * 1024,
            IsMemoryPressured: true,
            Status: "Degraded",
            Message: "API memory headroom is below 128 MiB.");

        var result = ImplementationCapacityPreflight.Evaluate(diagnostics, 128L * 1024 * 1024);

        Assert.False(result.Succeeded);
        Assert.Contains("memory pressured", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Same(diagnostics, result.Diagnostics);
    }

    [Fact]
    public void Pending_implementation_run_is_reported_before_starting_another_attempt()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest("GitHub", "rosenvalls-homelab", "https://github.com/carnufex/Rosenvalls-Homelab.git", "master", "https://github.com/carnufex/Rosenvalls-Homelab", "carnufex", "gitops-homelab"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("Homelab", repository.Id, null, null, null, null, null, ImplementationProfile: "gitops-homelab"))!;
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "clock", "Create a clock app.", "Todo", "Medium", null));
        var aiRun = store.StartAiPlan(item.Id, "codex", "gpt-5.5", "Plan.")!;
        var implementationRun = store.StartImplementationRun(item.Id, new StartImplementationRunRequest(aiRun.Id, "crille", repository.Id))!;

        var pending = store.GetPendingImplementationRun(item.Id);

        Assert.Equal(implementationRun.Id, pending?.Id);
    }

    [Theory]
    [InlineData("0/4 nodes are available: 4 Insufficient memory.", "insufficient memory")]
    [InlineData("0/4 nodes are available: 4 Insufficient cpu.", "insufficient CPU")]
    [InlineData("pods \"impl\" is forbidden: User cannot create resource", "RBAC")]
    [InlineData("container waiting reason ImagePullBackOff", "image pull")]
    [InlineData("Last State: Terminated Reason: OOMKilled Exit Code: 137", "OOMKilled")]
    public void Kubernetes_failure_classifier_maps_common_runtime_errors(string message, string expected)
    {
        var classified = KubernetesFailureClassifier.Classify(message);

        Assert.Contains(expected, classified, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Work_item_cleanup_manifest_includes_repository_cleanup_jobs_and_token_secrets()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest("GitHub", "rosenvalls-homelab", "https://github.com/carnufex/Rosenvalls-Homelab.git", "master", "https://github.com/carnufex/Rosenvalls-Homelab", "carnufex", "gitops-homelab"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("Homelab", repository.Id, null, null, null, null, null, ImplementationProfile: "gitops-homelab"))!;
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "test", "Create test.rosenvall.se.", "Review", "Medium", null));
        var aiRun = store.StartAiPlan(item.Id, "codex", "gpt-5.5", "Plan")!;
        var implementationRun = store.StartImplementationRun(item.Id, new StartImplementationRunRequest(aiRun.Id, "crille", repository.Id))!;
        store.UpdateImplementationRun(implementationRun.Id, "PullRequestReady", "RDO_PULL_REQUEST_URL=https://github.com/carnufex/Rosenvalls-Homelab/pull/33");
        var cleanupRun = store.StartRepositoryCleanupRun(item.Id, implementationRun.Id, "crille", "merged", "diff")!;

        var manifest = store.RenderWorkItemCleanupManifest(item.Id)!;
        var cleanupContext = store.GetWorkItemDetail(item.Id)!;

        Assert.Contains(RepositoryCleanupJobManifestRenderer.JobName(cleanupRun, cleanupContext), manifest);
        Assert.Contains(RepositoryCleanupJobManifestRenderer.GitHubTokenSecretName(cleanupRun), manifest);
        Assert.Contains(RepositoryImplementationJobManifestRenderer.JobName(implementationRun, cleanupContext), manifest);
        Assert.Contains(RepositoryImplementationJobManifestRenderer.GitHubTokenSecretName(implementationRun), manifest);
        Assert.DoesNotContain("rosenvall-devops-codex-home", manifest);
    }

    [Fact]
    public void Repository_cleanup_token_secret_payload_has_metadata_labels_and_data()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest("GitHub", "rosenvalls-homelab", "https://github.com/carnufex/Rosenvalls-Homelab.git", "master", "https://github.com/carnufex/Rosenvalls-Homelab", "carnufex", "gitops-homelab"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("Homelab", repository.Id, null, null, null, null, null, ImplementationProfile: "gitops-homelab"))!;
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "test", "Create test.rosenvall.se.", "Review", "Medium", null));
        var aiRun = store.StartAiPlan(item.Id, "codex", "gpt-5.5", "Plan")!;
        var implementationRun = store.StartImplementationRun(item.Id, new StartImplementationRunRequest(aiRun.Id, "crille", repository.Id))!;
        store.UpdateImplementationRun(implementationRun.Id, "PullRequestReady", "RDO_PULL_REQUEST_URL=https://github.com/carnufex/Rosenvalls-Homelab/pull/33");
        var cleanupRun = store.StartRepositoryCleanupRun(item.Id, implementationRun.Id, "crille", "merged", "diff")!;

        var payload = RepositoryCleanupJobManifestRenderer.RenderGitHubTokenSecret(cleanupRun, "ghs_cleanup_installation_token");
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var metadata = root.GetProperty("metadata");
        var labels = metadata.GetProperty("labels");
        var data = root.GetProperty("data");

        Assert.Equal("Secret", root.GetProperty("kind").GetString());
        Assert.Equal(RepositoryCleanupJobManifestRenderer.Namespace, metadata.GetProperty("namespace").GetString());
        Assert.Equal(RepositoryCleanupJobManifestRenderer.GitHubTokenSecretName(cleanupRun), metadata.GetProperty("name").GetString());
        Assert.Equal("rosenvall-devops-repository-cleanup", labels.GetProperty("app.kubernetes.io/part-of").GetString());
        Assert.Equal(cleanupRun.Id.ToString(), labels.GetProperty("rosenvall.devops/repository-cleanup-run").GetString());
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("ghs_cleanup_installation_token")), data.GetProperty("token").GetString());
        Assert.DoesNotContain("ghs_cleanup_installation_token", payload);
        Assert.DoesNotContain("stringData", payload);
        Assert.DoesNotContain("kubectl.kubernetes.io/last-applied-configuration", payload);
    }

    [Fact]
    public async Task GitHub_client_reads_closes_and_comments_on_pull_requests()
    {
        var requests = new List<(HttpMethod Method, string Path, string Body)>();
        using var httpClient = new HttpClient(new RoutingHttpMessageHandler(request =>
        {
            var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "";
            requests.Add((request.Method, request.RequestUri!.PathAndQuery, body));
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/pulls/33", StringComparison.Ordinal))
            {
                return JsonResponse("""{"number":33,"state":"open","merged":false,"html_url":"https://github.com/carnufex/Rosenvalls-Homelab/pull/33","diff_url":"https://github.com/carnufex/Rosenvalls-Homelab/pull/33.diff"}""");
            }

            if (request.Method.Method == "PATCH" && request.RequestUri!.AbsolutePath.EndsWith("/pulls/33", StringComparison.Ordinal))
            {
                return JsonResponse("""{"number":33,"state":"closed","merged":false,"html_url":"https://github.com/carnufex/Rosenvalls-Homelab/pull/33"}""");
            }

            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/issues/33/comments", StringComparison.Ordinal))
            {
                return JsonResponse("""{"html_url":"https://github.com/carnufex/Rosenvalls-Homelab/pull/33#issuecomment-1"}""");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        }))
        {
            BaseAddress = new Uri("https://api.github.com")
        };
        var github = new GitHubRepositoryClient(httpClient, new ConfigurationBuilder().Build());

        var pullRequest = await github.GetPullRequestAsync("https://github.com/carnufex/Rosenvalls-Homelab/pull/33", "token", CancellationToken.None);
        var closed = await github.ClosePullRequestAsync(pullRequest!, "token", CancellationToken.None);
        var commented = await github.AddPullRequestCommentAsync(pullRequest!, "Closed by cleanup.", "token", CancellationToken.None);

        Assert.NotNull(pullRequest);
        Assert.Equal("open", pullRequest.State);
        Assert.False(pullRequest.Merged);
        Assert.True(closed);
        Assert.True(commented);
        Assert.Contains(requests, request => request.Method.Method == "PATCH" && request.Body.Contains("\"state\":\"closed\"", StringComparison.Ordinal));
        Assert.Contains(requests, request => request.Method == HttpMethod.Post && request.Body.Contains("Closed by cleanup.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GitHub_client_rejects_non_github_pull_request_urls()
    {
        var requests = new List<HttpRequestMessage>();
        using var httpClient = new HttpClient(new RoutingHttpMessageHandler(request =>
        {
            requests.Add(request);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }))
        {
            BaseAddress = new Uri("https://api.github.com")
        };
        var github = new GitHubRepositoryClient(httpClient, new ConfigurationBuilder().Build());

        var pullRequest = await github.GetPullRequestAsync("https://example.com/carnufex/Rosenvalls-Homelab/pull/33", "token", CancellationToken.None);

        Assert.Null(pullRequest);
        Assert.Empty(requests);
    }

    [Fact]
    public async Task Forgejo_client_reads_merges_closes_comments_and_deletes_repositories()
    {
        var requests = new List<(HttpMethod Method, string Path, string Body)>();
        using var httpClient = new HttpClient(new RoutingHttpMessageHandler(request =>
        {
            var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "";
            requests.Add((request.Method, request.RequestUri!.PathAndQuery, body));
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/repos/rdo/demo-app/pulls/7", StringComparison.Ordinal))
            {
                return JsonResponse("""{"number":7,"state":"open","merged":false,"html_url":"http://forgejo.local/rdo/demo-app/pulls/7","base":{"ref":"main"},"head":{"ref":"rdo/task-1"}}""");
            }

            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/repos/rdo/demo-app/pulls/7/files", StringComparison.Ordinal))
            {
                return JsonResponse("""[{"filename":"src/App.tsx","status":"modified","additions":12,"deletions":3},{"filename":"README.md","status":"added","additions":4,"deletions":0}]""");
            }

            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/repos/rdo/demo-app/pulls/7.diff", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("diff --git a/src/App.tsx b/src/App.tsx\n+hello\n", Encoding.UTF8, "text/plain")
                };
            }

            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/repos/rdo/demo-app/pulls/7/merge", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (request.Method.Method == "PATCH" && request.RequestUri!.AbsolutePath.EndsWith("/repos/rdo/demo-app/pulls/7", StringComparison.Ordinal))
            {
                return JsonResponse("""{"number":7,"state":"closed","merged":false,"html_url":"http://forgejo.local/rdo/demo-app/pulls/7"}""");
            }

            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/repos/rdo/demo-app/issues/7/comments", StringComparison.Ordinal))
            {
                return JsonResponse("""{"html_url":"http://forgejo.local/rdo/demo-app/pulls/7#issuecomment-1"}""");
            }

            if (request.Method == HttpMethod.Delete && request.RequestUri!.AbsolutePath.EndsWith("/repos/rdo/demo-app", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        }))
        {
            BaseAddress = new Uri("http://forgejo.local")
        };
        var forgejo = new ForgejoRepositoryClient(httpClient, new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LocalGit:ApiBaseUrl"] = "http://forgejo.local/api/v1",
                ["LocalGit:Username"] = "rdo",
                ["LocalGit:Password"] = "demo-token"
            })
            .Build());
        var repository = new RepositoryDto(Guid.NewGuid(), "LocalGit", "demo-app", "http://forgejo.local/rdo/demo-app.git", null, "main", DateTimeOffset.UtcNow, "rdo");

        var pullRequest = await forgejo.GetPullRequestAsync(repository, "http://forgejo.local/rdo/demo-app/pulls/7", CancellationToken.None);
        var pullRequestFromNumber = await forgejo.GetPullRequestAsync(repository, 7, "http://forgejo.local/rdo/demo-app", CancellationToken.None);
        var files = await forgejo.GetPullRequestFilesAsync(pullRequest!, CancellationToken.None);
        var diff = await forgejo.GetPullRequestDiffAsync(pullRequest!, CancellationToken.None);
        var merged = await forgejo.MergePullRequestAsync(pullRequest!, CancellationToken.None);
        var commented = await forgejo.AddPullRequestCommentAsync(pullRequest!, "Closed by cleanup.", CancellationToken.None);
        var closed = await forgejo.ClosePullRequestAsync(pullRequest!, CancellationToken.None);
        var deleted = await forgejo.DeleteRepositoryAsync(repository, CancellationToken.None);

        Assert.NotNull(pullRequest);
        Assert.Equal(7, pullRequest.Number);
        Assert.Equal("open", pullRequest.State);
        Assert.Equal("main", pullRequest.BaseRef);
        Assert.Equal("rdo/task-1", pullRequest.HeadRef);
        Assert.NotNull(pullRequestFromNumber);
        Assert.Equal(7, pullRequestFromNumber!.Number);
        Assert.Equal("http://forgejo.local/rdo/demo-app/pulls/7", pullRequestFromNumber.HtmlUrl);
        Assert.NotNull(files);
        Assert.Equal(2, files!.Count);
        Assert.Contains(files, file => file.Path == "src/App.tsx" && file.Additions == 12 && file.Deletions == 3);
        Assert.Contains("diff --git", diff);
        Assert.True(merged);
        Assert.True(commented);
        Assert.True(closed);
        Assert.True(deleted);
        Assert.Contains(requests, request => request.Method == HttpMethod.Post && request.Path.EndsWith("/pulls/7/merge", StringComparison.Ordinal));
        Assert.Contains(requests, request => request.Method == HttpMethod.Get && request.Path.EndsWith("/pulls/7/files?limit=200", StringComparison.Ordinal));
        Assert.Contains(requests, request => request.Method == HttpMethod.Get && request.Path.EndsWith("/pulls/7.diff", StringComparison.Ordinal));
        Assert.Contains(requests, request => request.Method.Method == "PATCH" && request.Body.Contains("\"state\":\"closed\"", StringComparison.Ordinal));
        Assert.Contains(requests, request => request.Method == HttpMethod.Delete && request.Path.EndsWith("/repos/rdo/demo-app", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Forgejo_client_reads_source_tree_and_text_files()
    {
        var requests = new List<(HttpMethod Method, string Path)>();
        using var httpClient = new HttpClient(new RoutingHttpMessageHandler(request =>
        {
            requests.Add((request.Method, request.RequestUri!.PathAndQuery));
            if (request.Method == HttpMethod.Get && request.RequestUri!.PathAndQuery.EndsWith("/repos/rdo/demo-app/contents?ref=main", StringComparison.Ordinal))
            {
                return JsonResponse("""[{"name":"src","path":"src","type":"dir"},{"name":"README.md","path":"README.md","type":"file","size":42}]""");
            }

            if (request.Method == HttpMethod.Get && request.RequestUri!.PathAndQuery.EndsWith("/repos/rdo/demo-app/contents/src/App.tsx?ref=main", StringComparison.Ordinal))
            {
                return JsonResponse("""{"name":"App.tsx","path":"src/App.tsx","type":"file","size":29,"encoding":"base64","content":"ZXhwb3J0IGNvbnN0IGFwcCA9IHRydWU7Cg=="}""");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        }))
        {
            BaseAddress = new Uri("http://forgejo.local")
        };
        var forgejo = new ForgejoRepositoryClient(httpClient, new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LocalGit:ApiBaseUrl"] = "http://forgejo.local/api/v1",
                ["LocalGit:Username"] = "rdo",
                ["LocalGit:Password"] = "demo-token"
            })
            .Build());
        var repository = new RepositoryDto(Guid.NewGuid(), "LocalGit", "demo-app", "http://forgejo.local/rdo/demo-app.git", null, "main", DateTimeOffset.UtcNow, "rdo");

        var tree = await forgejo.GetSourceTreeAsync(repository, "main", "", CancellationToken.None);
        var file = await forgejo.GetSourceFileAsync(repository, "main", "src/App.tsx", CancellationToken.None);

        Assert.NotNull(tree);
        Assert.Equal("main", tree!.Ref);
        Assert.Contains(tree.Entries, entry => entry.Path == "src" && entry.Type == "directory");
        Assert.Contains(tree.Entries, entry => entry.Path == "README.md" && entry.Type == "file" && entry.Size == 42);
        Assert.NotNull(file);
        Assert.Equal("src/App.tsx", file!.Path);
        Assert.Equal("export const app = true;\n", file.Content);
        Assert.False(file.IsBinary);
        Assert.Contains(requests, request => request.Method == HttpMethod.Get && request.Path.EndsWith("/contents/src/App.tsx?ref=main", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Source_clients_preserve_provider_failures_for_endpoint_mapping()
    {
        using var forgejoHttpClient = new HttpClient(new RoutingHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("forgejo is starting")
            }))
        {
            BaseAddress = new Uri("http://forgejo.local")
        };
        var forgejo = new ForgejoRepositoryClient(forgejoHttpClient, new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LocalGit:ApiBaseUrl"] = "http://forgejo.local/api/v1",
                ["LocalGit:Username"] = "rdo",
                ["LocalGit:Password"] = "demo-token"
            })
            .Build());
        var localRepository = new RepositoryDto(Guid.NewGuid(), "LocalGit", "demo-app", "http://forgejo.local/rdo/demo-app.git", null, "main", DateTimeOffset.UtcNow, "rdo");

        var localError = await Assert.ThrowsAsync<RepositorySourceProviderException>(() => forgejo.GetSourceTreeAsync(localRepository, "main", "", CancellationToken.None));

        Assert.Equal("LocalGit", localError.Provider);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, localError.StatusCode);
        Assert.Contains("forgejo is starting", localError.Detail);

        using var githubHttpClient = new HttpClient(new RoutingHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent("upstream unavailable")
            }))
        {
            BaseAddress = new Uri("https://api.github.com")
        };
        var github = new GitHubRepositoryClient(githubHttpClient, new ConfigurationBuilder().Build());
        var githubRepository = new RepositoryDto(Guid.NewGuid(), "GitHub", "demo-app", "https://github.com/carnufex/demo-app.git", "https://github.com/carnufex/demo-app", "main", DateTimeOffset.UtcNow, "carnufex");

        var githubError = await Assert.ThrowsAsync<RepositorySourceProviderException>(() => github.GetSourceFileAsync(githubRepository, "main", "src/App.tsx", "ghs_token", CancellationToken.None));

        Assert.Equal("GitHub", githubError.Provider);
        Assert.Equal(HttpStatusCode.BadGateway, githubError.StatusCode);
        Assert.Contains("upstream unavailable", githubError.Detail);
    }

    [Fact]
    public async Task GitHub_client_reads_source_tree_and_text_files()
    {
        var requests = new List<(HttpMethod Method, string Path, string? Authorization)>();
        using var httpClient = new HttpClient(new RoutingHttpMessageHandler(request =>
        {
            requests.Add((request.Method, request.RequestUri!.PathAndQuery, request.Headers.Authorization?.Parameter));
            if (request.Method == HttpMethod.Get && request.RequestUri!.PathAndQuery.EndsWith("/repos/carnufex/demo-app/contents?ref=main", StringComparison.Ordinal))
            {
                return JsonResponse("""[{"name":"src","path":"src","type":"dir"},{"name":"package.json","path":"package.json","type":"file","size":64}]""");
            }

            if (request.Method == HttpMethod.Get && request.RequestUri!.PathAndQuery.EndsWith("/repos/carnufex/demo-app/contents/package.json?ref=main", StringComparison.Ordinal))
            {
                return JsonResponse("""{"name":"package.json","path":"package.json","type":"file","size":18,"encoding":"base64","content":"eyJuYW1lIjoiZGVtbyJ9Cg=="}""");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        }))
        {
            BaseAddress = new Uri("https://api.github.com")
        };
        var github = new GitHubRepositoryClient(httpClient, new ConfigurationBuilder().Build());
        var repository = new RepositoryDto(Guid.NewGuid(), "GitHub", "demo-app", "https://github.com/carnufex/demo-app.git", "https://github.com/carnufex/demo-app", "main", DateTimeOffset.UtcNow, "carnufex");

        var tree = await github.GetSourceTreeAsync(repository, "main", "", "ghs_token", CancellationToken.None);
        var file = await github.GetSourceFileAsync(repository, "main", "package.json", "ghs_token", CancellationToken.None);

        Assert.NotNull(tree);
        Assert.Equal("main", tree!.Ref);
        Assert.Contains(tree.Entries, entry => entry.Path == "src" && entry.Type == "directory");
        Assert.NotNull(file);
        Assert.Equal("package.json", file!.Path);
        Assert.Equal("{\"name\":\"demo\"}\n", file.Content);
        Assert.All(requests, request => Assert.Equal("ghs_token", request.Authorization));
    }

    [Fact]
    public void Provider_sync_manifest_pushes_heads_and_tags_without_token_literals()
    {
        var run = new PipelineRunDto(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"), null, "ProviderSync", "Queued", "Queued provider sync.", null, DateTimeOffset.UtcNow);
        var source = new RepositoryDto(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), "LocalGit", "demo-app", "http://forgejo/rdo/demo-app.git", null, "main", DateTimeOffset.UtcNow, "rdo");
        var target = new RepositoryDto(Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"), "LocalGit", "demo-copy", "http://forgejo/rdo/demo-copy.git", null, "main", DateTimeOffset.UtcNow, "rdo");

        var manifest = RepositoryProviderSyncJobManifestRenderer.Render(run, source, target, "provider-sync-token", "http://forgejo.local/api/v1", "rdo");
        var job = KubernetesYaml.SingleDocument(manifest, "Job");
        var runner = Assert.Single(job.Spec.Template.Spec.Containers, container => container.Name == "runner");

        Assert.Equal(RepositoryProviderSyncJobManifestRenderer.JobName(run), job.Metadata.Name);
        Assert.Equal(RepositoryImplementationJobManifestRenderer.Namespace, job.Metadata.Namespace);
        Assert.Equal("rosenvall-devops-provider-sync", job.Metadata.Labels["app.kubernetes.io/part-of"]);
        Assert.Equal(run.Id.ToString(), job.Metadata.Labels["rosenvall.devops/pipeline-run"]);
        Assert.False(job.Spec.Template.Spec.AutomountServiceAccountToken);
        Assert.Equal("alpine/git:2.47.2", runner.Image);
        Assert.Contains("ALL", runner.SecurityContext.Capabilities.Drop);
        Assert.Contains("refs/heads/*:refs/heads/*", manifest);
        Assert.Contains("refs/tags/*:refs/tags/*", manifest);
        Assert.Contains("GIT_ASKPASS=\"$workspace/git-askpass.sh\"", manifest);
        Assert.Contains("git_with_credentials \"$ROSENVALL_SOURCE_PROVIDER\" \"$ROSENVALL_SOURCE_GIT_TOKEN\" git clone --mirror \"$ROSENVALL_SOURCE_REPOSITORY_URL\"", manifest);
        Assert.Contains("git_with_credentials \"$ROSENVALL_TARGET_PROVIDER\" \"$ROSENVALL_TARGET_GIT_TOKEN\" git -C \"$workspace/repo.git\" push \"$ROSENVALL_TARGET_REPOSITORY_URL\"", manifest);
        Assert.Contains("provider-sync-token", manifest);
        Assert.Contains("ROSENVALL_SOURCE_REPOSITORY_URL", manifest);
        Assert.Contains("ROSENVALL_TARGET_REPOSITORY_URL", manifest);
        Assert.DoesNotContain("auth_remote", manifest);
        Assert.DoesNotContain("x-access-token:${token}", manifest);
        Assert.DoesNotContain("${ROSENVALL_LOCAL_GIT_USERNAME}:${token}@", manifest);
        Assert.DoesNotContain("ghs_", manifest);
        Assert.DoesNotContain("demo-token", manifest);
        Assert.DoesNotContain("refs/pull", manifest, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("refs/merge", manifest, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Repository_source_feature_normalizes_paths_and_clone_commands()
    {
        Assert.Equal("src/App.tsx", RepositorySourceFeature.NormalizeSourcePath("/src\\App.tsx"));
        Assert.Equal("", RepositorySourceFeature.NormalizeSourcePath("../secret"));
        Assert.Equal("", RepositorySourceFeature.NormalizeSourcePath("src/../secret"));
        Assert.Equal("feature/dark-mode", RepositorySourceFeature.NormalizeSourceRef(" feature/dark-mode ", "main"));
        Assert.Equal("main", RepositorySourceFeature.NormalizeSourceRef("", "main"));
        Assert.Equal("0123456789abcdef0123456789abcdef01234567", RepositorySourceFeature.NormalizeSourceRef("0123456789abcdef0123456789abcdef01234567", "main"));
        Assert.Equal("", RepositorySourceFeature.NormalizeSourceRef("feature/../secret", "main"));
        Assert.Equal("", RepositorySourceFeature.NormalizeSourceRef("/feature", "main"));
        Assert.Equal("", RepositorySourceFeature.NormalizeSourceRef("feature.lock", "main"));
        Assert.Equal("", RepositorySourceFeature.NormalizeSourceRef("feature with space", "main"));
        Assert.Equal("src/App.tsx", RepositorySourceFeature.EscapeSourcePathForUrl("src/App.tsx"));
        Assert.Equal("src/App%20Shell.tsx", RepositorySourceFeature.EscapeSourcePathForUrl("src/App Shell.tsx"));
        Assert.Equal("git clone https://example.test/repo.git", RepositorySourceFeature.BuildCloneCommand("https://example.test/repo.git"));
        Assert.Equal("git clone \"https://example.test/repo with spaces.git\"", RepositorySourceFeature.BuildCloneCommand("https://example.test/repo with spaces.git"));

        var internalLocalGit = RepositorySourceFeature.BuildCloneInfo(Guid.NewGuid(), "LocalGit", "http://forgejo.rosenvall-devops.svc/rdo/demo.git", null);
        Assert.Null(internalLocalGit.HumanCloneUrl);
        Assert.Equal("http://forgejo.rosenvall-devops.svc/rdo/demo.git", internalLocalGit.RunnerCloneUrl);
        Assert.True(internalLocalGit.InternalOnly);
        Assert.Equal("rdo-runner", internalLocalGit.RecommendedMode);
        Assert.Equal("", internalLocalGit.CloneCommand);

        var githubClone = RepositorySourceFeature.BuildCloneInfo(Guid.NewGuid(), "GitHub", "https://github.com/carnufex/demo.git", "https://github.com/carnufex/demo");
        Assert.Equal("https://github.com/carnufex/demo.git", githubClone.HumanCloneUrl);
        Assert.Equal("https://github.com/carnufex/demo.git", githubClone.RunnerCloneUrl);
        Assert.Equal("https://github.com/carnufex/demo", githubClone.WebUrl);
        Assert.False(githubClone.InternalOnly);
        Assert.Equal("human", githubClone.RecommendedMode);
        Assert.Equal("git clone https://github.com/carnufex/demo.git", githubClone.CloneCommand);
    }

    [Theory]
    [InlineData("localgit", "LocalGit")]
    [InlineData("GitHub", "GitHub")]
    [InlineData("gitlab", "")]
    public void Repository_source_feature_normalizes_supported_target_providers(string input, string expected)
    {
        Assert.Equal(expected, RepositorySourceFeature.NormalizeTargetProvider(input));
    }

    [Theory]
    [InlineData("LocalGit", true)]
    [InlineData("GitHub", true)]
    [InlineData("GenericGit", false)]
    public void Repository_source_feature_reports_source_readability_by_provider(string provider, bool expectedReadable)
    {
        Assert.Equal(expectedReadable, RepositorySourceFeature.IsSourceReadableProvider(provider));
        Assert.Equal(expectedReadable ? null : RepositorySourceFeature.UnsupportedSourceProviderMessage, RepositorySourceFeature.SourceUnavailableReason(provider));

        var repository = new RepositorySourceRepositoryDto(Guid.NewGuid(), provider, "demo", "rdo", "main", true, null, RepositorySourceFeature.IsSourceReadableProvider(provider), RepositorySourceFeature.SourceUnavailableReason(provider));

        Assert.Equal(expectedReadable, repository.SourceReadable);
        Assert.Equal(expectedReadable ? null : RepositorySourceFeature.UnsupportedSourceProviderMessage, repository.SourceUnavailableReason);
    }

    [Fact]
    public void Source_and_provider_sync_endpoints_use_repository_source_feature_module()
    {
        var program = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "Rosenvall.DevOps.Api", "Program.cs"));

        Assert.Contains("RepositorySourceFeature.NormalizeSourcePath", program);
        Assert.Contains("RepositorySourceFeature.NormalizeSourceRef", program);
        Assert.Contains("RepositorySourceFeature.EscapeSourcePathForUrl", program);
        Assert.Contains("RepositorySourceFeature.BuildCloneInfo", program);
        Assert.Contains("RepositorySourceFeature.NormalizeTargetProvider", program);
        Assert.Contains("RepositorySourceReadResultAsync", program);
        Assert.DoesNotContain("static string NormalizeApiSourcePath", program);
        Assert.DoesNotContain("static string BuildCloneCommand", program);
    }

    [Fact]
    public void Provider_sync_run_can_be_marked_succeeded_after_job_completion()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest("LocalGit", "demo-app", "http://forgejo/rdo/demo-app.git", "main", null, "rdo", "react-preview"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("Provider sync observer", repository.Id, null, null, null, null, null))!;
        var run = store.RecordPipelineRun(new RecordPipelineRunRequest(repository.Id, board.Id, null, "ProviderSync", "Running", "Provider sync job submitted.", null))!;

        var updated = store.MarkPipelineRunSucceeded(run.Id, "provider-sync-monitor", "Provider sync completed.");

        Assert.NotNull(updated);
        Assert.Equal("Succeeded", updated!.Status);
        Assert.Equal("Provider sync completed.", updated.Message);
        Assert.NotNull(updated.CompletedAt);
        Assert.Contains(store.GetTimeline(board.Id), entry => entry.Kind == "Pipeline" && entry.Message == "Provider sync completed.");
    }

    [Fact]
    public void Provider_sync_target_repository_link_is_pending_until_pipeline_succeeds()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var source = store.CreateRepository(new CreateRepositoryRequest("LocalGit", "demo-app", "http://forgejo/rdo/demo-app.git", "main", null, "rdo", "react-preview"));
        var target = store.CreateRepository(new CreateRepositoryRequest("GitHub", "demo-app-copy", "https://github.com/rdo/demo-app-copy.git", "main", "https://github.com/rdo/demo-app-copy", "rdo", "react-preview"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("Provider sync state", source.Id, null, null, null, null, null))!;
        store.LinkRepositoryToBoard(board.Id, new LinkBoardRepositoryRequest(target.Id, false, source.ImplementationProfile, "PendingSync"));
        var run = store.RecordPipelineRun(new RecordPipelineRunRequest(source.Id, board.Id, null, "ProviderSync", "Running", "Syncing repository.", target.WebUrl, TargetRepositoryId: target.Id))!;

        Assert.Contains(store.GetBoardRepositories(board.Id), link => link.RepositoryId == target.Id && link.SyncState == "PendingSync");

        store.MarkPipelineRunFailed(run.Id, "provider-sync-monitor", "Push failed.");
        Assert.Contains(store.GetBoardRepositories(board.Id), link => link.RepositoryId == target.Id && link.SyncState == "Failed");

        store.MarkPipelineRunSucceeded(run.Id, "provider-sync-monitor", "Provider sync completed.");
        Assert.Contains(store.GetBoardRepositories(board.Id), link => link.RepositoryId == target.Id && link.SyncState == "Ready");
    }

    [Fact]
    public void Provider_sync_monitor_is_registered_and_uses_provider_sync_job_names()
    {
        var program = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "Rosenvall.DevOps.Api", "Program.cs"));

        Assert.Contains("builder.Services.AddHostedService<ProviderSyncRunMonitor>();", program);
        Assert.Contains("public sealed class ProviderSyncRunMonitor", program);
        Assert.Contains("RepositoryProviderSyncJobManifestRenderer.JobName(run)", program);
        Assert.Contains("store.MarkPipelineRunSucceeded(run.Id", program);
    }

    [Fact]
    public async Task GitHub_client_creates_private_repository_with_readme_initialization()
    {
        var requests = new List<(HttpMethod Method, string Path, string Body)>();
        using var httpClient = new HttpClient(new RoutingHttpMessageHandler(request =>
        {
            var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "";
            requests.Add((request.Method, request.RequestUri!.PathAndQuery, body));
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/user/repos")
            {
                return JsonResponse("""{"name":"new-board","full_name":"carnufex/new-board","clone_url":"https://github.com/carnufex/new-board.git","html_url":"https://github.com/carnufex/new-board","default_branch":"main","owner":{"login":"carnufex"}}""");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        }))
        {
            BaseAddress = new Uri("https://api.github.com")
        };
        var github = new GitHubRepositoryClient(httpClient, new ConfigurationBuilder().Build());
        var integration = new GitHubIntegrationDto(Guid.NewGuid(), 123, "carnufex", "User", "Active", 1, "crille", DateTimeOffset.UtcNow);

        var repository = await github.CreateRepositoryAsync(integration, new SyncGitHubRepositoryRequest(Name: "new-board", Private: true, ImplementationProfile: "code-repo"), "token", CancellationToken.None);

        Assert.NotNull(repository);
        Assert.Equal("new-board", repository.Name);
        Assert.Equal("https://github.com/carnufex/new-board.git", repository.RemoteUrl);
        Assert.Contains(requests, request => request.Method == HttpMethod.Post && request.Body.Contains("\"private\":true", StringComparison.OrdinalIgnoreCase) && request.Body.Contains("\"auto_init\":true", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GitHub_client_returns_sanitized_repository_creation_error()
    {
        using var httpClient = new HttpClient(new RoutingHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("""{"message":"Resource not accessible by integration","documentation_url":"https://docs.github.com/rest/repos/repos#create-a-repository-for-the-authenticated-user"}""")
            }))
        {
            BaseAddress = new Uri("https://api.github.com")
        };
        var github = new GitHubRepositoryClient(httpClient, new ConfigurationBuilder().Build());
        var integration = new GitHubIntegrationDto(Guid.NewGuid(), 123, "carnufex", "User", "Active", 1, "crille", DateTimeOffset.UtcNow);

        var result = await github.CreateRepositoryResultAsync(integration, new CreateGitHubRepositoryRequest(123, "new-board"), "user-token", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(result.Repository);
        Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
        Assert.Contains("Resource not accessible by integration", result.Message);
        Assert.DoesNotContain("documentation_url", result.Message);
    }

    [Fact]
    public void GitHub_manifest_requests_repository_administration_and_user_authorization_callback()
    {
        var github = new GitHubRepositoryClient(new HttpClient(new StaticHttpHandler(HttpStatusCode.OK, "{}")), new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PublicBaseUrl"] = "https://devops.rosenvall.se"
            })
            .Build());

        var page = github.RenderManifestStartPage("state-123");

        Assert.Contains("administration", page);
        Assert.Contains("write", page);
        Assert.Contains("/integrations/github/user-authorization/callback", page);
    }

    [Fact]
    public void GitHub_app_secret_renderer_includes_optional_oauth_client_credentials()
    {
        var payload = GitHubAppSecretRenderer.Render(new GitHubManifestAppDto(
            123,
            "rosenvall-devops",
            "Rosenvall DevOps",
            "-----BEGIN PRIVATE KEY-----\nsecret\n-----END PRIVATE KEY-----",
            "Iv1.client",
            "client-secret"));
        using var document = JsonDocument.Parse(payload);
        var data = document.RootElement.GetProperty("data");

        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("Iv1.client")), data.GetProperty("client-id").GetString());
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("client-secret")), data.GetProperty("client-secret").GetString());
        Assert.DoesNotContain(":\"client-secret\"", payload);
        Assert.DoesNotContain("stringData", payload);
    }

    [Fact]
    public void GitHub_user_authorization_secret_payload_uses_data_without_last_applied_annotation()
    {
        var token = new GitHubUserAuthorizationTokenDto("access-token-value", "refresh-token-value", DateTimeOffset.Parse("2026-05-26T10:00:00Z"));

        var payload = GitHubUserAuthorizationTokenStore.RenderSecretPayload("github-user-token-test", token, "rosenvall-devops");
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        Assert.Equal("Secret", root.GetProperty("kind").GetString());
        Assert.Equal("github-user-token-test", root.GetProperty("metadata").GetProperty("name").GetString());
        Assert.Equal("github-user-authorization", root.GetProperty("metadata").GetProperty("labels").GetProperty("rosenvall.devops/runtime-credential").GetString());
        Assert.False(root.GetProperty("metadata").TryGetProperty("annotations", out var _));
        Assert.False(root.TryGetProperty("stringData", out var _));

        var data = root.GetProperty("data");
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("access-token-value")), data.GetProperty("access-token").GetString());
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("refresh-token-value")), data.GetProperty("refresh-token").GetString());
        Assert.DoesNotContain("access-token-value", payload);
        Assert.DoesNotContain("refresh-token-value", payload);
        Assert.DoesNotContain("kubectl.kubernetes.io/last-applied-configuration", payload);
    }

    [Fact]
    public async Task GitHub_client_commits_onboarding_guidance_files_in_one_commit()
    {
        var requests = new List<(HttpMethod Method, string Path, string Body)>();
        using var httpClient = new HttpClient(new RoutingHttpMessageHandler(request =>
        {
            var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "";
            requests.Add((request.Method, request.RequestUri!.PathAndQuery, body));
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/repos/carnufex/new-board/git/ref/heads/main")
            {
                return JsonResponse("""{"object":{"sha":"head-sha"}}""");
            }

            if (request.Method == HttpMethod.Patch && path == "/repos/carnufex/new-board/git/refs/heads/main")
            {
                return JsonResponse("""{"ref":"refs/heads/main"}""");
            }

            return path switch
            {
                "/repos/carnufex/new-board/git/commits/head-sha" => JsonResponse("""{"tree":{"sha":"base-tree-sha"}}"""),
                "/repos/carnufex/new-board/git/blobs" => JsonResponse($$"""{"sha":"blob-{{requests.Count}}"}"""),
                "/repos/carnufex/new-board/git/trees" => JsonResponse("""{"sha":"tree-sha"}"""),
                "/repos/carnufex/new-board/git/commits" => JsonResponse("""{"sha":"commit-sha"}"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") }
            };
        }))
        {
            BaseAddress = new Uri("https://api.github.com")
        };
        var github = new GitHubRepositoryClient(httpClient, new ConfigurationBuilder().Build());
        var repository = new RepositoryDto(Guid.NewGuid(), "GitHub", "new-board", "https://github.com/carnufex/new-board.git", "https://github.com/carnufex/new-board", "main", DateTimeOffset.UtcNow, "carnufex", "code-repo");

        var committed = await github.CommitRepositoryFilesAsync(repository, [
            new GitHubRepositoryOnboardingFileDto("README.md", "# New board"),
            new GitHubRepositoryOnboardingFileDto("AGENTS.md", "# Agent Instructions")
        ], "token", "Initialize repository guidance", CancellationToken.None);

        Assert.True(committed);
        Assert.Contains(requests, request => request.Method == HttpMethod.Post && request.Path == "/repos/carnufex/new-board/git/trees" && request.Body.Contains("\"README.md\"", StringComparison.Ordinal));
        Assert.Contains(requests, request => request.Method == HttpMethod.Post && request.Path == "/repos/carnufex/new-board/git/commits" && request.Body.Contains("Initialize repository guidance", StringComparison.Ordinal));
        Assert.Contains(requests, request => request.Method == HttpMethod.Patch && request.Path == "/repos/carnufex/new-board/git/refs/heads/main" && request.Body.Contains("commit-sha", StringComparison.Ordinal));
    }

    [Fact]
    public void Onboarding_guidance_files_are_limited_to_safe_paths()
    {
        var files = RepositoryOnboardingDrafts.ValidateGuidanceFiles([
            new GitHubRepositoryOnboardingFileDto("README.md", "# ok"),
            new GitHubRepositoryOnboardingFileDto(".codex/skills/repo-workflow/SKILL.md", "# ok"),
            new GitHubRepositoryOnboardingFileDto("src/app.ts", "console.log('no')"),
            new GitHubRepositoryOnboardingFileDto("../secret.txt", "no")
        ]);

        Assert.Contains(files, file => file.Path == "README.md");
        Assert.Contains(files, file => file.Path == ".codex/skills/repo-workflow/SKILL.md");
        Assert.DoesNotContain(files, file => file.Path.Contains("src/", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(files, file => file.Path.Contains("secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Onboarding_fallback_detects_gitops_prompt_and_creates_editable_guidance()
    {
        var draft = RepositoryOnboardingDrafts.Fallback(new GitHubRepositoryOnboardingDraftRequest(
            "homelab-platform",
            "Homelab GitOps repo",
            "A Kubernetes GitOps repository managed by ArgoCD.",
            null));

        Assert.Equal("gitops-homelab", draft.RepositoryProfile.ImplementationProfile);
        Assert.Contains("argocd", draft.RepositoryProfile.EnabledSkills);
        Assert.Contains(draft.Files, file => file.Path == "README.md");
        Assert.Contains(draft.Files, file => file.Path == "AGENTS.md");
        Assert.Contains(draft.Files, file => file.Path.StartsWith(".codex/skills/", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Ai_session_is_stable_per_card_and_codex_resume_is_used_when_session_id_exists()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest("GitHub", "Gatebound", "https://github.com/carnufex/Gatebound.git", "main", "https://github.com/carnufex/Gatebound", "carnufex", "unity"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("Gatebound", repository.Id, null, null, null, null, null))!;
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "Add inventory", "Implement inventory.", "Todo", "Medium", null));
        var first = store.StartAiPlan(item.Id, "codex", "gpt-5.4", "Plan inventory.", "high")!;
        var firstSession = store.GetAiSession(item.Id)!;
        store.SetAiSessionProviderSession(item.Id, "11111111-1111-1111-1111-111111111111");
        var second = store.StartAiPlan(item.Id, "codex", "gpt-5.4", "Revise inventory plan.")!;
        var secondSession = store.GetAiSession(item.Id)!;
        var run = store.StartImplementationRun(item.Id, new StartImplementationRunRequest(second.Id, "crille", repository.Id))!;

        var manifest = store.RenderImplementationRunManifest(run.Id, new ConfigurationBuilder().Build());
        var reopened = fixture.Reopen();

        Assert.Equal(firstSession.Id, secondSession.Id);
        Assert.Equal(second.Id, secondSession.LastRunId);
        Assert.Equal("11111111-1111-1111-1111-111111111111", secondSession.ProviderSessionId);
        Assert.Contains("codex exec resume", manifest);
        Assert.Contains("codex exec resume --ephemeral", manifest);
        Assert.Contains("--sandbox danger-full-access", manifest);
        Assert.Contains("codex-output.log", manifest);
        Assert.Contains("RDO_FAILURE=Codex runner sandbox is unavailable in this Kubernetes runner", manifest);
        Assert.DoesNotContain("--dangerously-bypass-approvals-and-sandbox", manifest);
        Assert.Contains("ROSENVALL_CODEX_SESSION_ID", manifest);
        Assert.Contains("CODEX_REASONING_EFFORT", manifest);
        Assert.Contains("model_reasoning_effort=$CODEX_REASONING_EFFORT", manifest);
        Assert.Equal(2, reopened.GetAiRuns(item.Id).Count);
        Assert.Equal(secondSession.Id, reopened.GetAiSession(item.Id)!.Id);
        Assert.Equal("high", reopened.GetAiRuns(item.Id).First().ReasoningEffort);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void GitHub_app_integration_callback_is_persisted()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var integration = fixture.Store.CreateGitHubIntegration(new GitHubIntegrationCallbackRequest(12345, "carnufex", "User", "authentik|crille", 7));

        var reopened = fixture.Reopen();

        Assert.Equal(12345, integration.InstallationId);
        Assert.Contains(reopened.GetGitHubIntegrations(), entry => entry.InstallationId == 12345 && entry.AccountLogin == "carnufex" && entry.RepositoriesCount == 7);
    }

    [Fact]
    public void GitHub_app_integration_sync_is_idempotent()
    {
        using var fixture = DevOpsStoreFixture.Create();
        fixture.Store.UpsertGitHubIntegrations([
            new GitHubIntegrationCallbackRequest(12345, "carnufex", "User", "github-app", 7),
            new GitHubIntegrationCallbackRequest(67890, "rosenvall-corp", "Organization", "github-app", 2)
        ]);

        fixture.Store.UpsertGitHubIntegrations([
            new GitHubIntegrationCallbackRequest(12345, "carnufex", "User", "github-app", 9)
        ]);

        var integrations = fixture.Store.GetGitHubIntegrations();

        Assert.Equal(2, integrations.Count);
        Assert.Contains(integrations, entry => entry.InstallationId == 12345 && entry.RepositoriesCount == 9);
        Assert.Contains(integrations, entry => entry.InstallationId == 67890 && entry.RepositoriesCount == 2);
    }

    [Fact]
    public void GitHub_integrations_show_only_actor_owned_or_matching_authorized_personal_accounts()
    {
        using var fixture = DevOpsStoreFixture.Create();
        fixture.Store.CreateGitHubIntegration(new GitHubIntegrationCallbackRequest(111, "crille", "User", "authentik|crille", 3));
        fixture.Store.CreateGitHubIntegration(new GitHubIntegrationCallbackRequest(222, "guest", "User", "authentik|guest", 4));
        fixture.Store.CreateGitHubIntegration(new GitHubIntegrationCallbackRequest(223, "authorized-guest", "User", "github-app", 2));
        fixture.Store.CreateGitHubIntegration(new GitHubIntegrationCallbackRequest(333, "shared", "Organization", "github-app", 5));
        fixture.Store.UpsertGitHubUserAuthorization(new GitHubUserAuthorizationDto(Guid.NewGuid(), "authentik|crille", 223, "authorized-guest", "authorized-guest", "Connected", "github-user-token-test", DateTimeOffset.UtcNow));
        fixture.Store.UpsertGitHubUserAuthorization(new GitHubUserAuthorizationDto(Guid.NewGuid(), "authentik|guest", 111, "guest", "guest", "Connected", "github-user-token-guest", DateTimeOffset.UtcNow));

        var crilleIntegrations = fixture.Store.GetGitHubIntegrations("authentik|crille");

        Assert.Contains(crilleIntegrations, entry => entry.InstallationId == 111);
        Assert.DoesNotContain(crilleIntegrations, entry => entry.InstallationId == 222);
        Assert.Contains(crilleIntegrations, entry => entry.InstallationId == 223 && entry.HasUserAuthorization);
        Assert.DoesNotContain(crilleIntegrations, entry => entry.InstallationId == 333);
        Assert.True(fixture.Store.CanUseGitHubInstallation(111, "authentik|crille"));
        Assert.False(fixture.Store.CanUseGitHubInstallation(222, "authentik|crille"));
        Assert.True(fixture.Store.CanUseGitHubInstallation(223, "authentik|crille"));
        Assert.False(fixture.Store.CanUseGitHubInstallation(111, "authentik|guest"));
        Assert.False(fixture.Store.CanUseGitHubInstallation(333, "authentik|crille"));
        Assert.False(fixture.Store.CanCreateGitHubRepository(222, "authentik|crille"));
        Assert.NotEqual(222, fixture.Store.GetDefaultGitHubInstallationId("authentik|crille"));
    }

    [Fact]
    public void GitHub_app_integration_is_selected_for_matching_repository_owner()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var repository = fixture.Store.CreateRepository(new CreateRepositoryRequest("GitHub", "Gatebound", "https://github.com/carnufex/Gatebound.git", "main", "https://github.com/carnufex/Gatebound", "carnufex", "unity"));
        fixture.Store.CreateGitHubIntegration(new GitHubIntegrationCallbackRequest(111, "other-org", "Organization", "authentik|crille", 3));
        var integration = fixture.Store.CreateGitHubIntegration(new GitHubIntegrationCallbackRequest(222, "carnufex", "User", "authentik|crille", 7));

        Assert.Equal(222, fixture.Store.GetDefaultGitHubInstallationId());
        Assert.Equal(integration.Id, fixture.Store.GetGitHubIntegrationForRepository(repository)!.Id);
    }

    [Fact]
    public void GitHub_app_integration_for_repository_does_not_fall_back_to_default_installation()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var repository = fixture.Store.CreateRepository(new CreateRepositoryRequest("GitHub", "Gatebound", "https://github.com/carnufex/Gatebound.git", "main", "https://github.com/carnufex/Gatebound", "carnufex", "unity"));
        fixture.Store.CreateGitHubIntegration(new GitHubIntegrationCallbackRequest(111, "other-org", "Organization", "authentik|crille", 3));

        Assert.Equal(111, fixture.Store.GetDefaultGitHubInstallationId());
        Assert.Null(fixture.Store.GetGitHubIntegrationForRepository(repository));
    }

    [Fact]
    public void Source_and_provider_sync_resolve_github_tokens_from_repository_context()
    {
        var program = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "Rosenvall.DevOps.Api", "Program.cs"));

        Assert.Contains("ResolveGitHubRepositoryReadTokenAsync(store, github, repository, AuthenticatedSubjectOrNull(user)", program);
        Assert.Contains("ResolveGitHubRepositoryReadTokenAsync(store, github, source, AuthenticatedSubjectOrNull(user)", program);
        Assert.Contains("GetGitHubIntegrationForRepository(repository)", program);
        Assert.Contains("!store.CanUseGitHubInstallation(integration.InstallationId, actorSubject)", program);
        Assert.DoesNotContain("ResolveGitHubRepositoryReadTokenAsync(DevOpsStore store, GitHubRepositoryClient github, string? actorSubject", program);
        Assert.DoesNotContain("store.GetDefaultGitHubInstallationId(actorSubject) is { } installationId", program);
    }

    [Fact]
    public void GitOps_application_status_parser_handles_synced_healthy_and_degraded_apps()
    {
        using var document = JsonDocument.Parse("""
        {
          "items": [
            {
              "metadata": {
                "name": "grafana",
                "namespace": "argocd",
                "creationTimestamp": "2026-05-24T08:00:00Z"
              },
              "status": {
                "sync": { "status": "Synced", "revision": "abc123" },
                "health": { "status": "Healthy", "message": "all resources healthy" },
                "summary": { "externalURLs": ["https://grafana.rosenvall.se", "", "ftp://grafana.invalid", "https://grafana.rosenvall.se"] }
              }
            },
            {
              "metadata": {
                "name": "prometheus",
                "namespace": "argocd"
              },
              "status": {
                "sync": { "status": "OutOfSync", "revision": "def456" },
                "health": { "status": "Degraded", "message": "deployment unavailable" }
              }
            }
          ]
        }
        """);

        var statuses = GitOpsStatusReader.ParseApplicationsJson(document.RootElement, "https://argocd.rosenvall.se");

        Assert.Collection(statuses,
            grafana =>
            {
                Assert.Equal("grafana", grafana.Name);
                Assert.Equal("Synced", grafana.SyncStatus);
                Assert.Equal("Healthy", grafana.HealthStatus);
                Assert.Equal("abc123", grafana.Revision);
                Assert.Equal("https://argocd.rosenvall.se/applications/grafana", grafana.Url);
                Assert.Equal(["https://grafana.rosenvall.se"], grafana.ApplicationUrls);
            },
            prometheus =>
            {
                Assert.Equal("prometheus", prometheus.Name);
                Assert.Equal("OutOfSync", prometheus.SyncStatus);
                Assert.Equal("Degraded", prometheus.HealthStatus);
                Assert.Contains("deployment unavailable", prometheus.Message);
            });
    }

    [Theory]
    [InlineData("the server doesn't have a resource type \"applications\"", "ArgoCD Application CRD was not found")]
    [InlineData("Error from server (Forbidden): applications.argoproj.io is forbidden", "service account lacks access")]
    [InlineData("Error from server (NotFound): namespaces \"argocd\" not found", "namespace is missing")]
    public void GitOps_application_status_failures_are_actionable(string kubectlError, string expectedMessage)
    {
        var result = GitOpsStatusReader.FromKubectlFailure(kubectlError);

        Assert.Contains(expectedMessage, result.Message, StringComparison.OrdinalIgnoreCase);
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
    public void Kubernetes_kubeconfig_resolver_uses_in_cluster_auth_for_empty_path()
    {
        var result = KubernetesKubeconfigResolver.Resolve("", ["C:\\does-not-need-to-exist"]);

        Assert.True(result.UseInClusterAuth);
        Assert.Null(result.Path);
        Assert.Null(result.MissingMessage);
    }

    [Fact]
    public async Task Pipeline_orchestration_uses_in_cluster_auth_for_empty_kubeconfig_path()
    {
        var fakeKubectl = CreateFakeKubectl();
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Pipelines:KubectlPath"] = fakeKubectl.Path,
                    ["Pipelines:KubeconfigPath"] = ""
                })
                .Build();
            var jobs = new PipelineJobOrchestrator(configuration, NullLogger<PipelineJobOrchestrator>.Instance);

            var result = await jobs.ApplyAsync("apiVersion: v1\nkind: Namespace\nmetadata:\n  name: in-cluster\n", CancellationToken.None);

            Assert.True(result.Succeeded);
            var arguments = File.ReadAllText(fakeKubectl.ArgumentsPath);
            Assert.Contains("apply -f -", arguments);
            Assert.DoesNotContain("--kubeconfig", arguments);
        }
        finally
        {
            fakeKubectl.Directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Kubernetes_kubeconfig_resolver_finds_sibling_homelab_kubeconfig_for_default_path()
    {
        var temp = Directory.CreateTempSubdirectory("rdo-kubeconfig-");
        try
        {
            var devops = Directory.CreateDirectory(Path.Combine(temp.FullName, "rosenvall-devops"));
            var homelabOutput = Directory.CreateDirectory(Path.Combine(temp.FullName, "Rosenvalls-Homelab", "tofu", "output"));
            var kubeconfig = Path.Combine(homelabOutput.FullName, "kubeconfig");
            File.WriteAllText(kubeconfig, "apiVersion: v1");

            var result = KubernetesKubeconfigResolver.Resolve("tofu/output/kubeconfig", [devops.FullName]);

            Assert.False(result.UseInClusterAuth);
            Assert.Equal(kubeconfig, result.Path);
            Assert.Null(result.MissingMessage);
            Assert.Contains(kubeconfig, result.CheckedPaths);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [Fact]
    public void Kubernetes_kubeconfig_resolver_reports_checked_paths_when_local_kubeconfig_is_missing()
    {
        var temp = Directory.CreateTempSubdirectory("rdo-missing-kubeconfig-");
        try
        {
            var devops = Directory.CreateDirectory(Path.Combine(temp.FullName, "rosenvall-devops"));

            var result = KubernetesKubeconfigResolver.Resolve("tofu/output/kubeconfig", [devops.FullName]);

            Assert.False(result.UseInClusterAuth);
            Assert.Null(result.Path);
            Assert.NotNull(result.MissingMessage);
            Assert.Contains("Configured kubeconfig was not found", result.MissingMessage);
            Assert.Contains("tofu", result.MissingMessage);
            Assert.Contains("Rosenvalls-Homelab", result.MissingMessage);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Preview_source_provider_reports_preview_source_submission_failure_for_kubectl_errors()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var board = fixture.Store.GetWorkspaces().SelectMany(workspace => fixture.Store.GetBoards(workspace.Id)).First();
        var item = fixture.Store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "klocka", "gör en klocksida", "Todo", "Medium", null));
        var run = fixture.Store.StartAiPlan(item.Id, "codex", "gpt-5.4", "Build the preview.")!;
        run.Approve("crille");
        var context = fixture.Store.GetWorkItemDetail(item.Id)!;
        var missingKubeconfig = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "kubeconfig");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Pipelines:KubectlPath"] = "kubectl",
                ["Pipelines:KubeconfigPath"] = missingKubeconfig
            })
            .Build();
        var jobs = new PipelineJobOrchestrator(configuration, NullLogger<PipelineJobOrchestrator>.Instance);
        var provider = new KubernetesPreviewSourceProvider(jobs, configuration, NullLogger<KubernetesPreviewSourceProvider>.Instance);

        var error = await Assert.ThrowsAsync<AiPlanProviderUnavailableException>(() =>
            provider.GenerateSourceAsync("gpt-5.4", "high", run, context, null, CancellationToken.None));

        Assert.Contains("Preview source job submission failed", error.Message);
        Assert.DoesNotContain("Pipeline job submission failed", error.Message);
    }

    [Fact]
    public async Task Preview_source_provider_reuses_existing_result_configmap_without_recreating_job()
    {
        var fakeKubectl = CreatePreviewSourceResultKubectl();
        try
        {
            using var fixture = DevOpsStoreFixture.Create();
            var board = fixture.Store.GetWorkspaces().SelectMany(workspace => fixture.Store.GetBoards(workspace.Id)).First();
            var item = fixture.Store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "klocka", "build a clock page", "Todo", "Medium", null));
            var run = fixture.Store.StartAiPlan(item.Id, "codex", "gpt-5.4", "Build the preview.")!;
            run.Approve("crille");
            var context = fixture.Store.GetWorkItemDetail(item.Id)!;
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Pipelines:KubectlPath"] = fakeKubectl.Path,
                    ["Pipelines:KubeconfigPath"] = ""
                })
                .Build();
            var jobs = new PipelineJobOrchestrator(configuration, NullLogger<PipelineJobOrchestrator>.Instance);
            var provider = new KubernetesPreviewSourceProvider(jobs, configuration, NullLogger<KubernetesPreviewSourceProvider>.Instance);

            var files = await provider.GenerateSourceAsync("gpt-5.4", "high", run, context, null, CancellationToken.None);

            var commands = File.ReadAllText(fakeKubectl.ArgumentsPath);
            Assert.Contains(files, file => file.Path == "src/App.tsx" && file.Content.Contains("Klocka", StringComparison.Ordinal));
            Assert.Contains("get configmap", commands);
            Assert.DoesNotContain("delete -f", commands);
            Assert.DoesNotContain("apply -f", commands);
        }
        finally
        {
            fakeKubectl.Directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Preview_source_provider_adopts_result_configmap_that_appears_at_timeout()
    {
        var fakeKubectl = CreateDelayedPreviewSourceResultKubectl();
        try
        {
            using var fixture = DevOpsStoreFixture.Create();
            var board = fixture.Store.GetWorkspaces().SelectMany(workspace => fixture.Store.GetBoards(workspace.Id)).First();
            var item = fixture.Store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "klocka", "build a clock page", "Todo", "Medium", null));
            var run = fixture.Store.StartAiPlan(item.Id, "codex", "gpt-5.4", "Build the preview.")!;
            run.Approve("crille");
            var context = fixture.Store.GetWorkItemDetail(item.Id)!;
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Pipelines:KubectlPath"] = fakeKubectl.Path,
                    ["Pipelines:KubeconfigPath"] = "",
                    ["Ai:Codex:PreviewSourceJobTimeoutSeconds"] = "0"
                })
                .Build();
            var jobs = new PipelineJobOrchestrator(configuration, NullLogger<PipelineJobOrchestrator>.Instance);
            var provider = new KubernetesPreviewSourceProvider(jobs, configuration, NullLogger<KubernetesPreviewSourceProvider>.Instance);

            var files = await provider.GenerateSourceAsync("gpt-5.4", "high", run, context, null, CancellationToken.None);

            var commands = File.ReadAllText(fakeKubectl.ArgumentsPath);
            Assert.Contains(files, file => file.Path == "src/App.tsx" && file.Content.Contains("Timeout adopted", StringComparison.Ordinal));
            Assert.Contains("apply -f", commands);
            Assert.Contains("get configmap", commands);
        }
        finally
        {
            fakeKubectl.Directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Local_start_script_sets_preview_source_mode_from_kubeconfig_availability()
    {
        var script = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "scripts", "start-local-demo.ps1"));

        Assert.Contains("local-demo.pids.json", script);
        Assert.Contains("apiPid = $api.Id", script);
        Assert.Contains("frontendPid = $frontend.Id", script);
        Assert.Contains("forgejoPid", script);
        Assert.Contains("Ai__Codex__PreviewSourceMode", script);
        Assert.Contains("Ai__Codex__PreviewSourceJobTimeoutSeconds", script);
        Assert.Contains("Ai__Codex__KubernetesRunnerImage", script);
        Assert.Contains("kubernetes-job", script);
        Assert.Contains("in-process", script);
        Assert.Contains("Preview__KubeconfigPath", script);
        Assert.Contains("Pipelines__KubeconfigPath", script);
        Assert.Contains("svc/rosenvall-devops-forgejo", script);
        Assert.Contains("LocalGit__ApiBaseUrl", script);
        Assert.Contains("LocalGit__RunnerApiBaseUrl", script);
        Assert.Contains("LocalGit__UnavailableReason", script);
    }

    [Fact]
    public void Local_stop_script_stops_recorded_and_port_forward_processes()
    {
        var script = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "scripts", "stop-local-demo.ps1"));

        Assert.Contains("local-demo.pids.json", script);
        Assert.Contains("Stop-RecordedProcess \"API\" $recorded.apiPid", script);
        Assert.Contains("Stop-RecordedProcess \"frontend\" $recorded.frontendPid", script);
        Assert.Contains("Stop-RecordedProcess \"Forgejo port-forward\" $recorded.forgejoPid", script);
        Assert.Contains("rosenvall-devops-api", script);
        Assert.Contains("rosenvall-devops-forgejo", script);
        Assert.Contains("port-forward", script);
        Assert.Contains("Remove-Item -LiteralPath $pidFile", script);
        Assert.Contains("Some Rosenvall DevOps local demo processes are still running", script);
        Assert.Contains("No matching Rosenvall DevOps local demo processes remain", script);
    }

    [Fact]
    public void Api_dockerfile_does_not_install_preview_base_dependencies_for_preview_promotion()
    {
        var dockerfile = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "Rosenvall.DevOps.Api", "Dockerfile"));

        Assert.DoesNotContain("preview-base/package.json", dockerfile);
        Assert.DoesNotContain("/opt/rosenvall-preview/package.json", dockerfile);
        Assert.DoesNotContain("cd /opt/rosenvall-preview", dockerfile);
        Assert.DoesNotContain("npm install --no-audit --no-fund", dockerfile);
    }

    [Fact]
    public void Api_dockerfile_installs_jq_for_runner_json_parsing()
    {
        var dockerfile = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "Rosenvall.DevOps.Api", "Dockerfile"));

        Assert.Contains("ca-certificates curl git jq nodejs npm", dockerfile);
    }

    [Fact]
    public void Ci_runs_frontend_tests_before_frontend_build()
    {
        var ci = File.ReadAllText(Path.Combine(FindRepositoryRoot(), ".github", "workflows", "ci.yml"));
        var testIndex = ci.IndexOf("run: npm test -- --runInBand", StringComparison.Ordinal);
        var buildIndex = ci.IndexOf("run: npm run build", StringComparison.Ordinal);

        Assert.True(testIndex >= 0, "CI should run the frontend unit tests.");
        Assert.True(buildIndex >= 0, "CI should still run the frontend production build.");
        Assert.True(testIndex < buildIndex, "Frontend tests should run before the production build.");
    }

    [Fact]
    public void Image_publish_workflow_waits_for_successful_ci()
    {
        var workflow = File.ReadAllText(Path.Combine(FindRepositoryRoot(), ".github", "workflows", "publish-images.yml"));

        Assert.Contains("workflow_run:", workflow);
        Assert.Contains("- CI", workflow);
        Assert.Contains("github.event.workflow_run.conclusion == 'success'", workflow);
        Assert.Contains("COMMIT_SHA: ${{ github.event.workflow_run.head_sha || github.sha }}", workflow);
        Assert.Contains("ref: ${{ env.COMMIT_SHA }}", workflow);
        Assert.Contains("-api:${{ env.COMMIT_SHA }}", workflow);
    }

    [Fact]
    public void Browser_security_headers_are_declared_for_api_and_frontend()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Authority"] = "https://authentik.rosenvall.se/application/o/rosenvall-devops/",
                ["Frontend:AllowedOrigins:0"] = "https://devops.rosenvall.se"
            })
            .Build();
        var headers = new HeaderDictionary();

        BrowserSecurityHeaders.Apply(headers, configuration, includeHsts: true);

        var csp = Assert.Single(headers[BrowserSecurityHeaders.ContentSecurityPolicyHeader]);
        Assert.Contains("default-src 'self'", csp);
        Assert.Contains("script-src 'self'", csp);
        Assert.DoesNotContain("script-src 'self' 'unsafe-inline'", csp);
        Assert.Contains("https://authentik.rosenvall.se", csp);
        Assert.Contains("https://api.github.com", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Equal("nosniff", headers[BrowserSecurityHeaders.ContentTypeOptionsHeader]);
        Assert.Equal("DENY", headers[BrowserSecurityHeaders.FrameOptionsHeader]);
        Assert.Equal("strict-origin-when-cross-origin", headers[BrowserSecurityHeaders.ReferrerPolicyHeader]);
        Assert.Equal("max-age=31536000; includeSubDomains", headers[BrowserSecurityHeaders.StrictTransportSecurityHeader]);

        var nginx = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "frontend", "nginx.conf"));
        Assert.Contains("add_header Content-Security-Policy", nginx);
        Assert.Contains("script-src 'self'", nginx);
        Assert.DoesNotContain("script-src 'self' 'unsafe-inline'", nginx);
        Assert.Contains("add_header X-Content-Type-Options \"nosniff\" always;", nginx);
        Assert.Contains("add_header X-Frame-Options \"DENY\" always;", nginx);
        Assert.Contains("add_header Strict-Transport-Security", nginx);
    }

    [Fact]
    public void Authentication_mode_fails_closed_outside_development()
    {
        var missingRequired = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Mode"] = "Required"
            })
            .Build();
        var disabledProduction = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Mode"] = "DisabledForLocalDevelopment"
            })
            .Build();
        var configured = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Mode"] = "Required",
                ["Authentication:Authority"] = "https://authentik.rosenvall.se/application/o/rosenvall-devops/",
                ["Authentication:Audience"] = "rosenvall-devops"
            })
            .Build();

        Assert.Throws<InvalidOperationException>(() => AuthenticationMode.Resolve(missingRequired, isDevelopment: false));
        Assert.Throws<InvalidOperationException>(() => AuthenticationMode.Resolve(disabledProduction, isDevelopment: false));

        var resolved = AuthenticationMode.Resolve(configured, isDevelopment: false);
        Assert.Equal(AuthenticationMode.Required, resolved.Mode);
        Assert.True(resolved.Enabled);
        Assert.Equal("https://authentik.rosenvall.se/application/o/rosenvall-devops/", resolved.Authority);
        Assert.Equal("rosenvall-devops", resolved.Audience);
    }

    [Fact]
    public void Authentication_mode_allows_local_development_only_when_explicitly_disabled_or_unconfigured()
    {
        var unconfigured = new ConfigurationBuilder().Build();
        var disabled = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Mode"] = "DisabledForLocalDevelopment",
                ["Authentication:Authority"] = "https://authentik.rosenvall.se/application/o/rosenvall-devops/",
                ["Authentication:Audience"] = "rosenvall-devops"
            })
            .Build();
        var inferredRequired = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Authority"] = "https://authentik.rosenvall.se/application/o/rosenvall-devops/",
                ["Authentication:Audience"] = "rosenvall-devops"
            })
            .Build();

        var local = AuthenticationMode.Resolve(unconfigured, isDevelopment: true);
        Assert.Equal(AuthenticationMode.DisabledForLocalDevelopment, local.Mode);
        Assert.False(local.Enabled);

        var explicitlyDisabled = AuthenticationMode.Resolve(disabled, isDevelopment: true);
        Assert.Equal(AuthenticationMode.DisabledForLocalDevelopment, explicitlyDisabled.Mode);
        Assert.False(explicitlyDisabled.Enabled);

        var authEnabled = AuthenticationMode.Resolve(inferredRequired, isDevelopment: true);
        Assert.Equal(AuthenticationMode.Required, authEnabled.Mode);
        Assert.True(authEnabled.Enabled);
    }

    [Fact]
    public void Realtime_mode_fails_closed_for_authenticated_global_broadcasts()
    {
        var unsafeRealtime = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Realtime:Enabled"] = "true"
            })
            .Build();
        var explicitlyAcknowledged = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Realtime:Enabled"] = "true",
                ["Realtime:AllowUnsafeBroadcasts"] = "true"
            })
            .Build();

        var failure = Assert.Throws<InvalidOperationException>(() => RealtimeMode.Resolve(unsafeRealtime, authenticationEnabled: true));
        Assert.Contains("Realtime:AllowUnsafeBroadcasts", failure.Message);

        var disabled = RealtimeMode.Resolve(unsafeRealtime, authenticationEnabled: false);
        Assert.True(disabled.Enabled);

        var acknowledged = RealtimeMode.Resolve(explicitlyAcknowledged, authenticationEnabled: true);
        Assert.True(acknowledged.Enabled);
        Assert.True(acknowledged.UnsafeBroadcastsAllowed);
    }

    [Fact]
    public void Homelab_rosenvall_devops_config_declares_in_cluster_kubeconfig_paths_when_available()
    {
        var root = new DirectoryInfo(FindRepositoryRoot());
        var configMapPath = Path.Combine(
            root.Parent?.FullName ?? "",
            "Rosenvalls-Homelab",
            "kubernetes",
            "applications",
            "rosenvall-devops",
            "configmap.yaml");
        if (!File.Exists(configMapPath))
        {
            return;
        }

        var configMap = File.ReadAllText(configMapPath);

        Assert.Contains("Authentication__Mode: Required", configMap);
        Assert.Contains("Preview__KubeconfigPath: \"\"", configMap);
        Assert.Contains("Pipelines__KubeconfigPath: \"\"", configMap);
        Assert.Contains("Ai__Codex__KubernetesSandboxMode: danger-full-access", configMap);
        Assert.Contains("Ai__Codex__KubernetesRunnerImage: ghcr.io/carnufex/rosenvall-devops-api@sha256:", configMap);
        Assert.Contains("Ai__Codex__PreviewSourceApiMemoryMinHeadroomBytes: \"268435456\"", configMap);
    }

    [Fact]
    public void Homelab_api_deployment_has_preview_generation_memory_buffer_when_available()
    {
        var root = new DirectoryInfo(FindRepositoryRoot());
        var deploymentPath = Path.Combine(
            root.Parent?.FullName ?? "",
            "Rosenvalls-Homelab",
            "kubernetes",
            "applications",
            "rosenvall-devops",
            "api-deployment.yaml");
        if (!File.Exists(deploymentPath))
        {
            return;
        }

        var deployment = File.ReadAllText(deploymentPath);

        Assert.Contains("memory: 1Gi", deployment);
        Assert.Contains("memory: 3Gi", deployment);
    }

    [Fact]
    public void Homelab_api_deployment_and_image_run_as_non_root_when_available()
    {
        var root = new DirectoryInfo(FindRepositoryRoot());
        var deploymentPath = Path.Combine(
            root.Parent?.FullName ?? "",
            "Rosenvalls-Homelab",
            "kubernetes",
            "applications",
            "rosenvall-devops",
            "api-deployment.yaml");
        if (!File.Exists(deploymentPath))
        {
            return;
        }

        var deployment = File.ReadAllText(deploymentPath);
        var dockerfile = File.ReadAllText(Path.Combine(root.FullName, "src", "Rosenvall.DevOps.Api", "Dockerfile"));

        Assert.Contains("runAsNonRoot: true", deployment);
        Assert.Contains("runAsUser: 1000", deployment);
        Assert.Contains("runAsGroup: 1000", deployment);
        Assert.Contains("fsGroup: 1000", deployment);
        Assert.Contains("readOnlyRootFilesystem: true", deployment);
        Assert.Contains("mountPath: /tmp", deployment);
        Assert.Contains("name: tmp", deployment);
        Assert.Contains("USER 1000:1000", dockerfile);
    }

    [Fact]
    public void Homelab_frontend_deployment_and_image_run_unprivileged_without_extra_capabilities_when_available()
    {
        var root = new DirectoryInfo(FindRepositoryRoot());
        var deploymentPath = Path.Combine(
            root.Parent?.FullName ?? "",
            "Rosenvalls-Homelab",
            "kubernetes",
            "applications",
            "rosenvall-devops",
            "frontend-deployment.yaml");
        if (!File.Exists(deploymentPath))
        {
            return;
        }

        var deployment = File.ReadAllText(deploymentPath);
        var dockerfile = File.ReadAllText(Path.Combine(root.FullName, "frontend", "Dockerfile"));

        Assert.Contains("nginxinc/nginx-unprivileged", dockerfile);
        Assert.Contains("runAsNonRoot: true", deployment);
        Assert.Contains("runAsUser: 101", deployment);
        Assert.Contains("runAsGroup: 101", deployment);
        Assert.Contains("readOnlyRootFilesystem: true", deployment);
        Assert.Contains("mountPath: /tmp", deployment);
        Assert.Contains("name: nginx-cache", deployment);
        Assert.DoesNotContain("add:", deployment);
        Assert.DoesNotContain("CHOWN", deployment);
        Assert.DoesNotContain("SETGID", deployment);
        Assert.DoesNotContain("SETUID", deployment);
    }

    [Fact]
    public void Homelab_rosenvall_devops_declares_internal_forgejo_when_available()
    {
        var root = new DirectoryInfo(FindRepositoryRoot());
        var appPath = Path.Combine(root.Parent?.FullName ?? "", "Rosenvalls-Homelab", "kubernetes", "applications", "rosenvall-devops");
        if (!Directory.Exists(appPath))
        {
            return;
        }

        var kustomization = File.ReadAllText(Path.Combine(appPath, "kustomization.yaml"));
        var configMap = File.ReadAllText(Path.Combine(appPath, "configmap.yaml"));
        var deployment = File.ReadAllText(Path.Combine(appPath, "api-deployment.yaml"));
        var forgejoDeployment = File.ReadAllText(Path.Combine(appPath, "forgejo-deployment.yaml"));
        var forgejoPvc = File.ReadAllText(Path.Combine(appPath, "forgejo-pvc.yaml"));
        var forgejoService = File.ReadAllText(Path.Combine(appPath, "forgejo-service.yaml"));
        var forgejoCredentials = File.ReadAllText(Path.Combine(appPath, "forgejo-credentials.yaml"));

        Assert.Contains("forgejo-deployment.yaml", kustomization);
        Assert.Contains("forgejo-service.yaml", kustomization);
        Assert.Contains("forgejo-credentials.yaml", kustomization);
        Assert.Contains("LocalGit__Enabled: \"true\"", configMap);
        Assert.Contains("LocalGit__ApiBaseUrl: http://rosenvall-devops-forgejo.rosenvall-devops.svc.cluster.local:3000/api/v1", configMap);
        Assert.Contains("LocalGit__RunnerApiBaseUrl: http://rosenvall-devops-forgejo.rosenvall-devops.svc.cluster.local:3000/api/v1", configMap);
        Assert.Contains("LocalGit__Password", deployment);
        Assert.Contains("name: rosenvall-devops-forgejo-admin", deployment);
        Assert.Contains("name: rosenvall-devops-forgejo", forgejoDeployment);
        Assert.Contains("codeberg.org/forgejo/forgejo", forgejoDeployment);
        Assert.Contains("FORGEJO__DEFAULT__APP_NAME", forgejoDeployment);
        Assert.Contains("DISABLE_REGISTRATION", forgejoDeployment);
        Assert.Contains("DISABLE_SSH", forgejoDeployment);
        Assert.Contains("storageClassName: longhorn", forgejoPvc);
        Assert.Contains("type: ClusterIP", forgejoService);
        Assert.Contains("name: rosenvall-devops-forgejo-admin-password", forgejoCredentials);
    }

    [Fact]
    public void Homelab_rbac_allows_runtime_to_delete_legacy_pipeline_token_secrets_when_available()
    {
        var root = new DirectoryInfo(FindRepositoryRoot());
        var rbacPath = Path.Combine(
            root.Parent?.FullName ?? "",
            "Rosenvalls-Homelab",
            "kubernetes",
            "applications",
            "rosenvall-devops",
            "preview-rbac.yaml");
        if (!File.Exists(rbacPath))
        {
            return;
        }

        var rbac = File.ReadAllText(rbacPath);

        Assert.Contains("name: rosenvall-devops-pipeline-token-cleanup", rbac);
        Assert.Contains("namespace: rosenvall-devops-pipelines", rbac);
        Assert.Contains("resources: [\"secrets\"]", rbac);
        Assert.Contains("verbs: [\"delete\"]", rbac);
        Assert.Contains("name: rosenvall-devops-runtime", rbac);
        Assert.Contains("namespace: rosenvall-devops", rbac);
    }

    [Fact]
    public void Homelab_rbac_allows_runtime_to_read_events_when_available()
    {
        var root = new DirectoryInfo(FindRepositoryRoot());
        var rbacPath = Path.Combine(
            root.Parent?.FullName ?? "",
            "Rosenvalls-Homelab",
            "kubernetes",
            "applications",
            "rosenvall-devops",
            "preview-rbac.yaml");
        if (!File.Exists(rbacPath))
        {
            return;
        }

        var rbac = File.ReadAllText(rbacPath);

        Assert.Contains("resources: [\"events\"]", rbac);
        Assert.Contains("verbs: [\"get\", \"list\", \"watch\"]", rbac);
    }

    [Fact]
    public void Homelab_splits_api_control_plane_rbac_from_runtime_preview_source_publisher_when_available()
    {
        var root = new DirectoryInfo(FindRepositoryRoot());
        var homelabRoot = Path.Combine(
            root.Parent?.FullName ?? "",
            "Rosenvalls-Homelab",
            "kubernetes",
            "applications",
            "rosenvall-devops");
        var serviceAccountPath = Path.Combine(homelabRoot, "serviceaccount.yaml");
        var deploymentPath = Path.Combine(homelabRoot, "api-deployment.yaml");
        var rbacPath = Path.Combine(homelabRoot, "preview-rbac.yaml");
        if (!File.Exists(serviceAccountPath) || !File.Exists(deploymentPath) || !File.Exists(rbacPath))
        {
            return;
        }

        var serviceAccounts = File.ReadAllText(serviceAccountPath);
        var deployment = File.ReadAllText(deploymentPath);
        var rbac = File.ReadAllText(rbacPath);
        var previewManagerBinding = HomelabYamlDocument(rbac, "ClusterRoleBinding", "rosenvall-devops-preview-manager");
        var runtimeCredentialsBinding = HomelabYamlDocument(rbac, "RoleBinding", "rosenvall-devops-api-credentials");
        var pipelineTokenCleanupBinding = HomelabYamlDocument(rbac, "RoleBinding", "rosenvall-devops-pipeline-token-cleanup");
        var previewSourceWriterRole = HomelabYamlDocument(rbac, "Role", "rosenvall-devops-preview-source-result-writer");
        var previewSourceWriterBinding = HomelabYamlDocument(rbac, "RoleBinding", "rosenvall-devops-preview-source-result-writer");

        Assert.Contains("name: rosenvall-devops-api", serviceAccounts);
        Assert.Contains("name: rosenvall-devops-runtime", serviceAccounts);
        Assert.Contains("serviceAccountName: rosenvall-devops-api", deployment);

        Assert.Contains("name: rosenvall-devops-api", previewManagerBinding);
        Assert.DoesNotContain("name: rosenvall-devops-runtime", previewManagerBinding);
        Assert.Contains("name: rosenvall-devops-api", runtimeCredentialsBinding);
        Assert.DoesNotContain("name: rosenvall-devops-runtime", runtimeCredentialsBinding);
        Assert.Contains("name: rosenvall-devops-api", pipelineTokenCleanupBinding);
        Assert.DoesNotContain("name: rosenvall-devops-runtime", pipelineTokenCleanupBinding);

        Assert.Contains("resources: [\"configmaps\"]", previewSourceWriterRole);
        Assert.Contains("verbs: [\"get\", \"create\", \"update\", \"patch\", \"delete\"]", previewSourceWriterRole);
        Assert.Contains("name: rosenvall-devops-runtime", previewSourceWriterBinding);
    }

    [Fact]
    public void Homelab_app_project_allows_devops_pipeline_namespace_when_available()
    {
        var root = new DirectoryInfo(FindRepositoryRoot());
        var projectPath = Path.Combine(
            root.Parent?.FullName ?? "",
            "Rosenvalls-Homelab",
            "kubernetes",
            "applications",
            "project.yaml");
        if (!File.Exists(projectPath))
        {
            return;
        }

        var project = File.ReadAllText(projectPath);

        Assert.Contains("namespace: rosenvall-devops-pipelines", project);
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
    public void New_human_comment_ownership_uses_actor_subject_not_client_display_name()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var board = store.GetWorkspaces().SelectMany(workspace => store.GetBoards(workspace.Id)).First();
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "comments", "Edit comments.", "Todo", "Medium", null));
        var comment = store.AddComment(item.Id, "Owner Name", "Comment", "Original text.", "authentik|owner")!;

        Assert.Equal("authentik|owner", comment.AuthorSubject);
        Assert.Throws<InvalidOperationException>(() => store.UpdateComment(comment.Id, "authentik|other", "Owner Name", "Spoofed edit."));
        Assert.Throws<InvalidOperationException>(() => store.DeleteComment(comment.Id, "authentik|other", "Owner Name"));

        var updated = store.UpdateComment(comment.Id, "authentik|owner", "Renamed Owner", "Updated text.");

        Assert.NotNull(updated);
        Assert.Equal("Updated text.", updated.Body);
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
        Assert.Contains("rosenvall.devops/managed-by: rosenvall-devops", manifest);
        Assert.Contains($"rosenvall.devops/board-id: {board.Id}", manifest);
        Assert.Contains($"rosenvall.devops/work-item-id: {item.Id}", manifest);
        Assert.Contains("automountServiceAccountToken: false", manifest);
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
    public void Runner_and_preview_terminal_lines_redact_common_secret_values()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest("GitHub", "secure-demo", "https://github.com/rosenvall/secure-demo.git", "main", "https://github.com/rosenvall/secure-demo", "rosenvall", "code-repo"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("Secure Demo", repository.Id, null, null, null, null, null))!;
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "secret log", "Do not leak tokens.", "Todo", "Medium", null))!;
        var aiRun = store.StartAiPlan(item.Id, "codex", "gpt-5.5", "Plan.")!;
        var implementationRun = store.StartImplementationRun(item.Id, new StartImplementationRunRequest(aiRun.Id, "crille", repository.Id))!;
        store.BeginPreviewImplementation(item.Id, "codex");

        var updatedRun = store.UpdateImplementationRun(
            implementationRun.Id,
            "Failed",
            "GITHUB_TOKEN=ghp_abcdefghijklmnopqrstuvwxyz123456\nremote https://x-access-token:github_pat_abcdefghijklmnopqrstuvwxyz123456@github.com/rosenvall/secure-demo.git\nAuthorization: Bearer secret-token-value\nAuthorization: Basic abc123\ncurl -H 'Authorization: Basic $forgejo_auth'\nremote http://rdo:service-token@forgejo/rdo/app.git\nmirror https://user:secret@example.com/repo.git\nforgejo_abcdefghijklmnopqrstuvwxyz123456",
            "failed with GITHUB_TOKEN=ghp_abcdefghijklmnopqrstuvwxyz123456 and Authorization: Basic abc123 for http://rdo:service-token@forgejo/rdo/app.git")!;
        var updatedPreview = store.AppendPreviewTerminalLine(item.Id, "stderr", "CLOUDFLARE_API_TOKEN=abc123 PASSWORD=hunter2");
        store.RecordPreviewFailure(item.Id, "ApplyFailed", "runner", "Authorization: Bearer preview-secret-token");
        var failedPreview = store.GetWorkItemDetail(item.Id)!.Preview!;

        Assert.DoesNotContain("ghp_", updatedRun.FailureReason);
        Assert.DoesNotContain("abc123", updatedRun.FailureReason);
        Assert.DoesNotContain("service-token", updatedRun.FailureReason);
        Assert.DoesNotContain(updatedRun.TerminalLines!, line =>
            line.Message.Contains("ghp_", StringComparison.Ordinal) ||
            line.Message.Contains("github_pat_", StringComparison.Ordinal) ||
            line.Message.Contains("secret-token-value", StringComparison.Ordinal) ||
            line.Message.Contains("abc123", StringComparison.Ordinal) ||
            line.Message.Contains("service-token", StringComparison.Ordinal) ||
            line.Message.Contains("user:secret", StringComparison.Ordinal) ||
            line.Message.Contains("forgejo_abcdefghijklmnopqrstuvwxyz123456", StringComparison.Ordinal));
        Assert.Contains(updatedRun.TerminalLines!, line => line.Message.Contains("GITHUB_TOKEN=[redacted]", StringComparison.Ordinal));
        Assert.Contains(updatedRun.TerminalLines!, line => line.Message.Contains("x-access-token:[redacted]@github.com", StringComparison.Ordinal));
        Assert.Contains(updatedRun.TerminalLines!, line => line.Message.Contains("Authorization: Bearer [redacted]", StringComparison.Ordinal));
        Assert.Contains(updatedRun.TerminalLines!, line => line.Message.Contains("Authorization: Basic [redacted]", StringComparison.Ordinal));
        Assert.Contains(updatedRun.TerminalLines!, line => line.Message.Contains("http://[redacted]@forgejo/rdo/app.git", StringComparison.Ordinal));
        Assert.Contains(updatedRun.TerminalLines!, line => line.Message.Contains("https://[redacted]@example.com/repo.git", StringComparison.Ordinal));
        Assert.Contains(updatedRun.TerminalLines!, line => line.Message.Contains("[redacted-localgit-token]", StringComparison.Ordinal));
        Assert.DoesNotContain(failedPreview.TerminalLines!, line => line.Message.Contains("abc123", StringComparison.Ordinal) || line.Message.Contains("hunter2", StringComparison.Ordinal) || line.Message.Contains("preview-secret-token", StringComparison.Ordinal));
        Assert.Contains(failedPreview.TerminalLines!, line => line.Message.Contains("CLOUDFLARE_API_TOKEN=[redacted]", StringComparison.Ordinal));
        Assert.Contains(failedPreview.TerminalLines!, line => line.Message.Contains("Authorization: Bearer [redacted]", StringComparison.Ordinal));
        Assert.DoesNotContain("preview-secret-token", failedPreview.Message);
    }

    [Fact]
    public void Interrupted_preview_implementation_is_requeued_after_restart()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var board = store.GetWorkspaces().SelectMany(workspace => store.GetBoards(workspace.Id)).First();
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "hamburgare", "Skapa en demo for tva hamburgare.", "Todo", "Medium", null));
        var run = store.StartAiPlan(item.Id, "codex", "gpt-5.4", "Implement two burger cards.")!;
        store.ApproveAiRun(run.Id, "crille");
        store.BeginPreviewImplementation(item.Id, "codex");

        var restored = fixture.Reopen().GetWorkItemDetail(item.Id)!;

        Assert.Equal("Implementing", restored.Preview?.Status);
        Assert.Null(restored.Preview?.FailureReason);
        Assert.Contains(restored.Preview!.TerminalLines!, line => line.Message.Contains("reattaching", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(restored.Preview!.TerminalLines!, line => line.Message.Contains("existing preview source", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(restored.PreviewImplementationRunsAwaitingRecovery!, pending => pending.Id == run.Id);
    }

    [Fact]
    public void Previously_failed_server_restart_preview_is_requeued_after_restart()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var board = store.GetWorkspaces().SelectMany(workspace => store.GetBoards(workspace.Id)).First();
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "klocka", "build a clock page with a button that switches between digital and analog clock modes", "Todo", "Medium", null));
        var run = store.StartAiPlan(item.Id, "codex", "gpt-5.4", "Implement a Swedish clock page with a digital/analog toggle.")!;
        store.ApproveAiRun(run.Id, "crille");
        store.BeginPreviewImplementation(item.Id, "codex");
        store.RecordPreviewFailure(item.Id, "ServerRestart", "system", "Preview implementation was interrupted by an API restart.");

        var restored = fixture.Reopen().GetWorkItemDetail(item.Id)!;

        Assert.Equal("Implementing", restored.Preview?.Status);
        Assert.Null(restored.Preview?.FailureReason);
        Assert.Contains(restored.Preview!.TerminalLines!, line => line.Message.Contains("reattaching", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(restored.PreviewImplementationRunsAwaitingRecovery!, pending => pending.Id == run.Id);
    }

    [Fact]
    public void Previously_failed_preview_source_timeout_is_requeued_after_restart()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var board = store.GetWorkspaces().SelectMany(workspace => store.GetBoards(workspace.Id)).First();
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "klocka", "build a clock page with a button that switches between digital and analog clock modes", "Todo", "Medium", null));
        var run = store.StartAiPlan(item.Id, "codex", "gpt-5.4", "Implement a Swedish clock page with a digital/analog toggle.")!;
        store.ApproveAiRun(run.Id, "crille");
        store.BeginPreviewImplementation(item.Id, "codex");
        store.RecordPreviewFailure(item.Id, "ImplementationFailed", "system", "Preview source job preview-source-task-4829-example timed out after 300 seconds; no preview was deployed.");

        var restored = fixture.Reopen().GetWorkItemDetail(item.Id)!;

        Assert.Equal("Implementing", restored.Preview?.Status);
        Assert.Null(restored.Preview?.FailureReason);
        Assert.Contains(restored.Preview!.TerminalLines!, line => line.Message.Contains("reattaching", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(restored.PreviewImplementationRunsAwaitingRecovery!, pending => pending.Id == run.Id);
    }

    [Fact]
    public void Applying_preview_with_generated_source_recovers_as_provisioning_after_restart()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var board = store.GetWorkspaces().SelectMany(workspace => store.GetBoards(workspace.Id)).First();
        const string description = "gör en hemsida med en klocka och en knapp så att man kan växla mellan digital och analogt ur.";
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "klockhemsida", description, "Todo", "Medium", null));
        var run = store.StartAiPlan(item.Id, "codex", "gpt-5.4", "Implement a Swedish clock page with a digital/analog toggle.")!;
        store.ApproveAiRun(run.Id, "crille");
        var generatedSource = LocalReactPreviewProject.ForWorkItem(item.Key, item.Title, description)
            .Select(file => file.Path == "src/App.tsx"
                ? new PreviewSourceFile(file.Key, file.Path, "export default function App() { return <main>Klocka</main>; }")
                : file)
            .ToArray();
        store.CompletePreviewImplementation(item.Id, generatedSource, "codex");
        store.MarkPreviewApplying(item.Id, "kubectl apply started.");

        var reopened = fixture.Reopen();
        var restored = reopened.GetWorkItemDetail(item.Id)!;
        var awaiting = reopened.GetPreviewsAwaitingHealthCheck();

        Assert.Equal("Provisioning", restored.Preview?.Status);
        Assert.Equal("Waiting for pod readiness.", restored.Preview?.Phase);
        Assert.Null(restored.Preview?.FailureReason);
        Assert.Contains(awaiting, preview => preview.WorkItemId == item.Id && preview.Status == "Provisioning");
    }

    [Fact]
    public void Implementing_preview_is_not_checked_until_source_has_been_generated_and_applied()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var board = store.GetWorkspaces().SelectMany(workspace => store.GetBoards(workspace.Id)).First();
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "hamburgare", "Skapa en demo for tva hamburgare.", "Todo", "Medium", null));
        var run = store.StartAiPlan(item.Id, "codex", "gpt-5.4", "Implement two burger cards.")!;
        store.ApproveAiRun(run.Id, "crille");

        store.BeginPreviewImplementation(item.Id, "codex");

        var awaiting = store.GetPreviewsAwaitingHealthCheck();

        Assert.DoesNotContain(awaiting, preview => preview.WorkItemId == item.Id && preview.Status == "Implementing");
    }

    [Fact]
    public void Pending_or_failed_preview_implementation_without_source_does_not_render_placeholder_manifest()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var board = store.GetWorkspaces().SelectMany(workspace => store.GetBoards(workspace.Id)).First();
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "klocka", "gör en hemsida med en klocka och en knapp så att man kan växla mellan digital och analogt ur.", "Todo", "Medium", null));
        var run = store.StartAiPlan(item.Id, "codex", "gpt-5.4", "Implement a Swedish clock page with a digital/analog toggle.")!;
        store.ApproveAiRun(run.Id, "crille");

        store.BeginPreviewImplementation(item.Id, "codex");
        var pendingManifest = store.RenderPreviewManifest(item.Id);
        store.RecordImplementationFailure(item.Id, "crille", "Codex preview source generation failed.");
        var failedManifest = store.RenderPreviewManifest(item.Id);

        Assert.Null(pendingManifest);
        Assert.Null(failedManifest);
    }

    [Fact]
    public void Work_item_detail_includes_preview_history_for_implementation_attempts()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var store = fixture.Store;
        var board = store.GetWorkspaces().SelectMany(workspace => store.GetBoards(workspace.Id)).First();
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "hamburgare", "Skapa en demo for tva hamburgare.", "Todo", "Medium", null));
        var run = store.StartAiPlan(item.Id, "codex", "gpt-5.4", "Implement two burger cards.")!;
        store.ApproveAiRun(run.Id, "crille");

        store.BeginPreviewImplementation(item.Id, "codex");
        store.AppendPreviewTerminalLine(item.Id, "stdout", "OpenAI Codex v0.131.0");
        store.RecordImplementationFailure(item.Id, "crille", "Codex preview source generation failed.");
        var detail = store.GetWorkItemDetail(item.Id)!;

        Assert.Contains(detail.PreviewEvents!, entry => entry.EventType == "Implementing");
        Assert.Contains(detail.PreviewEvents!, entry => entry.EventType == "ImplementationFailed");
        Assert.Contains(detail.Preview!.TerminalLines!, line => line.Message.Contains("OpenAI Codex", StringComparison.Ordinal));
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
    public void Preview_source_policy_rejects_files_outside_react_preview_workspace()
    {
        var files = LocalReactPreviewProject.ForWorkItem("TASK-4825", "rita app", "skapa en app dar jag kan rita").ToList();
        files.Add(new PreviewSourceFile("env", ".env", "GITHUB_TOKEN=secret"));

        var error = Assert.Throws<ArgumentException>(() => PreviewSourcePolicy.Validate(files));

        Assert.Contains("outside the allowed React preview paths", error.Message);
    }

    [Fact]
    public void Preview_source_policy_allows_src_and_public_assets_with_size_limits()
    {
        var files = LocalReactPreviewProject.ForWorkItem("TASK-4825", "rita app", "skapa en app dar jag kan rita").ToList();
        files.Add(new PreviewSourceFile("logo", "public/logo.svg", "<svg role=\"img\" />"));
        files.Add(new PreviewSourceFile("component", "src/components/Demo.tsx", "export function Demo() { return null; }"));

        PreviewSourcePolicy.Validate(files);
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
    public async Task Codex_preview_source_provider_uses_workspace_sandbox_by_default()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var board = fixture.Store.GetWorkspaces().SelectMany(workspace => fixture.Store.GetBoards(workspace.Id)).First();
        var item = fixture.Store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "sandboxed preview", "Generate a preview safely.", "Todo", "Medium", null));
        var run = fixture.Store.StartAiPlan(item.Id, "codex", "gpt-5.4", "Build the preview.")!;
        run.Approve("crille");
        var context = fixture.Store.GetWorkItemDetail(item.Id)!;
        var argsCapture = Path.Combine(Path.GetTempPath(), $"codex-args-{Guid.NewGuid():N}.txt");
        var fakeCodex = CreateFakeCodexImplementationScript(0, "export default function App() { return <main>Sandboxed</main>; }", argsCapture);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:Codex:Path"] = fakeCodex,
                ["Ai:Codex:Home"] = Path.Combine(Path.GetTempPath(), $"codex-home-{Guid.NewGuid():N}")
            })
            .Build();
        var provider = new CodexCliPreviewSourceProvider(configuration, NullLogger<CodexCliPreviewSourceProvider>.Instance);

        await provider.GenerateSourceAsync("gpt-5.4", run, context, null, CancellationToken.None);

        var args = await File.ReadAllTextAsync(argsCapture);
        Assert.Contains("--sandbox", args);
        Assert.Contains("workspace-write", args);
        Assert.DoesNotContain("--dangerously-bypass-approvals-and-sandbox", args);
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
    public void Preview_source_job_manifest_uses_isolated_codex_home_and_kubernetes_sandbox()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var board = fixture.Store.GetWorkspaces().SelectMany(workspace => fixture.Store.GetBoards(workspace.Id)).First();
        var item = fixture.Store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "klocka", "gör en hemsida med en klocka", "Todo", "Medium", null));
        var run = fixture.Store.StartAiPlan(item.Id, "codex", "gpt-5.4", "Build a clock preview.")!;
        run.Approve("crille");
        var context = fixture.Store.GetWorkItemDetail(item.Id)!;

        var manifest = PreviewSourceJobManifestRenderer.Render(run, context, "gpt-5.4", "high", runnerImage: "ghcr.io/carnufex/rosenvall-devops-api@sha256:testdigest");

        AssertPreviewSourceJobYamlContract(manifest, "ghcr.io/carnufex/rosenvall-devops-api@sha256:testdigest");
        Assert.Contains("kind: Job", manifest);
        Assert.Contains("namespace: rosenvall-devops", manifest);
        Assert.Contains("serviceAccountName: rosenvall-devops-runtime", manifest);
        Assert.Contains("automountServiceAccountToken: false", manifest);
        Assert.Contains("app.kubernetes.io/part-of: rosenvall-devops-preview-source", manifest);
        Assert.Contains("name: prepare-codex-home", manifest);
        Assert.Contains("name: generate-preview-source", manifest);
        Assert.Contains("name: publish-result", manifest);
        Assert.Contains("app.kubernetes.io/name: rosenvall-devops-api", manifest);
        Assert.Contains("topologyKey: kubernetes.io/hostname", manifest);
        Assert.Contains("name: codex-home-source", manifest);
        Assert.Contains("claimName: rosenvall-devops-codex-home", manifest);
        Assert.Contains("readOnly: true", manifest);
        Assert.Contains("emptyDir: {}", manifest);
        Assert.Contains("mountPath: /app/codex-home", manifest);
        Assert.Contains("name: CODEX_HOME", manifest);
        Assert.Contains("value: /app/codex-home", manifest);
        Assert.Contains("name: HOME", manifest);
        Assert.Contains("value: /home/ubuntu", manifest);
        Assert.Contains("codex exec --ephemeral", manifest);
        Assert.Contains("--sandbox danger-full-access", manifest);
        Assert.Contains("codex-output.log", manifest);
        Assert.Contains("codex_pid=$!", manifest);
        Assert.Contains("rm -f \"$CODEX_HOME/auth.json\"", manifest);
        Assert.Contains("wait \"$codex_pid\"", manifest);
        Assert.True(manifest.IndexOf("rm -f \"$CODEX_HOME/auth.json\"", StringComparison.Ordinal) > manifest.IndexOf("codex exec", StringComparison.Ordinal));
        Assert.True(manifest.IndexOf("rm -f \"$CODEX_HOME/auth.json\"", StringComparison.Ordinal) < manifest.IndexOf("wait \"$codex_pid\"", StringComparison.Ordinal));
        Assert.Contains("const skippedFiles = new Set(['seed.json', 'prompt.md', 'result.json', 'codex-output.log']);", manifest);
        Assert.Contains("RDO_FAILURE=Codex runner sandbox is unavailable in this Kubernetes runner", manifest);
        Assert.Contains("name: kube-api-access", manifest);
        Assert.Contains("serviceAccountToken:", manifest);
        Assert.Contains("mountPath: /var/run/secrets/kubernetes.io/serviceaccount", manifest);
        Assert.DoesNotContain("--dangerously-bypass-approvals-and-sandbox", manifest);
        Assert.Contains("ROSENVALL_PREVIEW_SEED_B64", manifest);
        Assert.Contains("ROSENVALL_RESULT_CONFIGMAP", manifest);
        Assert.Contains("kubectl -n \"$ROSENVALL_RESULT_NAMESPACE\" create configmap \"$ROSENVALL_RESULT_CONFIGMAP\"", manifest);
        Assert.Contains("image: ghcr.io/carnufex/rosenvall-devops-api@sha256:testdigest", manifest);
        Assert.DoesNotContain("image: ghcr.io/carnufex/rosenvall-devops-api:main", manifest);
    }

    [Fact]
    public void Preview_source_prompt_forbids_readme_and_documentation_artifacts()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var board = fixture.Store.GetWorkspaces().SelectMany(workspace => fixture.Store.GetBoards(workspace.Id)).First();
        var item = fixture.Store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "clock", "Build a Swedish clock app.", "Todo", "Medium", null));
        var run = fixture.Store.StartAiPlan(item.Id, "codex", "gpt-5.4", "Build a clock preview.")!;
        run.Approve("crille");
        var context = fixture.Store.GetWorkItemDetail(item.Id)!;

        var prompt = PreviewSourcePromptBuilder.BuildImplementationPrompt(run, context);

        Assert.Contains("Do not create or update README", prompt);
        Assert.Contains("runtime Vite/React source", prompt);
    }

    [Fact]
    public void Preview_source_job_result_accepts_real_app_ignores_readme_and_rejects_placeholder()
    {
        var goodResult = """
            {
              "data": {
                "result.json": "{\"files\":[{\"key\":\"readme-md\",\"path\":\"README.md\",\"content\":\"# Klocka\"},{\"key\":\"src-app-tsx\",\"path\":\"src/App.tsx\",\"content\":\"export default function App() { return <main>Klocka</main>; }\"},{\"key\":\"vite-config-ts\",\"path\":\"vite.config.ts\",\"content\":\"export default {};\"}]}"
              }
            }
            """;
        var placeholderResult = """
            {
              "data": {
                "result.json": "{\"files\":[{\"key\":\"readme-md\",\"path\":\"README.md\",\"content\":\"# Placeholder\"},{\"key\":\"src-app-tsx\",\"path\":\"src/App.tsx\",\"content\":\"React, TypeScript and Tailwind are ready for this ticket slice.\"}]}"
              }
            }
            """;

        var files = PreviewSourceJobResultParser.ParseConfigMapJson(goodResult);
        var error = Assert.Throws<AiPlanProviderUnavailableException>(() => PreviewSourceJobResultParser.ParseConfigMapJson(placeholderResult));

        Assert.Contains(files, file => file.Path == "src/App.tsx" && file.Content.Contains("Klocka", StringComparison.Ordinal));
        Assert.DoesNotContain(files, file => file.Path == "README.md");
        Assert.Contains("seeded placeholder app unchanged", error.Message);
    }

    [Fact]
    public void Preview_source_job_result_accepts_real_app_ignores_runner_log_artifact()
    {
        var result = """
            {
              "data": {
                "result.json": "{\"files\":[{\"key\":\"codex-output-log\",\"path\":\"codex-output.log\",\"content\":\"Codex summary\"},{\"key\":\"src-app-tsx\",\"path\":\"src/App.tsx\",\"content\":\"export default function App() { return <main>Klocka</main>; }\"},{\"key\":\"src-index-css\",\"path\":\"src/index.css\",\"content\":\"body { margin: 0; }\"}]}"
              }
            }
            """;

        var files = PreviewSourceJobResultParser.ParseConfigMapJson(result);

        Assert.Contains(files, file => file.Path == "src/App.tsx");
        Assert.Contains(files, file => file.Path == "src/index.css");
        Assert.DoesNotContain(files, file => file.Path == "codex-output.log");
    }

    [Fact]
    public void Preview_source_job_result_still_rejects_non_documentation_files_outside_allowed_paths()
    {
        var result = """
            {
              "data": {
                "result.json": "{\"files\":[{\"key\":\"env\",\"path\":\".env\",\"content\":\"SECRET=1\"},{\"key\":\"src-app-tsx\",\"path\":\"src/App.tsx\",\"content\":\"export default function App() { return <main>Klocka</main>; }\"}]}"
              }
            }
            """;

        var error = Assert.Throws<AiPlanProviderUnavailableException>(() => PreviewSourceJobResultParser.ParseConfigMapJson(result));

        Assert.Contains("outside the allowed React preview paths", error.Message);
    }

    [Fact]
    public void Preview_source_job_result_still_rejects_arbitrary_root_log_files()
    {
        var result = """
            {
              "data": {
                "result.json": "{\"files\":[{\"key\":\"debug-log\",\"path\":\"debug.log\",\"content\":\"debug output\"},{\"key\":\"src-app-tsx\",\"path\":\"src/App.tsx\",\"content\":\"export default function App() { return <main>Klocka</main>; }\"}]}"
              }
            }
            """;

        var error = Assert.Throws<AiPlanProviderUnavailableException>(() => PreviewSourceJobResultParser.ParseConfigMapJson(result));

        Assert.Contains("outside the allowed React preview paths", error.Message);
    }

    [Fact]
    public void Bubblewrap_preview_source_failure_is_reported_clearly()
    {
        var message = KubernetesFailureClassifier.Classify("bwrap: No permissions to create a new namespace, likely because the kernel does not allow non-privileged user namespaces.");

        Assert.Equal("Codex runner sandbox is unavailable in this Kubernetes runner.", message);
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
                ["Ai:Codex:ReasoningEffort"] = "xhigh",
                ["Ai:Codex:AvailableModels:0"] = "gpt-5.4",
                ["Ai:Codex:AvailableModels:1"] = "gpt-5.3-codex"
            })
            .Build();

        var settings = fixture.Store.GetSettings(configuration);

        Assert.Equal("qwen3.5:latest", settings.Ai.ActiveModel);
        Assert.Equal(["qwen3.5:latest", "llama3.1:8b"], settings.Ai.AvailableModels);
        Assert.Contains(settings.Ai.AvailableProviders, provider => provider.Provider == "ollama" && provider.Status == "Ready");
        Assert.Contains(settings.Ai.AvailableProviders, provider => provider.Provider == "codex" && provider.ActiveModel == "gpt-5.4");
        var codex = settings.Ai.AvailableProviders.Single(provider => provider.Provider == "codex");
        Assert.Contains("gpt-5.5", codex.AvailableModels);
        Assert.Contains("gpt-5.3-codex-spark", codex.AvailableModels);
        Assert.Equal(["low", "medium", "high", "xhigh"], codex.AvailableReasoningEfforts);
        Assert.Equal("xhigh", codex.DefaultReasoningEffort);
    }

    [Fact]
    public void Settings_filters_github_integration_details_by_installer()
    {
        using var fixture = DevOpsStoreFixture.Create();
        fixture.Store.CreateGitHubIntegration(new GitHubIntegrationCallbackRequest(
            1234,
            "private-org",
            "Organization",
            "authentik|owner",
            RepositoriesCount: 17));

        var ownerSettings = fixture.Store.GetSettings(new ConfigurationBuilder().Build(), "authentik|owner");
        var guestSettings = fixture.Store.GetSettings(new ConfigurationBuilder().Build(), "authentik|guest");

        Assert.Equal("private-org (Organization)", ownerSettings.GitHub.Account);
        Assert.Equal("17 repositories granted", ownerSettings.GitHub.TargetRepository);
        Assert.Equal("No GitHub App installation", guestSettings.GitHub.Account);
        Assert.Equal("Install the GitHub App to list repositories", guestSettings.GitHub.TargetRepository);
    }

    [Fact]
    public void Ai_model_policy_rejects_unconfigured_provider_model_and_reasoning_effort()
    {
        using var fixture = DevOpsStoreFixture.Create();
        var settings = fixture.Store.GetSettings(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:DefaultModel"] = "qwen3.5:latest",
                ["Ai:Codex:Model"] = "gpt-5.5"
            })
            .Build());

        var valid = AiModelPolicy.ValidatePlanningRequest(new StartAiPlanRequest("codex", "gpt-5.5", "high"), settings);
        var badProvider = AiModelPolicy.ValidatePlanningRequest(new StartAiPlanRequest("unknown", "gpt-5.5", "high"), settings);
        var badModel = AiModelPolicy.ValidatePlanningRequest(new StartAiPlanRequest("codex", "gpt-unknown", "high"), settings);
        var badReasoning = AiModelPolicy.ValidatePlanningRequest(new StartAiPlanRequest("codex", "gpt-5.5", "max"), settings);

        Assert.NotNull(valid);
        Assert.Equal("codex", valid.Provider);
        Assert.Equal("gpt-5.5", valid.Model);
        Assert.Equal("high", valid.ReasoningEffort);
        Assert.Null(badProvider);
        Assert.Null(badModel);
        Assert.Null(badReasoning);
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

    private static (RepositoryDto Repository, WorkItemSummaryDto Item, ImplementationRunDto Run) CreateLocalGitPreviewPullRequest(DevOpsStore store)
    {
        var workspace = store.GetWorkspaces().First();
        var repository = store.CreateRepository(new CreateRepositoryRequest(
            "LocalGit",
            "demo-app",
            "http://rosenvall-devops-forgejo.rosenvall-devops.svc.cluster.local:3000/rdo/demo-app.git",
            "main",
            null,
            "rdo",
            "react-preview",
            "preview-then-pr"));
        var board = store.CreateBoard(workspace.Id, new CreateBoardRequest("Demo app", repository.Id, null, null, null, null, null, ImplementationProfile: "react-preview"))!;
        var item = store.CreateWorkItem(new CreateWorkItemRequest(board.Id, "Feature", "utveckling", "Build a time app.", "Todo", "Medium", null));
        var aiRun = store.StartAiPlan(item.Id, "codex", "gpt-5.5", "Build preview.")!;
        store.ApproveAiRun(aiRun.Id, "crille");
        store.CompletePreviewImplementation(item.Id, [
            new PreviewSourceFile("app", "src/App.tsx", "export default function App(){return <main>Clock</main>}")
        ], "codex");
        store.MarkPreviewRunning(item.Id, "test", "Preview is running.");
        var run = store.StartPreviewPromotionRun(item.Id, "crille")!;
        store.UpdateImplementationRun(run.Id, "PullRequestReady", "RDO_COMMIT=abc123\nRDO_PULL_REQUEST_URL=http://forgejo.local/rdo/demo-app/pulls/7\nRDO_PULL_REQUEST_NUMBER=7\nRDO_PULL_REQUEST_STATE=open");
        return (repository, item, run);
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

        public void UpdateSnapshotJson(Func<string, string> update)
        {
            var options = new DbContextOptionsBuilder<DevOpsStateDbContext>()
                .UseSqlite($"Data Source={_databasePath}")
                .Options;
            using var db = new DevOpsStateDbContext(options);
            var document = db.Documents.Single(document => document.Id == "default");
            document.Json = update(document.Json);
            document.UpdatedAt = DateTimeOffset.UtcNow;
            db.SaveChanges();
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

    private sealed class RoutingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(route(request));
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static HttpResponseMessage JsonContent(string content)
    {
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));
        return JsonResponse($$"""{"encoding":"base64","content":"{{base64}}"}""");
    }

    private sealed class FakePlanProvider(string providerName, string plan) : IAiPlanProvider
    {
        public string ProviderName => providerName;

        public string? LastModel { get; private set; }
        public string? LastReasoningEffort { get; private set; }

        public Task<string> GeneratePlanAsync(string model, string? reasoningEffort, WorkItemDetailDto context, CancellationToken cancellationToken)
        {
            LastModel = model;
            LastReasoningEffort = reasoningEffort;
            return Task.FromResult(plan);
        }
    }

    private static string DecodeManifestEnvironmentValue(string manifest, string name)
    {
        var lines = manifest.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var index = 0; index < lines.Length - 1; index++)
        {
            if (lines[index].Trim() == $"- name: {name}")
            {
                var value = lines[index + 1].Trim();
                const string prefix = "value: \"";
                if (value.StartsWith(prefix, StringComparison.Ordinal) && value.EndsWith("\"", StringComparison.Ordinal))
                {
                    var encoded = value[prefix.Length..^1];
                    return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                }
            }
        }

        throw new InvalidOperationException($"Environment value {name} was not found.");
    }

    private static string RemovePreviewStepLogs(string json)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        if (root["previews"] is JsonArray previews)
        {
            foreach (var preview in previews.OfType<JsonObject>())
            {
                preview.Remove("stepLogs");
            }
        }

        return root.ToJsonString();
    }

    private static string DuplicateFirstRosenvallAiResultComment(string json)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        var comments = root["comments"]!.AsArray();
        var firstSystemResult = comments
            .OfType<JsonObject>()
            .First(comment =>
                string.Equals(comment["author"]?.GetValue<string>(), "Rosenvall AI", StringComparison.Ordinal) &&
                string.Equals(comment["kind"]?.GetValue<string>(), "Result", StringComparison.Ordinal));
        var duplicate = firstSystemResult.DeepClone().AsObject();
        duplicate["body"] = $"{duplicate["body"]!.GetValue<string>().TrimEnd('.')}.";
        comments.Add(duplicate);
        return root.ToJsonString();
    }

    private static void AssertImplementationManifestEnvListIsWellFormed(string manifest)
    {
        var lines = manifest.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var envIndex = Array.FindIndex(lines, line => line.Trim() == "env:");
        var commandIndex = Array.FindIndex(lines, envIndex + 1, line => line.Trim() == "command:");

        Assert.True(envIndex >= 0, "Manifest should include a container env block.");
        Assert.True(commandIndex > envIndex, "Manifest env block should be followed by a command block.");

        var nameLines = lines[(envIndex + 1)..commandIndex]
            .Where(line => line.TrimStart().StartsWith("- name:", StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(nameLines);

        var listIndent = LeadingSpaces(nameLines[0]);
        foreach (var line in nameLines)
        {
            Assert.Equal(listIndent, LeadingSpaces(line));
        }

        foreach (var line in lines[(envIndex + 1)..commandIndex])
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("value:", StringComparison.Ordinal) || trimmed.StartsWith("valueFrom:", StringComparison.Ordinal))
            {
                Assert.Equal(listIndent + 2, LeadingSpaces(line));
            }
        }
    }

    private static void AssertPreviewPromotionRunnerSecurityContextIsWellFormed(string manifest)
    {
        var job = KubernetesYaml.SingleDocument(manifest, "Job");
        var runner = Assert.Single(job.Spec.Template.Spec.Containers, container => container.Name == "runner");

        Assert.True(runner.SecurityContext.RunAsNonRoot);
        Assert.Equal(1000, runner.SecurityContext.RunAsUser);
        Assert.Equal(1000, runner.SecurityContext.RunAsGroup);
        Assert.False(runner.SecurityContext.AllowPrivilegeEscalation);
        Assert.Contains("ALL", runner.SecurityContext.Capabilities.Drop);
    }

    private static void AssertCodexRepositoryJobYamlContract(string manifest, string expectedImage, string secretEnvName)
    {
        var job = KubernetesYaml.SingleDocument(manifest, "Job");
        var pod = job.Spec.Template.Spec;
        var init = Assert.Single(pod.InitContainers, container => container.Name == "prepare-codex-home");
        var runner = Assert.Single(pod.Containers, container => container.Name == "runner");

        Assert.False(pod.AutomountServiceAccountToken);
        Assert.Equal(expectedImage, init.Image);
        Assert.Equal(expectedImage, runner.Image);
        Assert.Equal("codex-home", Assert.Single(pod.Volumes, volume => volume.Name == "codex-home").Name);
        Assert.NotNull(pod.Volumes.Single(volume => volume.Name == "codex-home").EmptyDir);
        Assert.Equal("rosenvall-devops-codex-home", pod.Volumes.Single(volume => volume.Name == "codex-home-source").PersistentVolumeClaim.ClaimName);
        Assert.True(init.VolumeMounts.Single(mount => mount.Name == "codex-home-source").ReadOnly);
        Assert.Equal("/app/codex-home", init.VolumeMounts.Single(mount => mount.Name == "codex-home").MountPath);
        Assert.Equal("/app/codex-home", runner.VolumeMounts.Single(mount => mount.Name == "codex-home").MountPath);
        Assert.DoesNotContain(runner.VolumeMounts, mount => mount.Name == "codex-home-source");
        Assert.Equal("/app/codex-home", runner.Env.Single(env => env.Name == "CODEX_HOME").Value);
        var secretEnv = runner.Env.Single(env => env.Name == secretEnvName);
        Assert.Null(secretEnv.Value);
        Assert.NotNull(secretEnv.ValueFrom.SecretKeyRef);
        Assert.Equal("token", secretEnv.ValueFrom.SecretKeyRef.Key);

        var command = string.Join('\n', runner.Command ?? []);
        Assert.Contains("codex_pid=$!", command);
        Assert.Contains("rm -f \"$CODEX_HOME/auth.json\"", command);
        Assert.Contains("wait \"$codex_pid\"", command);
        Assert.True(command.IndexOf("rm -f \"$CODEX_HOME/auth.json\"", StringComparison.Ordinal) < command.IndexOf("wait \"$codex_pid\"", StringComparison.Ordinal));
    }

    private static void AssertPreviewSourceJobYamlContract(string manifest, string expectedImage)
    {
        var job = KubernetesYaml.SingleDocument(manifest, "Job");
        var pod = job.Spec.Template.Spec;
        var init = Assert.Single(pod.InitContainers, container => container.Name == "prepare-codex-home");
        var source = Assert.Single(pod.InitContainers, container => container.Name == "generate-preview-source");
        var publisher = Assert.Single(pod.Containers, container => container.Name == "publish-result");

        Assert.False(pod.AutomountServiceAccountToken);
        Assert.Equal("rosenvall-devops-runtime", pod.ServiceAccountName);
        Assert.Equal(expectedImage, init.Image);
        Assert.Equal(expectedImage, source.Image);
        Assert.Equal(expectedImage, publisher.Image);
        Assert.NotNull(pod.Volumes.Single(volume => volume.Name == "codex-home").EmptyDir);
        Assert.Equal("rosenvall-devops-codex-home", pod.Volumes.Single(volume => volume.Name == "codex-home-source").PersistentVolumeClaim.ClaimName);
        Assert.True(init.VolumeMounts.Single(mount => mount.Name == "codex-home-source").ReadOnly);
        Assert.Equal("/app/codex-home", source.VolumeMounts.Single(mount => mount.Name == "codex-home").MountPath);
        Assert.DoesNotContain(source.VolumeMounts, mount => mount.Name == "kube-api-access");
        Assert.Equal("/var/run/secrets/kubernetes.io/serviceaccount", publisher.VolumeMounts.Single(mount => mount.Name == "kube-api-access").MountPath);
        Assert.Equal("/app/codex-home", source.Env.Single(env => env.Name == "CODEX_HOME").Value);

        var command = string.Join('\n', source.Command ?? []);
        Assert.Contains("codex_pid=$!", command);
        Assert.Contains("rm -f \"$CODEX_HOME/auth.json\"", command);
        Assert.Contains("wait \"$codex_pid\"", command);
        Assert.True(command.IndexOf("rm -f \"$CODEX_HOME/auth.json\"", StringComparison.Ordinal) < command.IndexOf("wait \"$codex_pid\"", StringComparison.Ordinal));
    }

    private static int LeadingSpaces(string value)
    {
        var count = 0;
        while (count < value.Length && value[count] == ' ')
        {
            count++;
        }

        return count;
    }

    private static string HomelabYamlDocument(string manifest, string kind, string metadataName)
    {
        foreach (var document in manifest.Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n---\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!document.Split('\n').Any(line => line.Trim() == $"kind: {kind}"))
            {
                continue;
            }

            var lines = document.Split('\n');
            var metadataIndex = Array.FindIndex(lines, line => line.Trim() == "metadata:");
            if (metadataIndex < 0)
            {
                continue;
            }

            for (var index = metadataIndex + 1; index < lines.Length; index++)
            {
                var line = lines[index];
                if (!line.StartsWith(' ') && !string.IsNullOrWhiteSpace(line))
                {
                    break;
                }

                if (line.Trim() == $"name: {metadataName}")
                {
                    return document;
                }
            }
        }

        throw new InvalidOperationException($"Could not find {kind}/{metadataName}.");
    }

    private static class KubernetesYaml
    {
        private static readonly IDeserializer Deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        public static KubernetesDocument SingleDocument(string manifest, string kind)
        {
            var matches = ParseDocuments(manifest)
                .Where(document => string.Equals(document.Kind, kind, StringComparison.Ordinal))
                .ToArray();
            return Assert.Single(matches);
        }

        private static IReadOnlyList<KubernetesDocument> ParseDocuments(string manifest) =>
            manifest
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split("\n---\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(document => !string.IsNullOrWhiteSpace(document))
                .Select(document => Deserializer.Deserialize<KubernetesDocument>(new StringReader(document)))
                .Where(document => !string.IsNullOrWhiteSpace(document.Kind))
                .ToArray();
    }

    private sealed class KubernetesDocument
    {
        public string ApiVersion { get; set; } = "";
        public string Kind { get; set; } = "";
        public KubernetesMetadata Metadata { get; set; } = new();
        public string Type { get; set; } = "";
        public Dictionary<string, string> Data { get; set; } = [];
        public KubernetesJobSpec Spec { get; set; } = new();
    }

    private sealed class KubernetesMetadata
    {
        public string Name { get; set; } = "";
        public string Namespace { get; set; } = "";
        public Dictionary<string, string> Labels { get; set; } = [];
    }

    private sealed class KubernetesJobSpec
    {
        public KubernetesPodTemplate Template { get; set; } = new();
    }

    private sealed class KubernetesPodTemplate
    {
        public KubernetesPodSpec Spec { get; set; } = new();
    }

    private sealed class KubernetesPodSpec
    {
        public bool AutomountServiceAccountToken { get; set; }
        public string ServiceAccountName { get; set; } = "";
        public List<KubernetesContainer> InitContainers { get; set; } = [];
        public List<KubernetesContainer> Containers { get; set; } = [];
        public List<KubernetesVolume> Volumes { get; set; } = [];
    }

    private sealed class KubernetesContainer
    {
        public string Name { get; set; } = "";
        public string Image { get; set; } = "";
        public KubernetesSecurityContext SecurityContext { get; set; } = new();
        public List<KubernetesEnvVar> Env { get; set; } = [];
        public List<KubernetesVolumeMount> VolumeMounts { get; set; } = [];
        public List<string> Command { get; set; } = [];
    }

    private sealed class KubernetesEnvVar
    {
        public string Name { get; set; } = "";
        public string? Value { get; set; }
        public KubernetesEnvVarSource ValueFrom { get; set; } = new();
    }

    private sealed class KubernetesEnvVarSource
    {
        public KubernetesSecretKeyRef? SecretKeyRef { get; set; }
    }

    private sealed class KubernetesSecretKeyRef
    {
        public string Name { get; set; } = "";
        public string Key { get; set; } = "";
    }

    private sealed class KubernetesVolumeMount
    {
        public string Name { get; set; } = "";
        public string MountPath { get; set; } = "";
        public bool ReadOnly { get; set; }
    }

    private sealed class KubernetesVolume
    {
        public string Name { get; set; } = "";
        public Dictionary<string, object>? EmptyDir { get; set; }
        public KubernetesPersistentVolumeClaim PersistentVolumeClaim { get; set; } = new();
    }

    private sealed class KubernetesPersistentVolumeClaim
    {
        public string ClaimName { get; set; } = "";
    }

    private sealed class KubernetesSecurityContext
    {
        public bool RunAsNonRoot { get; set; }
        public int RunAsUser { get; set; }
        public int RunAsGroup { get; set; }
        public bool AllowPrivilegeEscalation { get; set; }
        public KubernetesCapabilities Capabilities { get; set; } = new();
    }

    private sealed class KubernetesCapabilities
    {
        public List<string> Drop { get; set; } = [];
    }

    private static string CreateFakeCodexScript(int exitCode, string plan, bool requireSkipGitRepoCheck = false, string? promptCapturePath = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            var unixScriptPath = Path.Combine(Path.GetTempPath(), $"fake-codex-{Guid.NewGuid():N}.sh");
            var escapedPlan = plan.Replace("'", "'\"'\"'", StringComparison.Ordinal);
            var escapedCapture = (promptCapturePath ?? "").Replace("'", "'\"'\"'", StringComparison.Ordinal);
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
if [ -n "{{escapedCapture}}" ]; then cat > "{{escapedCapture}}"; else cat >/dev/null; fi
if [ -n "$last" ]; then printf '%s\n' '{{escapedPlan}}' > "$last"; fi
exit {{exitCode}}
""");
            File.SetUnixFileMode(unixScriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return unixScriptPath;
        }

        var scriptPath = Path.Combine(Path.GetTempPath(), $"fake-codex-{Guid.NewGuid():N}.cmd");
        var requiredSkipCheckWindows = requireSkipGitRepoCheck ? "1" : "0";
        var captureCommand = string.IsNullOrWhiteSpace(promptCapturePath) ? "more > nul" : $"more > \"{promptCapturePath}\"";
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
{captureCommand}
if defined last echo {plan}> "%last%"
exit /b {exitCode}
""");
        return scriptPath;
    }

    private static string CreateFakeCodexImplementationScript(int exitCode, string appSource, string? argsCapturePath = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            var unixScriptPath = Path.Combine(Path.GetTempPath(), $"fake-codex-implementation-{Guid.NewGuid():N}.sh");
            var escapedAppSource = appSource.Replace("'", "'\"'\"'", StringComparison.Ordinal);
            var argsCapture = string.IsNullOrWhiteSpace(argsCapturePath)
                ? ""
                : $"printf '%s\\n' \"$*\" > '{argsCapturePath.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";
            File.WriteAllText(unixScriptPath, $$"""
#!/usr/bin/env sh
{{argsCapture}}
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
        var argsCaptureCommand = string.IsNullOrWhiteSpace(argsCapturePath) ? "" : $"echo %* > \"{argsCapturePath}\"";
        File.WriteAllText(scriptPath, $"""
@echo off
{argsCaptureCommand}
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
cat >/dev/null
exit {{exitCode}}
""");
            File.SetUnixFileMode(unixScriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return unixScriptPath;
        }

        var scriptPath = Path.Combine(Path.GetTempPath(), $"fake-codex-noop-{Guid.NewGuid():N}.cmd");
        File.WriteAllText(scriptPath, $"""
@echo off
more > nul
exit /b {exitCode}
""");
        return scriptPath;
    }

    private static FakeKubectl CreateFakeKubectl()
    {
        var directory = Directory.CreateTempSubdirectory("fake-kubectl-");
        var argumentsPath = Path.Combine(directory.FullName, "arguments.txt");
        if (!OperatingSystem.IsWindows())
        {
            var scriptPath = Path.Combine(directory.FullName, "kubectl");
            File.WriteAllText(scriptPath, $$"""
#!/usr/bin/env sh
printf '%s\n' "$*" > '{{argumentsPath.Replace("'", "'\"'\"'", StringComparison.Ordinal)}}'
cat >/dev/null
exit 0
""");
            File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return new FakeKubectl(directory, scriptPath, argumentsPath);
        }

        var windowsScriptPath = Path.Combine(directory.FullName, "kubectl.cmd");
        File.WriteAllText(windowsScriptPath, $$"""
@echo off
echo %* > "{{argumentsPath}}"
more > nul
exit /b 0
""");
        return new FakeKubectl(directory, windowsScriptPath, argumentsPath);
    }

    private static FakeKubectl CreatePreviewSourceResultKubectl()
    {
        var directory = Directory.CreateTempSubdirectory("fake-preview-source-kubectl-");
        var argumentsPath = Path.Combine(directory.FullName, "arguments.txt");
        var resultPath = Path.Combine(directory.FullName, "result.json");
        File.WriteAllText(resultPath, """
            {
              "data": {
                "result.json": "{\"files\":[{\"key\":\"src-app-tsx\",\"path\":\"src/App.tsx\",\"content\":\"export default function App() { return <main>Klocka</main>; }\"}]}"
              }
            }
            """);
        if (!OperatingSystem.IsWindows())
        {
            var scriptPath = Path.Combine(directory.FullName, "kubectl");
            File.WriteAllText(scriptPath, $$"""
#!/usr/bin/env sh
printf '%s\n' "$*" >> '{{argumentsPath.Replace("'", "'\"'\"'", StringComparison.Ordinal)}}'
case "$*" in
  *"get configmap"*)
    cat '{{resultPath.Replace("'", "'\"'\"'", StringComparison.Ordinal)}}'
    exit 0
    ;;
esac
echo "unexpected kubectl command: $*" >&2
exit 1
""");
            File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return new FakeKubectl(directory, scriptPath, argumentsPath);
        }

        var windowsScriptPath = Path.Combine(directory.FullName, "kubectl.cmd");
        File.WriteAllText(windowsScriptPath, $$"""
@echo off
echo %* >> "{{argumentsPath}}"
echo %* | findstr /C:"get configmap" > nul
if %errorlevel%==0 (
  type "{{resultPath}}"
  exit /b 0
)
echo unexpected kubectl command: %* 1>&2
exit /b 1
""");
        return new FakeKubectl(directory, windowsScriptPath, argumentsPath);
    }

    private static FakeKubectl CreateDelayedPreviewSourceResultKubectl()
    {
        var directory = Directory.CreateTempSubdirectory("fake-preview-source-delayed-kubectl-");
        var argumentsPath = Path.Combine(directory.FullName, "arguments.txt");
        var countPath = Path.Combine(directory.FullName, "configmap-count.txt");
        var resultPath = Path.Combine(directory.FullName, "result.json");
        File.WriteAllText(resultPath, """
            {
              "data": {
                "result.json": "{\"files\":[{\"key\":\"src-app-tsx\",\"path\":\"src/App.tsx\",\"content\":\"export default function App() { return <main>Timeout adopted</main>; }\"}]}"
              }
            }
            """);
        if (!OperatingSystem.IsWindows())
        {
            var scriptPath = Path.Combine(directory.FullName, "kubectl");
            File.WriteAllText(scriptPath, $$"""
#!/usr/bin/env sh
printf '%s\n' "$*" >> '{{argumentsPath.Replace("'", "'\"'\"'", StringComparison.Ordinal)}}'
case "$*" in
  *"get configmap"*)
    count=0
    if [ -f '{{countPath.Replace("'", "'\"'\"'", StringComparison.Ordinal)}}' ]; then
      count=$(cat '{{countPath.Replace("'", "'\"'\"'", StringComparison.Ordinal)}}')
    fi
    count=$((count + 1))
    printf '%s' "$count" > '{{countPath.Replace("'", "'\"'\"'", StringComparison.Ordinal)}}'
    if [ "$count" -ge 2 ]; then
      cat '{{resultPath.Replace("'", "'\"'\"'", StringComparison.Ordinal)}}'
      exit 0
    fi
    echo "not found" >&2
    exit 1
    ;;
  *"get job"*)
    echo "not found" >&2
    exit 1
    ;;
esac
cat >/dev/null
exit 0
""");
            File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return new FakeKubectl(directory, scriptPath, argumentsPath);
        }

        var windowsScriptPath = Path.Combine(directory.FullName, "kubectl.cmd");
        File.WriteAllText(windowsScriptPath, $$"""
@echo off
setlocal enabledelayedexpansion
echo %* >> "{{argumentsPath}}"
echo %* | findstr /C:"get configmap" > nul
if %errorlevel%==0 (
  set count=0
  if exist "{{countPath}}" set /p count=<"{{countPath}}"
  set /a count=!count!+1
  > "{{countPath}}" echo !count!
  if !count! GEQ 2 (
    type "{{resultPath}}"
    exit /b 0
  )
  echo not found 1>&2
  exit /b 1
)
echo %* | findstr /C:"get job" > nul
if %errorlevel%==0 (
  echo not found 1>&2
  exit /b 1
)
more > nul
exit /b 0
""");
        return new FakeKubectl(directory, windowsScriptPath, argumentsPath);
    }

    private sealed record FakeKubectl(DirectoryInfo Directory, string Path, string ArgumentsPath);

    private static string EndpointSnippet(string program, string startMarker, string endMarker)
    {
        var start = program.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find endpoint start marker: {startMarker}");
        var end = program.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"Could not find endpoint end marker: {endMarker}");
        return program[start..end];
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Rosenvall.DevOps.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find Rosenvall.DevOps.slnx from the current test directory.");
    }

    private sealed class DelayedHttpMessageHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]")
            };
        }
    }
}
