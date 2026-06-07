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
}
