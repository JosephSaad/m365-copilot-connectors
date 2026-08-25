// ---------------------------------------------------------------------------
// RotatingCertificateCredential.cs
// Tries each resolved certificate in order until one produces a token.
//
// The candidates are tried lazily, on the first token request, rather than
// probed at startup. A service that refuses to start because the directory was
// briefly unreachable is worse than one that starts and reports a clear
// authentication error on its first crawl.
// ---------------------------------------------------------------------------

namespace SqlConnector.Security.Credentials
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Azure.Core;
    using Azure.Identity;
    using Serilog;
    using SqlConnector.Security.Certificates;

    /// <summary>
    /// A <see cref="TokenCredential"/> over an ordered list of client certificates.
    /// </summary>
    public sealed class RotatingCertificateCredential : TokenCredential
    {
        private readonly List<Attempt> attempts = new List<Attempt>();
        private readonly ILogger logger;
        private readonly object gate = new object();

        private int preferredIndex;
        private string reportedThumbprint;

        /// <summary>Initializes the credential over the resolved candidates.</summary>
        public RotatingCertificateCredential(
            string tenantId,
            string clientId,
            IReadOnlyList<CertificateCandidate> candidates,
            ILogger logger)
        {
            if (string.IsNullOrWhiteSpace(tenantId))
            {
                throw new ArgumentException("Tenant ID is required.", nameof(tenantId));
            }

            if (string.IsNullOrWhiteSpace(clientId))
            {
                throw new ArgumentException("Client ID is required.", nameof(clientId));
            }

            if (candidates == null || candidates.Count == 0)
            {
                throw new ArgumentException("At least one certificate candidate is required.", nameof(candidates));
            }

            this.logger = logger ?? Log.Logger;

            foreach (CertificateCandidate candidate in candidates)
            {
                // SendCertificateChain enables subject name and issuer authentication,
                // so a renewed certificate from the same issuer keeps working even
                // before the app registration is updated with the new thumbprint.
                var options = new ClientCertificateCredentialOptions
                {
                    SendCertificateChain = true,
                };

                this.attempts.Add(new Attempt(
                    candidate,
                    new ClientCertificateCredential(tenantId, clientId, candidate.Certificate, options)));
            }
        }

        /// <summary>Gets the thumbprint that last produced a token, or null before the first success.</summary>
        public string ActiveThumbprint
        {
            get
            {
                lock (this.gate)
                {
                    return this.reportedThumbprint;
                }
            }
        }

        /// <inheritdoc />
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            return this.Acquire(
                (attempt, ct) => new ValueTask<AccessToken>(attempt.Credential.GetToken(requestContext, ct)),
                cancellationToken).GetAwaiter().GetResult();
        }

        /// <inheritdoc />
        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken)
        {
            return this.Acquire(
                (attempt, ct) => attempt.Credential.GetTokenAsync(requestContext, ct),
                cancellationToken);
        }

        private async ValueTask<AccessToken> Acquire(
            Func<Attempt, CancellationToken, ValueTask<AccessToken>> acquire,
            CancellationToken cancellationToken)
        {
            var failures = new List<string>();
            Exception lastFailure = null;

            int start;
            lock (this.gate)
            {
                start = this.preferredIndex;
            }

            for (int offset = 0; offset < this.attempts.Count; offset++)
            {
                int index = (start + offset) % this.attempts.Count;
                Attempt attempt = this.attempts[index];

                try
                {
                    AccessToken token = await acquire(attempt, cancellationToken).ConfigureAwait(false);
                    this.ReportSuccess(index, attempt);
                    return token;
                }
                catch (AuthenticationFailedException ex)
                {
                    lastFailure = ex;
                    failures.Add(attempt.Candidate.Thumbprint + ": " + ex.Message);

                    // The exception carries the AADSTS code that says WHY this
                    // certificate was rejected. When a later candidate succeeds
                    // this warning is the only record of it, so the exception goes
                    // with it - message and stack, no key material.
                    this.logger.Warning(
                        Logging.RedactedException.Wrap(ex),
                        "Certificate {Thumbprint} ({Subject}) did not authenticate. Trying the next candidate.",
                        attempt.Candidate.Thumbprint,
                        attempt.Candidate.Subject);
                }
            }

            throw new AuthenticationFailedException(
                "None of the configured certificates authenticated against Entra ID. Tried: " +
                string.Join("; ", failures) +
                ". Confirm the public certificate is uploaded to the app registration and that the " +
                "thumbprints in Auth:CertificateThumbprints match.",
                lastFailure);
        }

        private void ReportSuccess(int index, Attempt attempt)
        {
            bool report = false;

            lock (this.gate)
            {
                this.preferredIndex = index;

                if (!string.Equals(this.reportedThumbprint, attempt.Candidate.Thumbprint, StringComparison.OrdinalIgnoreCase))
                {
                    this.reportedThumbprint = attempt.Candidate.Thumbprint;
                    report = true;
                }
            }

            if (report)
            {
                // First use of a thumbprint is the line an operator looks for after a
                // rotation to confirm the new certificate is the one in play.
                this.logger.Information(
                    "Authenticated to Entra ID with certificate {Thumbprint} ({Subject}), expires {NotAfter:o}.",
                    attempt.Candidate.Thumbprint,
                    attempt.Candidate.Subject,
                    attempt.Candidate.NotAfterUtc);
            }
        }

        private sealed class Attempt
        {
            public Attempt(CertificateCandidate candidate, ClientCertificateCredential credential)
            {
                this.Candidate = candidate;
                this.Credential = credential;
            }

            public CertificateCandidate Candidate { get; }

            public ClientCertificateCredential Credential { get; }
        }
    }
}
