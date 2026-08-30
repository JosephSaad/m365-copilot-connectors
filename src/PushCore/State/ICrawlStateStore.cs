// ---------------------------------------------------------------------------
// ICrawlStateStore.cs
// The memory a push has instead of an agent.
//
// The Graph connector agent remembers what it sent, what each item looked like
// when it sent it, and how far it got. Everything a direct push cannot do -
// detect a deletion, skip an unchanged item, resume - traces back to not having
// that memory. This is the seam where it gets one.
//
// THREE RULES SHAPE EVERY METHOD HERE.
//
// 1. Batched, never per item. Every call that could be per item takes a
//    collection instead, because the engine's whole reason for existing at the
//    moment is that 3.5 seconds a row was too slow. A store that added a round
//    trip beside each Graph write would give back what batching and concurrency
//    just bought, and it would do it invisibly - the timing table would charge
//    it to Commit, where nobody is looking.
//
// 2. Recorded after the fact, never before. RecordWrittenAsync is called once
//    Graph has confirmed, exactly as IPushSource.OnItemCommittedAsync is. The
//    failure mode of getting this backwards is the worst one in the design: a
//    hash written before the PUT means the next run sees the item as unchanged
//    and skips it, so a single failure turns into an item that is permanently
//    stale AND permanently invisible, with nothing reporting either.
//
// 3. The store is optional. NullCrawlStateStore implements every method as a
//    no-op and IsEnabled returns false, so a connector with no ConnectorState
//    database configured behaves exactly as it did before any of this existed -
//    it writes everything every run and never deletes. That is the pre-existing
//    behaviour, unchanged, rather than a degraded mode.
//
// Implementations must be safe to call from several writer threads at once. The
// engine keeps reading and duplicate detection on one thread, but the write
// path is concurrent by design and the recording follows the writes.
// ---------------------------------------------------------------------------

namespace PushCore.State;

/// <summary>Durable memory for one connection's crawls.</summary>
public interface ICrawlStateStore : IAsyncDisposable
{
    /// <summary>
    /// Gets a value indicating whether this store actually persists anything.
    ///
    /// False for the null store. The engine reads it to decide whether to offer
    /// change detection and delete sweeping at all, rather than calling methods
    /// that will silently do nothing and then reporting counts of zero as though
    /// they were measurements.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>Registers the connection and opens a run.</summary>
    /// <param name="connection">Which connection, and what the dashboard should call it.</param>
    /// <param name="requested">The mode the operator asked for.</param>
    /// <param name="dryRun">True when nothing will be written to Graph.</param>
    /// <param name="fullEveryHours">How stale a full crawl may get before another is due.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The run's ID and what the store believes about it.</returns>
    /// <remarks>
    /// May return a mode other than the one requested. An incremental run with
    /// no checkpoint to start from is escalated to full, because reading from
    /// the beginning of time while telling the sweep this was a partial read is
    /// the one combination that produces a wrong answer rather than a slow one.
    /// </remarks>
    Task<CrawlRunStart> BeginRunAsync(
        CrawlConnectionInfo connection,
        CrawlMode requested,
        bool dryRun,
        int fullEveryHours,
        CancellationToken cancellationToken);

    /// <summary>
    /// Compares the hash framing this build produces against the one that
    /// produced the hashes on record, and adopts this build's on the way past.
    /// </summary>
    /// <param name="connectionId">The connection whose hashes are in question.</param>
    /// <param name="hashVersion">This build's <see cref="ItemHasher.HashVersion"/>.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>True when the version moved, and every stored hash is therefore stale.</returns>
    /// <remarks>
    /// Called once, before the run opens, because the answer decides what kind
    /// of run it should be: a changed framing makes every stored hash useless,
    /// and an incremental run against useless hashes rewrites the whole corpus
    /// while reporting a routine success. Escalating to full costs the same
    /// writes and says what it is doing.
    ///
    /// TRUE IS RETURNED EXACTLY ONCE PER CHANGE. The store adopts the new
    /// version as it reports it, so a caller that ignores the answer has spent
    /// the only notice it was going to get. That is deliberate: reporting it
    /// again next run would rewrite a corpus the first run already rewrote.
    ///
    /// A connection the store has never seen returns false. Its first run
    /// writes everything regardless, and reporting a migration there would be
    /// reporting one that is not happening.
    /// </remarks>
    Task<bool> CheckHashVersionAsync(
        string connectionId,
        int hashVersion,
        CancellationToken cancellationToken);

    /// <summary>Looks up what is on record for a batch of item IDs.</summary>
    /// <param name="itemIds">The IDs about to be considered.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The known state, keyed by item ID. IDs with no row are absent, not null.</returns>
    /// <remarks>
    /// Absent means new, and new means write. That is the correct default for
    /// anything this store has never seen, and it is why the result is a
    /// dictionary of what IS known rather than one entry per requested ID.
    /// </remarks>
    Task<IReadOnlyDictionary<string, CrawlItemState>> GetItemStatesAsync(
        IReadOnlyCollection<string> itemIds,
        CancellationToken cancellationToken);

    /// <summary>Records items Graph has confirmed written.</summary>
    /// <param name="items">What was written, with the hashes as sent.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task for the operation.</returns>
    Task RecordWrittenAsync(
        IReadOnlyCollection<CrawlItemState> items,
        CancellationToken cancellationToken);

    /// <summary>Marks items seen without writing them.</summary>
    /// <param name="itemIds">Items whose hashes matched what is on record.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task for the operation.</returns>
    /// <remarks>
    /// THIS IS NOT OPTIONAL AND IT IS NOT BOOKKEEPING. An unchanged item is not
    /// written to Graph - that is the entire point of change detection - but it
    /// must still be marked seen, or the delete sweep will conclude the source
    /// stopped returning it and remove it from the index. Skipping the write and
    /// skipping the mark are one line apart in the engine and produce opposite
    /// outcomes: the first is the optimisation, the second empties the corpus
    /// one run at a time, silently, starting with whatever changes least.
    /// </remarks>
    Task RecordUnchangedAsync(
        IReadOnlyCollection<string> itemIds,
        CancellationToken cancellationToken);

    /// <summary>Asks which items the source stopped returning.</summary>
    /// <param name="maxDeletePercent">
    /// The guard. A sweep proposing to remove more than this share of the live
    /// corpus is refused rather than performed.
    /// </param>
    /// <param name="overrideGuard">True to proceed anyway. An operator decision, never a retry.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The items to delete, including any a previous run failed to remove.</returns>
    /// <remarks>
    /// Valid only after a FULL run that enumerated to the end without throwing,
    /// and the store enforces both - it refuses for an incremental run outright.
    /// The percentage guard is the second lock, and it is aimed at a correct
    /// full run that read the wrong thing: a dropped view, a revoked permission,
    /// a filter that matched nothing, a source restored to last month. All four
    /// present identically as a clean run that read too little, and all four
    /// would otherwise sweep the difference out of the index.
    /// </remarks>
    Task<IReadOnlyList<CrawlDeletion>> GetPendingDeletesAsync(
        double maxDeletePercent,
        bool overrideGuard,
        CancellationToken cancellationToken);

    /// <summary>Records deletions Graph has confirmed.</summary>
    /// <param name="itemIds">The items removed. Include 404s - an item Graph says is absent is absent.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task for the operation.</returns>
    Task ConfirmDeletesAsync(
        IReadOnlyCollection<string> itemIds,
        CancellationToken cancellationToken);


    /// <summary>Lists every live item ID the index holds for this connection.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The IDs, ascending. Empty when the store is disabled.</returns>
    /// <remarks>
    /// Read-only, and it exists for the dry-run delete preview. The sweep's own
    /// GetPendingDeletesAsync cannot answer that question: it mutates - moving
    /// rows to pending and stamping PendingSinceUtc - and it deliberately returns
    /// nothing on a dry run, because a dry run records no item state and every
    /// item would therefore look unseen. So the preview is computed the other way
    /// round, by diffing what the source yielded against what this returns.
    ///
    /// Items already pending delete are excluded. They are reported by the sweep
    /// as a retry, and counting them here would show one item twice to somebody
    /// trying to judge whether the number is alarming.
    /// </remarks>
    Task<IReadOnlyList<string>> GetLiveItemIdsAsync(CancellationToken cancellationToken);
    /// <summary>Reads where the last run got to.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The marker, or null when there is none and a full read is required.</returns>
    Task<CrawlMarker?> GetCheckpointAsync(CancellationToken cancellationToken);

    /// <summary>Advances the checkpoint.</summary>
    /// <param name="marker">The position confirmed written.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task for the operation.</returns>
    /// <remarks>
    /// The store refuses to move a marker backwards. That is not distrust of the
    /// caller: it is what makes two runs overlapping - an operator running the
    /// tool by hand while the scheduled one is still going - lose nothing
    /// instead of resetting the slower one's progress.
    /// </remarks>
    Task SaveCheckpointAsync(CrawlMarker marker, CancellationToken cancellationToken);

    /// <summary>Reads cached identity mappings.</summary>
    /// <param name="sourceType">The principal family: AdGroup, PosixGroup, RangerGroup, Upn.</param>
    /// <param name="sourceKeys">The identifiers to look up.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The unexpired answers. A key absent from the result is a cache miss.</returns>
    Task<IReadOnlyDictionary<string, PrincipalGrant>> ResolvePrincipalsAsync(
        string sourceType,
        IReadOnlyCollection<string> sourceKeys,
        CancellationToken cancellationToken);

    /// <summary>Caches one identity mapping, including a negative one.</summary>
    /// <param name="grant">The answer. A null object ID is stored, not discarded.</param>
    /// <param name="sourceType">The principal family.</param>
    /// <param name="ttl">How long this answer may be reused.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task for the operation.</returns>
    Task CachePrincipalAsync(
        PrincipalGrant grant,
        string sourceType,
        TimeSpan ttl,
        CancellationToken cancellationToken);

    /// <summary>Buffers one throttling event for the run's record.</summary>
    /// <param name="throttle">What happened.</param>
    /// <remarks>
    /// Synchronous and in memory by design. Writing this when it happens would
    /// put a database round trip inside the write loop's catch block, on
    /// precisely the run that is already struggling. The buffer is flushed when
    /// the run closes.
    /// </remarks>
    void RecordThrottle(ThrottleEvent throttle);

    /// <summary>Closes the run as succeeded.</summary>
    /// <param name="totals">What the run did.</param>
    /// <param name="byType">The same, per kind of item.</param>
    /// <param name="timing">Where the time went, persisted alongside.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task for the operation.</returns>
    Task CompleteRunAsync(
        RunTotals totals,
        IReadOnlyCollection<ItemTypeTotals> byType,
        PushTiming timing,
        CancellationToken cancellationToken);

    /// <summary>Closes the run as failed, keeping the counters it managed.</summary>
    /// <param name="errorKind">A short stable token, for indexing in the runbook.</param>
    /// <param name="errorMessage">A message for a person. Never a property value or row content.</param>
    /// <param name="totals">What the run did before it stopped.</param>
    /// <param name="byType">The same, per kind of item.</param>
    /// <param name="timing">Where the time went.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task for the operation.</returns>
    /// <remarks>
    /// The counters are recorded on the failure path too. A run that died after
    /// nine hundred of a thousand items wrote nine hundred items, and a failure
    /// row full of zeroes invites the reader to conclude nothing happened.
    /// </remarks>
    Task FailRunAsync(
        string errorKind,
        string errorMessage,
        RunTotals totals,
        IReadOnlyCollection<ItemTypeTotals> byType,
        PushTiming timing,
        CancellationToken cancellationToken);
}
