// ---------------------------------------------------------------------------
// ConnectorOptions.cs
// Strongly typed binding for appsettings.json, plus validation that reports
// every problem in one pass.
//
// The old loader silently fell back to defaults when the file was missing or
// malformed. That is the wrong behaviour for a connector whose ACL configuration
// decides who can see customer data: a typo must stop the service, not quietly
// change its security posture.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Server
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.Json;
    using SqlTicketsConnector.Security.Configuration;

    /// <summary>Root of appsettings.json.</summary>
    public sealed class ConnectorOptions
    {
        /// <summary>The file this configuration is read from.</summary>
        public const string FileName = "appsettings.json";

        /// <summary>Gets or sets the deployment environment, for example Production.</summary>
        public string Environment { get; set; } = "Production";

        /// <summary>Gets or sets the connector identity and listener settings.</summary>
        public ConnectorSection Connector { get; set; } = new ConnectorSection();

        /// <summary>Gets or sets the Entra credential settings.</summary>
        public AuthOptions Auth { get; set; } = new AuthOptions();

        /// <summary>Gets or sets the Key Vault settings.</summary>
        public KeyVaultOptions KeyVault { get; set; } = new KeyVaultOptions();

        /// <summary>Gets or sets the SQL data source settings.</summary>
        public DataSourceOptions DataSource { get; set; } = new DataSourceOptions();

        /// <summary>Gets or sets the access control settings.</summary>
        public AclOptions Acl { get; set; } = new AclOptions();

        /// <summary>Gets or sets the logging settings.</summary>
        public LoggingOptions Logging { get; set; } = new LoggingOptions();

        /// <summary>Gets a value indicating whether the strict production rules apply.</summary>
        public bool IsProduction
        {
            get { return Security.Sql.SqlConnectionStringFactory.IsProduction(this.Environment); }
        }

        /// <summary>Reads appsettings.json from beside the executable.</summary>
        public static ConnectorOptions Load()
        {
            return Load(Path.Combine(AppContext.BaseDirectory, FileName));
        }

        /// <summary>
        /// Reads and deserializes the file. A missing or malformed file is fatal:
        /// there is no safe default for the ACL section.
        /// </summary>
        public static ConnectorOptions Load(string path)
        {
            if (!File.Exists(path))
            {
                throw new ConfigurationException(
                    "Configuration file not found at " + path + ". The connector cannot start without it.");
            }

            string json = File.ReadAllText(path);

            var serializerOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            };

            ConnectorOptions options;
            try
            {
                options = JsonSerializer.Deserialize<ConnectorOptions>(json, serializerOptions);
            }
            catch (JsonException ex)
            {
                throw new ConfigurationException(
                    "Configuration file " + path + " is not valid JSON: " + ex.Message,
                    ex);
            }

            if (options == null)
            {
                throw new ConfigurationException("Configuration file " + path + " is empty.");
            }

            options.SourcePath = path;
            return options;
        }

        /// <summary>Gets the path the configuration was read from. Useful in error messages.</summary>
        public string SourcePath { get; private set; } = "(not loaded from a file)";

        /// <summary>
        /// Validates every section and returns all problems at once, so an
        /// operator fixes five mistakes in one edit rather than in five restarts.
        /// </summary>
        public ValidationErrors Validate()
        {
            var errors = new ValidationErrors();

            errors.RequireOneOf("Environment", this.Environment, "Production", "Staging", "Development");

            (this.Connector ?? new ConnectorSection()).Validate(errors, "Connector");
            (this.Auth ?? new AuthOptions()).Validate(errors, "Auth");
            (this.DataSource ?? new DataSourceOptions()).Validate(errors, "DataSource", this.Environment);
            (this.Acl ?? new AclOptions()).Validate(errors, "Acl");
            (this.Logging ?? new LoggingOptions()).Validate(errors, "Logging");

            bool vaultRequired = this.DataSource != null && this.DataSource.RequiresVaultSecret;
            (this.KeyVault ?? new KeyVaultOptions()).Validate(errors, "KeyVault", vaultRequired);

            if (vaultRequired && (this.KeyVault == null || this.KeyVault.SecretName(KeyVaultOptions.SqlPasswordKey) == null))
            {
                errors.Add(
                    "KeyVault:Secrets:SqlPassword",
                    "is required because DataSource:SqlAuthMode is SqlLogin.");
            }

            if (this.Auth != null && this.Auth.ParsedMode == AuthMode.ManagedIdentity && this.IsProduction)
            {
                // Recorded rather than rejected: a managed identity is legitimate on
                // an Azure hosted agent, but not on the on-premises Windows Server
                // this deployment targets.
                errors.Add(
                    "Auth:Mode",
                    "is ManagedIdentity, which is not available on a domain joined on-premises host. " +
                    "Use Certificate unless this connector runs on an Azure VM with an assigned identity.");
            }

            return errors;
        }
    }

    /// <summary>The "Connector" section.</summary>
    public sealed class ConnectorSection
    {
        /// <summary>Gets or sets the connector ID. Must match Manifest.json and CustomConnectorPortMap.json.</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>Gets or sets the TCP port the gRPC server listens on.</summary>
        public int Port { get; set; } = 30303;

        /// <summary>Gets or sets a value indicating whether the agent connection uses TLS.</summary>
        public bool UseTls { get; set; } = true;

        /// <summary>
        /// Gets or sets the thumbprint of the TLS server certificate. When empty
        /// the authentication certificate is reused. The private key must be
        /// exportable, because gRPC Core needs PEM key material.
        /// </summary>
        public string TlsCertificateThumbprint { get; set; } = string.Empty;

        /// <summary>Validates the section.</summary>
        public void Validate(ValidationErrors errors, string path)
        {
            errors.RequireGuid(path + ":Id", this.Id);
            errors.RequireRange(path + ":Port", this.Port, 1024, 65535);

            if (!string.IsNullOrWhiteSpace(this.TlsCertificateThumbprint) &&
                !Security.Certificates.CertificateSelector.IsWellFormedThumbprint(
                    Security.Certificates.CertificateSelector.NormalizeThumbprint(this.TlsCertificateThumbprint)))
            {
                errors.Add(path + ":TlsCertificateThumbprint", "must be a 40 character SHA-1 thumbprint in hexadecimal.");
            }
        }
    }

    /// <summary>The "Logging" section.</summary>
    public sealed class LoggingOptions
    {
        /// <summary>Gets or sets the log directory. Defaults under the install directory, not LocalAppData.</summary>
        public string Directory { get; set; } = string.Empty;

        /// <summary>Gets or sets the minimum level: Verbose, Debug, Information, Warning, Error or Fatal.</summary>
        public string MinimumLevel { get; set; } = "Information";

        /// <summary>Gets or sets a value indicating whether Warning and above are written to the Windows event log.</summary>
        public bool EventLogEnabled { get; set; } = true;

        /// <summary>Gets or sets the event log source. Created by the installer, never at runtime.</summary>
        public string EventLogSource { get; set; } = "SqlTicketsConnector";

        /// <summary>Gets or sets the size of a single log file in bytes.</summary>
        public long FileSizeLimitBytes { get; set; } = 10L * 1024 * 1024;

        /// <summary>Gets or sets how many rolled files are kept.</summary>
        public int RetainedFileCountLimit { get; set; } = 30;

        /// <summary>Gets or sets the optional OpenTelemetry exporter settings.</summary>
        public OtlpOptions Otlp { get; set; } = new OtlpOptions();

        /// <summary>Validates the section.</summary>
        public void Validate(ValidationErrors errors, string path)
        {
            errors.RequireOneOf(
                path + ":MinimumLevel",
                this.MinimumLevel,
                "Verbose",
                "Debug",
                "Information",
                "Warning",
                "Error",
                "Fatal");

            errors.RequireRange(path + ":RetainedFileCountLimit", this.RetainedFileCountLimit, 1, 1000);

            if (this.FileSizeLimitBytes < 1024L * 1024 || this.FileSizeLimitBytes > 1024L * 1024 * 1024)
            {
                errors.Add(path + ":FileSizeLimitBytes", "must be between 1 MB and 1 GB.");
            }

            if (this.EventLogEnabled)
            {
                errors.RequireNonEmpty(path + ":EventLogSource", this.EventLogSource);
            }

            (this.Otlp ?? new OtlpOptions()).Validate(errors, path + ":Otlp");
        }
    }

    /// <summary>The "Logging:Otlp" section. Off by default.</summary>
    public sealed class OtlpOptions
    {
        /// <summary>Gets or sets a value indicating whether the OTLP exporter is enabled.</summary>
        public bool Enabled { get; set; }

        /// <summary>Gets or sets the collector endpoint, for example http://otel-collector.contoso.local:4318/v1/logs.</summary>
        public string Endpoint { get; set; } = string.Empty;

        /// <summary>Validates the section.</summary>
        public void Validate(ValidationErrors errors, string path)
        {
            if (!this.Enabled)
            {
                return;
            }

            errors.RequireNonEmpty(path + ":Endpoint", this.Endpoint);
        }
    }

    /// <summary>Raised when configuration cannot be read at all.</summary>
    public sealed class ConfigurationException : Exception
    {
        /// <summary>Initializes a new instance.</summary>
        public ConfigurationException(string message)
            : base(message)
        {
        }

        /// <summary>Initializes a new instance with an inner exception.</summary>
        public ConfigurationException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
