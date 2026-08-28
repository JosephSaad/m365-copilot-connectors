// ---------------------------------------------------------------------------
// PushSummary.cs
// What the run wrote, counted by item type so the summary line says something
// a person can check against the source.
//
// Every counter here is written from whichever thread finished a write, because
// a source that keeps no position lets the engine run several writers at once
// (IPushSource.RequiresOrderedCommit). So the increments are interlocked and
// the per-type dictionary is behind a lock. That is not defensive habit: a lost
// increment here shows up as a summary line that disagrees with the index, and
// this line is what an operator reconciles the run against.
// ---------------------------------------------------------------------------

namespace PushCore;

using System.Globalization;

/// <summary>Counts for one run.</summary>
public sealed class PushSummary
{
    private readonly Dictionary<string, int> byType = new Dictionary<string, int>(StringComparer.Ordinal);
    private readonly object gate = new object();

    private int truncated;
    private int throttleWaits;
    private int skipped;
    private int duplicates;
    private int total;

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

    /// <summary>Gets the total number of items written.</summary>
    public int Total => Volatile.Read(ref this.total);

    /// <summary>
    /// Gets where the wall clock went, attributed per segment. Rides on the
    /// summary because the summary already reaches the host; measuring the run
    /// should not require its own plumbing.
    /// </summary>
    public PushTiming Timing { get; } = new PushTiming();

    /// <summary>Gets the counts, keyed by item type.</summary>
    public IReadOnlyDictionary<string, int> ByType
    {
        get
        {
            lock (this.gate)
            {
                return new Dictionary<string, int>(this.byType, StringComparer.Ordinal);
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

    /// <summary>Records one written item.</summary>
    /// <param name="itemType">The item's declared type.</param>
    /// <returns>
    /// The running total including this item. Returned rather than read back so
    /// a caller pacing progress on it gets each number exactly once, even with
    /// several writers counting at the same moment.
    /// </returns>
    public int Count(string itemType)
    {
        string key = string.IsNullOrWhiteSpace(itemType) ? "item" : itemType;

        lock (this.gate)
        {
            this.byType[key] = this.byType.TryGetValue(key, out int existing) ? existing + 1 : 1;
        }

        return Interlocked.Increment(ref this.total);
    }

    /// <summary>Renders the per-type counts as "Customer=12, Engagement=62".</summary>
    /// <returns>The counts in insertion order, or "none" when nothing was written.</returns>
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
                this.byType.Select(pair => string.Format(
                    CultureInfo.InvariantCulture, "{0}={1}", pair.Key, pair.Value)));
        }
    }
}
