// ---------------------------------------------------------------------------
// ConfigurationTests.cs
// Startup validation and connection string construction.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Microsoft.Data.SqlClient;
    using SqlConnector.Security.Configuration;
    using SqlConnector.Security.Sql;
    using SqlTicketsConnector.Server;
    using SqlTicketsConnector.Tests.TestSupport;
    using Xunit;

    public class ConfigurationTests
    {
        [Fact]
        public void Valid_configuration_produces_no_errors()
        {
            ValidationErrors errors = TestData.ValidOptions().Validate();

            Assert.False(errors.HasErrors, errors.ToMessage());
        }

        [Fact]
        public void Every_invalid_field_is_reported_in_one_pass()
        {
            var options = new ConnectorOptions
            {
                Environment = "Prod",                       // not one of the allowed values
                Connector = new ConnectorSection
                {
                    Id = "not-a-guid",
                    Port = 42,                              // below the allowed range
                },
                Auth = new AuthOptions
                {
                    Mode = "Password",                      // not a supported mode
                    TenantId = "00000000-0000-0000-0000-000000000000",
                    ClientId = string.Empty,
                    CertificateStoreLocation = "Machine",
                    CertificateThumbprints = new List<string> { "ZZZZ" },
                    ExpiryWarningDays = 4000,
                },
                KeyVault = new KeyVaultOptions
                {
                    Uri = "ftp://vault",
                    SecretCacheTtlMinutes = 0,
                },
                DataSource = new DataSourceOptions
                {
                    Server = string.Empty,
                    Database = string.Empty,
                    SqlAuthMode = "Kerberos",
                    MaxContentBytes = 99,
                    SqlUserId = "sa",                       // only valid with SqlLogin
                },
                Acl = new AclOptions(),                     // empty: no silent everyone
                Logging = new LoggingOptions
                {
                    MinimumLevel = "Chatty",
                    RetainedFileCountLimit = 0,
                    FileSizeLimitBytes = 10,
                    EventLogEnabled = true,
                    EventLogSource = string.Empty,
                },
            };

            ValidationErrors errors = options.Validate();

            Assert.True(errors.HasErrors);

            // One restart should be enough to learn about all of them.
            string[] expected =
            {
                "Environment",
                "Connector:Id",
                "Connector:Port",
                "Auth:Mode",
                "Auth:ClientId",
                "DataSource:Server",
                "DataSource:Database",
                "DataSource:SqlAuthMode",
                "DataSource:MaxContentBytes",
                "DataSource:SqlUserId",
                "Acl:GrantGroupObjectIds",
                "Logging:MinimumLevel",
                "Logging:RetainedFileCountLimit",
                "Logging:FileSizeLimitBytes",
                "Logging:EventLogSource",
                "KeyVault:SecretCacheTtlMinutes",
                "KeyVault:Uri",
            };

            foreach (string path in expected)
            {
                Assert.True(
                    errors.Errors.Any(e => e.StartsWith(path + ":", StringComparison.Ordinal)),
                    "expected an error for " + path + ". Got: " + errors.ToMessage());
            }
        }

        [Fact]
        public void An_empty_acl_section_fails_validation()
        {
            ConnectorOptions options = TestData.ValidOptions();
            options.Acl.GrantGroupObjectIds.Clear();

            Assert.Contains(
                options.Validate().Errors,
                e => e.StartsWith("Acl:GrantGroupObjectIds:", StringComparison.Ordinal));
        }

        [Fact]
        public void SqlLogin_without_a_vault_secret_name_fails_validation()
        {
            ConnectorOptions options = TestData.ValidOptions();
            options.DataSource.SqlAuthMode = "SqlLogin";
            options.DataSource.SqlUserId = "svc_gca_reader";
            options.KeyVault.Secrets.Clear();

            Assert.Contains(
                options.Validate().Errors,
                e => e.StartsWith("KeyVault:Secrets:SqlPassword:", StringComparison.Ordinal));
        }

        [Fact]
        public void Windows_integrated_connections_carry_no_credential_and_force_encryption()
        {
            var dataSource = new DataSourceOptions
            {
                Server = "sql01.contoso.local",
                Database = "Ops",
                SqlAuthMode = "WindowsIntegrated",
            };

            SqlConnectionStringBuilder builder = SqlConnectionStringFactory.Build(dataSource, "Production", null);

            Assert.True(builder.IntegratedSecurity);
            Assert.False(builder.ShouldSerialize("Password"));
            Assert.False(builder.ShouldSerialize("User ID"));
            Assert.False(builder.TrustServerCertificate);
            Assert.Contains("Encrypt=True", builder.ConnectionString, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(3, builder.ConnectRetryCount);
            Assert.Equal(10, builder.ConnectRetryInterval);
        }

        [Fact]
        public void TrustServerCertificate_is_rejected_in_production()
        {
            var dataSource = new DataSourceOptions
            {
                Server = "sql01.contoso.local",
                Database = "Ops",
                SqlAuthMode = "WindowsIntegrated",
                ExtraConnectionOptions = "TrustServerCertificate=true",
            };

            Assert.NotEmpty(SqlConnectionStringFactory.InspectExtraOptions(
                dataSource.ExtraConnectionOptions,
                "Production"));

            Assert.Throws<InvalidOperationException>(
                () => SqlConnectionStringFactory.Build(dataSource, "Production", null));

            // Outside production it is a warning, not a wall.
            Assert.Empty(SqlConnectionStringFactory.InspectExtraOptions(
                dataSource.ExtraConnectionOptions,
                "Development"));
        }

        [Fact]
        public void Credentials_in_operator_supplied_connection_text_are_rejected()
        {
            IReadOnlyList<string> problems = SqlConnectionStringFactory.InspectExtraOptions(
                "User ID=sa;Password=Hunter2;",
                "Production");

            Assert.Equal(2, problems.Count);
            Assert.Contains(problems, p => p.Contains("password", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(problems, p => p.Contains("user ID", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void SqlLogin_requires_a_resolved_password_and_keeps_it_out_of_configuration()
        {
            var dataSource = new DataSourceOptions
            {
                Server = "sql01.contoso.local",
                Database = "Ops",
                SqlAuthMode = "SqlLogin",
                SqlUserId = "svc_gca_reader",
            };

            Assert.Throws<InvalidOperationException>(
                () => SqlConnectionStringFactory.Build(dataSource, "Production", null));

            SqlConnectionStringBuilder builder = SqlConnectionStringFactory.Build(
                dataSource,
                "Production",
                "resolved-at-runtime");

            Assert.False(builder.IntegratedSecurity);
            Assert.Equal("svc_gca_reader", builder.UserID);
            Assert.Equal("resolved-at-runtime", builder.Password);
        }

        [Fact]
        public void The_environment_secret_provider_refuses_to_run_in_production()
        {
            Assert.Throws<InvalidOperationException>(
                () => new SqlConnector.Security.Secrets.EnvironmentSecretProvider("Production", Serilog.Core.Logger.None));

            var provider = new SqlConnector.Security.Secrets.EnvironmentSecretProvider("Development", Serilog.Core.Logger.None);
            Assert.NotNull(provider);

            Assert.Equal(
                "SQL_TICKETS_READER_PASSWORD",
                SqlConnector.Security.Secrets.EnvironmentSecretProvider.ToVariableName("sql-tickets-reader-password"));
        }
    }
}
