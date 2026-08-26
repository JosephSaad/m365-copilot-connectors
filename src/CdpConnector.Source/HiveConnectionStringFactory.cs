// ---------------------------------------------------------------------------
// HiveConnectionStringFactory.cs
// Composes the ODBC connection string, and refuses the ones that would put a
// credential in a configuration file.
//
// The connection string is BUILT from typed settings rather than pasted in as
// one. That is not tidiness: a pasted string is where UID and PWD end up, and
// a configuration key called something like HiveConnectionString is exactly
// what the repository's secret-hygiene build gate exists to catch. Composing it
// means the shape is fixed - Kerberos, SSPI, TLS - and the only free text is
// HiveExtraOptions, which is inspected keyword by keyword before use.
//
// The refusals mirror SqlConnectionStringFactory.InspectExtraOptions, which
// does the same job for SQL Server. Two sources, one rule: a credential in
// configuration is a build failure, and a silently downgraded transport is a
// startup failure.
// ---------------------------------------------------------------------------

namespace CdpConnector.Source;

/// <summary>Builds and inspects the Hive and Impala ODBC connection string.</summary>
public static class HiveConnectionStringFactory
{
    /// <summary>Keywords that carry a credential. None of these may appear.</summary>
    private static readonly string[] CredentialKeywords =
    [
        "pwd", "password", "uid", "user", "authmech", "krbservicename", "delegationuid", "token",
    ];

    /// <summary>Keywords that weaken the transport. None of these may appear either.</summary>
    private static readonly string[] DowngradeKeywords =
    [
        "allowselfsignedservercert", "allowhostnamecnmismatch", "cacertfile", "ssl", "trustedcerts",
    ];

    /// <summary>Builds the connection string for the configured cluster.</summary>
    /// <param name="settings">Validated settings.</param>
    /// <returns>An ODBC connection string carrying no credential.</returns>
    public static string Build(CdpSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var parts = new List<string>
        {
            "Driver={" + settings.HiveDriver + "}",
            "Host=" + settings.HiveHost,
            "Port=" + settings.HivePort.ToString(System.Globalization.CultureInfo.InvariantCulture),

            // 1 = Kerberos. There is no mode here that takes a password, and
            // that is the point.
            "AuthMech=1",
            "KrbServiceName=" + settings.HiveServiceName,

            // Kerberos is not supported over the binary transport; 1 is SASL and
            // 2 is HTTP. Configuration validation has already refused anything
            // else, so this is a mapping rather than a decision.
            "ThriftTransport=" + (settings.HiveTransport == "http" ? "2" : "1"),
            "SSL=" + (settings.HiveUseSsl ? "1" : "0"),

            // Trust the Windows certificate store rather than a PEM file beside
            // the executable: the store is what the machine's own policy
            // manages, and a file is what goes stale.
            "UseSystemTrustStore=1",
        };

        if (settings.HiveTransport == "http")
        {
            parts.Add("HTTPPath=" + settings.HiveHttpPath);
        }

        if (!string.IsNullOrWhiteSpace(settings.HiveRealm))
        {
            parts.Add("KrbRealm=" + settings.HiveRealm);
        }

        // The Windows-only SSPI plugin, so the driver authenticates from the
        // logon session of the account this service runs as - a gMSA, whose
        // password Active Directory owns and this process never sees. Turning it
        // off means the driver goes to MIT Kerberos and needs a keytab, which is
        // a secret at rest and therefore a deliberate, separate mode.
        parts.Add("UseOnlySSPI=" + (settings.Kerberos == KerberosMode.Sspi ? "1" : "0"));

        if (!string.IsNullOrWhiteSpace(settings.HiveExtraOptions))
        {
            parts.Add(settings.HiveExtraOptions.Trim().TrimEnd(';'));
        }

        return string.Join(";", parts) + ";";
    }

    /// <summary>
    /// Returns a message for every keyword in the operator's extra options that
    /// must not be there. Empty means the value is usable.
    /// </summary>
    /// <param name="extraOptions">The configured extra keywords.</param>
    /// <returns>One message per problem.</returns>
    public static IReadOnlyList<string> Inspect(string extraOptions)
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(extraOptions))
        {
            return problems;
        }

        foreach (string pair in extraOptions.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=', StringComparison.Ordinal);
            string keyword = (equals < 0 ? pair : pair[..equals]).Trim().ToLowerInvariant().Replace(" ", string.Empty);

            if (keyword.Length == 0)
            {
                continue;
            }

            if (CredentialKeywords.Contains(keyword))
            {
                problems.Add(
                    $"must not set '{keyword}'. This connector authenticates with Kerberos as the service " +
                    "identity; a credential in configuration is refused here and would fail the build's secret " +
                    "hygiene gate anyway.");
            }
            else if (DowngradeKeywords.Contains(keyword))
            {
                problems.Add(
                    $"must not set '{keyword}'. TLS and certificate validation are decided by Settings:HiveUseSsl " +
                    "and the Windows trust store, not by an override that would silently accept any certificate.");
            }
        }

        return problems;
    }
}
