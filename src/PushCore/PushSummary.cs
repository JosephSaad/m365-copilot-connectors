// ---------------------------------------------------------------------------
// PushSummary.cs
// What the run did, counted by item type so the summary line says something a
// person can check against the source.
//
// Every counter here is written from whichever thread finished a write, because
// a source that keeps no position lets the engine run several writers at once
// (IPushSource.RequiresOrderedCommit). So the increments are interlocked and
// the per-type dictionary is behind a lock. That is not defensive habit: a lost
// increment here shows up as a summary line that disagrees with the index, and
// this line is what an operator reconciles the run against.
//
// WRITTEN AND UNCHANGED ARE COUNTED SEPARATELY, AND THAT IS THE POINT.
// With a crawl state store attached, most items on a healthy run are not
// written at all - their hashes matched what is already in the index, so the
// engine skips the PUT. Folding those into Total would report a run that did
// almost nothing as a run that did everything, and would hide the single number
// that says whether change detection is working. The two are kept apart all the
// way through: Total is what reached Graph, Unchanged is what did not need to,
// and their sum is what the source actually yielded and the engine accepted.
//
// The per-type breakdown carries the same distinction. crawl.RunItemType stores
// it verbatim, which is what lets the dashboard answer "what did this run
// actually do" rather than "how many things did it touch".
// ---------------------------------------------------------------------------

namespace PushCore;

using System.Globalization;
using PushCore.State;

/// <summary>Counts for one run.</summary>
public sealed class PushSummary
{
    private readonly Dictionary<string, TypeTally> byType = new Dictionary<string, TypeTally>(StringComparer.Ordinal);
    private readonly object gate = new object();

    private int truncated;
    private int throttleWaits;
    private int skipped;
    private int duplicates;
    private int total;
    private int unchanged;
    private int deleted;
    private int failed;
    private int batches;
    private long bytesWritten;

    /// <summary>Gets the number of items whose content did not fit the cap.</summary>
    public int Truncated => Volatile.Read(ref this.truncated);

    /// <summary>Gets the number of times the run backed off after a 429.</summary>
    public int ThrottleWaits => Volatile.Read(ref this.throttleWaits);

    /// <summary>Gets the number of rows the connector chose to skip.</summary>
    public int Skipped => Volatile.Read(ref this.skipped);

    /// <summary>
    /// Gets the number of rows whose item ID repeated an earlier row's. The later
    /// row overwrote the earlier item; the source should return one row per item.
    /// </summary>
    public int Duplicates => Volatile.Read(ref this.duplicates);

    /// <summary>Gets the total number of items written to Graph.</summary>
    /// <remarks>
    /// Written, not considered. An item the state store said was already correct
    /// is counted in <see cref="Unchanged"/> and is deliberately not here - the
    /// whole value of change detection is the gap between the two numbers.
    /// </remarks>
    public int Total => Volatile.Read(ref this.total);

    /// <summary>
    /// Gets the number of items the state store said were already correct, so no
    /// write was made.
    /// </summary>
    /// <remarks>
    /// Always zero without a crawl state store, which is the honest answer: a
    /// run with no memory cannot know an item was already right, so it writes
    /// everything and this stays at nought.
    /// </remarks>
    public int Unchanged => Volatile.Read(ref this.unchanged);

    /// <summary>Gets the number of items the delete sweep removed from the index.</summary>
    public int Deleted => Volatile.Read(ref this.deleted);

    /// <summary>Gets the number of writes that gave up. Zero on a successful run.</summary>
    /// <remarks>
    /// Non-zero only on the batching path, where one refused item does not stop
    /// the other nineteen. A single-item write that exhausts its attempts throws
    /// and ends the run, so it is never counted here.
    /// </remarks>
    public int Failed => Volatile.Read(ref this.failed);

    /// <summary>Gets the number of $batch requests issued. Zero when batching is off.</summary>
    public int Batches => Volatile.Read(ref this.batches);

    /// <summary>Gets the content bytes actually sent to Graph.</summary>
    public long BytesWritten => Volatile.Read(ref this.bytesWritten);

    /// <summary>
    /// Gets where the wall clock went, attributed per segment. Rides on the
    /// summary because the summary already reaches the host; measuring the run
    /// should not require its own plumbing.
    /// </summary>
    public PushTiming Timing { get; } = new PushTiming();

    /// <summary>Gets the written counts, keyed by item type.</summary>
    /// <remarks>
    /// Written only, for compatibility with everything that read this before the
    /// state store existed. <see cref="TypeTotals"/> is the full breakdown.
    /// </remarks>
    public IReadOnlyDictionary<string, int> ByType
    {
        get
        {
            lock (this.gate)
            {
                return this.byType.ToDictionary(
                    pair => pair.Key, pair => pair.Value.Written, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>Records one item whose content was truncated to the cap.</summary>
    internal void CountTruncated()
    {
        Interlocked.Increment(ref this.truncated);
    }

    /// <summary>Records one wait taken after a 429.</summary>
    internal void CountThrottleWait()
    {
        Interlocked.Increment(ref this.throttleWaits);
    }

    /// <summary>Records one candidate that was not written.</summary>
    /// <param name="count">How many to add. One, except when folding in the source's own tally.</param>
    internal void CountSkipped(int count = 1)
    {
        Interlocked.Add(ref this.skipped, count);
    }

    /// <summary>Records one item ID that repeated an earlier row's.</summary>
    internal void CountDuplicate()
    {
        Interlocked.Increment(ref this.duplicates);
    }

    /// <summary>Records one $batch request.</summary>
    internal void CountBatch()
    {
        Interlocked.Increment(ref this.batches);
    }

    /// <summary>Records content bytes sent to Graph.</summary>
    /// <param name="itemType">The item's declared type.</param>
    /// <param name="bytes">How many bytes of content were sent.</param>
    internal void CountBytes(string itemType, long bytes)
    {
        Interlocked.Add(ref this.bytesWritten, bytes);
        this.Tally(itemType, tally => tally.Bytes += bytes);
    }

    /// <summary>Records one item the store said was already correct.</summary>
    /// <param name="itemType">The item's declared type.</param>
    /// <returns>The running unchanged total including this item.</returns>
    public int CountUnchanged(string itemType)
    {
        this.Tally(itemType, tally => tally.Unchanged++);
        return Interlocked.Increment(ref this.unchanged);
    }

    /// <summary>Records one item removed from the index by the sweep.</summary>
    /// <param name="itemType">The item's declared type.</param>
    /// <returns>The running deleted total including this item.</returns>
    public int CountDeleted(string itemType)
    {
        this.Tally(itemType, tally => tally.Deleted++);
        return Interlocked.Increment(ref this.deleted);
    }

    /// <summary>Records one write that gave up.</summary>
    /// <param name="itemType">The item's declared type.</param>
    /// <returns>The running failed total including this item.</returns>
    public int CountFailed(string itemType)
    {
        this.Tally(itemType, tally => tally.Failed++);
        return Interlocked.Increment(ref this.failed);
    }

    /// <summary>Records one candidate of a known type that was not written.</summary>
    /// <param name="itemType">The item's declared type.</param>
    internal void CountSkipped(string itemType)
    {
        this.Tally(itemType, tally => tally.Skipped++);
        Interlocked.Increment(ref this.skipped);
    }

    /// <summary>Records one written item.</summary>
    /// <param name="itemType">The item's declared type.</param>
    /// <returns>
    /// The running total including this item. Returned rather than read back so
    /// a caller pacing progress on it gets each number exactly once, even with
    /// several writers counting at the same moment.
    /// </returns>
    public int Count(string itemType)
    {
        this.Tally(itemType, tally => tally.Written++);
        return Interlocked.Increment(ref this.total);
    }

    /// <summary>Gets the full per-type breakdown, as the state store records it.</summary>
    /// <returns>One entry per item type the run touched.</returns>
    public IReadOnlyList<ItemTypeTotals> TypeTotals()
    {
        lock (this.gate)
        {
            return this.byType
                .Select(pair => new ItemTypeTotals(
                    pair.Key,
                    pair.Value.Written,
                    pair.Value.Unchanged,
                    pair.Value.Deleted,
                    pair.Value.Skipped,
                    pair.Value.Failed,
                    pair.Value.Bytes))
                .ToList();
        }
    }

    /// <summary>Renders the per-type counts as "Customer=12, Engagement=62".</summary>
    /// <returns>The counts in insertion order, or "none" when nothing was written.</returns>
    /// <remarks>
    /// Unchanged items are shown beside the written count when there are any -
    /// "TimeEntry=4 (+1040 unchanged)" - because on a healthy incremental run
    /// the written number alone looks like a failure.
    /// </remarks>
    public string Describe()
    {
        lock (this.gate)
        {
            if (this.byType.Count == 0)
            {
                return "none";
            }

            return string.Join(
                ", ",
                this.byType.Select(pair => pair.Value.Unchanged == 0
                    ? string.Format(CultureInfo.InvariantCulture, "{0}={1}", pair.Key, pair.Value.Written)
                    : string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}={1} (+{2} unchanged)",
                        pair.Key,
                        pair.Value.Written,
                        pair.Value.Unchanged)));
        }
    }

    /// <summary>Applies a change to one type's tally under the lock.</summary>
    /// <param name="itemType">The item's declared type. Blank becomes "item".</param>
    /// <param name="change">What to do to the tally.</param>
    private void Tally(string itemType, Action<TypeTally> change)
    {
        string key = string.IsNullOrWhiteSpace(itemType) ? "item" : itemType;

        lock (this.gate)
        {
            if (!this.byType.TryGetValue(key, out TypeTally? tally))
            {
                tally = new TypeTally();
                this.byType[key] = tally;
            }

            change(tally);
        }
    }

    /// <summary>One item type's counters. Mutable, and only ever touched under the lock.</summary>
    /// <remarks>
    /// A class rather than a struct on purpose: a struct in a dictionary is
    /// copied on read, so the increment would land on the copy and be lost.
    /// That defect compiles, runs, and silently reports zeros.
    /// </remarks>
    private sealed class TypeTally
    {
        public int Written { get; set; }

        public int Unchanged { get; set; }

        public int Deleted { get; set; }

        public int Skipped { get; set; }

        public int Failed { get; set; }

        public long Bytes { get; set; }
    }
}
