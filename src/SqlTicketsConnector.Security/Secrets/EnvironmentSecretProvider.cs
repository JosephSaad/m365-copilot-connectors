// ---------------------------------------------------------------------------
// EnvironmentSecretProvider.cs
// Local development only. Refuses to construct when Environment is Production.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Security.Secrets
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Serilog;

    /// <summary>
    /// Reads secrets from environment variables so a developer can run the
    /// connector without vault access. Never selected in Production: the
    /// constructor throws, which fails startup rather than quietly downgrading
    /// the control.
    /// </summary>
    public sealed class EnvironmentSecretProvider : ISecretProvider
    {
        /// <summary>The configured environment name that this provider refuses to run under.</summary>
        public const string ForbiddenEnvironment = "Production";

        private readonly ILogger logger;

        /// <summary>Initializes the provider, refusing to run in Production.</summary>
        /// <param name="environment">The value of the "Environment" configuration key.</param>
        /// <param name="logger">Destination for the startup warning.</param>
        public EnvironmentSecretProvider(string environment, ILogger logger)
        {
            this.logger = logger ?? Log.Logger;

            if (string.Equals(environment, ForbiddenEnvironment, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "EnvironmentSecretProvider is a development-only secret source and cannot be used when " +
                    "Environment is 'Production'. Configure KeyVault:Uri and use KeyVaultSecretProvider.");
            }

            this.logger.Warning(
                "SECURITY: secrets are being read from environment variables because Environment is '{Environment}'. " +
                "This provider is for local development only and must never be used in Production.",
                environment);
        }

        /// <summary>
        /// Maps a vault secret name to an environment variable name:
        /// "sql-tickets-reader-password" becomes "SQL_TICKETS_READER_PASSWORD".
        /// </summary>
        public static string ToVariableName(string secretName)
        {
            if (string.IsNullOrWhiteSpace(secretName))
            {
                throw new ArgumentException("Secret name is required.", nameof(secretName));
            }

            return secretName.Trim().Replace('-', '_').Replace('.', '_').ToUpperInvariant();
        }

        /// <inheritdoc />
        public Task<string> GetSecretAsync(string name, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            string variable = ToVariableName(name);
            string value = Environment.GetEnvironmentVariable(variable);

            if (string.IsNullOrEmpty(value))
            {
                throw new SecretResolutionException(
                    "Environment variable '" + variable + "' is not set. It supplies the development value for " +
                    "secret '" + name + "'.");
            }

            this.logger.Debug("Resolved secret {SecretName} from environment variable {Variable}.", name, variable);
            return Task.FromResult(value);
        }

        /// <inheritdoc />
        public Task InvalidateAsync(string name, CancellationToken ct)
        {
            // Environment variables are read on every call, so there is nothing to drop.
            return Task.CompletedTask;
        }
    }
}
