using System.Text.RegularExpressions;

namespace Bmt.Core;

/// <summary>
/// Masks credentials AND internal infrastructure identifiers in a Mongo connection string so it can
/// appear in logs, persisted run artifacts, and the HTML report (§8.1 "masked connection string")
/// without leaking secrets or internal topology. Replaces the <c>user:password@</c> userinfo, any
/// obvious key/secret query parameters, the host/SRV name (private IPs and internal Azure cluster
/// hostnames are sensitive), and the Cosmos <c>appName</c> value (which echoes the account name).
/// The scheme, port, database path, and non-sensitive options (replicaSet, tls, authSource, ...) are
/// preserved so the string stays diagnostically useful. Idempotent: re-masking an already-masked
/// string is a no-op.
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
        // Redact the host/SRV name that immediately follows the masked credentials. Stops at the
        // port (':'), path ('/'), or query ('?') so those non-sensitive parts survive.
        masked = HostRegex().Replace(masked, "$1****");
        // Cosmos appName echoes the account name (e.g. appName=@cosmos-acct@) -> redact its value.
        masked = AppNameRegex().Replace(masked, "$1****");
        return masked;
    }

    // mongodb:// or mongodb+srv:// then user:pass@  -> capture scheme prefix in group 1.
    [GeneratedRegex(@"(mongodb(?:\+srv)?:\/\/)[^@\/]+@", RegexOptions.IgnoreCase)]
    private static partial Regex UserInfoRegex();

    // Common secret-bearing query params.
    [GeneratedRegex(@"(?i)\b(password|pwd|accountkey|sig|signature)=([^&\s]+)")]
    private static partial Regex SecretParamRegex();

    // Host/SRV name right after the masked userinfo marker (****:****@). Keeps port/path/query.
    [GeneratedRegex(@"(\*\*\*\*:\*\*\*\*@)[^:\/?\s]+")]
    private static partial Regex HostRegex();

    // appName=<value> (value may itself contain '@'), up to the next '&' or whitespace.
    [GeneratedRegex(@"(?i)(appName=)[^&\s]*")]
    private static partial Regex AppNameRegex();
}
