// ---------------------------------------------------------------------------
// PushTimingTests.cs
// The measurement has to be trustworthy before anything is decided on it.
//
// Two claims are worth pinning. The first is arithmetic: the histogram trades
// exactness for fixed memory, so these tests state how much exactness - the
// maximum stays exact, and a percentile stays inside the power-of-two bucket it
// fell in. A reader who sees "p95 = 4096ms" should know it means "between 4096
// and 8192", not "4096".
//
// The second is the reason the class exists at all: time asleep in backoff and
// time in flight to Graph must never be added together, because they argue for
// opposite changes. A run that is 90% asleep needs FEWER requests in flight,
// and a single blended "the write took 3.5s" number would send someone the
// other way.
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

    public class PushTimingTests
    {
        private const string ConnectionId = "consultingwork";

        [Fact]
        public void An_empty_series_reports_zero_rather_than_dividing_by_it()
        {
            var series = new TimingSeries("empty");

            Assert.Equal(0, series.Count);
            Assert.Equal(0, series.Percentile(0.50));
            Assert.Equal(0, series.Percentile(0.99));
            Assert.Equal(0, series.Max);
        }

        [Fact]
        public void The_maximum_is_exact_even_though_the_buckets_are_not()
        {
            var series = new TimingSeries("write");

            foreach (long sample in new long[] { 3, 700, 12_345, 9_999_999 })
            {
                series.Add(sample);
            }

            // The bucket the largest sample landed in spans 8.4M to 16.8M, but the
            // maximum is tracked outside the histogram: an operator chasing a
            // pathological row needs the real number, not its bucket.
            Assert.Equal(9_999_999, series.Max);
            Assert.Equal(4, series.Count);
            Assert.Equal(3 + 700 + 12_345 + 9_999_999, series.Sum);
        }

        [Fact]
        public void A_percentile_lands_inside_the_bucket_its_sample_fell_in()
        {
            var series = new TimingSeries("write");

            // 985 fast rows and 15 slow ones. p99 is the 990th sample, so it has
            // to fall among the slow ones - and the median has to stay unmoved by
            // them, which is the whole reason this reports percentiles rather
            // than a mean.
            for (int i = 0; i < 985; i++)
            {
                series.Add(1_000);
            }

            for (int i = 0; i < 15; i++)
            {
                series.Add(4_000_000);
            }

            long median = series.Percentile(0.50);
            long p99 = series.Percentile(0.99);

            // 1,000 lands in the bucket spanning [512, 1024) and 4,000,000 in the
            // one spanning [2097152, 4194304). A reader must take a reported
            // percentile as "somewhere in this bucket", never as an exact figure -
            // which is accurate enough to separate 0.3s from 3.2s, and that is the
            // only decision it is asked to inform.
            Assert.InRange(median, 512, 1_024);
            Assert.InRange(p99, 2_097_152, 4_194_304);

            // A mean would have read about 61ms here and described neither
            // population: no row was anywhere near that.
            Assert.True(p99 > median * 100);
        }

        [Fact]
        public void Backoff_and_time_in_flight_are_never_added_together()
        {
            var timing = new PushTiming();

            // One row: 200ms actually talking to Graph, 4s asleep after a 429.
            timing.WriteInFlight.Add(200_000);
            timing.WriteBackoff.Add(4_000_000);
            timing.RowTotal.Add(4_200_000);

            Assert.Equal(200_000, timing.WriteInFlight.Sum);
            Assert.Equal(4_000_000, timing.WriteBackoff.Sum);
            Assert.Equal(1, timing.RowsThatBackedOff);

            // And the report must say which world this is, because the two call
            // for opposite remedies.
            string report = timing.Report();

            Assert.Contains("THROTTLE-BOUND", report);
            Assert.DoesNotContain("MOSTLY IN FLIGHT", report);
        }

        [Fact]
        public void A_run_that_is_mostly_in_flight_refuses_to_call_itself_latency_bound()
        {
            // The most expensive mistake this file can make. Time inside PutAsync
            // includes retries the Graph SDK performed on its own - Kiota's
            // RetryHandler defaults to 3 attempts at 3s and never tells the engine -
            // so a throttled run and a slow one produce an identical table. The
            // verdict has to name that, not resolve it.
            var timing = new PushTiming();

            timing.WriteInFlight.Add(3_000_000);
            timing.WriteBackoff.Add(0);
            timing.RowTotal.Add(3_100_000);

            string report = timing.Report();

            Assert.Contains("MOSTLY IN FLIGHT", report);
            Assert.Contains("MaxRetry", report);
            Assert.DoesNotContain("THROTTLE-BOUND", report);
            Assert.Equal(0, timing.RowsThatBackedOff);
        }

        [Fact]
        public void The_report_never_names_an_item_or_its_content()
        {
            // The same rule the log follows. Durations and counts leave the
            // process; nothing that identifies a row does.
            var timing = new PushTiming();

            timing.SourceRead.Add(1_000);
            timing.Prepare.Add(50);
            timing.WriteInFlight.Add(300_000);
            timing.WriteBackoff.Add(0);
            timing.Commit.Add(200);
            timing.ContentBytes.Add(4_096);
            timing.RowTotal.Add(301_250);

            string report = timing.Report();

            Assert.Contains("source read", report);
            Assert.Contains("content bytes", report);
            Assert.Contains("ROW TOTAL", report);
        }

        [Fact]
        public async Task A_real_run_measures_one_row_total_for_every_item_it_wrote()
        {
            var source = new FakePushSource(Items("a1", "a2", "a3"));
            PushEngine engine = Engine();

            PushSummary summary = await engine.PushItemsAsync(source);

            Assert.Equal(3, summary.Total);
            Assert.Equal(3, summary.Timing.RowTotal.Count);
            Assert.Equal(3, summary.Timing.WriteInFlight.Count);
            Assert.Equal(3, summary.Timing.ContentBytes.Count);

            // One more read than there are rows: the last MoveNextAsync is the one
            // that reports the source is finished, and it is time spent too.
            Assert.Equal(4, summary.Timing.SourceRead.Count);

            // Nothing was throttled, so nothing slept.
            Assert.Equal(0, summary.Timing.WriteBackoff.Sum);
            Assert.Equal(0, summary.Timing.RowsThatBackedOff);
        }

        [Fact]
        public async Task A_dry_run_measures_the_pipeline_without_measuring_any_write()
        {
            // Worth having as its own guarantee: it means the non-Graph cost of a
            // slow run can be attributed with no tenant, no credential and no risk
            // of writing anything.
            var source = new FakePushSource(Items("a1", "a2"));
            PushEngine engine = Engine(dryRun: true);

            PushSummary summary = await engine.PushItemsAsync(source);

            Assert.Equal(2, summary.Timing.RowTotal.Count);
            Assert.Equal(2, summary.Timing.Prepare.Count);
            Assert.Equal(0, summary.Timing.WriteInFlight.Count);
            Assert.Equal(0, summary.Timing.Commit.Count);

            // And the dry-run contract itself still holds.
            Assert.Empty(source.Committed);
        }

        private static PushEngine Engine(bool dryRun = false)
        {
            var adapter = new StubGraphAdapter(
                new ExternalConnection { Id = ConnectionId, State = ConnectionState.Ready },
                new SqlHierarchyPush.HierarchyPushConnector().BuildSchema());

            return new PushEngine(
                new SqlHierarchyPush.HierarchyPushConnector(),
                TestData.ValidPushOptions(ConnectionId),
                new Microsoft.Graph.GraphServiceClient(adapter),
                Logger.None,
                dryRun);
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
    }
}
