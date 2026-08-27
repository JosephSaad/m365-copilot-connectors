// ---------------------------------------------------------------------------
// PushSourceTests.cs
// The source seam, and the rule it exists to make structural.
//
// A failed crawl must never advance the watermark. Before the seam that was a
// convention every connector had to keep; now the engine is the only component
// that can say an item counted, and it says so by calling
// OnItemCommittedAsync AFTER the write returned. These tests drive the real
// engine against a real Graph call path (StubGraphAdapter) and assert on what
// the source was actually told - which is the only thing a watermark can be
// built from.
//
// If someone moves the commit callback above the write, or calls it during a
// dry run, or reports completion after an exception, one of these fails.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using Microsoft.Graph.Models.ExternalConnectors;
    using PushCore;
    using Serilog.Core;
    using SqlTicketsConnector.Tests.TestSupport;
    using Xunit;

    public class PushSourceTests
    {
        private const string ConnectionId = "consultingwork";

        [Fact]
        public async Task An_item_is_reported_committed_only_after_its_write_returns()
        {
            var source = new FakePushSource(Items("a1", "a2", "a3"));
            (PushEngine engine, StubGraphAdapter adapter) = Engine();

            PushSummary summary = await engine.PushItemsAsync(source);

            Assert.Equal(new[] { "a1", "a2", "a3" }, adapter.WrittenItemIds.ToArray());
            Assert.Equal(new[] { "a1", "a2", "a3" }, source.Committed.ToArray());
            Assert.True(source.Completed);
            Assert.Equal(3, summary.Total);
        }

        [Fact]
        public async Task A_write_that_dies_leaves_the_watermark_on_the_last_item_that_landed()
        {
            // The unbreakable rule. Item three's PUT throws; the source must have
            // been told about one and two and nothing else, and must never hear
            // that the crawl completed - so a resume re-reads from two.
            var source = new FakePushSource(Items("a1", "a2", "a3", "a4"));
            (PushEngine engine, StubGraphAdapter adapter) = Engine();

            adapter.FailItem = id => id == "a3"
                ? new Microsoft.Graph.Models.ODataErrors.ODataError { ResponseStatusCode = 400 }
                : null;

            await Assert.ThrowsAsync<Microsoft.Graph.Models.ODataErrors.ODataError>(
                () => engine.PushItemsAsync(source));

            Assert.Equal(new[] { "a1", "a2" }, source.Committed.ToArray());
            Assert.False(source.Completed);

            // And nothing after the failure was written either: the run stops, it
            // does not skip the bad item and carry on.
            Assert.Equal(new[] { "a1", "a2" }, adapter.WrittenItemIds.ToArray());
        }

        [Fact]
        public async Task A_source_that_dies_mid_enumeration_keeps_what_it_had_and_is_not_completed()
        {
            // The other half: the failure is in the source itself - a dropped
            // connection, a filesystem that went away - rather than in the write.
            var source = new FakePushSource(
                Items("a1", "a2", "a3"),
                throwOn: item => item.Id == "a3" ? new TimeoutException("the source went away") : null);

            (PushEngine engine, _) = Engine();

            await Assert.ThrowsAsync<TimeoutException>(() => engine.PushItemsAsync(source));

            Assert.Equal(new[] { "a1", "a2" }, source.Committed.ToArray());
            Assert.False(source.Completed);
        }

        [Fact]
        public async Task A_dry_run_writes_nothing_and_commits_nothing()
        {
            // A dry run that advanced the watermark would make the next real run
            // skip everything it had only pretended to write.
            var source = new FakePushSource(Items("a1", "a2"));
            (PushEngine engine, StubGraphAdapter adapter) = Engine(dryRun: true);

            PushSummary summary = await engine.PushItemsAsync(source);

            Assert.Empty(adapter.WrittenItemIds);
            Assert.Empty(source.Committed);
            Assert.False(source.Completed);
            Assert.Equal(2, summary.Total);
        }

        [Fact]
        public async Task An_item_the_source_could_grant_to_nobody_is_skipped_rather_than_written()
        {
            // Fail closed. An item with an empty ACL is accepted by Graph and then
            // returned to no one, so it looks indexed and is not; the engine
            // refuses it instead, and says which item and why.
            var visible = new PushItem { Id = "a1", ItemType = "file" };
            visible.Acl = new[] { new PushAclEntry(PushAclType.Group, TestData.GroupObjectId) };

            var invisible = new PushItem { Id = "a2", ItemType = "file" };
            invisible.Acl = Array.Empty<PushAclEntry>();

            var source = new FakePushSource(new[] { visible, invisible });
            (PushEngine engine, StubGraphAdapter adapter) = Engine();

            PushSummary summary = await engine.PushItemsAsync(source);

            Assert.Equal(new[] { "a1" }, adapter.WrittenItemIds.ToArray());
            Assert.Equal(new[] { "a1" }, source.Committed.ToArray());
            Assert.Equal(1, summary.Skipped);
            Assert.Equal(1, summary.Total);
        }

        [Fact]
        public async Task An_items_own_grants_are_used_in_place_of_the_connection_wide_acl()
        {
            // A source whose items differ in who may see them - a filesystem -
            // supplies grants per item. The connection-wide ACL is not consulted,
            // and both kinds of principal survive the trip.
            var item = new PushItem { Id = "a1", ItemType = "file" };
            item.Acl = new[]
            {
                new PushAclEntry(PushAclType.Group, "  " + TestData.GroupObjectId + "  "),
                new PushAclEntry(PushAclType.ExternalGroup, "hdfsAnalysts"),

                // A duplicate naming the same principal collapses rather than
                // being sent twice.
                new PushAclEntry(PushAclType.ExternalGroup, "hdfsAnalysts"),
            };

            var source = new FakePushSource(new[] { item });
            (PushEngine engine, StubGraphAdapter adapter) = Engine();

            await engine.PushItemsAsync(source);

            Assert.Single(adapter.WrittenItemIds);

            List<(string Type, string Value, string Access)> acl = LastWrittenAcl(adapter);

            Assert.Equal(2, acl.Count);
            Assert.Equal("group", acl[0].Type);
            Assert.Equal(TestData.GroupObjectId, acl[0].Value);          // trimmed and normalised
            Assert.Equal("externalGroup", acl[1].Type);
            Assert.Equal("hdfsAnalysts", acl[1].Value);

            // Every grant is a grant. The type cannot express a deny, and the
            // engine never invents one.
            Assert.All(acl, entry => Assert.Equal("grant", entry.Access));
        }

        [Fact]
        public async Task An_item_with_no_grants_of_its_own_falls_back_to_the_connection_wide_acl()
        {
            var source = new FakePushSource(Items("a1"));
            (PushEngine engine, StubGraphAdapter adapter) = Engine();

            await engine.PushItemsAsync(source);

            (string Type, string Value, string Access) only = Assert.Single(LastWrittenAcl(adapter));

            Assert.Equal("group", only.Type);
            Assert.Equal(TestData.GroupObjectId, only.Value);
        }

        [Fact]
        public async Task What_the_source_declined_is_counted_so_the_summary_reconciles()
        {
            // "1,000 files on the cluster, 940 items indexed" needs the other 60
            // to appear somewhere, or the next question is whether the run broke.
            var source = new FakePushSource(Items("a1", "a2"), skipped: 58);
            (PushEngine engine, _) = Engine();

            PushSummary summary = await engine.PushItemsAsync(source);

            Assert.Equal(2, summary.Total);
            Assert.Equal(58, summary.Skipped);
        }

        [Fact]
        public async Task A_repeated_item_id_is_counted_and_named_rather_than_silently_overwriting()
        {
            var source = new FakePushSource(Items("a1", "a1"));
            (PushEngine engine, _) = Engine();

            PushSummary summary = await engine.PushItemsAsync(source);

            Assert.Equal(1, summary.Duplicates);
            Assert.Equal(2, summary.Total);
        }

        [Fact]
        public async Task A_multi_value_property_reaches_graph_as_an_annotated_collection()
        {
            // Graph rejects a collection sent without its
            // "name@odata.type": "Collection(String)" sibling as a type mismatch
            // against the registered StringCollection property, and the message
            // names the item rather than the annotation. The engine adds it, so
            // a connector adds a list and is finished - there is no second thing
            // to remember, and no way to remember it in one connector and forget
            // it in the next.
            var item = new PushItem { Id = "collection1", ItemType = "file" };
            item.AddIfPresent("title", "Tagged");
            item.AddIfPresent("tags", new[] { "PII", "GDPR" });

            // An empty or all-blank collection is not written at all, the way an
            // empty string is not.
            item.AddIfPresent("empty", Array.Empty<string>());
            item.AddIfPresent("blank", new[] { " ", string.Empty });

            Assert.False(item.Properties.ContainsKey("empty"));
            Assert.False(item.Properties.ContainsKey("blank"));

            (PushEngine engine, StubGraphAdapter adapter) = Engine();

            await engine.PushItemsAsync(new FakePushSource(new[] { item }));

            using var document = System.Text.Json.JsonDocument.Parse(adapter.WrittenBodies[^1]);

            System.Text.Json.JsonElement properties = document.RootElement.GetProperty("properties");

            Assert.Equal(
                new[] { "PII", "GDPR" },
                properties.GetProperty("tags").EnumerateArray().Select(v => v.GetString()).ToArray());

            Assert.Equal("Collection(String)", properties.GetProperty("tags@odata.type").GetString());

            // A single-value property is untouched by the annotation pass.
            Assert.Equal("Tagged", properties.GetProperty("title").GetString());
            Assert.False(properties.TryGetProperty("title@odata.type", out _));
        }

        private static IReadOnlyList<PushItem> Items(params string[] ids)
        {
            return ids.Select(id =>
            {
                var item = new PushItem { Id = id, ItemType = "file" };
                item.AddIfPresent("title", "Title of " + id);
                return item;
            }).ToList();
        }

        private static (PushEngine Engine, StubGraphAdapter Adapter) Engine(bool dryRun = false)
        {
            var adapter = new StubGraphAdapter(
                new ExternalConnection { Id = ConnectionId, State = ConnectionState.Ready },
                new SqlHierarchyPush.HierarchyPushConnector().BuildSchema());

            var engine = new PushEngine(
                new SqlHierarchyPush.HierarchyPushConnector(),
                TestData.ValidPushOptions(ConnectionId),
                new Microsoft.Graph.GraphServiceClient(adapter),
                Logger.None,
                dryRun);

            return (engine, adapter);
        }

        /// <summary>Reads the acl array out of the JSON the engine actually sent.</summary>
        private static List<(string Type, string Value, string Access)> LastWrittenAcl(StubGraphAdapter adapter)
        {
            using var document = System.Text.Json.JsonDocument.Parse(adapter.WrittenBodies[^1]);

            return document.RootElement.GetProperty("acl").EnumerateArray()
                .Select(entry => (
                    Type: entry.GetProperty("type").GetString(),
                    Value: entry.GetProperty("value").GetString(),
                    Access: entry.GetProperty("accessType").GetString()))
                .ToList();
        }
    }
}
