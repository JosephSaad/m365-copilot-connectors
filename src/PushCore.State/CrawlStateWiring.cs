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

using Microsoft.Data.SqlClient;
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
    /// The two refusals below are the only opinions this method holds. A
    /// connection string carrying a password would put a credential in
    /// appsettings.json, which build/SecretHygiene.targets exists to prevent and
    /// which the whole repository is arranged to make unnecessary. Refusing
    /// loudly is better than connecting: the alternative is a secret in a file
    /// that is copied to every deployment host and read by anyone who can read
    /// the directory.
    ///
    /// THE SECOND REFUSAL IS NEW AND IS THE POINT OF PARSING AT ALL. A value
    /// SqlConnectionStringBuilder cannot parse used to be accepted here without
    /// comment, because a substring test has no opinion about syntax. The store
    /// was then constructed, the run started, and the failure arrived at the
    /// first connection attempt as a SqlClient exception that names neither this
    /// setting nor this file - which is as far from the mistyped character as the
    /// failure could reasonably be put. Refusing at the point of reading turns
    /// that into one sentence naming the setting, before anything else happens.
    /// </remarks>
    public static ICrawlStateStore? FromSettings(PushOptions options, ILogger log)
    {
        string connectionString = options.Setting(ConnectionStringSetting);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        SqlConnectionStringBuilder parsed;

        try
        {
            parsed = new SqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException ex)
        {
            // NOTHING FROM ex.Message IS REPEATED, and that is deliberate rather
            // than lazy. The parser quotes the offending token back at you -
            // "Keyword not supported: 'correcthorse'" - and a malformed
            // connection string is exactly where a password has ended up in
            // keyword position. Running it: "...;Password=hun;ter=x" is an
            // unquoted password holding a semicolon, an ordinary mistake, and the
            // parser's message for it is "Keyword not supported: 'ter'" - a
            // fragment of the secret. This message is logged, so it says nothing
            // the value could have coloured. The exception is kept as the inner
            // one so a debugger still has it; RedactedException.Wrap in PushHost
            // scrubs that on the way to a sink.
            throw new InvalidOperationException(
                $"Settings:{ConnectionStringSetting} is not a valid SQL Server connection string and " +
                "was refused before anything tried to use it. Correct it in appsettings.json - a working " +
                "value looks like 'Server=SQL01;Database=ConnectorState;Integrated Security=true'. " +
                "Neither the value nor the parser's own message is reproduced here, because a malformed " +
                "connection string is precisely where a password can have landed in the wrong position.",
                ex);
        }

        if (CarriesPassword(parsed))
        {
            throw new InvalidOperationException(
                $"Settings:{ConnectionStringSetting} contains a password. Crawl state is reached with " +
                "Integrated Security so the service identity is the database principal - see sql/25, which " +
                "grants that identity the crawl_writer role. Remove the password and add " +
                "'Integrated Security=true'. The value is not logged.");
        }

        log.Information("Crawl state is enabled. Delete detection, change detection and resume are available.");

        // The ORIGINAL string, not parsed.ConnectionString. The builder
        // round-trips a normalised form - reordered, requoted, and carrying every
        // default it decided to make explicit - and handing that on would mean
        // the connector connects with a string the operator never wrote and
        // cannot find in any log or ticket. Parsing here is a question asked
        // about the value, not a rewrite of it.
        return new SqlCrawlStateStore(connectionString, log);
    }

    /// <summary>Asks SQL Server's own parser whether a connection string carries a password.</summary>
    /// <param name="parsed">The configured value, already through the parser.</param>
    /// <returns>True when it carries a non-empty Password or PWD.</returns>
    /// <remarks>
    /// THIS WAS A SUBSTRING TEST, and the comment that defended it made one good
    /// point and one mistake. The good point: SqlConnectionStringBuilder THROWS
    /// on a malformed string, and a bare ArgumentException in place of the
    /// specific message above is worse than useless when a typo is exactly the
    /// thing the operator has to find. The mistake was concluding that the test
    /// therefore had to be a substring match, when the throw can simply be caught
    /// and re-stated - which is what <see cref="FromSettings"/> now does, and
    /// which leaves the operator with a better message than before rather than a
    /// worse one, because an unparseable value is now refused HERE instead of at
    /// the first connection attempt somewhere in the middle of a run.
    ///
    /// WHAT THE PARSE BUYS, measured against the shipped parser rather than
    /// assumed. The substring test refused, today, every one of these, none of
    /// which carries a credential:
    ///
    ///   Server=pwd-sql01;Database=ConnectorState;Integrated Security=true
    ///   Server=sql01;Database=PasswordVault;Integrated Security=true
    ///   Server=sql01;Application Name=PwdReset;Integrated Security=true
    ///   Server=sql01;Initial Catalog='a;Password=x';Integrated Security=true
    ///
    /// The first is not a curiosity. A host named for the credential service it
    /// runs is ordinary, and an operator whose only correct connection string is
    /// refused with "contains a password" has been told something false about a
    /// value they cannot fix. The last one is the case a substring match cannot
    /// reach at all: the text "Password=x" is there, inside a QUOTED database
    /// name, and the parser correctly reports no password.
    ///
    /// The other direction was checked and is the smaller half. Every legal
    /// spelling of the keyword was run through the builder - Password, PWD, Pwd,
    /// mixed case, padded with spaces and tabs on both sides of the '=' - and all
    /// of them contain "password" or "pwd" literally, so none slipped past the
    /// old test. The encodings that do not (fullwidth, a soft hyphen, an escaped
    /// '=', internal spaces) are rejected by the parser as unsupported keywords
    /// rather than honoured, so there is no spelling that carries a password past
    /// a substring match. What the old test let through was the malformed string,
    /// which it accepted in silence.
    ///
    /// ShouldSerialize rather than ContainsKey: the builder pre-populates every
    /// keyword it knows, so ContainsKey("Password") is true for a connection
    /// string that never mentioned one, and a check built on it would refuse
    /// everything. ShouldSerialize is false for absent and for present-but-empty,
    /// which is the right reading - "Password=" carries nothing.
    ///
    /// Where the parser draws the empty/present line is worth knowing and is not
    /// where it was assumed to be: an UNQUOTED trailing space is whitespace
    /// between tokens and gets trimmed, so "Password= " also carries nothing,
    /// while the quoted "Password=' '" is a value somebody chose and is refused.
    /// That was found by asserting the opposite and watching the test fail.
    /// </remarks>
    private static bool CarriesPassword(SqlConnectionStringBuilder parsed)
    {
        return parsed.ShouldSerialize("Password");
    }
}
