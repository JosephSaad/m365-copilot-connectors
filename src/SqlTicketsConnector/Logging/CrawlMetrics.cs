// ---------------------------------------------------------------------------
// CrawlMetrics.cs
// Counters for one crawl, emitted as a single summary line at Information.
// A metrics endpoint is not required; one structured line per crawl is enough
// for a SIEM or a log analytics query to trend on.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Logging
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Threading;
    using Serilog;

    /// <summary>
    /// Accumulates counts for a single crawl.
    /// </summary>
    public sealed class CrawlMetrics
    {
        private readonly Stopwatch stopwatch = Stopwatch.StartNew();
        private readonly Dictionary<string, int> errorsByCategory = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly object errorGate = new object();

        private long itemsStreamed;
        private long itemsSkipped;
        private long itemsDeleted;
        private long itemsTruncated;
        private long contentBytes;
        private long sqlRoundTrips;

        /// <summary>Gets the number of items written to the response stream.</summary>
        public long ItemsStreamed
        {
            get { return Interlocked.Read(ref this.itemsStreamed); }
        }

        /// <summary>Gets the number of rows deliberately not emitted.</summary>
        public long ItemsSkipped
        {
            get { return Interlocked.Read(ref this.itemsSkipped); }
        }

        /// <summary>Gets the number of delete markers emitted.</summary>
        public long ItemsDeleted
        {
            get { return Interlocked.Read(ref this.itemsDeleted); }
        }

        /// <summary>Gets the number of items whose content was truncated.</summary>
        public long ItemsTruncated
        {
            get { return Interlocked.Read(ref this.itemsTruncated); }
        }

        /// <summary>Gets the total content bytes streamed, after truncation.</summary>
        public long ContentBytes
        {
            get { return Interlocked.Read(ref this.contentBytes); }
        }

        /// <summary>Gets the number of SQL commands executed.</summary>
        public long SqlRoundTrips
        {
            get { return Interlocked.Read(ref this.sqlRoundTrips); }
        }

        /// <summary>Gets the elapsed time since the crawl started.</summary>
        public TimeSpan Elapsed
        {
            get { return this.stopwatch.Elapsed; }
        }

        /// <summary>Counts an emitted item and its content size.</summary>
        public void RecordItem(int contentByteCount)
        {
            Interlocked.Increment(ref this.itemsStreamed);
            Interlocked.Add(ref this.contentBytes, contentByteCount);
        }

        /// <summary>Counts a skipped row.</summary>
        public void RecordSkipped()
        {
            Interlocked.Increment(ref this.itemsSkipped);
        }

        /// <summary>Counts an emitted delete marker.</summary>
        public void RecordDeleted()
        {
            Interlocked.Increment(ref this.itemsDeleted);
            Interlocked.Increment(ref this.itemsStreamed);
        }

        /// <summary>Counts a truncated item.</summary>
        public void RecordTruncated()
        {
            Interlocked.Increment(ref this.itemsTruncated);
        }

        /// <summary>Counts a SQL command execution.</summary>
        public void RecordSqlRoundTrip()
        {
            Interlocked.Increment(ref this.sqlRoundTrips);
        }

        /// <summary>Counts an error against a category such as authentication or transient.</summary>
        public void RecordError(string category)
        {
            lock (this.errorGate)
            {
                int count;
                this.errorsByCategory.TryGetValue(category, out count);
                this.errorsByCategory[category] = count + 1;
            }
        }

        /// <summary>Gets a snapshot of the error counts.</summary>
        public IReadOnlyDictionary<string, int> ErrorsByCategory()
        {
            lock (this.errorGate)
            {
                return new Dictionary<string, int>(this.errorsByCategory, StringComparer.Ordinal);
            }
        }

        /// <summary>
        /// Writes the end of crawl summary. Counts and sizes only: no identifiers
        /// from the data and no content.
        /// </summary>
        public void WriteSummary(ILogger logger, string operation, string startWatermark, string endWatermark)
        {
            if (logger == null)
            {
                return;
            }

            logger.Information(
                "{Operation} summary: items={Items} deleted={Deleted} skipped={Skipped} truncated={Truncated} " +
                "contentBytes={ContentBytes} sqlRoundTrips={SqlRoundTrips} durationMs={DurationMs} " +
                "errors={@ErrorsByCategory} watermarkIn={WatermarkIn} watermarkOut={WatermarkOut}",
                operation,
                this.ItemsStreamed,
                this.ItemsDeleted,
                this.ItemsSkipped,
                this.ItemsTruncated,
                this.ContentBytes,
                this.SqlRoundTrips,
                (long)this.Elapsed.TotalMilliseconds,
                this.ErrorsByCategory(),
                startWatermark,
                endWatermark);
        }
    }
}
