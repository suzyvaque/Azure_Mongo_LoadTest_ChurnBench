using System.Text.RegularExpressions;

namespace Bmt.Core;

/// <summary>
/// Masks credentials in a Mongo connection string so it can appear in logs and the HTML report
/// (§8.1 "masked connection string") without leaking secrets. Replaces the
/// <c>user:password@</c> userinfo and any obvious key/secret query parameters.
/// </summary>
public static partial class ConnectionStringMasker
{
    public static string Mask(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return string.Empty;
        }

        var masked = UserInfoRegex().Replace(connectionString, "$1****:****@");
        masked = SecretParamRegex().Replace(masked, "$1=****");
        return masked;
    }

    // mongodb:// or mongodb+srv:// then user:pass@  -> capture scheme prefix in group 1.
    [GeneratedRegex(@"(mongodb(?:\+srv)?:\/\/)[^@\/]+@", RegexOptions.IgnoreCase)]
    private static partial Regex UserInfoRegex();

    // Common secret-bearing query params.
    [GeneratedRegex(@"(?i)\b(password|pwd|accountkey|sig|signature)=([^&\s]+)")]
    private static partial Regex SecretParamRegex();
}
