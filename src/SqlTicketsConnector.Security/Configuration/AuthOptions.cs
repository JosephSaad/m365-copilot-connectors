// ---------------------------------------------------------------------------
// AuthOptions.cs
// The "Auth" section. Everything here is non-sensitive: tenant ID, client ID,
// certificate store coordinates, and the *name* of a Windows Credential Manager
// entry. No secret, no PFX path, no password.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Security.Configuration
{
    using System;
    using System.Collections.Generic;
    using System.Security.Cryptography.X509Certificates;

    /// <summary>Identity acquisition modes supported by <see cref="Credentials.TokenCredentialFactory"/>.</summary>
    public enum AuthMode
    {
        /// <summary>Not configured. Startup fails.</summary>
        Unspecified = 0,

        /// <summary>Client certificate from the Windows certificate store.</summary>
        Certificate = 1,

        /// <summary>Platform-assigned managed identity. Not available on domain-joined on-premises servers.</summary>
        ManagedIdentity = 2,

        /// <summary>
        /// Client secret read from Windows Credential Manager at runtime. For a
        /// tenant that will not issue a client certificate. The secret value is
        /// never in configuration; only the Credential Manager target name is.
        /// </summary>
        ClientSecret = 3,
    }

    /// <summary>
    /// Binding target for the "Auth" configuration section.
    /// </summary>
    public sealed class AuthOptions
    {
        /// <summary>Gets or sets the credential mode: Certificate, ManagedIdentity or ClientSecret.</summary>
        public string Mode { get; set; } = "Certificate";

        /// <summary>Gets or sets the Entra tenant ID. Not sensitive.</summary>
        public string TenantId { get; set; } = string.Empty;

        /// <summary>Gets or sets the application (client) ID. Not sensitive.</summary>
        public string ClientId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the certificate store location: LocalMachine for a service
        /// account, CurrentUser for interactive development.
        /// </summary>
        public string CertificateStoreLocation { get; set; } = "LocalMachine";

        /// <summary>
        /// Gets or sets the thumbprints to try, in order. A list rather than a
        /// single value so a replacement certificate can be installed and proven
        /// before the outgoing one is removed.
        /// </summary>
        public List<string> CertificateThumbprints { get; set; } = new List<string>();

        /// <summary>
        /// Gets or sets an optional subject name (for example "CN=sqltickets.contoso.local").
        /// Certificates matching the subject are used after the listed thumbprints,
        /// newest first, so a rotation can complete without a configuration change.
        /// </summary>
        public string CertificateSubject { get; set; } = string.Empty;

        /// <summary>Gets or sets how many days before expiry the daily warning starts.</summary>
        public int ExpiryWarningDays { get; set; } = 30;

        /// <summary>
        /// Gets or sets the Windows Credential Manager target holding the client
        /// secret, used when Mode is ClientSecret. This is a lookup key, not a
        /// credential: the value lives in Credential Manager under the service
        /// account and never appears in configuration. See docs/RUNBOOK.md for
        /// how to store it as an account that cannot log on interactively.
        /// </summary>
        public string ClientSecretCredentialTarget { get; set; } = string.Empty;

        /// <summary>Gets the parsed credential mode.</summary>
        public AuthMode ParsedMode
        {
            get
            {
                if (string.Equals(this.Mode, "Certificate", StringComparison.OrdinalIgnoreCase))
                {
                    return AuthMode.Certificate;
                }

                if (string.Equals(this.Mode, "ManagedIdentity", StringComparison.OrdinalIgnoreCase))
                {
                    return AuthMode.ManagedIdentity;
                }

                if (string.Equals(this.Mode, "ClientSecret", StringComparison.OrdinalIgnoreCase))
                {
                    return AuthMode.ClientSecret;
                }

                return AuthMode.Unspecified;
            }
        }

        /// <summary>Gets the parsed store location, defaulting to LocalMachine.</summary>
        public StoreLocation ParsedStoreLocation
        {
            get
            {
                return string.Equals(this.CertificateStoreLocation, "CurrentUser", StringComparison.OrdinalIgnoreCase)
                    ? StoreLocation.CurrentUser
                    : StoreLocation.LocalMachine;
            }
        }

        /// <summary>
        /// Entra client secrets are around 40 characters of base64 with no
        /// separators. Target names in this deployment look like a path. The test
        /// is deliberately loose: it exists to catch a paste, not to validate a
        /// secret, and a false positive is a one word rename.
        /// </summary>
        private static bool LooksLikeASecretValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length < 30)
            {
                return false;
            }

            foreach (char c in value)
            {
                if (c == '/' || c == '\\' || c == ':' || c == ' ' || c == '-' || c == '_' || c == '.')
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Adds a message for every invalid field rather than stopping at the first.</summary>
        public void Validate(ValidationErrors errors, string path)
        {
            if (errors == null)
            {
                throw new ArgumentNullException(nameof(errors));
            }

            errors.RequireOneOf(path + ":Mode", this.Mode, "Certificate", "ManagedIdentity", "ClientSecret");
            errors.RequireGuid(path + ":ClientId", this.ClientId);

            if (this.ParsedMode == AuthMode.Certificate)
            {
                errors.RequireGuid(path + ":TenantId", this.TenantId);
                errors.RequireOneOf(
                    path + ":CertificateStoreLocation",
                    this.CertificateStoreLocation,
                    "LocalMachine",
                    "CurrentUser");

                bool hasThumbprint = this.CertificateThumbprints != null && this.CertificateThumbprints.Count > 0;
                bool hasSubject = !string.IsNullOrWhiteSpace(this.CertificateSubject);

                if (!hasThumbprint && !hasSubject)
                {
                    errors.Add(
                        path + ":CertificateThumbprints",
                        "at least one thumbprint or a CertificateSubject is required when Mode is Certificate.");
                }

                if (hasThumbprint)
                {
                    for (int i = 0; i < this.CertificateThumbprints.Count; i++)
                    {
                        string thumbprint = Certificates.CertificateSelector.NormalizeThumbprint(
                            this.CertificateThumbprints[i]);

                        if (!Certificates.CertificateSelector.IsWellFormedThumbprint(thumbprint))
                        {
                            errors.Add(
                                path + ":CertificateThumbprints[" + i + "]",
                                "must be a 40 character SHA-1 thumbprint in hexadecimal.");
                        }
                    }
                }
            }

            if (this.ParsedMode == AuthMode.ClientSecret)
            {
                errors.RequireGuid(path + ":TenantId", this.TenantId);
                errors.RequireNonEmpty(path + ":ClientSecretCredentialTarget", this.ClientSecretCredentialTarget);

                // A secret pasted into configuration is the failure this mode
                // exists to avoid, so it is rejected by shape rather than left to
                // the build time scan, which only sees files in the repository.
                if (LooksLikeASecretValue(this.ClientSecretCredentialTarget))
                {
                    errors.Add(
                        path + ":ClientSecretCredentialTarget",
                        "looks like a secret value rather than a Credential Manager target name. This key holds " +
                        "the name of the entry, for example 'SqlTicketsConnector/EntraClientSecret'. Store the " +
                        "secret itself with cmdkey under the service account.");
                }
            }

            errors.RequireRange(path + ":ExpiryWarningDays", this.ExpiryWarningDays, 0, 365);
        }
    }
}
