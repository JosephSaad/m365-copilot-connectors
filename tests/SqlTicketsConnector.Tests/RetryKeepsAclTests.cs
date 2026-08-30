// ---------------------------------------------------------------------------
// RetryKeepsAclTests.cs
// A retried write must still carry its ACL — through BOTH write paths.
//
// WHAT THIS PINS. Graph SDK models are backed models: the store marks values
// clean once serialized, so serializing the same instance again emits only what
// changed since — nothing. Graph answers 400 NullOrEmptyValue, "'Acl' is null or
// empty", and refuses the item terminally. A retry is exactly that second
// serialization.
//
// WHY THERE ARE TWO TESTS AND NOT ONE. The engine has two write paths and the
// defect was fixed in one of them. GraphBatchWriter re-serializes into a fresh
// $batch body; PushEngine.WriteWithRetryAsync re-issues the same PUT, and a
// chunk of one always goes there. The first was found in production — 191 items
// took a 429 and all 191 came back 400 — and fixed. The second was never
// touched, held the identical defect, and had nothing asserting otherwise. Both
// now call GraphModelReset.ForSerialization, and both are tested here so that
// fixing one and not the other cannot happen twice.
//
// These assert on WrittenBodies, which is the JSON read off the wire rather than
// the object the engine built. That distinction is the whole point: the item in
// memory always had its ACL. What Graph received is the thing in question.
//
// BatchRetrySerializationTests covers the same defect one level down, on the
// serializer itself. This file covers it through the engine, which is where it
// actually happened.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Threading.Tasks;
    using Microsoft.Graph.Models.ExternalConnectors;
    using Microsoft.Graph.Models.ODataErrors;
    using PushCore;
    using Serilog.Core;
    using SqlTicketsConnector.Tests.TestSupport;
    using Xunit;

    public class RetryKeepsAclTests
    {
        private const string ConnectionId = "retryacl";

        [Fact]
        public async Task A_retried_single_item_put_still_carries_its_acl()
        {
            (PushEngine engine, StubGraphAdapter adapter) = Engine(batch: false);

            // Throws once, then lets the retry through. FailItem is invoked
            // before the write is recorded, so WrittenBodies holds the RETRY's
            // body and nothing else — which is the body that used to arrive with
            // no grants on it.
            int attempts = 0;
            adapter.FailItem = _ => ++attempts == 1 ? Throttled() : null;

            PushSummary summary = await engine.PushItemsAsync(new FakePushSource(Items(1)));

            Assert.Equal(2, attempts);
            Assert.Equal(1, summary.Total);

            string retryBody = Assert.Single(adapter.WrittenBodies);
            Assert.Contains("\"acl\"", retryBody, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(TestData.GroupObjectId, retryBody, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task A_retried_batched_item_still_carries_its_acl()
        {
            (PushEngine engine, StubGraphAdapter adapter) = Engine(batch: true);

            // THREE items, not one: a chunk of one never reaches the batch
            // writer at all - the engine sends it down the single-item path -
            // so a one-item fixture here would pass without testing anything.
            //
            // A 429 on the sub-response, not on the envelope: Graph answers 200
            // on the batch and puts the refusal inside it. a1 is throttled once
            // and retried from the same instance, which is the second
            // serialization this test exists for.
            var throttled = new HashSet<string>(StringComparer.Ordinal);
            adapter.BatchStatusFor = id => id == "a1" && throttled.Add(id) ? 429 : (int?)null;

            PushSummary summary = await engine.PushItemsAsync(new FakePushSource(Items(3)));

            Assert.Contains("a1", throttled);
            Assert.Equal(3, summary.Total);
            Assert.Equal(0, summary.Failed);

            // A refused sub-response records no body, so these three are a2 and
            // a3 from the first attempt plus a1 from the RETRY - which is the
            // one that used to arrive with no grants on it.
            Assert.Equal(3, adapter.WrittenBodies.Count);

            string retried = Assert.Single(
                adapter.WrittenBodies,
                b => b.Contains("\"a1\"", StringComparison.Ordinal));

            Assert.Contains("\"acl\"", retried, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(TestData.GroupObjectId, retried, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// A 429 carrying Retry-After: 1. The header is not decoration — without
        /// it GraphThrottling falls back to Backoff(1), and this test would pay
        /// four real seconds to prove something about serialization.
        /// </summary>
        private static ODataError Throttled()
        {
            return new ODataError
            {
                ResponseStatusCode = 429,
                ResponseHeaders = new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { "Retry-After", new[] { "1" } },
                },
            };
        }

        private static (PushEngine Engine, StubGraphAdapter Adapter) Engine(bool batch)
        {
            var adapter = new StubGraphAdapter(
                new ExternalConnection { Id = ConnectionId, State = ConnectionState.Ready },
                new SqlHierarchyPush.HierarchyPushConnector().BuildSchema());

            PushOptions options = TestData.ValidPushOptions(ConnectionId);
            options.Settings["Batch"] = batch ? "true" : "false";
            options.Settings["Writers"] = 1.ToString(CultureInfo.InvariantCulture);

            var engine = new PushEngine(
                new SqlHierarchyPush.HierarchyPushConnector(),
                options,
                new Microsoft.Graph.GraphServiceClient(adapter),
                Logger.None,
                dryRun: false);

            return (engine, adapter);
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
    }
}
