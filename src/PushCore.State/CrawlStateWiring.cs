// ---------------------------------------------------------------------------
// CrawlStateWiring.cs
// The one line each push executable adds to switch crawl state on.
//
// PushHost takes a factory rather than building the store itself, because the
// store opens SQL connections and PushCore must not reference SqlClient - the
// same rule that keeps PushCore.Sql a separate project, and the reason a
// connector reading something that is not a database can reference PushCore and
// stop there. This file is the other end of that seam.
//
// It also puts the configuration key in exactly one place. Three executables
// each reading Settings:StateConnectionString for themselves is three chances
// to spell it differently, and the failure mode of a mistyped key is not an
// error - it is a connector that silently runs without crawl state, writes
// every item on every run, and never deletes anything. That is precisely the
// behaviour this whole feature exists to end, and it would look like success.
// ---------------------------------------------------------------------------

namespace PushCore.State;

using Serilog;

/// <summary>Builds the crawl state store from a connector's configuration.</summary>
public static class CrawlStateWiring
{
    /// <summary>The Settings key holding the ConnectorState connection string.</summary>
    /// <remarks>
    /// A connection string rather than a server and database pair, because the
    /// operator may need to add Encrypt, TrustServerCertificate, Application
    /// Name or a failover partner, and a pair would mean adding a setting per
    /// option for ever. It must carry Integrated Security - see
    /// <see cref="FromSettings"/>.
    /// </remarks>
    public const string ConnectionStringSetting = "StateConnectionString";

    /// <summary>
    /// Builds the store, or returns null when no state database is configured.
    /// </summary>
    /// <param name="options">Validated configuration.</param>
    /// <param name="log">Where to report progress.</param>
    /// <returns>The store, or null to run without durable crawl memory.</returns>
    /// <remarks>
    /// Null is a supported answer and not a degraded one: it is what every
    /// release before the state store did, and the engine's behaviour without a
    /// store is unchanged from those - write everything, delete nothing.
    ///
    /// The refusal below is the only opinion this method holds. A connection
    /// string carrying a password would put a credential in appsettings.json,
    /// which build/SecretHygiene.targets exists to prevent and which the whole
    /// repository is arranged to make unnecessary. Refusing loudly is better
    /// than connecting: the alternative is a secret in a file that is copied to
    /// every deployment host and read by anyone who can read the directory.
    /// </remarks>
    public static ICrawlStateStore? FromSettings(PushOptions options, ILogger log)
    {
        string connectionString = options.Setting(ConnectionStringSetting);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        if (ContainsPassword(connectionString))
        {
            throw new InvalidOperationException(
                $"Settings:{ConnectionStringSetting} contains a password. Crawl state is reached with " +
                "Integrated Security so the service identity is the database principal - see sql/25, which " +
                "grants that identity the crawl_writer role. Remove the password and add " +
                "'Integrated Security=true'. The value is not logged.");
        }

        log.Information("Crawl state is enabled. Delete detection, change detection and resume are available.");

        return new SqlCrawlStateStore(connectionString, log);
    }

    /// <summary>Looks for a password keyword in a connection string.</summary>
    /// <param name="connectionString">The configured value.</param>
    /// <returns>True when it appears to carry a secret.</returns>
    /// <remarks>
    /// Deliberately a substring test rather than a parse. SqlConnectionStringBuilder
    /// would be more precise and would also THROW on a malformed string, turning
    /// a typo into a stack trace instead of the specific message above - and a
    /// malformed string is exactly when an operator most needs to be told which
    /// setting is wrong.
    /// </remarks>
    private static bool ContainsPassword(string connectionString)
    {
        return connectionString.Contains("password", StringComparison.OrdinalIgnoreCase)
            || connectionString.Contains("pwd", StringComparison.OrdinalIgnoreCase);
    }
}
