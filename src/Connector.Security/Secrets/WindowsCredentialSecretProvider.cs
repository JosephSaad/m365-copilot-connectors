// ---------------------------------------------------------------------------
// WindowsCredentialSecretProvider.cs
// ISecretProvider over Windows Credential Manager.
//
// This exists to solve a bootstrap problem the Key Vault provider cannot: the
// credential used to reach the vault has to come from somewhere that is not the
// vault. A certificate in the machine store is the preferred answer and remains
// the default. Where a tenant will not issue one, this is the alternative that
// keeps the secret out of source, configuration, environment variables and
// deployment scripts.
//
// It is a deliberate deviation from the original control set, which excluded
// DPAPI backed secret storage. Recorded as such in docs/SECURITY.md, with what
// is gained and what is given up.
// ---------------------------------------------------------------------------

namespace Connector.Security.Secrets
{
    using System;
    using System.Runtime.Versioning;
    using System.Threading;
    using System.Threading.Tasks;
    using Serilog;

    /// <summary>
    /// Resolves secrets from Windows Credential Manager, where the secret name is
    /// the Credential Manager target.
    /// </summary>
    /// <remarks>
    /// Reads are cheap and local, so this provider does no caching of its own.
    /// Wrap it in <see cref="CachingSecretProvider"/> only if a caller resolves
    /// the same secret in a hot path; the point of the cache elsewhere is to
    /// avoid a network round trip that does not happen here.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    public sealed class WindowsCredentialSecretProvider : ISecretProvider
    {
        private readonly ILogger logger;

        /// <summary>Initializes the provider.</summary>
        public WindowsCredentialSecretProvider(ILogger logger)
        {
            this.logger = logger ?? Log.Logger;
        }

        /// <summary>Reads the secret stored against the named Credential Manager target.</summary>
        public Task<string> GetSecretAsync(string name, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("A Credential Manager target name is required.", nameof(name));
            }

            ct.ThrowIfCancellationRequested();

            // The target name is logged; the value never is.
            this.logger.Debug("Reading Credential Manager target {CredentialTarget}.", name);

            return Task.FromResult(WindowsCredentialStore.Read(name));
        }

        /// <summary>
        /// No-op. There is nothing cached here to invalidate: every read goes to
        /// Credential Manager, so a secret rotated in place is picked up by the
        /// next resolution without a restart.
        /// </summary>
        public Task InvalidateAsync(string name, CancellationToken ct)
        {
            return Task.CompletedTask;
        }
    }
}
