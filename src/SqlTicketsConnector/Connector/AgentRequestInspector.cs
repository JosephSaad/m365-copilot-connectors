// ---------------------------------------------------------------------------
// AgentRequestInspector.cs
// The agent forwards whatever the search admin typed into the connection wizard.
// Configuration on this host is authoritative for how SQL is reached, so what
// arrives from the wizard is inspected, reported, and otherwise ignored.
//
// Nothing from AuthenticationData is ever logged by value: the type of
// authentication is logged, never a user name and never a secret.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Connector
{
    using System;
    using System.Collections.Generic;
    using Microsoft.Data.SqlClient;
    using Microsoft.Graph.Connectors.Contracts.Grpc;
    using Serilog;
    using SqlConnector.Security.Sql;
    using SqlTicketsConnector.Server;

    /// <summary>Validates and reports on the connection data supplied by the agent.</summary>
    public static class AgentRequestInspector
    {
        /// <summary>
        /// Returns the problems that must fail the call. In Production a data
        /// source URL that weakens transport security or carries a credential is
        /// rejected; elsewhere it is logged as a warning.
        /// </summary>
        public static IReadOnlyList<string> Inspect(
            AuthenticationData authenticationData,
            ConnectorOptions options,
            ILogger logger)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            ILogger log = logger ?? Log.Logger;
            var problems = new List<string>();

            if (authenticationData == null)
            {
                return problems;
            }

            log.Information(
                "Agent supplied {AuthType} connection data. SQL access uses {SqlAuthMode} from configuration on this host.",
                authenticationData.AuthType,
                options.DataSource == null ? "(unset)" : options.DataSource.SqlAuthMode);

            if (authenticationData.AuthType == AuthenticationData.Types.AuthenticationType.Basic ||
                authenticationData.AuthType == AuthenticationData.Types.AuthenticationType.Oauth2ClientCredential)
            {
                log.Warning(
                    "Credentials entered in the connection wizard are ignored. This connector resolves SQL " +
                    "credentials from configuration and Key Vault. Set the connection to Windows authentication " +
                    "in the admin centre to avoid storing an unused credential.");
            }

            string url = authenticationData.DatasourceUrl;
            if (string.IsNullOrWhiteSpace(url))
            {
                return problems;
            }

            foreach (string problem in SqlConnectionStringFactory.InspectExtraOptions(url, options.Environment))
            {
                if (options.IsProduction)
                {
                    problems.Add("The data source URL " + problem);
                }
                else
                {
                    log.Warning("The data source URL {Problem}", problem);
                }
            }

            try
            {
                var supplied = new SqlConnectionStringBuilder(url);

                if (!string.IsNullOrWhiteSpace(supplied.DataSource) &&
                    options.DataSource != null &&
                    !string.Equals(supplied.DataSource, options.DataSource.Server, StringComparison.OrdinalIgnoreCase))
                {
                    log.Warning(
                        "The data source URL names server {SuppliedServer} but DataSource:Server is {ConfiguredServer}. " +
                        "The configured value is used.",
                        supplied.DataSource,
                        options.DataSource.Server);
                }
            }
            catch (ArgumentException)
            {
                // A free text URL that is not a connection string is fine: this
                // connector takes its target from configuration.
            }

            return problems;
        }
    }
}
