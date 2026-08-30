// ---------------------------------------------------------------------------
// SensitivityLabelTests.cs
// The classification-to-label mapping, and the refusal it can produce.
//
// WHAT MAKES THIS WORTH TESTING RATHER THAN READING. A refusal to index is a
// control whose correct operation looks exactly like its total absence: the item
// is not in the index either way. Nothing about a working corpus tells you
// whether the policy ran, so the only evidence that it did is a test that puts a
// labelled item in front of it and proves the item did not reach Graph.
//
// FOUR SURFACES, NOT ONE. A refused item must be absent from the index, absent
// from the source's committed list, counted as a skip so rows-read still
// reconciles, and counted again as a refusal so the control can be evidenced.
// The committed assertion is the one that matters most and the easiest to leave
// out: an item that was refused but still committed would move the source's
// watermark past a row that is not in the index, which is the invariant the
// whole repository is built around.
//
// AND ON BOTH WRITE PATHS. Settings:Batch is false in every fixture, so a check
// that worked on the single-item path and quietly sent the item inside a $batch
// would pass every test anybody would think to write. The batch path is
// exercised explicitly here for that reason.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Tests
{
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using global::Connector.Security.Configuration;
    using Microsoft.Graph.Models.ExternalConnectors;
    using PushCore;
    using Serilog.Core;
    using SqlTicketsConnector.Tests.TestSupport;
    using Xunit;

    public class SensitivityLabelTests
    {
        private const string ConnectionId = "sensitivity";

        // --- the policy itself -------------------------------------------

        [Fact]
        public void Off_ignores_every_classification()
        {
            SensitivityPolicy policy = SensitivityPolicy.Compile(new SensitivityOptions());

            SensitivityVerdict verdict = policy.Evaluate(new[] { "PCI" });

            Assert.False(policy.IsEnabled);
            Assert.True(verdict.Indexable);
            Assert.Null(verdict.Label);
        }

        [Fact]
        public void A_null_section_compiles_to_a_policy_that_does_nothing()
        {
            // The engine calls Compile unconditionally, and a connector whose
            // appsettings predates this feature deserializes no section at all.
            SensitivityPolicy policy = SensitivityPolicy.Compile(null);

            Assert.False(policy.IsEnabled);
            Assert.True(policy.Evaluate(new[] { "PCI" }).Indexable);
        }

        [Fact]
        public void The_most_restrictive_matching_label_wins()
        {
            SensitivityPolicy policy = SensitivityPolicy.Compile(Enforcing());

            // Order in the configuration is the order of restriction, and the
            // item carries the mild tag FIRST - so a policy that took the first
            // match, or the last classification, would answer Internal.
            Assert.Equal("Confidential", policy.Evaluate(new[] { "INTERNAL", "PII" }).Label);
            Assert.Equal("Confidential", policy.Evaluate(new[] { "PII", "INTERNAL" }).Label);
        }

        [Fact]
        public void Classification_matching_ignores_case_and_surrounding_space()
        {
            // A catalogue's tag casing is not a contract, and Atlas merges tags
            // from two endpoints that do not agree about it.
            SensitivityPolicy policy = SensitivityPolicy.Compile(Enforcing());

            Assert.Equal("Confidential", policy.Evaluate(new[] { "pii" }).Label);
            Assert.Equal("Confidential", policy.Evaluate(new[] { "  Pii  " }).Label);
        }

        [Fact]
        public void A_label_marked_not_indexable_refuses_the_item()
        {
            SensitivityPolicy policy = SensitivityPolicy.Compile(Enforcing());

            SensitivityVerdict verdict = policy.Evaluate(new[] { "PCI" });

            Assert.False(verdict.Indexable);
            Assert.Equal("Restricted", verdict.Label);
            Assert.Contains("not indexable", verdict.Reason);
        }

        [Fact]
        public void Annotate_publishes_the_label_and_refuses_nothing()
        {
            SensitivityOptions options = Enforcing();
            options.Mode = nameof(SensitivityMode.Annotate);

            // Index:false is what Enforce acts on; Annotate must ignore it
            // rather than quietly half-enforcing.
            SensitivityVerdict verdict = SensitivityPolicy.Compile(options).Evaluate(new[] { "PCI" });

            Assert.True(verdict.Indexable);
            Assert.Equal("Restricted", verdict.Label);
        }

        [Fact]
        public void An_unmapped_classification_is_refused_when_configured_to_be()
        {
            SensitivityOptions options = Enforcing();
            options.Unmapped = nameof(SensitivityAction.Refuse);

            SensitivityVerdict verdict = SensitivityPolicy.Compile(options).Evaluate(new[] { "PUBLIC", "SOMETHINGNEW" });

            // Refused even though PUBLIC maps and is indexable. An item whose
            // tags are partly unrecognised has an unknown sensitivity, and the
            // recognised half does not make it known.
            Assert.False(verdict.Indexable);
            Assert.Contains("SOMETHINGNEW", verdict.Reason);
        }

        [Fact]
        public void An_unmapped_classification_is_allowed_when_configured_to_be()
        {
            SensitivityOptions options = Enforcing();
            options.Unmapped = nameof(SensitivityAction.Allow);

            SensitivityVerdict verdict = SensitivityPolicy.Compile(options).Evaluate(new[] { "INTERNAL", "SOMETHINGNEW" });

            Assert.True(verdict.Indexable);
            Assert.Equal("Internal", verdict.Label);
        }

        [Fact]
        public void An_item_with_no_classification_follows_Unlabelled()
        {
            SensitivityOptions refusing = Enforcing();
            refusing.Unlabelled = nameof(SensitivityAction.Refuse);

            Assert.False(SensitivityPolicy.Compile(refusing).Evaluate(null).Indexable);
            Assert.False(SensitivityPolicy.Compile(refusing).Evaluate(new string[0]).Indexable);

            // A list of blanks is the same thing as no list. An empty tag is
            // what a catalogue returns for a tag that was deleted.
            Assert.False(SensitivityPolicy.Compile(refusing).Evaluate(new[] { "  " }).Indexable);

            SensitivityOptions allowing = Enforcing();
            allowing.Unlabelled = nameof(SensitivityAction.Allow);

            Assert.True(SensitivityPolicy.Compile(allowing).Evaluate(null).Indexable);
        }

        // --- configuration -----------------------------------------------

        [Fact]
        public void Enforce_refuses_to_start_until_both_fallbacks_are_decided()
        {
            // The two ways this control fails silently. Neither has a safe
            // default, so neither gets one.
            SensitivityOptions options = Enforcing();
            options.Unmapped = string.Empty;
            options.Unlabelled = string.Empty;

            var errors = new ValidationErrors();
            options.Validate(errors, "Sensitivity");

            Assert.True(errors.HasErrors);
            Assert.Contains(errors.Errors, e => e.Contains("Sensitivity:Unmapped"));
            Assert.Contains(errors.Errors, e => e.Contains("Sensitivity:Unlabelled"));
        }

        [Fact]
        public void Annotate_does_not_demand_the_fallbacks()
        {
            SensitivityOptions options = Enforcing();
            options.Mode = nameof(SensitivityMode.Annotate);
            options.Unmapped = string.Empty;
            options.Unlabelled = string.Empty;

            // They decide what to REFUSE, and Annotate refuses nothing. Demanding
            // them there would be ceremony.
            foreach (SensitivityLabelOptions label in options.Labels)
            {
                label.Index = true;
            }

            var errors = new ValidationErrors();
            options.Validate(errors, "Sensitivity");

            Assert.False(errors.HasErrors, errors.ToMessage());
        }

        [Fact]
        public void A_label_that_cannot_refuse_in_this_mode_is_a_configuration_error()
        {
            // Index:false under Annotate reads, to whoever wrote it, as
            // protection. It is not, and the build says so rather than the
            // corpus saying so later.
            SensitivityOptions options = Enforcing();
            options.Mode = nameof(SensitivityMode.Annotate);

            var errors = new ValidationErrors();
            options.Validate(errors, "Sensitivity");

            Assert.Contains(errors.Errors, e => e.Contains("Index") && e.Contains("Annotate"));
        }

        [Fact]
        public void One_classification_may_not_belong_to_two_labels()
        {
            SensitivityOptions options = Enforcing();
            options.Labels[0].Classifications.Add("PII");

            var errors = new ValidationErrors();
            options.Validate(errors, "Sensitivity");

            Assert.Contains(errors.Errors, e => e.Contains("already claims it"));
        }

        [Fact]
        public void A_malformed_mapping_is_rejected_even_when_the_mode_is_Off()
        {
            // It becomes load-bearing the day somebody flips the mode, and that
            // day is a change window rather than a development afternoon.
            SensitivityOptions options = Enforcing();
            options.Mode = nameof(SensitivityMode.Off);
            options.Labels[1].Classifications.Clear();

            var errors = new ValidationErrors();
            options.Validate(errors, "Sensitivity");

            Assert.Contains(errors.Errors, e => e.Contains("Classifications"));
        }

        [Fact]
        public void A_property_name_Graph_would_reject_is_caught_at_startup()
        {
            // A registered schema is append-only, so the alternative to catching
            // this here is a rejection fifteen minutes into a server-side
            // registration against a connection nobody can then fix.
            SensitivityOptions options = Enforcing();
            options.Property = "sensitivity_label";

            var errors = new ValidationErrors();
            options.Validate(errors, "Sensitivity");

            Assert.Contains(errors.Errors, e => e.Contains("Sensitivity:Property"));
        }

        [Fact]
        public void The_section_reaches_PushOptions_validation()
        {
            // The policy is only a control if the shared Validate actually runs
            // it; a section validated by nobody is a section that can say
            // anything.
            PushOptions options = TestData.ValidPushOptions(ConnectionId);
            options.Sensitivity = Enforcing();
            options.Sensitivity.Unmapped = string.Empty;

            Assert.Contains(options.Validate().Errors, e => e.Contains("Sensitivity:Unmapped"));
        }

        // --- the engine ---------------------------------------------------

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task A_refused_item_reaches_neither_the_index_nor_the_watermark(bool batched)
        {
            var source = new FakePushSource(new[]
            {
                Tagged("a1", "INTERNAL"),
                Tagged("a2", "PCI"),
                Tagged("a3", "PII"),
            });

            (StubGraphAdapter adapter, PushSummary summary) = await RunAsync(source, Enforcing(), batched);

            // Proves the theory actually took two different paths rather than
            // running the serial one twice. Settings:Batch is false in every
            // fixture, so a batched case that quietly stayed serial would pass
            // this whole test while testing nothing new.
            Assert.Equal(batched, summary.Batches > 0);

            // Absent from the index...
            Assert.Equal(new[] { "a1", "a3" }, adapter.WrittenItemIds.OrderBy(id => id).ToArray());

            // ...and absent from what the source was told it may move past. An
            // item that was refused but still committed would advance the
            // watermark over a row the index does not have.
            Assert.Equal(new[] { "a1", "a3" }, source.Committed.OrderBy(id => id).ToArray());

            Assert.Equal(2, summary.Total);
            Assert.Equal(1, summary.RefusedByLabel);

            // Counted as a skip as well, which is what keeps rows read
            // reconcilable: Total + Unchanged + Skipped is the number the host
            // prints and the run row stores.
            Assert.Equal(1, summary.Skipped);
            Assert.Equal(3, summary.Total + summary.Unchanged + summary.Skipped);
        }

        [Fact]
        public async Task The_winning_label_is_published_on_the_item()
        {
            var source = new FakePushSource(new[] { Tagged("a1", "INTERNAL", "PII") });

            (StubGraphAdapter adapter, _) = await RunAsync(source, Enforcing(), batched: false);

            string body = Assert.Single(adapter.WrittenBodies);

            Assert.Contains("\"sensitivityLabel\"", body);
            Assert.Contains("Confidential", body);
            Assert.DoesNotContain("Internal", body);
        }

        [Fact]
        public async Task Annotate_publishes_the_label_and_writes_everything()
        {
            SensitivityOptions options = Enforcing();
            options.Mode = nameof(SensitivityMode.Annotate);

            foreach (SensitivityLabelOptions label in options.Labels)
            {
                label.Index = true;
            }

            var source = new FakePushSource(new[] { Tagged("a1", "PCI") });

            (StubGraphAdapter adapter, PushSummary summary) = await RunAsync(source, options, batched: false);

            Assert.Equal(new[] { "a1" }, adapter.WrittenItemIds.ToArray());
            Assert.Equal(0, summary.RefusedByLabel);
            Assert.Contains("Restricted", Assert.Single(adapter.WrittenBodies));
        }

        [Fact]
        public async Task An_untagged_item_is_written_with_no_label_at_all()
        {
            // Omitted, not written as an empty string: Graph rejects a null and
            // an empty refiner bucket is a bucket somebody has to explain.
            var source = new FakePushSource(new[] { new PushItem { Id = "a1", ItemType = "file", Content = "x" } });

            (StubGraphAdapter adapter, PushSummary summary) = await RunAsync(source, Enforcing(), batched: false);

            Assert.Equal(new[] { "a1" }, adapter.WrittenItemIds.ToArray());
            Assert.Equal(0, summary.RefusedByLabel);
            Assert.DoesNotContain("sensitivityLabel", Assert.Single(adapter.WrittenBodies));
        }

        [Fact]
        public async Task A_dry_run_refuses_the_same_items_it_would_refuse_for_real()
        {
            // The only way to answer "how much of this corpus would we lose"
            // before committing to Enforce.
            var source = new FakePushSource(new[] { Tagged("a1", "PCI"), Tagged("a2", "PII") });

            (StubGraphAdapter adapter, PushSummary summary) = await RunAsync(
                source, Enforcing(), batched: false, dryRun: true);

            Assert.Empty(adapter.WrittenItemIds);
            Assert.Equal(1, summary.RefusedByLabel);
        }

        [Fact]
        public async Task No_policy_means_no_change_to_any_existing_connector()
        {
            var source = new FakePushSource(new[] { Tagged("a1", "PCI"), Tagged("a2", "PII") });

            (StubGraphAdapter adapter, PushSummary summary) = await RunAsync(source, null, batched: false);

            Assert.Equal(2, adapter.WrittenItemIds.Count);
            Assert.Equal(0, summary.RefusedByLabel);
            Assert.DoesNotContain("sensitivityLabel", adapter.WrittenBodies[0]);
        }

        // --- fixtures ------------------------------------------------------

        /// <summary>A four-label policy in Enforce mode, least restrictive first.</summary>
        private static SensitivityOptions Enforcing()
        {
            return new SensitivityOptions
            {
                Mode = nameof(SensitivityMode.Enforce),
                Unmapped = nameof(SensitivityAction.Allow),
                Unlabelled = nameof(SensitivityAction.Allow),
                Labels = new List<SensitivityLabelOptions>
                {
                    new SensitivityLabelOptions
                    {
                        Name = "Public",
                        Classifications = new List<string> { "PUBLIC" },
                    },
                    new SensitivityLabelOptions
                    {
                        Name = "Internal",
                        Classifications = new List<string> { "INTERNAL" },
                    },
                    new SensitivityLabelOptions
                    {
                        Name = "Confidential",
                        Classifications = new List<string> { "PII", "GDPR" },
                    },
                    new SensitivityLabelOptions
                    {
                        Name = "Restricted",
                        Classifications = new List<string> { "PCI", "SOX" },
                        Index = false,
                    },
                },
            };
        }

        private static PushItem Tagged(string id, params string[] classifications)
        {
            return new PushItem
            {
                Id = id,
                ItemType = "file",
                Content = "content of " + id,
                Classifications = classifications,
            };
        }

        /// <summary>Runs a whole crawl the way the host does.</summary>
        /// <remarks>
        /// RunAsync rather than PushItemsAsync, because the policy is compiled in
        /// the constructor and announced there, and because the summary a caller
        /// asserts on is the one the run returns.
        /// </remarks>
        private static async Task<(StubGraphAdapter Adapter, PushSummary Summary)> RunAsync(
            FakePushSource source, SensitivityOptions sensitivity, bool batched, bool dryRun = false)
        {
            var adapter = new StubGraphAdapter(
                new ExternalConnection { Id = ConnectionId, State = ConnectionState.Ready },
                new SqlHierarchyPush.HierarchyPushConnector().BuildSchema());

            PushOptions options = TestData.ValidPushOptions(ConnectionId);
            options.Settings["Batch"] = batched ? "true" : "false";
            options.Settings["Writers"] = "1";

            if (sensitivity != null)
            {
                options.Sensitivity = sensitivity;
            }

            var engine = new PushEngine(
                new FakePushConnector(source),
                options,
                new Microsoft.Graph.GraphServiceClient(adapter),
                Logger.None,
                dryRun);

            var context = new PushSourceContext(
                options,
                new Azure.Identity.DefaultAzureCredential(),
                secrets: null,
                Logger.None);

            PushSummary summary = await engine.RunAsync(context);

            return (adapter, summary);
        }
    }
}
