// ---------------------------------------------------------------------------
// ConnectorServer.cs
// Hosts the gRPC server the Graph connector agent calls into, and owns the
// objects whose lifetime is the process: the certificate resolver, the secret
// cache and the SQL connection factory.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Server
{
    using System;
    using System.Collections.Generic;
    using System.Security.Cryptography;
    using System.Security.Cryptography.X509Certificates;
    using System.Threading;
    using Azure.Core;
    using Grpc.Core;
    using Grpc.Core.Interceptors;
    using Microsoft.Graph.Connectors.Contracts.Grpc;
    using Serilog;
    using SqlTicketsConnector.Connector;
    using SqlTicketsConnector.Logging;
    using SqlConnector.Security.Certificates;
    using SqlConnector.Security.Configuration;
    using SqlConnector.Security.Credentials;
    using SqlConnector.Security.Logging;
    using SqlConnector.Security.Secrets;
    using SqlConnector.Security.Sql;

    /// <summary>Starts and stops the connector's gRPC listener.</summary>
    public sealed class ConnectorServer : IDisposable
    {
        private static readonly TimeSpan CertificateCheckInterval = TimeSpan.FromHours(24);

        private readonly ConnectorOptions options;
        private readonly ILogger logger;

        private Grpc.Core.Server server;
        private StoreCertificateResolver certificateResolver;
        private CachingSecretProvider secretCache;
        private Timer certificateTimer;
        private X509Certificate2 tlsCertificate;
        private bool disposed;

        /// <summary>Initializes the host.</summary>
        public ConnectorServer(ConnectorOptions options, ILogger logger)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            this.options = options;
            this.logger = logger ?? Log.Logger;
        }

        /// <summary>
        /// Builds every dependency and starts listening. Throws rather than
        /// returning on failure, so Program can log Fatal and exit non-zero.
        /// </summary>
        public void Start()
        {
            TokenCredential credential = this.BuildCredential();
            ISecretProvider secrets = this.BuildSecretProvider(credential);

            string passwordSecretName = this.options.KeyVault == null
                ? null
                : this.options.KeyVault.SecretName(KeyVaultOptions.SqlPasswordKey);

            var connections = new SqlConnectionFactory(
                this.options.DataSource,
                this.options.Environment,
                secrets,
                passwordSecretName,
                credential,
                this.logger);

            var sourceFactory = new SqlTicketSourceFactory(connections, this.options.DataSource, this.logger);
            var interceptor = new CallLoggingInterceptor(this.logger);
            var infoService = new ConnectorInfoServiceImpl(this.options.Connector.Id);

            if (!string.Equals(
                    this.options.Connector.Id,
                    ConnectorInfoServiceImpl.DefaultConnectorId,
                    StringComparison.OrdinalIgnoreCase))
            {
                this.logger.Warning(
                    "Connector:Id {ConfiguredId} differs from the ID this build was created for ({BuildId}). " +
                    "Existing connections are bound to the original ID and will stop working.",
                    this.options.Connector.Id,
                    ConnectorInfoServiceImpl.DefaultConnectorId);
            }

            ServerCredentials credentials = this.BuildServerCredentials();

            this.server = new Grpc.Core.Server
            {
                Services =
                {
                    ConnectorInfoService.BindService(infoService).Intercept(interceptor),
                    ConnectionManagementService
                        .BindService(new ConnectionManagementServiceImpl(sourceFactory, this.options, this.logger))
                        .Intercept(interceptor),
                    ConnectorCrawlerService
                        .BindService(new ConnectorCrawlerServiceImpl(sourceFactory, this.options, this.logger))
                        .Intercept(interceptor),
                    ConnectorOAuthService.BindService(new ConnectorOAuthServiceImpl()).Intercept(interceptor),
                },
                Ports = { new ServerPort("localhost", this.options.Connector.Port, credentials) },
            };

            this.server.Start();

            this.logger.Information(
                "Server started. ConnectorId {ConnectorId} listening on localhost:{Port} with TLS {TlsEnabled}. " +
                "Data source {DataSource}. Environment {Environment}.",
                this.options.Connector.Id,
                this.options.Connector.Port,
                this.options.Connector.UseTls,
                sourceFactory.Description,
                this.options.Environment);

            this.logger.Information(
                "Confirm CustomConnectorPortMap.json maps {ConnectorId} to {Port}, then restart GcaHostService.",
                this.options.Connector.Id,
                this.options.Connector.Port);

            this.StartCertificateMonitor();
        }

        /// <summary>Shuts the listener down and releases process scoped resources.</summary>
        public void Stop()
        {
            this.logger.Information("Stopping server.");

            if (this.certificateTimer != null)
            {
                this.certificateTimer.Dispose();
                this.certificateTimer = null;
            }

            if (this.server != null)
            {
                this.server.ShutdownAsync().Wait();
                this.server = null;
            }

            if (this.secretCache != null)
            {
                this.secretCache.Dispose();
                this.secretCache = null;
            }

            if (this.tlsCertificate != null)
            {
                this.tlsCertificate.Dispose();
                this.tlsCertificate = null;
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            this.Stop();
        }

        private TokenCredential BuildCredential()
        {
            if (this.options.Auth.ParsedMode == AuthMode.Certificate)
            {
                // Resolved at startup rather than on first use: a missing certificate
                // or an unreadable private key is a deployment fault, and finding it
                // during installation is far cheaper than finding it mid crawl.
                this.certificateResolver = new StoreCertificateResolver(this.options.Auth, this.logger);
            }

            return TokenCredentialFactory.Create(this.options.Auth, this.certificateResolver, this.logger);
        }

        private ISecretProvider BuildSecretProvider(TokenCredential credential)
        {
            KeyVaultOptions vault = this.options.KeyVault ?? new KeyVaultOptions();
            bool vaultConfigured = !string.IsNullOrWhiteSpace(vault.Uri);

            ISecretProvider inner;

            if (vaultConfigured)
            {
                inner = new KeyVaultSecretProvider(new Uri(vault.Uri), credential, this.logger);

                this.logger.Information(
                    "Secrets resolve from {VaultUri} with a cache time to live of {TtlMinutes} minute(s).",
                    vault.Uri,
                    vault.SecretCacheTtlMinutes);
            }
            else if (!this.options.IsProduction)
            {
                // Throws if Environment is Production, which is the point.
                inner = new EnvironmentSecretProvider(this.options.Environment, this.logger);
            }
            else
            {
                this.logger.Information(
                    "No Key Vault is configured and the current configuration resolves no secrets " +
                    "(DataSource:SqlAuthMode is {SqlAuthMode}).",
                    this.options.DataSource.SqlAuthMode);

                return null;
            }

            this.secretCache = new CachingSecretProvider(
                inner,
                TimeSpan.FromMinutes(vault.SecretCacheTtlMinutes),
                this.logger);

            return this.secretCache;
        }

        private ServerCredentials BuildServerCredentials()
        {
            if (!this.options.Connector.UseTls)
            {
                this.logger.Warning(
                    "Connector:UseTls is false. Traffic between the agent and this process is unencrypted. " +
                    "It stays on the loopback interface, but a local process can read it.");

                return ServerCredentials.Insecure;
            }

            X509Certificate2 certificate = this.ResolveTlsCertificate();

            try
            {
                string certificatePem = certificate.ExportCertificatePem();
                string keyPem;

                using (RSA rsa = certificate.GetRSAPrivateKey())
                {
                    if (rsa == null)
                    {
                        throw new CryptographicException("The TLS certificate does not expose an RSA private key.");
                    }

                    // PEM is produced in memory. No key material is written to disk.
                    keyPem = rsa.ExportPkcs8PrivateKeyPem();
                }

                this.logger.Information(
                    "TLS enabled on the agent connection using certificate {Thumbprint} ({Subject}).",
                    certificate.Thumbprint,
                    certificate.Subject);

                return new SslServerCredentials(new[] { new KeyCertificatePair(certificatePem, keyPem) });
            }
            catch (CryptographicException ex)
            {
                throw new InvalidOperationException(
                    "The TLS certificate " + certificate.Thumbprint + " has a private key that cannot be exported, " +
                    "so gRPC Core cannot use it. Re-import the certificate with an exportable key, set " +
                    "Connector:TlsCertificateThumbprint to one that is exportable, or set Connector:UseTls to false " +
                    "and rely on the loopback interface.",
                    ex);
            }
        }

        private X509Certificate2 ResolveTlsCertificate()
        {
            string configured = this.options.Connector.TlsCertificateThumbprint;

            if (!string.IsNullOrWhiteSpace(configured))
            {
                var resolver = new StoreCertificateResolver(
                    this.options.Auth.ParsedStoreLocation,
                    new CertificateSelectionCriteria
                    {
                        Thumbprints = new List<string> { configured },
                        ExpiryWarningDays = this.options.Auth.ExpiryWarningDays,
                    },
                    this.logger);

                IReadOnlyList<CertificateCandidate> tlsCandidates = resolver.ResolveCandidates();
                this.tlsCertificate = new X509Certificate2(tlsCandidates[0].Certificate);
                return this.tlsCertificate;
            }

            if (this.certificateResolver == null)
            {
                throw new InvalidOperationException(
                    "Connector:UseTls is true but no certificate is available. Set Connector:TlsCertificateThumbprint, " +
                    "or set Auth:Mode to Certificate so the authentication certificate can be reused.");
            }

            IReadOnlyList<CertificateCandidate> candidates = this.certificateResolver.ResolveCandidates();
            this.tlsCertificate = new X509Certificate2(candidates[0].Certificate);
            return this.tlsCertificate;
        }

        private void StartCertificateMonitor()
        {
            if (this.certificateResolver == null)
            {
                return;
            }

            // Daily: Warning inside the expiry window, Error once expired.
            this.certificateTimer = new Timer(
                state => this.ReportCertificateState(),
                null,
                CertificateCheckInterval,
                CertificateCheckInterval);
        }

        private void ReportCertificateState()
        {
            try
            {
                this.certificateResolver.ReportExpiryState();
            }
            catch (Exception ex)
            {
                this.logger.Error(RedactedException.Wrap(ex), "The daily certificate check failed.");
            }
        }
    }
}
