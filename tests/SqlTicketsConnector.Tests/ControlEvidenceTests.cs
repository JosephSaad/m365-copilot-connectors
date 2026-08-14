// ---------------------------------------------------------------------------
// ControlEvidenceTests.cs
// A tripwire over the tests that exist as security control evidence.
//
// Deleting or renaming one of those tests would quietly remove the evidence a
// reviewer relies on. This test fails the build instead, and names what is
// missing.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Tests
{
    using System;
    using System.Linq;
    using System.Reflection;
    using Xunit;

    public class ControlEvidenceTests
    {
        private static readonly string[][] RequiredTests =
        {
            new[] { "SqlTicketsConnector.Tests.RedactionCanaryTests", "Crawl_does_not_leak_row_content_into_logs" },
            new[] { "SqlTicketsConnector.Tests.RedactionCanaryTests", "A_connection_string_never_reaches_a_sink_in_either_form" },
            new[] { "SqlTicketsConnector.Tests.SecretCacheTests", "Authentication_failure_invalidates_the_secret_and_retries_exactly_once" },
            new[] { "SqlTicketsConnector.Tests.CertificateResolutionTests", "A_certificate_whose_private_key_is_unusable_is_reported_clearly" },
            new[] { "SqlTicketsConnector.Tests.WatermarkResumptionTests", "No_row_is_skipped_or_repeated_across_a_checkpoint_boundary" },
            new[] { "SqlTicketsConnector.Tests.ConfigurationTests", "Every_invalid_field_is_reported_in_one_pass" },
            new[] { "SqlTicketsConnector.Tests.ContentAndSchemaTests", "An_empty_acl_configuration_fails_loudly_instead_of_granting_everyone" },
        };

        [Fact]
        public void Every_control_evidence_test_is_still_present()
        {
            Assembly assembly = typeof(ControlEvidenceTests).Assembly;

            foreach (string[] required in RequiredTests)
            {
                Type type = assembly.GetType(required[0]);

                Assert.True(
                    type != null,
                    "Control evidence class " + required[0] + " is missing. See docs/SECURITY.md before removing it.");

                MethodInfo method = type.GetMethod(required[1], BindingFlags.Public | BindingFlags.Instance);

                Assert.True(
                    method != null,
                    "Control evidence test " + required[0] + "." + required[1] +
                    " is missing. See docs/SECURITY.md before removing it.");

                Assert.True(
                    method.GetCustomAttributes(typeof(FactAttribute), false).Any(),
                    "Control evidence test " + required[0] + "." + required[1] +
                    " is no longer a [Fact] and would not run.");
            }
        }
    }
}
