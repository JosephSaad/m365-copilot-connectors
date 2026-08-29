// ---------------------------------------------------------------------------
// IPushSource.cs
// Where items come from, and where the watermark is kept.
//
// The two callbacks are the whole reason this is an interface rather than an
// IAsyncEnumerable. The unbreakable rule of this repository is that a failed
// crawl must never advance the watermark, and the only component that knows
// whether an item reached the index is the engine - it made the PUT. So the
// engine tells the source, item by item, what actually landed:
//
//   ReadAsync              yields candidates, in ascending checkpoint order
//   OnItemCommittedAsync   called ONLY after a successful write of that item
//   OnCrawlCompletedAsync  called ONLY when the enumeration finished cleanly
//
// A source that checkpointed inside ReadAsync would be recording rows it had
// merely read. A source that checkpointed on dispose would be recording a run
// that threw. Neither is possible through this interface: the failure path is
// simply the absence of a call.
//
// Ordering is the source's contract, not the engine's: resumption compares the
// stored marker against the next item, so a source that yields out of order
// loses rows on the run after an interruption rather than the run itself.
//
// RequiresOrderedCommit is the one place a source can trade that away. It
// defaults to true, so a source written before this existed - or written
// without thinking about it - keeps the serial behaviour and the guarantee that
// comes with it. Only a source that keeps NO position may return false, because
// only then is there no marker for out-of-order completion to corrupt.
// ---------------------------------------------------------------------------

namespace PushCore;

using PushCore.State;

/// <summary>An opened source, read once per run.</summary>
public interface IPushSource : IAsyncDisposable
{
    /// <summary>
    /// Gets how many candidates the source examined and declined to yield - a
    /// row with no key, a file of a type this connector does not index.
    ///
    /// It exists so the run summary still reconciles against the source: without
    /// it, "1,000 rows in the table, 940 items indexed" has no explanation in
    /// the log. Read once, after the enumeration ends. Defaults to zero for a
    /// source that yields everything it finds.
    /// </summary>
    int Skipped => 0;

    /// <summary>
    /// Gets a value indicating whether the engine must write this source's items
    /// one at a time, in the order they were yielded.
    ///
    /// True - the default, and what every source got before this existed - means
    /// serial writes. It is what makes the watermark rule free: the engine
    /// commits after each confirmed write, so a run that dies cannot leave the
    /// marker past an item the index does not have.
    ///
    /// Return false ONLY if the source keeps no position at all, which in
    /// practice means OnItemCommittedAsync does nothing. Such a source has no
    /// marker to corrupt, so there is nothing for ordering to protect, and the
    /// engine is free to write several items at once. A source that returns
    /// false while still recording a position is asking for a checkpoint that
    /// can outrun its own writes - do not.
    /// </summary>
    bool RequiresOrderedCommit => true;

    /// <summary>
    /// Gets how this source finds what changed, which decides whether the engine
    /// may read it incrementally.
    ///
    /// Defaults to <see cref="SourceChangeDetection.Differencing"/>: read
    /// everything, every run, and let the content and ACL hashes in the state
    /// store decide what is actually WRITTEN. That is the correct default
    /// because it is the one that is always safe - the saving is on the
    /// expensive side of the pipeline, since reading a hundred thousand rows out
    /// of SQL Server is seconds and writing a hundred thousand items to Graph is
    /// hours.
    ///
    /// Return <see cref="SourceChangeDetection.ChangeMarker"/> only when the
    /// source exposes a modification time that is monotonic, is UTC, and moves
    /// on EVERY change - including bulk updates, triggers and direct edits. Such
    /// a source must also read from
    /// <see cref="PushSourceContext.ResumeFrom"/>, yield in ascending
    /// (marker, id) order, and set <see cref="PushItem.LastModifiedUtc"/> on
    /// every item. Declaring the marker tier without doing all four is how a
    /// connector silently stops indexing the changes its timestamp missed.
    ///
    /// A ChangeMarker source almost certainly also needs
    /// <see cref="RequiresOrderedCommit"/> left at true, because it now has a
    /// position for out-of-order completion to move past.
    /// </summary>
    SourceChangeDetection ChangeDetection => SourceChangeDetection.Differencing;

    /// <summary>
    /// The items to consider, in ascending checkpoint order.
    ///
    /// Yield the item as mapped; the engine truncates, attaches the ACL,
    /// validates the ID and writes. Yielding is not indexing - an item may still
    /// be skipped as a duplicate or refused by Graph.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The candidate items.</returns>
    IAsyncEnumerable<PushItem> ReadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Reports that this item is in the index. Called after the write succeeded
    /// and never during a dry run.
    ///
    /// This is where a source advances its in-memory marker. Flush it to durable
    /// storage as often as the source can afford: everything since the last
    /// flush is re-read after an interruption, which is safe because the write
    /// is an upsert, and cheap compared to losing it.
    /// </summary>
    /// <param name="item">The item that was written.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task for the operation.</returns>
    ValueTask OnItemCommittedAsync(PushItem item, CancellationToken cancellationToken);

    /// <summary>
    /// Reports that the enumeration reached its end with no failed write. Only
    /// here may a source record anything that describes the run as a whole - a
    /// partition fingerprint, a high-water timestamp for the sweep.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task for the operation.</returns>
    ValueTask OnCrawlCompletedAsync(CancellationToken cancellationToken);
}
