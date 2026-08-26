// ---------------------------------------------------------------------------
// CdpRangerFidelityTests.cs
// Reading a Ranger policy the way Ranger reads it.
//
// The connector decides what may be indexed from the cluster's own policies, so
// any field of a policy it parses away is a field whose meaning it gets wrong.
// Three were being dropped, and each one failed in the direction that indexes
// too much:
//
//   isExcludes   "every finance table EXCEPT salaries" was read as "salaries",
//                the exact inverse, so the excluded table was the one indexed.
//   isRecursive  a grant on one directory was applied to its whole subtree.
//   wildcards    Ranger matches * and ? anywhere; only a trailing * was
//                handled, so a row-filter policy named "*_pii" was invisible
//                and the filtered table was read and indexed.
//
// One asymmetry is deliberate and is tested as such: grants are matched
// faithfully, denies conservatively. A grant that matches too much over-grants,
// while a deny that matches too little fails open, so a deny covering any
// ancestor of a path disqualifies indexing whatever its recursive flag says.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using CdpConnector.Source.Ranger;
    using Xunit;

    public class CdpRangerFidelityTests
    {
        // ------------------------------------------------------------------
        // isExcludes
        // ------------------------------------------------------------------

        [Fact]
        public void An_excluded_table_is_not_covered_by_the_policy_that_excludes_it()
        {
            // "finance-analysts may select every finance table except salaries."
            RangerPolicy policy = Access(55, allowGroups: new[] { "finance-analysts" });
            policy.SetResource("database", new List<string> { "finance" });
            policy.SetResource("table", new List<string> { "salaries" }, isExcludes: true);
            policy.SetResource("column", new List<string> { "*" });

            var evaluator = new RoutingEvaluator(new[] { policy });

            // The excluded table has no grant, so there is nobody to put on its
            // items and it is not indexed.
            RoutingDecision salaries = evaluator.EvaluateTable("finance", "salaries");
            Assert.False(salaries.MayIndex);

            // Every other finance table still is.
            RoutingDecision expenses = evaluator.EvaluateTable("finance", "expenses");
            Assert.True(expenses.MayIndex);
            Assert.Contains("finance-analysts", expenses.Groups, StringComparer.OrdinalIgnoreCase);
        }

        [Fact]
        public void An_exclusion_with_no_values_does_not_invert_into_matching_nothing()
        {
            // A resource the policy puts no constraint on is not the same as a
            // resource it excludes everything from.
            RangerPolicy policy = Access(56, allowGroups: new[] { "analysts" });
            policy.SetResource("database", new List<string> { "finance" });
            policy.SetResource("table", new List<string> { "expenses" });
            policy.SetResource("column", new List<string>(), isExcludes: true);

            Assert.True(new RoutingEvaluator(new[] { policy }).EvaluateTable("finance", "expenses").MayIndex);
        }

        // ------------------------------------------------------------------
        // isRecursive
        // ------------------------------------------------------------------

        [Fact]
        public void A_non_recursive_path_grant_stops_at_the_directory_it_names()
        {
            RangerPolicy policy = Access(42, allowGroups: new[] { "hadoop-all-staff" });
            policy.SetResource("path", new List<string> { "/data/contracts" }, isRecursive: false);

            var evaluator = new RoutingEvaluator(new[] { policy });

            Assert.Contains(
                "hadoop-all-staff",
                evaluator.EvaluatePath("/data/contracts").Groups,
                StringComparer.OrdinalIgnoreCase);

            // The file beneath it is a different resource, and Ranger would not
            // grant it. Returning the group here is the over-grant.
            Assert.DoesNotContain(
                "hadoop-all-staff",
                evaluator.EvaluatePath("/data/contracts/legal/settlement.docx").Groups,
                StringComparer.OrdinalIgnoreCase);
        }

        [Fact]
        public void A_recursive_path_grant_reaches_the_whole_subtree()
        {
            RangerPolicy policy = Access(43, allowGroups: new[] { "hadoop-all-staff" });
            policy.SetResource("path", new List<string> { "/data/contracts" }, isRecursive: true);

            Assert.Contains(
                "hadoop-all-staff",
                new RoutingEvaluator(new[] { policy }).EvaluatePath("/data/contracts/legal/settlement.docx").Groups,
                StringComparer.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------
        // Wildcards
        // ------------------------------------------------------------------

        [Theory]
        [InlineData("*_pii", "customer_pii", true)]
        [InlineData("*_pii", "customer_public", false)]
        [InlineData("cust*er", "customer", true)]
        [InlineData("cust*er", "custer", true)]
        [InlineData("cust*er", "customers", false)]
        [InlineData("customer?", "customer1", true)]
        [InlineData("customer?", "customer12", false)]
        [InlineData("customer", "customer", true)]
        [InlineData("customer", "customer_pii", false)]
        [InlineData("*", "anything", true)]
        public void A_resource_value_matches_the_way_ranger_matches_it(
            string value, string candidate, bool expected)
        {
            RangerPolicy policy = Access(60);
            policy.SetResource("table", new List<string> { value });

            Assert.Equal(expected, policy.Covers("table", candidate));
        }

        [Fact]
        public void A_row_filter_named_with_a_leading_wildcard_still_refuses_the_table()
        {
            // The failure this rule exists to prevent: the grant matched, the
            // row filter did not, so a filtered table was read and indexed.
            RangerPolicy grant = Access(70, allowGroups: new[] { "finance-analysts" });
            grant.SetResource("database", new List<string> { "finance" });
            grant.SetResource("table", new List<string> { "*" });

            var filter = new RangerPolicy { Id = 71, Enabled = true, PolicyType = RangerPolicyType.RowFilter };
            filter.SetResource("database", new List<string> { "finance" });
            filter.SetResource("table", new List<string> { "*_pii" });

            RoutingDecision decision =
                new RoutingEvaluator(new[] { grant, filter }).EvaluateTable("finance", "customer_pii");

            Assert.Equal(RoutingVerdict.LiveQuery, decision.Verdict);
            Assert.Contains(71L, decision.PolicyIds);
        }

        // ------------------------------------------------------------------
        // Denies, conservatively
        // ------------------------------------------------------------------

        [Fact]
        public void A_non_recursive_deny_still_covers_everything_beneath_it()
        {
            // Deliberately not faithful. A grant matching too much over-grants;
            // a deny matching too little fails open, so the deny wins wider.
            var deny = new RangerPolicy { Id = 88, Enabled = true, PolicyType = RangerPolicyType.Access };
            deny.SetResource("path", new List<string> { "/data/restricted" }, isRecursive: false);
            deny.Deny.Add(Item(new[] { "everyone" }, "read"));

            var evaluator = new RoutingEvaluator(new[] { deny });

            Assert.False(evaluator.EvaluatePath("/data/restricted/q1.txt").MayIndex);
            Assert.False(evaluator.EvaluatePath("/data/restricted").MayIndex);

            // A sibling whose name merely begins the same way is a different
            // directory, and widening a deny must not reach it.
            Assert.True(evaluator.EvaluatePath("/data/restricted-public/q1.txt").MayIndex);
        }

        // ------------------------------------------------------------------
        // Disabled policies
        // ------------------------------------------------------------------

        [Fact]
        public void A_disabled_policy_decides_nothing_however_it_is_written()
        {
            RangerPolicy grant = Access(90, allowGroups: new[] { "analysts" });
            grant.SetResource("database", new List<string> { "finance" });
            grant.SetResource("table", new List<string> { "*" });

            var disabledFilter = new RangerPolicy
            {
                Id = 91,
                Enabled = false,
                PolicyType = RangerPolicyType.RowFilter,
            };

            disabledFilter.SetResource("database", new List<string> { "finance" });
            disabledFilter.SetResource("table", new List<string> { "*_pii" });

            // A disabled row filter does not refuse the table...
            Assert.True(
                new RoutingEvaluator(new[] { grant, disabledFilter }).EvaluateTable("finance", "customer_pii").MayIndex);

            // ...and a disabled exclusion does not take a grant away.
            RangerPolicy disabledExclusion = Access(92, allowGroups: new[] { "analysts" });
            disabledExclusion.Enabled = false;
            disabledExclusion.SetResource("database", new List<string> { "finance" });
            disabledExclusion.SetResource("table", new List<string> { "expenses" }, isExcludes: true);

            Assert.True(
                new RoutingEvaluator(new[] { grant, disabledExclusion }).EvaluateTable("finance", "expenses").MayIndex);
        }

        // ------------------------------------------------------------------
        // The wire format
        // ------------------------------------------------------------------

        [Fact]
        public void The_parser_keeps_the_flags_that_change_what_a_policy_means()
        {
            const string Json = @"[
              {
                ""id"": 55,
                ""name"": ""finance-except-salaries"",
                ""isEnabled"": true,
                ""policyType"": 0,
                ""resources"": {
                  ""database"": { ""values"": [""finance""], ""isExcludes"": false, ""isRecursive"": false },
                  ""table"":    { ""values"": [""salaries""], ""isExcludes"": true,  ""isRecursive"": false },
                  ""path"":     { ""values"": [""/data/x""],  ""isExcludes"": false, ""isRecursive"": true }
                },
                ""policyItems"": [
                  { ""groups"": [""finance-analysts""], ""accesses"": [{ ""type"": ""select"", ""isAllowed"": true }] }
                ]
              }
            ]";

            using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(Json);

            RangerPolicy policy = Assert.Single(RangerPolicyClient.Parse(document.RootElement));

            Assert.True(policy.IsExcludes("table"));
            Assert.False(policy.IsExcludes("database"));
            Assert.True(policy.IsRecursive("path"));
            Assert.False(policy.IsRecursive("database"));

            // And the flags actually reach the matcher.
            Assert.False(policy.Covers("table", "salaries"));
            Assert.True(policy.Covers("table", "expenses"));
        }

        [Fact]
        public void A_resource_with_no_flags_on_the_wire_takes_rangers_own_defaults()
        {
            const string Json = @"[
              {
                ""id"": 12, ""isEnabled"": true, ""policyType"": 0,
                ""resources"": { ""path"": { ""values"": [""/data/y""] } },
                ""policyItems"": [
                  { ""groups"": [""g""], ""accesses"": [{ ""type"": ""read"", ""isAllowed"": true }] }
                ]
              }
            ]";

            using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(Json);

            RangerPolicy policy = Assert.Single(RangerPolicyClient.Parse(document.RootElement));

            Assert.False(policy.IsExcludes("path"));
            Assert.False(policy.IsRecursive("path"));
        }

        // ------------------------------------------------------------------

        private static RangerPolicy Access(long id, string[] allowGroups = null)
        {
            var policy = new RangerPolicy { Id = id, Enabled = true, PolicyType = RangerPolicyType.Access };

            if (allowGroups is not null)
            {
                policy.Allow.Add(Item(allowGroups, "select"));
            }

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
    }
}
