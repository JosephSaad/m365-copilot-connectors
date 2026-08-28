// ---------------------------------------------------------------------------
// PushConcurrencyTests.cs
// The gate, and the guarantee it protects.
//
// A source that keeps a watermark must be written one item at a time. That is
// not a preference: the engine commits after each confirmed write, and out-of-
// order completion is exactly what would let a checkpoint pass an item the
// index never received. Serial writing is what makes that free.
//
// So the engine goes concurrent only for a source that declares it keeps no
// position - IPushSource.RequiresOrderedCommit, which defaults to true, so a
// source written without thinking about it keeps the guarantee. These tests
// assert the gate as a FACT rather than inferring it: StubGraphAdapter records
// the greatest number of writes ever in flight at once, so "was it serial" has
// an answer that does not depend on what order things happened to land in.
//
// If someone deletes the gate, or has a watermarked source return false, the
// second test here fails and says why.
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

    public class PushConcurrencyTests
    {
        private const string ConnectionId = "consultingwork";

        [Fact]
        public async Task A_source_that_keeps_no_position_really_is_written_by_more_than_one_writer()
        {
            var source = new FakePushSource(Items(24), requiresOrderedCommit: false);
            (PushEngine engine, StubGraphAdapter adapter) = Engine(writers: 8);
            adapter.WriteDelay = TimeSpan.FromMilliseconds(40);

            PushSummary summary = await engine.PushItemsAsync(source);

            Assert.True(
                adapter.MaxConcurrentWrites > 1,
                $"expected overlapping writes, saw at most {adapter.MaxConcurrentWrites} at once");

            Assert.Equal(24, summary.Total);
            Assert.Equal(24, adapter.WrittenItemIds.Count);
            Assert.True(source.Completed);
        }

        [Fact]
        public async Task A_source_that_keeps_a_position_is_written_one_at_a_time_however_many_writers_are_configured()
        {
            // THE GATE. Eight writers are configured and the source still gets
            // one, because it did not say it was safe to do otherwise.
            var source = new FakePushSource(Items(12), requiresOrderedCommit: true);
            (PushEngine engine, StubGraphAdapter adapter) = Engine(writers: 8);
            adapter.WriteDelay = TimeSpan.FromMilliseconds(20);

            PushSummary summary = await engine.PushItemsAsync(source);

            Assert.Equal(1, adapter.MaxConcurrentWrites);

            // And ordering survives, which is the property the watermark rests on.
            Assert.Equal(Ids(12), adapter.WrittenItemIds.ToArray());
            Assert.Equal(Ids(12), source.Committed.ToArray());
            Assert.Equal(12, summary.Total);
        }

        [Fact]
        public async Task A_dry_run_never_goes_concurrent_and_still_writes_nothing()
        {
            var source = new FakePushSource(Items(10), requiresOrderedCommit: false);
            (PushEngine engine, StubGraphAdapter adapter) = Engine(writers: 8, dryRun: true);

            PushSummary summary = await engine.PushItemsAsync(source);

            Assert.Equal(0, adapter.MaxConcurrentWrites);
            Assert.Empty(adapter.WrittenItemIds);
            Assert.Empty(source.Committed);
            Assert.False(source.Completed);
            Assert.Equal(10, summary.Total);
        }

        [Fact]
        public async Task A_write_that_dies_stops_a_concurrent_run_and_never_reports_it_complete()
        {
            var source = new FakePushSource(Items(40), requiresOrderedCommit: false);
            (PushEngine engine, StubGraphAdapter adapter) = Engine(writers: 4);

            adapter.FailItem = id => id == "a20"
                ? new Microsoft.Graph.Models.ODataErrors.ODataError { ResponseStatusCode = 400 }
                : null;

            // Specifically the ODataError, not the OperationCanceledException it
            // caused in the other three writers. The first failure cancels the
            // rest, so without deliberate selection the run reports "a write was
            // cancelled" and the operator never learns which item Graph refused.
            var error = await Assert.ThrowsAsync<Microsoft.Graph.Models.ODataErrors.ODataError>(
                () => engine.PushItemsAsync(source));

            Assert.Equal(400, error.ResponseStatusCode);

            // The only claim a concurrent run can still make: it did not finish.
            // Which items landed before the failure is deliberately NOT asserted -
            // with several writers in flight that set is not deterministic, and a
            // test that pinned it would be pinning a coincidence.
            Assert.False(source.Completed);
            Assert.DoesNotContain("a20", adapter.WrittenItemIds);
        }

        [Fact]
        public async Task The_writer_count_is_clamped_below_the_concurrency_limit_graph_documents()
        {
            // Graph states an application is limited to 25 concurrent operations
            // on a connection. Asking for 999 must not try to take them all, and
            // must leave room for the polls this run makes on its own.
            var source = new FakePushSource(Items(60), requiresOrderedCommit: false);
            (PushEngine engine, StubGraphAdapter adapter) = Engine(writers: 999);
            adapter.WriteDelay = TimeSpan.FromMilliseconds(30);

            await engine.PushItemsAsync(source);

            Assert.InRange(adapter.MaxConcurrentWrites, 2, 16);
        }

        [Fact]
        public async Task Every_item_is_written_exactly_once_under_concurrency()
        {
            // The counters are interlocked and the duplicate set stays on the
            // reading thread; this is the test that would catch either coming
            // undone.
            var source = new FakePushSource(Items(200), requiresOrderedCommit: false);
            (PushEngine engine, StubGraphAdapter adapter) = Engine(writers: 8);

            PushSummary summary = await engine.PushItemsAsync(source);

            Assert.Equal(200, summary.Total);
            Assert.Equal(200, adapter.WrittenItemIds.Count);
            Assert.Equal(200, adapter.WrittenItemIds.Distinct().Count());
            Assert.Equal(200, source.Committed.Count);
            Assert.Equal(0, summary.Duplicates);
            Assert.Equal(200, summary.Timing.RowTotal.Count);
        }

        [Fact]
        public void The_graph_pipeline_carries_no_retry_handler_of_its_own()
        {
            // The engine is the only component that may retry a write. The SDK's
            // own handler retries 429/503/504 inside PutAsync - three times, three
            // seconds apart, by default - where ThrottleWaits cannot see it and
            // the timing table charges its sleeps to time in flight.
            IList<System.Net.Http.DelegatingHandler> handlers = GraphPipeline.CreateHandlers();

            Assert.DoesNotContain(
                handlers,
                h => h.GetType().Name.Contains("RetryHandler", StringComparison.Ordinal));

            // And the removal has to have removed something real: if the SDK ever
            // renames or moves the handler, matching nothing would look identical
            // to matching it, and the behaviour would come back unnoticed.
            Assert.NotEmpty(handlers);
        }

        private static (PushEngine Engine, StubGraphAdapter Adapter) Engine(
            int writers, bool dryRun = false)
        {
            var adapter = new StubGraphAdapter(
                new ExternalConnection { Id = ConnectionId, State = ConnectionState.Ready },
                new SqlHierarchyPush.HierarchyPushConnector().BuildSchema());

            PushOptions options = TestData.ValidPushOptions(ConnectionId);
            options.Settings["Writers"] = writers.ToString();

            var engine = new PushEngine(
                new SqlHierarchyPush.HierarchyPushConnector(),
                options,
                new Microsoft.Graph.GraphServiceClient(adapter),
                Logger.None,
                dryRun);

            return (engine, adapter);
        }

        private static string[] Ids(int count)
        {
            return Enumerable.Range(1, count).Select(n => "a" + n).ToArray();
        }

        private static IReadOnlyList<PushItem> Items(int count)
        {
            return Ids(count).Select(id =>
            {
                var item = new PushItem { Id = id, ItemType = "file" };
                item.AddIfPresent("title", "Title of " + id);
                return item;
            }).ToList();
        }
    }
}
