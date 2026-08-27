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

            // A grant over every column constrains nothing.
            Assert.Empty(new RoutingEvaluator(new[] { GrantPolicy() }).CatalogueColumns("contracts", "contract"));
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
            Assert.Equal("PII", table.Properties["classifications"]);
            Assert.Equal("Contract", table.Properties["glossaryTerms"]);
            Assert.Equal("raw_contracts", table.Properties["upstream"]);
            Assert.Equal("contract_mart", table.Properties["downstream"]);

            // The body reads as sentences, because "which table holds the
            // counterparty" is a question asked in words.
            Assert.Contains("Owned by priya.raman", table.Content, StringComparison.Ordinal);
            Assert.Contains("Produced from raw_contracts", table.Content, StringComparison.Ordinal);
            Assert.Contains("counterparty", table.Content, StringComparison.Ordinal);
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

            // Nothing in the fake changed, so a second run has nothing after
            // the marker and writes nothing - rather than re-pushing the lot.
            List<PushItem> second = await this.CatalogueAsync();

            Assert.Empty(second);
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
            FakeAtlas atlas = null, RangerPolicy grant = null, RangerPolicy[] extraPolicies = null, bool complete = false)
        {
            atlas ??= new FakeAtlas();

            var policies = new List<RangerPolicy> { grant ?? GrantPolicy() };

            if (extraPolicies is not null)
            {
                policies.AddRange(extraPolicies);
            }

            PushOptions options = AtlasOptions();
            options.Settings["CheckpointDirectory"] = this.stateDirectory;

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
            private const string TableGuid = "a1b2c3d4-e5f6-4789-abcd-0123456789ab";
            private const string DbGuid = "b1b2c3d4-e5f6-4789-abcd-0123456789ab";

            public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

            /// <summary>When true the table hit comes back scrubbed, as Ranger blanks it.</summary>
            public bool Scrubbed { get; set; }

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

                    // A second page must come back empty, or the pager loops.
                    if (query.Contains("offset=0", StringComparison.Ordinal) == false)
                    {
                        body = "{\"entities\":[]}";
                    }
                    else if (!tables)
                    {
                        body = "{\"entities\":[" + Header(DbGuid, "hive_db", "contracts", "contracts@cm") + "]}";
                    }
                    else if (this.Scrubbed)
                    {
                        body = "{\"entities\":[{\"guid\":\"-1\",\"typeName\":\"hive_table\"," +
                               "\"attributes\":{},\"classificationNames\":[],\"meaningNames\":[]}]}";
                    }
                    else
                    {
                        body = "{\"entities\":[" +
                               Header(TableGuid, "hive_table", "contract", "contracts.contract@cm") + "]}";
                    }
                }
                else if (path.Contains("/lineage/", StringComparison.Ordinal))
                {
                    body = "{\"baseEntityGuid\":\"" + TableGuid + "\",\"guidEntityMap\":{" +
                           "\"up-1\":{\"displayText\":\"raw_contracts\"}," +
                           "\"down-1\":{\"displayText\":\"contract_mart\"}}," +
                           "\"relations\":[" +
                           "{\"fromEntityId\":\"up-1\",\"toEntityId\":\"" + TableGuid + "\"}," +
                           "{\"fromEntityId\":\"" + TableGuid + "\",\"toEntityId\":\"down-1\"}]}";
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
