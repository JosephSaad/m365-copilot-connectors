// ---------------------------------------------------------------------------
// CertificateResolutionTests.cs
// Control evidence for certificate selection and rotation:
//   the first usable thumbprint in the configured list wins,
//   an expired certificate is skipped rather than used,
//   a certificate whose private key cannot be used produces a message naming
//   the process identity and the fix.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Security.Cryptography.X509Certificates;
    using Connector.Security.Certificates;
    using SqlTicketsConnector.Tests.TestSupport;
    using Xunit;

    public class CertificateResolutionTests
    {
        private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 8, 13, 0, 0, 0, TimeSpan.Zero);

        [Fact]
        public void First_valid_thumbprint_in_the_configured_order_is_selected()
        {
            using (X509Certificate2 current = Valid("current"))
            using (X509Certificate2 next = Valid("next"))
            {
                CertificateSelectionResult result = CertificateSelector.Select(
                    new[] { next, current },
                    Criteria(current.Thumbprint, next.Thumbprint),
                    Now);

                Assert.Equal(2, result.Candidates.Count);

                // Store order is irrelevant: configuration order decides, so a
                // rotation is a deterministic list edit.
                Assert.Equal(current.Thumbprint, result.Candidates[0].Thumbprint);
                Assert.Equal(next.Thumbprint, result.Candidates[1].Thumbprint);
            }
        }

        [Fact]
        public void An_expired_certificate_is_skipped_and_the_next_one_is_used()
        {
            using (X509Certificate2 expired = Expired("outgoing"))
            using (X509Certificate2 valid = Valid("incoming"))
            {
                CertificateSelectionResult result = CertificateSelector.Select(
                    new[] { expired, valid },
                    Criteria(expired.Thumbprint, valid.Thumbprint),
                    Now);

                Assert.Single(result.Candidates);
                Assert.Equal(valid.Thumbprint, result.Candidates[0].Thumbprint);

                CertificateRejection rejection = Assert.Single(result.Rejections);
                Assert.Equal(CertificateRejectionReason.Expired, rejection.Reason);
            }
        }

        [Fact]
        public void A_certificate_whose_private_key_is_unusable_is_reported_clearly()
        {
            using (X509Certificate2 publicOnly = TestData.Certificate(
                "no-key",
                Now.AddDays(-10),
                Now.AddDays(200),
                withPrivateKey: false))
            {
                CertificateSelectionResult result = CertificateSelector.Select(
                    new[] { publicOnly },
                    Criteria(publicOnly.Thumbprint),
                    Now);

                Assert.Empty(result.Candidates);

                CertificateRejection rejection = Assert.Single(result.Rejections);
                Assert.Equal(CertificateRejectionReason.NoPrivateKey, rejection.Reason);
                Assert.Contains("private key", rejection.Detail, StringComparison.OrdinalIgnoreCase);

                // The message an operator actually reads must name the identity that
                // did the looking, because the usual cause is a key ACL.
                string message = StoreCertificateResolver.DescribeFailure(result, StoreLocation.LocalMachine);

                Assert.Contains(ProcessIdentity.Current(), message, StringComparison.Ordinal);
                Assert.Contains("Manage Private Keys", message, StringComparison.Ordinal);
                Assert.Contains(publicOnly.Thumbprint, message, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public void A_missing_thumbprint_is_reported_without_stopping_the_search()
        {
            using (X509Certificate2 valid = Valid("present"))
            {
                CertificateSelectionResult result = CertificateSelector.Select(
                    new[] { valid },
                    Criteria(new string('B', 40), valid.Thumbprint),
                    Now);

                Assert.Single(result.Candidates);
                Assert.Equal(CertificateRejectionReason.NotFound, Assert.Single(result.Rejections).Reason);
            }
        }

        [Fact]
        public void Subject_matches_are_used_after_thumbprints_newest_first()
        {
            using (X509Certificate2 older = TestData.Certificate("sqltickets.contoso.local", Now.AddDays(-100), Now.AddDays(30)))
            using (X509Certificate2 newer = TestData.Certificate("sqltickets.contoso.local", Now.AddDays(-1), Now.AddDays(400)))
            {
                var criteria = new CertificateSelectionCriteria
                {
                    Thumbprints = new List<string>(),
                    Subject = "CN=sqltickets.contoso.local",
                    ExpiryWarningDays = 30,
                };

                CertificateSelectionResult result = CertificateSelector.Select(new[] { older, newer }, criteria, Now);

                Assert.Equal(2, result.Candidates.Count);
                Assert.Equal(newer.Thumbprint, result.Candidates[0].Thumbprint);
                Assert.True(result.Candidates[0].MatchedBySubject);
            }
        }

        [Fact]
        public void A_certificate_inside_the_warning_window_is_flagged_but_still_usable()
        {
            using (X509Certificate2 expiring = TestData.Certificate("expiring", Now.AddDays(-300), Now.AddDays(10)))
            {
                CertificateSelectionResult result = CertificateSelector.Select(
                    new[] { expiring },
                    Criteria(expiring.Thumbprint),
                    Now);

                CertificateCandidate candidate = Assert.Single(result.Candidates);
                Assert.True(candidate.ExpiresSoon);
                Assert.Equal(10, candidate.DaysUntilExpiry);
            }
        }

        [Fact]
        public void Thumbprints_copied_out_of_certmgr_with_spaces_still_match()
        {
            using (X509Certificate2 valid = Valid("spaced"))
            {
                string spaced = string.Join(" ", SplitPairs(valid.Thumbprint));

                CertificateSelectionResult result = CertificateSelector.Select(
                    new[] { valid },
                    Criteria(spaced),
                    Now);

                Assert.Single(result.Candidates);
            }
        }

        private static IEnumerable<string> SplitPairs(string value)
        {
            for (int i = 0; i + 1 < value.Length; i += 2)
            {
                yield return value.Substring(i, 2);
            }
        }

        private static CertificateSelectionCriteria Criteria(params string[] thumbprints)
        {
            return new CertificateSelectionCriteria
            {
                Thumbprints = new List<string>(thumbprints),
                ExpiryWarningDays = 30,
            };
        }

        private static X509Certificate2 Valid(string subject)
        {
            return TestData.Certificate(subject, Now.AddDays(-30), Now.AddDays(365));
        }

        private static X509Certificate2 Expired(string subject)
        {
            return TestData.Certificate(subject, Now.AddDays(-400), Now.AddDays(-1));
        }
    }
}
