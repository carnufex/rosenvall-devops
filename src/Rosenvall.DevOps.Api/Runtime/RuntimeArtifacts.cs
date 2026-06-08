namespace Rosenvall.DevOps.Api;

public sealed record RuntimeArtifactDto(
    Guid Id,
    string ApiVersion,
    string Kind,
    string Namespace,
    string Name,
    Guid? BoardId,
    Guid? WorkItemId,
    Guid? RunId,
    string RunKind,
    DateTimeOffset CreatedAt,
    bool Sensitive,
    string Status = "Active",
    DateTimeOffset? CleanedUpAt = null,
    string? CleanupMessage = null);

public static class RuntimeArtifactCatalog
{
    public static IReadOnlyList<RuntimeArtifactDto> ProviderSyncArtifacts(PipelineRunDto run)
    {
        var now = DateTimeOffset.UtcNow;
        return
        [
            new RuntimeArtifactDto(
                Guid.NewGuid(),
                "batch/v1",
                "Job",
                RepositoryImplementationJobManifestRenderer.Namespace,
                RepositoryProviderSyncJobManifestRenderer.JobName(run),
                run.BoardId,
                run.WorkItemId,
                run.Id,
                "ProviderSync",
                now,
                Sensitive: false),
            new RuntimeArtifactDto(
                Guid.NewGuid(),
                "v1",
                "Secret",
                RepositoryImplementationJobManifestRenderer.Namespace,
                RepositoryProviderSyncJobManifestRenderer.TokenSecretName(run),
                run.BoardId,
                run.WorkItemId,
                run.Id,
                "ProviderSync",
                now,
                Sensitive: true)
        ];
    }

    public static string RenderDeleteManifest(RuntimeArtifactDto artifact) =>
        $$"""
          apiVersion: {{artifact.ApiVersion}}
          kind: {{artifact.Kind}}
          metadata:
            name: {{artifact.Name}}
            namespace: {{artifact.Namespace}}
          """;
}
