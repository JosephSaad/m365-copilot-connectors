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
// ---------------------------------------------------------------------------

namespace PushCore;

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
