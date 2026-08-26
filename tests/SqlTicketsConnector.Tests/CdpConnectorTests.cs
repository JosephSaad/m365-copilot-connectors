// ---------------------------------------------------------------------------
// CdpConnectorTests.cs
// The decisions the CDP connector makes before it reads anything, and the ones
// it would be most expensive to get wrong.
//
// Three of these are control evidence rather than unit tests:
//
//   * A table carrying a Ranger row filter or column mask is never indexed.
//     One indexed copy cannot show different rows to different people, so
//     indexing it would either leak the unfiltered rows or store a masked
//     version and lie to the people entitled to the real one.
//   * The watermark comparison. Getting "strictly after, ties broken by key"
//     backwards loses rows silently, which is the failure mode nobody notices.
//   * The composed ODBC connection string carries no credential, and the
//     inspection refuses one smuggled in through the extra options.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using CdpConnector.Source;
    using CdpConnector.Source.Acl;
    using CdpConnector.Source.Hdfs;
    using CdpConnector.Source.Hive;
    using CdpConnector.Source.Ranger;
    using CdpConnector.Source.Watermark;
    using CdpGraphPush;
    using global::Connector.Security.Configuration;
    using Microsoft.Graph.Models.ExternalConnectors;
    using PushCore;
    using Serilog.Core;
    using SqlTicketsConnector.Tests.TestSupport;
    using Xunit;

    public class CdpConnectorTests
    {
        // ------------------------------------------------------------------
        // Routing: what must never be indexed
        // ------------------------------------------------------------------

        [Theory]
        [InlineData(2, "row-level filter")]
        [InlineData(1, "masks at least one column")]
        public void A_table_ranger_filters_or_masks_is_routed_to_a_live_query(int policyType, string expected)
        {
            // The rule the whole routing doctrine rests on. A filter and a mask
            // are per-user transforms applied when a query runs; an index holds
            // one copy of the row and cannot apply either.
            var evaluator = new RoutingEvaluator(new[]
            {
                Policy(1, RangerPolicyType.Access, "contracts", "contract", allowGroups: new[] { "analysts" }),
                Policy(2, (RangerPolicyType)policyType, "contracts", "contract"),
            });

            RoutingDecision decision = evaluator.EvaluateTable("contracts", "contract");

            Assert.Equal(RoutingVerdict.LiveQuery, decision.Verdict);
            Assert.False(decision.MayIndex);
            Assert.Contains(expected, decision.Reason, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(2L, decision.PolicyIds);

            // And no grants come back, so nothing downstream can index it by
            // accident with the groups the access policy would have given.
            Assert.Empty(decision.Groups);
        }

        [Fact]
        public void A_table_with_a_deny_policy_is_routed_rather_than_mirrored()
        {
            // Graph has deny ACEs, and mirroring one looks like the safe move.
            // It is not: a mirrored deny only protects while the translation is
            // right every time, and a translation that drifts fails open.
            RangerPolicy deny = Policy(7, RangerPolicyType.Access, "contracts", "contract", allowGroups: new[] { "analysts" });
            deny.Deny.Add(new RangerPolicyItem { Groups = { "contractors" }, Accesses = { "select" } });

            RoutingDecision decision = new RoutingEvaluator(new[] { deny }).EvaluateTable("contracts", "contract");

            Assert.Equal(RoutingVerdict.LiveQuery, decision.Verdict);
            Assert.Contains("not mirrored", decision.Reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void A_table_granted_column_by_column_is_routed()
        {
            RangerPolicy scoped = Policy(
                9, RangerPolicyType.Access, "contracts", "contract", allowGroups: new[] { "analysts" });
            scoped.Resources["column"] = new List<string> { "contract_ref", "status" };

            RoutingDecision decision = new RoutingEvaluator(new[] { scoped }).EvaluateTable("contracts", "contract");

            Assert.Equal(RoutingVerdict.LiveQuery, decision.Verdict);
            Assert.Contains("some columns", decision.Reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void A_table_nobody_is_granted_is_not_indexed_either()
        {
            // An item granted to nobody is accepted by Graph and then returned
            // to no one, which reads as success in every log and is not.
            RoutingDecision decision = new RoutingEvaluator(new[]
            {
                Policy(3, RangerPolicyType.Access, "contracts", "contract"),
            }).EvaluateTable("contracts", "contract");

            Assert.Equal(RoutingVerdict.LiveQuery, decision.Verdict);
            Assert.Contains("returned to nobody", decision.Reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void A_plain_table_wide_grant_is_indexable_and_names_its_groups()
        {
            RoutingDecision decision = new RoutingEvaluator(new[]
            {
                Policy(4, RangerPolicyType.Access, "contracts", "contract",
                    allowGroups: new[] { "analysts", "auditors" }),
            }).EvaluateTable("contracts", "contract");

            Assert.Equal(RoutingVerdict.Index, decision.Verdict);
            Assert.Equal(new[] { "analysts", "auditors" }, decision.Groups.ToArray());
        }

        [Fact]
        public void A_deny_on_a_path_stops_its_subtree_being_indexed()
        {
            RangerPolicy deny = new RangerPolicy { Id = 11, PolicyType = RangerPolicyType.Access };
            deny.Resources["path"] = new List<string> { "/data/restricted" };
            deny.Deny.Add(new RangerPolicyItem { Groups = { "everyone" }, Accesses = { "read" } });

            var evaluator = new RoutingEvaluator(new[] { deny });

            Assert.False(evaluator.EvaluatePath("/data/restricted/q1.txt").MayIndex);

            // A sibling whose name merely starts the same way is not covered:
            // "/data/restricted-public" is a different directory.
            Assert.True(evaluator.EvaluatePath("/data/restricted-public/q1.txt").MayIndex);
        }

        // ------------------------------------------------------------------
        // Permissions
        // ------------------------------------------------------------------

        [Fact]
        public void The_owning_group_grants_nothing_when_the_permission_bits_do_not()
        {
            // A file owned by group "finance" with mode 600 grants finance
            // nothing. Treating ownership as access would invent a grant the
            // cluster does not give.
            var closed = new HdfsFileStatus { Group = "finance", Permission = "600" };
            var open = new HdfsFileStatus { Group = "finance", Permission = "640" };

            Assert.Empty(HdfsAclBuilder.ClusterGroups(closed, null, Array.Empty<string>()));
            Assert.Equal(new[] { "finance" }, HdfsAclBuilder.ClusterGroups(open, null, Array.Empty<string>()).ToArray());
        }

        [Fact]
        public void A_named_acl_entry_grants_and_a_default_entry_does_not()
        {
            // A default entry describes what a file created here would inherit,
            // not who may read what is here now.
            //
            // The mode is 640, not 600, and that is load-bearing: on a file with
            // an extended ACL the middle digit is the ACL MASK, and every named
            // entry's grant is its own bits AND the mask. This test used to pass
            // 600 and still expect analysts to be granted, which is the
            // over-grant CdpAclMaskTests now covers from both directions.
            var status = new HdfsFileStatus { Group = "owners", Permission = "640" };
            var acl = new HdfsAclStatus();
            acl.Entries.Add("group:analysts:r--");
            acl.Entries.Add("group:writers:-w-");
            acl.Entries.Add("default:group:futurereaders:r--");
            acl.Entries.Add("user:someone:r--");

            IReadOnlyList<string> groups = HdfsAclBuilder.ClusterGroups(status, acl, Array.Empty<string>());

            Assert.Equal(new[] { "analysts" }, groups.ToArray());
        }

        [Fact]
        public async Task An_unresolved_group_is_dropped_rather_than_guessed()
        {
            // Dropping narrows the audience. Every other option widens it, and
            // widening the audience of the one item whose permissions could not
            // be established is the least defensible thing available.
            var resolver = new PrincipalResolver(
                new Dictionary<string, string> { ["known"] = TestData.GroupObjectId },
                graph: null,
                Logger.None);

            List<PushAclEntry> grants = await resolver.ResolveAsync(
                new[] { "known", "unknown" }, CancellationToken.None);

            PushAclEntry only = Assert.Single(grants);
            Assert.Equal(TestData.GroupObjectId, only.Value);
            Assert.Equal(PushAclType.Group, only.Type);
            Assert.Contains("unknown", resolver.Unresolved);
        }

        [Fact]
        public async Task A_world_readable_file_grants_nothing_unless_a_group_was_named_for_it()
        {
            // "Everyone with an account on the cluster" and "everyone in the
            // tenant" are different sets of people.
            var status = new HdfsFileStatus { Group = "owners", Permission = "604" };

            var silent = new HdfsAclBuilder(
                new PrincipalResolver(new Dictionary<string, string>(), null, Logger.None), string.Empty);

            Assert.Empty(await silent.BuildAsync(status, null, Array.Empty<string>(), CancellationToken.None));

            var configured = new HdfsAclBuilder(
                new PrincipalResolver(new Dictionary<string, string>(), null, Logger.None), TestData.GroupObjectId);

            PushAclEntry granted = Assert.Single(
                await configured.BuildAsync(status, null, Array.Empty<string>(), CancellationToken.None));

            Assert.Equal(TestData.GroupObjectId, granted.Value);
        }

        // ------------------------------------------------------------------
        // Watermarks
        // ------------------------------------------------------------------

        [Fact]
        public void The_resume_rule_is_strictly_after_with_ties_broken_by_key()
        {
            // Ties repeat rather than disappear. Two files can share a
            // modification time to the millisecond, and a marker of only the
            // timestamp either re-reads that group for ever or loses whichever
            // of them had not been written when the run stopped.
            var checkpoint = new CrawlCheckpoint
            {
                MarkerTime = "2026-08-20T10:00:00.0000000+00:00",
                MarkerKey = "/data/b.txt",
            };

            DateTimeOffset marker = checkpoint.MarkerTimestamp();

            Assert.True(checkpoint.IsAfter(marker.AddSeconds(1), "/data/a.txt"));   // later time
            Assert.False(checkpoint.IsAfter(marker.AddSeconds(-1), "/data/z.txt")); // earlier time
            Assert.True(checkpoint.IsAfter(marker, "/data/c.txt"));                 // tie, key after
            Assert.False(checkpoint.IsAfter(marker, "/data/b.txt"));                // the marker itself
            Assert.False(checkpoint.IsAfter(marker, "/data/a.txt"));                // tie, key before

            // With no marker at all, everything is after it.
            Assert.True(new CrawlCheckpoint().IsAfter(DateTimeOffset.UnixEpoch, "/data/a.txt"));
        }

        [Fact]
        public void Slack_widens_the_window_backwards_rather_than_forwards()
        {
            var checkpoint = new CrawlCheckpoint
            {
                MarkerTime = "2026-08-20T10:00:00.0000000+00:00",
                MarkerKey = "/data/b.txt",
            };

            DateTimeOffset justBefore = checkpoint.MarkerTimestamp().AddSeconds(-30);

            Assert.False(checkpoint.IsAfter(justBefore, "/data/a.txt"));
            Assert.True(checkpoint.IsAfter(justBefore, "/data/a.txt", slack: 900));
        }

        [Fact]
        public void A_checkpoint_survives_a_round_trip_and_an_unreadable_one_means_recrawl()
        {
            string directory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));

            try
            {
                var store = new CheckpointStore(directory, "cdphdfsdocs", Logger.None);

                store.Write(new CrawlCheckpoint
                {
                    MarkerTime = "2026-08-20T10:00:00.0000000+00:00",
                    MarkerKey = "/data/b.txt",
                    RunCount = 3,
                });

                CrawlCheckpoint read = store.Read();

                Assert.Equal("/data/b.txt", read.MarkerKey);
                Assert.Equal(3, read.RunCount);
                Assert.True(read.HasMarker);

                // Corrupt it: treated as absent, which means a full recrawl.
                // Safe, because every write is an upsert.
                System.IO.File.WriteAllText(store.FilePath, "{ not json");

                Assert.False(store.Read().HasMarker);
            }
            finally
            {
                if (System.IO.Directory.Exists(directory))
                {
                    System.IO.Directory.Delete(directory, true);
                }
            }
        }

        // ------------------------------------------------------------------
        // The Hive query and the connection string
        // ------------------------------------------------------------------

        [Fact]
        public void The_hive_query_resumes_on_the_composite_marker_and_orders_by_it()
        {
            CdpSettings settings = Settings(new Dictionary<string, string>
            {
                ["HiveWatermarkColumn"] = "last_modified_ts",
                ["HiveKeyColumn"] = "contract_ref",
            });

            PushOptions options = CdpOptions();
            options.Source.ItemView = "contracts.contract";

            var checkpoint = new CrawlCheckpoint
            {
                MarkerTime = "2026-08-20 10:00:00",
                MarkerKey = "C-1000",
            };

            string query = HivePushSource.BuildQuery(settings, options, checkpoint);

            Assert.Contains("FROM `contracts`.`contract`", query, StringComparison.Ordinal);
            Assert.Contains("`last_modified_ts` > '2026-08-20 10:00:00'", query, StringComparison.Ordinal);
            Assert.Contains("`contract_ref` > 'C-1000'", query, StringComparison.Ordinal);
            Assert.Contains("ORDER BY `last_modified_ts`, `contract_ref`", query, StringComparison.Ordinal);

            // Without a marker there is no resume predicate, but the ordering
            // stays - a first run must leave a resumable prefix behind it too.
            //
            // There IS still a WHERE, and it is not the resume clause: a row
            // whose watermark column is NULL cannot be ordered against a
            // marker, and Hive sorts NULLs first, so leaving them in lets a
            // capped first run fill its whole window with rows that produce no
            // marker and re-read them for ever. They are excluded, loudly.
            string first = HivePushSource.BuildQuery(settings, options, new CrawlCheckpoint());

            Assert.DoesNotContain("last_modified_ts` > '", first, StringComparison.Ordinal);
            Assert.Contains("IS NOT NULL", first, StringComparison.Ordinal);
            Assert.Contains("ORDER BY", first, StringComparison.Ordinal);
        }

        [Fact]
        public void A_table_with_no_watermark_column_is_read_whole_and_says_so()
        {
            PushOptions options = CdpOptions();
            options.Source.ItemView = "contracts.contract";
            options.Source.MaxItems = 25;

            string query = HivePushSource.BuildQuery(
                Settings(new Dictionary<string, string>()), options, new CrawlCheckpoint());

            Assert.DoesNotContain("WHERE", query, StringComparison.Ordinal);
            Assert.DoesNotContain("ORDER BY", query, StringComparison.Ordinal);
            Assert.Contains("LIMIT 25", query, StringComparison.Ordinal);
        }

        [Fact]
        public void A_marker_containing_a_quote_cannot_break_out_of_its_literal()
        {
            CdpSettings settings = Settings(new Dictionary<string, string>
            {
                ["HiveWatermarkColumn"] = "last_modified_ts",
                ["HiveKeyColumn"] = "contract_ref",
            });

            PushOptions options = CdpOptions();
            options.Source.ItemView = "contracts.contract";

            string query = HivePushSource.BuildQuery(
                settings,
                options,
                new CrawlCheckpoint { MarkerTime = "2026-08-20", MarkerKey = "C' OR '1'='1" });

            Assert.DoesNotContain("OR '1'='1'", query, StringComparison.Ordinal);
            Assert.Contains(@"C\' OR \'1\'=\'1", query, StringComparison.Ordinal);
        }

        [Fact]
        public void The_composed_odbc_string_authenticates_with_kerberos_and_carries_no_credential()
        {
            string connection = HiveConnectionStringFactory.Build(Settings(new Dictionary<string, string>
            {
                ["HiveHost"] = "hs2-01.corp",
                ["HiveRealm"] = "CORP.EXAMPLE",
            }));

            Assert.Contains("AuthMech=1", connection, StringComparison.Ordinal);
            Assert.Contains("UseOnlySSPI=1", connection, StringComparison.Ordinal);
            Assert.Contains("SSL=1", connection, StringComparison.Ordinal);
            Assert.Contains("UseSystemTrustStore=1", connection, StringComparison.Ordinal);

            // ThriftTransport 2 is HTTP. Kerberos does not support the binary
            // transport at all, and 0 is binary.
            Assert.Contains("ThriftTransport=2", connection, StringComparison.Ordinal);

            Assert.DoesNotContain("PWD", connection, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("UID", connection, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData("PWD=hunter2", "credential")]
        [InlineData("UID=svc_ingest", "credential")]
        [InlineData("AllowSelfSignedServerCert=1", "TLS")]
        [InlineData("SSL=0", "TLS")]
        public void A_credential_or_a_downgrade_in_the_extra_options_is_refused(string extra, string expected)
        {
            IReadOnlyList<string> problems = HiveConnectionStringFactory.Inspect(extra);

            string message = Assert.Single(problems);
            Assert.Contains(expected, message, StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------
        // Configuration
        // ------------------------------------------------------------------

        [Fact]
        public void The_shipped_hdfs_configuration_is_valid_apart_from_its_placeholders()
        {
            // Everything the host would run, in order. The placeholders are GUIDs
            // the operator replaces, so they are filled in here rather than
            // asserting that a template with REPLACE-WITH in it validates.
            PushOptions options = CdpOptions();
            IPushConnector connector = new HdfsDocumentsConnector();

            PushHost.ApplyDefaults(options, connector);

            ValidationErrors errors = options.Validate(requireSharedAcl: !connector.ItemsCarryTheirOwnAcl);
            connector.Validate(options, errors);

            Assert.False(errors.HasErrors, errors.ToMessage());
        }

        [Fact]
        public void A_file_connector_needs_no_connection_wide_acl_and_a_table_connector_still_validates()
        {
            // The point of ItemsCarryTheirOwnAcl: an HDFS configuration with an
            // empty Acl section is correct, because every file carries its own
            // grants, while the same emptiness for a shared-ACL connector is not.
            Assert.True(new HdfsDocumentsConnector().ItemsCarryTheirOwnAcl);

            PushOptions options = CdpOptions();
            options.Acl = new AclOptions();

            Assert.False(options.Validate(requireSharedAcl: false).HasErrors);
            Assert.True(options.Validate(requireSharedAcl: true).HasErrors);
        }

        [Theory]
        [InlineData("HdfsBaseUrl", "http://httpfs.corp:14000/webhdfs/v1", "https")]
        [InlineData("HdfsBaseUrl", "https://httpfs.corp:14000", "/webhdfs/v1")]
        [InlineData("HdfsRoots", "data/contracts", "absolute")]
        [InlineData("HdfsRoots", "/data/../etc", "'..'")]
        public void An_hdfs_setting_that_would_fail_at_runtime_fails_at_startup(
            string key, string value, string expected)
        {
            PushOptions options = CdpOptions();
            options.Settings[key] = value;

            var errors = new ValidationErrors();
            new HdfsDocumentsConnector().ValidateOptions(options, errors);

            Assert.True(errors.HasErrors, key + "=" + value + " should have been rejected");
            Assert.Contains(errors.Errors, e =>
                e.StartsWith("Settings:" + key + ":", StringComparison.Ordinal) &&
                e.Contains(expected, StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Turning_off_the_full_recrawl_is_reported_because_it_is_the_acl_staleness_bound()
        {
            // A permission change does not alter a file's modification time, so
            // an incremental crawl never revisits a file whose grant was
            // revoked. The periodic full recrawl is the only thing that does,
            // which makes disabling it a decision to take in writing.
            PushOptions options = CdpOptions();
            options.Settings["FullRecrawlEveryRuns"] = "0";

            var errors = new ValidationErrors();
            new HdfsDocumentsConnector().ValidateOptions(options, errors);

            Assert.Contains(errors.Errors, e =>
                e.StartsWith("Settings:FullRecrawlEveryRuns:", StringComparison.Ordinal) &&
                e.Contains("ACL", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void A_watermark_column_without_a_key_column_is_refused()
        {
            PushOptions options = CdpOptions();
            options.Source.ItemView = "contracts.contract";
            options.Settings["HiveHost"] = "hs2-01.corp";
            options.Settings["HiveWatermarkColumn"] = "last_modified_ts";
            options.Settings["HiveKeyColumn"] = string.Empty;

            var errors = new ValidationErrors();
            new HiveContractsConnector().ValidateOptions(options, errors);

            Assert.Contains(errors.Errors, e =>
                e.StartsWith("Settings:HiveKeyColumn:", StringComparison.Ordinal));
        }

        [Fact]
        public void Mirroring_cluster_local_groups_is_refused_with_the_reason()
        {
            // Not "unsupported": an external group can only contain Entra users
            // and groups, so mirroring a cluster-local group produces a group
            // with nobody in it, and items granted to it reach no one.
            PushOptions options = CdpOptions();
            options.Settings["GroupMappingMode"] = "ExternalGroups";

            var errors = new ValidationErrors();
            new HdfsDocumentsConnector().ValidateOptions(options, errors);

            Assert.Contains(errors.Errors, e =>
                e.StartsWith("Settings:GroupMappingMode:", StringComparison.Ordinal) &&
                e.Contains("Entra", StringComparison.OrdinalIgnoreCase));
        }

        // ------------------------------------------------------------------
        // The connectors themselves
        // ------------------------------------------------------------------

        [Fact]
        public void Both_schemas_obey_the_rules_that_cannot_be_corrected_later()
        {
            // A registered schema cannot be replaced: a property cannot be
            // removed, and refinability cannot be added after the fact. So the
            // guards run here, at the desk, rather than against the tenant.
            foreach (IPushConnector connector in new IPushConnector[]
            {
                new HdfsDocumentsConnector(), new HiveContractsConnector(),
            })
            {
                Schema schema = connector.BuildSchema();

                Assert.Equal("microsoft.graph.externalItem", schema.BaseType);

                foreach (Property property in schema.Properties)
                {
                    Assert.True(property.Name.Length <= 32, property.Name + " is longer than 32 characters");
                    Assert.All(property.Name, c => Assert.True(char.IsAsciiLetterOrDigit(c), property.Name));

                    bool searchable = property.IsSearchable ?? false;
                    bool refinable = property.IsRefinable ?? false;

                    Assert.False(searchable && refinable, property.Name + " is both searchable and refinable");
                }
            }
        }

        [Fact]
        public void The_cdp_connectors_keep_their_own_connections_and_cannot_claim_a_neighbours()
        {
            var connectors = new IPushConnector[] { new HdfsDocumentsConnector(), new HiveContractsConnector() };

            Assert.Equal(
                connectors.Length,
                connectors.Select(c => c.DefaultConnectionId).Distinct(StringComparer.OrdinalIgnoreCase).Count());

            PushOptions options = CdpOptions();
            options.Graph.ConnectionId = "cdphivecontracts";

            var errors = new ValidationErrors();
            PushHost.RejectNeighboursConnection(
                options, new HdfsDocumentsConnector(), connectors, errors);

            Assert.Contains(errors.Errors, e =>
                e.Contains("'cdphivecontracts' connector", StringComparison.Ordinal));
        }

        [Fact]
        public void An_item_id_is_deterministic_bounded_and_alphanumeric()
        {
            // Determinism is what makes a re-read after an interruption an
            // update rather than a second copy of the file.
            string first = HdfsPushSource.ItemId("/data/contracts/q1 report.pdf");
            string again = HdfsPushSource.ItemId("/data/contracts/q1 report.pdf");
            string other = HdfsPushSource.ItemId("/data/contracts/q2 report.pdf");

            Assert.Equal(first, again);
            Assert.NotEqual(first, other);
            Assert.True(first.Length <= 128);
            Assert.All(first, c => Assert.True(char.IsAsciiLetterOrDigit(c)));

            // Two tables sharing a key space must not collide into one item.
            Assert.NotEqual(
                HiveTableSourceFactory.ItemId("a.contract", "C-1"),
                HiveTableSourceFactory.ItemId("b.contract", "C-1"));
        }

        [Fact]
        public void A_row_with_no_key_is_skipped_rather_than_given_an_invented_id()
        {
            PushOptions options = CdpOptions();
            options.Source.ItemView = "contracts.contract";

            Assert.Null(HiveContractsConnector.MapRow(Row(new Dictionary<string, object>()), options));

            PushItem mapped = HiveContractsConnector.MapRow(
                Row(new Dictionary<string, object>
                {
                    ["contract_ref"] = "C-1000",
                    ["counterparty"] = "Northwind",
                    ["status"] = "Open",
                    ["value_amount"] = 1250.5d,
                }),
                options);

            Assert.NotNull(mapped);
            Assert.Equal("C-1000 - Northwind", mapped.Properties["title"]);
            Assert.Equal(1250.5d, mapped.Properties["valueAmount"]);
            Assert.Contains("Northwind", mapped.Content, StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static HiveRow Row(Dictionary<string, object> values)
        {
            var typed = new Dictionary<string, object>(values);
            return new HiveRow(typed.ToDictionary(pair => pair.Key, pair => (object)pair.Value));
        }

        private static RangerPolicy Policy(
            long id, RangerPolicyType type, string database, string table, string[] allowGroups = null)
        {
            var policy = new RangerPolicy { Id = id, PolicyType = type, Enabled = true };

            policy.Resources["database"] = new List<string> { database };
            policy.Resources["table"] = new List<string> { table };

            if (allowGroups is not null)
            {
                var item = new RangerPolicyItem();
                item.Accesses.Add("select");

                foreach (string group in allowGroups)
                {
                    item.Groups.Add(group);
                }

                policy.Allow.Add(item);
            }

            return policy;
        }

        private static CdpSettings Settings(Dictionary<string, string> overrides)
        {
            PushOptions options = CdpOptions();

            foreach (KeyValuePair<string, string> pair in overrides)
            {
                options.Settings[pair.Key] = pair.Value;
            }

            return CdpSettings.From(options);
        }

        /// <summary>A configuration matching the shipped template, with the placeholders filled in.</summary>
        internal static PushOptions CdpOptions()
        {
            return new PushOptions
            {
                Environment = "Production",
                Auth = TestData.ValidAuth(),
                KeyVault = new KeyVaultOptions { Uri = string.Empty, SecretCacheTtlMinutes = 60 },
                DataSource = new DataSourceOptions { MaxContentBytes = 3670016 },
                Acl = new AclOptions(),
                Graph = new GraphSection
                {
                    ConnectionId = "cdphdfsdocs",
                    ConnectionName = "Cloudera HDFS documents",
                    Description = "Documents held in HDFS on the Cloudera CDP cluster",
                    SchemaReadyTimeoutMinutes = 30,
                },
                Source = new SourceSection { ItemView = string.Empty, MaxItems = 0 },
                Settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["HdfsBaseUrl"] = "https://httpfs01.corp.example:14000/webhdfs/v1",
                    ["HdfsRoots"] = "/data/contracts;/data/policies",
                    ["IncludeExtensions"] = "txt;md;docx",
                    ["MaxRawFileBytes"] = "268435456",
                    ["ScanSlackSeconds"] = "900",
                    ["RangerBaseUrl"] = "https://ranger01.corp.example:6182",
                    ["RangerHdfsService"] = "cm_hdfs",
                    ["RangerSqlService"] = "cm_hive",
                    ["EntraGroupMap"] = "hadoop-contracts-read=" + TestData.GroupObjectId,
                    ["ResolveGroupsFromDirectory"] = "false",
                    ["OtherReadableGroupId"] = string.Empty,
                    ["CheckpointDirectory"] = "state",
                    ["FullRecrawlEveryRuns"] = "7",
                    ["MaxItemsPerRun"] = "0",
                    ["ItemBudget"] = "2000000",
                    ["MaxErrorRatePercent"] = "5",
                },
            };
        }
    }
}
