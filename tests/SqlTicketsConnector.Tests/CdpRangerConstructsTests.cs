// ---------------------------------------------------------------------------
// CdpRangerConstructsTests.cs
// The four Ranger constructs that fail OPEN, and the two that fail closed.
//
// What-is-Next item 0. RoutingEvaluator reads policyItems and denyPolicyItems
// and nothing else, so allowExceptions, conditions, validitySchedules and
// isDenyAllElse are all read as absent - and every one of them makes the
// cluster MORE restrictive than this connector computes. Reading them as absent
// therefore writes an access-control list that is too generous, which is the
// direction that matters.
//
// These tests exist because that failure is silent. Security zones stop the run
// with a message; these did not stop anything at all, and the only evidence
// would have been an item reaching somebody Ranger refuses.
//
// The two that fail closed - denyExceptions, and grants to named users - are
// asserted NOT to stop the run. Refusing on them would cost content over a
// defect that only ever hides content, and a guard that fires on the safe
// direction teaches operators to disable guards.
// ---------------------------------------------------------------------------

#nullable enable

namespace SqlTicketsConnector.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using CdpConnector.Source.Ranger;
    using Serilog.Core;
    using Xunit;

    public class CdpRangerConstructsTests
    {
        private static RangerPolicyClient Client(HttpMessageHandler ranger)
        {
            return new RangerPolicyClient(
                "https://ranger.test:6182", new HttpClient(ranger), Logger.None, ownsClient: true);
        }

        private static Task<IReadOnlyList<RangerPolicy>> Read(string policyJson)
        {
            return Client(new OnePageRanger(policyJson)).PoliciesAsync("cm_hive", CancellationToken.None);
        }

        private static async Task<string> Refusal(string policyJson)
        {
            InvalidOperationException error =
                await Assert.ThrowsAsync<InvalidOperationException>(() => Read(policyJson));

            return error.Message;
        }

        [Fact]
        public async Task An_ordinary_policy_set_is_read_normally()
        {
            // The guard must not fire on a healthy cluster. Every other test
            // here is worthless if this one can regress.
            IReadOnlyList<RangerPolicy> policies = await Read(
                "{\"id\":1,\"isEnabled\":true,\"policyType\":0," +
                "\"policyItems\":[{\"groups\":[\"analysts\"],\"accesses\":[{\"type\":\"select\",\"isAllowed\":true}]}]}");

            Assert.Single(policies);
            Assert.False(policies[0].HasAllowExceptions);
            Assert.False(policies[0].HasConditions);
            Assert.False(policies[0].DeniesAllElse);
        }

        [Fact]
        public async Task An_allowExceptions_block_no_longer_stops_the_run()
        {
            // Step 2: it is EVALUATED now rather than refused. It is static, so
            // honouring it is permanent, and it can only ever remove groups from
            // a grant - see RoutingEvaluator.NarrowByExceptions.
            IReadOnlyList<RangerPolicy> policies = await Read(
                "{\"id\":7,\"isEnabled\":true,\"policyType\":0," +
                "\"policyItems\":[{\"groups\":[\"analysts\"],\"accesses\":[{\"type\":\"select\",\"isAllowed\":true}]}]," +
                "\"allowExceptions\":[{\"groups\":[\"contractors\"]}]}");

            Assert.Single(policies);
            Assert.True(policies[0].HasAllowExceptions);
            Assert.Equal("contractors", policies[0].AllowExceptions[0].Groups[0]);
        }

        [Fact]
        public async Task A_policy_level_condition_stops_the_run()
        {
            string message = await Refusal(
                "{\"id\":8,\"isEnabled\":true,\"policyType\":0," +
                "\"conditions\":[{\"type\":\"accessed-after-expiry\",\"values\":[\"yes\"]}]}");

            Assert.Contains("a condition", message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task An_item_level_condition_stops_the_run()
        {
            // The EXPIRES_ON pattern seen on the QA cluster puts the condition
            // on the deny item rather than on the policy, so looking only at the
            // policy level would have missed the one that prompted this guard.
            string message = await Refusal(
                "{\"id\":4,\"isEnabled\":true,\"policyType\":0," +
                "\"denyPolicyItems\":[{\"groups\":[\"public\"]," +
                "\"accesses\":[{\"type\":\"select\",\"isAllowed\":true}]," +
                "\"conditions\":[{\"type\":\"accessed-after-expiry\",\"values\":[\"yes\"]}]}]}");

            Assert.Contains("a condition", message, StringComparison.Ordinal);
            Assert.Contains("4", message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task A_validity_schedule_stops_the_run()
        {
            string message = await Refusal(
                "{\"id\":9,\"isEnabled\":true,\"policyType\":0," +
                "\"validitySchedules\":[{\"startTime\":\"2026/01/01 00:00:00\"}]}");

            Assert.Contains("validity schedule", message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task isDenyAllElse_no_longer_stops_the_run()
        {
            // Also evaluated: the grant is intersected with the policy's own
            // allow list rather than unioned with it.
            IReadOnlyList<RangerPolicy> policies = await Read(
                "{\"id\":10,\"isEnabled\":true,\"policyType\":0,\"isDenyAllElse\":true}");

            Assert.Single(policies);
            Assert.True(policies[0].DeniesAllElse);
        }

        [Fact]
        public async Task A_disabled_policy_carrying_a_construct_does_not_stop_the_run()
        {
            // A disabled policy decides nothing, so it cannot over-grant either.
            // Refusing on one would block a crawl over a policy the cluster
            // itself ignores.
            IReadOnlyList<RangerPolicy> policies = await Read(
                "{\"id\":11,\"isEnabled\":false,\"policyType\":0," +
                "\"allowExceptions\":[{\"groups\":[\"contractors\"]}]}");

            Assert.Single(policies);
        }

        [Fact]
        public async Task A_denyExceptions_block_does_not_stop_the_run()
        {
            // Fails closed: it denies people the cluster exempts, which costs
            // content rather than exposing it. Logged, not refused.
            IReadOnlyList<RangerPolicy> policies = await Read(
                "{\"id\":12,\"isEnabled\":true,\"policyType\":0," +
                "\"denyPolicyItems\":[{\"groups\":[\"public\"]}]," +
                "\"denyExceptions\":[{\"groups\":[\"platform-admin\"]}]}");

            Assert.Single(policies);
            Assert.True(policies[0].HasDenyExceptions);
        }

        [Fact]
        public async Task A_grant_to_a_named_user_does_not_stop_the_run()
        {
            // RoutingEvaluator reads item.Groups and never item.Users, so this
            // grant is dropped - under-granting, not over-granting.
            IReadOnlyList<RangerPolicy> policies = await Read(
                "{\"id\":13,\"isEnabled\":true,\"policyType\":0," +
                "\"policyItems\":[{\"users\":[\"jsmith\"]," +
                "\"accesses\":[{\"type\":\"select\",\"isAllowed\":true}]}]}");

            Assert.Single(policies);
            Assert.True(policies[0].NamesUsers);
        }

        [Fact]
        public async Task Only_the_time_varying_constructs_are_named_in_the_refusal()
        {
            // A policy carrying all three stops on the schedule alone. Naming
            // the evaluated two would send an operator to remove something the
            // connector now handles.
            string message = await Refusal(
                "{\"id\":14,\"isEnabled\":true,\"policyType\":0," +
                "\"allowExceptions\":[{\"groups\":[\"c\"]}]," +
                "\"validitySchedules\":[{\"startTime\":\"2026/01/01 00:00:00\"}]," +
                "\"isDenyAllElse\":true}");

            // The message still MENTIONS both, to say they are handled - what it
            // must not do is list them as reasons the run stopped.
            Assert.Contains("carrying a validity schedule", message, StringComparison.Ordinal);
            Assert.DoesNotContain("an allowExceptions block", message, StringComparison.Ordinal);
            Assert.Contains("no longer refused here", message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task The_refusal_cannot_be_disabled()
        {
            string message = await Refusal(
                "{\"id\":15,\"isEnabled\":true,\"policyType\":0," +
                "\"validitySchedules\":[{\"startTime\":\"2026/01/01 00:00:00\"}]}");

            Assert.Contains("no setting that disables this", message, StringComparison.Ordinal);
        }


        [Fact]
        public async Task A_tag_service_holding_only_grants_does_not_stop_the_run()
        {
            // Not reading a tag GRANT under-grants: it costs content rather than
            // exposing it. Refusing on one would block a crawl over a policy
            // that could only ever have made this connector too cautious.
            await Client(new OnePageRanger(
                "{\"id\":20,\"isEnabled\":true,\"policyType\":0," +
                "\"policyItems\":[{\"groups\":[\"analysts\"]," +
                "\"accesses\":[{\"type\":\"select\",\"isAllowed\":true}]}]}"))
                .RefuseTagPoliciesAsync("cm_tag", CancellationToken.None);
        }

        [Fact]
        public async Task A_tag_deny_stops_the_run()
        {
            // The QA cluster's policy 4: a deny on group public, invisible to a
            // connector that reads resource services only.
            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Client(new OnePageRanger(
                    "{\"id\":4,\"isEnabled\":true,\"policyType\":0," +
                    "\"denyPolicyItems\":[{\"groups\":[\"public\"]," +
                    "\"accesses\":[{\"type\":\"select\",\"isAllowed\":true}]}]}"))
                    .RefuseTagPoliciesAsync("cm_tag", CancellationToken.None));

            Assert.Contains("deny or mask", error.Message, StringComparison.Ordinal);
            Assert.Contains("4", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task A_tag_masking_policy_stops_the_run()
        {
            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Client(new OnePageRanger("{\"id\":21,\"isEnabled\":true,\"policyType\":1}"))
                    .RefuseTagPoliciesAsync("cm_tag", CancellationToken.None));

            Assert.Contains("deny or mask", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task A_disabled_tag_deny_does_not_stop_the_run()
        {
            await Client(new OnePageRanger(
                "{\"id\":22,\"isEnabled\":false,\"policyType\":0," +
                "\"denyPolicyItems\":[{\"groups\":[\"public\"]}]}"))
                .RefuseTagPoliciesAsync("cm_tag", CancellationToken.None);
        }

        [Fact]
        public async Task An_empty_tag_service_name_skips_the_check_entirely()
        {
            // Right for a cluster with no tag service; wrong for one that simply
            // did not configure it, which is why the deployment guide names it.
            await Client(new OnePageRanger("{\"id\":23,\"isEnabled\":true,\"policyType\":0," +
                "\"denyPolicyItems\":[{\"groups\":[\"public\"]}]}"))
                .RefuseTagPoliciesAsync(string.Empty, CancellationToken.None);
        }

        private sealed class OnePageRanger : HttpMessageHandler
        {
            private readonly string body;

            public OnePageRanger(string policyJson)
            {
                this.body = policyJson;
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);

                // One page: the second request must come back empty or the pager
                // keeps asking.
                string json = int.Parse(query["startIndex"] ?? "0") > 0
                    ? "[]"
                    : new StringBuilder("[").Append(this.body).Append(']').ToString();

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                });
            }
        }
    }
}
