// ---------------------------------------------------------------------------
// PushEngine.RunLifecycle.cs
// Opening a run, and turning its counters into what the store records.
//
// Split out of PushEngine.cs, which had grown past two thousand lines and held
// four separable concerns in one scroll. This is the first: the decision about
// what KIND of run this is - full or incremental, escalated or as asked - and
// the arithmetic that turns a PushSummary into RunTotals at the end.
//
// A partial class rather than a helper type, deliberately. These methods read
// this.options, this.store, this.log and this.crawlMode; extracting them into a
// collaborator would mean passing four fields through a constructor to buy
// nothing but the appearance of decomposition. The file boundary is for the
// reader; the class boundary would be for a design that does not exist.
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
    /// <summary>Registers the connection with the state store and opens a run.</summary>
    /// <param name="context">What the connector needs to open its source.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What the store said about this run.</returns>
    /// <remarks>
    /// Also decides the mode, and the decision is deliberately the store's
    /// rather than the operator's. A connector may ask for an incremental run;
    /// the store escalates it to full when there has never been a successful
    /// full crawl, when the last one has aged out, or when there is no
    /// checkpoint to start from. That third case is the one worth naming: an
    /// incremental read with no marker reads from the beginning of time, which
    /// is a full crawl that has told the delete sweep it was not one.
    ///
    /// The resume marker is put on the context here, before the source is
    /// created, which is the only moment a connector can act on it.
    /// </remarks>
    private async Task<CrawlRunStart> OpenRunAsync(
        PushSourceContext context, CancellationToken cancellationToken)
    {
        CrawlMode requested = this.options.Setting("Incremental", false)
            ? CrawlMode.Incremental
            : CrawlMode.Full;

        // Asked before the run opens, because the answer changes what kind of
        // run this should be. A changed hash framing makes every stored hash
        // stale at once, and an incremental run against stale hashes rewrites
        // the whole corpus while reporting an ordinary success - the same cost
        // as a full crawl, with none of the explanation. Escalating says what is
        // happening in the run's own mode.
        //
        // The store reports this exactly once and adopts the new version as it
        // does, so acting on it here is the only chance to act on it at all.
        if (await this.store.CheckHashVersionAsync(
                this.options.Graph.ConnectionId, ItemHasher.HashVersion, cancellationToken))
        {
            requested = CrawlMode.Full;
        }

        var connection = new CrawlConnectionInfo(
            this.options.Graph.ConnectionId,
            this.connector.Key,
            this.connector.DisplayName,
            this.options.Setting("ExpectedIntervalMinutes", 0) > 0
                ? this.options.Setting("ExpectedIntervalMinutes", 0)
                : null);

        CrawlRunStart run = await this.store.BeginRunAsync(
            connection,
            requested,
            this.dryRun,
            this.options.Setting("FullEveryHours", DefaultFullEveryHours),
            cancellationToken);

        this.crawlMode = run.Mode;

        if (run.AbandonedRunsReaped > 0)
        {
            this.log.Warning(
                "{Count} previous run(s) were closed as abandoned. Those processes stopped without reporting; " +
                "check whether the host is being restarted mid-crawl.",
                run.AbandonedRunsReaped);
        }

        if (requested == CrawlMode.Incremental && run.Mode == CrawlMode.Full)
        {
            this.log.Information(
                "An incremental run was requested; reading in full instead. " +
                "Last successful full crawl: {LastFull}.",
                run.LastFullSuccessUtc?.ToString("o") ?? "never");
        }

        if (run.Mode == CrawlMode.Incremental)
        {
            context.ResumeFrom = await this.store.GetCheckpointAsync(cancellationToken);
        }

        return run;
    }

    /// <summary>Collects the run's totals for the state store.</summary>
    /// <param name="summary">The run's counters.</param>
    /// <returns>The totals, in the shape the store records.</returns>
    private RunTotals Totals(PushSummary summary)
    {
        return new RunTotals(
            summary.Total + summary.Unchanged + summary.Skipped,
            summary.Total,
            summary.Unchanged,
            summary.Deleted,
            summary.Skipped,
            summary.Duplicates,
            summary.Failed,
            summary.ThrottleWaits,
            summary.Batches,
            summary.BytesWritten);
    }

    /// <summary>Shortens a message to what the store's column can hold.</summary>
    /// <param name="text">The message.</param>
    /// <param name="limit">The column's width.</param>
    /// <returns>The message, cut on a character boundary, with an ellipsis when cut.</returns>
    private static string Truncate(string text, int limit)
    {
        return text.Length <= limit ? text : text.Substring(0, limit - 3) + "...";
    }
}
