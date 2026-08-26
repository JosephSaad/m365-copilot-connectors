// ---------------------------------------------------------------------------
// TestData.cs
// Shared builders: valid configuration, in-memory certificates and a logger
// wired with the same redaction pipeline the service uses.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Tests.TestSupport
{
    using System;
    using System.Collections.Generic;
    using System.Security.Cryptography;
    using System.Security.Cryptography.X509Certificates;
    using Serilog;
    using Serilog.Core;
    using Serilog.Events;
    using SqlPushCore;
    using SqlTicketsConnector.Connector;
    using SqlTicketsConnector.Logging;
    using SqlConnector.Security.Configuration;
    using SqlConnector.Security.Logging;
    using SqlTicketsConnector.Server;

    /// <summary>Builders used across the test suite.</summary>
    public static class TestData
    {
        /// <summary>An Entra group object ID used by the ACL tests.</summary>
        public const string GroupObjectId = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";

        /// <summary>Certificate mode auth that passes validation.</summary>
        public static AuthOptions ValidAuth()
        {
            return new AuthOptions
            {
                Mode = "Certificate",
                TenantId = "8f3a1c22-0d5e-4a1e-9c2b-6a7d5e4f3b21",
                ClientId = "1b9d6bcd-bbfd-4b2d-9b5d-ab8dfbbd4bed",
                CertificateStoreLocation = "LocalMachine",
                CertificateThumbprints = new List<string> { new string('A', 40) },
                ExpiryWarningDays = 30,
            };
        }

        /// <summary>Windows integrated SQL settings that pass validation.</summary>
        public static DataSourceOptions ValidDataSource()
        {
            return new DataSourceOptions
            {
                Server = "sql01.contoso.local",
                Database = "Ops",
                SqlAuthMode = "WindowsIntegrated",
                MaxContentBytes = 3670016,
                SoftDeleteEnabled = true,

                // The tickets push tool requires a template; the hierarchy tool
                // ignores it. An agnostic value keeps one fixture valid for both.
                ItemUrlTemplate = "https://portal.contoso.com/item/{0}",
            };
        }

        /// <summary>Returns configuration that passes validation.</summary>
        public static ConnectorOptions ValidOptions()
        {
            return new ConnectorOptions
            {
                Environment = "Production",
                Connector = new ConnectorSection
                {
                    Id = ConnectorInfoServiceImpl.DefaultConnectorId,
                    Port = 30303,
                    UseTls = false,
                },
                Auth = new AuthOptions
                {
                    Mode = "Certificate",
                    TenantId = "8f3a1c22-0d5e-4a1e-9c2b-6a7d5e4f3b21",
                    ClientId = "1b9d6bcd-bbfd-4b2d-9b5d-ab8dfbbd4bed",
                    CertificateStoreLocation = "LocalMachine",
                    CertificateThumbprints = new List<string> { new string('A', 40) },
                    ExpiryWarningDays = 30,
                },
                KeyVault = new KeyVaultOptions
                {
                    Uri = "https://kv-connectors-test.vault.azure.net/",
                    SecretCacheTtlMinutes = 60,
                    Secrets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        { KeyVaultOptions.SqlPasswordKey, "sql-tickets-reader-password" },
                    },
                },
                DataSource = new DataSourceOptions
                {
                    Server = "sql01.contoso.local",
                    Database = "Ops",
                    SqlAuthMode = "WindowsIntegrated",
                    MaxContentBytes = 3670016,
                    SoftDeleteEnabled = true,

                    // Required by the connector's own validation now that the
                    // shared library no longer defaults it.
                    ItemUrlTemplate = "https://tickets.contoso.com/ticket/{0}",
                },
                Acl = new AclOptions
                {
                    GrantGroupObjectIds = new List<string> { GroupObjectId },
                },
                Logging = new LoggingOptions
                {
                    Directory = System.IO.Path.GetTempPath(),
                    MinimumLevel = "Information",
                    EventLogEnabled = false,
                },
            };
        }

        /// <summary>Returns push configuration that passes validation.</summary>
        /// <param name="connectionId">The external connection to target.</param>
        /// <param name="itemView">The table or view to read.</param>
        /// <returns>Configuration with no validation errors.</returns>
        public static PushOptions ValidPushOptions(
            string connectionId = "consultingwork", string itemView = "dbo.vwExternalItems")
        {
            return new PushOptions
            {
                Environment = "Production",
                Auth = ValidAuth(),
                KeyVault = new KeyVaultOptions
                {
                    Uri = "https://kv-connectors-test.vault.azure.net/",
                    SecretCacheTtlMinutes = 60,
                },
                DataSource = ValidDataSource(),
                Acl = new AclOptions
                {
                    GrantGroupObjectIds = new List<string> { GroupObjectId },
                },
                Graph = new GraphSection
                {
                    ConnectionId = connectionId,

                    // The display identity follows the connection: a tickets
                    // fixture labelled as the hierarchy connection would be the
                    // same cross-contamination the source review hunts for.
                    ConnectionName = connectionId == "sqltickets" ? "SQL Support Tickets" : "Consulting work",
                    Description = connectionId == "sqltickets"
                        ? "Support tickets ingested from SQL Server"
                        : "Customers, engagements and logged time",
                    SchemaReadyTimeoutMinutes = 30,
                },
                Source = new SourceSection
                {
                    ItemView = itemView,
                    MaxItems = 0,
                },
            };
        }

        /// <summary>Builds a row.</summary>
        public static TicketRow Row(int id, DateTime lastModifiedUtc, string text, bool deleted = false)
        {
            return new TicketRow
            {
                TicketId = id,
                Title = "Title " + text,
                Status = "Open",
                AssignedTo = text + "@contoso.com",
                Body = "Body " + text,
                LastModifiedUtc = lastModifiedUtc,
                IsDeleted = deleted,
            };
        }

        /// <summary>
        /// Builds a logger with the production redaction pipeline attached, writing
        /// to a collecting sink at Verbose so nothing is filtered out before the
        /// assertions run.
        /// </summary>
        public static Logger RedactingLogger(CollectingSink sink)
        {
            return LoggingSetup.ApplyRedaction(new LoggerConfiguration())
                .MinimumLevel.Verbose()
                .Enrich.FromLogContext()
                .WriteTo.Sink(sink, LogEventLevel.Verbose)
                .CreateLogger();
        }

        /// <summary>Creates a self-signed certificate in memory.</summary>
        public static X509Certificate2 Certificate(
            string subject,
            DateTimeOffset notBefore,
            DateTimeOffset notAfter,
            bool withPrivateKey = true)
        {
            using (RSA rsa = RSA.Create(2048))
            {
                var request = new CertificateRequest(
                    "CN=" + subject,
                    rsa,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1);

                X509Certificate2 certificate = request.CreateSelfSigned(notBefore, notAfter);

                if (withPrivateKey)
                {
                    return certificate;
                }

                // Public part only: this is what an import without the key looks like.
                // X509CertificateLoader rather than the byte[] constructor, which is
                // obsolete from .NET 9 (SYSLIB0057).
                using (certificate)
                {
                    return X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));
                }
            }
        }
    }
}
