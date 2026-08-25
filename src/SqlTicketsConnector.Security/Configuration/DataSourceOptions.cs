// ---------------------------------------------------------------------------
// DataSourceOptions.cs
// The "DataSource" section. Server and database names are not sensitive; a
// password never appears here under any authentication mode.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Security.Configuration
{
    using System;

    /// <summary>How the connector authenticates to SQL Server, in order of preference.</summary>
    public enum SqlAuthMode
    {
        /// <summary>Not configured. Startup fails.</summary>
        Unspecified = 0,

        /// <summary>
        /// Entra ID with the same certificate credential, supplied through
        /// SqlConnection.AccessToken. Requires Azure SQL or an Entra-enabled SQL Server.
        /// </summary>
        EntraId = 1,

        /// <summary>
        /// Windows integrated authentication using the service account identity.
        /// No credential appears in the connection string at all.
        /// </summary>
        WindowsIntegrated = 2,

        /// <summary>
        /// SQL login whose password is resolved from Key Vault at runtime.
        /// Last resort; requires KeyVault:Secrets:SqlPassword and DataSource:SqlUserId.
        /// </summary>
        SqlLogin = 3,
    }

    /// <summary>
    /// Binding target for the "DataSource" configuration section.
    /// </summary>
    public sealed class DataSourceOptions
    {
        /// <summary>Hard platform ceiling for a single crawl item.</summary>
        public const int PlatformItemLimitBytes = 4 * 1024 * 1024;

        /// <summary>Gets or sets the SQL Server host name. Not sensitive.</summary>
        public string Server { get; set; } = string.Empty;

        /// <summary>Gets or sets the database name. Not sensitive.</summary>
        public string Database { get; set; } = string.Empty;

        /// <summary>Gets or sets the SQL authentication mode.</summary>
        public string SqlAuthMode { get; set; } = "WindowsIntegrated";

        /// <summary>Gets or sets the truncation threshold for item content, in bytes.</summary>
        public int MaxContentBytes { get; set; } = 3670016;

        /// <summary>
        /// Gets or sets the composite format string used to build an item URL from
        /// the ticket ID, for example "https://tickets.contoso.com/ticket/{0}".
        /// </summary>
        public string ItemUrlTemplate { get; set; } = "https://tickets.contoso.com/ticket/{0}";

        /// <summary>
        /// Gets or sets the SQL login name, used only when SqlAuthMode is SqlLogin.
        /// A login name is not a secret; its password lives in Key Vault.
        /// </summary>
        public string SqlUserId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets optional extra connection string keywords, for example
        /// "Application Intent=ReadOnly". Validated at startup: credentials and
        /// TrustServerCertificate are rejected here.
        /// </summary>
        public string ExtraConnectionOptions { get; set; } = string.Empty;

        /// <summary>Gets or sets the SqlClient transparent connection retry count.</summary>
        public int ConnectRetryCount { get; set; } = 3;

        /// <summary>Gets or sets the SqlClient transparent connection retry interval, in seconds.</summary>
        public int ConnectRetryIntervalSeconds { get; set; } = 10;

        /// <summary>Gets or sets the connection timeout, in seconds.</summary>
        public int ConnectTimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// Gets or sets the timeout for query execution, in seconds. Zero means
        /// unlimited. Separate from ConnectTimeoutSeconds deliberately: connecting
        /// should fail fast, but a full-corpus read of a large view legitimately
        /// runs long, and reusing the connect timeout as the command timeout kills
        /// such a read mid-stream.
        /// </summary>
        public int CommandTimeoutSeconds { get; set; } = 600;

        /// <summary>
        /// Gets or sets a value indicating whether dbo.Tickets carries the IsDeleted
        /// column. When false the incremental crawl cannot report deletes and they
        /// are only picked up by the next periodic full crawl.
        /// </summary>
        public bool SoftDeleteEnabled { get; set; } = true;

        /// <summary>Gets the parsed SQL authentication mode.</summary>
        public SqlAuthMode ParsedSqlAuthMode
        {
            get
            {
                if (string.Equals(this.SqlAuthMode, "EntraId", StringComparison.OrdinalIgnoreCase))
                {
                    return Configuration.SqlAuthMode.EntraId;
                }

                if (string.Equals(this.SqlAuthMode, "WindowsIntegrated", StringComparison.OrdinalIgnoreCase))
                {
                    return Configuration.SqlAuthMode.WindowsIntegrated;
                }

                if (string.Equals(this.SqlAuthMode, "SqlLogin", StringComparison.OrdinalIgnoreCase))
                {
                    return Configuration.SqlAuthMode.SqlLogin;
                }

                return Configuration.SqlAuthMode.Unspecified;
            }
        }

        /// <summary>Gets a value indicating whether this configuration needs a Key Vault secret.</summary>
        public bool RequiresVaultSecret
        {
            get { return this.ParsedSqlAuthMode == Configuration.SqlAuthMode.SqlLogin; }
        }

        /// <summary>Adds a message for every invalid field rather than stopping at the first.</summary>
        public void Validate(ValidationErrors errors, string path, string environment)
        {
            if (errors == null)
            {
                throw new ArgumentNullException(nameof(errors));
            }

            errors.RequireNonEmpty(path + ":Server", this.Server);
            errors.RequireNonEmpty(path + ":Database", this.Database);
            errors.RequireOneOf(
                path + ":SqlAuthMode",
                this.SqlAuthMode,
                "EntraId",
                "WindowsIntegrated",
                "SqlLogin");

            errors.RequireRange(path + ":MaxContentBytes", this.MaxContentBytes, 1024, PlatformItemLimitBytes);
            errors.RequireRange(path + ":ConnectRetryCount", this.ConnectRetryCount, 0, 255);
            errors.RequireRange(path + ":ConnectRetryIntervalSeconds", this.ConnectRetryIntervalSeconds, 1, 60);
            errors.RequireRange(path + ":ConnectTimeoutSeconds", this.ConnectTimeoutSeconds, 5, 300);
            errors.RequireRange(path + ":CommandTimeoutSeconds", this.CommandTimeoutSeconds, 0, 86400);

            if (this.ParsedSqlAuthMode == Configuration.SqlAuthMode.SqlLogin)
            {
                errors.RequireNonEmpty(path + ":SqlUserId", this.SqlUserId);
            }
            else if (!string.IsNullOrWhiteSpace(this.SqlUserId))
            {
                errors.Add(
                    path + ":SqlUserId",
                    "must be empty unless SqlAuthMode is SqlLogin.");
            }

            if (!string.IsNullOrWhiteSpace(this.ExtraConnectionOptions))
            {
                foreach (string problem in Sql.SqlConnectionStringFactory.InspectExtraOptions(
                    this.ExtraConnectionOptions,
                    environment))
                {
                    errors.Add(path + ":ExtraConnectionOptions", problem);
                }
            }
        }
    }
}
