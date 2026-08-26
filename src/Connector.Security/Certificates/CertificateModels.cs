// ---------------------------------------------------------------------------
// CertificateModels.cs
// Value types describing what the resolver found and what it rejected. The
// rejection list is what turns "authentication failed" into a message an
// operator can act on at 2am.
// ---------------------------------------------------------------------------

namespace Connector.Security.Certificates
{
    using System;
    using System.Collections.Generic;
    using System.Security.Cryptography.X509Certificates;

    /// <summary>Why a certificate in the store was not usable.</summary>
    public enum CertificateRejectionReason
    {
        /// <summary>No certificate with that thumbprint is present in the store.</summary>
        NotFound = 0,

        /// <summary>The certificate expired.</summary>
        Expired = 1,

        /// <summary>The certificate is not valid yet.</summary>
        NotYetValid = 2,

        /// <summary>The certificate has no associated private key.</summary>
        NoPrivateKey = 3,

        /// <summary>The private key exists but this process identity cannot use it.</summary>
        PrivateKeyUnreadable = 4,
    }

    /// <summary>What to look for in the certificate store.</summary>
    public sealed class CertificateSelectionCriteria
    {
        /// <summary>Gets or sets the thumbprints to try, in configuration order.</summary>
        public IReadOnlyList<string> Thumbprints { get; set; } = new List<string>();

        /// <summary>Gets or sets an optional subject name used after the thumbprints.</summary>
        public string Subject { get; set; } = string.Empty;

        /// <summary>Gets or sets how many days before expiry a warning is raised.</summary>
        public int ExpiryWarningDays { get; set; } = 30;
    }

    /// <summary>A certificate that passed every check and may be used for authentication.</summary>
    public sealed class CertificateCandidate
    {
        /// <summary>Initializes a candidate.</summary>
        public CertificateCandidate(
            X509Certificate2 certificate,
            bool matchedBySubject,
            DateTimeOffset nowUtc,
            int expiryWarningDays)
        {
            if (certificate == null)
            {
                throw new ArgumentNullException(nameof(certificate));
            }

            this.Certificate = certificate;
            this.Thumbprint = certificate.Thumbprint;
            this.Subject = certificate.Subject;
            this.NotAfterUtc = certificate.NotAfter.ToUniversalTime();
            this.MatchedBySubject = matchedBySubject;
            this.DaysUntilExpiry = (int)Math.Floor((this.NotAfterUtc - nowUtc).TotalDays);
            this.ExpiresSoon = this.DaysUntilExpiry <= expiryWarningDays;
        }

        /// <summary>Gets the certificate, including its private key.</summary>
        public X509Certificate2 Certificate { get; }

        /// <summary>Gets the thumbprint. Not a secret; safe to log.</summary>
        public string Thumbprint { get; }

        /// <summary>Gets the subject. Not a secret; safe to log.</summary>
        public string Subject { get; }

        /// <summary>Gets the expiry instant in UTC.</summary>
        public DateTimeOffset NotAfterUtc { get; }

        /// <summary>Gets a value indicating whether this was found by subject rather than by thumbprint.</summary>
        public bool MatchedBySubject { get; }

        /// <summary>Gets the whole days remaining before expiry.</summary>
        public int DaysUntilExpiry { get; }

        /// <summary>Gets a value indicating whether the certificate is inside the expiry warning window.</summary>
        public bool ExpiresSoon { get; }
    }

    /// <summary>A configured certificate that could not be used, and why.</summary>
    public sealed class CertificateRejection
    {
        /// <summary>Initializes a rejection.</summary>
        public CertificateRejection(string identifier, CertificateRejectionReason reason, string detail)
        {
            this.Identifier = identifier;
            this.Reason = reason;
            this.Detail = detail;
        }

        /// <summary>Gets the thumbprint or subject that was searched for.</summary>
        public string Identifier { get; }

        /// <summary>Gets the classification.</summary>
        public CertificateRejectionReason Reason { get; }

        /// <summary>Gets the human readable explanation.</summary>
        public string Detail { get; }

        /// <summary>Renders the rejection for a log line or an exception message.</summary>
        public override string ToString()
        {
            return this.Identifier + " -> " + this.Reason + ": " + this.Detail;
        }
    }

    /// <summary>The outcome of a store search.</summary>
    public sealed class CertificateSelectionResult
    {
        /// <summary>Initializes a result.</summary>
        public CertificateSelectionResult(
            IReadOnlyList<CertificateCandidate> candidates,
            IReadOnlyList<CertificateRejection> rejections)
        {
            this.Candidates = candidates ?? new List<CertificateCandidate>();
            this.Rejections = rejections ?? new List<CertificateRejection>();
        }

        /// <summary>Gets the usable certificates, in the order they should be tried.</summary>
        public IReadOnlyList<CertificateCandidate> Candidates { get; }

        /// <summary>Gets every certificate that was considered and skipped.</summary>
        public IReadOnlyList<CertificateRejection> Rejections { get; }

        /// <summary>Gets a value indicating whether anything usable was found.</summary>
        public bool HasCandidates
        {
            get { return this.Candidates.Count > 0; }
        }
    }
}
