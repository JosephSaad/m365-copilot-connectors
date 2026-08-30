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
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Serilog;
using Serilog.Context;

/// <summary>Runs one connector against one connection.</summary>
public sealed partial class PushEngine
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
    // TWENTY WAS TWO DECISIONS WEARING ONE NUMBER. Graph's hard ceiling on a
    // $batch is twenty requests, and this constant was also the granularity of
    // every state-store round trip - one lookup and at most two recording calls
    // per chunk. Tying them together meant the store paid Graph's limit: a
    // 111,900-row crawl made 5,595 lookups, each returning at most twenty rows,
    // when the store is perfectly willing to answer about two hundred at once.
    //
    // They are now separate. GraphBatchWriter.WriteAsync already splits a list
    // of any length into service-legal batches of twenty, so a larger chunk here
    // changes what the STORE is asked and leaves what GRAPH is asked exactly as
    // it was.
    private const int DefaultLookupChunkSize = 200;

    // ...but a count alone is not a safe bound, and this is the reason the
    // ceiling below exists. A chunk holds a fully built ExternalItem per row,
    // and one item may carry DataSource:MaxContentBytes of content - 3.5 MB by
    // default, and up to the platform's 30 MB. Two hundred of those is several
    // gigabytes of live objects, which is a memory profile nobody asked for and
    // an out-of-memory nobody could explain from the setting that caused it.
    //
    // So the chunk closes on whichever it reaches first, exactly as the batch
    // writer closes a batch on requests-or-bytes. On this rig's corpus - p50
    // 491 content bytes, max 904 - the count closes every chunk and this
    // ceiling never fires; on a corpus of large documents the ceiling closes
    // them and the count never fires. Both stay correct without being tuned.
    private const long DefaultLookupChunkBytes = 16 * 1024 * 1024;

    // The floor is one: a chunk of one is legal, and forcing a minimum above it
    // would silently overrule an operator debugging a specific row. The ceiling
    // is a guard against a typo turning a crawl into one enormous chunk.
    private const int MaxLookupChunkSize = 2000;

    // What one write actually carries, and the unit the writer channel moves.
    //
    // Kept at Graph's $batch ceiling and kept SEPARATE from the lookup window
    // above, because the two numbers answer to different services. Raising the
    // lookup window is free; raising this is not permitted by Graph. And the
    // first attempt at this change used one number for both, which quietly
    // starved the writer pool: the channel moves chunks, so a window of two
    // hundred handed one writer two hundred rows and left the other fifteen
    // idle. The concurrency tests caught it, which is the only reason this
    // comment can be specific about it.
    private const int WriteChunkSize = 20;

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

    // How often a running crawl tells the store it is still alive.
    //
    // Sixty seconds against sql/43's 180-second grace: three beats of headroom,
    // so one slow round trip, one long pause or one retry storm cannot hand the
    // lease to a second process while this one is mid-crawl. Cheap at this rate -
    // sixty round trips an hour against the 560 a full crawl already makes.
    private static readonly TimeSpan DefaultHeartbeatEvery = TimeSpan.FromSeconds(60);

    // The floor, and it is a guard rather than a preference. A one-second beat
    // against a store under load is a round trip per second per connection for
    // the length of every crawl, and the grace period is three minutes wide, so
    // nothing below this buys anything an operator would notice.
    private static readonly TimeSpan MinimumHeartbeatEvery = TimeSpan.FromSeconds(1);

    private readonly IPushConnector connector;
    private readonly PushOptions options;
    private readonly GraphServiceClient graph;
    private readonly ILogger log;
    private readonly bool dryRun;
    private readonly ICrawlStateStore store;

    private GraphBatchWriter? batchWriter;

    // Every item ID a DRY RUN has read, kept so the delete preview can diff it
    // against what the index holds. Null on a real run, where the store's own
    // LastSeenRunId bookkeeping answers the same question without holding
    // 111,900 strings in memory - about 3 MB, which is affordable only because a
    // dry run is something a person is sitting and waiting for.
    private HashSet<string>? dryRunSeenIds;

    // Resolved once in the constructor. Setting() re-parses configuration on
    // every call and the byte ceiling is consulted once per row, which is a
    // needless cost 111,900 times over.
    private readonly int lookupChunkSize;
    private readonly long lookupChunkBytes;

    private readonly TimeSpan heartbeatEvery;

    // Compiled once, consulted once per row, and immutable so the concurrent
    // path needs no lock - it is read on the single reading thread anyway, which
    // is the same reason duplicate detection lives there.
    private readonly SensitivityPolicy sensitivity;

    // The rescue path's stand-in for a lookup that cannot be made. See its use
    // in the reader's catch: static, immutable, and shared because nothing ever
    // writes to it.
    private static readonly IReadOnlySet<string> EmptyUnchanged =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // What kind of read this run is making. Full unless the state store said an
    // incremental one was safe, and consulted by exactly one thing: the delete
    // sweep, which is valid after a full crawl and meaningless after a partial
    // one. Defaulting to Full is the safe direction - it makes the sweep ASK the
    // store, and the store refuses anything it should not answer.
    private CrawlMode crawlMode = CrawlMode.Full;

    // Set the moment any item in this run is refused, and never cleared.
    //
    // A batch refusal does not end the run - that is the point of per-item
    // outcomes - so without this flag the NEXT chunk would commit in full and
    // move the marker straight over the gap the refusal left. The item would be
    // neither in the index nor ever re-read, which is precisely what this
    // repository's oldest invariant forbids.
    //
    // Once it is set, later items are still written and still recorded in the
    // store, because they are genuinely in the index and the sweep must not
    // delete them. What stops is the marker: no further source commits and no
    // further checkpoint advance, so the next run resumes from before the gap
    // and retries the refused item. Re-reading what was already written is free
    // - every write is an upsert.
    private bool markerBlocked;

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

        // Clamped, and the clamp is announced. A silently-honoured 100,000 would
        // read every row into memory before writing one, and the operator who
        // typed it would be looking at a memory graph rather than at the setting
        // that caused it. This matches how Settings:Writers reports its own
        // clamp - "was 99; using 16" - because a limit nobody is told about is
        // indistinguishable from a limit that did not work.
        int requested = this.options.Setting("LookupChunkSize", DefaultLookupChunkSize);
        this.lookupChunkSize = Math.Clamp(requested, 1, MaxLookupChunkSize);

        if (this.lookupChunkSize != requested)
        {
            this.log.Warning(
                "Settings:LookupChunkSize was {Requested}; using {Used}. The permitted range is 1 to {Max}.",
                requested,
                this.lookupChunkSize,
                MaxLookupChunkSize);
        }

        // Settings:HeartbeatSeconds, because the right interval depends on the
        // store's latency and on sql/43's @LeaseGraceSeconds, and those are
        // deployment facts rather than compile-time ones. The default keeps three
        // beats of headroom inside the default grace.
        int beat = this.options.Setting("HeartbeatSeconds", 0);

        this.heartbeatEvery = beat > 0
            ? TimeSpan.FromSeconds(Math.Max(beat, MinimumHeartbeatEvery.TotalSeconds))
            : DefaultHeartbeatEvery;

        this.lookupChunkBytes = this.options.Setting("LookupChunkBytes", 0) > 0
            ? this.options.Setting("LookupChunkBytes", 0)
            : DefaultLookupChunkBytes;

        this.sensitivity = SensitivityPolicy.Compile(this.options.Sensitivity);

        if (this.sensitivity.IsEnabled)
        {
            // Announced at construction, because a control that is off and a
            // control that is on look identical in a log until it refuses
            // something - and the run where it refuses nothing is exactly the
            // run somebody later has to prove it was switched on for.
            this.log.Information(
                "Sensitivity mapping is {Mode}: {Count} classification(s) mapped, published as {Property}. " +
                "{Effect}",
                this.sensitivity.Mode,
                this.sensitivity.MappedClassifications,
                this.sensitivity.Property,
                this.sensitivity.Enforces
                    ? "Items whose label is not indexable will NOT be written."
                    : "No item will be refused; this mode only publishes the label.");
        }
    }

    /// <summary>Creates the connection and schema if needed, then pushes every item.</summary>
    /// <param name="context">What the connector needs to open its source.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What the run wrote.</returns>
    public async Task<PushSummary> RunAsync(PushSourceContext context, CancellationToken cancellationToken = default)
    {
        // Opened first and disposed last, so that every phase below is a child of
        // it and a run that throws still closes a span rather than leaving one
        // open for the exporter to time out on. Null when nobody is listening,
        // which is the ordinary case and costs a null check.
        using Activity? runSpan = PushTelemetry.StartRun(
            this.connector.Key, this.options.Graph.ConnectionId, this.dryRun);

        long runStarted = PushTiming.Now();

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
            using (Activity? _ = PushTelemetry.StartPhase("connection"))
            {
                await this.EnsureConnectionAsync(cancellationToken);
            }

            // Its own phase because it is the one that can legitimately take
            // fifteen minutes, and an operator watching a trace needs to see
            // that the time is registration rather than the source.
            using (Activity? _ = PushTelemetry.StartPhase("schema"))
            {
                await this.EnsureSchemaAsync(cancellationToken);
            }
        }

        // Opened after the ownership check, never before: a connector pointed at
        // a neighbour's connection should fail without ever having authenticated
        // to a database or walked a filesystem.
        //
        // But the RUN is opened before the source, because the source may want
        // to know where to resume from and that answer lives in the store.
        CrawlRunStart run = await this.OpenRunAsync(context, cancellationToken);

        PushTelemetry.SetRun(runSpan, run.RunId, run.Mode.ToString(), this.store.IsEnabled);

        // Every event from here to the end of the run carries the run identifier,
        // so a log file and a dashboard row can be lined up by reading rather than
        // by matching timestamps - which is guesswork the moment two connectors
        // share a host, and worse when the question is being asked about an
        // incident that happened yesterday.
        //
        // RunTag is pre-formatted rather than raw, because a run identifier is
        // only useful when there is one: without a state store the id is 0, and a
        // log full of "run 0" reads as a real run rather than as no bookkeeping at
        // all. In that case the tag is empty and the file looks exactly as it did.
        using IDisposable? runTag = LogContext.PushProperty(
            "RunTag",
            this.store.IsEnabled ? $"run {run.RunId} " : string.Empty);

        await using IPushSource source = this.connector.CreateSource(context);

        // THE LEASE HAS TO BE KEPT ALIVE FOR AS LONG AS THE CRAWL RUNS. sql/43
        // presumes a run dead when its heartbeat goes stale and hands the lease
        // to whoever asks next, so a crawl that stops beating would invite a
        // second process to start beside it - which is the exact scenario the
        // lease exists to prevent, caused by the mechanism meant to prevent it.
        //
        // Linked to the caller's token so it stops when the run does, and
        // disposed in a finally so it stops when the run THROWS as well: a
        // heartbeat outliving a dead crawl would hold the lease against its own
        // replacement.
        using var beating = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task heartbeat = this.HeartbeatUntilAsync(beating.Token);

        try
        {
            PushSummary summary;

            using (Activity? _ = PushTelemetry.StartPhase("items"))
            {
                summary = await this.PushItemsAsync(source, cancellationToken);
            }

            await this.store.CompleteRunAsync(
                this.Totals(summary), summary.TypeTotals(), summary.Timing, cancellationToken);

            // Recorded after the store call rather than before it, so the
            // duration a dashboard shows is the duration an operator waited
            // rather than the part of it this class happens to own.
            PushTelemetry.RecordRun(
                summary,
                this.connector.Key,
                this.options.Graph.ConnectionId,
                PushTiming.MicrosecondsSince(runStarted) / 1_000_000.0);

            runSpan?.SetTag("crawl.items.written", summary.Total);
            runSpan?.SetTag("crawl.items.failed", summary.Failed);
            runSpan?.SetTag("crawl.items.skipped", summary.Skipped);
            runSpan?.SetTag("crawl.items.deleted", summary.Deleted);

            if (this.sensitivity.IsEnabled)
            {
                // Only when the policy is on. A tag reading zero on every run of
                // every connector that has no policy is noise that makes the
                // runs which DO have one harder to find, not easier.
                runSpan?.SetTag("crawl.sensitivity.mode", this.sensitivity.Mode.ToString());
                runSpan?.SetTag("crawl.items.refused_by_label", summary.RefusedByLabel);
            }

            return summary;
        }
        catch (Exception ex)
        {
            PushTelemetry.SetFailed(runSpan, ex);

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
        finally
        {
            // Stop beating before anything else observes the run as closed.
            // Awaited rather than abandoned so a beat in flight cannot land
            // after CompleteRunAsync and resurrect a finished row - though
            // uspHeartbeatRun guards that too, by only touching rows still at
            // status 1. Belt and braces on the one thing that would be silent.
            beating.Cancel();

            try
            {
                await heartbeat;
            }
            catch (OperationCanceledException)
            {
                // Expected: this is how the loop ends.
            }
        }
    }

    /// <summary>Tells the store this run is alive, until the run ends.</summary>
    /// <param name="cancellationToken">Cancelled when the run finishes or fails.</param>
    /// <returns>A task that completes when beating stops.</returns>
    /// <remarks>
    /// A FAILED HEARTBEAT MUST NOT FAIL THE CRAWL. The grace period in sql/43 is
    /// three beats wide precisely so a single missed one is survivable, and
    /// killing an otherwise healthy hour-long crawl because a keepalive could not
    /// reach the database would be the cure causing the disease. So this warns
    /// and carries on.
    ///
    /// It warns every time rather than once, and that is deliberate: a beat that
    /// keeps failing IS heading for a lost lease, and the operator wants to see
    /// it accumulating rather than to find one line at the top of an hour of log.
    /// </remarks>
    private async Task HeartbeatUntilAsync(CancellationToken cancellationToken)
    {
        if (!this.store.IsEnabled)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(this.heartbeatEvery, cancellationToken);
                await this.store.HeartbeatAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                this.log.Warning(
                    "Could not record a heartbeat for this run ({Message}). The crawl continues. If this keeps " +
                    "failing the run's lease will expire and another process may start beside it.",
                    ex.Message);
            }
        }
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
        }

        // Both paths, and only here. A real run sweeps; a dry run previews the
        // same sweep and touches nothing. The precondition is identical and is
        // the reason this sits after the loop rather than inside it: the
        // enumeration ended without throwing, so what the source did not return
        // is genuinely absent rather than merely not reached yet. A preview
        // computed from a partial read would name rows the source had simply not
        // got to, which is the one thing a delete preview must never do.
        await this.SweepDeletedItemsAsync(summary, cancellationToken);

        return summary;
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

        // Settings:MaxBatchContentBytes, because the header on
        // DefaultMaxBatchContentBytes says to raise it "with the constructor once
        // a tenant's real behaviour is known" - and until now the only way to do
        // that was to rebuild. A tenant's real behaviour is learned in
        // production, by the operator, not by whoever compiled the binary.
        //
        // Left at the default here on purpose. This rig's measured corpus is p50
        // 491 content bytes and max 904, so twenty requests is 18 KB and the
        // REQUEST COUNT closed all 5,608 batches; the byte ceiling has never
        // fired once. There is therefore no measurement on this corpus that
        // would justify moving it, and moving it anyway would be tuning against
        // a number nobody has observed.
        long envelope = this.options.Setting(
            "MaxBatchContentBytes", GraphBatchWriter.DefaultMaxBatchContentBytes);

        if (envelope <= 0)
        {
            this.log.Warning(
                "Settings:MaxBatchContentBytes was {Requested}, which is not a size; using {Used}.",
                envelope,
                GraphBatchWriter.DefaultMaxBatchContentBytes);

            envelope = GraphBatchWriter.DefaultMaxBatchContentBytes;
        }

        return this.batchWriter ??= new GraphBatchWriter(
            this.graph,
            this.options.Graph.ConnectionId,
            summary,
            this.log,
            throttle => this.store.RecordThrottle(throttle),
            envelope);
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
        var chunk = new List<Prepared>(this.lookupChunkSize);
        long chunkBytes = 0;
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
                chunkBytes += prepared.Value.ContentBytes;

                // Whichever comes first. See DefaultLookupChunkBytes: the count
                // closes the chunk on small rows, the ceiling closes it on large
                // ones, and neither has to be tuned for the other's corpus.
                if (chunk.Count >= this.lookupChunkSize || chunkBytes >= this.lookupChunkBytes)
                {
                    await this.FlushWindowAsync(source, chunk, summary, cancellationToken);
                    chunk.Clear();
                    chunkBytes = 0;
                }
            }
        }
        }
        catch (Exception readFailure)
        {
            try
            {
                await this.FlushWindowAsync(source, chunk, summary, CancellationToken.None);
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
        await this.FlushWindowAsync(source, chunk, summary, cancellationToken);
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
        var queue = Channel.CreateBounded<WriteChunk>(new BoundedChannelOptions(writers * 2)
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
                var chunk = new List<Prepared>(this.lookupChunkSize);
                long chunkBytes = 0;

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
                        chunkBytes += prepared.Value.ContentBytes;

                        if (chunk.Count >= this.lookupChunkSize || chunkBytes >= this.lookupChunkBytes)
                        {
                            // A fresh list per chunk. Handing the same one to the
                            // channel and clearing it would let a writer read a
                            // chunk the reader is already refilling - a data race
                            // whose symptom is items silently written twice or not
                            // at all, depending on timing.
                            await this.PublishWindowAsync(queue.Writer, chunk, failed.Token);
                            chunk = new List<Prepared>(this.lookupChunkSize);
                            chunkBytes = 0;
                        }
                    }

                    if (chunk.Count > 0)
                    {
                        await this.PublishWindowAsync(queue.Writer, chunk, failed.Token);
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
                        // An EMPTY unchanged set, and deliberately so. Resolving
                        // the window needs an async store call and this is a
                        // synchronous best-effort rescue inside a catch, with a
                        // source that has already died. An empty set means every
                        // buffered row is treated as changed and written, which
                        // costs a few redundant writes and cannot lose one -
                        // whereas skipping the rescue to keep the lookup would
                        // lose rows the source had already handed over.
                        queue.Writer.TryWrite(new WriteChunk(chunk, EmptyUnchanged));
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
                    await foreach (WriteChunk batch in queue.Reader.ReadAllAsync(failed.Token))
                    {
                        await this.FlushChunkAsync(
                            source, batch.Rows, batch.Unchanged, summary, failed.Token);
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

        if (this.sensitivity.IsEnabled && !this.ApplySensitivity(mapped, summary))
        {
            return null;
        }

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
            summary.CountDuplicate(mapped.ItemType);
            this.log.Warning(
                "Item {ItemId} appeared more than once (row {RowOrdinal}); the later row overwrote the earlier item.",
                mapped.Id,
                rowOrdinal);
        }

        return new Prepared(mapped, item, contentHash, aclHash, contentBytes, rowStarted);
    }

    /// <summary>Applies the sensitivity policy to one row, publishing or refusing it.</summary>
    /// <param name="mapped">The row as the source yielded it. Its properties may gain the label.</param>
    /// <param name="summary">The run's counters.</param>
    /// <returns>True to carry on preparing the item; false when it must not be written.</returns>
    /// <remarks>
    /// DELIBERATELY OUTSIDE THE try IN <see cref="Prepare"/>. That block converts
    /// any exception into a run-ending InvalidOperationException naming the row,
    /// which is right for a mapping fault and wrong for a security decision: a
    /// policy that threw would take down the crawl rather than decline one item.
    /// Nothing here can throw - the policy is compiled, the lookup is a
    /// dictionary and the write is into a dictionary the engine owns.
    ///
    /// IT RUNS BEFORE THE ACL RESOLVE, WHICH IS BOTH CHEAPER AND MORE CORRECT.
    /// Cheaper because a refused item costs a dictionary probe instead of a
    /// group resolution, a truncation and two hashes. More correct because an
    /// item that must not be indexed must not be indexed whether or not anybody
    /// could have been granted it.
    ///
    /// THE LABEL IS ADDED BEFORE THE HASHES ARE TAKEN, and that ordering is what
    /// makes a relabelling detectable. ItemHasher.HashContent covers
    /// mapped.Properties, so an item whose classification changed hashes
    /// differently and is rewritten; a label added after hashing would be
    /// published once and then never corrected on any later run.
    ///
    /// It also runs on a DRY RUN, which is the only way to answer "how much of
    /// this corpus would we refuse" before committing to the mode.
    /// </remarks>
    private bool ApplySensitivity(PushItem mapped, PushSummary summary)
    {
        SensitivityVerdict verdict = this.sensitivity.Evaluate(mapped.Classifications);

        if (!verdict.Indexable)
        {
            summary.CountRefusedByLabel(mapped.ItemType);

            // Warning rather than Information. This is not routine housekeeping
            // like a lease refusal - something the source holds was declined,
            // and the count of these is what somebody is asked to evidence.
            // The classification name is metadata, not content, so naming it is
            // within the logging policy; the row itself still is not.
            this.log.Warning(
                "Item {ItemId} was NOT indexed: {Reason}.",
                mapped.Id,
                verdict.Reason);

            return false;
        }

        if (verdict.Label is not null)
        {
            mapped.Properties[this.sensitivity.Property] = verdict.Label;
        }

        return true;
    }



    /// <summary>One write's worth of rows, and the lookup answer they came with.</summary>
    /// <remarks>
    /// The channel carries this rather than a bare list because the reader now
    /// resolves a whole window against the state store before cutting it up, and
    /// the answer has to travel with the pieces. Every chunk cut from one window
    /// shares one set; it is read-only from the moment it is published, so the
    /// sharing needs no lock.
    /// </remarks>
    private readonly record struct WriteChunk(List<Prepared> Rows, IReadOnlySet<string> Unchanged);
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
            //
            // DO NOT CACHE THIS. The grants cannot change between items, so one
            // instance reused across the run reads as the obvious optimisation,
            // and it was written that way. Acl is a Graph SDK model carrying a
            // backing store, and hanging one instance off every ExternalItem
            // made a pilot write 441 of 1,118 items and refuse 677 with
            //
            //     DeserializationError | The Value field is required.
            //
            // Item one carried a complete ACL and every item after it carried a
            // valueless one. Rebuilding per item took the same run to 1,118
            // written and 0 failed. The allocation is a few objects per item
            // against a write measured in hundreds of milliseconds; the sharing
            // is not worth having at any price.
            //
            // The regression test in PushSourceTests states this invariant but
            // cannot enforce it - see the comment there. This paragraph is the
            // guard.
            return BuildAcl(this.options);
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
                // SAME DEFECT AS THE BATCH PATH, AND THIS IS THE PATH THAT KEPT
                // IT. Every attempt after the first re-serializes this same
                // ExternalItem, and a backed model emits only what changed since
                // the last serialization - nothing - so the retry arrives with
                // no ACL and Graph refuses it 400 NullOrEmptyValue. The batch
                // writer was fixed for this; this loop was not, and a chunk of
                // one always comes here.
                GraphModelReset.ForSerialization(item);

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
