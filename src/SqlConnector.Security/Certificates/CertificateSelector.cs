// ---------------------------------------------------------------------------
// CertificateSelector.cs
// The selection rules, kept free of any store access so they can be unit tested
// against certificates created in memory.
// ---------------------------------------------------------------------------

namespace SqlConnector.Security.Certificates
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Security.Cryptography.X509Certificates;

    /// <summary>
    /// Chooses which certificates may be used, in which order.
    /// </summary>
    public static class CertificateSelector
    {
        /// <summary>Strips spaces and invisible characters copied out of certmgr.msc and upper-cases the value.</summary>
        public static string NormalizeThumbprint(string thumbprint)
        {
            if (string.IsNullOrWhiteSpace(thumbprint))
            {
                return string.Empty;
            }

            var buffer = new char[thumbprint.Length];
            int length = 0;

            foreach (char c in thumbprint)
            {
                if (Uri.IsHexDigit(c))
                {
                    buffer[length++] = char.ToUpperInvariant(c);
                }
            }

            return new string(buffer, 0, length);
        }

        /// <summary>Returns true when the value looks like a SHA-1 thumbprint.</summary>
        public static bool IsWellFormedThumbprint(string normalizedThumbprint)
        {
            return !string.IsNullOrEmpty(normalizedThumbprint) && normalizedThumbprint.Length == 40;
        }

        /// <summary>
        /// Selects usable certificates from the supplied set.
        /// Configured thumbprints come first, in order, so a rotation is a
        /// deterministic list reorder rather than a guess. Subject matches follow,
        /// newest expiry first, which is what lets a replacement certificate be
        /// picked up without editing configuration.
        /// </summary>
        public static CertificateSelectionResult Select(
            IEnumerable<X509Certificate2> available,
            CertificateSelectionCriteria criteria,
            DateTimeOffset nowUtc)
        {
            if (available == null)
            {
                throw new ArgumentNullException(nameof(available));
            }

            if (criteria == null)
            {
                throw new ArgumentNullException(nameof(criteria));
            }

            List<X509Certificate2> pool = available.Where(c => c != null).ToList();
            var candidates = new List<CertificateCandidate>();
            var rejections = new List<CertificateRejection>();
            var accepted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            IReadOnlyList<string> thumbprints = criteria.Thumbprints ?? new List<string>();

            foreach (string configured in thumbprints)
            {
                string normalized = NormalizeThumbprint(configured);
                if (!IsWellFormedThumbprint(normalized))
                {
                    rejections.Add(new CertificateRejection(
                        configured,
                        CertificateRejectionReason.NotFound,
                        "not a 40 character SHA-1 thumbprint."));
                    continue;
                }

                X509Certificate2 match = pool.FirstOrDefault(
                    c => string.Equals(NormalizeThumbprint(c.Thumbprint), normalized, StringComparison.Ordinal));

                if (match == null)
                {
                    rejections.Add(new CertificateRejection(
                        normalized,
                        CertificateRejectionReason.NotFound,
                        "not present in the certificate store."));
                    continue;
                }

                CertificateRejection rejection;
                if (!IsUsable(match, nowUtc, out rejection))
                {
                    rejections.Add(rejection);
                    continue;
                }

                if (accepted.Add(normalized))
                {
                    candidates.Add(new CertificateCandidate(match, false, nowUtc, criteria.ExpiryWarningDays));
                }
            }

            if (!string.IsNullOrWhiteSpace(criteria.Subject))
            {
                IEnumerable<X509Certificate2> subjectMatches = pool
                    .Where(c => SubjectMatches(c, criteria.Subject))
                    .OrderByDescending(c => c.NotAfter);

                bool anySubjectMatch = false;

                foreach (X509Certificate2 match in subjectMatches)
                {
                    anySubjectMatch = true;
                    string normalized = NormalizeThumbprint(match.Thumbprint);

                    if (accepted.Contains(normalized))
                    {
                        continue;
                    }

                    CertificateRejection rejection;
                    if (!IsUsable(match, nowUtc, out rejection))
                    {
                        rejections.Add(rejection);
                        continue;
                    }

                    accepted.Add(normalized);
                    candidates.Add(new CertificateCandidate(match, true, nowUtc, criteria.ExpiryWarningDays));
                }

                if (!anySubjectMatch)
                {
                    rejections.Add(new CertificateRejection(
                        criteria.Subject,
                        CertificateRejectionReason.NotFound,
                        "no certificate in the store carries that subject."));
                }
            }

            return new CertificateSelectionResult(candidates, rejections);
        }

        /// <summary>
        /// Proves the process identity can actually use the private key by signing
        /// a byte with it. A certificate that is present but whose key ACL excludes
        /// the service account otherwise fails much later, as an opaque
        /// authentication error during the first crawl.
        /// </summary>
        public static bool TryUsePrivateKey(X509Certificate2 certificate, out string failureDetail)
        {
            if (certificate == null)
            {
                throw new ArgumentNullException(nameof(certificate));
            }

            if (!certificate.HasPrivateKey)
            {
                failureDetail = "the certificate has no associated private key. Import the certificate with its key, " +
                    "not just the public part.";
                return false;
            }

            try
            {
                byte[] probe = new byte[] { 0x01 };

                using (RSA rsa = certificate.GetRSAPrivateKey())
                {
                    if (rsa != null)
                    {
                        rsa.SignData(probe, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                        failureDetail = null;
                        return true;
                    }
                }

                using (ECDsa ecdsa = certificate.GetECDsaPrivateKey())
                {
                    if (ecdsa != null)
                    {
                        ecdsa.SignData(probe, HashAlgorithmName.SHA256);
                        failureDetail = null;
                        return true;
                    }
                }

                failureDetail = "the private key uses an algorithm this connector cannot use for client authentication. " +
                    "Use an RSA or ECDSA certificate.";
                return false;
            }
            catch (CryptographicException ex)
            {
                failureDetail = "the private key could not be used: " + ex.Message;
                return false;
            }
            catch (NotSupportedException ex)
            {
                failureDetail = "the private key could not be used: " + ex.Message;
                return false;
            }
        }

        private static bool IsUsable(X509Certificate2 certificate, DateTimeOffset nowUtc, out CertificateRejection rejection)
        {
            string identifier = NormalizeThumbprint(certificate.Thumbprint);

            if (certificate.NotAfter.ToUniversalTime() < nowUtc)
            {
                rejection = new CertificateRejection(
                    identifier,
                    CertificateRejectionReason.Expired,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "expired on {0:o}. Subject {1}.",
                        certificate.NotAfter.ToUniversalTime(),
                        certificate.Subject));
                return false;
            }

            if (certificate.NotBefore.ToUniversalTime() > nowUtc)
            {
                rejection = new CertificateRejection(
                    identifier,
                    CertificateRejectionReason.NotYetValid,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "is not valid until {0:o}. Subject {1}.",
                        certificate.NotBefore.ToUniversalTime(),
                        certificate.Subject));
                return false;
            }

            string detail;
            if (!TryUsePrivateKey(certificate, out detail))
            {
                rejection = new CertificateRejection(
                    identifier,
                    certificate.HasPrivateKey
                        ? CertificateRejectionReason.PrivateKeyUnreadable
                        : CertificateRejectionReason.NoPrivateKey,
                    detail);
                return false;
            }

            rejection = null;
            return true;
        }

        private static bool SubjectMatches(X509Certificate2 certificate, string subject)
        {
            string configured = subject.Trim();

            return string.Equals(certificate.Subject, configured, StringComparison.OrdinalIgnoreCase) ||
                   certificate.Subject.IndexOf(configured, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
