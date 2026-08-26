// ---------------------------------------------------------------------------
// KeyVaultOptions.cs
// The "KeyVault" section. Holds the vault URI and the *names* of secrets.
// A secret name is not a secret; a secret value never appears in configuration.
// ---------------------------------------------------------------------------

namespace Connector.Security.Configuration
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Binding target for the "KeyVault" configuration section.
    /// </summary>
    public sealed class KeyVaultOptions
    {
        /// <summary>Well-known key for the SQL login password secret name.</summary>
        public const string SqlPasswordKey = "SqlPassword";

        /// <summary>Gets or sets the vault URI, for example https://kv-connectors-prod.vault.azure.net/.</summary>
        public string Uri { get; set; } = string.Empty;

        /// <summary>Gets or sets how long a resolved secret stays in memory before it is fetched again.</summary>
        public int SecretCacheTtlMinutes { get; set; } = 60;

        /// <summary>
        /// Gets or sets the logical name to vault secret name map, for example
        /// { "SqlPassword": "sql-reader-password" }.
        /// </summary>
        public Dictionary<string, string> Secrets { get; set; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Gets the vault secret name for a logical key, or null when unmapped.</summary>
        public string SecretName(string logicalKey)
        {
            if (this.Secrets == null)
            {
                return null;
            }

            string name;
            return this.Secrets.TryGetValue(logicalKey, out name) && !string.IsNullOrWhiteSpace(name)
                ? name
                : null;
        }

        /// <summary>
        /// Validates the section. <paramref name="vaultRequired"/> is set by the
        /// caller when the current configuration actually needs a secret; a
        /// connector using Windows integrated SQL auth needs no vault at all.
        /// </summary>
        public void Validate(ValidationErrors errors, string path, bool vaultRequired)
        {
            if (errors == null)
            {
                throw new ArgumentNullException(nameof(errors));
            }

            errors.RequireRange(path + ":SecretCacheTtlMinutes", this.SecretCacheTtlMinutes, 1, 1440);

            bool hasUri = !string.IsNullOrWhiteSpace(this.Uri);

            if (!hasUri)
            {
                if (vaultRequired)
                {
                    errors.Add(path + ":Uri", "is required because the current configuration resolves a secret.");
                }

                return;
            }

            // The Uri property shadows the type name inside this class, so the
            // System.Uri members are qualified.
            System.Uri parsed;
            if (!System.Uri.TryCreate(this.Uri, UriKind.Absolute, out parsed) ||
                !string.Equals(parsed.Scheme, System.Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(path + ":Uri", "must be an absolute https URI.");
            }
        }
    }
}
