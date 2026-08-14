// ---------------------------------------------------------------------------
// TokenCredentialFactory.cs
// The only place a TokenCredential is constructed in this solution.
//
// DefaultAzureCredential is deliberately absent. Its fallback chain means the
// identity that actually authenticated depends on ambient environment state,
// which cannot be evidenced in an audit. Test projects may use it; production
// paths may not.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Security.Credentials
{
    using System;
    using System.Collections.Generic;
    using Azure.Core;
    using Azure.Identity;
    using Serilog;
    using SqlTicketsConnector.Security.Certificates;
    using SqlTicketsConnector.Security.Configuration;

    /// <summary>
    /// Builds the credential named by Auth:Mode, and fails loudly for anything else.
    /// </summary>
    public static class TokenCredentialFactory
    {
        /// <summary>
        /// Returns the credential for the configured mode:
        /// ManagedIdentity, then Certificate, then a clear failure.
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

                    return string.IsNullOrWhiteSpace(auth.ClientId)
                        ? new ManagedIdentityCredential()
                        : new ManagedIdentityCredential(auth.ClientId);

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
                        "Auth:Mode must be 'Certificate' or 'ManagedIdentity'. Found '" + (auth.Mode ?? "(null)") +
                        "'. DefaultAzureCredential is not used in production paths because its fallback chain makes " +
                        "the authenticating identity non-deterministic.");
            }
        }
    }
}
