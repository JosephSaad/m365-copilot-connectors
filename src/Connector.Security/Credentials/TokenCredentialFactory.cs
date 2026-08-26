// ---------------------------------------------------------------------------
// TokenCredentialFactory.cs
// The only place a TokenCredential is constructed in this solution.
//
// DefaultAzureCredential is deliberately absent. Its fallback chain means the
// identity that actually authenticated depends on ambient environment state,
// which cannot be evidenced in an audit. Test projects may use it; production
// paths may not.
// ---------------------------------------------------------------------------

namespace Connector.Security.Credentials
{
    using System;
    using System.Collections.Generic;
    using Azure.Core;
    using Azure.Identity;
    using Serilog;
    using Connector.Security.Certificates;
    using Connector.Security.Configuration;
    using Connector.Security.Secrets;

    /// <summary>
    /// Builds the credential named by Auth:Mode, and fails loudly for anything else.
    /// </summary>
    public static class TokenCredentialFactory
    {
        /// <summary>
        /// Returns the credential for the configured mode: ManagedIdentity,
        /// ClientSecret, Certificate, then a clear failure.
        /// </summary>
        public static TokenCredential Create(AuthOptions auth, ICertificateResolver resolver, ILogger logger)
        {
            if (auth == null)
            {
                throw new ArgumentNullException(nameof(auth));
            }

            ILogger log = logger ?? Log.Logger;

            switch (auth.ParsedMode)
            {
                case AuthMode.ManagedIdentity:
                    log.Information(
                        "Auth:Mode is ManagedIdentity. Using the platform assigned identity for client {ClientId}.",
                        string.IsNullOrWhiteSpace(auth.ClientId) ? "(system assigned)" : auth.ClientId);

                    // ManagedIdentityId rather than the string overload: the
                    // latter is obsolete from Azure.Identity 1.21.0 because it
                    // could not express which kind of identifier was meant.
                    return string.IsNullOrWhiteSpace(auth.ClientId)
                        ? new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned)
                        : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(auth.ClientId));

                case AuthMode.ClientSecret:
                    // The bootstrap problem this mode solves: the credential that
                    // reaches Key Vault cannot itself live in Key Vault. It comes
                    // from Credential Manager, under the service account, and the
                    // only thing in configuration is the entry's name.
                    //
                    // The OS test is not defensive coding. Credential Manager is a
                    // Windows facility, and the alternative to failing here is a
                    // PlatformNotSupportedException from the P/Invoke with nothing
                    // to act on.
                    if (!OperatingSystem.IsWindows())
                    {
                        throw new InvalidOperationException(
                            "Auth:Mode is ClientSecret, which reads the secret from Windows Credential Manager " +
                            "and is therefore Windows only. Use Certificate or ManagedIdentity on this platform.");
                    }

                    string target = auth.ClientSecretCredentialTarget;

                    log.Information(
                        "Auth:Mode is ClientSecret. Reading Credential Manager target {CredentialTarget} for " +
                        "client {ClientId} in tenant {TenantId}.",
                        target,
                        auth.ClientId,
                        auth.TenantId);

                    // Read once, at startup, so a missing or unreadable entry is a
                    // deployment failure rather than a token failure during a
                    // crawl. A secret rotated in place needs a service restart;
                    // that trade is recorded in docs/SECURITY.md and the rotation
                    // procedure is in docs/RUNBOOK.md.
                    string secret = WindowsCredentialStore.Read(target);

                    log.Information(
                        "Client secret resolved from Credential Manager target {CredentialTarget}. The value is " +
                        "held in memory only and is never logged.",
                        target);

                    return new ClientSecretCredential(auth.TenantId, auth.ClientId, secret);

                case AuthMode.Certificate:
                    if (resolver == null)
                    {
                        throw new ArgumentNullException(nameof(resolver));
                    }

                    IReadOnlyList<CertificateCandidate> candidates = resolver.ResolveCandidates();

                    log.Information(
                        "Auth:Mode is Certificate. {CandidateCount} candidate certificate(s) resolved for client {ClientId} in tenant {TenantId}.",
                        candidates.Count,
                        auth.ClientId,
                        auth.TenantId);

                    return new RotatingCertificateCredential(auth.TenantId, auth.ClientId, candidates, log);

                default:
                    throw new InvalidOperationException(
                        "Auth:Mode must be 'Certificate', 'ManagedIdentity' or 'ClientSecret'. Found '" +
                        (auth.Mode ?? "(null)") + "'. DefaultAzureCredential is not used in production paths " +
                        "because its fallback chain makes the authenticating identity non-deterministic.");
            }
        }
    }
}
