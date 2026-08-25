// ---------------------------------------------------------------------------
// ClientSecretAuthTests.cs
// Evidence for the ClientSecret credential mode: what configuration is
// accepted, what is rejected, and that a secret stored in Windows Credential
// Manager is read back exactly.
//
// The Credential Manager tests are Windows only. CI runs on windows-latest, so
// they execute on every build; on a developer's Mac they report as skipped
// rather than silently passing.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Tests
{
    using System;
    using System.Runtime.Versioning;
    using System.Threading;
    using SqlConnector.Security.Configuration;
    using SqlConnector.Security.Secrets;
    using SqlTicketsConnector.Server;
    using SqlTicketsConnector.Tests.TestSupport;
    using Xunit;

    public class ClientSecretAuthTests
    {
        [Fact]
        public void ClientSecret_mode_requires_a_credential_manager_target()
        {
            ConnectorOptions options = TestData.ValidOptions();
            options.Auth.Mode = "ClientSecret";
            options.Auth.CertificateThumbprints.Clear();
            options.Auth.ClientSecretCredentialTarget = string.Empty;

            var errors = new ValidationErrors();
            options.Auth.Validate(errors, "Auth");

            Assert.True(errors.HasErrors);
            Assert.Contains("Auth:ClientSecretCredentialTarget", errors.ToMessage());
        }

        [Fact]
        public void ClientSecret_mode_accepts_a_target_name_and_needs_no_certificate()
        {
            ConnectorOptions options = TestData.ValidOptions();
            options.Auth.Mode = "ClientSecret";

            // The point of the mode: no certificate anywhere in the configuration.
            options.Auth.CertificateThumbprints.Clear();
            options.Auth.CertificateSubject = string.Empty;
            options.Auth.ClientSecretCredentialTarget = "SqlTicketsConnector/EntraClientSecret";

            var errors = new ValidationErrors();
            options.Auth.Validate(errors, "Auth");

            Assert.False(errors.HasErrors, errors.ToMessage());
            Assert.Equal(AuthMode.ClientSecret, options.Auth.ParsedMode);
        }

        /// <summary>
        /// The mistake this mode invites is pasting the secret itself into the
        /// key whose name ends in "Target". The build time scan cannot catch it
        /// on a server, so startup validation does.
        /// </summary>
        [Fact]
        public void A_secret_pasted_into_the_target_name_is_rejected_at_startup()
        {
            ConnectorOptions options = TestData.ValidOptions();
            options.Auth.Mode = "ClientSecret";
            options.Auth.CertificateThumbprints.Clear();

            // Shaped like an Entra client secret: long, no separators.
            options.Auth.ClientSecretCredentialTarget = "8Xq7SdKk1pRzT4wYb2NcVfHjMgLtQe6UaZ0iOx9P";

            var errors = new ValidationErrors();
            options.Auth.Validate(errors, "Auth");

            Assert.True(errors.HasErrors);
            Assert.Contains("looks like a secret value", errors.ToMessage());
        }

        [Fact]
        public void An_unknown_mode_is_still_rejected()
        {
            ConnectorOptions options = TestData.ValidOptions();
            options.Auth.Mode = "ClientSecretFromEnvironment";

            var errors = new ValidationErrors();
            options.Auth.Validate(errors, "Auth");

            Assert.True(errors.HasErrors);
            Assert.Equal(AuthMode.Unspecified, options.Auth.ParsedMode);
        }

        [WindowsOnlyFact]
        [SupportedOSPlatform("windows")]
        public void A_secret_stored_in_credential_manager_is_read_back_unchanged()
        {
            string target = "SqlTicketsConnectorTests/" + Guid.NewGuid().ToString("N");

            // Deliberately awkward: non-ASCII and punctuation prove the UTF-16
            // blob is decoded rather than truncated at the first high byte.
            const string Secret = "s3cr3t~value-with_ünicode.and/slashes";

            CredentialManagerTestStore.Write(target, "client-id", Secret);

            try
            {
                Assert.True(WindowsCredentialStore.Exists(target));
                Assert.Equal(Secret, WindowsCredentialStore.Read(target));

                var provider = new WindowsCredentialSecretProvider(Serilog.Core.Logger.None);
                Assert.Equal(Secret, provider.GetSecretAsync(target, CancellationToken.None).GetAwaiter().GetResult());
            }
            finally
            {
                CredentialManagerTestStore.Delete(target);
            }

            Assert.False(WindowsCredentialStore.Exists(target));
        }

        [WindowsOnlyFact]
        [SupportedOSPlatform("windows")]
        public void An_odd_length_blob_is_decoded_as_utf8_not_truncated_utf16()
        {
            // cmdkey and .NET write UTF-16 (always even length); other tools
            // write UTF-8. The odd length is how the store tells them apart, and
            // only a raw-byte write can produce one.
            string target = "SqlTicketsConnectorTests/utf8-" + Guid.NewGuid().ToString("N");
            const string Secret = "utf8-secret-with-ü";

            CredentialManagerTestStore.Write(target, "client-id", System.Text.Encoding.UTF8.GetBytes(Secret));

            try
            {
                Assert.Equal(Secret, WindowsCredentialStore.Read(target));
            }
            finally
            {
                CredentialManagerTestStore.Delete(target);
            }
        }

        [WindowsOnlyFact]
        [SupportedOSPlatform("windows")]
        public void An_entry_with_an_empty_blob_reports_it_holds_no_value()
        {
            // An entry that exists but is empty is a different operator mistake
            // from a missing entry, and the message has to say which one it was.
            string target = "SqlTicketsConnectorTests/empty-" + Guid.NewGuid().ToString("N");

            CredentialManagerTestStore.Write(target, "client-id", Array.Empty<byte>());

            try
            {
                SecretResolutionException ex = Assert.Throws<SecretResolutionException>(
                    () => WindowsCredentialStore.Read(target));

                Assert.Contains("holds no value", ex.Message);
                Assert.Contains(target, ex.Message);
            }
            finally
            {
                CredentialManagerTestStore.Delete(target);
            }
        }

        [WindowsOnlyFact]
        [SupportedOSPlatform("windows")]
        public void A_missing_credential_names_the_target_and_the_account_that_looked()
        {
            string target = "SqlTicketsConnectorTests/missing-" + Guid.NewGuid().ToString("N");

            SecretResolutionException ex = Assert.Throws<SecretResolutionException>(
                () => WindowsCredentialStore.Read(target));

            Assert.Contains(target, ex.Message);

            // The failure has to say whose Credential Manager was searched: the
            // usual cause is an entry stored by an administrator rather than by
            // the service account.
            Assert.Contains("per account", ex.Message);
        }
    }
}
