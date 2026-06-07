using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using System.Net;

namespace Rosenvall.DevOps.Api;

public static class RepositorySourceFeature
{
    public const string UnsupportedSourceProviderMessage = "Source view is available for LocalGit and GitHub repositories.";
    public const string InvalidSourcePathMessage = "Invalid source path.";
    public const string InvalidSourceRefMessage = "Invalid source ref.";
    public const string RequiredSourcePathMessage = "Source file path is required.";
    public const string InvalidTargetProviderMessage = "Target provider must be LocalGit or GitHub.";
    public const string GitHubSourceUnavailableMessage = "GitHub source access is unavailable. Sync the GitHub App installation first.";
    public static readonly IReadOnlyList<string> ProviderSyncActionKinds = ["provider-sync"];

    public static string NormalizeSourcePath(string? value)
    {
        var path = (value ?? "").Replace('\\', '/').Trim('/');
        if (path.Contains("..", StringComparison.Ordinal) || path.StartsWith("/", StringComparison.Ordinal))
        {
            return "";
        }

        return string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries));
    }

    public static string EscapeSourcePathForUrl(string? value)
    {
        var normalized = NormalizeSourcePath(value);
        return string.Join('/', normalized
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString));
    }

    public static string NormalizeSourceRef(string? value, string? fallback)
    {
        var reference = string.IsNullOrWhiteSpace(value)
            ? fallback?.Trim() ?? "main"
            : value.Trim();
        return IsValidSourceRef(reference) ? reference : "";
    }

    public static bool IsValidSourceRef(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 200)
        {
            return false;
        }

        if (value.Length == 40 && value.All(Uri.IsHexDigit))
        {
            return true;
        }

        if (value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            return false;
        }

        if (value is "@" ||
            value.Contains("..", StringComparison.Ordinal) ||
            value.Contains("//", StringComparison.Ordinal) ||
            value.Contains("@{", StringComparison.Ordinal) ||
            value.StartsWith("/", StringComparison.Ordinal) ||
            value.EndsWith("/", StringComparison.Ordinal) ||
            value.EndsWith(".", StringComparison.Ordinal) ||
            value.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) ||
            value.IndexOfAny(['~', '^', ':', '?', '*', '[', '\\']) >= 0)
        {
            return false;
        }

        return value
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .All(part => !part.StartsWith(".", StringComparison.Ordinal) &&
                !part.EndsWith(".lock", StringComparison.OrdinalIgnoreCase));
    }

    public static string BuildCloneCommand(string remoteUrl)
    {
        var trimmed = remoteUrl.Trim();
        return trimmed.Any(char.IsWhiteSpace)
            ? $"git clone \"{trimmed.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : $"git clone {trimmed}";
    }

    public static RepositoryCloneInfo BuildCloneInfo(Guid repositoryId, string provider, string remoteUrl, string? webUrl)
    {
        var runnerCloneUrl = remoteUrl.Trim();
        var normalizedWebUrl = string.IsNullOrWhiteSpace(webUrl) ? null : webUrl.Trim();
        var internalOnly = provider.Equals("LocalGit", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(normalizedWebUrl);
        var humanCloneUrl = internalOnly ? null : runnerCloneUrl;
        var explanation = internalOnly
            ? "Clone is available only inside RDO runners or the cluster network."
            : "Use the human clone URL from your workstation.";

        return new RepositoryCloneInfo(
            repositoryId,
            provider,
            humanCloneUrl,
            runnerCloneUrl,
            normalizedWebUrl,
            humanCloneUrl is null ? "" : BuildCloneCommand(humanCloneUrl),
            internalOnly,
            internalOnly ? "rdo-runner" : "human",
            explanation);
    }

    public static string NormalizeTargetProvider(string? provider) =>
        provider?.Trim() switch
        {
            { } value when value.Equals("LocalGit", StringComparison.OrdinalIgnoreCase) => "LocalGit",
            { } value when value.Equals("GitHub", StringComparison.OrdinalIgnoreCase) => "GitHub",
            _ => ""
        };

    public static bool SameProvider(string? sourceProvider, string? targetProvider) =>
        !string.IsNullOrWhiteSpace(sourceProvider) &&
        !string.IsNullOrWhiteSpace(targetProvider) &&
        sourceProvider.Trim().Equals(targetProvider.Trim(), StringComparison.OrdinalIgnoreCase);

    public static string ProviderSyncActionIdempotencyKey(string actorSubject, Guid boardId, Guid sourceRepositoryId, string targetProvider, string targetName, bool isPrivate) =>
        $"provider-sync:{NormalizeActionKeyPart(actorSubject, "local-dev")}:{boardId:N}:{sourceRepositoryId:N}:{NormalizeActionKeyPart(targetProvider, "provider")}:{NormalizeActionKeyPart(targetName, "repository")}:{isPrivate.ToString().ToLowerInvariant()}";

    public static ExpensiveActionQuotaOptions ReadProviderSyncActionQuota(IConfiguration configuration)
    {
        var enabled = configuration.GetValue("Actions:Quotas:ProviderSync:Enabled", true);
        var maxStartedPerActor = Math.Max(1, configuration.GetValue("Actions:Quotas:ProviderSync:MaxStartedPerActor", 4));
        var windowSeconds = Math.Max(60, configuration.GetValue("Actions:Quotas:ProviderSync:WindowSeconds", 900));
        return new ExpensiveActionQuotaOptions(enabled, maxStartedPerActor, TimeSpan.FromSeconds(windowSeconds));
    }

    public static string ProviderSyncRepositoryDescription(RepositoryDto source) =>
        $"Synced from {source.Provider} / {source.Owner}/{source.Name}.";

    public static CreateLocalGitRepositoryRequest BuildLocalGitProviderSyncCreateRequest(SyncRepositoryToProviderRequest request, RepositoryDto source) =>
        new(
            request.TargetName,
            request.Private,
            ProviderSyncRepositoryDescription(source),
            source.ImplementationProfile,
            ImplementationWorkflow: source.ImplementationWorkflow);

    public static CreateGitHubRepositoryRequest BuildGitHubProviderSyncCreateRequest(long installationId, SyncRepositoryToProviderRequest request, RepositoryDto source, GitHubIntegrationDto integration) =>
        new(
            installationId,
            request.TargetName,
            request.Private,
            ProviderSyncRepositoryDescription(source),
            integration.AccountLogin,
            source.ImplementationProfile,
            ImplementationWorkflow: source.ImplementationWorkflow);

    public static CreateRepositoryRequest BuildProviderSyncTargetRepositoryCreateRequest(RepositoryDto targetTemplate, RepositoryDto source) =>
        new(
            targetTemplate.Provider,
            targetTemplate.Name,
            targetTemplate.RemoteUrl,
            targetTemplate.DefaultBranch,
            targetTemplate.WebUrl,
            targetTemplate.Owner,
            source.ImplementationProfile,
            source.ImplementationWorkflow);

    public static LinkBoardRepositoryRequest BuildProviderSyncBoardLinkRequest(RepositoryDto target, RepositoryDto source) =>
        new(target.Id, false, source.ImplementationProfile, "PendingSync");

    public static RecordPipelineRunRequest BuildProviderSyncPipelineRunRequest(Guid boardId, RepositoryDto source, RepositoryDto target) =>
        new(source.Id, boardId, null, "ProviderSync", "Queued", $"Syncing {source.Name} to {target.Provider}.", target.WebUrl ?? target.RemoteUrl, TargetRepositoryId: target.Id);

    public static bool IsSourceReadableProvider(string? provider) =>
        provider?.Trim() switch
        {
            { } value when value.Equals("LocalGit", StringComparison.OrdinalIgnoreCase) => true,
            { } value when value.Equals("GitHub", StringComparison.OrdinalIgnoreCase) => true,
            _ => false
        };

    public static string? SourceUnavailableReason(string? provider) =>
        IsSourceReadableProvider(provider) ? null : UnsupportedSourceProviderMessage;

    public static IResult Problem(RepositorySourceProblem problem) =>
        Results.Problem(problem.Message, statusCode: problem.StatusCode);

    public static RepositorySourceProblem ProviderUnavailable(string provider, string? detail = null) =>
        new($"{NormalizeProviderName(provider)} source provider is unavailable{FormatDetail(detail)}", StatusCodes.Status503ServiceUnavailable);

    public static RepositorySourceProblem ProviderBadResponse(string provider, string? detail = null) =>
        new($"{NormalizeProviderName(provider)} source provider returned an unreadable response{FormatDetail(detail)}", StatusCodes.Status502BadGateway);

    public static RepositorySourceProblem ProviderRejectedRequest(string provider, int statusCode, string? detail = null) =>
        new($"{NormalizeProviderName(provider)} source provider rejected the request with HTTP {statusCode}{FormatDetail(detail)}", StatusCodes.Status502BadGateway);

    public static async Task<string?> ProviderResponseDetailAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            return body.Length > 400 ? body[..400] : body;
        }
        catch
        {
            return null;
        }
    }

    public static RepositorySourceProblem InvalidSourcePath() =>
        new(InvalidSourcePathMessage, StatusCodes.Status400BadRequest);

    public static RepositorySourceProblem RequiredSourcePath() =>
        new(RequiredSourcePathMessage, StatusCodes.Status400BadRequest);

    public static IResult InvalidSourceRefProblem() =>
        Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["ref"] = [InvalidSourceRefMessage]
        }, statusCode: StatusCodes.Status400BadRequest);

    public static RepositorySourceProblem UnsupportedSourceProvider() =>
        new(UnsupportedSourceProviderMessage, StatusCodes.Status400BadRequest);

    public static RepositorySourceProblem InvalidTargetProvider() =>
        new(InvalidTargetProviderMessage, StatusCodes.Status400BadRequest);

    public static RepositorySourceProblem GitHubSourceUnavailable() =>
        new(GitHubSourceUnavailableMessage, StatusCodes.Status503ServiceUnavailable);

    private static string NormalizeProviderName(string? provider) =>
        string.IsNullOrWhiteSpace(provider) ? "Repository" : provider.Trim();

    private static string FormatDetail(string? detail) =>
        string.IsNullOrWhiteSpace(detail) ? "." : $": {detail.Trim()}";

    private static string NormalizeActionKeyPart(string? value, string fallback)
    {
        var input = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        var normalized = new string(input
            .ToLowerInvariant()
            .Select(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'
                ? character
                : '-')
            .ToArray())
            .Trim('-', '.', '_');
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }
}

public sealed record RepositorySourceProblem(string Message, int StatusCode);

public sealed record RepositoryCloneInfo(
    Guid RepositoryId,
    string Provider,
    string? HumanCloneUrl,
    string RunnerCloneUrl,
    string? WebUrl,
    string CloneCommand,
    bool InternalOnly,
    string RecommendedMode,
    string Explanation);

public sealed class RepositorySourceProviderException(
    string provider,
    HttpStatusCode? statusCode,
    string? detail = null,
    Exception? innerException = null)
    : Exception(BuildMessage(provider, statusCode, detail), innerException)
{
    public string Provider { get; } = provider;
    public HttpStatusCode? StatusCode { get; } = statusCode;
    public string? Detail { get; } = detail;

    private static string BuildMessage(string provider, HttpStatusCode? statusCode, string? detail)
    {
        var prefix = statusCode is { } code
            ? $"{provider} source read failed with HTTP {(int)code}"
            : $"{provider} source read failed";
        return string.IsNullOrWhiteSpace(detail) ? prefix : $"{prefix}: {detail.Trim()}";
    }
}
