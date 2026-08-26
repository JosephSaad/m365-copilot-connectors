// ---------------------------------------------------------------------------
// CdpHiveWatermarkTests.cs
// The Hive watermark, round tripped through a checkpoint and back into a query.
//
// The defect these exist for read zero rows and reported success. A marker
// captured as ISO-8601 - 'T' separator, trailing 'Z' - is quoted into the next
// run's WHERE clause as a bare literal, and HiveQL's timestamp grammar accepts
// neither, so the comparison casts to NULL and matches nothing. Run 1 read the
// whole table and every run after it read nothing at all, nightly, for as long
// as anybody left it running.
//
// No test caught that, because every test stopped at one half of the loop:
// either it asserted the shape of a query built from a marker somebody typed
// into a fixture, or it asserted a checkpoint written from rows nobody read
// back. The bug lives in the join between the two. So the test that matters
// here is the ROUND TRIP - a source writes a marker, a SECOND source over the
// same checkpoint builds a query from it, and canned rows are filtered through
// a reader that mimics Hive's own comparison, including its refusal to compare
// against a literal it cannot cast. A marker Hive cannot parse admits no row,
// exactly as the cluster would, and the test fails with the empty result the
// operator would have seen in six months' time.
//
// The non-timestamp case is a guard rather than a reproduction: for a bigint or
// a string column the marker rendering is the existing text and always was, and
// this pins it there so a fix aimed at timestamps cannot start reformatting the
// columns that were never broken.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Runtime.CompilerServices;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using CdpConnector.Source;
    using CdpConnector.Source.Hive;
    using CdpConnector.Source.Ranger;
    using CdpConnector.Source.Watermark;
    using PushCore;
    using Serilog;
    using Serilog.Core;
    using Serilog.Events;
    using SqlTicketsConnector.Tests.TestSupport;
    using Xunit;

    public class CdpHiveWatermarkTests : IDisposable
    {
        private const string ConnectorKey = "cdphivecontracts";
        private const string WatermarkColumn = "last_modified_ts";
        private const string KeyColumn = "contract_ref";

        /// <summary>
        /// Hive's timestamp literal grammar, which is what a marker has to be
        /// written in. A space between the date and the time, no zone suffix,
        /// and up to nine fractional digits - none of which describes
        /// "2026-08-20T10:00:00.0000000Z".
        /// </summary>
        private static readonly string[] HiveTimestampFormats =
        {
            "yyyy-MM-dd",
            "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-dd HH:mm:ss.f",
            "yyyy-MM-dd HH:mm:ss.ff",
            "yyyy-MM-dd HH:mm:ss.fff",
            "yyyy-MM-dd HH:mm:ss.ffff",
            "yyyy-MM-dd HH:mm:ss.fffff",
            "yyyy-MM-dd HH:mm:ss.ffffff",
            "yyyy-MM-dd HH:mm:ss.fffffff",
            "yyyy-MM-dd HH:mm:ss.ffffffff",
            "yyyy-MM-dd HH:mm:ss.fffffffff",
        };

        private static readonly DateTime Base = new DateTime(2026, 8, 20, 10, 0, 0, DateTimeKind.Utc);

        private readonly string stateDirectory =
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(this.stateDirectory))
            {
                Directory.Delete(this.stateDirectory, true);
            }
        }

        [Fact]
        public async Task A_timestamp_marker_is_written_in_hives_literal_form_rather_than_iso_8601()
        {
            // The checkpoint is not a document for a person to read: it is the
            // input to the next run's WHERE clause. ISO-8601 is right for the
            // Graph property and wrong here, and the two renderings have to be
            // allowed to differ.
            var reader = new HiveLikeRowReader(Row("C-1000", Base));

            await using (HivePushSource source = this.Source(reader, Logger.None))
            {
                await CommitEverythingAsync(source);
            }

            string marker = this.Checkpoint().Read().MarkerTime;

            Assert.DoesNotContain("T", marker, StringComparison.Ordinal);
            Assert.DoesNotContain("Z", marker, StringComparison.Ordinal);
            Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}", marker);

            // And it is a literal Hive itself would accept, which is the claim
            // the shape above is only evidence for.
            Assert.True(
                TryParseHiveTimestamp(marker, out DateTime _),
                marker + " is not a Hive timestamp literal");
        }

        [Fact]
        public async Task A_marker_written_by_one_run_selects_the_right_rows_in_the_next()
        {
            // THE ROUND TRIP. Run one commits two of four rows and stops; run two
            // is a fresh source over the same checkpoint file, and the rows it
            // gets back are decided by a reader that compares the way Hive does -
            // a literal it cannot cast to a timestamp yields NULL, and a NULL
            // predicate admits nothing.
            var first = new HiveLikeRowReader(
                Row("C-1000", Base),
                Row("C-1001", Base),
                Row("C-1500", Base),
                Row("C-1002", Base.AddDays(1)));

            await using (HivePushSource source = this.Source(first, Logger.None))
            {
                var read = new List<PushItem>();

                await foreach (PushItem item in source.ReadAsync(CancellationToken.None))
                {
                    read.Add(item);
                }

                Assert.Equal(4, read.Count);

                // Two committed, then the run ends: the checkpoint stands on
                // C-1001, which shares its timestamp with C-1500.
                await source.OnItemCommittedAsync(read[0], CancellationToken.None);
                await source.OnItemCommittedAsync(read[1], CancellationToken.None);
                await source.OnCrawlCompletedAsync(CancellationToken.None);
            }

            var second = new HiveLikeRowReader(
                Row("C-1000", Base),
                Row("C-1001", Base),
                Row("C-1500", Base),
                Row("C-1002", Base.AddDays(1)));

            var resumed = new List<PushItem>();

            await using (HivePushSource source = this.Source(second, Logger.None))
            {
                await foreach (PushItem item in source.ReadAsync(CancellationToken.None))
                {
                    resumed.Add(item);
                }
            }

            string query = Assert.Single(second.Queries);

            // The literal the second run quoted is one Hive can parse. Without
            // that, everything below is zero rows and a green run.
            Match literal = Regex.Match(query, "`" + WatermarkColumn + "` > '([^']*)'");

            Assert.True(literal.Success, "the resumed query carries no watermark comparison: " + query);
            Assert.True(
                TryParseHiveTimestamp(literal.Groups[1].Value, out DateTime _),
                "Hive cannot parse '" + literal.Groups[1].Value + "', so this comparison matches nothing");

            // C-1000 is before the marker, C-1001 IS the marker, C-1500 ties on
            // the timestamp and is after it on the key, C-1002 is later.
            Assert.Equal(
                new[] { "C-1500", "C-1002" },
                resumed.Select(item => (string)item.Properties["contractRef"]).ToArray());
        }

        [Fact]
        public async Task A_null_watermark_is_excluded_from_the_query_and_the_run_says_so()
        {
            // Hive orders NULLs first ascending, so with Source:MaxItems set an
            // unexcluded NULL row is committed, contributes an empty marker, and
            // the checkpoint never moves: the same first N rows, every run, for
            // ever, reported as success.
            var sink = new CollectingSink();
            var reader = new HiveLikeRowReader(
                Row("C-0001", null),
                Row("C-0002", null),
                Row("C-0003", Base));

            var items = new List<PushItem>();

            using (Logger log = Log(sink))
            {
                await using HivePushSource source = this.Source(reader, log, maxItems: 2);

                await foreach (PushItem item in source.ReadAsync(CancellationToken.None))
                {
                    items.Add(item);
                }
            }

            string query = Assert.Single(reader.Queries);

            Assert.Contains("`" + WatermarkColumn + "` IS NOT NULL", query, StringComparison.Ordinal);

            Assert.Equal(
                new[] { "C-0003" },
                items.Select(item => (string)item.Properties["contractRef"]).ToArray());

            // Loud rather than silent: a row that is never indexed is a fact
            // about the crawl's coverage, and the column is named so the
            // operator knows which one to populate.
            LogEvent warning = Assert.Single(
                sink.Events,
                e => e.Level == LogEventLevel.Warning &&
                     e.MessageTemplate.Text.Contains("NULL", StringComparison.Ordinal));

            Assert.Contains(WatermarkColumn, warning.RenderMessage(), StringComparison.Ordinal);
        }

        [Fact]
        public async Task Items_committed_without_a_usable_marker_are_reported_rather_than_stranded()
        {
            // The backstop, for the row the query excluded and the driver handed
            // back anyway - a view over the table, or a driver that does not push
            // the predicate down. Items were written, the checkpoint did not
            // move, and the next run reads exactly the same rows: that is a
            // crawl that has stopped making progress while reporting success,
            // and it has to be audible.
            var sink = new CollectingSink();
            var reader = new HiveLikeRowReader(Row("C-0001", null), Row("C-0002", null))
            {
                HonoursPredicates = false,
            };

            using (Logger log = Log(sink))
            {
                await using HivePushSource source = this.Source(reader, log);

                await CommitEverythingAsync(source);
            }

            Assert.False(this.Checkpoint().Read().HasMarker);

            LogEvent warning = Assert.Single(
                sink.Events,
                e => e.Level == LogEventLevel.Warning &&
                     e.MessageTemplate.Text.Contains("watermark did not move", StringComparison.Ordinal));

            Assert.Contains(WatermarkColumn, warning.RenderMessage(), StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("rev", 100L, 200L, 300L)]
        [InlineData("etag", "rev-001", "rev-002", "rev-003")]
        public async Task A_non_timestamp_watermark_column_still_round_trips(
            string column, object first, object second, object third)
        {
            // The watermark column is any column identifier: a monotonic bigint
            // and a string revision are both configurations this ships to
            // support. Their markers need no reformatting to be comparable, and
            // reformatting one would be inventing a value the column does not
            // hold - which is why the timestamp rendering is a separate method
            // with a fallback rather than a rule applied to everything.
            var run = new HiveLikeRowReader(
                Row("C-1000", column, first),
                Row("C-1001", column, second),
                Row("C-1002", column, third));

            await using (HivePushSource source = this.Source(run, Logger.None, watermarkColumn: column))
            {
                var read = new List<PushItem>();

                await foreach (PushItem item in source.ReadAsync(CancellationToken.None))
                {
                    read.Add(item);
                }

                await source.OnItemCommittedAsync(read[0], CancellationToken.None);
                await source.OnItemCommittedAsync(read[1], CancellationToken.None);
                await source.OnCrawlCompletedAsync(CancellationToken.None);
            }

            Assert.Equal(
                Convert.ToString(second, CultureInfo.InvariantCulture),
                this.Checkpoint().Read().MarkerTime);

            var resumedReader = new HiveLikeRowReader(
                Row("C-1000", column, first),
                Row("C-1001", column, second),
                Row("C-1002", column, third));

            var resumed = new List<PushItem>();

            await using (HivePushSource source = this.Source(resumedReader, Logger.None, watermarkColumn: column))
            {
                await foreach (PushItem item in source.ReadAsync(CancellationToken.None))
                {
                    resumed.Add(item);
                }
            }

            Assert.Equal(
                new[] { "C-1002" },
                resumed.Select(item => (string)item.Properties["contractRef"]).ToArray());
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static bool TryParseHiveTimestamp(string literal, out DateTime parsed)
        {
            return DateTime.TryParseExact(
                literal, HiveTimestampFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed);
        }

        private static Logger Log(CollectingSink sink)
        {
            return new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.Sink(sink, LogEventLevel.Verbose)
                .CreateLogger();
        }

        private static Dictionary<string, object> Row(string reference, object watermark)
        {
            return Row(reference, WatermarkColumn, watermark);
        }

        private static Dictionary<string, object> Row(string reference, string column, object watermark)
        {
            return new Dictionary<string, object>
            {
                [KeyColumn] = reference,
                ["counterparty"] = "Northwind",
                ["status"] = "Open",
                [column] = watermark,
            };
        }

        private static async Task CommitEverythingAsync(HivePushSource source)
        {
            var read = new List<PushItem>();

            await foreach (PushItem item in source.ReadAsync(CancellationToken.None))
            {
                read.Add(item);
            }

            foreach (PushItem item in read)
            {
                await source.OnItemCommittedAsync(item, CancellationToken.None);
            }

            await source.OnCrawlCompletedAsync(CancellationToken.None);
        }

        private CheckpointStore Checkpoint()
        {
            return new CheckpointStore(this.stateDirectory, ConnectorKey, Logger.None);
        }

        private HivePushSource Source(
            IHiveRowReader reader,
            ILogger log,
            string watermarkColumn = WatermarkColumn,
            int maxItems = 0)
        {
            PushOptions options = CdpConnectorTests.CdpOptions();
            options.Source.ItemView = "contracts.contract";
            options.Source.MaxItems = maxItems;
            options.Settings["HiveWatermarkColumn"] = watermarkColumn;
            options.Settings["HiveKeyColumn"] = KeyColumn;
            options.Settings["CheckpointDirectory"] = this.stateDirectory;

            var policy = new RangerPolicy { Id = 1, PolicyType = RangerPolicyType.Access, Enabled = true };

            policy.Resources["database"] = new List<string> { "contracts" };
            policy.Resources["table"] = new List<string> { "contract" };

            var granted = new RangerPolicyItem();
            granted.Accesses.Add("select");
            granted.Groups.Add("hadoop-contracts-read");
            policy.Allow.Add(granted);

            return new HivePushSource(
                CdpSettings.From(options),
                options,
                reader,
                new RoutingEvaluator(new[] { policy }),
                new[] { new PushAclEntry(PushAclType.Group, TestData.GroupObjectId) },
                this.Checkpoint(),
                CdpGraphPush.HiveContractsConnector.MapRow,
                log);
        }

        /// <summary>
        /// Canned rows, filtered the way Hive would filter them.
        ///
        /// The point of the mimicry is the refusal: Hive compares a column
        /// against a string literal by casting the literal to the column's type,
        /// and a cast that fails produces NULL rather than an error. A NULL
        /// predicate is not true, so the row is not returned - which is why a
        /// marker in the wrong format reads zero rows instead of failing.
        /// </summary>
        private sealed class HiveLikeRowReader : IHiveRowReader
        {
            private readonly List<Dictionary<string, object>> rows;

            public HiveLikeRowReader(params Dictionary<string, object>[] rows)
            {
                this.rows = rows.ToList();
            }

            /// <summary>
            /// Gets or sets a value indicating whether the WHERE clause is
            /// applied. False is a view or a driver that hands back a row the
            /// predicate excluded, which is the only way the backstop is
            /// reachable once the query itself is right.
            /// </summary>
            public bool HonoursPredicates { get; set; } = true;

            public List<string> Queries { get; } = new List<string>();

            public async IAsyncEnumerable<HiveRow> QueryAsync(
                string query, [EnumeratorCancellation] CancellationToken cancellationToken)
            {
                this.Queries.Add(query);

                foreach (Dictionary<string, object> row in this.rows)
                {
                    await Task.Yield();

                    if (!this.HonoursPredicates || Admits(query, row))
                    {
                        yield return new HiveRow(row);
                    }
                }
            }

            public ValueTask DisposeAsync()
            {
                return ValueTask.CompletedTask;
            }

            private static bool Admits(string query, Dictionary<string, object> row)
            {
                // The literals this reads back are timestamps, keys and
                // revisions, none of which contains a quote - the escaping of a
                // marker that does is asserted elsewhere, not parsed here.
                Match watermark = Regex.Match(query, "`([A-Za-z0-9_]+)` IS NOT NULL");
                Match after = Regex.Match(query, "`([A-Za-z0-9_]+)` > '([^']*)'");

                if (watermark.Success && Value(row, watermark.Groups[1].Value) == null)
                {
                    return false;
                }

                if (!after.Success)
                {
                    // A first run: nothing to resume from, so every row the
                    // NULL check admitted is read.
                    return true;
                }

                string column = after.Groups[1].Value;
                string marker = after.Groups[2].Value;

                if (!TryCompare(Value(row, column), marker, out int comparison))
                {
                    return false;
                }

                if (comparison != 0)
                {
                    return comparison > 0;
                }

                Match tie = Regex.Match(query, @"` = '[^']*' AND `([A-Za-z0-9_]+)` > '([^']*)'");

                if (!tie.Success)
                {
                    return false;
                }

                string key = Convert.ToString(Value(row, tie.Groups[1].Value), CultureInfo.InvariantCulture)
                    ?? string.Empty;

                return string.CompareOrdinal(key, tie.Groups[2].Value) > 0;
            }

            private static object Value(Dictionary<string, object> row, string column)
            {
                return row.TryGetValue(column, out object value) ? value : null;
            }

            private static bool TryCompare(object value, string literal, out int comparison)
            {
                comparison = 0;

                if (value == null)
                {
                    // NULL compares to nothing at all, in either direction.
                    return false;
                }

                if (value is DateTime time)
                {
                    if (!TryParseHiveTimestamp(literal, out DateTime marker))
                    {
                        return false;
                    }

                    comparison = DateTime.SpecifyKind(time, DateTimeKind.Utc)
                        .CompareTo(DateTime.SpecifyKind(marker, DateTimeKind.Utc));

                    return true;
                }

                if (value is int || value is long || value is double || value is decimal)
                {
                    if (!double.TryParse(
                        literal, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
                    {
                        return false;
                    }

                    comparison = Convert.ToDouble(value, CultureInfo.InvariantCulture).CompareTo(number);

                    return true;
                }

                comparison = string.CompareOrdinal(
                    Convert.ToString(value, CultureInfo.InvariantCulture), literal);

                return true;
            }
        }
    }
}
