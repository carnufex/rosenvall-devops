using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Rosenvall.DevOps.Api;

public interface IRuntimeSecretStore
{
    Task<PreviewCleanupResult> StoreAsync(string secretName, IReadOnlyDictionary<string, string> data, IReadOnlyDictionary<string, string> labels, string @namespace, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<string, string>?> ReadAsync(string secretName, string @namespace, CancellationToken cancellationToken);
    Task<PreviewCleanupResult> DeleteAsync(string secretName, string @namespace, CancellationToken cancellationToken);
}

public sealed class GitHubUserAuthorizationTokenStore(IRuntimeSecretStore secrets, IConfiguration configuration)
{
    private const string DefaultNamespace = "rosenvall-devops";

    public static string SecretName(long installationId, string actorSubject)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{installationId}:{actorSubject}"))).ToLowerInvariant()[..24];
        return $"github-user-token-{hash}";
    }

    public Task<PreviewCleanupResult> StoreAsync(string secretName, GitHubUserAuthorizationTokenDto token, CancellationToken cancellationToken) =>
        secrets.StoreAsync(secretName, SecretData(token), SecretLabels(), Namespace(configuration), cancellationToken);

    public async Task<string?> ReadAccessTokenAsync(string secretName, CancellationToken cancellationToken)
    {
        var data = await secrets.ReadAsync(secretName, Namespace(configuration), cancellationToken);
        return data is not null && data.TryGetValue("access-token", out var accessToken) && !string.IsNullOrWhiteSpace(accessToken)
            ? accessToken
            : null;
    }

    public Task<PreviewCleanupResult> DeleteAsync(string secretName, CancellationToken cancellationToken) =>
        secrets.DeleteAsync(secretName, Namespace(configuration), cancellationToken);

    public static string RenderSecretPayload(string secretName, GitHubUserAuthorizationTokenDto token, string @namespace) =>
        KubernetesRuntimeSecretStore.RenderSecretPayload(secretName, SecretData(token), SecretLabels(), @namespace);

    private static IReadOnlyDictionary<string, string> SecretData(GitHubUserAuthorizationTokenDto token) => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["access-token"] = token.AccessToken,
        ["refresh-token"] = token.RefreshToken ?? "",
        ["expires-at"] = token.ExpiresAt?.ToString("O") ?? ""
    };

    private static IReadOnlyDictionary<string, string> SecretLabels() => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["app.kubernetes.io/part-of"] = "rosenvall-devops",
        ["rosenvall.devops/runtime-credential"] = "github-user-authorization"
    };

    private static string Namespace(IConfiguration configuration) =>
        configuration["GitHub:UserAuthorizationSecretNamespace"] ??
        configuration["Secrets:Namespace"] ??
        configuration["Preview:Namespace"] ??
        DefaultNamespace;
}

public sealed class KubernetesRuntimeSecretStore(IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<KubernetesRuntimeSecretStore> logger) : IRuntimeSecretStore
{
    private const string ServiceAccountTokenPath = "/var/run/secrets/kubernetes.io/serviceaccount/token";
    private const string ServiceAccountCaPath = "/var/run/secrets/kubernetes.io/serviceaccount/ca.crt";

    public async Task<PreviewCleanupResult> StoreAsync(string secretName, IReadOnlyDictionary<string, string> data, IReadOnlyDictionary<string, string> labels, string @namespace, CancellationToken cancellationToken)
    {
        var auth = KubernetesApiAuth.FromEnvironment(configuration);
        if (!auth.Configured)
        {
            return PreviewCleanupResult.Failed("Kubernetes runtime secret storage is not available in this API environment.");
        }

        try
        {
            using var getResponse = await SendAsync(auth, HttpMethod.Get, SecretPath(@namespace, secretName), null, cancellationToken);
            var payload = RenderSecretPayload(secretName, data, labels, @namespace);
            if (getResponse.StatusCode == HttpStatusCode.NotFound)
            {
                using var createContent = new StringContent(payload, Encoding.UTF8, "application/json");
                using var createResponse = await SendAsync(auth, HttpMethod.Post, SecretsPath(@namespace), createContent, cancellationToken);
                return await ResultFromResponseAsync(createResponse, "create", secretName, @namespace, cancellationToken);
            }

            if (!getResponse.IsSuccessStatusCode)
            {
                return await ResultFromResponseAsync(getResponse, "read", secretName, @namespace, cancellationToken);
            }

            using var updateContent = new StringContent(payload, Encoding.UTF8, "application/json");
            using var updateResponse = await SendAsync(auth, HttpMethod.Put, SecretPath(@namespace, secretName), updateContent, cancellationToken);
            return await ResultFromResponseAsync(updateResponse, "update", secretName, @namespace, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            logger.LogWarning(ex, "Kubernetes runtime secret write failed for {SecretName} in {Namespace}.", secretName, @namespace);
            return PreviewCleanupResult.Failed($"Kubernetes runtime secret write failed for {secretName} in {@namespace}: {ex.GetType().Name}.");
        }
    }

    public async Task<IReadOnlyDictionary<string, string>?> ReadAsync(string secretName, string @namespace, CancellationToken cancellationToken)
    {
        var auth = KubernetesApiAuth.FromEnvironment(configuration);
        if (!auth.Configured)
        {
            return null;
        }

        try
        {
            using var response = await SendAsync(auth, HttpMethod.Get, SecretPath(@namespace, secretName), null, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("data", out var dataElement) || dataElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in dataElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { } encoded)
                {
                    values[property.Name] = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                }
            }

            return values;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or FormatException or IOException or TaskCanceledException or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            logger.LogWarning(ex, "Kubernetes runtime secret read failed for {SecretName} in {Namespace}.", secretName, @namespace);
            return null;
        }
    }

    public async Task<PreviewCleanupResult> DeleteAsync(string secretName, string @namespace, CancellationToken cancellationToken)
    {
        var auth = KubernetesApiAuth.FromEnvironment(configuration);
        if (!auth.Configured)
        {
            return PreviewCleanupResult.Ok("Kubernetes runtime secret storage is not available in this API environment.");
        }

        try
        {
            using var response = await SendAsync(auth, HttpMethod.Delete, SecretPath(@namespace, secretName), null, cancellationToken);
            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound)
            {
                return PreviewCleanupResult.Ok($"Kubernetes runtime secret {secretName} deleted.");
            }

            return await ResultFromResponseAsync(response, "delete", secretName, @namespace, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            logger.LogWarning(ex, "Kubernetes runtime secret delete failed for {SecretName} in {Namespace}.", secretName, @namespace);
            return PreviewCleanupResult.Failed($"Kubernetes runtime secret delete failed for {secretName} in {@namespace}: {ex.GetType().Name}.");
        }
    }

    public static string RenderSecretPayload(string secretName, IReadOnlyDictionary<string, string> data, IReadOnlyDictionary<string, string> labels, string @namespace)
    {
        var encodedData = data.ToDictionary(
            entry => entry.Key,
            entry => Convert.ToBase64String(Encoding.UTF8.GetBytes(entry.Value)),
            StringComparer.Ordinal);
        var payload = new
        {
            apiVersion = "v1",
            kind = "Secret",
            metadata = new
            {
                name = secretName,
                @namespace,
                labels
            },
            type = "Opaque",
            data = encodedData
        };
        return JsonSerializer.Serialize(payload);
    }

    public static HttpMessageHandler CreateHttpMessageHandler()
    {
        var caPath = Environment.GetEnvironmentVariable("KUBERNETES_SERVICEACCOUNT_CA_PATH") ?? ServiceAccountCaPath;
        if (!File.Exists(caPath))
        {
            return new SocketsHttpHandler();
        }

        var root = X509CertificateLoader.LoadCertificateFromFile(caPath);
        return new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                CertificateChainPolicy = new X509ChainPolicy
                {
                    TrustMode = X509ChainTrustMode.CustomRootTrust,
                    CustomTrustStore = { root },
                    RevocationMode = X509RevocationMode.NoCheck
                }
            }
        };
    }

    private async Task<HttpResponseMessage> SendAsync(KubernetesApiAuth auth, HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("kubernetes-runtime-secrets");
        using var request = new HttpRequestMessage(method, new Uri(auth.BaseUri, path));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", auth.Token);
        request.Headers.Accept.ParseAdd("application/json");
        request.Content = content;
        return await client.SendAsync(request, cancellationToken);
    }

    private static string SecretsPath(string @namespace) =>
        $"/api/v1/namespaces/{Uri.EscapeDataString(@namespace)}/secrets";

    private static string SecretPath(string @namespace, string secretName) =>
        $"{SecretsPath(@namespace)}/{Uri.EscapeDataString(secretName)}";

    private static async Task<PreviewCleanupResult> ResultFromResponseAsync(HttpResponseMessage response, string operation, string secretName, string @namespace, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return PreviewCleanupResult.Ok($"Kubernetes runtime secret {secretName} {operation} succeeded.");
        }

        var message = await SanitizedKubernetesMessageAsync(response, cancellationToken);
        return PreviewCleanupResult.Failed($"Kubernetes runtime secret {operation} failed for {secretName} in {@namespace}: {message}");
    }

    private static async Task<string> SanitizedKubernetesMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var fallback = $"{(int)response.StatusCode} {response.ReasonPhrase}".Trim();
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(body) || body.Contains("access-token", StringComparison.OrdinalIgnoreCase))
            {
                return fallback;
            }

            using var document = JsonDocument.Parse(body);
            var reason = GetString(document.RootElement, "reason");
            var message = GetString(document.RootElement, "message");
            var sanitized = Regex.Replace(string.Join(": ", new[] { reason, message }.Where(value => !string.IsNullOrWhiteSpace(value))), @"\s+", " ").Trim();
            return string.IsNullOrWhiteSpace(sanitized)
                ? fallback
                : sanitized[..Math.Min(240, sanitized.Length)];
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return fallback;
        }
    }

    private static string? GetString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private sealed record KubernetesApiAuth(Uri BaseUri, string Token, bool Configured)
    {
        public static KubernetesApiAuth FromEnvironment(IConfiguration configuration)
        {
            var tokenPath = configuration["Kubernetes:ServiceAccountTokenPath"] ?? Environment.GetEnvironmentVariable("KUBERNETES_SERVICEACCOUNT_TOKEN_PATH") ?? ServiceAccountTokenPath;
            var host = Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST");
            var port = Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_PORT") ?? "443";
            if (string.IsNullOrWhiteSpace(host) || !File.Exists(tokenPath))
            {
                return new KubernetesApiAuth(new Uri("https://kubernetes.default.svc"), "", false);
            }

            var token = File.ReadAllText(tokenPath).Trim();
            if (string.IsNullOrWhiteSpace(token))
            {
                return new KubernetesApiAuth(new Uri("https://kubernetes.default.svc"), "", false);
            }

            return new KubernetesApiAuth(new Uri($"https://{host}:{port}"), token, true);
        }
    }
}
