// ---------------------------------------------------------------------------
// CrawlStateOptions.cs
// The "CrawlState" configuration section, and the only place a connection
// string is constructed.
//
// It is built here from a server name and a database name rather than read from
// configuration as a string, and that is the whole point of the file. A
// connection string in appsettings.json is the place a password ends up: not
// today, but on the afternoon somebody cannot get Kerberos working and reaches
// for a SQL login to prove the query runs. There is no key here to paste one
// into. IntegratedSecurity is set in code and cannot be turned off by editing a
// file on the server.
//
// That also means this app has no credential to rotate, no secret in Key Vault,
// and nothing for build/SecretHygiene.targets to find. The IIS application pool
// identity IS the crawl_reader principal from sql/25 - the permission boundary
// and the process identity are the same thing, so "what can the dashboard read"
// is answered by a GRANT statement rather than by reading web-tier code.
//
// Server and Database are not sensitive. sql/01 makes the same call for the
// connector and for the same reason: a host name is not a credential, and
// pretending it is teaches people to treat the things that are as though they
// were host names.
// ---------------------------------------------------------------------------

namespace ConnectorState.Dashboard.Data;

using Microsoft.Data.SqlClient;

/// <summary>Binding target for the "CrawlState" configuration section.</summary>
public sealed class CrawlStateOptions
{
    /// <summary>The configuration section this class binds to.</summary>
    public const string SectionName = "CrawlState";

    /// <summary>Gets or sets the SQL Server host name or instance. Not sensitive.</summary>
    public string Server { get; set; } = string.Empty;

    /// <summary>Gets or sets the crawl-state database name. Not sensitive.</summary>
    public string Database { get; set; } = "ConnectorState";

    /// <summary>
    /// Gets or sets a value indicating whether the connection is encrypted.
    /// Defaults to true and should stay true: this database records which items
    /// exist in a customer's index, which is metadata worth protecting even
    /// though it holds no item content.
    /// </summary>
    public bool Encrypt { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether an untrusted server certificate is
    /// accepted. Defaults to false. Setting it true disables the check that makes
    /// <see cref="Encrypt"/> mean anything, so it is a temporary measure while a
    /// certificate is issued and not a configuration to leave in place.
    /// </summary>
    public bool TrustServerCertificate { get; set; }

    /// <summary>Gets or sets the connect timeout, in seconds.</summary>
    public int ConnectTimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// Gets or sets the command timeout, in seconds. Separate from the connect
    /// timeout for the reason SqlPushSource gives: a report over a large
    /// inventory legitimately outlives the time a connection attempt gets.
    /// </summary>
    public int CommandTimeoutSeconds { get; set; } = 60;

    /// <summary>Gets or sets the default number of rows per page in list views.</summary>
    public int DefaultPageSize { get; set; } = 50;

    /// <summary>Gets or sets the window, in hours, the front page summarises over.</summary>
    public int SummaryWindowHours { get; set; } = 24;

    /// <summary>
    /// Gets or sets the application name reported to SQL Server. It appears in
    /// sys.dm_exec_sessions, so a query from the dashboard is distinguishable
    /// from a query from the connector without guessing at host names.
    /// </summary>
    public string ApplicationName { get; set; } = "ConnectorState.Dashboard";

    /// <summary>Builds the connection string. Integrated Security, always.</summary>
    /// <returns>A connection string carrying no credential of any kind.</returns>
    public string BuildConnectionString()
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = this.Server,
            InitialCatalog = this.Database,

            // Not configurable, deliberately. See the file header.
            IntegratedSecurity = true,

            Encrypt = this.Encrypt
                ? SqlConnectionEncryptOption.Mandatory
                : SqlConnectionEncryptOption.Optional,
            TrustServerCertificate = this.TrustServerCertificate,
            ConnectTimeout = this.ConnectTimeoutSeconds,
            ApplicationName = this.ApplicationName,

            // A web tier opens and closes a connection per request. Pooling is
            // the difference between a page load and a Kerberos handshake per
            // page load.
            Pooling = true,
        };

        return builder.ConnectionString;
    }
}
