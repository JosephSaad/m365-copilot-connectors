// ---------------------------------------------------------------------------
// BatchedDeleteTests.cs
// The delete sweep goes through $batch, and 404 still means success.
//
// WHY THIS EXISTS. The sweep was the last caller paying a round trip per item.
// Writes were batched; deletions were not, so removing 412 items cost 412 calls
// against a writer that was already sitting there able to do it in 21.
//
// The two things worth pinning are the two ways a delete differs from a write,
// because they are the only places the shared code branches:
//
//   1. It issues a DELETE, not a PUT. If that branch were wrong the sweep would
//      quietly REWRITE every item it was asked to remove - which is the worst
//      possible failure here, and one a round-trip count alone would not notice.
//
//   2. 404 counts as SUCCESS. An item Graph says is absent is absent, and the
//      state store must be told so. Treating it as a failure leaves the row
//      pending for ever, retried on every run against something already gone.
//      The single-item TryDeleteAsync has always done this; the batched path
//      has to agree, or one item gets two policies depending on which removed
//      it.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Tests
{
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using Microsoft.Graph.Models.ExternalConnectors;
    using PushCore;
    using Serilog.Core;
    using SqlTicketsConnector.Tests.TestSupport;
    using Xunit;

    public class BatchedDeleteTests
    {
        private const string ConnectionId = "batchdelete";

        [Fact]
        public async Task Twenty_deletions_cost_one_round_trip()
        {
            (GraphBatchWriter writer, StubGraphAdapter adapter, PushSummary summary) = Writer();

            BatchWriteResult result = await writer.DeleteAsync(Ids(20));

            Assert.Equal(20, result.WrittenCount);
            Assert.Equal(0, result.FailedCount);

            // The number this change exists for. Twenty deletions used to be
            // twenty calls.
            Assert.Equal(1, result.RoundTrips);
            Assert.Equal(new[] { 20 }, adapter.BatchSizes.ToArray());
            Assert.Equal(1, adapter.BatchRoundTrips);

            // THE ASSERTION THAT ACTUALLY MATTERS. Everything above is equally
            // true of a batch that PUT twenty items instead of deleting them -
            // it would report twenty successes, one round trip, and would have
            // rewritten every item the sweep was told to remove.
            Assert.Equal(20, adapter.BatchMethods.Count);
            Assert.All(adapter.BatchMethods, method => Assert.Equal("DELETE", method));

            // And no bodies went out. A DELETE has none.
            Assert.All(adapter.WrittenBodies, body => Assert.Equal(string.Empty, body));

            _ = summary;
        }

        [Fact]
        public async Task Forty_one_deletions_fill_three_envelopes()
        {
            // Graph caps a $batch at twenty, so the split is 20 + 20 + 1 rather
            // than one oversized envelope the service would refuse.
            (GraphBatchWriter writer, StubGraphAdapter adapter, _) = Writer();

            BatchWriteResult result = await writer.DeleteAsync(Ids(41));

            Assert.Equal(41, result.WrittenCount);
            Assert.Equal(3, result.RoundTrips);
            Assert.Equal(new[] { 20, 20, 1 }, adapter.BatchSizes.ToArray());
        }

        [Fact]
        public async Task A_deletion_that_answers_404_counts_as_deleted()
        {
            // The item is already gone. That is the state being asked for, so it
            // must be confirmed to the store rather than left pending for ever.
            (GraphBatchWriter writer, StubGraphAdapter adapter, _) = Writer();

            adapter.BatchStatusFor = id => id == "a3" ? 404 : (int?)null;

            BatchWriteResult result = await writer.DeleteAsync(Ids(5));

            Assert.Equal(5, result.WrittenCount);
            Assert.Equal(0, result.FailedCount);
            Assert.Contains("a3", result.Written.Select(item => item.ItemId));
        }

        [Fact]
        public async Task A_deletion_refused_terminally_is_reported_and_the_rest_still_go()
        {
            // One item that will not delete must not abandon the sweep. The store
            // keeps it pending and the next run retries it.
            (GraphBatchWriter writer, _, _) = Writer();

            BatchWriteResult result = await writer.DeleteAsync(Ids(5));

            Assert.Equal(5, result.WrittenCount);

            (GraphBatchWriter refusing, StubGraphAdapter adapter, _) = Writer();
            adapter.BatchStatusFor = id => id == "a2" ? 403 : (int?)null;

            BatchWriteResult refused = await refusing.DeleteAsync(Ids(5));

            Assert.Equal(4, refused.WrittenCount);
            Assert.Equal(1, refused.FailedCount);
            Assert.Equal("a2", Assert.Single(refused.Failed).ItemId);
        }

        [Fact]
        public async Task A_throttled_deletion_is_retried_rather_than_lost()
        {
            // Same passes and the same backoff a write gets, because it is the
            // same code. A 429 on a delete must not silently drop the item.
            (GraphBatchWriter writer, StubGraphAdapter adapter, _) = Writer();

            var throttled = new HashSet<string>(System.StringComparer.Ordinal);
            adapter.BatchStatusFor = id => id == "a1" && throttled.Add(id) ? 429 : (int?)null;

            BatchWriteResult result = await writer.DeleteAsync(Ids(5));

            Assert.Contains("a1", throttled);
            Assert.Equal(5, result.WrittenCount);
            Assert.Equal(0, result.FailedCount);

            // Two envelopes: the first pass, then the retry carrying only a1.
            Assert.Equal(new[] { 5, 1 }, adapter.BatchSizes.ToArray());
        }

        private static (GraphBatchWriter Writer, StubGraphAdapter Adapter, PushSummary Summary) Writer()
        {
            var adapter = new StubGraphAdapter(
                new ExternalConnection { Id = ConnectionId, State = ConnectionState.Ready },
                new SqlHierarchyPush.HierarchyPushConnector().BuildSchema());

            var graph = new Microsoft.Graph.GraphServiceClient(adapter);
            var summary = new PushSummary();

            return (new GraphBatchWriter(graph, ConnectionId, summary, Logger.None), adapter, summary);
        }

        private static IReadOnlyList<string> Ids(int count) =>
            Enumerable.Range(1, count).Select(n => "a" + n).ToList();
    }
}
