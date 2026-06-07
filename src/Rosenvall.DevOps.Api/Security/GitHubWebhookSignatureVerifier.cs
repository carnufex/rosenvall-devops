using System.Security.Cryptography;
using System.Text;

namespace Rosenvall.DevOps.Api;

public static class GitHubWebhookSignatureVerifier
{
    public const string SignatureHeaderName = "X-Hub-Signature-256";

    public static bool Verify(ReadOnlySpan<byte> payload, string? signatureHeader, string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret) ||
            string.IsNullOrWhiteSpace(signatureHeader) ||
            !signatureHeader.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var signatureHex = signatureHeader["sha256=".Length..].Trim();
        if (signatureHex.Length != 64)
        {
            return false;
        }

        Span<byte> expected = stackalloc byte[32];
        if (!TryParseHex(signatureHex, expected))
        {
            return false;
        }

        var computed = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payload);
        return CryptographicOperations.FixedTimeEquals(computed, expected);
    }

    private static bool TryParseHex(string value, Span<byte> destination)
    {
        if (value.Length != destination.Length * 2)
        {
            return false;
        }

        for (var index = 0; index < destination.Length; index++)
        {
            var high = FromHex(value[index * 2]);
            var low = FromHex(value[index * 2 + 1]);
            if (high < 0 || low < 0)
            {
                return false;
            }

            destination[index] = (byte)((high << 4) | low);
        }

        return true;
    }

    private static int FromHex(char value) =>
        value switch
        {
            >= '0' and <= '9' => value - '0',
            >= 'a' and <= 'f' => value - 'a' + 10,
            >= 'A' and <= 'F' => value - 'A' + 10,
            _ => -1
        };
}
