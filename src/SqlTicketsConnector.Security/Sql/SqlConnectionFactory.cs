// ---------------------------------------------------------------------------
// SqlConnectionFactory.cs
// Opens connections for the configured authentication mode, and implements the
// rotation rule: an authentication failure invalidates the cached secret and
// retries exactly once.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Security.Sql
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Azure.Core;
    using Microsoft.Data.SqlClient;
    using Serilog;
    using SqlTicketsConnector.Security.Configuration;
    using SqlTicketsConnector.Security.Secrets;

    /// <summary>
    /// Produces open <see cref="SqlConnection"/> instances. Shared by the
    /// agent-hosted connector and the direct push tool so the authentication
    /// rules exist in exactly one place.
    /// </summary>
    public sealed class SqlConnectionFactory
    {
        private readonly DataSourceOptions dataSource;
        private readonly string environment;
        private readonly ISecretProvider secrets;
        private readonly string passwordSecretName;
        private readonly TokenCredential credential;
        private readonly SecretRefreshRetryPolicy retryPolicy;
        private readonly ILogger logger;

        /// <summary>Initializes the factory.</summary>
        /// <param name="dataSource">The DataSource configuration section.</param>
        /// <param name="environment">The Environment configuration value.</param>
        /// <param name="secrets">Secret source. Required only for SqlAuthMode=SqlLogin.</param>
        /// <param name="passwordSecretName">Vault secret name for the SQL login password.</param>
        /// <param name="credential">Token credential. Required only for SqlAuthMode=EntraId.</param>
        /// <param name="logger">Log destination.</param>
        public SqlConnectionFactory(
            DataSourceOptions dataSource,
            string environment,
            ISecretProvider secrets,
            string passwordSecretName,
            TokenCredential credential,
            ILogger logger)
        {
            if (dataSource == null)
            {
                throw new ArgumentNullException(nameof(dataSource));
            }

            this.dataSource = dataSource;
            this.environment = environment;
            this.secrets = secrets;
            this.credential = credential;
            this.logger = logger ?? Log.Logger;

            switch (dataSource.ParsedSqlAuthMode)
            {
                case SqlAuthMode.SqlLogin:
                    if (secrets == null)
                    {
                        throw new ArgumentNullException(
                            nameof(secrets),
                            "A secret provider is required when SqlAuthMode is SqlLogin.");
                    }

                    if (string.IsNullOrWhiteSpace(passwordSecretName))
                    {
                        throw new ArgumentException(
                            "KeyVault:Secrets:SqlPassword must name the vault secret holding the SQL login password.",
                            nameof(passwordSecretName));
                    }

                    this.passwordSecretName = passwordSecretName;
                    break;

                case SqlAuthMode.EntraId:
                    if (credential == null)
                    {
                        throw new ArgumentNullException(
                            nameof(credential),
                            "A token credential is required when SqlAuthMode is EntraId.");
                    }

                    break;
            }

            this.retryPolicy = new SecretRefreshRetryPolicy(
                secrets ?? new NullSecretProvider(),
                this.logger);
        }

        /// <summary>Gets the number of secret refresh retries performed so far.</summary>
        public int SecretRefreshRetries
        {
            get { return this.retryPolicy.RetryCount; }
        }

        /// <summary>Gets a log-safe description of the target: server, database and mode.</summary>
        public string Description
        {
            get { return SqlConnectionStringFactory.Describe(this.dataSource); }
        }

        /// <summary>
        /// Opens a connection. On a login failure the cached password is dropped
        /// and the open is attempted once more, which is what makes a password
        /// rotation invisible to the service.
        /// </summary>
        public Task<SqlConnection> OpenAsync(CancellationToken ct)
        {
            return this.retryPolicy.ExecuteAsync(
                this.passwordSecretName,
                token => this.OpenCoreAsync(token),
                SqlErrorClassifier.IsAuthenticationFailure,
                ct);
        }

        private async Task<SqlConnection> OpenCoreAsync(CancellationToken ct)
        {
            string password = null;

            if (this.dataSource.ParsedSqlAuthMode == SqlAuthMode.SqlLogin)
            {
                password = await this.secrets.GetSecretAsync(this.passwordSecretName, ct).ConfigureAwait(false);
            }

            SqlConnectionStringBuilder builder = SqlConnectionStringFactory.Build(
                this.dataSource,
                this.environment,
                password);

            // The builder holds the password in managed memory for the lifetime of
            // this call only. It is never logged and never written to disk.
            var connection = new SqlConnection(builder.ConnectionString);

            try
            {
                if (this.dataSource.ParsedSqlAuthMode == SqlAuthMode.EntraId)
                {
                    AccessToken token = await this.credential
                        .GetTokenAsync(
                            new TokenRequestContext(new[] { SqlConnectionStringFactory.SqlTokenScope }),
                            ct)
                        .ConfigureAwait(false);

                    connection.AccessToken = token.Token;
                }

                await connection.OpenAsync(ct).ConfigureAwait(false);
                return connection;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Stand-in used when no secret source is configured, so the retry policy
        /// has a non-null dependency even though it will never invalidate anything.
        /// </summary>
        private sealed class NullSecretProvider : ISecretProvider
        {
            public Task<string> GetSecretAsync(string name, CancellationToken ct)
            {
                throw new SecretResolutionException(
                    "No secret provider is configured. Set KeyVault:Uri to resolve secret '" + name + "'.");
            }

            public Task InvalidateAsync(string name, CancellationToken ct)
            {
                return Task.CompletedTask;
            }
        }
    }
}
