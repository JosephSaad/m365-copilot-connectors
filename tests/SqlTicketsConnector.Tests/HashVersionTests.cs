// ---------------------------------------------------------------------------
// HashVersionTests.cs
// The hash framing carries a version, and a change to it escalates the run.
//
// WHAT THIS PREVENTS. Change how an item is framed for hashing - a new field, a
// different separator, another normalisation rule - and every stored hash stops
// matching at once. The next run then rewrites the entire corpus and reports
// complete success: no error, no bad item, just a night of Graph write quota
// spent on data that did not change. There is nothing to find afterwards, which
// is what makes it expensive to diagnose and easy to repeat.
//
// The version turns that into a migration: announced, escalated on purpose, and
// visible in the log as the thing it is.
//
// WHAT THESE TESTS DO NOT COVER. Whether crawl.uspCheckHashVersion advances the
// stored version, and whether it therefore reports a change exactly once. That
// is in sql/28 and needs a server; the verification block at the foot of that
// file is the check, and the CI state-database job is where it runs. What is
// testable here is the engine's half of the contract: that it asks, that it
// reports this build's version, and that it acts on the answer.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Tests
{
    using System.Threading.Tasks;
    using Microsoft.Graph;
    using Microsoft.Graph.Models.ExternalConnectors;
    using PushCore;
    using PushCore.State;
    using Serilog.Core;
    using SqlTicketsConnector.Tests.TestSupport;
    using Xunit;

    public class HashVersionTests
    {
        private const string ConnectionId = "consultingwork";

        [Fact]
        public async Task An_unchanged_hash_version_leaves_an_incremental_run_incremental()
        {
            // The control. Without it the escalation test proves only that the
            // engine runs full crawls, which it would do anyway if the setting
            // were being ignored.
            var store = new RecordingCrawlStateStore { HashVersionChanged = false };

            await RunAsync(store, incremental: true);

            Assert.True(store.HashVersionChecked);
            Assert.Equal(CrawlMode.Incremental, store.RequestedMode);
        }

        [Fact]
        public async Task A_changed_hash_version_escalates_an_incremental_run_to_full()
        {
            // Every stored hash was computed by the previous framing and none of
            // them will match. An incremental run would therefore rewrite the
            // whole corpus while calling itself incremental - the same cost as a
            // full crawl with none of the explanation, and a run mode that lies
            // to anyone reading the history afterwards.
            var store = new RecordingCrawlStateStore { HashVersionChanged = true };

            await RunAsync(store, incremental: true);

            Assert.Equal(CrawlMode.Full, store.RequestedMode);
        }

        [Fact]
        public async Task The_version_reported_is_the_one_this_build_hashes_with()
        {
            // Reporting a constant that is not the hasher's own would be worse
            // than not checking: the store would record a version the hashes
            // were never computed with, and the mismatch it exists to catch
            // would be silently reconciled.
            var store = new RecordingCrawlStateStore();

            await RunAsync(store, incremental: false);

            Assert.Equal(ItemHasher.HashVersion, store.CheckedHashVersion);
        }

        private static async Task RunAsync(RecordingCrawlStateStore store, bool incremental)
        {
            var adapter = new StubGraphAdapter(
                new ExternalConnection { Id = ConnectionId, State = ConnectionState.Ready },
                new SqlHierarchyPush.HierarchyPushConnector().BuildSchema());

            PushOptions options = TestData.ValidPushOptions(ConnectionId);
            options.Settings["Incremental"] = incremental ? "true" : "false";

            var source = new FakePushSource(new[]
            {
                new PushItem { Id = "cust1", ItemType = "Customer", Content = "one" },
            });

            // RunAsync, not PushItemsAsync. The version check happens as the run
            // is opened, which is a step PushItemsAsync never reaches - it is
            // the item loop, and the loop is not where this decision is made.
            var engine = new PushEngine(
                new FakePushConnector(source),
                options,
                new GraphServiceClient(adapter),
                Logger.None,
                dryRun: false,
                store);

            // The credential and secret provider are never touched: the fake
            // connector opens nothing, and the run never authenticates to a
            // source. They are here because the context requires them.
            var context = new PushSourceContext(
                options,
                new Azure.Identity.DefaultAzureCredential(),
                secrets: null,
                Logger.None);

            await engine.RunAsync(context);
        }
    }
}
