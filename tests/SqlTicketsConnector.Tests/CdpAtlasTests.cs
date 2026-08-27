// ---------------------------------------------------------------------------
// CdpAtlasTests.cs
// The catalogue connector, and the two places its access rules deliberately
// differ from every other source in this repository.
//
//   1. It is STRICTER than the cluster. CDP ships Atlas with a Ranger policy
//      granting every authenticated user read on every entity, and this
//      connector does not mirror that: an entry is granted to the groups
//      Ranger grants select on the described table, and skipped when that is
//      nobody. "Everyone with a cluster account" is not "everyone in the
//      tenant".
//
//   2. A row filter or a column mask does NOT refuse an entry, where it does
//      refuse the data. A filter governs rows and a mask governs values;
//      neither hides a table's existence, its columns or its owner from
//      somebody granted select. The tables whose data can never be indexed are
//      frequently the ones most worth cataloguing.
//
// Both are easy to "fix" in the wrong direction by somebody who has only read
// one of the rules, so both are asserted here from both sides.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using CdpConnector.Source;
    using CdpConnector.Source.Acl;
    using CdpConnector.Source.Atlas;
    using CdpConnector.Source.Ranger;
    using CdpConnector.Source.Watermark;
    using CdpGraphPush;
    using global::Connector.Security.Configuration;
    using Microsoft.Graph.Models.ExternalConnectors;
    using PushCore;
    using Serilog.Core;
    using SqlTicketsConnector.Tests.TestSupport;
    using Xunit;

    public class CdpAtlasTests : IDisposable
    {
        private readonly string stateDirectory =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (System.IO.Directory.Exists(this.stateDirectory))
            {
                System.IO.Directory.Delete(this.stateDirectory, true);
            }
        }

        // ------------------------------------------------------------------
        // The two rules that differ
        // ------------------------------------------------------------------

        [Fact]
        public async Task A_row_filtered_table_is_still_catalogued_even_though_its_rows_are_not()
        {
            // The distinction that makes a catalogue worth having. The data of
            // this table can never be indexed; its description can, for exactly
            // the people who already see it when they query.
            var filter = new RangerPolicy { Id = 71, Enabled = true, PolicyType = RangerPolicyType.RowFilter };
            filter.SetResource("database", new List<string> { "contracts" });
            filter.SetResource("table", new List<string> { "contract" });

            List<PushItem> items = await this.CatalogueAsync(extraPolicies: new[] { filter });

            PushItem table = Assert.Single(items, i => (string)i.Properties["entityKind"] == "table");

            Assert.Equal("contracts.contract@cm", table.Properties["qualifiedName"]);
            Assert.NotEmpty(table.Acl);

            // And the data connector still refuses the same table, so the two
            // rules genuinely disagree on purpose rather than by accident.
            var evaluator = new RoutingEvaluator(new[] { filter, GrantPolicy() });

            Assert.False(evaluator.EvaluateTable("contracts", "contract").MayIndex);
            Assert.True(evaluator.EvaluateCatalogueEntry("contracts", "contract").MayIndex);
        }

        [Fact]
        public void A_denied_table_is_not_catalogued_either()
        {
            // Where the two rules agree. A description of a table is still a
            // disclosure about it, and a deny is never mirrored anywhere.
            RangerPolicy deny = GrantPolicy();
            deny.Deny.Add(Item(new[] { "contractors" }, "select"));

            RoutingDecision decision =
                new RoutingEvaluator(new[] { deny }).EvaluateCatalogueEntry("contracts", "contract");

            Assert.False(decision.MayIndex);
            Assert.Contains("still a disclosure", decision.Reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void A_table_nobody_is_granted_has_no_catalogue_entry()
        {
            RangerPolicy noGrant = new RangerPolicy
            {
                Id = 3,
                Enabled = true,
                PolicyType = RangerPolicyType.Access,
            };

            noGrant.SetResource("database", new List<string> { "contracts" });
            noGrant.SetResource("table", new List<string> { "contract" });

            Assert.False(
                new RoutingEvaluator(new[] { noGrant }).EvaluateCatalogueEntry("contracts", "contract").MayIndex);
        }

        [Fact]
        public void A_column_scoped_grant_narrows_what_may_be_described_rather_than_refusing_it()
        {
            // A column name discloses: one called "hiv_status" says something by
            // existing. Somebody granted three columns has not been shown forty.
            RangerPolicy scoped = GrantPolicy();
            scoped.SetResource("column", new List<string> { "contract_ref", "status" });

            var evaluator = new RoutingEvaluator(new[] { scoped });

            Assert.True(evaluator.EvaluateCatalogueEntry("contracts", "contract").MayIndex);

            Assert.Equal(
                new[] { "contract_ref", "status" },
                evaluator.CatalogueColumns("contracts", "contract").ToArray());

            // A grant over every column constrains nothing, and null is how that
            // is said. An EMPTY list is the opposite answer - "no column may be
            // described" - so the two must not be spelled the same way.
            Assert.Null(new RoutingEvaluator(new[] { GrantPolicy() }).CatalogueColumns("contracts", "contract"));
        }

        [Fact]
        public void Column_grants_intersect_across_policies_rather_than_union()
        {
            // One item carries one set of column names and the union of every
            // granting policy's groups, so a column named by any one policy
            // would be shown to every group on the item. Two ordinary policies -
            // ward-admin granted the identifiers, clinicians granted the
            // diagnosis - would tell ward-admin that a column called hiv_status
            // exists. That is the exact disclosure the narrowing prevents.
            var wardAdmin = new RangerPolicy { Id = 10, Enabled = true, PolicyType = RangerPolicyType.Access };
            wardAdmin.SetResource("database", new List<string> { "contracts" });
            wardAdmin.SetResource("table", new List<string> { "patient" });
            wardAdmin.SetResource("column", new List<string> { "patient_id", "admission_date" });
            wardAdmin.Allow.Add(Item(new[] { "ward-admin" }, "select"));

            var clinicians = new RangerPolicy { Id = 11, Enabled = true, PolicyType = RangerPolicyType.Access };
            clinicians.SetResource("database", new List<string> { "contracts" });
            clinicians.SetResource("table", new List<string> { "patient" });
            clinicians.SetResource("column", new List<string> { "patient_id", "hiv_status" });
            clinicians.Allow.Add(Item(new[] { "clinicians" }, "select"));

            var evaluator = new RoutingEvaluator(new[] { wardAdmin, clinicians });

            // Both groups are on the entry, so only what BOTH grants cover may
            // be named on it.
            Assert.Equal(
                new[] { "ward-admin", "clinicians" },
                evaluator.EvaluateCatalogueEntry("contracts", "patient").Groups.ToArray());

            Assert.Equal(
                new[] { "patient_id" },
                evaluator.CatalogueColumns("contracts", "patient").ToArray());

            // Disjoint grants describe nothing rather than everything. An entry
            // that under-describes is a search that misses; one that
            // over-describes is a leak.
            clinicians.SetResource("column", new List<string> { "hiv_status" });

            Assert.Empty(new RoutingEvaluator(new[] { wardAdmin, clinicians })
                .CatalogueColumns("contracts", "patient"));
        }

        [Fact]
        public void A_database_entry_is_granted_to_whoever_may_read_anything_in_it()
        {
            // Asking EvaluateCatalogueEntry(database, "*") asks about a table
            // whose NAME is "*", which matches a policy written over "*" and no
            // other. A cluster whose policies name their tables one at a time -
            // the ordinary arrangement, where a database holds tables of
            // different sensitivities - catalogued no databases at all.
            var perTable = new RangerPolicy { Id = 20, Enabled = true, PolicyType = RangerPolicyType.Access };
            perTable.SetResource("database", new List<string> { "contracts" });
            perTable.SetResource("table", new List<string> { "contract" });
            perTable.Allow.Add(Item(new[] { "analysts" }, "select"));

            var evaluator = new RoutingEvaluator(new[] { perTable });

            Assert.False(evaluator.EvaluateCatalogueEntry("contracts", "*").MayIndex);

            RoutingDecision decision = evaluator.EvaluateDatabaseEntry("contracts");

            Assert.True(decision.MayIndex);
            Assert.Equal(new[] { "analysts" }, decision.Groups.ToArray());

            // A database nobody is granted anything in still has no entry.
            Assert.False(evaluator.EvaluateDatabaseEntry("finance").MayIndex);
        }

        [Fact]
        public async Task Only_the_columns_a_grant_names_reach_the_item()
        {
            RangerPolicy scoped = GrantPolicy();
            scoped.SetResource("column", new List<string> { "contract_ref" });

            List<PushItem> items = await this.CatalogueAsync(grant: scoped);

            PushItem table = Assert.Single(items, i => (string)i.Properties["entityKind"] == "table");

            Assert.Equal("contract_ref", table.Properties["columnNames"]);
            Assert.Equal(1L, table.Properties["columnCount"]);
            Assert.DoesNotContain("counterparty", table.Content, StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------
        // Reading Atlas
        // ------------------------------------------------------------------

        [Fact]
        public async Task An_entry_carries_the_owner_the_tags_and_the_lineage()
        {
            List<PushItem> items = await this.CatalogueAsync();

            PushItem table = Assert.Single(items, i => (string)i.Properties["entityKind"] == "table");

            Assert.Equal("contract", table.Properties["title"]);
            Assert.Equal("contracts", table.Properties["databaseName"]);
            Assert.Equal("cm", table.Properties["clusterName"]);
            Assert.Equal("priya.raman", table.Properties["ownerName"]);

            // Collections, not joined strings: both are refiners, and a refiner
            // buckets on the whole stored value.
            Assert.Equal(new[] { "PII" }, Assert.IsType<List<string>>(table.Properties["classifications"]));
            Assert.Equal(new[] { "Contract" }, Assert.IsType<List<string>>(table.Properties["glossaryTerms"]));

            // The lineage walk went THROUGH the hive_process to the tables on
            // its far side. Naming the process would have put its own name -
            // the query text - in the index instead.
            Assert.Equal("raw_contracts", table.Properties["upstream"]);
            Assert.Equal("contract_mart", table.Properties["downstream"]);
            Assert.DoesNotContain("insert overwrite", table.Content, StringComparison.OrdinalIgnoreCase);

            // The body reads as sentences, because "which table holds the
            // counterparty" is a question asked in words.
            Assert.Contains("Owned by priya.raman", table.Content, StringComparison.Ordinal);
            Assert.Contains("Produced from raw_contracts", table.Content, StringComparison.Ordinal);
            Assert.Contains("counterparty", table.Content, StringComparison.Ordinal);
        }

        [Fact]
        public async Task A_database_is_never_asked_for_lineage()
        {
            // The defect this pins made the SHIPPED configuration unable to
            // finish a crawl. Atlas serves lineage for entities deriving from
            // DataSet or Process; a hive_db derives from neither, so a healthy
            // Atlas answers 400 - and the client treated anything that was not a
            // 404 as fatal. AtlasTypes ships as "hive_db;hive_table" with
            // lineage on, so the first database killed every run part-way,
            // leaving a permanently partial index and blaming Atlas's health.
            var atlas = new FakeAtlas();

            List<PushItem> items = await this.CatalogueAsync(atlas: atlas);

            Assert.Contains(items, i => (string)i.Properties["entityKind"] == "database");
            Assert.DoesNotContain(FakeAtlas.DbGuid, atlas.LineageRequests);
            Assert.Contains(FakeAtlas.TableGuid, atlas.LineageRequests);
        }

        [Fact]
        public async Task A_lineage_400_is_not_fatal_even_for_a_type_that_should_have_had_lineage()
        {
            // The second line, for a type this code does not know is not a
            // DataSet. A customer type that turns out not to be one must cost
            // that entry's lineage, not the crawl.
            var atlas = new FakeAtlas { LineageStatus = HttpStatusCode.BadRequest };

            List<PushItem> items = await this.CatalogueAsync(atlas: atlas);

            PushItem table = Assert.Single(items, i => (string)i.Properties["entityKind"] == "table");

            Assert.False(table.Properties.ContainsKey("upstream"));
            Assert.Equal("contract", table.Properties["title"]);
        }

        [Fact]
        public async Task A_lineage_neighbour_nobody_on_this_entry_may_read_is_not_named()
        {
            // A neighbour's NAME is a disclosure. "Produced from hr.salaries_raw"
            // tells everybody granted the downstream table that a table of
            // salaries exists and what it is called, and this entry's ACL has
            // nothing to do with who may read that one. Atlas will not stop it:
            // on a stock cluster its own policy shows every authenticated user
            // every entity, which is why the check is here.
            var atlas = new FakeAtlas { DownstreamQualifiedName = "hr.salaries_raw@cm" };

            List<PushItem> items = await this.CatalogueAsync(atlas: atlas);

            PushItem table = Assert.Single(items, i => (string)i.Properties["entityKind"] == "table");

            // Upstream sits in the granted database and is still named.
            Assert.Equal("raw_contracts", table.Properties["upstream"]);

            Assert.False(table.Properties.ContainsKey("downstream"));
            Assert.DoesNotContain("salaries", table.Content, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task A_whole_page_of_scrubbed_entities_does_not_end_the_catalogue()
        {
            // "This page added nothing new" has two causes that look identical
            // and are opposite. A server ignoring the offset must stop the
            // pager; a page of entities this caller may not read must not,
            // because the offset advanced correctly and the rest of the lake is
            // still to come. Conflating them truncated the catalogue silently
            // and still reported a clean crawl.
            var atlas = new FakeAtlas { ScrubbedFirstPage = true };

            List<PushItem> items = await this.CatalogueAsync(atlas: atlas);

            Assert.Single(items, i => (string)i.Properties["entityKind"] == "table");
        }

        [Fact]
        public async Task A_scrubbed_entity_is_not_indexed_as_a_nameless_item()
        {
            // Atlas blanks an entity the caller may not read and leaves it in
            // the array with guid "-1" rather than removing it. Indexing one
            // would put an empty entry in the catalogue.
            var atlas = new FakeAtlas();
            atlas.Scrubbed = true;

            List<PushItem> items = await this.CatalogueAsync(atlas: atlas);

            Assert.DoesNotContain(items, i => i.Id == AtlasPushSource.ItemId("-1"));
            Assert.DoesNotContain(items, i => ((string)i.Properties["title"]).Length == 0);
        }

        [Fact]
        public async Task Atlas_refusing_this_identity_is_a_credential_failure()
        {
            var atlas = new FakeAtlas { Status = HttpStatusCode.Forbidden };

            await Assert.ThrowsAsync<PushSourceAuthenticationException>(() => this.CatalogueAsync(atlas: atlas));
        }

        [Fact]
        public async Task An_unreachable_atlas_names_the_thing_an_operator_has_to_check()
        {
            var atlas = new FakeAtlas { Status = HttpStatusCode.ServiceUnavailable };

            InvalidOperationException thrown =
                await Assert.ThrowsAsync<InvalidOperationException>(() => this.CatalogueAsync(atlas: atlas));

            Assert.Contains("Atlas", thrown.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void A_qualified_name_is_split_the_way_atlas_writes_it()
        {
            // "finance.customer@prod" is database finance, table customer, on
            // cluster prod. Asking Ranger about a table called "customer@prod"
            // would match no policy, read as "nobody is granted", and silently
            // drop every entry.
            Assert.Equal(("finance", "customer", "prod"), AtlasEntity.SplitQualifiedName("finance.customer@prod"));
            Assert.Equal(("finance", string.Empty, "cm"), AtlasEntity.SplitQualifiedName("finance@cm"));
            Assert.Equal(("finance", "customer", string.Empty), AtlasEntity.SplitQualifiedName("finance.customer"));
            Assert.Equal((string.Empty, string.Empty, string.Empty), AtlasEntity.SplitQualifiedName(""));
        }

        [Fact]
        public void An_item_id_is_the_guid_made_alphanumeric_and_stays_within_the_limit()
        {
            string id = AtlasPushSource.ItemId("a1b2c3d4-e5f6-4789-abcd-0123456789ab");

            Assert.Equal("aa1b2c3d4e5f64789abcd0123456789ab", id);
            Assert.True(id.Length <= 128);
            Assert.All(id, c => Assert.True(char.IsAsciiLetterOrDigit(c)));

            // Deterministic, which is what makes a re-read an update.
            Assert.Equal(id, AtlasPushSource.ItemId("a1b2c3d4-e5f6-4789-abcd-0123456789ab"));
        }

        // ------------------------------------------------------------------
        // The watermark
        // ------------------------------------------------------------------

        [Fact]
        public async Task A_resumed_catalogue_run_writes_only_what_is_after_the_marker()
        {
            // The bug this pins: the source ordered by the marker and never
            // compared against it, so every entry was re-pushed on every run.
            // And the fix has a trap of its own - a basic-search hit carries no
            // updateTime, so filtering before the detail fetch would compare
            // every entity at the epoch and, once a marker existed, write
            // nothing at all while reporting success. Both directions are here.
            List<PushItem> first = await this.CatalogueAsync(complete: true);

            Assert.NotEmpty(first);

            var store = new CheckpointStore(this.stateDirectory, "cdpatlascatalog", Logger.None);
            CrawlCheckpoint after = store.Read();

            Assert.True(after.HasMarker, "the first run must leave a marker");
            Assert.Equal(1, after.RunCount);

            // Nothing in the fake changed, so a second run has nothing strictly
            // after the marker and writes nothing - rather than re-pushing the
            // lot. Slack is off here so the filter itself is what is pinned.
            List<PushItem> second = await this.CatalogueAsync(slackSeconds: 0);

            Assert.Empty(second);
        }

        [Fact]
        public async Task An_entity_inside_the_slack_window_is_re_read_rather_than_left_stale()
        {
            // Each entity's timestamp is read by its own detail call, so the
            // timestamps in one run are snapshots taken minutes apart across the
            // enrich loop rather than at one instant. An entity altered after
            // its own snapshot, but before a later entity pushed the marker past
            // it, would be filtered out on every later incremental run and would
            // sit stale - content AND ACL - until the next full recrawl.
            //
            // The slack is the same one the HDFS source applies, and re-reading
            // a few entities is upsert-cheap. Not reading a changed one for a
            // week is not.
            await this.CatalogueAsync(complete: true);

            List<PushItem> second = await this.CatalogueAsync();

            Assert.NotEmpty(second);
        }

        [Fact]
        public async Task The_periodic_full_recrawl_re_derives_every_entry()
        {
            // The ACL staleness bound applies to the catalogue too: a Ranger
            // policy change does not touch an Atlas entity's updateTime, so
            // only a full recrawl re-derives who may see an entry.
            new CheckpointStore(this.stateDirectory, "cdpatlascatalog", Logger.None).Write(new CrawlCheckpoint
            {
                MarkerTime = DateTimeOffset.UtcNow.AddYears(1).UtcDateTime.ToString("o"),
                MarkerKey = "zzzz",
                RunCount = 7,
            });

            List<PushItem> items = await this.CatalogueAsync();

            Assert.NotEmpty(items);
        }

        // ------------------------------------------------------------------
        // Configuration and schema
        // ------------------------------------------------------------------

        [Theory]
        [InlineData("AtlasBaseUrl", "http://atlas01.corp:31000", "https")]
        [InlineData("AtlasBaseUrl", "https://atlas01.corp:31443/api/atlas", "without /api/atlas")]
        [InlineData("AtlasBaseUrl", "", "required")]
        [InlineData("AtlasTypes", ";;", "at least one Atlas entity type")]

        // A type this connector has no shape for enumerates and details every
        // one of its entities and then describes none of them: a full crawl, a
        // clean run, and nothing written. hive_column is the tempting one, and
        // is deliberately absent because a column is described as part of its
        // table.
        [InlineData("AtlasTypes", "hive_db;hive_column", "cannot describe")]
        [InlineData("AtlasTypes", "HiveTable", "cannot describe")]
        public void An_atlas_setting_that_would_fail_at_runtime_fails_at_startup(
            string key, string value, string expected)
        {
            PushOptions options = AtlasOptions();
            options.Settings[key] = value;

            var errors = new ValidationErrors();
            new AtlasCatalogueConnector().ValidateOptions(options, errors);

            Assert.True(errors.HasErrors, key + "=" + value + " should have been rejected");
            Assert.Contains(errors.Errors, e =>
                e.StartsWith("Settings:" + key + ":", StringComparison.Ordinal) &&
                e.Contains(expected, StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void The_shipped_catalogue_configuration_is_valid_apart_from_its_placeholders()
        {
            PushOptions options = AtlasOptions();
            IPushConnector connector = new AtlasCatalogueConnector();

            PushHost.ApplyDefaults(options, connector);

            ValidationErrors errors = options.Validate(requireSharedAcl: !connector.ItemsCarryTheirOwnAcl);
            connector.Validate(options, errors);

            Assert.False(errors.HasErrors, errors.ToMessage());
        }

        [Fact]
        public void The_catalogue_schema_obeys_the_rules_that_cannot_be_corrected_later()
        {
            Schema schema = new AtlasCatalogueConnector().BuildSchema();

            Assert.Equal("microsoft.graph.externalItem", schema.BaseType);

            foreach (Property property in schema.Properties)
            {
                Assert.True(property.Name.Length <= 32, property.Name);
                Assert.All(property.Name, c => Assert.True(char.IsAsciiLetterOrDigit(c), property.Name));
                Assert.False(
                    (property.IsSearchable ?? false) && (property.IsRefinable ?? false),
                    property.Name + " is both searchable and refinable");
            }
        }

        [Fact]
        public void The_three_cdp_connectors_keep_their_own_connections()
        {
            var connectors = new IPushConnector[]
            {
                new HdfsDocumentsConnector(), new HiveContractsConnector(), new AtlasCatalogueConnector(),
            };

            Assert.Equal(
                connectors.Length,
                connectors.Select(c => c.DefaultConnectionId).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Equal(
                connectors.Length,
                connectors.Select(c => c.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());

            PushOptions options = AtlasOptions();
            options.Graph.ConnectionId = "cdphdfsdocs";

            var errors = new ValidationErrors();
            PushHost.RejectNeighboursConnection(options, new AtlasCatalogueConnector(), connectors, errors);

            Assert.Contains(errors.Errors, e => e.Contains("'cdphdfsdocs' connector", StringComparison.Ordinal));
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private async Task<List<PushItem>> CatalogueAsync(
            FakeAtlas atlas = null,
            RangerPolicy grant = null,
            RangerPolicy[] extraPolicies = null,
            bool complete = false,
            int? slackSeconds = null)
        {
            atlas ??= new FakeAtlas();

            var policies = new List<RangerPolicy> { grant ?? GrantPolicy() };

            if (extraPolicies is not null)
            {
                policies.AddRange(extraPolicies);
            }

            PushOptions options = AtlasOptions();
            options.Settings["CheckpointDirectory"] = this.stateDirectory;

            if (slackSeconds.HasValue)
            {
                options.Settings["ScanSlackSeconds"] = slackSeconds.Value.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
            }

            var source = new AtlasPushSource(
                CdpSettings.From(options),
                new AtlasClient("https://atlas.test:31443", new HttpClient(atlas), Logger.None, ownsClient: true),
                new RangerPolicyClient(
                    "https://ranger.test:6182",
                    new HttpClient(new FakeRangerPolicies(policies)),
                    Logger.None,
                    ownsClient: true),
                new PrincipalResolver(
                    new Dictionary<string, string> { ["analysts"] = TestData.GroupObjectId },
                    graph: null,
                    Logger.None),
                new CheckpointStore(this.stateDirectory, "cdpatlascatalog", Logger.None),
                Logger.None);

            var items = new List<PushItem>();

            await using (source)
            {
                await foreach (PushItem item in source.ReadAsync(CancellationToken.None))
                {
                    items.Add(item);
                }

                if (complete)
                {
                    foreach (PushItem item in items)
                    {
                        await source.OnItemCommittedAsync(item, CancellationToken.None);
                    }

                    await source.OnCrawlCompletedAsync(CancellationToken.None);
                }
            }

            return items;
        }

        private static RangerPolicy GrantPolicy()
        {
            var policy = new RangerPolicy { Id = 1, Enabled = true, PolicyType = RangerPolicyType.Access };

            policy.SetResource("database", new List<string> { "contracts" });
            policy.SetResource("table", new List<string> { "*" });
            policy.SetResource("column", new List<string> { "*" });
            policy.Allow.Add(Item(new[] { "analysts" }, "select"));

            return policy;
        }

        private static RangerPolicyItem Item(string[] groups, string access)
        {
            var item = new RangerPolicyItem();
            item.Accesses.Add(access);

            foreach (string group in groups)
            {
                item.Groups.Add(group);
            }

            return item;
        }

        internal static PushOptions AtlasOptions()
        {
            PushOptions options = CdpConnectorTests.CdpOptions();

            options.Graph.ConnectionId = "cdpatlascatalog";
            options.Graph.ConnectionName = "Cloudera data catalogue";
            options.Settings["AtlasBaseUrl"] = "https://atlas01.corp.example:31443";
            options.Settings["AtlasTypes"] = "hive_db;hive_table";
            options.Settings["AtlasPageSize"] = "100";
            options.Settings["AtlasIncludeLineage"] = "true";
            options.Settings["EntraGroupMap"] = "analysts=" + TestData.GroupObjectId;

            return options;
        }

        /// <summary>Serves Ranger's policy list from canned policies.</summary>
        private sealed class FakeRangerPolicies : HttpMessageHandler
        {
            private readonly IReadOnlyList<RangerPolicy> policies;

            public FakeRangerPolicies(IReadOnlyList<RangerPolicy> policies)
            {
                this.policies = policies;
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var json = new StringBuilder("[");

                for (int i = 0; i < this.policies.Count; i++)
                {
                    RangerPolicy p = this.policies[i];

                    if (i > 0)
                    {
                        json.Append(',');
                    }

                    json.Append("{\"id\":").Append(p.Id)
                        .Append(",\"isEnabled\":").Append(p.Enabled ? "true" : "false")
                        .Append(",\"policyType\":").Append((int)p.PolicyType)
                        .Append(",\"resources\":{");

                    bool firstResource = true;

                    foreach (string name in new[] { "database", "table", "column" })
                    {
                        System.Collections.Generic.IList<string> values = p.Resource(name);

                        if (values.Count == 0)
                        {
                            continue;
                        }

                        if (!firstResource)
                        {
                            json.Append(',');
                        }

                        firstResource = false;
                        json.Append('"').Append(name).Append("\":{\"values\":[")
                            .Append(string.Join(",", values.Select(v => "\"" + v + "\"")))
                            .Append("]}");
                    }

                    json.Append("},\"policyItems\":[")
                        .Append(string.Join(",", p.Allow.Select(Serialize)))
                        .Append("],\"denyPolicyItems\":[")
                        .Append(string.Join(",", p.Deny.Select(Serialize)))
                        .Append("]}");
                }

                json.Append(']');

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json.ToString(), Encoding.UTF8, "application/json"),
                });
            }

            private static string Serialize(RangerPolicyItem item)
            {
                return "{\"groups\":[" + string.Join(",", item.Groups.Select(g => "\"" + g + "\"")) +
                       "],\"accesses\":[" +
                       string.Join(",", item.Accesses.Select(a => "{\"type\":\"" + a + "\",\"isAllowed\":true}")) +
                       "]}";
            }
        }

        /// <summary>An Atlas made of canned JSON, in the shapes Atlas 2.1.0 returns.</summary>
        private sealed class FakeAtlas : HttpMessageHandler
        {
            internal const string TableGuid = "a1b2c3d4-e5f6-4789-abcd-0123456789ab";
            internal const string DbGuid = "b1b2c3d4-e5f6-4789-abcd-0123456789ab";

            /// <summary>How many entities a full page holds, matching AtlasPageSize in the test options.</summary>
            private const int PageSize = 100;

            public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

            /// <summary>When true the table hit comes back scrubbed, as Ranger blanks it.</summary>
            public bool Scrubbed { get; set; }

            /// <summary>What the lineage endpoint answers, for a type Atlas will not serve it for.</summary>
            public HttpStatusCode LineageStatus { get; set; } = HttpStatusCode.OK;

            /// <summary>
            /// When true the first page of tables is entirely scrubbed and the
            /// real table is on the second, which is what a restricted database
            /// whose tables sort together looks like.
            /// </summary>
            public bool ScrubbedFirstPage { get; set; }

            /// <summary>How many lineage requests were made, and for what.</summary>
            public List<string> LineageRequests { get; } = new List<string>();

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (this.Status != HttpStatusCode.OK)
                {
                    return Task.FromResult(new HttpResponseMessage(this.Status));
                }

                string path = request.RequestUri.AbsolutePath;
                string query = request.RequestUri.Query;
                string body;

                if (path.EndsWith("/search/basic", StringComparison.Ordinal))
                {
                    bool tables = query.Contains("hive_table", StringComparison.Ordinal);
                    bool firstPage = query.Contains("offset=0", StringComparison.Ordinal);

                    if (tables && this.ScrubbedFirstPage)
                    {
                        // A whole page of entities this caller may not read,
                        // then the real one. A pager that reads "this page added
                        // nothing" as "the catalogue ends here" stops on the
                        // first and never sees the second.
                        body = firstPage
                            ? "{\"entities\":[" + string.Join(",", Enumerable.Repeat(Scrub(), PageSize)) + "]}"
                            : query.Contains("offset=" + PageSize, StringComparison.Ordinal)
                                ? "{\"entities\":[" +
                                  Header(TableGuid, "hive_table", "contract", "contracts.contract@cm") + "]}"
                                : "{\"entities\":[]}";
                    }
                    else if (!firstPage)
                    {
                        // A second page must come back empty, or the pager loops.
                        body = "{\"entities\":[]}";
                    }
                    else if (!tables)
                    {
                        body = "{\"entities\":[" + Header(DbGuid, "hive_db", "contracts", "contracts@cm") + "]}";
                    }
                    else if (this.Scrubbed)
                    {
                        body = "{\"entities\":[" + Scrub() + "]}";
                    }
                    else
                    {
                        body = "{\"entities\":[" +
                               Header(TableGuid, "hive_table", "contract", "contracts.contract@cm") + "]}";
                    }
                }
                else if (path.Contains("/lineage/", StringComparison.Ordinal))
                {
                    string guid = path[(path.LastIndexOf('/') + 1) ..];

                    this.LineageRequests.Add(guid);

                    // Atlas serves lineage for entities deriving from DataSet or
                    // Process. A hive_db derives from neither, so a perfectly
                    // healthy Atlas answers 400 - which is the shape that made
                    // the shipped configuration unable to finish a crawl.
                    if (string.Equals(guid, DbGuid, StringComparison.Ordinal) ||
                        this.LineageStatus != HttpStatusCode.OK)
                    {
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
                        {
                            Content = new StringContent(
                                "{\"errorCode\":\"ATLAS-400-00-06D\",\"errorMessage\":\"Invalid lineage entity type\"}",
                                Encoding.UTF8,
                                "application/json"),
                        });
                    }

                    // Hive does not join two tables directly: it records
                    // table -> hive_process -> table, and the process's name is
                    // the query text. A walk that stops at the first neighbour
                    // names the SQL rather than the table.
                    body = "{\"baseEntityGuid\":\"" + TableGuid + "\",\"guidEntityMap\":{" +
                           "\"proc-in\":{\"typeName\":\"hive_process\",\"displayText\":" +
                           "\"insert overwrite table contracts.contract select * from contracts.raw_contracts\"," +
                           "\"attributes\":{\"qualifiedName\":\"contracts.contract@cm:1699\"}}," +
                           "\"proc-out\":{\"typeName\":\"hive_process\",\"displayText\":\"create table mart\"," +
                           "\"attributes\":{\"qualifiedName\":\"mart.contract_mart@cm:1700\"}}," +
                           "\"up-1\":{\"typeName\":\"hive_table\",\"displayText\":\"raw_contracts\"," +
                           "\"attributes\":{\"qualifiedName\":\"contracts.raw_contracts@cm\"}}," +
                           "\"down-1\":{\"typeName\":\"hive_table\",\"displayText\":\"contract_mart\"," +
                           "\"attributes\":{\"qualifiedName\":\"" + this.DownstreamQualifiedName + "\"}}}," +
                           "\"relations\":[" +
                           "{\"fromEntityId\":\"up-1\",\"toEntityId\":\"proc-in\"}," +
                           "{\"fromEntityId\":\"proc-in\",\"toEntityId\":\"" + TableGuid + "\"}," +
                           "{\"fromEntityId\":\"" + TableGuid + "\",\"toEntityId\":\"proc-out\"}," +
                           "{\"fromEntityId\":\"proc-out\",\"toEntityId\":\"down-1\"}]}";
                }
                else if (path.Contains("/entity/guid/", StringComparison.Ordinal))
                {
                    bool table = path.EndsWith(TableGuid, StringComparison.Ordinal);

                    body = "{\"entity\":{\"guid\":\"" + (table ? TableGuid : DbGuid) + "\"," +
                           "\"typeName\":\"" + (table ? "hive_table" : "hive_db") + "\"," +
                           "\"status\":\"ACTIVE\",\"updateTime\":1756000000000," +
                           "\"attributes\":{\"owner\":\"priya.raman\"," +
                           "\"description\":\"Executed customer contracts.\"}," +
                           "\"classifications\":[{\"typeName\":\"PII\"}]," +
                           "\"meanings\":[{\"displayText\":\"Contract\"}]," +
                           (table
                               ? "\"relationshipAttributes\":{\"columns\":[" +
                                 "{\"displayText\":\"contract_ref\"},{\"displayText\":\"counterparty\"}]}"
                               : "\"relationshipAttributes\":{}") +
                           "}}";
                }
                else
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                });
            }

            /// <summary>
            /// Where the downstream neighbour lives. In the granted database by
            /// default; a test moves it out to prove the neighbour check bites.
            /// </summary>
            public string DownstreamQualifiedName { get; set; } = "contracts.contract_mart@cm";

            private static string Scrub()
            {
                // Atlas blanks a hit the caller may not read and leaves it in
                // the array with guid "-1", rather than removing it.
                return "{\"guid\":\"-1\",\"typeName\":\"hive_table\"," +
                       "\"attributes\":{},\"classificationNames\":[],\"meaningNames\":[]}";
            }

            private static string Header(string guid, string type, string name, string qualifiedName)
            {
                return "{\"guid\":\"" + guid + "\",\"typeName\":\"" + type + "\",\"status\":\"ACTIVE\"," +
                       "\"displayText\":\"" + name + "\",\"attributes\":{\"name\":\"" + name +
                       "\",\"qualifiedName\":\"" + qualifiedName + "\",\"owner\":\"priya.raman\"}," +
                       "\"classificationNames\":[\"PII\"],\"meaningNames\":[]}";
            }
        }
    }
}
