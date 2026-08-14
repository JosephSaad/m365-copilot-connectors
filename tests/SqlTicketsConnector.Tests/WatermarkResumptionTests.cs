// ---------------------------------------------------------------------------
// WatermarkResumptionTests.cs
// Control evidence for checkpoint correctness.
//
// Rows sharing a LastModified value are the interesting case: a checkpoint that
// carries only the timestamp either loses the rest of the group or repeats it.
// These tests drive the real crawl code, cancel it mid group, resume from the
// checkpoint it handed back, and assert every row appears exactly once.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Graph.Connectors.Contracts.Grpc;
    using Serilog.Core;
    using SqlTicketsConnector.Connector;
    using SqlTicketsConnector.Server;
    using SqlTicketsConnector.Tests.TestSupport;
    using Xunit;

    public class WatermarkResumptionTests
    {
        private static readonly DateTime Shared = new DateTime(2026, 8, 13, 9, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime Later = Shared.AddMinutes(5);

        [Fact]
        public async Task No_row_is_skipped_or_repeated_across_a_checkpoint_boundary()
        {
            // Three rows share one LastModified value; the boundary falls inside them.
            var rows = new List<TicketRow>
            {
                TestData.Row(1001, Shared, "alpha"),
                TestData.Row(1002, Shared, "bravo"),
                TestData.Row(1003, Shared, "charlie"),
                TestData.Row(1004, Later, "delta"),
            };

            var source = new FakeTicketSource(rows);
            ConnectorOptions options = TestData.ValidOptions();
            var service = new ConnectorCrawlerServiceImpl(source, options, Logger.None);

            // First pass: cancel after two items, as a crashed or throttled crawl would.
            var cancellation = new CancellationTokenSource();
            var firstWriter = new FakeStreamWriter<IncrementalCrawlStreamBit>(count =>
            {
                if (count >= 2)
                {
                    cancellation.Cancel();
                }
            });

            await service.GetIncrementalCrawlStream(
                new GetIncrementalCrawlStreamRequest { Schema = SqlDataSource.BuildSchema() },
                firstWriter,
                new FakeServerCallContext("/x/GetIncrementalCrawlStream", cancellation.Token));

            List<string> firstPass = ItemIds(firstWriter.Written);
            Assert.Equal(new[] { "ticket1001", "ticket1002" }, firstPass);

            string checkpoint = firstWriter.Written
                .Where(bit => bit.CrawlItem != null)
                .Select(bit => bit.CrawlProgressMarker.CustomMarkerData)
                .Last();

            // Second pass: resume from the checkpoint the agent would have stored.
            var secondWriter = new FakeStreamWriter<IncrementalCrawlStreamBit>();

            await service.GetIncrementalCrawlStream(
                new GetIncrementalCrawlStreamRequest
                {
                    Schema = SqlDataSource.BuildSchema(),
                    CrawlProgressMarker = new CrawlCheckpoint { CustomMarkerData = checkpoint },
                },
                secondWriter,
                new FakeServerCallContext("/x/GetIncrementalCrawlStream", CancellationToken.None));

            List<string> secondPass = ItemIds(secondWriter.Written);

            Assert.Equal(new[] { "ticket1003", "ticket1004" }, secondPass);

            List<string> all = firstPass.Concat(secondPass).ToList();
            Assert.Equal(4, all.Count);
            Assert.Equal(4, all.Distinct().Count());
        }

        [Fact]
        public async Task Cancellation_emits_a_cancelled_status_rather_than_an_rpc_error()
        {
            var source = new FakeTicketSource(new List<TicketRow>
            {
                TestData.Row(1, Shared, "one"),
                TestData.Row(2, Shared, "two"),
                TestData.Row(3, Later, "three"),
            });

            var cancellation = new CancellationTokenSource();
            var writer = new FakeStreamWriter<CrawlStreamBit>(count =>
            {
                if (count >= 1)
                {
                    cancellation.Cancel();
                }
            });

            var service = new ConnectorCrawlerServiceImpl(source, TestData.ValidOptions(), Logger.None);

            await service.GetCrawlStream(
                new GetCrawlStreamRequest { Schema = SqlDataSource.BuildSchema() },
                writer,
                new FakeServerCallContext("/x/GetCrawlStream", cancellation.Token));

            CrawlStreamBit last = writer.Written[writer.Written.Count - 1];
            Assert.Equal(OperationResult.Cancelled, last.Status.Result);
            Assert.NotNull(last.CrawlProgressMarker);
        }

        [Fact]
        public async Task Soft_deleted_rows_are_emitted_as_deletes_incrementally_and_hidden_from_a_full_crawl()
        {
            var rows = new List<TicketRow>
            {
                TestData.Row(1, Shared, "live"),
                TestData.Row(2, Shared, "gone", deleted: true),
            };

            var incremental = new FakeStreamWriter<IncrementalCrawlStreamBit>();
            var service = new ConnectorCrawlerServiceImpl(
                new FakeTicketSource(rows),
                TestData.ValidOptions(),
                Logger.None);

            await service.GetIncrementalCrawlStream(
                new GetIncrementalCrawlStreamRequest { Schema = SqlDataSource.BuildSchema() },
                incremental,
                new FakeServerCallContext("/x/GetIncrementalCrawlStream", CancellationToken.None));

            IncrementalCrawlItem deleted = incremental.Written
                .Where(bit => bit.CrawlItem != null)
                .Select(bit => bit.CrawlItem)
                .Single(item => item.ItemId == "ticket2");

            Assert.Equal(IncrementalCrawlItem.Types.ItemType.DeletedItem, deleted.ItemType);
            Assert.NotNull(deleted.DeletedItem);

            var full = new FakeStreamWriter<CrawlStreamBit>();

            await service.GetCrawlStream(
                new GetCrawlStreamRequest { Schema = SqlDataSource.BuildSchema() },
                full,
                new FakeServerCallContext("/x/GetCrawlStream", CancellationToken.None));

            Assert.Equal(new[] { "ticket1" }, ItemIds(full.Written));
        }

        [Fact]
        public void Markers_round_trip_and_legacy_markers_degrade_safely()
        {
            var watermark = new Watermark(Shared, 1002);
            Watermark parsed;

            Assert.True(Watermark.TryParse(watermark.ToMarker(), out parsed));
            Assert.Equal(watermark, parsed);

            // A timestamp-only marker from the previous build resumes at the start of
            // that instant, which repeats a row rather than losing one.
            Assert.True(Watermark.TryParse(Shared.ToString("o"), out parsed));
            Assert.Equal(Shared, parsed.LastModifiedUtc);
            Assert.Equal(int.MinValue, parsed.TicketId);

            // An item ID marker cannot be resumed from at all.
            Assert.False(Watermark.TryParse("ticket1001", out parsed));
        }

        [Fact]
        public void The_watermark_predicate_orders_by_time_then_id()
        {
            var watermark = new Watermark(Shared, 1002);

            Assert.False(watermark.IsAfter(Shared, 1001));
            Assert.False(watermark.IsAfter(Shared, 1002));
            Assert.True(watermark.IsAfter(Shared, 1003));
            Assert.True(watermark.IsAfter(Later, int.MinValue));
            Assert.False(watermark.IsAfter(Shared.AddSeconds(-1), int.MaxValue));
        }

        private static List<string> ItemIds(IEnumerable<IncrementalCrawlStreamBit> bits)
        {
            return bits.Where(bit => bit.CrawlItem != null).Select(bit => bit.CrawlItem.ItemId).ToList();
        }

        private static List<string> ItemIds(IEnumerable<CrawlStreamBit> bits)
        {
            return bits.Where(bit => bit.CrawlItem != null).Select(bit => bit.CrawlItem.ItemId).ToList();
        }
    }
}
