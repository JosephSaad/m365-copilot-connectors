// ---------------------------------------------------------------------------
// StoreCertificateResolver.cs
// The single production certificate source: the Windows certificate store.
//
// There is deliberately no PFX loader. A PFX on disk needs a password on disk to
// open it, which recreates exactly the problem certificate authentication is
// meant to solve.
// ---------------------------------------------------------------------------

namespace Connector.Security.Certificates
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Security.Cryptography.X509Certificates;
    using Serilog;
    using Connector.Security.Configuration;

    /// <summary>
    /// Resolves client certificates from StoreName.My at the configured store
    /// location.
    /// </summary>
    public sealed class StoreCertificateResolver : ICertificateResolver
    {
        private readonly StoreLocation location;
        private readonly CertificateSelectionCriteria criteria;
        private readonly ILogger logger;
        private readonly TimeProvider time;

        private string lastLoggedSelectionThumbprint;

        /// <summary>Initializes the resolver from the Auth section.</summary>
        public StoreCertificateResolver(AuthOptions auth, ILogger logger, TimeProvider timeProvider = null)
            : this(
                  auth == null ? StoreLocation.LocalMachine : auth.ParsedStoreLocation,
                  BuildCriteria(auth),
                  logger,
                  timeProvider)
        {
        }

        /// <summary>Initializes the resolver explicitly.</summary>
        public StoreCertificateResolver(
            StoreLocation location,
            CertificateSelectionCriteria criteria,
            ILogger logger,
            TimeProvider timeProvider = null)
        {
            if (criteria == null)
            {
                throw new ArgumentNullException(nameof(criteria));
            }

            this.location = location;
            this.criteria = criteria;
            this.logger = logger ?? Log.Logger;
            this.time = timeProvider ?? TimeProvider.System;
        }

        /// <inheritdoc />
        public IReadOnlyList<CertificateCandidate> ResolveCandidates()
        {
            DateTimeOffset now = this.time.GetUtcNow();
            CertificateSelectionResult result = this.Search(now);

            foreach (CertificateRejection rejection in result.Rejections)
            {
                if (rejection.Reason == CertificateRejectionReason.Expired ||
                    rejection.Reason == CertificateRejectionReason.PrivateKeyUnreadable ||
                    rejection.Reason == CertificateRejectionReason.NoPrivateKey)
                {
                    this.logger.Error(
                        "Certificate {Thumbprint} in {StoreLocation}\\My cannot be used: {Reason} {Detail} " +
                        "Process identity: {ProcessIdentity}.",
                        rejection.Identifier,
                        this.location,
                        rejection.Reason,
                        rejection.Detail,
                        ProcessIdentity.Current());
                }
                else
                {
                    this.logger.Warning(
                        "Certificate {Thumbprint} in {StoreLocation}\\My was skipped: {Reason} {Detail}",
                        rejection.Identifier,
                        this.location,
                        rejection.Reason,
                        rejection.Detail);
                }
            }

            if (!result.HasCandidates)
            {
                throw new CertificateResolutionException(DescribeFailure(result, this.location), result.Rejections);
            }

            foreach (CertificateCandidate candidate in result.Candidates)
            {
                this.LogExpiry(candidate, now);
            }

            CertificateCandidate first = result.Candidates[0];
            if (!string.Equals(this.lastLoggedSelectionThumbprint, first.Thumbprint, StringComparison.OrdinalIgnoreCase))
            {
                this.lastLoggedSelectionThumbprint = first.Thumbprint;

                this.logger.Information(
                    "Certificate {Thumbprint} ({Subject}) selected from {StoreLocation}\\My. " +
                    "Matched by {MatchKind}, expires {NotAfter:o}, {CandidateCount} candidate(s) available.",
                    first.Thumbprint,
                    first.Subject,
                    this.location,
                    first.MatchedBySubject ? "subject" : "thumbprint",
                    first.NotAfterUtc,
                    result.Candidates.Count);
            }

            return result.Candidates;
        }

        /// <summary>
        /// Re-reads the store and logs expiry state without changing the selection.
        /// Called on a daily timer so an approaching expiry is visible in the log
        /// and in the SIEM before it becomes an outage.
        /// </summary>
        public void ReportExpiryState()
        {
            DateTimeOffset now = this.time.GetUtcNow();

            try
            {
                CertificateSelectionResult result = this.Search(now);

                foreach (CertificateRejection rejection in result.Rejections)
                {
                    if (rejection.Reason == CertificateRejectionReason.Expired)
                    {
                        this.logger.Error(
                            "Certificate {Thumbprint} has expired: {Detail}",
                            rejection.Identifier,
                            rejection.Detail);
                    }
                }

                foreach (CertificateCandidate candidate in result.Candidates)
                {
                    this.LogExpiry(candidate, now);
                }

                if (!result.HasCandidates)
                {
                    this.logger.Error(
                        "No usable authentication certificate is present in {StoreLocation}\\My. " +
                        "Authentication will fail on the next token request.",
                        this.location);
                }
            }
            catch (CertificateResolutionException ex)
            {
                this.logger.Error(Logging.RedactedException.Wrap(ex), "Daily certificate check failed.");
            }
        }

        private static CertificateSelectionCriteria BuildCriteria(AuthOptions auth)
        {
            if (auth == null)
            {
                throw new ArgumentNullException(nameof(auth));
            }

            return new CertificateSelectionCriteria
            {
                Thumbprints = auth.CertificateThumbprints ?? new List<string>(),
                Subject = auth.CertificateSubject,
                ExpiryWarningDays = auth.ExpiryWarningDays,
            };
        }

        /// <summary>
        /// Renders the "no usable certificate" message. Public so the message an
        /// operator will actually read is covered by a test.
        /// </summary>
        public static string DescribeFailure(CertificateSelectionResult result, StoreLocation location)
        {
            string detail = result.Rejections.Count == 0
                ? "No thumbprints or subject were configured."
                : string.Join("; ", result.Rejections.Select(r => r.ToString()));

            return string.Format(
                CultureInfo.InvariantCulture,
                "No usable client certificate was found in {0}\\My. Process identity: {1}. Tried: {2}. " +
                "Confirm the certificate is installed in that store with its private key, and that the process " +
                "identity has Read permission on the key (certlm.msc, right click the certificate, All Tasks, " +
                "Manage Private Keys).",
                location,
                ProcessIdentity.Current(),
                detail);
        }

        private CertificateSelectionResult Search(DateTimeOffset now)
        {
            var snapshot = new List<X509Certificate2>();

            using (var store = new X509Store(StoreName.My, this.location))
            {
                try
                {
                    store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                }
                catch (Exception ex)
                {
                    throw new CertificateResolutionException(
                        "Could not open the " + this.location + "\\My certificate store as " +
                        ProcessIdentity.Current() + ".",
                        ex);
                }

                foreach (X509Certificate2 certificate in store.Certificates)
                {
                    snapshot.Add(certificate);
                }

                CertificateSelectionResult result = CertificateSelector.Select(snapshot, this.criteria, now);

                // Copy the winners so they stay valid once the store handle closes,
                // then release every handle taken from the store.
                var copies = new List<CertificateCandidate>(result.Candidates.Count);
                foreach (CertificateCandidate candidate in result.Candidates)
                {
                    copies.Add(new CertificateCandidate(
                        new X509Certificate2(candidate.Certificate),
                        candidate.MatchedBySubject,
                        now,
                        this.criteria.ExpiryWarningDays));
                }

                foreach (X509Certificate2 certificate in snapshot)
                {
                    certificate.Dispose();
                }

                return new CertificateSelectionResult(copies, result.Rejections);
            }
        }

        private void LogExpiry(CertificateCandidate candidate, DateTimeOffset now)
        {
            if (candidate.NotAfterUtc < now)
            {
                this.logger.Error(
                    "Certificate {Thumbprint} ({Subject}) expired on {NotAfter:o}.",
                    candidate.Thumbprint,
                    candidate.Subject,
                    candidate.NotAfterUtc);
                return;
            }

            if (candidate.ExpiresSoon)
            {
                this.logger.Warning(
                    "Certificate {Thumbprint} ({Subject}) expires in {DaysRemaining} day(s) on {NotAfter:o}. " +
                    "Start the rotation described in docs/RUNBOOK.md.",
                    candidate.Thumbprint,
                    candidate.Subject,
                    candidate.DaysUntilExpiry,
                    candidate.NotAfterUtc);
            }
        }
    }
}
