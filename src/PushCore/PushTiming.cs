// ---------------------------------------------------------------------------
// PushTiming.cs
// Where the wall clock actually went, attributed to the five things a row does.
//
// This exists to answer one question before anyone optimises anything: of the
// seconds a row costs, how many are spent talking to Graph and how many are
// spent ASLEEP in backoff after Graph refused. Those two numbers call for
// opposite remedies - the first wants more requests in flight, the second wants
// fewer - so guessing between them is worse than not acting at all.
//
// Two properties make this safe to leave switched on in production:
//
//   * It records durations and byte counts. Never an ID, never a property, never
//     content. The same rule the log follows, for the same reason.
//
//   * Memory is fixed. Samples land in quarter-octave buckets rather than a
//     list, so a ten-row run and a ten-million-row run both cost 1.5 KB.
//     Percentiles are interpolated within a bucket and are approximate by
//     construction; they are accurate to well within the margin that separates
//     "0.3 seconds" from "3.2 seconds", which is the decision they inform.
//
// Percentiles, not means: one row that waited 60 seconds behind a Retry-After
// moves a mean and tells you nothing about the other thousand.
//
// ONE BLIND SPOT, AND IT IS LOAD-BEARING. "Write in flight" is time inside
// PutAsync, and the Graph SDK's default pipeline puts a Kiota RetryHandler in
// there - MaxRetry 3, Delay 3s - which retries 429/503/504 on its own and never
// tells the engine. Its sleeps are charged here to in-flight, not to backoff,
// and they never increment ThrottleWaits. So this table CANNOT, by itself,
// distinguish a slow service from a throttled one. Verdict() says so rather than
// guessing; the way to settle it is to build the client with MaxRetry = 0 and
// let WriteWithRetryAsync be the only component that retries.
// ---------------------------------------------------------------------------

namespace PushCore;

using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;

/// <summary>
/// A fixed-memory distribution of one measured quantity, in quarter-octave buckets.
/// </summary>
public sealed class TimingSeries
{
    // 2^47 microseconds is a little over four years, so nothing a run can
    // produce falls off the end and the array never has to grow.
    private const int Octaves = 48;

    // Four linear sub-buckets per octave rather than one. A plain power-of-two
    // histogram reports p95, p99 and max as the same number whenever they share
    // an octave - which is exactly where the interesting rows are - so this
    // quarters it: 1.5 KB instead of 400 bytes, worst-case error 25% not 100%.
    private const int PerOctave = 4;

    private const int BucketCount = Octaves * PerOctave;

    private readonly long[] buckets = new long[BucketCount];

    /// <summary>Initializes a new instance of the <see cref="TimingSeries"/> class.</summary>
    /// <param name="name">The label this series reports under.</param>
    public TimingSeries(string name)
    {
        this.Name = name;
    }

    /// <summary>Gets the label this series reports under.</summary>
    public string Name { get; }

    /// <summary>Gets the number of samples recorded.</summary>
    public long Count { get; private set; }

    /// <summary>Gets the sum of every sample, in the series' own unit.</summary>
    public long Sum { get; private set; }

    /// <summary>Gets the largest sample, or zero when nothing was recorded.</summary>
    public long Max { get; private set; }

    /// <summary>Gets the number of samples that were greater than zero.</summary>
    public long NonZero { get; private set; }

    /// <summary>Records one sample.</summary>
    /// <param name="value">The sample, in the series' own unit. Negatives are clamped to zero.</param>
    public void Add(long value)
    {
        if (value < 0)
        {
            value = 0;
        }

        this.Count++;
        this.Sum += value;

        if (value > this.Max)
        {
            this.Max = value;
        }

        if (value > 0)
        {
            this.NonZero++;
        }

        this.buckets[Math.Min(BucketFor(value), BucketCount - 1)]++;
    }

    /// <summary>Maps a sample onto its quarter-octave bucket.</summary>
    /// <param name="value">The sample. Bucket 0 holds 0 and 1.</param>
    /// <returns>The bucket index.</returns>
    private static int BucketFor(long value)
    {
        if (value <= 1)
        {
            return 0;
        }

        int octave = BitOperations.Log2((ulong)value);
        long low = 1L << octave;
        long quarter = (value - low) * PerOctave / low;

        return (octave * PerOctave) + (int)quarter;
    }

    /// <summary>Gets the half-open range a bucket covers.</summary>
    /// <param name="bucket">The bucket index.</param>
    /// <returns>The inclusive low bound and the exclusive high bound.</returns>
    private static (long Low, long High) RangeOf(int bucket)
    {
        if (bucket == 0)
        {
            return (0, 2);
        }

        int octave = bucket / PerOctave;
        int quarter = bucket % PerOctave;

        long low = 1L << octave;
        long step = low / PerOctave;

        return step == 0
            ? (low, low + 1)
            : (low + (quarter * step), low + ((quarter + 1) * step));
    }

    /// <summary>Estimates a percentile by interpolating inside the bucket that contains it.</summary>
    /// <param name="fraction">The percentile as a fraction, so 0.95 for p95.</param>
    /// <returns>The estimated value, in the series' own unit.</returns>
    public long Percentile(double fraction)
    {
        if (this.Count == 0)
        {
            return 0;
        }

        long target = (long)Math.Ceiling(fraction * this.Count);

        if (target < 1)
        {
            target = 1;
        }

        long cumulative = 0;

        for (int i = 0; i < BucketCount; i++)
        {
            if (this.buckets[i] == 0)
            {
                continue;
            }

            if (cumulative + this.buckets[i] >= target)
            {
                (long low, long high) = RangeOf(i);

                double within = (target - cumulative) / (double)this.buckets[i];
                long estimate = low + (long)((high - low) * within);

                return Math.Min(estimate, this.Max);
            }

            cumulative += this.buckets[i];
        }

        return this.Max;
    }
}

/// <summary>
/// The per-segment attribution for one run. Hangs off <see cref="PushSummary"/>
/// so it reaches the host without any new plumbing.
/// </summary>
public sealed class PushTiming
{
    /// <summary>Gets time spent waiting on the source for the next row, in microseconds.</summary>
    /// <remarks>
    /// For a SQL source this is the fetch AND the row mapping, because the source
    /// yields from inside its own iterator. They are not separable from here, and
    /// they do not need to be: if this number is small, neither one is the problem.
    /// </remarks>
    public TimingSeries SourceRead { get; } = new TimingSeries("source read");

    /// <summary>Gets time spent resolving the ACL and building the item, in microseconds.</summary>
    public TimingSeries Prepare { get; } = new TimingSeries("prepare item");

    /// <summary>Gets time a row spent inside a Graph call that returned, in microseconds.</summary>
    public TimingSeries WriteInFlight { get; } = new TimingSeries("write in flight");

    /// <summary>Gets time a row spent asleep in backoff before a retry, in microseconds.</summary>
    /// <remarks>
    /// The number this whole class exists for. If it dominates, the run is
    /// throttle-bound and every form of added concurrency makes it worse.
    /// </remarks>
    public TimingSeries WriteBackoff { get; } = new TimingSeries("write backoff");

    /// <summary>Gets time spent logging, counting and committing after the write, in microseconds.</summary>
    public TimingSeries Commit { get; } = new TimingSeries("commit");

    /// <summary>Gets the whole cost of a row, in microseconds.</summary>
    public TimingSeries RowTotal { get; } = new TimingSeries("ROW TOTAL");

    /// <summary>Gets the size of the content sent for each item, in bytes.</summary>
    /// <remarks>
    /// Not a duration. It is here because a body near the 3.5 MB cap explains a
    /// slow PUT on its own, and because twenty such items will not fit in a
    /// single Graph $batch - so this number decides whether batching is even
    /// available before anyone designs for it.
    /// </remarks>
    public TimingSeries ContentBytes { get; } = new TimingSeries("content bytes");

    /// <summary>Gets the number of rows that slept in backoff at least once.</summary>
    /// <remarks>
    /// Read off the backoff series rather than counted separately: one row
    /// contributes exactly one sample to it, so a non-zero sample IS a row that
    /// slept, and a second counter could only ever disagree with this one.
    /// </remarks>
    public long RowsThatBackedOff => this.WriteBackoff.NonZero;

    /// <summary>Gets a timestamp for use with <see cref="MicrosecondsSince"/>.</summary>
    /// <returns>An opaque tick count.</returns>
    public static long Now()
    {
        return Stopwatch.GetTimestamp();
    }

    /// <summary>Converts a <see cref="Now"/> timestamp into elapsed microseconds.</summary>
    /// <param name="since">The timestamp taken when the segment started.</param>
    /// <returns>Elapsed microseconds.</returns>
    public static long MicrosecondsSince(long since)
    {
        long ticks = Stopwatch.GetTimestamp() - since;

        // Multiply before dividing, and only in long: at the 10 MHz frequency
        // Windows reports, doing it the other way rounds every sub-100us
        // segment to zero and "prepare item" reads as free when it is not.
        return ticks <= 0 ? 0 : ticks * 1_000_000L / Stopwatch.Frequency;
    }

    /// <summary>Renders the attribution as the block the operator reads after a run.</summary>
    /// <returns>A multi-line report, or a single line when no rows were measured.</returns>
    public string Report()
    {
        if (this.RowTotal.Count == 0)
        {
            return "No rows were measured.";
        }

        TimingSeries[] segments =
        {
            this.SourceRead,
            this.Prepare,
            this.WriteInFlight,
            this.WriteBackoff,
            this.Commit,
            this.RowTotal,
        };

        // Share is taken against the summed row total rather than against wall
        // clock: the difference between them is the run's fixed cost - opening
        // the connection, registering the schema, waiting for Ready - which is
        // real but is not per-row and must not be attributed to a segment.
        double whole = this.RowTotal.Sum <= 0 ? 1 : this.RowTotal.Sum;

        var text = new StringBuilder();

        text.Append(CultureInfo.InvariantCulture, $"Timing attribution over {this.RowTotal.Count:N0} row(s). ");
        text.AppendLine("Milliseconds, except where noted.");
        text.AppendLine();
        text.AppendLine("  segment                  p50        p95        p99        max      share");
        text.AppendLine("  ------------------------------------------------------------------------");

        foreach (TimingSeries series in segments)
        {
            if (series == this.RowTotal)
            {
                text.AppendLine("  ------------------------------------------------------------------------");
            }

            text.AppendLine(string.Format(
                CultureInfo.InvariantCulture,
                "  {0,-18} {1,10} {2,10} {3,10} {4,10} {5,9}",
                series.Name,
                Milliseconds(series.Percentile(0.50)),
                Milliseconds(series.Percentile(0.95)),
                Milliseconds(series.Percentile(0.99)),
                Milliseconds(series.Max),
                series == this.RowTotal
                    ? "100.0%"
                    : (series.Sum / whole * 100).ToString("F1", CultureInfo.InvariantCulture) + "%"));
        }

        text.AppendLine();
        text.AppendLine(string.Format(
            CultureInfo.InvariantCulture,
            "  content bytes      p50={0:N0}  p95={1:N0}  p99={2:N0}  max={3:N0}",
            this.ContentBytes.Percentile(0.50),
            this.ContentBytes.Percentile(0.95),
            this.ContentBytes.Percentile(0.99),
            this.ContentBytes.Max));

        text.AppendLine();
        text.AppendLine(this.Verdict());

        return text.ToString().TrimEnd();
    }

    private static string Milliseconds(long micros)
    {
        return (micros / 1000.0).ToString(micros >= 100_000 ? "F0" : "F2", CultureInfo.InvariantCulture);
    }

    /// <summary>States which of the two worlds the numbers describe, in one line.</summary>
    /// <returns>The reading, phrased as what to do next.</returns>
    private string Verdict()
    {
        double backoffShare = this.RowTotal.Sum <= 0
            ? 0
            : this.WriteBackoff.Sum / (double)this.RowTotal.Sum * 100;

        double throttledRows = this.RowTotal.Count == 0
            ? 0
            : this.RowsThatBackedOff / (double)this.RowTotal.Count * 100;

        double inFlightShare = this.WriteInFlight.Sum / (this.RowTotal.Sum <= 0 ? 1 : (double)this.RowTotal.Sum);

        string reading;

        if (backoffShare >= 40)
        {
            reading =
                "THROTTLE-BOUND. The run is mostly asleep, not mostly working. More items in flight " +
                "(concurrency or $batch) will increase 429s and make this worse. Reduce pressure, or " +
                "take the rate up with Microsoft.";
        }
        else if (inFlightShare >= 0.5)
        {
            // Deliberately not stated as a conclusion. "In flight" means "inside
            // PutAsync", and the Graph SDK's own Kiota RetryHandler sits in there:
            // it retries 429/503/504 by itself, three times, three seconds apart
            // by default, and none of that reaches the engine's catch or
            // ThrottleWaits. A throttle-bound run is therefore INDISTINGUISHABLE
            // from a latency-bound one in this table, and claiming otherwise here
            // would be the most expensive sentence in the file.
            reading =
                "MOSTLY IN FLIGHT - and that is not the same as latency-bound. Time inside PutAsync " +
                "includes any retry the Graph SDK performed internally: the Kiota RetryHandler " +
                "retries 429/503/504 itself, 3 times at 3s by default, invisibly to ThrottleWaits " +
                "and to the backoff row above. Tell them apart before acting: a 'write in flight' " +
                "p50 sitting near a multiple of 3s plus the base call is the signature of hidden " +
                "retries, and re-running with RetryHandlerOption.MaxRetry = 0 settles it outright.";
        }
        else
        {
            reading =
                "NEITHER. Most of the cost is outside the Graph call; read the segment shares above " +
                "before changing anything about how the writes are issued.";
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "  {0} of {1} row(s) ({2:F1}%) slept at least once; backoff is {3:F1}% of per-row time.{4}  => {5}",
            this.RowsThatBackedOff,
            this.RowTotal.Count,
            throttledRows,
            backoffShare,
            Environment.NewLine,
            reading);
    }
}
