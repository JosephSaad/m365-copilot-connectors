// ---------------------------------------------------------------------------
// CdpRangerExceptionsTests.cs
// Item 0, step 2: the two STATIC constructs are now evaluated rather than
// refused, and both can only ever remove groups from a grant.
//
// The distinction that decides which constructs get evaluated at all is worth
// stating once. allowExceptions and isDenyAllElse do not move: whatever they
// say today they say tomorrow, so honouring them produces an ACL that stays
// correct. Conditions and validity schedules DO move - they depend on the clock
// - and a Graph permission is a static snapshot with nowhere to put one.
// Evaluating those would turn CDP-18's loud refusal into a quiet divergence
// nobody watches for, so they stay refused. These tests pin the first half; the
// refusal tests next door pin the second.
// ---------------------------------------------------------------------------

#nullable enable

namespace SqlTicketsConnector.Tests
{
    using System.Collections.Generic;
    using System.Linq;
    using CdpConnector.Source.Ranger;
    using Xunit;

    public class CdpRangerExceptionsTests
    {
        [Fact]
        public void An_allow_exception_removes_the_group_it_names()
        {
            // "analysts and contractors may select, except contractors."
            RangerPolicy policy = Access(1, "analysts", "contractors");
            policy.AllowExceptions.Add(Item("contractors"));

            RoutingDecision decision = Evaluate(policy);

            Assert.True(decision.MayIndex);
            Assert.Equal(new[] { "analysts" }, decision.Groups);
        }

        [Fact]
        public void An_allow_exception_that_names_everybody_leaves_nothing_to_grant()
        {
            RangerPolicy policy = Access(2, "analysts");
            policy.AllowExceptions.Add(Item("analysts"));

            // No group means nobody to put on the item, so it is not indexed.
            // Before step 2 this policy read as a plain grant to analysts.
            Assert.False(Evaluate(policy).MayIndex);
        }

        [Fact]
        public void An_exception_is_matched_without_regard_to_case()
        {
            RangerPolicy policy = Access(3, "Analysts");
            policy.AllowExceptions.Add(Item("analysts"));

            Assert.False(Evaluate(policy).MayIndex);
        }

        [Fact]
        public void An_exception_on_one_policy_narrows_a_grant_made_by_another()
        {
            // Ranger applies exceptions per policy, but the connector unions
            // grants across policies - so an exception has to be subtracted from
            // the union, not only from its own policy's contribution.
            RangerPolicy granting = Access(4, "analysts", "contractors");
            RangerPolicy excepting = Access(5, "contractors");
            excepting.AllowExceptions.Add(Item("contractors"));

            RoutingDecision decision = Evaluate(granting, excepting);

            Assert.Equal(new[] { "analysts" }, decision.Groups);
        }

        [Fact]
        public void isDenyAllElse_intersects_rather_than_unions()
        {
            // Policy 7 denies everything it does not itself allow, INCLUDING
            // what policy 6 allows. Unioning would grant contractors, which the
            // cluster refuses.
            RangerPolicy open = Access(6, "analysts", "contractors");
            RangerPolicy restrictive = Access(7, "analysts");
            restrictive.DeniesAllElse = true;

            RoutingDecision decision = Evaluate(open, restrictive);

            Assert.Equal(new[] { "analysts" }, decision.Groups);
        }

        [Fact]
        public void isDenyAllElse_is_bound_by_its_own_exceptions()
        {
            // A group this policy allows and then carves out is not permitted by
            // it, so it cannot survive the intersection either.
            RangerPolicy open = Access(8, "analysts", "auditors");
            RangerPolicy restrictive = Access(9, "analysts", "auditors");
            restrictive.DeniesAllElse = true;
            restrictive.AllowExceptions.Add(Item("auditors"));

            Assert.Equal(new[] { "analysts" }, Evaluate(open, restrictive).Groups);
        }

        [Fact]
        public void A_disabled_policys_exception_does_not_narrow_anything()
        {
            // A disabled policy decides nothing, so it cannot take a grant away
            // either. Honouring one would under-grant against a policy the
            // cluster itself ignores.
            RangerPolicy granting = Access(10, "analysts");
            RangerPolicy disabled = Access(11, "analysts");
            disabled.Enabled = false;
            disabled.AllowExceptions.Add(Item("analysts"));

            Assert.Equal(new[] { "analysts" }, Evaluate(granting, disabled).Groups);
        }

        [Fact]
        public void An_ordinary_policy_is_unchanged_by_the_narrowing()
        {
            // The regression that matters: no exceptions, no isDenyAllElse, and
            // the grant must be exactly what it was before step 2.
            RoutingDecision decision = Evaluate(Access(12, "analysts", "auditors"));

            Assert.True(decision.MayIndex);
            Assert.Equal(new[] { "analysts", "auditors" }, decision.Groups.OrderBy(g => g).ToArray());
        }

        private static RoutingDecision Evaluate(params RangerPolicy[] policies)
        {
            foreach (RangerPolicy policy in policies)
            {
                policy.SetResource("database", new List<string> { "finance" });
                policy.SetResource("table", new List<string> { "expenses" });
                policy.SetResource("column", new List<string> { "*" });
            }

            return new RoutingEvaluator(policies).EvaluateTable("finance", "expenses");
        }

        private static RangerPolicy Access(long id, params string[] allowGroups)
        {
            var policy = new RangerPolicy { Id = id, Enabled = true, PolicyType = RangerPolicyType.Access };
            policy.Allow.Add(Item(allowGroups));
            return policy;
        }

        private static RangerPolicyItem Item(params string[] groups)
        {
            var item = new RangerPolicyItem();

            foreach (string group in groups)
            {
                item.Groups.Add(group);
            }

            item.Accesses.Add("select");
            return item;
        }
    }
}
