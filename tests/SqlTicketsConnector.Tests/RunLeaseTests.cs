// ---------------------------------------------------------------------------
// RunLeaseTests.cs
// A running crawl keeps its lease alive, and stops the moment it ends.
//
// WHAT THE LEASE IS FOR. sql/43 refuses a second crawl of a connection while a
// first is alive, because two runs mean two DELETE SWEEPS: each diffs the corpus
// against what IT has seen, so the second offers every item the first has not
// reached yet for deletion. MaxDeletePercent is the only thing between that and
// an emptied index, and a guard is a backstop rather than a design.
//
// The database decides who holds the lease by looking at a heartbeat. That makes
// the connector's half - beating while it runs, and stopping when it does not -
// load-bearing in both directions:
//
//   A crawl that STOPS beating hands its lease away mid-flight and invites the
//   second process it was meant to exclude. That is the failure the lease
//   exists to prevent, caused by the mechanism meant to prevent it.
//
//   A beat that OUTLIVES its run holds the lease against the run's own
//   replacement, turning a finished crawl into a lockout.
//
// Both are tested here. The SQL half - refusal, expiry, reaping - is verified by
// sql/43's own block, which opens a real run, proves a second is refused with
// 50043, ages the heartbeat and proves the lease frees.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Graph.Models.ExternalConnectors;
    using PushCore;
    using PushCore.State;
    using Serilog.Core;
    using SqlTicketsConnector.Tests.TestSupport;
    using Xunit;

    public class RunLeaseTests
    {
        private const string ConnectionId = "runlease";

        [Fact]
        public async Task A_running_crawl_beats_while_it_runs()
        {
            // A one-second beat against a source slow enough to span several.
            // Asserting "more than one" rather than an exact count on purpose:
            // the timer is wall-clock and a machine under load will drop one,
            // which is precisely what sql/43's three-beat grace exists to absorb.
            // A test that demanded an exact number would fail for the reason the
            // design already tolerates.
            var store = new BeatCountingStore();

            await RunAsync(store, new SlowPushSource(Items(6), TimeSpan.FromMilliseconds(700)), heartbeatSeconds: 1);

            Assert.True(
                store.Beats >= 2,
                $"expected the crawl to beat more than once, saw {store.Beats}");
        }

        [Fact]
        public async Task Beating_stops_when_the_run_does()
        {
            // A beat arriving after the run has closed holds the lease against
            // the run's own replacement. uspHeartbeatRun guards this too - it
            // only touches rows still at status 1 - but the connector must not
            // rely on that, because the guard is in the other half of the system.
            var store = new BeatCountingStore();

            await RunAsync(store, new SlowPushSource(Items(3), TimeSpan.FromMilliseconds(400)), heartbeatSeconds: 1);

            int atEnd = store.Beats;
            await Task.Delay(TimeSpan.FromSeconds(2.5));

            Assert.Equal(atEnd, store.Beats);
        }

        [Fact]
        public async Task A_heartbeat_that_throws_does_not_fail_the_crawl()
        {
            // The grace period is three beats wide precisely so a missed beat is
            // survivable. Killing an otherwise healthy hour-long crawl because a
            // keepalive could not reach the database would be the cure causing
            // the disease.
            var store = new BeatCountingStore { ThrowOnBeat = true };

            StubGraphAdapter adapter = await RunAsync(
                store, new SlowPushSource(Items(4), TimeSpan.FromMilliseconds(600)), heartbeatSeconds: 1);

            // The crawl completed despite every beat failing.
            Assert.Equal(4, adapter.WrittenItemIds.Count);
            Assert.True(store.Attempts >= 2, $"expected repeated attempts, saw {store.Attempts}");
            Assert.Equal(0, store.Beats);
        }

        [Fact]
        public async Task A_disabled_store_is_never_asked_to_beat()
        {
            // No store means no lease to keep, and a beat against
            // NullCrawlStateStore would be a timer burning for nothing.
            var store = new BeatCountingStore { Enabled = false };

            await RunAsync(store, new SlowPushSource(Items(3), TimeSpan.FromMilliseconds(500)), heartbeatSeconds: 1);

            Assert.Equal(0, store.Attempts);
        }

        /// <summary>Runs a whole crawl the way the host does, and reports what beat.</summary>
        /// <remarks>
        /// RunAsync, not PushItemsAsync, and that distinction is the reason two
        /// of these tests failed on their first outing. The heartbeat is started
        /// where the RUN is opened, because that is what has a lease to keep;
        /// PushItemsAsync is the item loop beneath it and never opens one. A test
        /// driving the loop directly measured a heartbeat that was correctly not
        /// running, and reported it as a defect in the code rather than in the
        /// test.
        /// </remarks>
        private static async Task<StubGraphAdapter> RunAsync(
            ICrawlStateStore store, IPushSource source, int heartbeatSeconds)
        {
            var adapter = new StubGraphAdapter(
                new ExternalConnection { Id = ConnectionId, State = ConnectionState.Ready },
                new SqlHierarchyPush.HierarchyPushConnector().BuildSchema());

            PushOptions options = TestData.ValidPushOptions(ConnectionId);
            options.Settings["Batch"] = "false";
            options.Settings["Writers"] = "1";
            options.Settings["HeartbeatSeconds"] =
                heartbeatSeconds.ToString(CultureInfo.InvariantCulture);

            var engine = new PushEngine(
                new FakePushConnector(source),
                options,
                new Microsoft.Graph.GraphServiceClient(adapter),
                Logger.None,
                dryRun: false,
                store);

            // The credential and secret provider are never touched: the fake
            // connector opens nothing. They are here because the context requires
            // them.
            var context = new PushSourceContext(
                options,
                new Azure.Identity.DefaultAzureCredential(),
                secrets: null,
                Logger.None);

            await engine.RunAsync(context);

            return adapter;
        }

        private static IReadOnlyList<PushItem> Items(int count)
        {
            return Enumerable.Range(1, count).Select(n =>
            {
                var item = new PushItem { Id = "a" + n, ItemType = "file" };
                item.AddIfPresent("title", "Title of a" + n);
                return item;
            }).ToList();
        }

        /// <summary>A source that takes its time, so a beat has room to fire.</summary>
        /// <remarks>
        /// The delay is per row and on the READ, which is where a real slow
        /// source spends its time. Driving the clock with the source rather than
        /// with a sleep in the test keeps the engine's own loop in the picture:
        /// a heartbeat that only fired between runs would pass a test that slept.
        /// </remarks>
        private sealed class SlowPushSource : IPushSource
        {
            private readonly IReadOnlyList<PushItem> items;
            private readonly TimeSpan perRow;

            public SlowPushSource(IReadOnlyList<PushItem> items, TimeSpan perRow)
            {
                this.items = items;
                this.perRow = perRow;
            }

            public bool RequiresOrderedCommit => false;

            public int Skipped => 0;

            public async IAsyncEnumerable<PushItem> ReadAsync(
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                foreach (PushItem item in this.items)
                {
                    await Task.Delay(this.perRow, cancellationToken);
                    yield return item;
                }
            }

            public ValueTask OnItemCommittedAsync(PushItem item, CancellationToken cancellationToken) =>
                ValueTask.CompletedTask;

            public ValueTask OnCrawlCompletedAsync(CancellationToken cancellationToken) =>
                ValueTask.CompletedTask;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        /// <summary>Counts beats, and can refuse them.</summary>
        private sealed class BeatCountingStore : ICrawlStateStore
        {
            private readonly ICrawlStateStore inner = NullCrawlStateStore.Instance;
            private int beats;
            private int attempts;

            public bool Enabled { get; set; } = true;

            public bool ThrowOnBeat { get; set; }

            public int Beats => Volatile.Read(ref this.beats);

            public int Attempts => Volatile.Read(ref this.attempts);

            public bool IsEnabled => this.Enabled;

            public Task HeartbeatAsync(CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref this.attempts);

                if (this.ThrowOnBeat)
                {
                    throw new InvalidOperationException("the state database is not answering");
                }

                Interlocked.Increment(ref this.beats);
                return Task.CompletedTask;
            }

            public Task<CrawlRunStart> BeginRunAsync(
                CrawlConnectionInfo connection, CrawlMode requested, bool dryRun,
                int fullEveryHours, CancellationToken cancellationToken) =>
                this.inner.BeginRunAsync(connection, requested, dryRun, fullEveryHours, cancellationToken);

            public Task<bool> CheckHashVersionAsync(
                string connectionId, int hashVersion, CancellationToken cancellationToken) =>
                this.inner.CheckHashVersionAsync(connectionId, hashVersion, cancellationToken);

            public Task<IReadOnlyDictionary<string, CrawlItemState>> GetItemStatesAsync(
                IReadOnlyCollection<string> itemIds, CancellationToken cancellationToken) =>
                this.inner.GetItemStatesAsync(itemIds, cancellationToken);

            public Task<IReadOnlySet<string>> CompareAndSeeAsync(
                IReadOnlyCollection<CrawlItemState> candidates, CancellationToken cancellationToken) =>
                this.inner.CompareAndSeeAsync(candidates, cancellationToken);

            public Task RecordWrittenAsync(
                IReadOnlyCollection<CrawlItemState> items, CancellationToken cancellationToken) =>
                this.inner.RecordWrittenAsync(items, cancellationToken);

            public Task RecordUnchangedAsync(
                IReadOnlyCollection<string> itemIds, CancellationToken cancellationToken) =>
                this.inner.RecordUnchangedAsync(itemIds, cancellationToken);

            public Task<IReadOnlyList<CrawlDeletion>> GetPendingDeletesAsync(
                double maxDeletePercent, bool overrideGuard, CancellationToken cancellationToken) =>
                this.inner.GetPendingDeletesAsync(maxDeletePercent, overrideGuard, cancellationToken);

            public Task<IReadOnlyList<string>> GetLiveItemIdsAsync(CancellationToken cancellationToken) =>
                this.inner.GetLiveItemIdsAsync(cancellationToken);

            public Task ConfirmDeletesAsync(
                IReadOnlyCollection<string> itemIds, CancellationToken cancellationToken) =>
                this.inner.ConfirmDeletesAsync(itemIds, cancellationToken);

            public Task<CrawlMarker?> GetCheckpointAsync(CancellationToken cancellationToken) =>
                this.inner.GetCheckpointAsync(cancellationToken);

            public Task SaveCheckpointAsync(CrawlMarker marker, CancellationToken cancellationToken) =>
                this.inner.SaveCheckpointAsync(marker, cancellationToken);

            public Task<IReadOnlyDictionary<string, PrincipalGrant>> ResolvePrincipalsAsync(
                string sourceType, IReadOnlyCollection<string> sourceKeys, CancellationToken cancellationToken) =>
                this.inner.ResolvePrincipalsAsync(sourceType, sourceKeys, cancellationToken);

            public Task CachePrincipalAsync(
                PrincipalGrant grant, string sourceType, TimeSpan? ttl, CancellationToken cancellationToken) =>
                this.inner.CachePrincipalAsync(grant, sourceType, ttl, cancellationToken);

            public void RecordThrottle(ThrottleEvent throttle) => this.inner.RecordThrottle(throttle);

            public Task CompleteRunAsync(
                RunTotals totals, IReadOnlyCollection<ItemTypeTotals> byType,
                PushTiming timing, CancellationToken cancellationToken) =>
                this.inner.CompleteRunAsync(totals, byType, timing, cancellationToken);

            public ValueTask DisposeAsync() => this.inner.DisposeAsync();

            public Task FailRunAsync(
                string errorKind, string errorMessage, RunTotals totals,
                IReadOnlyCollection<ItemTypeTotals> byType, PushTiming timing,
                CancellationToken cancellationToken) =>
                this.inner.FailRunAsync(errorKind, errorMessage, totals, byType, timing, cancellationToken);
        }
    }
}
