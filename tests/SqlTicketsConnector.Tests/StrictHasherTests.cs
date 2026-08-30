// ---------------------------------------------------------------------------
// StrictHasherTests.cs
// The hasher refuses a property type it does not recognise, and these tests
// exist because the thing it used to do instead looked like it worked.
//
// WHAT THE OLD FALLBACK DID. Convert.ToString on a value whose type reached no
// branch. That never threw and never logged; it returned whatever ToString gave
// back, and for an array or a collection ToString gives back the TYPE NAME.
// Measured, not assumed - the number below is what the old code actually
// produced:
//
//   int[] { 1, 2, 3 }  renders as  "System.Int32[]"
//   int[] { 4, 5, 6 }  renders as  "System.Int32[]"
//
// So two different values hashed to the same 32 bytes. That is not a wasted
// write, which is the cheap failure this hasher tolerates; it is the expensive
// one that ItemHasher's own header names: a hash that stays the same when the
// item DID change. The item is written once, and every run afterwards compares
// two equal hashes, skips it, and reports success. It is wrong in the index for
// ever and nothing anywhere says so.
//
// WHY THIS DOES NOT MOVE HashVersion, which is the judgement these tests are
// really pinning. The version answers one question: would an item that did not
// change still produce the same bytes? It would. No branch was touched, so
// every rendering is byte-for-byte what it was, and the only behaviour that
// moved is that a value reaching no branch now throws rather than returning
// something. A refusal is not a different answer, and no hash on record has
// become wrong. The last test in this file is the evidence: it pins every
// supported rendering against the exact string it must produce, without
// reimplementing the hasher to do it.
//
// The other half of that argument is empirical and lives outside this file:
// grep the shipped connectors for what they put into PushItem.Properties and
// the set is string, double, bool, long and List<string> through AddIfPresent,
// plus string by direct assignment. All five reach a branch. Nothing any
// shipped connector emits could have reached the old fallback, so nothing any
// shipped connector emits can reach the new refusal either - the change is
// invisible to every deployed corpus, which is exactly why forcing one to
// rewrite itself would have been the wrong call.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Microsoft.Graph;
    using Microsoft.Graph.Models.ExternalConnectors;
    using PushCore;
    using PushCore.State;
    using Serilog.Core;
    using SqlTicketsConnector.Tests.TestSupport;
    using Xunit;

    public class StrictHasherTests
    {
        [Fact]
        public void An_array_is_refused_rather_than_hashed_as_its_type_name()
        {
            // THE CASE THE CHANGE EXISTS FOR. Both of these hashed identically
            // before, because both rendered as "System.Int32[]" - so a run that
            // saw the codes change from {1,2,3} to {4,5,6} concluded the item was
            // unchanged and skipped it, on that run and on every run after it.
            Assert.Throws<NotSupportedException>(() => Hash("codes", new[] { 1, 2, 3 }));
            Assert.Throws<NotSupportedException>(() => Hash("codes", new[] { 4, 5, 6 }));
        }

        [Fact]
        public void The_other_collection_types_that_stringify_to_a_type_name_are_refused_too()
        {
            // Same defect, three more shapes of it. A string collection is the
            // one collection that IS supported, and it goes through its own
            // branch rather than here - covered below.
            Assert.Throws<NotSupportedException>(() => Hash("blob", new byte[] { 1, 2, 3 }));
            Assert.Throws<NotSupportedException>(() => Hash("counts", new List<int> { 1, 2 }));
            Assert.Throws<NotSupportedException>(
                () => Hash("map", new Dictionary<string, string> { { "a", "b" } }));
        }

        [Fact]
        public void A_type_that_stringifies_legibly_is_refused_as_well()
        {
            // Legible is not the same as decided. decimal renders "12.34" and
            // looks entirely fine, but nothing here has chosen its stable form:
            // 12.34m and 12.340m are one number and two hashes, so the item
            // rewrites itself whenever the source's scale changes. The whole
            // point of refusing is that somebody adds a branch on purpose and
            // moves HashVersion with it.
            Assert.Throws<NotSupportedException>(() => Hash("contractValue", 12.34m));
            Assert.Throws<NotSupportedException>(() => Hash("correlationId", Guid.NewGuid()));
            Assert.Throws<NotSupportedException>(() => Hash("elapsed", TimeSpan.FromMinutes(90)));
        }

        [Fact]
        public void The_refusal_names_the_property_and_its_type()
        {
            // "Which one" is the operator's next question and the only one this
            // exception can answer cheaply. A schema here carries forty
            // properties; a message that said only "unsupported type" would send
            // somebody to read the mapping code to find out which column.
            NotSupportedException refusal = Assert.Throws<NotSupportedException>(
                () => Hash("contractValue", 12.34m));

            Assert.Contains("contractValue", refusal.Message, StringComparison.Ordinal);
            Assert.Contains("System.Decimal", refusal.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void The_refusal_does_not_quote_the_value()
        {
            // A property value is row content, and row content does not go into
            // an exception any more than it goes into a log - this exception is
            // logged. The name and the type locate the mapping; PushEngine.Prepare
            // adds the row ordinal, which locates the row without quoting it.
            NotSupportedException refusal = Assert.Throws<NotSupportedException>(
                () => Hash("contractValue", 8675309.42m));

            Assert.DoesNotContain("8675309", refusal.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void A_null_property_value_is_refused_by_name_rather_than_hashed_as_empty()
        {
            // It used to hash as an empty string, which is a hash the item never
            // earns: Graph rejects a null property value outright, so the item
            // was hashed here and then refused at the write. The failure has been
            // moved to where the property name is still in hand, and it must be
            // the refusal rather than a NullReferenceException from the code that
            // reports it.
            NotSupportedException refusal = Assert.Throws<NotSupportedException>(
                () => Hash("consultantName", null));

            Assert.Contains("consultantName", refusal.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Every_type_the_shipped_connectors_emit_still_hashes()
        {
            // The other side of a strictness change, and the one that would make
            // it a regression rather than a fix. These are exactly what a grep of
            // the shipped connectors turns up: AddIfPresent's five overloads,
            // plus the three types that reach Properties only by direct
            // assignment. Every one of them has to keep working.
            var item = new PushItem { Id = "te-1", ItemType = "TimeEntry", Content = "text" };

            item.AddIfPresent("customerName", "Contoso Ltd");
            item.AddIfPresent("hours", 7.5d);
            item.AddIfPresent("billable", true);
            item.AddIfPresent("sizeBytes", 4096L);
            item.AddIfPresent("classifications", new[] { "PII", "GDPR" });
            item.Properties["childCount"] = 3;
            item.Properties["workDate"] = new DateTime(2026, 3, 14, 9, 30, 0, DateTimeKind.Unspecified);
            item.Properties["observedAt"] = new DateTimeOffset(2026, 3, 14, 9, 30, 0, TimeSpan.Zero);

            byte[] hash = ItemHasher.HashContent(item.Id, item.ItemType, item.Properties, item.Content);

            Assert.Equal(ItemHasher.HashBytes, hash.Length);
        }

        [Fact]
        public void No_supported_rendering_moved_which_is_why_the_hash_version_did_not()
        {
            // THE EVIDENCE FOR NOT BUMPING HashVersion, and the reason it is
            // stated as an equivalence rather than as a pinned hex constant. A
            // pinned constant would have to be produced by running this hasher,
            // which makes it a test that agrees with whatever the code does. A
            // property holding the double 7.5 must instead hash exactly as a
            // property of the same name holding the string "7.5" - same framing,
            // same length prefixes, and the ONLY thing that can differ is the
            // rendering. That pins the rendering to a literal without
            // reimplementing a byte of the hasher.
            //
            // If any of these ever stops holding, the framing has moved and
            // HashVersion must move with it or every deployed corpus rewrites
            // itself overnight while reporting success.
            AssertRendersAs(7.5d, "7.5");
            AssertRendersAs(1234L, "1234");
            AssertRendersAs(42, "42");
            AssertRendersAs(true, "true");
            AssertRendersAs(false, "false");
            AssertRendersAs(new DateTime(2026, 3, 14, 9, 30, 0, DateTimeKind.Utc), "2026-03-14T09:30:00.0000000Z");
            AssertRendersAs(
                new DateTimeOffset(2026, 3, 14, 9, 30, 0, TimeSpan.Zero),
                "2026-03-14T09:30:00.0000000+00:00");

            // 0x1F is the ASCII unit separator, and it is a literal control
            // character in ItemHasher's source - invisible in an editor and in a
            // diff, which is exactly why it is written as an escape here. The
            // elements are separated rather than concatenated for the same reason
            // fields are length-prefixed: without it {"ab","c"} and {"a","bc"}
            // are one hash.
            AssertRendersAs(new List<string> { "PII", "GDPR" }, "PII\u001FGDPR");
        }

        [Fact]
        public async Task A_refused_property_stops_the_run_and_says_which_row_and_which_property()
        {
            // The refusal only earns its keep if the operator can act on it, and
            // acting means finding the row. PushEngine.Prepare wraps anything
            // thrown while preparing with the row ordinal and the ID of the item
            // before it; this asserts the two halves arrive together - the engine
            // supplies where, the hasher supplies which property and what type.
            var source = new FakePushSource(new[]
            {
                Row("cust1"),
                Row("cust2"),
                Bad("cust3"),
            });

            var adapter = new StubGraphAdapter(
                new ExternalConnection { Id = "consultingwork", State = ConnectionState.Ready },
                new SqlHierarchyPush.HierarchyPushConnector().BuildSchema());

            PushOptions options = TestData.ValidPushOptions("consultingwork");

            var engine = new PushEngine(
                new FakePushConnector(source),
                options,
                new GraphServiceClient(adapter),
                Logger.None,
                dryRun: false);

            var context = new PushSourceContext(
                options,
                new Azure.Identity.DefaultAzureCredential(),
                secrets: null,
                Logger.None);

            InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => engine.RunAsync(context));

            // Where: the third row, and the item before it, which is what turns
            // "somewhere in a million rows" into a lookup.
            Assert.Contains("Row 3", failure.Message, StringComparison.Ordinal);
            Assert.Contains("cust2", failure.Message, StringComparison.Ordinal);

            // Which: the property and its type, from the hasher underneath.
            Assert.IsType<NotSupportedException>(failure.InnerException);
            Assert.Contains("contractValue", failure.InnerException.Message, StringComparison.Ordinal);
            Assert.Contains("System.Decimal", failure.InnerException.Message, StringComparison.Ordinal);
        }

        private static void AssertRendersAs(object value, string expected)
        {
            var typed = new PushItem { Id = "x-1", ItemType = "item", Content = "c" };
            typed.Properties["p"] = value;

            var text = new PushItem { Id = "x-1", ItemType = "item", Content = "c" };
            text.Properties["p"] = expected;

            Assert.Equal(
                ItemHasher.HashContent(text.Id, text.ItemType, text.Properties, text.Content),
                ItemHasher.HashContent(typed.Id, typed.ItemType, typed.Properties, typed.Content));
        }

        private static byte[] Hash(string property, object value)
        {
            var item = new PushItem { Id = "x-1", ItemType = "item", Content = "c" };
            item.Properties[property] = value;

            return ItemHasher.HashContent(item.Id, item.ItemType, item.Properties, item.Content);
        }

        private static PushItem Row(string id)
        {
            var item = new PushItem { Id = id, ItemType = "Customer", Content = "one" };
            item.AddIfPresent("customerName", "Contoso Ltd");
            return item;
        }

        private static PushItem Bad(string id)
        {
            PushItem item = Row(id);
            item.Properties["contractValue"] = 12.34m;
            return item;
        }
    }
}
