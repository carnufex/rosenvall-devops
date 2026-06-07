using Microsoft.Extensions.Configuration;
using System.Reflection;

namespace Rosenvall.DevOps.Api;

public static class ReleaseDiagnosticsReader
{
    public static ReleaseDiagnosticsDto Read(IConfiguration configuration)
    {
        return new ReleaseDiagnosticsDto(
            AssemblyVersion(),
            ConfigValue(configuration, "Release:CommitSha", "GITHUB_SHA", "SOURCE_VERSION"),
            ConfigValue(configuration, "Release:BuildTimestamp", "BUILD_TIMESTAMP", "BUILD_DATE"),
            ConfigValue(configuration, "Release:ApiImage", "ROSENVALL_API_IMAGE", "API_IMAGE"),
            ConfigValue(configuration, "Release:FrontendImage", "ROSENVALL_FRONTEND_IMAGE", "FRONTEND_IMAGE"),
            ConfigValue(configuration, "Release:RunnerImage", "Ai:Codex:KubernetesRunnerImage", "ROSENVALL_RUNNER_IMAGE"),
            ConfigValue(configuration, "Release:ConfigurationMode", "ASPNETCORE_ENVIRONMENT"));
    }

    private static string AssemblyVersion()
    {
        var assembly = typeof(ReleaseDiagnosticsReader).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ??
            assembly.GetName().Version?.ToString() ??
            "unknown";
    }

    private static string ConfigValue(IConfiguration configuration, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = configuration[key];
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return "unknown";
    }
}
