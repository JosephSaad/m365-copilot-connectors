// ---------------------------------------------------------------------------
// SqlConnectionStringFactory.cs
// Builds the connection string from configuration. Pure and side effect free so
// the encryption rules can be unit tested without a SQL instance.
//
// Rules that hold for every authentication mode:
//   Encrypt=true always.
//   TrustServerCertificate=true is rejected outright in Production. If the
//   server certificate does not validate, fix the certificate.
//   No password is ever placed in a string that is logged; see Describe().
// ---------------------------------------------------------------------------

namespace SqlConnector.Security.Sql
{
    using System;
    using System.Collections.Generic;
    using Microsoft.Data.SqlClient;
    using SqlConnector.Security.Configuration;

    /// <summary>
    /// Assembles <see cref="SqlConnectionStringBuilder"/> instances for the
    /// configured authentication mode.
    /// </summary>
    public static class SqlConnectionStringFactory
    {
        /// <summary>
        /// The Application Name stamped on every connection: the entry
        /// executable's own name, so a DBA looking at sys.dm_exec_sessions sees
        /// which of the connectors a session belongs to - not all three wearing
        /// one connector's name.
        /// </summary>
        public static readonly string ApplicationName =
            System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? "SqlConnector";

        /// <summary>The environment name that enables the strict rules.</summary>
        public const string ProductionEnvironment = "Production";

        /// <summary>Scope used when acquiring an Entra access token for SQL.</summary>
        public const string SqlTokenScope = "https://database.windows.net/.default";

        /// <summary>
        /// Reports every problem with an operator supplied connection string
        /// fragment. Used both by startup validation and by the runtime path that
        /// receives a data source URL from the connection wizard.
        /// </summary>
        public static IReadOnlyList<string> InspectExtraOptions(string extraOptions, string environment)
        {
            var problems = new List<string>();

            if (string.IsNullOrWhiteSpace(extraOptions))
            {
                return problems;
            }

            SqlConnectionStringBuilder builder;
            try
            {
                builder = new SqlConnectionStringBuilder(extraOptions);
            }
            catch (ArgumentException ex)
            {
                problems.Add("is not a valid connection string fragment: " + ex.Message);
                return problems;
            }

            // ShouldSerialize, not ContainsKey: SqlConnectionStringBuilder answers
            // ContainsKey for every keyword SqlClient knows about, whether or not it
            // was actually supplied.
            if (builder.ShouldSerialize("Password"))
            {
                problems.Add(
                    "must not contain a password. Passwords are resolved from Key Vault at runtime.");
            }

            if (builder.ShouldSerialize("User ID"))
            {
                problems.Add(
                    "must not contain a user ID. Set DataSource:SqlUserId and use SqlAuthMode=SqlLogin instead.");
            }

            if (builder.TrustServerCertificate && IsProduction(environment))
            {
                problems.Add(
                    "sets TrustServerCertificate=true, which is rejected when Environment is Production. " +
                    "Install a server certificate that chains to a trusted root instead.");
            }

            return problems;
        }

        /// <summary>Returns true when the environment name is Production.</summary>
        public static bool IsProduction(string environment)
        {
            return string.Equals(environment, ProductionEnvironment, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Builds the connection string for the configured mode.
        /// </summary>
        /// <param name="dataSource">The DataSource configuration section.</param>
        /// <param name="environment">The Environment configuration value.</param>
        /// <param name="password">
        /// The SQL login password, resolved from Key Vault moments earlier. Only
        /// used when SqlAuthMode is SqlLogin. Held in the returned builder in
        /// memory only; never logged, never written to disk.
        /// </param>
        public static SqlConnectionStringBuilder Build(
            DataSourceOptions dataSource,
            string environment,
            string password)
        {
            if (dataSource == null)
            {
                throw new ArgumentNullException(nameof(dataSource));
            }

            IReadOnlyList<string> problems = InspectExtraOptions(dataSource.ExtraConnectionOptions, environment);
            if (problems.Count > 0)
            {
                throw new InvalidOperationException(
                    "DataSource:ExtraConnectionOptions is not acceptable: " + string.Join(" ", problems));
            }

            var builder = string.IsNullOrWhiteSpace(dataSource.ExtraConnectionOptions)
                ? new SqlConnectionStringBuilder()
                : new SqlConnectionStringBuilder(dataSource.ExtraConnectionOptions);

            builder.DataSource = dataSource.Server;
            builder.InitialCatalog = dataSource.Database;
            builder.ApplicationName = ApplicationName;
            builder.ConnectTimeout = dataSource.ConnectTimeoutSeconds;

            // Transient network blips on a busy on-premises instance are handled by
            // SqlClient itself before the crawl ever sees an exception.
            builder.ConnectRetryCount = dataSource.ConnectRetryCount;
            builder.ConnectRetryInterval = dataSource.ConnectRetryIntervalSeconds;

            // Encryption is not negotiable.
            builder.Encrypt = true;

            if (builder.TrustServerCertificate && IsProduction(environment))
            {
                throw new InvalidOperationException(
                    "TrustServerCertificate=true is rejected when Environment is Production. " +
                    "Install a SQL Server certificate that chains to a root the connector host trusts.");
            }

            switch (dataSource.ParsedSqlAuthMode)
            {
                case SqlAuthMode.WindowsIntegrated:
                    // The service account identity is the credential. Nothing to carry.
                    builder.IntegratedSecurity = true;
                    builder.Remove("User ID");
                    builder.Remove("Password");
                    break;

                case SqlAuthMode.EntraId:
                    // The token is attached to the SqlConnection instead, so the
                    // connection string stays free of credentials.
                    builder.IntegratedSecurity = false;
                    builder.Remove("User ID");
                    builder.Remove("Password");
                    break;

                case SqlAuthMode.SqlLogin:
                    if (string.IsNullOrWhiteSpace(dataSource.SqlUserId))
                    {
                        throw new InvalidOperationException(
                            "DataSource:SqlUserId is required when SqlAuthMode is SqlLogin.");
                    }

                    if (string.IsNullOrEmpty(password))
                    {
                        throw new InvalidOperationException(
                            "No password was resolved for SQL login '" + dataSource.SqlUserId +
                            "'. Check KeyVault:Secrets:SqlPassword and the connector's access to the vault.");
                    }

                    builder.IntegratedSecurity = false;
                    builder.UserID = dataSource.SqlUserId;
                    builder.Password = password;
                    break;

                default:
                    throw new InvalidOperationException(
                        "DataSource:SqlAuthMode must be one of EntraId, WindowsIntegrated or SqlLogin. Found '" +
                        (dataSource.SqlAuthMode ?? "(null)") + "'.");
            }

            return builder;
        }

        /// <summary>
        /// Renders the safe part of a connection: server, database and mode.
        /// This is the only form of a connection string that may be logged.
        /// </summary>
        public static string Describe(DataSourceOptions dataSource)
        {
            if (dataSource == null)
            {
                return "(no data source configured)";
            }

            return dataSource.Server + "/" + dataSource.Database + " (" + dataSource.SqlAuthMode + ")";
        }
    }
}
