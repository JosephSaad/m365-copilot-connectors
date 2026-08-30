// ---------------------------------------------------------------------------
// PushBatchingTests.cs
// The path every item now takes, and the one contract it does not share with
// the path it replaced.
//
// Settings:Batch defaults to true, so $batch is how every row reaches the index
// unless an operator turned it off. That makes this the most-travelled code in
// the engine and, until this file existed, the least covered: the fixtures pin
// Batch=false because every test written before batching asserts the
// single-item contract, and a $batch POST does not even reach the same adapter
// method a PUT does. The batch path was therefore reachable only against a live
// tenant.
//
// THE CONTRACT HERE IS GENUINELY DIFFERENT AND THAT IS THE WHOLE POINT. A batch
// comes back HTTP 200 while individual sub-responses carry a 429 or a terminal
// 4xx, so one refused item is reported rather than thrown: nineteen items that
// landed must not be discarded to report the one that did not. PushSummary.Failed
// is the count that only this path can produce.
//
// WHAT BREAKS IF THIS IS WRONG. An engine that read the envelope's 200 and moved
// on would report twenty writes having made none, and would then record a
// content hash for twenty rows that are not in the index - after which the next
// incremental run sees a matching hash, skips the row, and the gap never closes.
// The commit-prefix test is the one that guards the other half of that: a
// watermark that stepped over a refused item would never revisit it either.
//
// TWO DEFECTS IN PushEngine.FlushChunkAsync WERE OPEN WHEN THIS FILE WAS
// WRITTEN AND ARE NOW FIXED, in 7fc7135. The history is worth keeping, because
// both were found by these tests and neither would have surfaced from reading
// the engine:
//
//   1. THE MARKER STEPPED OVER A GAP, one chunk later. The prefix stopped at the
//      refusal inside its own chunk, but the run did not stop, and the next chunk
//      committed in full and saved its checkpoint - so a run of forty with item 5
//      refused ended with the marker past the gap and no incremental run ever
//      returning for a5. Once_a_run_has_left_a_gap_the_marker_never_moves_again
//      now asserts the fix: the marker freezes for the rest of the run.
//
//   2. THE RUN UNDER-REPORTED WHAT IT WROTE, counting over the commit prefix
//      rather than over what landed, so a chunk of twenty with item 5 refused
//      reported four written for nineteen items genuinely in the index. The
//      concurrency test at the foot of this file is where that is now pinned
//      hardest: eighty items, two refused in different chunks, and the summary
//      has to reconcile to exactly 78 written and 2 failed across eight writers
//      that never see each other.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Threading.Tasks;
    using Microsoft.Graph.Models.ExternalConnectors;
    using PushCore;
    using PushCore.State;
    using Serilog.Core;
    using SqlTicketsConnector.Tests.TestSupport;
    using Xunit;

    public class PushBatchingTests
    {
        private const string ConnectionId = "consultingwork";

        [Fact]
        public async Task A_batched_run_writes_every_item_once_and_reports_what_the_single_item_path_reports()
        {
            // EQUIVALENCE. Batching is a latency optimisation and nothing else,
            // so the two paths must agree on every number an operator reconciles
            // the run against. Anything that differs here is a behaviour change
            // shipped as a performance change, which is the worst way to ship one.
            var batched = new FakePushSource(Items(24));
            (PushEngine batchEngine, StubGraphAdapter batchAdapter) = Engine(batch: true);

            var single = new FakePushSource(Items(24));
            (PushEngine singleEngine, StubGraphAdapter singleAdapter) = Engine(batch: false);

            PushSummary batchSummary = await batchEngine.PushItemsAsync(batched);
            PushSummary singleSummary = await singleEngine.PushItemsAsync(single);

            Assert.Equal(singleSummary.Total, batchSummary.Total);
            Assert.Equal(singleSummary.Failed, batchSummary.Failed);
            Assert.Equal(singleSummary.Unchanged, batchSummary.Unchanged);
            Assert.Equal(singleSummary.Duplicates, batchSummary.Duplicates);
            Assert.Equal(singleSummary.BytesWritten, batchSummary.BytesWritten);

            // Exactly once, and in the source's order. A batch that dropped a
            // sub-request, or replayed a whole batch to retry one item in it,
            // would show up here and nowhere else.
            Assert.Equal(Ids(24), batchAdapter.WrittenItemIds.ToArray());
            Assert.Equal(singleAdapter.WrittenItemIds, batchAdapter.WrittenItemIds);
            Assert.Equal(24, batchAdapter.WrittenItemIds.Distinct().Count());

            Assert.Equal(Ids(24), batched.Committed.ToArray());
            Assert.Equal(single.Committed, batched.Committed);
            Assert.True(batched.Completed);

            // The body is the one thing that MUST be identical rather than
            // merely equivalent: the ACL rides in it, and a batched write that
            // serialized differently from a single write would be a different
            // item in the index for the same row.
            Assert.Equal(singleAdapter.WrittenBodies, batchAdapter.WrittenBodies);

            // And the run really did take the path it was asked to take.
            Assert.True(batchSummary.Batches > 0);
            Assert.Equal(0, singleSummary.Batches);
        }

        [Fact]
        public async Task More_than_twenty_items_cannot_ride_in_one_batch_and_the_summary_counts_both_round_trips()
        {
            // Twenty is a hard service limit, not a tuning knob: the SDK throws
            // on the twenty-first step. A regression that let a chunk grow would
            // not be a slow run, it would be a crash on the first real corpus.
            var source = new FakePushSource(Items(24));
            (PushEngine engine, StubGraphAdapter adapter) = Engine(batch: true);

            PushSummary summary = await engine.PushItemsAsync(source);

            Assert.Equal(new[] { 20, 4 }, adapter.BatchSizes.ToArray());
            Assert.Equal(2, adapter.BatchRoundTrips);

            // PushSummary.Batches is the number that says whether batching paid.
            // Counting batches instead of round trips would report one where two
            // were spent, and a retried batch would then be free.
            Assert.Equal(2, summary.Batches);

            Assert.Equal(24, summary.Total);
            Assert.Equal(Ids(24), adapter.WrittenItemIds.ToArray());
        }

        [Fact]
        public async Task A_refused_item_in_a_batch_does_not_abandon_the_other_nineteen()
        {
            // The behaviour that makes batching worth having rather than merely
            // faster. Throwing here would discard the knowledge of which
            // nineteen landed, which is the one thing the caller needs in order
            // to record hashes for exactly those.
            var source = new FakePushSource(Items(20));
            (PushEngine engine, StubGraphAdapter adapter) = Engine(batch: true);

            adapter.BatchStatusFor = id => id == "a13" ? 400 : (int?)null;

            PushSummary summary = await engine.PushItemsAsync(source);

            Assert.Equal(19, adapter.WrittenItemIds.Count);
            Assert.DoesNotContain("a13", adapter.WrittenItemIds);

            // Counted, not thrown. A run that wrote nineteen of twenty says so.
            Assert.Equal(1, summary.Failed);

            // A terminal 4xx is terminal for this item: retrying it would spend
            // a round trip to be refused identically, four more times.
            Assert.Equal(new[] { 20 }, adapter.BatchSizes.ToArray());
        }

        [Fact]
        public async Task The_commit_prefix_stops_at_the_refused_item_and_never_steps_over_the_gap()
        {
            // THE SUBTLEST ASSERTION IN THIS FILE. Item 5 of twenty is refused
            // and the other nineteen are written anyway - so the set that is in
            // the index and the set the source may be told about are different
            // sets, and the engine has to keep them apart.
            //
            // The marker is the half that must be conservative. A source told it
            // committed as far as a20 would resume after a20 on the next
            // incremental run, and a5 would never be read again - permanently
            // missing from the index, with nothing reporting it. So the commit
            // stops at the first refusal in YIELDED order and does not resume
            // after it, even though a6 onwards demonstrably landed.
            var source = new FakePushSource(Items(20));
            (PushEngine engine, StubGraphAdapter adapter) = Engine(batch: true);

            adapter.BatchStatusFor = id => id == "a5" ? 400 : (int?)null;

            PushSummary summary = await engine.PushItemsAsync(source);

            Assert.Equal(new[] { "a1", "a2", "a3", "a4" }, source.Committed.ToArray());

            // The other half of the same fact: the items after the gap are not
            // re-sent and are not lost - they went out in the same batch and
            // Graph took them. A test that only checked the prefix would pass
            // just as happily on an engine that abandoned a6-a20 entirely.
            Assert.Equal(19, adapter.WrittenItemIds.Count);
            Assert.DoesNotContain("a5", adapter.WrittenItemIds);
            Assert.Contains("a6", adapter.WrittenItemIds);
            Assert.Contains("a20", adapter.WrittenItemIds);
            Assert.Equal(new[] { 20 }, adapter.BatchSizes.ToArray());

            Assert.Equal(1, summary.Failed);

            // The run reports what it WROTE, not how far the marker got. These
            // are different numbers whenever a batch refused something in the
            // middle, and reporting the prefix would have a run that sent
            // nineteen items claim four - while the state store, which records
            // what landed, held nineteen item rows beside it. The same database
            // disagreeing with itself is worse than either number alone.
            Assert.Equal(19, summary.Total);
        }

        [Fact]
        public async Task Once_a_run_has_left_a_gap_the_marker_never_moves_again()
        {
            // The other half of the gap rule, and the one that is invisible in a
            // single chunk. A batch refusal does not end the run, so the NEXT
            // chunk would commit in full and carry the marker straight over the
            // gap the refusal left - putting the source's position past an item
            // that is not in the index, which is the oldest invariant in this
            // repository and the one the whole IPushSource contract exists for.
            //
            // Forty items, two chunks, a5 refused in the first. The marker stops
            // at a4 and stays there: a21-a40 are written and recorded, because
            // they are genuinely in the index and the delete sweep must not
            // remove them, but the source is not told. The next run resumes from
            // before a5 and retries it, re-reading what was already written -
            // which costs time and nothing else, because every write is an upsert.
            var source = new FakePushSource(Items(40));
            (PushEngine engine, StubGraphAdapter adapter) = Engine(batch: true);

            adapter.BatchStatusFor = id => id == "a5" ? 400 : (int?)null;

            PushSummary summary = await engine.PushItemsAsync(source);

            Assert.Equal(new[] { "a1", "a2", "a3", "a4" }, source.Committed.ToArray());

            // Written and counted regardless - the gap freezes the marker, not
            // the work.
            Assert.Equal(39, adapter.WrittenItemIds.Count);
            Assert.Contains("a40", adapter.WrittenItemIds);
            Assert.DoesNotContain("a5", adapter.WrittenItemIds);
            Assert.Equal(39, summary.Total);
            Assert.Equal(1, summary.Failed);
        }

        [Fact]
        public async Task A_throttled_sub_response_is_retried_and_the_item_still_lands()
        {
            // 429 on a sub-response, 200 on the envelope carrying it. The item is
            // retried on its own rather than the whole batch being replayed - a
            // replay would rewrite the nineteen that already landed and spend the
            // quota twice, on precisely the run that is already being throttled.
            var refusedOnce = new HashSet<string>(StringComparer.Ordinal);
            var source = new FakePushSource(Items(20));
            (PushEngine engine, StubGraphAdapter adapter) = Engine(batch: true);

            adapter.BatchStatusFor = id => id == "a7" && refusedOnce.Add(id) ? 429 : (int?)null;

            PushSummary summary = await engine.PushItemsAsync(source);

            Assert.Contains("a7", adapter.WrittenItemIds);
            Assert.Equal(20, adapter.WrittenItemIds.Count);
            Assert.Equal(0, summary.Failed);
            Assert.Equal(20, summary.Total);

            // The retry carried the throttled item and nothing else.
            Assert.Equal(new[] { 20, 1 }, adapter.BatchSizes.ToArray());
            Assert.Equal(2, summary.Batches);

            // Counted once per refused SUB-REQUEST, not once per refused batch.
            // ThrottleWaits has always meant "how many writes the service turned
            // away", and batching changes how they were sent, not how many were
            // refused - a counter that dropped to one per batch would make a
            // throttled run look nineteen twentieths healthier than it is.
            Assert.Equal(1, summary.ThrottleWaits);

            // AND THE RETRY CARRIED THE ITEM'S GRANTS. This test asserted that
            // a7 "landed" and never looked at what landed, which is how a
            // retried item lost its ACL for the life of the project without a
            // red test: the stub accepts any body, so an item stripped of its
            // grants is indistinguishable from a correct one by arrival alone.
            //
            // Graph is not so forgiving - it answers 400 NullOrEmptyValue,
            // "'Acl' is null or empty" - and on the first run that was ever
            // throttled, all 191 throttled items came back exactly that, under a
            // run that reported success. The body is the assertion that would
            // have caught it.
            string retried = adapter.WrittenBodies[adapter.WrittenItemIds.IndexOf("a7")];

            Assert.Contains("\"acl\"", retried, StringComparison.Ordinal);
            Assert.Contains(TestData.GroupObjectId, retried, StringComparison.Ordinal);

            // And the run still finished, so the marker is honest all the way to
            // the end rather than stopping short of a row that did land.
            Assert.Equal(Ids(20), source.Committed.ToArray());
            Assert.True(source.Completed);
        }

        [Fact]
        public async Task The_throttle_callback_is_told_about_every_refused_sub_request()
        {
            // Driven against GraphBatchWriter directly, because the engine hands
            // this callback to the crawl state store and a run with no store
            // configured drops the events on the floor. What is being guarded is
            // that a throttled sub-response reaches the callback AT ALL, and with
            // the status and the wait it actually carried: crawl.RunThrottle is
            // the only record of why a slow run was slow, and an empty table
            // looks exactly like a run that was never throttled.
            var refusedOnce = new HashSet<string>(StringComparer.Ordinal);
            var events = new List<ThrottleEvent>();
            StubGraphAdapter adapter = Adapter();

            adapter.BatchStatusFor = id => refusedOnce.Add(id) ? 429 : (int?)null;
            adapter.BatchRetryAfterSeconds = 1;

            var summary = new PushSummary();
            var writer = new GraphBatchWriter(
                new Microsoft.Graph.GraphServiceClient(adapter),
                ConnectionId,
                summary,
                Logger.None,
                events.Add);

            BatchWriteResult result = await writer.WriteAsync(
                new[]
                {
                    ("a1", new ExternalItem { Id = "a1" }),
                    ("a2", new ExternalItem { Id = "a2" }),
                });

            // Both refused on the first pass, both written on the second.
            Assert.True(result.AllWritten);
            Assert.Equal(2, result.WrittenCount);
            Assert.Equal(2, result.RoundTrips);

            Assert.Equal(2, events.Count);
            Assert.All(events, throttle => Assert.Equal(429, throttle.StatusCode));

            // "batch" rather than "write", so the dashboard can tell a throttled
            // batch from a throttled single write without guessing.
            Assert.All(events, throttle => Assert.Equal("batch", throttle.Endpoint));

            // The wait the service ASKED for, read off the sub-response's own
            // headers. Reporting null here would mean the per-item Retry-After
            // never made it out of the batch envelope, which is the header the
            // writer's backoff is built on.
            Assert.All(events, throttle => Assert.Equal(1, throttle.RetryAfterSeconds));
            Assert.All(events, throttle => Assert.Equal(1, throttle.AttemptNumber));

            Assert.Equal(2, summary.ThrottleWaits);
        }

        [Fact]
        public async Task Batching_off_takes_the_single_item_path_and_posts_no_batch_at_all()
        {
            // Settings:Batch = false is a supported configuration and the first
            // thing an operator tries when a run starts failing in a way batching
            // could explain. Asserting the counts alone would not notice a
            // setting that had stopped being read, because both paths write the
            // same twenty-four items - only the round trips differ.
            var source = new FakePushSource(Items(24));
            (PushEngine engine, StubGraphAdapter adapter) = Engine(batch: false);

            PushSummary summary = await engine.PushItemsAsync(source);

            Assert.Equal(0, adapter.BatchRoundTrips);
            Assert.Empty(adapter.BatchSizes);
            Assert.Equal(0, summary.Batches);

            Assert.Equal(24, summary.Total);
            Assert.Equal(Ids(24), adapter.WrittenItemIds.ToArray());
            Assert.Equal(Ids(24), source.Committed.ToArray());
        }

        [Fact]
        public async Task A_dry_run_never_batches_and_still_writes_nothing()
        {
            // Batching is on, and a dry run must still reach Graph zero times.
            // A batch assembled and posted "harmlessly" would be a dry run that
            // wrote the corpus, which is the one failure a dry run exists to make
            // impossible.
            var source = new FakePushSource(Items(24));
            (PushEngine engine, StubGraphAdapter adapter) = Engine(batch: true, dryRun: true);

            PushSummary summary = await engine.PushItemsAsync(source);

            Assert.Equal(0, adapter.BatchRoundTrips);
            Assert.Empty(adapter.BatchSizes);
            Assert.Equal(0, summary.Batches);

            Assert.Empty(adapter.WrittenItemIds);
            Assert.Empty(source.Committed);
            Assert.False(source.Completed);

            // Still counted, because a dry run reports what it WOULD have done.
            Assert.Equal(24, summary.Total);
        }

        [Fact]
        public async Task Several_writers_each_flushing_a_batch_lose_nothing_when_one_batch_is_refused()
        {
            // The interaction, rather than either half of it. Concurrency is
            // covered, batching is covered, and until this test the combination
            // was covered only by a live tenant - which is where it was found:
            // the run that first exercised the default write path wrote 441 of
            // 1,118 items and refused the rest.
            //
            // What makes the combination its own case is that a batch is the unit
            // of a round trip while an item is the unit of an outcome, and with
            // several writers in flight those two stop lining up. A refusal
            // inside one writer's batch must not cost another writer's items,
            // and the counts have to reconcile across writers that never see
            // each other. Getting that wrong does not throw - it under-reports,
            // which is the failure this whole file exists to catch.
            var source = new FakePushSource(Items(80), requiresOrderedCommit: false);
            (PushEngine engine, StubGraphAdapter adapter) = Engine(batch: true, writers: 8);

            // Enough delay that the batches genuinely overlap rather than
            // serialising by accident on a fast machine.
            adapter.WriteDelay = TimeSpan.FromMilliseconds(40);

            // Two refusals in different batches, so this is not a single-batch
            // case wearing a concurrency hat. Ids are a01.. so a13 and a57 fall
            // in the first and third chunks of twenty.
            adapter.BatchStatusFor = id => id is "a13" or "a57" ? 400 : (int?)null;

            PushSummary summary = await engine.PushItemsAsync(source);

            Assert.True(
                adapter.MaxConcurrentWrites > 1,
                $"expected overlapping batches, saw at most {adapter.MaxConcurrentWrites} at once");

            // The whole claim: 78 of 80 land, and the two that did not are the
            // two that were refused. An engine that abandoned a batch on its
            // first refusal would be short by up to nineteen more.
            Assert.Equal(78, adapter.WrittenItemIds.Count);
            Assert.DoesNotContain("a13", adapter.WrittenItemIds);
            Assert.DoesNotContain("a57", adapter.WrittenItemIds);

            // No item is written twice under concurrency - a duplicate here
            // would mean two writers took the same chunk.
            Assert.Equal(
                adapter.WrittenItemIds.Count,
                adapter.WrittenItemIds.Distinct().Count());

            // And the summary reconciles across writers that never saw each
            // other: every item is accounted for exactly once, as written or
            // as failed.
            Assert.Equal(2, summary.Failed);
            Assert.Equal(78, summary.Total);
            Assert.Equal(80, summary.Total + summary.Failed);
        }

        private static StubGraphAdapter Adapter()
        {
            return new StubGraphAdapter(
                new ExternalConnection { Id = ConnectionId, State = ConnectionState.Ready },
                new SqlHierarchyPush.HierarchyPushConnector().BuildSchema());
        }

        private static (PushEngine Engine, StubGraphAdapter Adapter) Engine(
            bool batch, bool dryRun = false, int writers = 0)
        {
            StubGraphAdapter adapter = Adapter();

            // Set explicitly in both directions. The fixture pins Batch=false for
            // every other test in the suite, so a test here that relied on the
            // default would be testing the fixture.
            PushOptions options = TestData.ValidPushOptions(ConnectionId);
            options.Settings["Batch"] = batch ? "true" : "false";

            // Left unset by default so every existing test in this file keeps the
            // writer count it was written against.
            if (writers > 0)
            {
                options.Settings["Writers"] = writers.ToString(CultureInfo.InvariantCulture);
            }

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
