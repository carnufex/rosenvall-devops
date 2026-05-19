using Rosenvall.DevOps.Core;

namespace Rosenvall.DevOps.Tests;

public sealed class DevOpsDomainTests
{
    [Fact]
    public void Work_item_type_allows_configured_status_transitions()
    {
        var type = WorkItemType.Create(
            "Feature",
            ["Todo", "In Progress", "AI Planning", "Review", "Done"],
            [
                new StatusTransition("Todo", "In Progress"),
                new StatusTransition("In Progress", "AI Planning"),
                new StatusTransition("AI Planning", "Review")
            ]);

        Assert.True(type.CanMove("Todo", "In Progress"));
        Assert.False(type.CanMove("Todo", "Review"));
    }

    [Fact]
    public void Ai_run_requires_plan_before_approval()
    {
        var run = AiRun.Start(Guid.NewGuid(), "ollama", "llama3:8b");

        Assert.Throws<InvalidOperationException>(() => run.Approve("crille"));

        run.PostPlan("Implement the endpoint and add tests.");
        run.Approve("crille");

        Assert.Equal(AiRunStatus.Approved, run.Status);
        Assert.Equal("crille", run.ApprovedBy);
    }

    [Fact]
    public void Ai_run_can_be_discarded_after_plan_is_ready()
    {
        var run = AiRun.Start(Guid.NewGuid(), "ollama", "llama3:8b");
        run.PostPlan("Implement the endpoint and add tests.");

        run.Discard("crille");

        Assert.Equal(AiRunStatus.Discarded, run.Status);
        Assert.Equal("crille", run.ApprovedBy);
    }

    [Fact]
    public void Preview_hostname_uses_safe_single_label_slug()
    {
        var hostname = PreviewHostnames.ForWorkItem("TASK-4821", "Implement OAuth2 Flow for Partner API Integrations");

        Assert.Equal("task-4821-implement-oauth2-flow-for-partner-api.rosenvall.se", hostname);
        Assert.DoesNotContain("--", hostname);
        Assert.True(hostname.Split('.')[0].Length <= 63);
    }

    [Fact]
    public void Preview_environment_defaults_to_seven_day_ttl()
    {
        var created = new DateTimeOffset(2026, 5, 19, 12, 0, 0, TimeSpan.Zero);
        var preview = PreviewEnvironment.Create(Guid.NewGuid(), "ghcr.io/rosenvall/demo@sha256:abc", created);

        Assert.Equal(created.AddDays(7), preview.ExpiresAt);
    }

    [Fact]
    public void Preview_resource_set_uses_dedicated_namespace_and_external_gateway()
    {
        var resources = PreviewResourceSet.Create(
            "TASK-4821",
            "Implement OAuth2 Flow for Partner API Integrations",
            "ghcr.io/rosenvall/demo@sha256:abc");

        Assert.Equal("devops-preview-task-4821-implement-oauth2-flow-for-partner-api", resources.Namespace);
        Assert.Equal("task-4821-implement-oauth2-flow-for-partner-api", resources.Name);
        Assert.Equal("task-4821-implement-oauth2-flow-for-partner-api.rosenvall.se", resources.Hostname);
        Assert.Equal("gateway/external", resources.ParentGateway);
    }

    [Fact]
    public void Preview_manifest_contains_deployment_service_and_httproute()
    {
        var resources = PreviewResourceSet.Create(
            "TASK-4821",
            "Implement OAuth2 Flow for Partner API Integrations",
            "ghcr.io/rosenvall/demo@sha256:abc");

        var manifest = PreviewManifestRenderer.Render(resources);

        Assert.Contains("kind: Deployment", manifest);
        Assert.Contains("kind: Service", manifest);
        Assert.Contains("kind: HTTPRoute", manifest);
        Assert.Contains("kind: Namespace", manifest);
        Assert.Contains("name: devops-preview-task-4821-implement-oauth2-flow-for-partner-api", manifest);
        Assert.Contains("task-4821-implement-oauth2-flow-for-partner-api.rosenvall.se", manifest);
        Assert.Contains("sectionName: https", manifest);
    }

    [Fact]
    public void Preview_manifest_can_run_vite_react_tailwind_project()
    {
        var resources = PreviewResourceSet.Create(
            "TASK-4825",
            "hello world",
            "ghcr.io/carnufex/rosenvall-devops-preview-base:main",
            sourceFiles:
            [
                new PreviewSourceFile("package-json", "package.json", "{\"scripts\":{\"dev\":\"vite\"}}"),
                new PreviewSourceFile("src-app-tsx", "src/App.tsx", "export default function App() { return <h1>Hello world</h1>; }"),
                new PreviewSourceFile("tailwind-config-ts", "tailwind.config.ts", "export default {};")
            ]);

        var manifest = PreviewManifestRenderer.Render(resources);

        Assert.Contains("kind: ConfigMap", manifest);
        Assert.Contains("ghcr.io/carnufex/rosenvall-devops-preview-base:main", manifest);
        Assert.Contains("initContainers:", manifest);
        Assert.Contains("npm run dev -- --host 0.0.0.0 --port 8080", manifest);
        Assert.Contains("/opt/rosenvall-preview/node_modules", manifest);
        Assert.DoesNotContain("npm install", manifest);
        Assert.Contains("path: src/App.tsx", manifest);
        Assert.Contains("path: tailwind.config.ts", manifest);
        Assert.Contains("<h1>Hello world</h1>", manifest);
        Assert.Contains("annotations:", manifest);
        Assert.Contains("rosenvall.dev/source-hash:", manifest);
    }

    [Fact]
    public void Preview_manifest_source_hash_changes_when_source_changes()
    {
        var first = PreviewResourceSet.Create(
            "TASK-4825",
            "hello world",
            "ghcr.io/carnufex/rosenvall-devops-preview-base:main",
            sourceFiles: [new PreviewSourceFile("src-app-tsx", "src/App.tsx", "export default function App() { return <h1>One</h1>; }")]);
        var second = PreviewResourceSet.Create(
            "TASK-4825",
            "hello world",
            "ghcr.io/carnufex/rosenvall-devops-preview-base:main",
            sourceFiles: [new PreviewSourceFile("src-app-tsx", "src/App.tsx", "export default function App() { return <h1>Two</h1>; }")]);

        var firstManifest = PreviewManifestRenderer.Render(first);
        var secondManifest = PreviewManifestRenderer.Render(second);

        Assert.NotEqual(ExtractSourceHash(firstManifest), ExtractSourceHash(secondManifest));
    }

    private static string ExtractSourceHash(string manifest)
    {
        var line = manifest.Split('\n').Single(entry => entry.Contains("rosenvall.dev/source-hash:", StringComparison.Ordinal));
        return line.Split(':', 2)[1].Trim();
    }
}
