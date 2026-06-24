using Microsoft.Extensions.Configuration;

namespace Rosenvall.DevOps.Api;

public static class KubernetesFailureClassifier
{
    public static string Classify(string? message)
    {
        var text = string.IsNullOrWhiteSpace(message) ? "Kubernetes job submission failed." : message.Trim();
        if (text.Contains("bwrap:", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("bubblewrap", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("No permissions to create a new namespace", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("unprivileged user namespaces", StringComparison.OrdinalIgnoreCase))
        {
            return "Codex runner sandbox is unavailable in this Kubernetes runner.";
        }

        if (text.Contains("OOMKilled", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("exit code: 137", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Exit Code: 137", StringComparison.OrdinalIgnoreCase))
        {
            return "Kubernetes reported OOMKilled. The container exceeded its memory limit.";
        }

        if (text.Contains("Insufficient memory", StringComparison.OrdinalIgnoreCase))
        {
            return "Kubernetes could not schedule the job because of insufficient memory.";
        }

        if (text.Contains("Insufficient cpu", StringComparison.OrdinalIgnoreCase))
        {
            return "Kubernetes could not schedule the job because of insufficient CPU.";
        }

        if (text.Contains("forbidden", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("RBAC", StringComparison.OrdinalIgnoreCase))
        {
            return "Kubernetes RBAC denied the operation.";
        }

        if (text.Contains("ImagePullBackOff", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("ErrImagePull", StringComparison.OrdinalIgnoreCase))
        {
            return "Kubernetes image pull failed for the runner job.";
        }

        if (text.Contains("FailedAttachVolume", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Multi-Attach", StringComparison.OrdinalIgnoreCase))
        {
            return "Kubernetes could not attach the runner's RWO PVC. The job must run on the same node as the API pod or avoid the shared Codex PVC.";
        }

        if (text.Contains("quota", StringComparison.OrdinalIgnoreCase))
        {
            return "Kubernetes resource quota blocked the runner job.";
        }

        return text;
    }
}

public static class CodexKubernetesRunner
{
    public const string DefaultSandboxMode = "danger-full-access";
    public const string DefaultRunnerImage = "ghcr.io/carnufex/rosenvall-devops-api:main";

    public static string SandboxMode(IConfiguration configuration) =>
        NormalizeSandboxMode(configuration["Ai:Codex:KubernetesSandboxMode"]);

    public static string RunnerImage(IConfiguration configuration) =>
        NormalizeRunnerImage(configuration["Ai:Codex:KubernetesRunnerImage"]);

    public static string NormalizeRunnerImage(string? value) =>
        string.IsNullOrWhiteSpace(value) ? DefaultRunnerImage : value.Trim();

    public static string NormalizeSandboxMode(string? value)
    {
        if (string.Equals(value, "workspace-write", StringComparison.OrdinalIgnoreCase))
        {
            return "workspace-write";
        }

        if (string.Equals(value, "danger-full-access", StringComparison.OrdinalIgnoreCase))
        {
            return "danger-full-access";
        }

        return DefaultSandboxMode;
    }

    // Builds the in-job agent command for the implementation runner. Codex and Claude
    // share the same job: clone repo, write prompt.md, run this command (which edits the
    // working tree), then test/commit/push. The command reads the prompt on stdin and
    // uses the CODEX_MODEL / ROSENVALL_CODEX_SESSION_ID env the manifest already sets.
    public static string BuildAgentCommand(string? provider, string? sandboxMode, string? providerSessionId, bool chdirWorkspace = false)
    {
        var hasSession = !string.IsNullOrWhiteSpace(providerSessionId);
        if (string.Equals(provider?.Trim(), "claude", StringComparison.OrdinalIgnoreCase))
        {
            // The job runs as a non-root user in an isolated pod, so
            // --dangerously-skip-permissions is the analogue of Codex's
            // approval_policy=never + sandbox: full autonomy, no human approval.
            // Claude edits its current directory, so cd into the workspace when the
            // job builds output there (preview source) rather than in a cloned repo.
            var chdir = chdirWorkspace ? "cd \"$workspace\" && " : "";
            return hasSession
                ? $"{chdir}claude --print --dangerously-skip-permissions --resume \"$ROSENVALL_CODEX_SESSION_ID\" --model \"$CODEX_MODEL\" < \"$workspace/prompt.md\""
                : $"{chdir}claude --print --dangerously-skip-permissions --model \"$CODEX_MODEL\" < \"$workspace/prompt.md\"";
        }

        var codexSandbox = NormalizeSandboxMode(sandboxMode);
        var workspaceDir = chdirWorkspace ? "-C \"$workspace\" " : "";
        return hasSession
            ? $"codex exec resume --ephemeral --ignore-user-config --ignore-rules --skip-git-repo-check --sandbox {codexSandbox} -c \"approval_policy=\\\"never\\\"\" -m \"$CODEX_MODEL\" -c \"model_reasoning_effort=$CODEX_REASONING_EFFORT\" {workspaceDir}\"$ROSENVALL_CODEX_SESSION_ID\" - < \"$workspace/prompt.md\""
            : $"codex exec --ephemeral --ignore-user-config --ignore-rules --skip-git-repo-check --sandbox {codexSandbox} -c \"approval_policy=\\\"never\\\"\" -m \"$CODEX_MODEL\" -c \"model_reasoning_effort=$CODEX_REASONING_EFFORT\" {workspaceDir}- < \"$workspace/prompt.md\"";
    }
}
