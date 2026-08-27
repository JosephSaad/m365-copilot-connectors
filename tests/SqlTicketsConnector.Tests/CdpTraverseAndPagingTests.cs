// ---------------------------------------------------------------------------
// CdpTraverseAndPagingTests.cs
// The three findings that hold on every cluster, whatever its Ranger looks
// like - so unlike security zones or tag policies, none of these can be ruled
// out by asking how the customer has configured things.
//
//   RNG-04  Reading a file on HDFS needs read on the file AND execute on every
//           directory above it. The ACL was built from the file alone, so a
//           750 directory holding a 644 file - the ordinary umask outcome -
//           published that file to everyone the file's own bits allowed.
//
//   RNG-05  The policy list was read without paging. A mask or deny past the
//           first page was simply absent, and the table it protects was
//           indexed as though unprotected. The trap in the fix is that Ranger
//           clamps pageSize to its own maximum, so a pager that steps by what
//           it ASKED for skips whatever the clamp withheld.
//
//   RNG-09  Resource matching folded case for paths as well as for Hive names.
//           Hive identifiers are case-insensitive and HDFS paths are not, so a
//           grant written for /data/Finance was applied to /data/finance.
//
// All three fail in the over-grant direction, which is why each is asserted
// from the leaking side rather than only from the working one.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using CdpConnector.Source.Acl;
    using CdpConnector.Source.Hdfs;
    using CdpConnector.Source.Ranger;
    using Serilog.Core;
    using Xunit;

    public class CdpTraverseAndPagingTests
    {
        // ------------------------------------------------------------------
        // RNG-09 - paths are case-sensitive, Hive names are not
        // ------------------------------------------------------------------

        [Fact]
        public void A_path_grant_does_not_reach_a_directory_differing_only_by_case()
        {
            // HDFS is a case-sensitive filesystem: /data/Finance and
            // /data/finance are two directories that can hold different files
            // under different permissions. Folding them together applies a
            // grant written for one to the other.
            RangerPolicy grant = PathPolicy("/data/Finance", recursive: true, "finance-analysts");

            var evaluator = new RoutingEvaluator(new[] { grant });

            Assert.Equal(
                new[] { "finance-analysts" },
                evaluator.EvaluatePath("/data/Finance/ledger.csv").Groups.ToArray());

            Assert.Empty(evaluator.EvaluatePath("/data/finance/ledger.csv").Groups);
        }

        [Fact]
        public void A_hive_grant_still_ignores_case_because_a_table_name_does()
        {
            // The other half of the same change, and the reason it could not
            // simply be made ordinal everywhere: CUSTOMER and customer are one
            // Hive table, and Ranger matches them as one.
            var grant = new RangerPolicy { Id = 5, Enabled = true, PolicyType = RangerPolicyType.Access };
            grant.SetResource("database", new List<string> { "Finance" });
            grant.SetResource("table", new List<string> { "Customer" });
            grant.Allow.Add(Item("analysts", "select"));

            var evaluator = new RoutingEvaluator(new[] { grant });

            Assert.True(evaluator.EvaluateCatalogueEntry("finance", "customer").MayIndex);
            Assert.True(evaluator.EvaluateCatalogueEntry("FINANCE", "CUSTOMER").MayIndex);
        }

        [Fact]
        public void A_path_deny_still_matches_case_sensitively_and_so_refuses_only_its_own_path()
        {
            // A deny is matched conservatively in one direction only - it
            // reaches ancestors whatever isRecursive says. It does not become
            // case-blind, because that would refuse a directory the cluster
            // never denied and the refusal would look like a policy nobody
            // wrote.
            var deny = new RangerPolicy { Id = 9, Enabled = true, PolicyType = RangerPolicyType.Access };
            deny.SetResource("path", new List<string> { "/data/Private" });
            deny.Deny.Add(Item("contractors", "read"));

            var evaluator = new RoutingEvaluator(new[] { deny });

            Assert.False(evaluator.EvaluatePath("/data/Private/notes.txt").MayIndex);
            Assert.True(evaluator.EvaluatePath("/data/private/notes.txt").MayIndex);
        }

        // ------------------------------------------------------------------
        // RNG-04 - execute on every directory above the file
        // ------------------------------------------------------------------

        [Fact]
        public void A_group_that_cannot_traverse_the_directory_does_not_get_the_file()
        {
            // The layout that makes this the ordinary case rather than a corner
            // one: a directory locked to its own group, holding a file left
            // group-readable by the default umask. On the cluster nobody
            // outside fin-controllers can read that file at all.
            var directory = new HdfsFileStatus
            {
                Path = "/data/restricted",
                Type = "DIRECTORY",
                Group = "fin-controllers",
                Permission = "750",
            };

            var file = new HdfsFileStatus
            {
                Path = "/data/restricted/board-pack.csv",
                Type = "FILE",
                Group = "hadoop",
                Permission = "644",
            };

            (IReadOnlyList<string> traversers, bool everyone) =
                HdfsAclBuilder.TraverseGroups(directory, acl: null);

            Assert.Equal(new[] { "fin-controllers" }, traversers.ToArray());
            Assert.False(everyone);

            var gate = new HashSet<string>(traversers, System.StringComparer.OrdinalIgnoreCase);

            // Without the gate the file's own bits hand it to "hadoop".
            Assert.Contains(
                "hadoop",
                HdfsAclBuilder.ClusterGroups(file, acl: null, System.Array.Empty<string>()));

            // With it, nobody: "hadoop" cannot get through the directory.
            Assert.Empty(
                HdfsAclBuilder.ClusterGroups(file, acl: null, System.Array.Empty<string>(), gate));
        }

        [Fact]
        public void A_ranger_grant_is_not_gated_by_the_directory_bits()
        {
            // The asymmetry that keeps the gate honest. A Ranger path policy
            // authorises the path itself rather than deferring to the POSIX
            // walk, so a group Ranger grants is not subject to the mode bits of
            // the directories above it. Gating it would refuse files the
            // cluster genuinely serves.
            var file = new HdfsFileStatus
            {
                Path = "/data/restricted/board-pack.csv",
                Type = "FILE",
                Group = "hadoop",
                Permission = "644",
            };

            var gate = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

            Assert.Equal(
                new[] { "audit-readers" },
                HdfsAclBuilder.ClusterGroups(file, acl: null, new[] { "audit-readers" }, gate).ToArray());
        }

        [Fact]
        public void A_null_gate_is_not_an_empty_one()
        {
            // The distinction the whole fix turns on. Null means no ancestor
            // restricted anybody; empty means nobody gets through. Conflating
            // them strips every grant off every file, which would look like the
            // connector quietly indexing nothing.
            var file = new HdfsFileStatus
            {
                Path = "/data/open/report.csv",
                Type = "FILE",
                Group = "hadoop",
                Permission = "640",
            };

            Assert.Contains(
                "hadoop",
                HdfsAclBuilder.ClusterGroups(file, acl: null, System.Array.Empty<string>(), traversable: null));

            Assert.Empty(HdfsAclBuilder.ClusterGroups(
                file,
                acl: null,
                System.Array.Empty<string>(),
                new HashSet<string>(System.StringComparer.OrdinalIgnoreCase)));
        }

        [Fact]
        public void A_directory_mask_decides_traversal_the_way_it_decides_read()
        {
            // The same trap the read path already had: on a directory carrying
            // an extended ACL the group digit of the mode is the MASK, not the
            // owning group's permission. A mask without execute revokes every
            // named entry's traversal however the entry itself reads.
            var directory = new HdfsFileStatus
            {
                Path = "/data/restricted",
                Type = "DIRECTORY",
                Group = "fin-controllers",
                Permission = "740",
            };

            var acl = new HdfsAclStatus { Group = "fin-controllers", Permission = "740" };
            acl.Entries.Add("group::r-x");
            acl.Entries.Add("group:analysts:r-x");

            (IReadOnlyList<string> traversers, _) = HdfsAclBuilder.TraverseGroups(directory, acl);

            Assert.Empty(traversers);

            // Put execute back in the mask and both entries come through.
            directory.Permission = "750";
            acl.Permission = "750";

            (IReadOnlyList<string> withMask, _) = HdfsAclBuilder.TraverseGroups(directory, acl);

            Assert.Equal(new[] { "fin-controllers", "analysts" }, withMask.ToArray());
        }

        // ------------------------------------------------------------------
        // RNG-05 - the policy list is paged
        // ------------------------------------------------------------------

        [Fact]
        public async Task Every_page_of_the_policy_list_is_read()
        {
            // 640 policies behind a server that serves 200 at a time. The mask
            // that matters is policy 512, well past the first page: before this
            // was paged, the connector never saw it and indexed the table it
            // protects.
            var ranger = new PagingRanger(total: 640, serverPageSize: 200);

            IReadOnlyList<RangerPolicy> policies = await Client(ranger)
                .PoliciesAsync("cm_hive", CancellationToken.None);

            Assert.Equal(640, policies.Count);
            Assert.Contains(policies, p => p.Id == 512);
        }

        [Fact]
        public async Task The_pager_steps_by_what_a_page_held_not_by_what_it_asked_for()
        {
            // The trap in the obvious fix. Ranger clamps pageSize to
            // ranger.db.maxrows.default, so a request for a thousand comes back
            // with two hundred - and a loop that then advances the index by a
            // thousand skips the eight hundred in between without ever noticing.
            var ranger = new PagingRanger(total: 640, serverPageSize: 200);

            IReadOnlyList<RangerPolicy> policies = await Client(ranger)
                .PoliciesAsync("cm_hive", CancellationToken.None);

            Assert.Equal(
                Enumerable.Range(0, 640).ToArray(),
                policies.Select(p => (int)p.Id).OrderBy(id => id).ToArray());

            Assert.Equal(new[] { 0, 200, 400, 600 }, ranger.StartIndexes.Take(4).ToArray());
        }

        [Fact]
        public async Task A_server_that_ignores_the_start_index_does_not_spin()
        {
            // The other way a pager fails: a server answering every offset with
            // page one. The loop stops on a page that added nothing new rather
            // than reading the same two hundred policies for ever.
            var ranger = new PagingRanger(total: 640, serverPageSize: 200) { IgnoreStartIndex = true };

            IReadOnlyList<RangerPolicy> policies = await Client(ranger)
                .PoliciesAsync("cm_hive", CancellationToken.None);

            Assert.Equal(200, policies.Count);
            Assert.True(ranger.StartIndexes.Count <= 3, "the pager should stop, not spin");
        }

        // ------------------------------------------------------------------
        // RNG-01 - a zoned policy set is refused, not read zone-blind
        // ------------------------------------------------------------------

        [Fact]
        public async Task A_policy_in_a_security_zone_stops_the_run()
        {
            // The connector applies every policy to every resource. Ranger does
            // not: a resource inside a zone is evaluated against that zone's
            // policies only. Reading them together applies a legacy unzoned
            // grant to a table the zone protects, and hands the item to people
            // the cluster refuses - so the run stops instead.
            var ranger = new ZonedRanger("eu-pii");

            InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(
                () => Client(ranger).PoliciesAsync("cm_hive", CancellationToken.None));

            Assert.Contains("eu-pii", thrown.Message, System.StringComparison.Ordinal);
            Assert.Contains("cm_hive", thrown.Message, System.StringComparison.Ordinal);
            Assert.Contains("security zone", thrown.Message, System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task An_unzoned_policy_set_is_read_normally()
        {
            // The guard must not fire on the ordinary cluster. Ranger omits
            // zoneName on an unzoned policy and some builds send it empty;
            // both mean unzoned, and reading either as a zone would stop every
            // run everywhere.
            foreach (string zoneName in new[] { null, string.Empty, "   " })
            {
                var ranger = new ZonedRanger(zoneName);

                IReadOnlyList<RangerPolicy> policies =
                    await Client(ranger).PoliciesAsync("cm_hive", CancellationToken.None);

                Assert.Single(policies);
                Assert.Empty(policies[0].ZoneName);
            }
        }

        [Fact]
        public async Task The_refusal_names_the_zones_rather_than_only_counting_them()
        {
            // An operator reading this in a log at 03:00 needs to know which
            // zone to look at, not that some number of policies were zoned.
            var ranger = new ZonedRanger("eu-pii", "uk-retail");

            InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(
                () => Client(ranger).PoliciesAsync("cm_hive", CancellationToken.None));

            Assert.Contains("eu-pii", thrown.Message, System.StringComparison.Ordinal);
            Assert.Contains("uk-retail", thrown.Message, System.StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static RangerPolicyClient Client(HttpMessageHandler ranger)
        {
            return new RangerPolicyClient(
                "https://ranger.test:6182", new HttpClient(ranger), Logger.None, ownsClient: true);
        }

        private static RangerPolicy PathPolicy(string path, bool recursive, string group)
        {
            var policy = new RangerPolicy { Id = 1, Enabled = true, PolicyType = RangerPolicyType.Access };
            policy.SetResource("path", new List<string> { path }, isExcludes: false, isRecursive: recursive);
            policy.Allow.Add(Item(group, "read"));
            return policy;
        }

        private static RangerPolicyItem Item(string group, string access)
        {
            var item = new RangerPolicyItem();
            item.Accesses.Add(access);
            item.Groups.Add(group);
            return item;
        }

        /// <summary>A Ranger serving one policy per named zone.</summary>
        private sealed class ZonedRanger : HttpMessageHandler
        {
            private readonly string[] zoneNames;

            public ZonedRanger(params string[] zoneNames)
            {
                this.zoneNames = zoneNames;
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);

                // One page only: the second request must come back empty or the
                // pager keeps asking.
                if (int.Parse(query["startIndex"] ?? "0") > 0)
                {
                    return Task.FromResult(Ok("[]"));
                }

                var json = new StringBuilder("[");

                for (int i = 0; i < this.zoneNames.Length; i++)
                {
                    if (i > 0)
                    {
                        json.Append(',');
                    }

                    json.Append("{\"id\":").Append(i)
                        .Append(",\"isEnabled\":true,\"policyType\":0");

                    // A null name stands for the field Ranger omits entirely.
                    if (this.zoneNames[i] is not null)
                    {
                        json.Append(",\"zoneName\":\"").Append(this.zoneNames[i]).Append('"');
                    }

                    json.Append(",\"resources\":{},\"policyItems\":[]}");
                }

                json.Append(']');

                return Task.FromResult(Ok(json.ToString()));
            }

            private static HttpResponseMessage Ok(string body)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
            }
        }

        /// <summary>A Ranger that pages, and that clamps pageSize the way Ranger does.</summary>
        private sealed class PagingRanger : HttpMessageHandler
        {
            private readonly int total;
            private readonly int serverPageSize;

            public PagingRanger(int total, int serverPageSize)
            {
                this.total = total;
                this.serverPageSize = serverPageSize;
            }

            /// <summary>Gets the startIndex of every request, in order.</summary>
            public List<int> StartIndexes { get; } = new List<int>();

            /// <summary>Gets or sets a value indicating whether to answer every offset with page one.</summary>
            public bool IgnoreStartIndex { get; set; }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);

                int startIndex = int.Parse(query["startIndex"] ?? "0");
                this.StartIndexes.Add(startIndex);

                if (this.IgnoreStartIndex)
                {
                    startIndex = 0;
                }

                var json = new StringBuilder("[");

                for (int i = 0; i < this.serverPageSize && startIndex + i < this.total; i++)
                {
                    if (i > 0)
                    {
                        json.Append(',');
                    }

                    json.Append("{\"id\":").Append(startIndex + i)
                        .Append(",\"isEnabled\":true,\"policyType\":0,\"resources\":{},\"policyItems\":[]}");
                }

                json.Append(']');

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json.ToString(), Encoding.UTF8, "application/json"),
                });
            }
        }
    }
}
