// ---------------------------------------------------------------------------
// KeyVaultSecretProvider.cs
// Production secret source. Authenticates to the vault with the TokenCredential
// built by TokenCredentialFactory, which on this deployment is a client
// certificate credential.
// ---------------------------------------------------------------------------

namespace SqlConnector.Security.Secrets
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Azure;
    using Azure.Core;
    using Azure.Security.KeyVault.Secrets;
    using Serilog;

    /// <summary>
    /// Reads secrets from Azure Key Vault. No caching here: wrap this in
    /// <see cref="CachingSecretProvider"/> for that.
    /// </summary>
    public sealed class KeyVaultSecretProvider : ISecretProvider
    {
        private readonly SecretClient client;
        private readonly ILogger logger;
        private readonly string vaultDescription;

        /// <summary>Initializes a provider against a vault URI.</summary>
        public KeyVaultSecretProvider(Uri vaultUri, TokenCredential credential, ILogger logger)
            : this(new SecretClient(vaultUri, credential), logger)
        {
        }

        /// <summary>Initializes a provider around an existing client. Used by tests with a fake client.</summary>
        public KeyVaultSecretProvider(SecretClient client, ILogger logger)
        {
            if (client == null)
            {
                throw new ArgumentNullException(nameof(client));
            }

            this.client = client;
            this.logger = logger ?? Log.Logger;
            this.vaultDescription = client.VaultUri == null ? "(unknown vault)" : client.VaultUri.ToString();
        }

        /// <inheritdoc />
        public async Task<string> GetSecretAsync(string name, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Secret name is required.", nameof(name));
            }

            try
            {
                // Only the secret name and the vault URI are logged. Neither is sensitive.
                this.logger.Debug(
                    "Resolving secret {SecretName} from {VaultUri}.",
                    name,
                    this.vaultDescription);

                Response<KeyVaultSecret> response = await this.client
                    .GetSecretAsync(name, cancellationToken: ct)
                    .ConfigureAwait(false);

                return response.Value.Value;
            }
            catch (RequestFailedException ex)
            {
                // The exception message from Key Vault carries the request URI and
                // status, never the value - but the convention is uniform: every
                // exception goes through Wrap, so no call site needs a safety
                // argument and the tripwire test has no exceptions to memorise.
                this.logger.Error(
                    Logging.RedactedException.Wrap(ex),
                    "Key Vault refused to return secret {SecretName} from {VaultUri}. Status {Status}.",
                    name,
                    this.vaultDescription,
                    ex.Status);

                throw new SecretResolutionException(
                    "Could not resolve secret '" + name + "' from " + this.vaultDescription +
                    ". Confirm the connector's application object has the 'Key Vault Secrets User' role on the vault " +
                    "and that the certificate credential is valid.",
                    ex);
            }
        }

        /// <inheritdoc />
        public Task InvalidateAsync(string name, CancellationToken ct)
        {
            // Nothing is held here. The cache decorator implements invalidation.
            return Task.CompletedTask;
        }
    }
}
