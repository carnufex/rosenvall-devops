using Microsoft.Extensions.Configuration;
using System.Net;
using System.Text.Json;

namespace Rosenvall.DevOps.Api;

public sealed class GitOpsStatusReader(PipelineJobOrchestrator jobs)
{
    public async Task<GitOpsApplicationsResponseDto> ReadApplicationsAsync(BoardGitOpsSettingsDto? settings, IConfiguration configuration, CancellationToken cancellationToken)
    {
        if (settings is null)
        {
            return new GitOpsApplicationsResponseDto([], "GitOps settings are not configured for this board.");
        }

        var selector = string.IsNullOrWhiteSpace(settings.ArgoApplicationSelector)
            ? ""
            : $" -l \"{settings.ArgoApplicationSelector.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
        var result = await jobs.GetOutputAsync($"get applications.argoproj.io -n {settings.ArgoNamespace}{selector} -o json", cancellationToken);
        if (!result.Succeeded)
        {
            return FromKubectlFailure(result.Message);
        }

        try
        {
            using var document = JsonDocument.Parse(result.Message);
            var applications = ParseApplicationsJson(document.RootElement, configuration["ArgoCD:BaseUrl"]);
            var message = applications.Count == 0
                ? string.IsNullOrWhiteSpace(settings.ArgoApplicationSelector)
                    ? "No ArgoCD applications were found in the configured namespace."
                    : "No ArgoCD applications matched the configured selector."
                : null;
            return new GitOpsApplicationsResponseDto(applications, message);
        }
        catch (JsonException)
        {
            return new GitOpsApplicationsResponseDto([], "kubectl returned invalid ArgoCD Application JSON.");
        }
    }

    public static IReadOnlyList<GitOpsApplicationStatusDto> ParseApplicationsJson(JsonElement root, string? argoBaseUrl = null)
    {
        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return items.EnumerateArray()
            .Select(item =>
            {
                var metadata = item.TryGetProperty("metadata", out var meta) ? meta : default;
                var status = item.TryGetProperty("status", out var stat) ? stat : default;
                var sync = status.ValueKind == JsonValueKind.Object && status.TryGetProperty("sync", out var syncElement) ? syncElement : default;
                var health = status.ValueKind == JsonValueKind.Object && status.TryGetProperty("health", out var healthElement) ? healthElement : default;
                var summary = status.ValueKind == JsonValueKind.Object && status.TryGetProperty("summary", out var summaryElement) ? summaryElement : default;
                var name = JsonString(metadata, "name", "application");
                return new GitOpsApplicationStatusDto(
                    name,
                    JsonString(metadata, "namespace", "argocd"),
                    JsonString(sync, "status", "Unknown"),
                    JsonString(health, "status", "Unknown"),
                    JsonNullableString(sync, "revision"),
                    FirstNonEmpty(JsonNullableString(health, "message"), JsonNullableString(status, "message"), "Application status read from ArgoCD."),
                    BuildArgoUrl(argoBaseUrl, name),
                    JsonDate(metadata, "creationTimestamp") ?? JsonDate(status, "reconciledAt"),
                    ExternalUrls(summary));
            })
            .OrderBy(application => application.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static GitOpsApplicationsResponseDto FromKubectlFailure(string kubectlError)
    {
        var message = kubectlError ?? "";
        if (message.Contains("doesn't have a resource type", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("the server could not find the requested resource", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("applications.argoproj.io", StringComparison.OrdinalIgnoreCase) && message.Contains("not found", StringComparison.OrdinalIgnoreCase) && !message.Contains("namespaces", StringComparison.OrdinalIgnoreCase))
        {
            return new GitOpsApplicationsResponseDto([], "ArgoCD Application CRD was not found. Install ArgoCD CRDs before reading GitOps application status.");
        }

        if (message.Contains("forbidden", StringComparison.OrdinalIgnoreCase))
        {
            return new GitOpsApplicationsResponseDto([], "The service account lacks access to read ArgoCD Application resources.");
        }

        if (message.Contains("namespaces", StringComparison.OrdinalIgnoreCase) && message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return new GitOpsApplicationsResponseDto([], "The configured ArgoCD namespace is missing.");
        }

        return new GitOpsApplicationsResponseDto([], $"Could not read ArgoCD applications: {message}");
    }

    private static string JsonString(JsonElement element, string property, string fallback) =>
        JsonNullableString(element, property) ?? fallback;

    private static string? JsonNullableString(JsonElement element, string property)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    private static DateTimeOffset? JsonDate(JsonElement element, string property) =>
        JsonNullableString(element, property) is { } value && DateTimeOffset.TryParse(value, out var parsed)
            ? parsed
            : null;

    private static IReadOnlyList<string> ExternalUrls(JsonElement summary)
    {
        if (summary.ValueKind != JsonValueKind.Object ||
            !summary.TryGetProperty("externalURLs", out var urls) ||
            urls.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return urls.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()?.Trim() ?? "")
            .Where(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                 uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? BuildArgoUrl(string? baseUrl, string name) =>
        string.IsNullOrWhiteSpace(baseUrl)
            ? null
            : $"{baseUrl.TrimEnd('/')}/applications/{WebUtility.UrlEncode(name)}";

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
}
