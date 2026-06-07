using Microsoft.Extensions.Configuration;

namespace Rosenvall.DevOps.Api;

public static class AuthenticationMode
{
    public const string Required = "Required";
    public const string DisabledForLocalDevelopment = "DisabledForLocalDevelopment";

    public sealed record Settings(string Mode, bool Enabled, string Authority, string Audience);

    public static Settings Resolve(IConfiguration configuration, bool isDevelopment)
    {
        var configuredMode = configuration["Authentication:Mode"];
        var authority = configuration["Authentication:Authority"]?.Trim() ?? string.Empty;
        var audience = configuration["Authentication:Audience"]?.Trim() ?? string.Empty;
        var mode = Normalize(configuredMode, isDevelopment, authority);

        if (string.Equals(mode, DisabledForLocalDevelopment, StringComparison.Ordinal))
        {
            if (!isDevelopment)
            {
                throw new InvalidOperationException("Authentication:Mode=DisabledForLocalDevelopment is only allowed in Development.");
            }

            return new Settings(mode, Enabled: false, authority, audience);
        }

        if (string.IsNullOrWhiteSpace(authority) || string.IsNullOrWhiteSpace(audience))
        {
            throw new InvalidOperationException("Authentication:Mode=Required requires Authentication:Authority and Authentication:Audience.");
        }

        return new Settings(mode, Enabled: true, authority, audience);
    }

    private static string Normalize(string? configuredMode, bool isDevelopment, string authority)
    {
        if (string.IsNullOrWhiteSpace(configuredMode))
        {
            return !isDevelopment || !string.IsNullOrWhiteSpace(authority)
                ? Required
                : DisabledForLocalDevelopment;
        }

        if (configuredMode.Equals(Required, StringComparison.OrdinalIgnoreCase))
        {
            return Required;
        }

        if (configuredMode.Equals(DisabledForLocalDevelopment, StringComparison.OrdinalIgnoreCase) ||
            configuredMode.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
        {
            return DisabledForLocalDevelopment;
        }

        throw new InvalidOperationException("Authentication:Mode must be Required or DisabledForLocalDevelopment.");
    }
}
