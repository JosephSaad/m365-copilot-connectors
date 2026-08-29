// ---------------------------------------------------------------------------
// PushEngine.cs
// Everything that is the same whatever the source is.
//
// Create the connection, register the schema and wait for Ready, open the
// source, take each item it yields, truncate, attach the ACL, PUT with backoff,
// count, and tell the source what landed. A connector supplies a schema and a
// source, and none of this.
//
// Three behaviours are worth calling out because they are policy, not mechanics:
//
//   * Nothing here logs a property value or content. Item ID, item type and
//     byte counts only. The row is customer data and the log is a file on a
//     server that a wider group can read than can read the database.
//
//   * A push never deletes. An item excluded from the source - soft deleted,
//     filtered out, outside MaxItems - leaves its item in the index. That is a
//     property of this model rather than an oversight, and
//     deploy/Compare-SourceToIndex.ps1 is how the orphans are found.
//
//   * The source is told an item counted only after the PUT for it returned.
//     This is the unbreakable rule made structural: a run that throws simply
//     stops calling OnItemCommittedAsync, so a watermark cannot move past
//     something the index does not have. A dry run never calls it at all.
// ---------------------------------------------------------------------------

namespace PushCore;

using Connector.Security.Content;
using Connector.Security.Schema;
using Microsoft.Graph;
using Microsoft.Graph.Models.ExternalConnectors;
using Microsoft.Graph.Models.ODataErrors;
using PushCore.State;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Serilog;

/// <summary>Runs one connector against one connection.</summary>
public sealed class PushEngine
{
    private const int MaxWriteAttempts = 5;

    // Four is Microsoft's own default in its published connector template, and
    // the number it recommends starting from. Sixteen is the ceiling because the
    // connectors API limits an application to 25 concurrent operations on a
    // connection and this run makes others of its own.
    private const int DefaultWriters = 4;
    private const int MaxWriters = 16;

    // How often a run says it is still moving, in items. Frequent enough that a
    // stalled run is obvious, rare enough that the line is not on the per-row
    // critical path.
    private const int ProgressEvery = 250;

    // How many items are prepared before the engine asks the state store what it
    // already has and writes what moved.
    //
    // Twenty, because that is Graph's hard ceiling on a $batch and there is no
    // value in a chunk larger than the largest request that can carry it. It is
    // also the granularity of the state store's round trips: one lookup and at
    // most two recording calls per chunk, rather than three per item.
    private const int ChunkSize = 20;

    // The share of the live corpus a single sweep may remove before the state
    // store refuses it. Ten percent, because a real day's deletions in a
    // ticketing or engagement corpus are a fraction of that, and because a guard
    // is only useful if it fires before the damage rather than after.
    private const int DefaultMaxDeletePercent = 10;

    // How stale a full crawl may get before the store insists on another,
    // whatever the connector asked for. Weekly, matching the runbook's default
    // for the agent-hosted path - a full crawl is what re-establishes the
    // baseline every incremental read is a delta against.
    private const int DefaultFullEveryHours = 168;

    // crawl.Run.ErrorMessage is NVARCHAR(2000).
    private const int MaxStoredErrorLength = 2000;

    private readonly IPushConnector connector;
    private readonly PushOptions options;
    private readonly GraphServiceClient graph;
    private readonly ILogger log;
    private readonly bool dryRun;
    private readonly ICrawlStateStore store;

    private List<Acl>? sharedAcl;
    private GraphBatchWriter? batchWriter;

    // What kind of read this run is making. Full unless the state store said an
    // incremental one was safe, and consulted by exactly one thing: the delete
    // sweep, which is valid after a full crawl and meaningless after a partial
    // one. Defaulting to Full is the safe direction - it makes the sweep ASK the
    // store, and the store refuses anything it should not answer.
    private CrawlMode crawlMode = CrawlMode.Full;

    /// <summary>Initializes a new instance of the <see cref="PushEngine"/> class.</summary>
    /// <param name="connector">The source description.</param>
    /// <param name="options">Validated configuration.</param>
    /// <param name="graph">An authenticated Graph client.</param>
    /// <param name="log">Where to report progress.</param>
    /// <param name="dryRun">When true, reads and maps but writes nothing.</param>
    /// <param name="store">
    /// Durable crawl state, or null for the null store - which is what every
    /// deployment had before this existed and still gets when no state database
    /// is configured. Without it the engine writes every item every run and
    /// never deletes, because a run with no memory cannot know an item was
    /// already correct and must not conclude anything about what is missing.
    /// </param>
    public PushEngine(
        IPushConnector connector,
        PushOptions options,
        GraphServiceClient graph,
        ILogger log,
        bool dryRun,
        ICrawlStateStore? store = null)
    {
        this.connector = connector;
        this.options = options;
        this.graph = graph;
        this.log = log;
        this.dryRun = dryRun;
        this.store = store ?? NullCrawlStateStore.Instance;
    }

    /// <summary>Creates the connection and schema if needed, then pushes every item.</summary>
    /// <param name="context">What the connector needs to open its source.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What the run wrote.</returns>
    public async Task<PushSummary> RunAsync(PushSourceContext context, CancellationToken cancellationToken = default)
    {
        if (this.dryRun)
        {
            // A dry run still builds the schema: the searchable-and-refinable and
            // name-length guards throw here, at the desk, rather than on the real
            // run against the tenant.
            Schema schema = this.connector.BuildSchema();

            this.log.Information(
                "Dry run: schema builds cleanly ({Count} properties). Reading and mapping {DisplayName}, " +
                "writing nothing to Graph.",
                schema.Properties?.Count ?? 0,
                this.connector.DisplayName);

            // And it exercises the foreign-connection guard with read-only GETs,
            // so a wrong Graph:ConnectionId fails at the desk too. Skipped with a
            // note when the tenant is unreachable - the dry run's main job is
            // proving the mapping, and that needs only the source.
            try
            {
                Schema? registered = await this.TryGetRegisteredSchemaAsync(
                    this.options.Graph.ConnectionId, cancellationToken);

                VerifySchemaOwnership(this.options.Graph.ConnectionId, schema, registered, this.log);
            }
            catch (ODataError ex) when (ex.ResponseStatusCode == 404)
            {
                this.log.Information("Connection {ConnectionId} does not exist yet; a real run would create it.",
                    this.options.Graph.ConnectionId);
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                this.log.Warning(
                    "Could not check connection ownership ({Message}). The dry run continues; the real run checks.",
                    ex.Message);
            }
        }
        else
        {
            await this.EnsureConnectionAsync(cancellationToken);
            await this.EnsureSchemaAsync(cancellationToken);
        }

        // Opened after the ownership check, never before: a connector pointed at
        // a neighbour's connection should fail without ever having authenticated
        // to a database or walked a filesystem.
        //
        // But the RUN is opened before the source, because the source may want
        // to know where to resume from and that answer lives in the store.
        CrawlRunStart run = await this.OpenRunAsync(context, cancellationToken);

        await using IPushSource source = this.connector.CreateSource(context);

        try
        {
            PushSummary summary = await this.PushItemsAsync(source, cancellationToken);

            await this.store.CompleteRunAsync(
                this.Totals(summary), summary.TypeTotals(), summary.Timing, cancellationToken);

            return summary;
        }
        catch (Exception ex)
        {
            // Closed as failed rather than left open, so the next run does not
            // have to reap it and the dashboard shows a failure instead of a run
            // that appears to still be going. The message is flattened and
            // truncated: this database is more widely readable than the source,
            // and an exception carrying a row's content would undo the whole
            // logging policy upstream.
            await this.store.FailRunAsync(
                ex.GetType().Name,
                Truncate(ex.Message, MaxStoredErrorLength),
                default,
                Array.Empty<ItemTypeTotals>(),
                new PushTiming(),
                CancellationToken.None);

            throw;
        }
    }

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

    /// <summary>Creates the external connection. Idempotent.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task for the operation.</returns>
    public async Task EnsureConnectionAsync(CancellationToken cancellationToken = default)
    {
        string connectionId = this.options.Graph.ConnectionId;

        try
        {
            ExternalConnection? existing = await this.graph.External.Connections[connectionId]
                .GetAsync(cancellationToken: cancellationToken);

            this.log.Information(
                "Connection {ConnectionId} already exists. State {State}.", connectionId, existing?.State);
            return;
        }
        catch (ODataError ex) when (ex.ResponseStatusCode == 404)
        {
            // Not found, fall through to create.
        }

        await this.graph.External.Connections.PostAsync(
            new ExternalConnection
            {
                Id = connectionId,
                Name = this.options.Graph.ConnectionName,
                Description = this.options.Graph.Description,
            },
            cancellationToken: cancellationToken);

        this.log.Information("Connection {ConnectionId} created.", connectionId);
    }

    /// <summary>Registers the schema and polls until the connection is Ready.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task for the operation.</returns>
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        string connectionId = this.options.Graph.ConnectionId;

        ExternalConnection? connection = await this.graph.External.Connections[connectionId]
            .GetAsync(cancellationToken: cancellationToken);

        // The connection exists - but is it OURS? A registered schema cannot be
        // replaced, and the PUT is an upsert, so touching a connection another
        // connector registered would corrupt its index. Instead of each
        // connector naming the others' connection IDs - a list that goes stale
        // the day a connector is added - compare what is actually registered
        // against what this connector builds. Any foreign property means a
        // foreign connection. Checked in EVERY state, not only Ready: a foreign
        // connection whose registration is still in flight (draft) must not be
        // claimed by PATCHing this connector's schema over it either.
        Schema? registered = await this.TryGetRegisteredSchemaAsync(connectionId, cancellationToken);

        VerifySchemaOwnership(connectionId, this.connector.BuildSchema(), registered, this.log);

        if (connection?.State == ConnectionState.Ready)
        {
            this.log.Information("Schema already registered.");
            return;
        }

        Schema schema = this.connector.BuildSchema();

        await this.graph.External.Connections[connectionId].Schema
            .PatchAsync(schema, cancellationToken: cancellationToken);

        this.log.Information(
            "Schema registration submitted: {Count} properties. This runs server side and typically takes " +
            "5 to 15 minutes.",
            schema.Properties?.Count ?? 0);
        this.log.Information(
            "It cannot be changed afterwards except by adding properties. Run " +
            "deploy/Watch-SchemaRegistration.ps1 to watch, and read the schema it prints before pushing anything.");

        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMinutes(this.options.Graph.SchemaReadyTimeoutMinutes);
        TimeSpan pollDelay = TimeSpan.FromSeconds(30);

        while (true)
        {
            // One delay per iteration. A throttled poll sets the NEXT delay to
            // the honoured Retry-After instead of stacking it on top of the
            // fixed interval, so the logged wait is the wait that happens.
            await Task.Delay(pollDelay, cancellationToken);
            pollDelay = TimeSpan.FromSeconds(30);

            ConnectionState? state;

            try
            {
                state = (await this.graph.External.Connections[connectionId]
                    .GetAsync(cancellationToken: cancellationToken))?.State;
            }
            catch (ODataError ex) when (ex.ResponseStatusCode is 429 or 502 or 503 or 504)
            {
                // Registration runs server side for 5 to 15 minutes; one throttled
                // or transiently failing status poll must not abort a wait the
                // operation itself is surviving. The deadline still bounds it.
                pollDelay = GraphThrottling.RetryAfter(ex) ?? TimeSpan.FromSeconds(30);
                this.log.Warning(
                    "Status poll returned {Status}; polling again in {Seconds}s.",
                    ex.ResponseStatusCode,
                    (int)pollDelay.TotalSeconds);
                state = null;
            }
            catch (HttpRequestException ex)
            {
                this.log.Warning("Status poll failed ({Message}); polling again.", ex.Message);
                state = null;
            }

            if (state is not null)
            {
                this.log.Information("Connection state {State}.", state);
            }

            if (state == ConnectionState.Ready)
            {
                return;
            }

            if (state == ConnectionState.LimitExceeded)
            {
                throw new InvalidOperationException("Item quota exceeded for this tenant.");
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"Schema registration did not reach Ready within " +
                    $"{this.options.Graph.SchemaReadyTimeoutMinutes} minute(s). The operation continues server " +
                    "side; re-run deploy/Watch-SchemaRegistration.ps1 rather than recreating the connection.");
            }
        }
    }

    /// <summary>Reads the source and writes one item per row it yields.</summary>
    /// <param name="source">The opened source.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What the run wrote.</returns>
    public async Task<PushSummary> PushItemsAsync(IPushSource source, CancellationToken cancellationToken = default)
    {
        var summary = new PushSummary();
        int writers = this.ResolveWriterCount(source);

        if (writers > 1)
        {
            await this.WriteConcurrentlyAsync(source, summary, writers, cancellationToken);
        }
        else
        {
            await this.WriteInOrderAsync(source, summary, cancellationToken);
        }

        // Candidates the source itself declined, so the summary reconciles
        // against the source rather than only against what was written.
        summary.CountSkipped(source.Skipped);

        if (!this.dryRun)
        {
            // Reached only by falling out of the loop, which means the
            // enumeration ended without throwing and every write returned.
            await source.OnCrawlCompletedAsync(cancellationToken);

            // And only then may anything be concluded about what is missing.
            await this.SweepDeletedItemsAsync(summary, cancellationToken);
        }

        return summary;
    }

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

        foreach (CrawlDeletion deletion in pending)
        {
            if (await this.TryDeleteAsync(deletion.ItemId, summary, cancellationToken))
            {
                confirmed.Add(deletion.ItemId);
                summary.CountDeleted(deletion.ItemType);
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

    /// <summary>Builds the batch writer once, or returns null when batching is off.</summary>
    /// <param name="summary">The run's counters, which the writer reports into.</param>
    /// <returns>The writer, or null for the one-item-at-a-time path.</returns>
    /// <remarks>
    /// Batching is on by default, because a round trip per item is what made a
    /// thousand-row push take an hour and Graph's own limit of twenty requests
    /// per batch is the largest lever available without touching concurrency.
    /// Settings:Batch = false reverts to the single-item path, which is the
    /// behaviour every previous release had - worth keeping reachable, because
    /// it is the first thing to try when a run starts failing in a way batching
    /// could explain.
    ///
    /// A dry run never gets one: it writes nothing, so a batch of nothing would
    /// only make the log arrive out of order.
    ///
    /// Built lazily and kept, so one writer serves the whole run and its
    /// throttle callback reaches the same store the engine is using.
    /// </remarks>
    private GraphBatchWriter? ResolveBatchWriter(PushSummary summary)
    {
        if (this.dryRun || !this.options.Setting("Batch", true))
        {
            return null;
        }

        return this.batchWriter ??= new GraphBatchWriter(
            this.graph,
            this.options.Graph.ConnectionId,
            summary,
            this.log,
            throttle => this.store.RecordThrottle(throttle));
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

    /// <summary>Decides how many writers this run may use.</summary>
    /// <param name="source">The opened source.</param>
    /// <returns>One, unless the source has no position to protect and more are configured.</returns>
    /// <remarks>
    /// Three things force one writer, and each on its own is sufficient. A source
    /// that keeps a position (RequiresOrderedCommit) needs serial writes, because
    /// out-of-order completion is precisely what would let its checkpoint pass an
    /// item that never landed. A dry run writes nothing, so concurrency would buy
    /// nothing and only make the log arrive scrambled. And the configured count
    /// is the operator's own ceiling.
    ///
    /// The upper clamp is not arbitrary: the connectors API states that "an
    /// application is limited to 25 concurrent operations on a connection", so 16
    /// leaves room for the schema polls and ownership checks that share it.
    /// </remarks>
    private int ResolveWriterCount(IPushSource source)
    {
        if (this.dryRun || source.RequiresOrderedCommit)
        {
            return 1;
        }

        int configured = this.options.Setting("Writers", DefaultWriters);
        int writers = Math.Clamp(configured, 1, MaxWriters);

        if (writers != configured)
        {
            this.log.Warning(
                "Settings:Writers was {Configured}; using {Writers}. Graph allows an application 25 " +
                "concurrent operations on a connection, and this run needs headroom for its own polls.",
                configured,
                writers);
        }

        return writers;
    }

    /// <summary>Reads the source and writes one item at a time, in order.</summary>
    /// <param name="source">The opened source.</param>
    /// <param name="summary">The run's counters.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task for the operation.</returns>
    /// <remarks>
    /// The original path, and the only one a source with a watermark ever takes.
    /// The commit follows the write on the same thread, so the marker cannot pass
    /// an item the index does not have - the guarantee is structural here rather
    /// than argued.
    /// </remarks>
    private async Task WriteInOrderAsync(
        IPushSource source, PushSummary summary, CancellationToken cancellationToken)
    {
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var chunk = new List<Prepared>(ChunkSize);
        string lastItemId = "(none)";
        int rowOrdinal = 0;

        // Driven by hand rather than with await foreach for one reason: the time
        // spent inside MoveNextAsync is the time the SOURCE took to produce the
        // row, and await foreach leaves nowhere to stand to measure it. The using
        // block is scoped to the loop so the enumerator is still disposed on the
        // way out of it - before OnCrawlCompletedAsync, exactly as await foreach
        // did.
        //
        // The try around it exists because chunking introduced a way to lose
        // work that the item-at-a-time loop did not have. A source that dies
        // mid-enumeration - a dropped connection, a filesystem that went away -
        // leaves rows buffered in the chunk that were read perfectly well. They
        // are flushed before the failure propagates, so a dying source still
        // indexes everything it managed to hand over.
        try
        {
        await using (IAsyncEnumerator<PushItem> rows =
            source.ReadAsync(cancellationToken).GetAsyncEnumerator(cancellationToken))
        {
            while (true)
            {
                long readStarted = PushTiming.Now();
                bool hasRow = await rows.MoveNextAsync();
                summary.Timing.SourceRead.Add(PushTiming.MicrosecondsSince(readStarted));

                if (!hasRow)
                {
                    break;
                }

                PushItem mapped = rows.Current;

                long rowStarted = PushTiming.Now();
                rowOrdinal++;

                Prepared? prepared = this.Prepare(
                    mapped, rowOrdinal, lastItemId, written, summary, rowStarted);

                if (prepared is null)
                {
                    continue;
                }

                lastItemId = mapped.Id;
                chunk.Add(prepared.Value);

                if (chunk.Count >= ChunkSize)
                {
                    await this.FlushChunkAsync(source, chunk, summary, cancellationToken);
                    chunk.Clear();
                }
            }
        }
        }
        catch (Exception readFailure)
        {
            try
            {
                await this.FlushChunkAsync(source, chunk, summary, CancellationToken.None);
            }
            catch (Exception flushFailure)
            {
                // The read failure is the one that explains the run; a failure
                // flushing its remnant is a consequence. Say both, throw the
                // first - swapping them would report "Graph refused item a17"
                // for a run whose actual problem was that the database went away.
                this.log.Error(
                    "The final chunk could not be flushed after the source failed ({Message}). " +
                    "Reporting the source failure instead.",
                    flushFailure.Message);
            }

            ExceptionDispatchInfo.Capture(readFailure).Throw();
        }

        // The tail. Without this a corpus smaller than one chunk writes nothing
        // at all - the kind of defect that passes every test written against a
        // round number of rows and fails on the first real source.
        await this.FlushChunkAsync(source, chunk, summary, cancellationToken);
    }

    /// <summary>Reads the source on one thread and writes on several.</summary>
    /// <param name="source">The opened source. Must not require ordered commits.</param>
    /// <param name="summary">The run's counters.</param>
    /// <param name="writers">How many writers to run.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task for the operation.</returns>
    /// <remarks>
    /// Reading, ACL resolution, item building and duplicate detection all stay on
    /// the single reading thread. Only the write and its commit run in parallel.
    /// That is deliberate: the duplicate set and the row ordinals never become
    /// shared state, so the only concurrency to reason about is N outstanding
    /// PUTs - and this path is reachable only from a source that keeps no
    /// position, so there is no checkpoint for their out-of-order completion to
    /// corrupt.
    ///
    /// The channel is bounded and the reader waits on it. A source that outruns
    /// Graph is therefore throttled by the queue rather than buffering a corpus
    /// into memory, and the SQL reader closes as soon as the last row is handed
    /// over rather than being paced by the writes.
    /// </remarks>
    private async Task WriteConcurrentlyAsync(
        IPushSource source, PushSummary summary, int writers, CancellationToken cancellationToken)
    {
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = Channel.CreateBounded<List<Prepared>>(new BoundedChannelOptions(writers * 2)
        {
            SingleReader = false,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        // A failure in any writer has to stop the reader, and a failure in the
        // reader has to stop the writers; neither can be left running against a
        // run that is already over.
        using var failed = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        Task reader = Task.Run(
            async () =>
            {
                // Declared outside the try so the catch can hand over whatever
                // was already read before the source failed.
                var chunk = new List<Prepared>(ChunkSize);

                try
                {
                    string lastItemId = "(none)";
                    int rowOrdinal = 0;

                    await using IAsyncEnumerator<PushItem> rows =
                        source.ReadAsync(failed.Token).GetAsyncEnumerator(failed.Token);

                    while (true)
                    {
                        long readStarted = PushTiming.Now();
                        bool hasRow = await rows.MoveNextAsync();
                        summary.Timing.SourceRead.Add(PushTiming.MicrosecondsSince(readStarted));

                        if (!hasRow)
                        {
                            break;
                        }

                        PushItem mapped = rows.Current;

                        long rowStarted = PushTiming.Now();
                        rowOrdinal++;

                        Prepared? prepared = this.Prepare(
                            mapped, rowOrdinal, lastItemId, written, summary, rowStarted);

                        if (prepared is null)
                        {
                            continue;
                        }

                        lastItemId = mapped.Id;
                        chunk.Add(prepared.Value);

                        if (chunk.Count >= ChunkSize)
                        {
                            // A fresh list per chunk. Handing the same one to the
                            // channel and clearing it would let a writer read a
                            // chunk the reader is already refilling - a data race
                            // whose symptom is items silently written twice or not
                            // at all, depending on timing.
                            await queue.Writer.WriteAsync(chunk, failed.Token);
                            chunk = new List<Prepared>(ChunkSize);
                        }
                    }

                    if (chunk.Count > 0)
                    {
                        await queue.Writer.WriteAsync(chunk, failed.Token);
                    }

                    queue.Writer.Complete();
                }
                catch (Exception ex)
                {
                    // Hand over what was already read before faulting. Those rows
                    // are as good as any other; losing them because the row after
                    // them failed would make a dying source index less than it
                    // successfully produced.
                    if (chunk.Count > 0)
                    {
                        queue.Writer.TryWrite(chunk);
                    }

                    // Complete WITH the fault, so every writer's ReadAllAsync ends
                    // rather than waiting forever on a channel nobody will fill.
                    queue.Writer.TryComplete(ex);
                    failed.Cancel();
                    throw;
                }
            },
            CancellationToken.None);

        Task[] consumers = Enumerable.Range(0, writers).Select(_ => Task.Run(
            async () =>
            {
                try
                {
                    await foreach (List<Prepared> batch in queue.Reader.ReadAllAsync(failed.Token))
                    {
                        await this.FlushChunkAsync(source, batch, summary, failed.Token);
                    }
                }
                catch (Exception)
                {
                    failed.Cancel();
                    throw;
                }
            },
            CancellationToken.None)).ToArray();

        // WhenAll so every task is awaited - an unobserved writer fault would
        // otherwise surface later, attached to nothing. The reader is awaited
        // first because its exception is the one that explains the run.
        var all = new List<Task>(consumers.Length + 1) { reader };
        all.AddRange(consumers);

        try
        {
            await Task.WhenAll(all);
        }
        catch (Exception)
        {
            // Which exception surfaces decides whether the operator gets an
            // explanation or a symptom. The first failure cancels every other
            // task, so by the time WhenAll throws, most of what it holds is
            // OperationCanceledException caused BY the real fault - and WhenAll
            // rethrows only the first, which may well be one of those.
            //
            // Prefer the reader: a row that could not be prepared names its
            // ordinal, and that locates the problem in the source.
            if (reader.IsFaulted)
            {
                reader.GetAwaiter().GetResult();
            }

            // Otherwise prefer any genuine failure over the cancellations it
            // caused. "Graph refused item a20 with 400" is the run's cause of
            // death; "a write was cancelled" is a consequence of it.
            Exception? cause = all
                .Where(task => task.IsFaulted)
                .SelectMany(task => task.Exception?.Flatten().InnerExceptions
                    ?? (IEnumerable<Exception>)Array.Empty<Exception>())
                .FirstOrDefault(ex => ex is not OperationCanceledException);

            if (cause is not null)
            {
                ExceptionDispatchInfo.Capture(cause).Throw();
            }

            throw;
        }
    }

    /// <summary>Resolves the ACL and builds the item, or reports why it will not be written.</summary>
    /// <param name="mapped">The row as the source yielded it.</param>
    /// <param name="rowOrdinal">Which row this is, for locating a failure.</param>
    /// <param name="lastItemId">The previous item's ID, for the same reason.</param>
    /// <param name="written">IDs already seen this run. Touched only by the reading thread.</param>
    /// <param name="summary">The run's counters.</param>
    /// <param name="rowStarted">When this row began, for the timing table.</param>
    /// <returns>The prepared item, or null when the source could grant it to nobody.</returns>
    /// <remarks>
    /// The two hashes are computed here rather than at write time, and it has to
    /// be here: they must cover the item exactly as it will be sent, which means
    /// after truncation and after the ACL has been resolved. Hashing the
    /// connector's input instead would miss both - an item whose tail was cut
    /// would look different from itself on every run, and an item whose
    /// connection-wide ACL was reconfigured would look unchanged.
    /// </remarks>
    private Prepared? Prepare(
        PushItem mapped,
        int rowOrdinal,
        string lastItemId,
        HashSet<string> written,
        PushSummary summary,
        long rowStarted)
    {
        ExternalItem item;
        byte[] contentHash;
        byte[] aclHash;
        int contentBytes;

        try
        {
            long prepareStarted = PushTiming.Now();

            List<Acl>? acl = this.ResolveAcl(mapped);

            if (acl is null)
            {
                // The source derived no grant for this item. Writing it would
                // put a row in the index that Graph returns to nobody, which
                // reads as success and is not; skipping it narrows the index
                // rather than the audience of something already in it.
                summary.CountSkipped(mapped.ItemType);
                this.log.Warning(
                    "Item {ItemId} has no grants and was not written. " +
                    "The source could resolve no group for it.",
                    mapped.Id);
                return null;
            }

            item = this.BuildItem(mapped, acl, summary, out contentBytes);

            contentHash = ItemHasher.HashContent(
                mapped.Id, mapped.ItemType, mapped.Properties, item.Content?.Value ?? string.Empty);
            aclHash = ItemHasher.HashAcl(acl);

            summary.Timing.Prepare.Add(PushTiming.MicrosecondsSince(prepareStarted));
        }
        catch (Exception ex)
        {
            // Locate the failure without logging row content: ordinal and the
            // neighbouring item ID are policy-safe and turn "which row killed
            // the run" from bisection into a lookup.
            throw new InvalidOperationException(
                $"Row {rowOrdinal} could not be prepared (the item before it was {lastItemId}). " +
                "The row's content is deliberately not logged; find it in the source by ordinal.",
                ex);
        }

        if (!written.Add(mapped.Id))
        {
            // The PUT is an upsert, so a duplicate ID silently overwrites the
            // earlier item while the count claims both. The source is expected
            // to return one row per item; say so out loud and count it.
            //
            // With a state store attached this is also the fourth agent feature:
            // the set is the run's own memory of what it has already handled, so
            // a source that reaches the same item twice through two paths writes
            // it once. It is kept on the reading thread, which is why duplicate
            // detection stays correct however many writers are running.
            summary.CountDuplicate();
            this.log.Warning(
                "Item {ItemId} appeared more than once (row {RowOrdinal}); the later row overwrote the earlier item.",
                mapped.Id,
                rowOrdinal);
        }

        return new Prepared(mapped, item, contentHash, aclHash, contentBytes, rowStarted);
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
                // Item ID, type and sizes only. The content is customer data and
                // does not go to the console any more than it goes to the log.
                this.log.Information(
                    "Would write {ItemId} ({ItemType}): {PropertyCount} properties, {ContentBytes} content bytes, " +
                    "{AclCount} ACL entr(y/ies).",
                    prepared.Mapped.Id,
                    prepared.Mapped.ItemType,
                    prepared.Mapped.Properties.Count,
                    prepared.ContentBytes,
                    prepared.Item.Acl?.Count ?? 0);

                summary.Count(prepared.Mapped.ItemType);

                // Measured like any other row. A dry run writes nothing, so what
                // it reports IS the whole non-Graph cost of the pipeline - the
                // cheapest way to find out how much of a slow run is not Graph's
                // fault, and it needs no tenant at all.
                summary.Timing.RowTotal.Add(PushTiming.MicrosecondsSince(prepared.StartedAt));
            }

            // No commit callbacks and no state recorded: a dry run writes
            // nothing, so it must leave both the watermark and the store exactly
            // where it found them.
            return;
        }

        // 1. What is already on record for these items?
        var unchanged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (this.store.IsEnabled)
        {
            IReadOnlyDictionary<string, CrawlItemState> known = await this.store.GetItemStatesAsync(
                chunk.Select(prepared => prepared.Mapped.Id).ToList(), cancellationToken);

            foreach (Prepared prepared in chunk)
            {
                if (known.TryGetValue(prepared.Mapped.Id, out CrawlItemState state) &&
                    state.Matches(prepared.ContentHash, prepared.AclHash))
                {
                    unchanged.Add(prepared.Mapped.Id);
                }
            }
        }

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
        // Bounded work: at most two calls over at most ChunkSize rows.
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

            List<string> seen = stored
                .Where(prepared => unchanged.Contains(prepared.Mapped.Id))
                .Select(prepared => prepared.Mapped.Id)
                .ToList();

            if (seen.Count > 0)
            {
                await this.store.RecordUnchangedAsync(seen, recording);
            }
        }

        foreach (Prepared prepared in chunk.Where(p => refused.Contains(p.Mapped.Id)))
        {
            // Counted, not thrown. crawl.Run.ItemsFailed carries it and the
            // dashboard shows it, so a run that wrote 1,117 of 1,118 reports
            // exactly that rather than reporting success or dying outright.
            summary.CountFailed(prepared.Mapped.ItemType);
        }

        // 4. Count and commit, in the order the source yielded.
        foreach (Prepared prepared in confirmed)
        {
            long commitStarted = PushTiming.Now();

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

            await source.OnItemCommittedAsync(prepared.Mapped, recording);

            summary.Timing.Commit.Add(PushTiming.MicrosecondsSince(commitStarted));
            summary.Timing.RowTotal.Add(PushTiming.MicrosecondsSince(prepared.StartedAt));
        }

        // The checkpoint, once, from the last item that carried a marker. Every
        // item in the chunk is confirmed by the time this runs, so the position
        // is honest; doing it per item would put a database round trip beside
        // every Graph write for a value only the next run reads.
        if (this.store.IsEnabled && confirmed.Count > 0)
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

    /// <summary>One item, mapped, resolved, hashed and waiting for a writer.</summary>
    /// <param name="Mapped">The row as the source yielded it.</param>
    /// <param name="Item">The item to write.</param>
    /// <param name="ContentHash">SHA-256 over the item as it will be sent, after truncation.</param>
    /// <param name="AclHash">SHA-256 over the resolved grants.</param>
    /// <param name="ContentBytes">The content's size after truncation.</param>
    /// <param name="StartedAt">When this row began, for the timing table.</param>
    private readonly record struct Prepared(
        PushItem Mapped,
        ExternalItem Item,
        byte[] ContentHash,
        byte[] AclHash,
        int ContentBytes,
        long StartedAt);

    /// <summary>
    /// Decides which grants an item carries: its own when the source supplied
    /// them, the connection-wide ACL when it did not.
    /// </summary>
    /// <param name="mapped">The item to resolve grants for.</param>
    /// <returns>The grants, or null when the item has none and must be skipped.</returns>
    private List<Acl>? ResolveAcl(PushItem mapped)
    {
        if (mapped.Acl is null)
        {
            // Entra group principals, never Everyone. Every item in a connection
            // gets the same ACL: a child row is at least as sensitive as its
            // parent, so there is no argument for trimming them differently.
            // Built once per run - it cannot change between items.
            return this.sharedAcl ??= BuildAcl(this.options);
        }

        if (mapped.Acl.Count == 0)
        {
            return null;
        }

        return mapped.Acl
            .GroupBy(entry => entry.Key(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(entry => new Acl
            {
                Type = entry.Type == PushAclType.ExternalGroup ? AclType.ExternalGroup : AclType.Group,

                // An Entra group is named by object ID and normalised the way the
                // shared ACL is; an external group ID is an opaque string this
                // connection owns and is forwarded as written.
                Value = entry.Type == PushAclType.Group
                    ? Guid.Parse(entry.Value.Trim()).ToString("D")
                    : entry.Value.Trim(),
                AccessType = AccessType.Grant,
            })
            .ToList();
    }

    /// <summary>Reads the registered schema, or null when there is none to read.</summary>
    private async Task<Schema?> TryGetRegisteredSchemaAsync(string connectionId, CancellationToken cancellationToken)
    {
        try
        {
            return await this.graph.External.Connections[connectionId].Schema
                .GetAsync(cancellationToken: cancellationToken);
        }
        catch (ODataError ex) when (ex.ResponseStatusCode == 404)
        {
            // No schema registered yet: nothing to compare.
            return null;
        }
    }

    /// <summary>
    /// Throws when a connection's registered schema was written by a different
    /// connector. Pure and static so the control is testable without a tenant.
    ///
    /// The rule accounts for append-only evolution: a property this connector
    /// expects but the connection lacks is a pending addition (warned, not
    /// fatal); a property the connection carries that this connector does not
    /// build can only have come from another connector, and is fatal.
    /// </summary>
    /// <param name="connectionId">The connection being checked, for the error.</param>
    /// <param name="expected">The schema this connector builds.</param>
    /// <param name="registered">The schema Graph returned, or null when unreadable.</param>
    /// <param name="log">Where the pending-addition warning goes.</param>
    /// <exception cref="InvalidOperationException">The connection belongs to another connector.</exception>
    public static void VerifySchemaOwnership(string connectionId, Schema expected, Schema? registered, ILogger log)
    {
        if (registered?.Properties is null || registered.Properties.Count == 0)
        {
            return;
        }

        var expectedNames = new HashSet<string>(
            (expected.Properties ?? new List<Property>()).Select(p => p.Name ?? string.Empty),
            StringComparer.OrdinalIgnoreCase);

        List<string> foreign = registered.Properties
            .Select(p => p.Name ?? string.Empty)
            .Where(name => !expectedNames.Contains(name))
            .ToList();

        if (foreign.Count > 0)
        {
            throw new InvalidOperationException(
                $"Connection {connectionId} carries a schema this connector did not register: " +
                $"propert{(foreign.Count == 1 ? "y" : "ies")} {string.Join(", ", foreign)} " +
                $"do{(foreign.Count == 1 ? "es" : string.Empty)} not exist in this connector's schema. " +
                "It belongs to another connector, its schema cannot be replaced, and pushing into it would " +
                "corrupt that connector's index. Configure this connector's own Graph:ConnectionId.");
        }

        var registeredNames = new HashSet<string>(
            registered.Properties.Select(p => p.Name ?? string.Empty),
            StringComparer.OrdinalIgnoreCase);

        List<string> pending = expectedNames.Where(name => !registeredNames.Contains(name)).ToList();

        if (pending.Count > 0)
        {
            log.Warning(
                "Connection {ConnectionId} does not yet carry {Count} propert(y/ies) this connector now " +
                "builds: {Pending}. The schema is append-only; add them deliberately before relying on them.",
                connectionId,
                pending.Count,
                string.Join(", ", pending));
        }
    }

    /// <summary>Builds the ACL every item in the connection carries.</summary>
    /// <param name="options">Validated configuration.</param>
    /// <returns>One grant per configured group.</returns>
    public static List<Acl> BuildAcl(PushOptions options)
    {
        if (options.Acl.GrantGroupObjectIds is null || options.Acl.GrantGroupObjectIds.Count == 0)
        {
            // Validation rejects this, so reaching it means the engine was driven
            // from code rather than from a configuration file. Fail rather than
            // write an item nobody is granted, which Graph accepts silently.
            throw new InvalidOperationException(
                "Acl:GrantGroupObjectIds is empty. An item with no ACL is indexed and never returned to anyone.");
        }

        return options.Acl.GrantGroupObjectIds
            .Select(id => new Acl
            {
                Type = AclType.Group,

                // Validation accepts every GUID spelling ({B}, (P), 32 hex digits);
                // Graph wants the canonical one. Normalise rather than forward
                // whatever shape the operator pasted.
                Value = Guid.Parse(id.Trim()).ToString("D"),
                AccessType = AccessType.Grant,
            })
            .ToList();
    }

    /// <summary>
    /// Adds the OData type annotation Graph requires beside every multi-value
    /// property.
    ///
    /// A collection sent without its "name@odata.type": "Collection(String)"
    /// sibling is rejected by Graph as a type mismatch against the registered
    /// StringCollection property, and the message names the item rather than the
    /// annotation. Doing it here means a connector adds a list and is finished;
    /// there is no second thing to remember, and no way to remember it in one
    /// connector and forget it in the next.
    /// </summary>
    /// <param name="properties">The connector's property values.</param>
    /// <returns>Those values, with an annotation beside each collection.</returns>
    private static Dictionary<string, object> AnnotateCollections(Dictionary<string, object> properties)
    {
        List<string> collections = properties
            .Where(property => property.Value is IEnumerable<string>)
            .Select(property => property.Key)
            .ToList();

        if (collections.Count == 0)
        {
            return properties;
        }

        var annotated = new Dictionary<string, object>(properties, StringComparer.Ordinal);

        foreach (string name in collections)
        {
            annotated[name + "@odata.type"] = "Collection(String)";
        }

        return annotated;
    }

    private ExternalItem BuildItem(PushItem mapped, List<Acl> acl, PushSummary summary, out int contentBytes)
    {
        ExternalSchemaRules.ValidateItemId(mapped.Id);

        TruncationResult content = ContentTruncator.Truncate(
            mapped.Content ?? string.Empty, this.options.DataSource.MaxContentBytes);

        // Taken here because this is the only place the real byte count exists -
        // Value.Length would be characters, and the gap matters when the question
        // is whether twenty of these fit in one 30 MB Graph $batch.
        summary.Timing.ContentBytes.Add(content.FinalBytes);
        contentBytes = content.FinalBytes;

        if (content.Truncated)
        {
            summary.CountTruncated();

            this.log.Warning(
                "Item {ItemId} content truncated from {OriginalBytes} to {FinalBytes} bytes.",
                mapped.Id,
                content.OriginalBytes,
                content.FinalBytes);
        }

        return new ExternalItem
        {
            Id = mapped.Id,
            Acl = acl,
            Properties = new Properties { AdditionalData = AnnotateCollections(mapped.Properties) },
            Content = new ExternalItemContent
            {
                Type = ExternalItemContentType.Text,
                Value = content.Content,
            },
        };
    }

    private async Task WriteWithRetryAsync(
        string itemId, ExternalItem item, PushSummary summary, CancellationToken cancellationToken)
    {
        // Accumulated across every attempt for this row and recorded once, in the
        // finally, so a row that gave up still reports what it spent. The two are
        // kept apart on purpose: time in flight and time asleep after a 429 call
        // for opposite remedies, and a single "the write took 3.5s" number cannot
        // tell you which of them you are looking at.
        long inFlight = 0;
        long asleep = 0;

        try
        {
            for (int attempt = 1; ; attempt++)
            {
                long started = PushTiming.Now();

                try
                {
                    await this.graph.External.Connections[this.options.Graph.ConnectionId]
                        .Items[itemId].PutAsync(item, cancellationToken: cancellationToken);

                    inFlight += PushTiming.MicrosecondsSince(started);
                    return;
                }
                catch (ODataError ex) when (
                    ex.ResponseStatusCode is 429 or 502 or 503 or 504 && attempt < MaxWriteAttempts)
                {
                    // The refusal itself was time in flight; only the sleep that
                    // follows is backoff.
                    inFlight += PushTiming.MicrosecondsSince(started);

                    // 429 honours Retry-After; a transient 5xx gets the same bounded
                    // backoff rather than aborting a thousand-item run for one blip.
                    TimeSpan wait = GraphThrottling.RetryAfter(ex) ?? GraphThrottling.Backoff(attempt);

                    if (ex.ResponseStatusCode == 429)
                    {
                        summary.CountThrottleWait();
                    }

                    // Buffered in memory and flushed when the run closes, so
                    // crawl.ThrottleEvent can answer "were they clustered in one
                    // bad minute or spread across the hour" - which argue for
                    // opposite changes. Never a round trip here: this is the
                    // catch block of a run that is already struggling.
                    this.store.RecordThrottle(new ThrottleEvent(
                        DateTime.UtcNow,
                        ex.ResponseStatusCode,
                        (int)wait.TotalSeconds,
                        "item",
                        attempt));

                    this.log.Warning(
                        "Write of {ItemId} returned {Status}. Waiting {Seconds}s before attempt {Next} of {Max}.",
                        itemId,
                        ex.ResponseStatusCode,
                        (int)wait.TotalSeconds,
                        attempt + 1,
                        MaxWriteAttempts);

                    long sleeping = PushTiming.Now();
                    await Task.Delay(wait, cancellationToken);
                    asleep += PushTiming.MicrosecondsSince(sleeping);
                }
                catch (HttpRequestException ex) when (attempt < MaxWriteAttempts)
                {
                    inFlight += PushTiming.MicrosecondsSince(started);

                    TimeSpan wait = GraphThrottling.Backoff(attempt);

                    this.log.Warning(
                        "Write of {ItemId} failed in transit ({Message}). Waiting {Seconds}s before attempt " +
                        "{Next} of {Max}.",
                        itemId,
                        ex.Message,
                        (int)wait.TotalSeconds,
                        attempt + 1,
                        MaxWriteAttempts);

                    long sleeping = PushTiming.Now();
                    await Task.Delay(wait, cancellationToken);
                    asleep += PushTiming.MicrosecondsSince(sleeping);
                }
                catch (ODataError ex)
                {
                    inFlight += PushTiming.MicrosecondsSince(started);

                    // Terminal: name the item and the status before the exception
                    // climbs to the run-level handler, so "which row killed the run"
                    // is one log line, not an inference. Item ID and status only.
                    this.log.Error(
                        "Write failed for {ItemId} with status {Status}. Giving up on this run.",
                        itemId,
                        ex.ResponseStatusCode);
                    throw;
                }
            }
        }
        finally
        {
            summary.Timing.WriteInFlight.Add(inFlight);
            summary.Timing.WriteBackoff.Add(asleep);
        }
    }
}
