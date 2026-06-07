using Microsoft.Extensions.Configuration;

namespace Rosenvall.DevOps.Api;

public static class CorsConfiguration
{
    private static readonly string[] DevelopmentDefaultOrigins = ["http://localhost:5173"];

    public static Settings Resolve(IConfiguration configuration, bool isDevelopment)
    {
        var configured = configuration
            .GetSection("Frontend:AllowedOrigins")
            .Get<string[]>()?
            .Select(origin => origin.Trim())
            .Where(origin => !string.IsNullOrWhiteSpace(origin))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];

        var origins = configured.Length > 0
            ? configured
            : isDevelopment
                ? DevelopmentDefaultOrigins
                : throw new InvalidOperationException("Frontend:AllowedOrigins must be configured outside Development.");

        foreach (var origin in origins)
        {
            ValidateOrigin(origin);
        }

        var status = configured.Length > 0 ? "Configured" : "DevelopmentDefault";
        var message = configured.Length > 0
            ? "CORS allowed origins are configured explicitly."
            : "CORS allowed origins are using the local development default.";
        return new Settings(origins, new CorsDiagnosticsDto(origins, AllowCredentials: true, status, message));
    }

    private static void ValidateOrigin(string origin)
    {
        if (origin == "*")
        {
            throw new InvalidOperationException("Frontend:AllowedOrigins cannot contain wildcard origins when credentials are enabled.");
        }

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !string.Equals(origin, uri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Frontend:AllowedOrigins contains invalid origin '{origin}'. Use scheme, host and optional port only.");
        }
    }

    public sealed record Settings(IReadOnlyList<string> AllowedOrigins, CorsDiagnosticsDto Diagnostics);
}
