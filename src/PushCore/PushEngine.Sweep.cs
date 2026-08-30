// ---------------------------------------------------------------------------
// PushEngine.Sweep.cs
// Removing from the index what the source stopped returning.
//
// Split out of PushEngine.cs. This is the part of the engine that DELETES, and
// keeping it in its own file is worth more than the line count suggests: a
// reader auditing "what could remove a customer's data from the index" now has
// one file to read rather than a region of a very long one.
//
// Everything here is guarded by the same two preconditions, and both are in
// SweepDeletedItemsAsync rather than spread about: a sweep runs only after a
// FULL crawl that completed without throwing, because absence from a partial
// read means nothing at all. The delete guard and the dry-run preview are the
// other two halves.
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
    /// <summary>Removes items the source has stopped returning.</summary>
    /// <param name="summary">The run's counters.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task for the operation.</returns>
    /// <remarks>
    /// The second of the ten agent features, and the one that can do the most
    /// damage if it is wrong, so it is fenced on four sides.
    ///
    /// It runs only after a FULL crawl that enumerated to the end without
    /// throwing - the caller reaches it only on that path, and the store refuses
    /// an incremental RunId outright rather than trusting that. Absence from a
    /// partial read means nothing at all.
    ///
    /// It runs only with a state store. Without one there is no inventory to
    /// diff against, and "the source returned fewer items than I remember" is
    /// not a sentence a run with no memory can say.
    ///
    /// The store's percentage guard refuses a sweep that would remove more than
    /// Settings:MaxDeletePercent of the live corpus. That guard is aimed at a
    /// CORRECT full run that read the wrong thing: a dropped view, a revoked
    /// permission, a filter that matched nothing, a source restored to last
    /// month. All four present identically as a clean run that read too little.
    ///
    /// And a delete Graph refuses is left pending rather than forgotten, so it
    /// is retried on the next run. A 404 counts as success: an item Graph says
    /// is not there is not there, and treating that as a failure would keep it
    /// in the pending list for ever.
    ///
    /// The source is never consulted. It is not asked whether a record was
    /// deleted and it needs no soft-delete column - see docs/SOURCE-CONTRACT.md.
    /// A hard DELETE, a row falling out of the query, an archived record and a
    /// permission change that hides it are all "the source stopped returning
    /// it", which is the only question being asked.
    /// </remarks>
    private async Task SweepDeletedItemsAsync(PushSummary summary, CancellationToken cancellationToken)
    {
        if (!this.store.IsEnabled)
        {
            return;
        }

        if (this.crawlMode != CrawlMode.Full)
        {
            this.log.Debug("Incremental run; no delete sweep. Absence from a partial read means nothing.");
            return;
        }

        double guard = this.options.Setting("MaxDeletePercent", DefaultMaxDeletePercent);
        bool overrideGuard = this.options.Setting("OverrideDeleteGuard", false);

        if (overrideGuard)
        {
            this.log.Warning(
                "Settings:OverrideDeleteGuard is set. The {Guard}% delete guard is disabled for this run, " +
                "so a source that returned too few rows will have the difference removed from the index.",
                guard);
        }


        // A DRY RUN PREVIEWS THE SWEEP RATHER THAN SKIPPING IT. This is the half
        // of a preview that matters: a wrong write is additive and corrected by
        // the next run, while a sweep takes items out of the index and a search
        // stops answering. Being able to see the list first is the whole point of
        // running a crawl dry.
        //
        // It cannot go through GetPendingDeletesAsync. That procedure mutates -
        // it moves rows to pending and stamps PendingSinceUtc - and it returns
        // nothing at all on a dry run, deliberately, because a dry run records no
        // LastSeenRunId and every item would otherwise look unseen. Asking it
        // here would either corrupt the store or answer "none", and "none" is the
        // most dangerous wrong answer this code could give.
        //
        // So the diff runs the other way: what the source yielded, against what
        // the index holds.
        if (this.dryRun)
        {
            await this.PreviewDeleteSweepAsync(guard, overrideGuard, cancellationToken);
            return;
        }
        IReadOnlyList<CrawlDeletion> pending =
            await this.store.GetPendingDeletesAsync(guard, overrideGuard, cancellationToken);

        if (pending.Count == 0)
        {
            return;
        }

        this.log.Information(
            "Delete sweep: {Count} item(s) the source no longer returns will be removed from the index.",
            pending.Count);

        var confirmed = new List<string>(pending.Count);

        // BATCHED WHEN BATCHING IS ON, one at a time when it is not. The sweep
        // was the last caller still paying a round trip per item: 412 pending
        // deletions cost 412 calls, where the batch writer the engine already
        // owned would have made it 21.
        //
        // The single-item path stays reachable and is not dead code. It is what
        // Settings:Batch = false selects, which is the first thing to try when a
        // run starts failing in a way batching could explain - and a sweep is
        // exactly when somebody wants that lever.
        GraphBatchWriter? writer = this.ResolveBatchWriter(summary);

        if (writer is not null)
        {
            var byId = new Dictionary<string, string>(pending.Count, StringComparer.OrdinalIgnoreCase);

            foreach (CrawlDeletion deletion in pending)
            {
                byId[deletion.ItemId] = deletion.ItemType;
            }

            BatchWriteResult result = await writer.DeleteAsync(
                pending.Select(deletion => deletion.ItemId).ToList(), cancellationToken);

            // Reported because otherwise nothing distinguishes a batched sweep
            // from the per-item one it replaced. The counters are identical
            // either way - same deletions, same failures - so the round trips
            // are the only observable difference, and a change justified by them
            // that does not print them cannot be checked after the fact.
            this.log.Information(
                "Delete sweep: {Count} deletion(s) sent in {RoundTrips} $batch round trip(s).",
                pending.Count,
                result.RoundTrips);

            foreach (BatchItemResult item in result.Written)
            {
                confirmed.Add(item.ItemId);

                // The type comes from the store's own row rather than being
                // inferred, so the per-type breakdown of a sweep matches the
                // per-type breakdown of the crawl that created those items.
                summary.CountDeleted(byId[item.ItemId]);
            }

            if (result.FailedCount > 0)
            {
                // Logged per item, not just counted. A sweep that half-worked is
                // a list of items still answering searches for records that are
                // gone, and "37 failed" does not tell anybody which.
                foreach (BatchItemResult item in result.Failed)
                {
                    this.log.Error(
                        "Delete of {ItemId} failed with status {Status}. It stays pending and will be retried.",
                        item.ItemId,
                        item.StatusCode);
                }
            }
        }
        else
        {
            foreach (CrawlDeletion deletion in pending)
            {
                if (await this.TryDeleteAsync(deletion.ItemId, summary, cancellationToken))
                {
                    confirmed.Add(deletion.ItemId);
                    summary.CountDeleted(deletion.ItemType);
                }
            }
        }

        if (confirmed.Count > 0)
        {
            await this.store.ConfirmDeletesAsync(confirmed, cancellationToken);
        }

        if (confirmed.Count < pending.Count)
        {
            // Left pending on purpose. The next run retries them, and
            // crawl.vwPendingDeletes shows anything that keeps failing - which
            // is an item still answering searches for a record that is gone.
            this.log.Warning(
                "{Failed} of {Total} deletions were refused and remain pending. They will be retried next run; " +
                "until then those items still answer searches.",
                pending.Count - confirmed.Count,
                pending.Count);
        }
    }

    /// <summary>Reports what a real sweep would delete, without touching anything.</summary>
    /// <param name="guard">The MaxDeletePercent this run would apply.</param>
    /// <param name="overrideGuard">Whether the run is configured to ignore that guard.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task for the operation.</returns>
    /// <remarks>
    /// The guard is evaluated here as well as reported. An operator running this
    /// to decide whether a sweep is safe needs to know both numbers - how many
    /// items would go, and whether the store would refuse to let them - because
    /// "412 items" and "412 items, and the run will be refused" call for opposite
    /// actions. Computing the percentage the same way the store does is what
    /// makes the preview worth trusting; if the two ever disagree, the preview is
    /// the one that is wrong and this comment is where to start.
    /// </remarks>
    private async Task PreviewDeleteSweepAsync(
        double guard, bool overrideGuard, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> live = await this.store.GetLiveItemIdsAsync(cancellationToken);

        if (live.Count == 0)
        {
            // Absent and empty are different, and saying which is which is the
            // difference between "nothing would be deleted" and "this preview
            // could not see the index".
            this.log.Information(
                "Delete preview: the index holds no live items for this connection, so nothing would be removed.");

            return;
        }

        HashSet<string> seen = this.dryRunSeenIds ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var wouldDelete = new List<string>();

        foreach (string itemId in live)
        {
            if (!seen.Contains(itemId))
            {
                wouldDelete.Add(itemId);
            }
        }

        if (wouldDelete.Count == 0)
        {
            this.log.Information(
                "Delete preview: the source returned every one of the {Live} live item(s). " +
                "A real run would delete nothing.",
                live.Count);

            return;
        }

        double percent = (double)wouldDelete.Count / live.Count * 100.0;
        bool refused = !overrideGuard && percent > guard;

        this.log.Warning(
            "Delete preview: a real run would remove {Count} of {Live} live item(s) ({Percent:F2}% of the corpus). " +
            "The guard is {Guard}%{Verdict}.",
            wouldDelete.Count,
            live.Count,
            percent,
            guard,
            refused
                ? ", so the sweep WOULD BE REFUSED and the run would exit 4"
                : overrideGuard ? ", and Settings:OverrideDeleteGuard is set, so it would proceed regardless"
                : ", so the sweep would proceed");

        // Capped, and the cap is announced. A corpus-wide false positive would
        // otherwise put 111,900 lines in front of somebody who needed the first
        // twenty to recognise the pattern.
        const int Sample = 20;

        foreach (string itemId in wouldDelete.Take(Sample))
        {
            this.log.Information("Would DELETE {ItemId}.", itemId);
        }

        if (wouldDelete.Count > Sample)
        {
            this.log.Information(
                "...and {Remaining} more not listed. Query crawl.vwItemInventory for the full set.",
                wouldDelete.Count - Sample);
        }
    }

    /// <summary>Deletes one item, with the same backoff a write gets.</summary>
    /// <param name="itemId">The item to remove.</param>
    /// <param name="summary">The run's counters.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>True when the item is gone from the index.</returns>
    /// <remarks>
    /// Unlike a write, a terminal failure here does NOT end the run. One item
    /// that cannot be deleted should not abandon the other nine hundred, and the
    /// store keeps it pending so nothing is lost by carrying on.
    /// </remarks>
    private async Task<bool> TryDeleteAsync(
        string itemId, PushSummary summary, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await this.graph.External.Connections[this.options.Graph.ConnectionId]
                    .Items[itemId].DeleteAsync(cancellationToken: cancellationToken);

                return true;
            }
            catch (ODataError ex) when (ex.ResponseStatusCode == 404)
            {
                // Already absent. That is the state we were asking for, so it
                // counts - anything else keeps it pending for ever.
                return true;
            }
            catch (ODataError ex) when (
                ex.ResponseStatusCode is 429 or 502 or 503 or 504 && attempt < MaxWriteAttempts)
            {
                TimeSpan wait = GraphThrottling.RetryAfter(ex) ?? GraphThrottling.Backoff(attempt);

                if (ex.ResponseStatusCode == 429)
                {
                    summary.CountThrottleWait();
                    this.store.RecordThrottle(new ThrottleEvent(
                        DateTime.UtcNow, 429, (int)wait.TotalSeconds, "delete", attempt));
                }

                await Task.Delay(wait, cancellationToken);
            }
            catch (ODataError ex)
            {
                this.log.Error(
                    "Delete of {ItemId} failed with status {Status}. It stays pending and will be retried.",
                    itemId,
                    ex.ResponseStatusCode);

                return false;
            }
        }
    }
}
