// ---------------------------------------------------------------------------
// PushEngine.Flush.cs
// The window, the chunks cut from it, and what happens to each one.
//
// Split out of PushEngine.cs. This is the hot path: a window is compared
// against the state store in one call, cut into chunks of the size a $batch can
// carry, written, and recorded - in that order, and the order is the whole
// design.
//
// Two invariants live here and nowhere else, which is the argument for the file:
//
//   The COMMIT PREFIX. A chunk that fails on its fifth item must still record
//   the four that landed, or the next sweep concludes the source dropped them
//   and deletes them from the index.
//
//   RECORDING FOLLOWS GRAPH, never precedes it. A hash stored before the write
//   is confirmed makes the next run see the item as unchanged and skip it, so
//   one failure becomes an item that is permanently stale and permanently
//   invisible.
//
// A partial class rather than a helper type - see PushEngine.RunLifecycle.cs.
// ---------------------------------------------------------------------------

using Connector.Security.Content;
using Connector.Security.Schema;
using Microsoft.Graph;
using Microsoft.Graph.Models.ExternalConnectors;
using Microsoft.Graph.Models.ODataErrors;
using PushCore.State;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Serilog;
using Serilog.Context;

namespace PushCore;

public sealed partial class PushEngine
{
    /// <summary>Resolves a window and publishes its write-chunks to the writers.</summary>
    /// <param name="queue">The channel the writers read.</param>
    /// <param name="window">The accumulated window, in source order.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task for the operation.</returns>
    /// <remarks>
    /// The lookup happens HERE, on the reading thread, and that is a deliberate
    /// move rather than a side effect. It was previously on the writer threads,
    /// once per twenty rows; doing it once per window instead takes a tenth of
    /// the round trips off the write path entirely, at the cost of the reader
    /// pausing on one store call per window. The reader was never the bottleneck
    /// - the timing table puts source read at 0.1% of per-row time - so that is
    /// a good trade, and it is stated here so the next person profiling a run
    /// knows where the call went.
    /// </remarks>
    private async Task PublishWindowAsync(
        ChannelWriter<WriteChunk> queue,
        List<Prepared> window,
        CancellationToken cancellationToken)
    {
        if (window.Count == 0)
        {
            return;
        }

        IReadOnlySet<string> unchanged = await this.ResolveUnchangedAsync(window, cancellationToken);

        foreach (List<Prepared> chunk in CutIntoWriteChunks(window))
        {
            await queue.WriteAsync(new WriteChunk(chunk, unchanged), cancellationToken);
        }
    }

    /// <summary>Looks a window up once, then writes it as chunks of twenty.</summary>
    /// <param name="source">The opened source.</param>
    /// <param name="window">The accumulated window, in source order.</param>
    /// <param name="summary">The run's counters.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task for the operation.</returns>
    private async Task FlushWindowAsync(
        IPushSource source,
        List<Prepared> window,
        PushSummary summary,
        CancellationToken cancellationToken)
    {
        if (window.Count == 0)
        {
            return;
        }

        IReadOnlySet<string> unchanged = await this.ResolveUnchangedAsync(window, cancellationToken);

        foreach (List<Prepared> chunk in CutIntoWriteChunks(window))
        {
            await this.FlushChunkAsync(source, chunk, unchanged, summary, cancellationToken);
        }
    }

    /// <summary>Asks the store, once, which of a window's items have not moved.</summary>
    /// <param name="window">Prepared rows, up to one lookup window's worth.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The IDs whose stored content and ACL hashes both still match.</returns>
    /// <remarks>
    /// This is the round trip the lookup window exists to amortise. It used to
    /// run inside FlushChunkAsync, once per twenty rows, because the chunk was
    /// both the lookup unit and the write unit; a 111,900-row crawl therefore
    /// made 5,595 lookups that each returned at most twenty rows. Asking once per
    /// window and handing the answer to the ten chunks that came out of it makes
    /// that 560 lookups for the same rows, and leaves what Graph is asked
    /// untouched.
    ///
    /// The returned set is shared by every chunk from the window and is never
    /// written to afterwards. That is safe precisely because it is keyed by item
    /// ID rather than by position, so a chunk reads only its own rows out of it.
    /// </remarks>
    private async Task<IReadOnlySet<string>> ResolveUnchangedAsync(
        List<Prepared> window, CancellationToken cancellationToken)
    {
        if (!this.store.IsEnabled || window.Count == 0)
        {
            return EmptyUnchanged;
        }

        // ONE CALL, NOT TWO PLUS ONE PER CHUNK. The store compares the hashes
        // where the data already is and returns only what has to be written, so
        // a steady-state window returns a handful of IDs instead of two hundred
        // rows - and it marks the rest seen while it is there, which used to be
        // a separate uspRecordUnchanged per write chunk.
        //
        // See sql/41 for why marking seen at compare time is safe: "seen"
        // answers only "did the source still return this item", and for an
        // unchanged item that is settled the moment its hashes match.
        IReadOnlySet<string> toWrite = await this.store.CompareAndSeeAsync(
            window.Select(prepared => new CrawlItemState(
                prepared.Mapped.Id,
                prepared.Mapped.ItemType,
                prepared.ContentHash,
                prepared.AclHash,
                prepared.ContentBytes,
                0)).ToList(),
            cancellationToken);

        // Inverted here rather than in the store, because everything downstream
        // is written against "unchanged" and rewriting all of it to think in the
        // opposite polarity would be a much larger change for no behavioural
        // difference.
        var unchanged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Prepared prepared in window)
        {
            if (!toWrite.Contains(prepared.Mapped.Id))
            {
                unchanged.Add(prepared.Mapped.Id);
            }
        }

        return unchanged;
    }

    /// <summary>Cuts a resolved window into the chunks a single write carries.</summary>
    /// <param name="window">The window, already looked up.</param>
    /// <returns>Lists of at most <see cref="WriteChunkSize"/> rows, in source order.</returns>
    /// <remarks>
    /// Source order is preserved across the cut and within each chunk, because
    /// the checkpoint rests on it: a chunk's marker is its last row's, and a
    /// window emitted out of order would let the watermark pass a row that had
    /// not been written.
    /// </remarks>
    private static List<List<Prepared>> CutIntoWriteChunks(List<Prepared> window)
    {
        var chunks = new List<List<Prepared>>((window.Count / WriteChunkSize) + 1);

        for (int offset = 0; offset < window.Count; offset += WriteChunkSize)
        {
            chunks.Add(window.GetRange(offset, Math.Min(WriteChunkSize, window.Count - offset)));
        }

        return chunks;
    }

    /// <summary>Decides what in this chunk actually needs writing, writes it, and commits.</summary>
    /// <param name="source">The opened source.</param>
    /// <param name="chunk">Prepared items, in the order the source yielded them.</param>
    /// <param name="summary">The run's counters.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task for the operation.</returns>
    /// <remarks>
    /// FOUR STEPS, AND THE ORDER IS THE WHOLE CORRECTNESS ARGUMENT.
    ///
    /// 1. Ask the store what it already holds for these IDs. One round trip for
    ///    the chunk, not one per item.
    ///
    /// 2. Write only the items whose content or ACL hash moved. This is the
    ///    saving: on a steady-state run most items are already correct, and the
    ///    write that is skipped is the expensive one.
    ///
    /// 3. Record what happened - AFTER Graph confirmed, never before. A hash
    ///    written ahead of the PUT means the next run sees the item as unchanged
    ///    and skips it, so one failure becomes an item that is permanently stale
    ///    and permanently invisible.
    ///
    ///    Both halves are recorded. Marking the unchanged items SEEN is not
    ///    bookkeeping: the delete sweep diffs on exactly that, so skipping the
    ///    mark would have the next full crawl conclude the source had dropped
    ///    every item that did not change and remove them from the index.
    ///
    /// 4. Count and commit in the order the source yielded, which is what the
    ///    watermark rests on. Anything that throws above this point leaves the
    ///    checkpoint where it was, because this step is simply not reached.
    ///
    /// The checkpoint moves once per chunk rather than once per item, using the
    /// last item's marker. Every item in the chunk has been confirmed by then,
    /// so the position is honest, and it costs one round trip instead of twenty.
    /// </remarks>
    private async Task FlushChunkAsync(
        IPushSource source,
        List<Prepared> chunk,
        IReadOnlySet<string> unchanged,
        PushSummary summary,
        CancellationToken cancellationToken)
    {
        if (chunk.Count == 0)
        {
            return;
        }
        if (this.dryRun)
        {
            foreach (Prepared prepared in chunk)
            {
                // WOULD WRITE, OR WOULD SKIP. The store was consulted for this
                // window - a read, which a dry run is allowed to make - so the
                // preview can now say which of the two an item would be rather
                // than calling every row a write.
                //
                // That distinction is the point of the item. On a steady-state
                // corpus almost nothing changes between runs, so a preview that
                // reported 111,900 writes when the real run would perform four
                // overstated the work by four orders of magnitude, and was
                // useless for the one question it gets asked: how long will
                // this take and how much will it touch.
                bool skip = unchanged.Contains(prepared.Mapped.Id);

                // Recorded for the delete preview at the end of the run. BOTH
                // arms, because an unchanged item is still an item the source
                // returned - recording only the writes would have the preview
                // announce that the sweep was about to delete the whole
                // unchanged corpus.
                (this.dryRunSeenIds ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase))
                    .Add(prepared.Mapped.Id);

                if (skip)
                {
                    summary.CountUnchanged(prepared.Mapped.ItemType);
                }
                else
                {
                    summary.Count(prepared.Mapped.ItemType);
                }

                // Item ID, type and sizes only. The content is customer data and
                // does not go to the console any more than it goes to the log.
                this.log.Information(
                    "Would {Verb} {ItemId} ({ItemType}): {PropertyCount} properties, {ContentBytes} content bytes, " +
                    "{AclCount} ACL entr(y/ies).",
                    skip ? "SKIP" : "write",
                    prepared.Mapped.Id,
                    prepared.Mapped.ItemType,
                    prepared.Mapped.Properties.Count,
                    prepared.ContentBytes,
                    prepared.Item.Acl?.Count ?? 0);

                // Measured like any other row. A dry run writes nothing, so what
                // it reports IS the whole non-Graph cost of the pipeline - the
                // cheapest way to find out how much of a slow run is not Graph's
                // fault, and it needs no tenant at all.
                summary.Timing.RowTotal.Add(PushTiming.MicrosecondsSince(prepared.StartedAt));
            }

            // No commit callbacks and no state recorded: a dry run writes
            // nothing, so it must leave both the watermark and the store exactly
            // where it found them. Reading the hashes above changed nothing;
            // uspGetItemState is a SELECT.
            return;
        }

        // 1. What is already on record for these items?
        // Resolved once per WINDOW by ResolveUnchangedAsync and handed in, not
        // looked up here. This method used to make the round trip itself, which
        // is what tied the store's granularity to Graph's twenty.

        // 2. Write what moved, in the order the source yielded, remembering how
        //    far the chunk actually got.
        //
        //    THE PREFIX IS THE WHOLE POINT. A failure on the fifth item of
        //    twenty must not discard the four that landed - they are in the
        //    index, and a watermark that pretended otherwise would have the next
        //    run re-read them, which is merely wasteful, while a store that
        //    pretended otherwise would have the next SWEEP delete them, which is
        //    not. So the walk stops at the failure and everything before it is
        //    recorded and committed exactly as though the chunk had ended there.
        int landed = 0;
        Exception? failure = null;
        var refused = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        List<Prepared> toWrite = chunk
            .Where(prepared => !unchanged.Contains(prepared.Mapped.Id))
            .ToList();

        GraphBatchWriter? batch = this.ResolveBatchWriter(summary);

        if (batch is not null && toWrite.Count > 1)
        {
            // One round trip for up to twenty items instead of twenty. A batch
            // can return 200 overall while individual sub-responses carry a
            // refusal, so the result is per item and one refused item does not
            // abandon the other nineteen - which is the behaviour that makes
            // batching worth having rather than merely faster.
            BatchWriteResult result = await batch.WriteAsync(
                toWrite.Select(prepared => (prepared.Mapped.Id, prepared.Item)).ToList(),
                cancellationToken);

            for (int round = 0; round < result.RoundTrips; round++)
            {
                summary.CountBatch();
            }

            foreach (BatchItemResult item in result.Failed)
            {
                refused.Add(item.ItemId);
            }

            // The commit prefix ends at the first refusal in yielded order. Items
            // after it may well have landed and are recorded as such, but the
            // source's marker must not pass a gap.
            landed = chunk.FindIndex(prepared => refused.Contains(prepared.Mapped.Id));
            landed = landed < 0 ? chunk.Count : landed;

            if (result.FailedCount > 0)
            {
                this.log.Warning(
                    "{Failed} of {Total} items in this batch were refused: {Detail}",
                    result.FailedCount,
                    result.Items.Count,
                    result.Describe());
            }
        }
        else
        {
            // One at a time: the original path, and the only one a chunk of one
            // ever takes. A terminal refusal here throws and ends the run, which
            // is the pre-existing contract for a single write.
            try
            {
                foreach (Prepared prepared in chunk)
                {
                    if (!unchanged.Contains(prepared.Mapped.Id))
                    {
                        await this.WriteWithRetryAsync(
                            prepared.Mapped.Id, prepared.Item, summary, cancellationToken);
                    }

                    landed++;
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }

        // Recording the prefix must survive the cancellation a failure triggers
        // in the sibling writers, or the run loses its record of items that are
        // genuinely in the index - the one thing the store exists to prevent.
        // Bounded work: at most two calls over at most one lookup chunk of rows.
        CancellationToken recording = failure is null ? cancellationToken : CancellationToken.None;
        List<Prepared> confirmed = landed == chunk.Count ? chunk : chunk.GetRange(0, landed);

        // 3. Record, now that Graph has confirmed.
        //
        //    Recorded from what LANDED, not from the commit prefix. The store is
        //    keyed by item ID and knows nothing about order, so an item written
        //    after a refusal is genuinely in the index and must be recorded as
        //    such - otherwise the next sweep sees it unseen and deletes it. Only
        //    the source's marker cares about the prefix.
        List<Prepared> stored = chunk
            .Where(prepared => !refused.Contains(prepared.Mapped.Id))
            .ToList();

        if (failure is not null)
        {
            stored = confirmed;
        }

        if (this.store.IsEnabled && stored.Count > 0)
        {
            List<CrawlItemState> justWritten = stored
                .Where(prepared => !unchanged.Contains(prepared.Mapped.Id))
                .Select(prepared => new CrawlItemState(
                    prepared.Mapped.Id,
                    prepared.Mapped.ItemType,
                    prepared.ContentHash,
                    prepared.AclHash,
                    prepared.ContentBytes,
                    0))
                .ToList();

            if (justWritten.Count > 0)
            {
                await this.store.RecordWrittenAsync(justWritten, recording);
            }

            // No RecordUnchangedAsync here any more. CompareAndSeeAsync marked
            // every unchanged item in the window seen when it decided they were
            // unchanged, which removed one round trip per write chunk - 5,595 of
            // them on a full crawl of this corpus.
            //
            // The commit prefix above still governs what is recorded as WRITTEN,
            // and that has not moved: a hash stored before Graph confirms the
            // write makes the next run skip a stale item for ever.
        }

        foreach (Prepared prepared in chunk.Where(p => refused.Contains(p.Mapped.Id)))
        {
            // Counted, not thrown. crawl.Run.ItemsFailed carries it and the
            // dashboard shows it, so a run that wrote 1,117 of 1,118 reports
            // exactly that rather than reporting success or dying outright.
            summary.CountFailed(prepared.Mapped.ItemType);
        }

        // 4. Count what LANDED - not the commit prefix.
        //
        //    These two differ whenever a batch refused something in the middle,
        //    and counting the prefix would have the run under-report its own
        //    work: nineteen items reach Graph, RecordWrittenAsync writes
        //    nineteen item rows, and the run row would claim four. The same
        //    database disagreeing with itself is worse than either number.
        foreach (Prepared prepared in stored)
        {
            if (unchanged.Contains(prepared.Mapped.Id))
            {
                summary.CountUnchanged(prepared.Mapped.ItemType);
                this.log.Debug(
                    "Unchanged {ItemId} ({ItemType}); already correct in the index.",
                    prepared.Mapped.Id,
                    prepared.Mapped.ItemType);
            }
            else
            {
                int total = summary.Count(prepared.Mapped.ItemType);
                summary.CountBytes(prepared.Mapped.ItemType, prepared.ContentBytes);

                // Debug, not Information, for two reasons that point the same
                // way. The runbook already documents the per-item line as what
                // raising the level to Debug BUYS you. And at Information it is
                // a console write plus a file write per row, on the critical
                // path of every row, for a line nobody reads on a healthy run.
                this.log.Debug("Indexed {ItemId} ({ItemType}).", prepared.Mapped.Id, prepared.Mapped.ItemType);

                if (total % ProgressEvery == 0)
                {
                    this.log.Information("Indexed {Count} items so far.", total);
                }
            }

            summary.Timing.RowTotal.Add(PushTiming.MicrosecondsSince(prepared.StartedAt));
        }

        // 5. Move the marker, over the unbroken prefix only, and never once this
        //    run has left a gap behind it.
        if (!this.markerBlocked)
        {
            foreach (Prepared prepared in confirmed)
            {
                long commitStarted = PushTiming.Now();
                await source.OnItemCommittedAsync(prepared.Mapped, recording);
                summary.Timing.Commit.Add(PushTiming.MicrosecondsSince(commitStarted));
            }
        }

        if (!this.markerBlocked && confirmed.Count < chunk.Count)
        {
            // Set after this chunk's own prefix has been committed, so the
            // marker still reaches the last good item before the gap.
            this.markerBlocked = true;

            this.log.Warning(
                "An item in this chunk was not written, so the checkpoint stops here for the rest of the run. " +
                "Later items are still indexed and recorded; the next run resumes from before the gap and " +
                "retries what was refused. Re-reading what was already written costs time and nothing else.");
        }

        // The checkpoint, once, from the last item that carried a marker. Every
        // item in the prefix is confirmed by the time this runs, so the position
        // is honest; doing it per item would put a database round trip beside
        // every Graph write for a value only the next run reads.
        if (this.store.IsEnabled && confirmed.Count > 0 && !this.markerBlocked)
        {
            Prepared? marked = confirmed
                .Where(prepared => prepared.Mapped.LastModifiedUtc.HasValue)
                .Cast<Prepared?>()
                .LastOrDefault();

            if (marked is not null)
            {
                await this.store.SaveCheckpointAsync(
                    new CrawlMarker(marked.Value.Mapped.LastModifiedUtc!.Value, marked.Value.Mapped.Id),
                    recording);
            }
        }

        if (failure is not null)
        {
            // Rethrown with its stack intact, after everything that landed has
            // been recorded. The run still fails; it just does not lie about
            // what reached the index before it did.
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
