// ---------------------------------------------------------------------------
// ItemHasherTests.cs
// The hash decides whether an item is written. These tests pin the two ways it
// can be wrong, and they are not symmetric.
//
// A hash that CHANGES when the item did not costs a wasted write - annoying,
// visible, self-correcting. A hash that STAYS THE SAME when the item did leaves
// the index permanently wrong with nothing reporting it, because the engine
// will skip that item on every subsequent run for ever. Every test below that
// asserts two hashes differ is guarding the second failure; the determinism
// tests guard the first.
//
// The determinism tests deserve a word, because they look like they are
// asserting that a function is a function. They are not. They assert that the
// hash does not depend on dictionary enumeration order, on the current culture,
// or on field boundaries being unambiguous - three properties that hold by
// construction in ItemHasher and would each, if broken, rewrite the entire
// corpus on some future night without anyone changing a line of connector code.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Threading;
    using Microsoft.Graph.Models.ExternalConnectors;
    using PushCore;
    using PushCore.State;
    using Xunit;

    public class ItemHasherTests
    {
        [Fact]
        public void The_same_item_hashes_the_same_way_twice()
        {
            PushItem item = Item("te-1", "TimeEntry");

            byte[] first = Hash(item);
            byte[] second = Hash(item);

            Assert.Equal(first, second);
            Assert.Equal(ItemHasher.HashBytes, first.Length);
        }

        [Fact]
        public void Property_insertion_order_does_not_change_the_hash()
        {
            // THE ONE THAT REWRITES THE CORPUS AFTER A RUNTIME UPGRADE.
            // Dictionary enumeration order is an implementation detail that has
            // changed between .NET versions. A hash that depended on it would be
            // stable in every test and on every developer machine, and would
            // change for every item at once the night someone patched the server
            // - presenting as a mysteriously enormous run rather than as a bug.
            var forwards = new PushItem { Id = "te-1", ItemType = "TimeEntry", Content = "text" };
            forwards.AddIfPresent("alpha", "1");
            forwards.AddIfPresent("beta", "2");
            forwards.AddIfPresent("gamma", "3");

            var backwards = new PushItem { Id = "te-1", ItemType = "TimeEntry", Content = "text" };
            backwards.AddIfPresent("gamma", "3");
            backwards.AddIfPresent("beta", "2");
            backwards.AddIfPresent("alpha", "1");

            Assert.Equal(Hash(forwards), Hash(backwards));
        }

        [Fact]
        public void Field_boundaries_are_unambiguous()
        {
            // Without a length prefix, ("ab", "c") and ("a", "bc") concatenate to
            // the same bytes. That is not a theoretical collision: it is two
            // ordinary edits to two ordinary properties producing an item the
            // engine would decline to write.
            var first = new PushItem { Id = "x-1", ItemType = "item" };
            first.AddIfPresent("p", "ab");
            first.AddIfPresent("q", "c");

            var second = new PushItem { Id = "x-1", ItemType = "item" };
            second.AddIfPresent("p", "a");
            second.AddIfPresent("q", "bc");

            Assert.NotEqual(Hash(first), Hash(second));
        }

        [Fact]
        public void A_property_name_and_its_value_cannot_be_confused_for_each_other()
        {
            // The same framing argument, one level up: a property called "ab"
            // holding "c" must not hash as a property called "a" holding "bc".
            var first = new PushItem { Id = "x-1", ItemType = "item" };
            first.AddIfPresent("ab", "c");

            var second = new PushItem { Id = "x-1", ItemType = "item" };
            second.AddIfPresent("a", "bc");

            Assert.NotEqual(Hash(first), Hash(second));
        }

        [Fact]
        public void The_hash_does_not_depend_on_the_current_culture()
        {
            // A double rendered as "1,5" on a German host and "1.5" on an English
            // one is the same value and a different hash, and the two hosts would
            // then rewrite each other's items on every run for ever. Nothing
            // about that failure looks like a locale problem from the outside.
            var item = new PushItem { Id = "te-1", ItemType = "TimeEntry", Content = "text" };
            item.AddIfPresent("hours", 7.5d);
            item.AddIfPresent("count", 1234L);

            byte[] invariant = WithCulture(CultureInfo.InvariantCulture, () => Hash(item));
            byte[] german = WithCulture(new CultureInfo("de-DE"), () => Hash(item));
            byte[] turkish = WithCulture(new CultureInfo("tr-TR"), () => Hash(item));

            Assert.Equal(invariant, german);
            Assert.Equal(invariant, turkish);
        }

        [Fact]
        public void An_unspecified_datetime_is_taken_as_utc_rather_than_shifted()
        {
            // THE ONE THAT WOULD HAVE HAD TWO HOSTS REWRITE EACH OTHER'S ITEMS.
            // ToUniversalTime on a DateTime whose Kind is Unspecified shifts it
            // by the HOST's local offset - and Unspecified is exactly what
            // SqlDataReader.GetDateTime returns, so it is the ordinary case, not
            // an edge one. Two connectors in different timezones would each see
            // every one of the other's items as changed, for ever.
            //
            // The assertion is the property rather than a reproduction: the same
            // wall-clock value, once Unspecified and once stamped UTC, must hash
            // alike. On a host already at UTC this passes either way - it is the
            // developer machines and non-UTC deployment hosts where it bites,
            // which is precisely where it went unnoticed.
            var unspecified = new PushItem { Id = "te-1", ItemType = "TimeEntry", Content = "text" };
            unspecified.Properties["workDate"] = new DateTime(2026, 3, 14, 9, 30, 0, DateTimeKind.Unspecified);

            var stamped = new PushItem { Id = "te-1", ItemType = "TimeEntry", Content = "text" };
            stamped.Properties["workDate"] = new DateTime(2026, 3, 14, 9, 30, 0, DateTimeKind.Utc);

            Assert.Equal(Hash(stamped), Hash(unspecified));
        }

        [Fact]
        public void A_datetime_that_says_what_it_is_is_still_converted()
        {
            // The other half: treating Unspecified as UTC must not stop a value
            // that genuinely carries an offset from being normalised. Local and
            // Utc kinds naming the same instant have to agree.
            var local = new PushItem { Id = "x-1", ItemType = "item" };
            local.Properties["at"] = new DateTime(2026, 3, 14, 9, 30, 0, DateTimeKind.Utc).ToLocalTime();

            var utc = new PushItem { Id = "x-1", ItemType = "item" };
            utc.Properties["at"] = new DateTime(2026, 3, 14, 9, 30, 0, DateTimeKind.Utc);

            Assert.Equal(Hash(utc), Hash(local));
        }

        [Fact]
        public void Changing_the_content_changes_the_hash()
        {
            PushItem before = Item("te-1", "TimeEntry");
            PushItem after = Item("te-1", "TimeEntry");
            after.Content = before.Content + " and one more sentence";

            Assert.NotEqual(Hash(before), Hash(after));
        }

        [Fact]
        public void Changing_a_property_changes_the_hash()
        {
            PushItem before = Item("te-1", "TimeEntry");
            PushItem after = Item("te-1", "TimeEntry");
            after.Properties["customerName"] = "Northwind Traders";

            Assert.NotEqual(Hash(before), Hash(after));
        }

        [Fact]
        public void Removing_a_property_changes_the_hash()
        {
            // The case a naive "hash the values" implementation misses: dropping
            // a property leaves the remaining values identical, so only the names
            // being in the hash catches it. An item that lost its consultant name
            // must not read as unchanged.
            PushItem before = Item("te-1", "TimeEntry");
            PushItem after = Item("te-1", "TimeEntry");
            after.Properties.Remove("consultantName");

            Assert.NotEqual(Hash(before), Hash(after));
        }

        [Fact]
        public void Changing_the_item_type_changes_the_hash()
        {
            Assert.NotEqual(Hash(Item("x-1", "Customer")), Hash(Item("x-1", "Engagement")));
        }

        [Fact]
        public void A_string_collection_keeps_its_order()
        {
            // A collection's order is content, not incidental: the connector
            // chose it and Graph stores it, so two orders of the same tags are
            // two different stored values. This is the one place in the file
            // where NOT sorting is the correct decision.
            var first = new PushItem { Id = "x-1", ItemType = "item" };
            first.AddIfPresent("tags", new[] { "PII", "GDPR" });

            var second = new PushItem { Id = "x-1", ItemType = "item" };
            second.AddIfPresent("tags", new[] { "GDPR", "PII" });

            Assert.NotEqual(Hash(first), Hash(second));
        }

        [Fact]
        public void The_acl_is_hashed_apart_from_the_content()
        {
            // An item whose text is identical but whose grants moved MUST be
            // rewritten - the ACL is what trims the answer, so leaving it stale
            // is an access-control decision rather than a performance one. The
            // two hashes are separate so the engine can tell which one moved.
            PushItem item = Item("te-1", "TimeEntry");

            byte[] contentBefore = Hash(item);
            byte[] aclBefore = ItemHasher.HashAcl(Acl("11111111-1111-1111-1111-111111111111"));
            byte[] aclAfter = ItemHasher.HashAcl(Acl(
                "11111111-1111-1111-1111-111111111111",
                "22222222-2222-2222-2222-222222222222"));

            Assert.Equal(contentBefore, Hash(item));
            Assert.NotEqual(aclBefore, aclAfter);
        }

        [Fact]
        public void Grant_order_does_not_change_the_acl_hash()
        {
            // A source that enumerates groups in a different order on two runs
            // would otherwise rewrite every item it owns, on every run, for ever.
            const string A = "11111111-1111-1111-1111-111111111111";
            const string B = "22222222-2222-2222-2222-222222222222";

            Assert.Equal(ItemHasher.HashAcl(Acl(A, B)), ItemHasher.HashAcl(Acl(B, A)));
        }

        [Fact]
        public void An_external_group_is_not_the_same_grant_as_an_entra_group_with_the_same_id()
        {
            // The principal type is in the hash, and it must be: an external
            // group registered on the connection and an Entra group are
            // different principals even when the identifier matches.
            var entra = new List<Acl>
            {
                new Acl
                {
                    Type = AclType.Group,
                    Value = "11111111-1111-1111-1111-111111111111",
                    AccessType = AccessType.Grant,
                },
            };

            var external = new List<Acl>
            {
                new Acl
                {
                    Type = AclType.ExternalGroup,
                    Value = "11111111-1111-1111-1111-111111111111",
                    AccessType = AccessType.Grant,
                },
            };

            Assert.NotEqual(ItemHasher.HashAcl(entra), ItemHasher.HashAcl(external));
        }

        [Fact]
        public void A_grant_and_a_deny_over_the_same_principal_hash_differently()
        {
            // The engine never builds a deny - PushAclEntry cannot express one -
            // but the hash covers what Graph is SENT, and a hash that ignored
            // access type would let a hand-built deny read as the grant it
            // reverses. Cheap to cover; catastrophic to miss.
            var grant = new List<Acl>
            {
                new Acl
                {
                    Type = AclType.Group,
                    Value = "11111111-1111-1111-1111-111111111111",
                    AccessType = AccessType.Grant,
                },
            };

            var deny = new List<Acl>
            {
                new Acl
                {
                    Type = AclType.Group,
                    Value = "11111111-1111-1111-1111-111111111111",
                    AccessType = AccessType.Deny,
                },
            };

            Assert.NotEqual(ItemHasher.HashAcl(grant), ItemHasher.HashAcl(deny));
        }

        [Fact]
        public void An_absent_acl_hashes_to_something_stable_rather_than_throwing()
        {
            // The engine refuses to write an item with no grants, so this value
            // should never reach the store. A hash function that threw here would
            // turn that refusal into a crash on the row that triggered it.
            byte[] first = ItemHasher.HashAcl(null);
            byte[] second = ItemHasher.HashAcl(new List<Acl>());

            Assert.Equal(ItemHasher.HashBytes, first.Length);
            Assert.Equal(first, second);
        }

        [Fact]
        public void Two_different_items_with_identical_content_hash_differently()
        {
            // The item ID is in the content hash, so a duplicated row cannot be
            // compared against the wrong stored state if a caller ever mismatched
            // the key.
            PushItem first = Item("te-1", "TimeEntry");
            PushItem second = Item("te-2", "TimeEntry");
            second.Content = first.Content;

            foreach (KeyValuePair<string, object> property in first.Properties)
            {
                second.Properties[property.Key] = property.Value;
            }

            Assert.NotEqual(Hash(first), Hash(second));
        }

        [Fact]
        public void A_stored_state_matches_only_when_both_hashes_match()
        {
            // CrawlItemState.Matches is what the engine actually calls, and it
            // has to be an AND. An OR - or comparing only the content - is the
            // defect that leaves a re-permissioned item stale in the index.
            byte[] content = Hash(Item("te-1", "TimeEntry"));
            byte[] acl = ItemHasher.HashAcl(Acl("11111111-1111-1111-1111-111111111111"));
            byte[] otherAcl = ItemHasher.HashAcl(Acl("22222222-2222-2222-2222-222222222222"));
            byte[] otherContent = Hash(Item("te-2", "TimeEntry"));

            var state = new CrawlItemState("te-1", "TimeEntry", content, acl, 42, 0);

            Assert.True(state.Matches(content, acl));
            Assert.False(state.Matches(content, otherAcl));
            Assert.False(state.Matches(otherContent, acl));
            Assert.False(state.Matches(otherContent, otherAcl));
        }

        private static byte[] Hash(PushItem item)
        {
            return ItemHasher.HashContent(item.Id, item.ItemType, item.Properties, item.Content);
        }

        private static PushItem Item(string id, string itemType)
        {
            var item = new PushItem
            {
                Id = id,
                ItemType = itemType,
                Content = "Consultco engagement work recorded against Contoso Ltd.",
            };

            item.AddIfPresent("customerName", "Contoso Ltd");
            item.AddIfPresent("consultantName", "A. Consultant");
            item.AddIfPresent("hours", 7.5d);
            item.AddIfPresent("billable", true);

            return item;
        }

        private static List<Acl> Acl(params string[] objectIds)
        {
            return objectIds
                .Select(id => new Acl
                {
                    Type = AclType.Group,
                    Value = id,
                    AccessType = AccessType.Grant,
                })
                .ToList();
        }

        private static T WithCulture<T>(CultureInfo culture, Func<T> action)
        {
            CultureInfo original = Thread.CurrentThread.CurrentCulture;

            try
            {
                Thread.CurrentThread.CurrentCulture = culture;
                return action();
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = original;
            }
        }
    }
}
