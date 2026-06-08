using Microsoft.Extensions.Configuration;
using Rosenvall.DevOps.Api;

namespace Rosenvall.DevOps.Tests;

public sealed class RepositorySourceFeatureTests
{
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
        Assert.Equal("", RepositorySourceFeature.NormalizeSourceRef("feature/", "main"));
        Assert.Equal("", RepositorySourceFeature.NormalizeSourceRef("feature@{upstream}", "main"));
        Assert.Equal("", RepositorySourceFeature.NormalizeSourceRef("feature\nnext", "main"));
        Assert.Equal("", RepositorySourceFeature.NormalizeSourceRef("feature.lock", "main"));
        Assert.Equal("", RepositorySourceFeature.NormalizeSourceRef("feature with space", "main"));
        Assert.Equal("src/App.tsx", RepositorySourceFeature.EscapeSourcePathForUrl("src/App.tsx"));
        Assert.Equal("src/App%20Shell.tsx", RepositorySourceFeature.EscapeSourcePathForUrl("src/App Shell.tsx"));
        Assert.Equal(".github/workflows/ci.yml", RepositorySourceFeature.EscapeSourcePathForUrl(".github/workflows/ci.yml"));
        Assert.Equal("folder%20with%20space/file%20%231.ts", RepositorySourceFeature.EscapeSourcePathForUrl("folder with space/file #1.ts"));
        Assert.Equal("git clone https://example.test/repo.git", RepositorySourceFeature.BuildCloneCommand("https://example.test/repo.git"));
        Assert.Equal("git clone \"https://example.test/repo with spaces.git\"", RepositorySourceFeature.BuildCloneCommand("https://example.test/repo with spaces.git"));

        var internalLocalGit = RepositorySourceFeature.BuildCloneInfo(Guid.NewGuid(), "LocalGit", "http://forgejo.rosenvall-devops.svc/rdo/demo.git", null);
        Assert.Null(internalLocalGit.HumanCloneUrl);
        Assert.Equal("http://forgejo.rosenvall-devops.svc/rdo/demo.git", internalLocalGit.RunnerCloneUrl);
        Assert.True(internalLocalGit.InternalOnly);
        Assert.Equal("rdo-runner", internalLocalGit.RecommendedMode);
        Assert.Equal("", internalLocalGit.CloneCommand);

        var internalLocalGitDto = RepositorySourceFeature.BuildCloneInfoDto(new RepositoryDto(
            Guid.NewGuid(),
            "LocalGit",
            "demo",
            "http://forgejo.rosenvall-devops.svc/rdo/demo.git",
            null,
            "main",
            DateTimeOffset.UtcNow,
            "rdo"));
        Assert.Null(internalLocalGitDto.HumanCloneUrl);
        Assert.Equal("http://forgejo.rosenvall-devops.svc/rdo/demo.git", internalLocalGitDto.RunnerCloneUrl);
        Assert.Equal("rdo-runner", internalLocalGitDto.RecommendedMode);
        Assert.True(internalLocalGitDto.InternalOnly);
        Assert.Equal("", internalLocalGitDto.CloneCommand);
        Assert.Contains("only inside RDO runners", internalLocalGitDto.Explanation);

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

    [Fact]
    public void Repository_source_feature_owns_provider_sync_policy_helpers()
    {
        var actor = "Demo User";
        var boardId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var sourceId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

        var key = RepositorySourceFeature.ProviderSyncActionIdempotencyKey(actor, boardId, sourceId, " GitHub ", "My Repo!", true);

        Assert.Equal("provider-sync:demo-user:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb:github:my-repo:true", key);
        Assert.True(RepositorySourceFeature.SameProvider("LocalGit", "localgit"));
        Assert.False(RepositorySourceFeature.SameProvider("LocalGit", "GitHub"));
        Assert.Equal(new[] { "provider-sync" }, RepositorySourceFeature.ProviderSyncActionKinds);

        var quota = RepositorySourceFeature.ReadProviderSyncActionQuota(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Actions:Quotas:ProviderSync:Enabled"] = "true",
                ["Actions:Quotas:ProviderSync:MaxStartedPerActor"] = "0",
                ["Actions:Quotas:ProviderSync:WindowSeconds"] = "10"
            })
            .Build());

        Assert.True(quota.Enabled);
        Assert.Equal(1, quota.MaxStartedPerActor);
        Assert.Equal(TimeSpan.FromSeconds(60), quota.Window);
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
        var root = FindRepositoryRoot();
        var program = File.ReadAllText(Path.Combine(root, "src", "Rosenvall.DevOps.Api", "Program.cs"));
        var featurePath = Path.Combine(root, "src", "Rosenvall.DevOps.Api", "Features", "Source", "RepositorySourceFeature.cs");
        var endpointPath = Path.Combine(root, "src", "Rosenvall.DevOps.Api", "Features", "Source", "RepositorySourceEndpoints.cs");

        Assert.Contains("RepositorySourceEndpoints.Map(api);", program);
        Assert.True(File.Exists(featurePath), "Repository Source helpers should live in Features/Source/RepositorySourceFeature.cs.");
        Assert.True(File.Exists(endpointPath), "Repository Source read endpoints should live in Features/Source/RepositorySourceEndpoints.cs.");
        var feature = File.ReadAllText(featurePath);
        var endpoints = File.ReadAllText(endpointPath);
        Assert.Contains("ReadResultAsync", feature);
        Assert.Contains("catch (RepositorySourceProviderException", feature);
        Assert.Contains("RepositorySourceFeature.NormalizeSourcePath", endpoints);
        Assert.Contains("RepositorySourceFeature.NormalizeSourceRef", endpoints);
        Assert.Contains("RepositorySourceFeature.BuildBoardSourceRepositories", endpoints);
        Assert.Contains("RepositorySourceFeature.BuildCloneInfoDto", endpoints);
        Assert.Contains("RepositorySourceFeature.ReadResultAsync", endpoints);
        Assert.Contains("if (!CanViewBoardRequest(store, boardId, user))", endpoints);
        Assert.Contains("if (!CanViewRepositoryRequest(store, repositoryId, user))", endpoints);
        Assert.Contains("api.MapPost(\"/boards/{boardId:guid}/repositories/sync-to-provider\"", endpoints);
        Assert.Contains("RepositorySourceFeature.NormalizeTargetProvider", endpoints);
        Assert.Contains("RepositorySourceFeature.ProviderSyncActionIdempotencyKey", endpoints);
        Assert.Contains("RepositorySourceFeature.ReadProviderSyncActionQuota", endpoints);
        Assert.Contains("RepositorySourceFeature.BuildProviderSyncPipelineRunRequest", endpoints);
        Assert.DoesNotContain("api.MapGet(\"/boards/{boardId:guid}/source/repositories\"", program);
        Assert.DoesNotContain("api.MapGet(\"/repositories/{repositoryId:guid}/source/tree\"", program);
        Assert.DoesNotContain("api.MapGet(\"/repositories/{repositoryId:guid}/source/file\"", program);
        Assert.DoesNotContain("api.MapGet(\"/repositories/{repositoryId:guid}/clone-info\"", program);
        Assert.DoesNotContain("api.MapPost(\"/boards/{boardId:guid}/repositories/sync-to-provider\"", program);
        Assert.DoesNotContain("RepositorySourceReadResultAsync", program);
        Assert.DoesNotContain("new RepositorySourceRepositoryDto", program);
        Assert.DoesNotContain("new RepositoryCloneInfoDto", program);
        Assert.DoesNotContain("static string NormalizeApiSourcePath", program);
        Assert.DoesNotContain("static string BuildCloneCommand", program);
        Assert.DoesNotContain("static string ProviderSyncActionIdempotencyKey", program);
        Assert.DoesNotContain("private static string ProviderSyncIdempotencyKey", program);
    }

    [Fact]
    public void Local_pull_request_diff_endpoint_maps_provider_failures_to_source_problems()
    {
        var root = FindRepositoryRoot();
        var program = File.ReadAllText(Path.Combine(root, "src", "Rosenvall.DevOps.Api", "Program.cs"));

        Assert.Contains("api.MapGet(\"/work-items/{workItemId:guid}/pull-request/diff\"", program);
        Assert.Contains("catch (RepositorySourceProviderException ex)", program);
        Assert.Contains("RepositorySourceFeature.ProviderRejectedRequest(ex.Provider", program);
        Assert.Contains("catch (JsonException ex)", program);
        Assert.Contains("RepositorySourceFeature.ProviderBadResponse(\"LocalGit\", ex.Message)", program);
        Assert.Contains("catch (HttpRequestException ex)", program);
        Assert.Contains("RepositorySourceFeature.ProviderUnavailable(\"LocalGit\", ex.Message)", program);
    }

    [Fact]
    public void Provider_sync_monitor_is_registered_and_uses_provider_sync_job_names()
    {
        var root = FindRepositoryRoot();
        var program = File.ReadAllText(Path.Combine(root, "src", "Rosenvall.DevOps.Api", "Program.cs"));
        var runtimePath = Path.Combine(root, "src", "Rosenvall.DevOps.Api", "Runtime", "Monitors", "ProviderSyncRunMonitor.cs");

        Assert.Contains("builder.Services.AddHostedService<ProviderSyncRunMonitor>();", program);
        Assert.DoesNotContain("public sealed class ProviderSyncRunMonitor", program);
        Assert.True(File.Exists(runtimePath), "Provider sync monitoring should live in Runtime/Monitors/ProviderSyncRunMonitor.cs instead of Program.cs.");

        var runtime = File.ReadAllText(runtimePath);
        Assert.Contains("public sealed class ProviderSyncRunMonitor", runtime);
        Assert.Contains("RepositoryProviderSyncJobManifestRenderer.JobName(run)", runtime);
        Assert.Contains("store.MarkPipelineRunSucceeded(run.Id", runtime);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Rosenvall.DevOps.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
    }
}
