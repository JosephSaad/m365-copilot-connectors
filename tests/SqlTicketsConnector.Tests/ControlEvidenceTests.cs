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
            new[] { "SqlTicketsConnector.Tests.WatermarkResumptionTests", "A_crawl_that_dies_mid_row_checkpoints_the_last_delivered_row" },
            new[] { "SqlTicketsConnector.Tests.ConfigurationTests", "Every_invalid_field_is_reported_in_one_pass" },
            new[] { "SqlTicketsConnector.Tests.ContentAndSchemaTests", "An_empty_acl_configuration_fails_loudly_instead_of_granting_everyone" },

            // The push tools. A registered schema cannot be corrected, only
            // deleted with every item in it, so these four are the guards whose
            // removal is most expensive and least visible.
            new[] { "SqlTicketsConnector.Tests.PushSchemaTests", "A_searchable_and_refinable_property_is_rejected_before_any_graph_call" },
            new[] { "SqlTicketsConnector.Tests.PushSchemaTests", "A_property_name_the_platform_would_reject_is_caught_before_any_graph_call" },
            new[] { "SqlTicketsConnector.Tests.PushEngineTests", "A_connection_carrying_a_foreign_schema_is_refused_before_any_write" },
            new[] { "SqlTicketsConnector.Tests.PushConfigurationTests", "A_view_name_that_is_not_a_plain_identifier_is_rejected" },

            // The source seam. The unbreakable rule - a failed crawl never
            // advances a watermark - stopped being a convention every connector
            // had to keep and became something the engine enforces, so these are
            // the tests that prove the enforcement is still wired up.
            new[] { "SqlTicketsConnector.Tests.PushSourceTests", "A_write_that_dies_leaves_the_watermark_on_the_last_item_that_landed" },
            new[] { "SqlTicketsConnector.Tests.PushSourceTests", "A_dry_run_writes_nothing_and_commits_nothing" },
            new[] { "SqlTicketsConnector.Tests.PushSourceTests", "An_item_the_source_could_grant_to_nobody_is_skipped_rather_than_written" },

            // Crawl state. Three invariants whose failure is silent, which is why
            // they are pinned here rather than left to the suite's good intentions.
            //
            // Hash determinism decides whether an item is written at all. A hash
            // that varies for an unchanged item costs a wasted write and is
            // noticed; one that varies by HOST - which is what the Unspecified
            // DateTime defect did - has two connectors each rewriting the other's
            // entire corpus, every run, with every run reporting success.
            new[] { "SqlTicketsConnector.Tests.ItemHasherTests", "The_same_item_hashes_the_same_way_twice" },
            new[] { "SqlTicketsConnector.Tests.ItemHasherTests", "An_unspecified_datetime_is_taken_as_utc_rather_than_shifted" },
            new[] { "SqlTicketsConnector.Tests.ItemHasherTests", "The_hash_does_not_depend_on_the_current_culture" },

            // Ordered commit. The marker may only advance over an unbroken prefix,
            // and must freeze for the rest of the run once anything is refused.
            // Stepping over a gap loses the rows in it permanently: they are not
            // retried, because the marker says they were done.
            new[] { "SqlTicketsConnector.Tests.PushBatchingTests", "The_commit_prefix_stops_at_the_refused_item_and_never_steps_over_the_gap" },
            new[] { "SqlTicketsConnector.Tests.PushBatchingTests", "Once_a_run_has_left_a_gap_the_marker_never_moves_again" },

            // The retry handler. The SDK's own handler retries inside the call the
            // engine is timing, so its absence is what makes the write attribution
            // mean anything - and it returns by default on any SDK upgrade.
            new[] { "SqlTicketsConnector.Tests.PushConcurrencyTests", "The_graph_pipeline_carries_no_retry_handler_of_its_own" },

            // The CDP connector's refusals. Every one of these is a case where
            // indexing would publish data whose access rules the index cannot
            // reproduce, so they are refusals rather than best efforts.
            new[] { "SqlTicketsConnector.Tests.CdpConnectorTests", "A_table_ranger_filters_or_masks_is_routed_to_a_live_query" },
            new[] { "SqlTicketsConnector.Tests.CdpConnectorTests", "A_table_with_a_deny_policy_is_routed_rather_than_mirrored" },
            new[] { "SqlTicketsConnector.Tests.CdpConnectorTests", "An_unresolved_group_is_dropped_rather_than_guessed" },
            new[] { "SqlTicketsConnector.Tests.CdpConnectorTests", "The_resume_rule_is_strictly_after_with_ties_broken_by_key" },
            new[] { "SqlTicketsConnector.Tests.CdpConnectorTests", "A_credential_or_a_downgrade_in_the_extra_options_is_refused" },
            new[] { "SqlTicketsConnector.Tests.CdpSourceTests", "A_file_nobody_can_be_granted_is_skipped_before_its_content_is_read" },
            new[] { "SqlTicketsConnector.Tests.CdpSourceTests", "An_unreadable_ranger_stops_the_run_rather_than_indexing_anyway" },

            // The ACL staleness bound. A permission change does not alter a
            // file's modification time, so the periodic full recrawl is the only
            // thing that re-derives an item's grants after one.
            new[] { "SqlTicketsConnector.Tests.CdpSourceTests", "The_periodic_full_recrawl_ignores_the_marker" },
            new[] { "SqlTicketsConnector.Tests.CdpSourceTests", "The_watermark_moves_only_over_items_the_engine_confirmed" },

            // The ACL mask. HDFS keeps it in the group permission digit rather
            // than as an entry, so reading an entry without it grants access the
            // cluster refuses - the connector's worst over-grant, and invisible
            // unless a test asserts both directions.
            new[] { "SqlTicketsConnector.Tests.CdpAclMaskTests", "A_named_entry_grants_only_what_the_mask_allows" },
            new[] { "SqlTicketsConnector.Tests.CdpAclMaskTests", "The_owning_group_is_read_from_its_entry_and_not_from_the_mask_digit" },
            new[] { "SqlTicketsConnector.Tests.CdpAclMaskTests", "A_masked_entry_produces_no_grants_at_all_which_is_what_makes_the_engine_skip_it" },

            // Reading a Ranger policy the way Ranger reads it. Each of these
            // fields, dropped, fails in the direction that indexes too much.
            new[] { "SqlTicketsConnector.Tests.CdpRangerFidelityTests", "An_excluded_table_is_not_covered_by_the_policy_that_excludes_it" },
            new[] { "SqlTicketsConnector.Tests.CdpRangerFidelityTests", "A_non_recursive_path_grant_stops_at_the_directory_it_names" },
            new[] { "SqlTicketsConnector.Tests.CdpRangerFidelityTests", "A_row_filter_named_with_a_leading_wildcard_still_refuses_the_table" },
            new[] { "SqlTicketsConnector.Tests.CdpRangerFidelityTests", "A_non_recursive_deny_still_covers_everything_beneath_it" },

            // The catalogue. Its access rules deliberately differ from every
            // other source here - stricter than the cluster, and indexing the
            // description of a table whose data may never be indexed - so both
            // directions are pinned. Getting either backwards publishes the
            // shape of the lake to people who cannot reach the lake.
            new[] { "SqlTicketsConnector.Tests.CdpAtlasTests", "A_row_filtered_table_is_still_catalogued_even_though_its_rows_are_not" },
            new[] { "SqlTicketsConnector.Tests.CdpAtlasTests", "A_denied_table_is_not_catalogued_either" },
            new[] { "SqlTicketsConnector.Tests.CdpAtlasTests", "A_table_nobody_is_granted_has_no_catalogue_entry" },
            new[] { "SqlTicketsConnector.Tests.CdpAtlasTests", "A_column_scoped_grant_narrows_what_may_be_described_rather_than_refusing_it" },
            new[] { "SqlTicketsConnector.Tests.CdpAtlasTests", "A_scrubbed_entity_is_not_indexed_as_a_nameless_item" },

            // The four the catalogue's own adversarial review added. Each pins a
            // way one entry could disclose more than the cluster does: a
            // neighbour named to people not granted it, the columns of one grant
            // shown to the holders of another, and - the one that is not a
            // disclosure but a silence - a page of entities this caller may not
            // read ending the enumeration and passing for a complete crawl.
            new[] { "SqlTicketsConnector.Tests.CdpAtlasTests", "A_lineage_neighbour_nobody_on_this_entry_may_read_is_not_named" },
            new[] { "SqlTicketsConnector.Tests.CdpAtlasTests", "Column_grants_intersect_across_policies_rather_than_union" },
            new[] { "SqlTicketsConnector.Tests.CdpAtlasTests", "A_whole_page_of_scrubbed_entities_does_not_end_the_catalogue" },
            new[] { "SqlTicketsConnector.Tests.CdpAtlasTests", "A_database_is_never_asked_for_lineage" },

            // The three the Ranger fidelity review added. Each holds on every
            // cluster whatever its Ranger looks like, which is what separates
            // them from the zone and tag findings: no configuration answer can
            // make them inert, so none of them may be renamed away quietly.
            new[] { "SqlTicketsConnector.Tests.CdpTraverseAndPagingTests", "A_group_that_cannot_traverse_the_directory_does_not_get_the_file" },
            new[] { "SqlTicketsConnector.Tests.CdpTraverseAndPagingTests", "A_ranger_grant_is_not_gated_by_the_directory_bits" },
            new[] { "SqlTicketsConnector.Tests.CdpTraverseAndPagingTests", "A_null_gate_is_not_an_empty_one" },
            new[] { "SqlTicketsConnector.Tests.CdpTraverseAndPagingTests", "The_pager_steps_by_what_a_page_held_not_by_what_it_asked_for" },
            new[] { "SqlTicketsConnector.Tests.CdpTraverseAndPagingTests", "A_path_grant_does_not_reach_a_directory_differing_only_by_case" },

            // The zone guard, and the test that stops it firing everywhere. A
            // guard nobody can switch off is only safe while it cannot fire on
            // an ordinary cluster, so both halves are evidence.
            new[] { "SqlTicketsConnector.Tests.CdpTraverseAndPagingTests", "A_policy_in_a_security_zone_stops_the_run" },
            new[] { "SqlTicketsConnector.Tests.CdpTraverseAndPagingTests", "An_unzoned_policy_set_is_read_normally" },
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

                object[] attributes = method.GetCustomAttributes(typeof(FactAttribute), false);

                Assert.True(
                    attributes.Any(),
                    "Control evidence test " + required[0] + "." + required[1] +
                    " is no longer a [Fact] and would not run.");

                // [Fact(Skip = "...")] is still a FactAttribute, so without this
                // check control evidence could be switched off without the build
                // noticing - the exact quiet removal this tripwire exists for.
                foreach (FactAttribute fact in attributes.Cast<FactAttribute>())
                {
                    Assert.True(
                        fact.Skip == null,
                        "Control evidence test " + required[0] + "." + required[1] +
                        " is marked Skip and would not run. See docs/SECURITY.md before disabling it.");
                }
            }
        }
    }
}
