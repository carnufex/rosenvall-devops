namespace Rosenvall.DevOps.Api;

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
