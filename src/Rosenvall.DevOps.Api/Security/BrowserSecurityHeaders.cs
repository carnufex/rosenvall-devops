using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Rosenvall.DevOps.Api;

public static class BrowserSecurityHeaders
{
    public const string ContentSecurityPolicyHeader = "Content-Security-Policy";
    public const string ContentTypeOptionsHeader = "X-Content-Type-Options";
    public const string FrameOptionsHeader = "X-Frame-Options";
    public const string ReferrerPolicyHeader = "Referrer-Policy";
    public const string PermissionsPolicyHeader = "Permissions-Policy";
    public const string StrictTransportSecurityHeader = "Strict-Transport-Security";

    public static void Apply(IHeaderDictionary headers, IConfiguration configuration, bool includeHsts)
    {
        headers[ContentSecurityPolicyHeader] = BuildContentSecurityPolicy(configuration);
        headers[ContentTypeOptionsHeader] = "nosniff";
        headers[FrameOptionsHeader] = "DENY";
        headers[ReferrerPolicyHeader] = "strict-origin-when-cross-origin";
        headers[PermissionsPolicyHeader] = "camera=(), microphone=(), geolocation=()";
        if (includeHsts)
        {
            headers[StrictTransportSecurityHeader] = "max-age=31536000; includeSubDomains";
        }
    }

    public static string BuildContentSecurityPolicy(IConfiguration configuration)
    {
        var connectSources = new SortedSet<string>(StringComparer.Ordinal)
        {
            "'self'",
            "https://api.github.com"
        };
        AddOrigin(connectSources, configuration["Authentication:Authority"]);
        foreach (var origin in configuration.GetSection("Frontend:AllowedOrigins").Get<string[]>() ?? [])
        {
            AddOrigin(connectSources, origin);
        }

        var formSources = new SortedSet<string>(StringComparer.Ordinal) { "'self'" };
        AddOrigin(formSources, configuration["Authentication:Authority"]);

        return string.Join("; ", [
            "default-src 'self'",
            "script-src 'self'",
            "style-src 'self' 'unsafe-inline'",
            "img-src 'self' data: https:",
            "font-src 'self' data:",
            $"connect-src {string.Join(' ', connectSources)}",
            "frame-ancestors 'none'",
            "base-uri 'self'",
            $"form-action {string.Join(' ', formSources)}"
        ]);
    }

    private static void AddOrigin(ISet<string> targets, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            string.IsNullOrWhiteSpace(uri.Scheme) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            return;
        }

        var builder = new UriBuilder(uri.Scheme, uri.Host, uri.IsDefaultPort ? -1 : uri.Port);
        targets.Add(builder.Uri.GetLeftPart(UriPartial.Authority).TrimEnd('/'));
    }
}
