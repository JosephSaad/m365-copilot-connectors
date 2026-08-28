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

    private readonly IPushConnector connector;
    private readonly PushOptions options;
    private readonly GraphServiceClient graph;
    private readonly ILogger log;
    private readonly bool dryRun;

    private List<Acl>? sharedAcl;

    /// <summary>Initializes a new instance of the <see cref="PushEngine"/> class.</summary>
    /// <param name="connector">The source description.</param>
    /// <param name="options">Validated configuration.</param>
    /// <param name="graph">An authenticated Graph client.</param>
    /// <param name="log">Where to report progress.</param>
    /// <param name="dryRun">When true, reads and maps but writes nothing.</param>
    public PushEngine(
        IPushConnector connector,
        PushOptions options,
        GraphServiceClient graph,
        ILogger log,
        bool dryRun)
    {
        this.connector = connector;
        this.options = options;
        this.graph = graph;
        this.log = log;
        this.dryRun = dryRun;
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
        await using IPushSource source = this.connector.CreateSource(context);

        return await this.PushItemsAsync(source, cancellationToken);
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

        return summary;
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
        string lastItemId = "(none)";
        int rowOrdinal = 0;

        // Driven by hand rather than with await foreach for one reason: the time
        // spent inside MoveNextAsync is the time the SOURCE took to produce the
        // row, and await foreach leaves nowhere to stand to measure it. The using
        // block is scoped to the loop so the enumerator is still disposed on the
        // way out of it - before OnCrawlCompletedAsync, exactly as await foreach
        // did.
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

                ExternalItem? item = this.Prepare(mapped, rowOrdinal, lastItemId, written, summary);

                if (item is null)
                {
                    continue;
                }

                if (this.dryRun)
                {
                    // Item ID, type and sizes only. The content is customer data and
                    // does not go to the console any more than it goes to the log.
                    this.log.Information(
                        "Would write {ItemId} ({ItemType}): {PropertyCount} properties, {ContentBytes} content bytes, " +
                        "{AclCount} ACL entr(y/ies).",
                        mapped.Id,
                        mapped.ItemType,
                        mapped.Properties.Count,
                        item.Content?.Value?.Length ?? 0,
                        item.Acl?.Count ?? 0);

                    lastItemId = mapped.Id;
                    summary.Count(mapped.ItemType);

                    // Measured like any other row. A dry run writes nothing, so what
                    // it reports IS the whole non-Graph cost of the pipeline - the
                    // cheapest way to find out how much of a slow run is not Graph's
                    // fault, and it needs no tenant at all.
                    summary.Timing.RowTotal.Add(PushTiming.MicrosecondsSince(rowStarted));

                    // No commit callback: a dry run writes nothing, so it must leave
                    // the watermark exactly where it found it.
                    continue;
                }

                await this.WriteWithRetryAsync(mapped.Id, item, summary, cancellationToken);

                lastItemId = mapped.Id;
                await this.CommitAsync(source, mapped, summary, rowStarted, cancellationToken);
            }
        }
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
        var queue = Channel.CreateBounded<Pending>(new BoundedChannelOptions(writers * 2)
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

                        ExternalItem? item = this.Prepare(mapped, rowOrdinal, lastItemId, written, summary);

                        if (item is null)
                        {
                            continue;
                        }

                        lastItemId = mapped.Id;
                        await queue.Writer.WriteAsync(new Pending(mapped, item, rowStarted), failed.Token);
                    }

                    queue.Writer.Complete();
                }
                catch (Exception ex)
                {
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
                    await foreach (Pending pending in queue.Reader.ReadAllAsync(failed.Token))
                    {
                        await this.WriteWithRetryAsync(
                            pending.Mapped.Id, pending.Item, summary, failed.Token);

                        await this.CommitAsync(
                            source, pending.Mapped, summary, pending.StartedAt, failed.Token);
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
    /// <returns>The item to write, or null when the source could grant it to nobody.</returns>
    private ExternalItem? Prepare(
        PushItem mapped, int rowOrdinal, string lastItemId, HashSet<string> written, PushSummary summary)
    {
        ExternalItem item;

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
                summary.CountSkipped();
                this.log.Warning(
                    "Item {ItemId} has no grants and was not written. " +
                    "The source could resolve no group for it.",
                    mapped.Id);
                return null;
            }

            item = this.BuildItem(mapped, acl, summary);
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
            summary.CountDuplicate();
            this.log.Warning(
                "Item {ItemId} appeared more than once (row {RowOrdinal}); the later row overwrote the earlier item.",
                mapped.Id,
                rowOrdinal);
        }

        return item;
    }

    /// <summary>Counts a written item and tells the source it landed.</summary>
    /// <param name="source">The opened source.</param>
    /// <param name="mapped">The item that was written.</param>
    /// <param name="summary">The run's counters.</param>
    /// <param name="rowStarted">When this row began, for the timing table.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task for the operation.</returns>
    /// <remarks>
    /// Reached only after WriteWithRetryAsync returned. Everything that can throw
    /// happens before this, and every one of those paths leaves the source's
    /// marker on the last item that really landed.
    /// </remarks>
    private async Task CommitAsync(
        IPushSource source,
        PushItem mapped,
        PushSummary summary,
        long rowStarted,
        CancellationToken cancellationToken)
    {
        long commitStarted = PushTiming.Now();

        int total = summary.Count(mapped.ItemType);

        // Debug, not Information, for two reasons that point the same way. The
        // runbook already documents the per-item line as what raising the level
        // to Debug BUYS you - this engine was the one component contradicting
        // that. And at Information it is a synchronous console write plus a file
        // write per row, on the critical path of every row, for a line nobody
        // reads on a healthy run. The evidence is not lost; it is where the
        // documentation always said it was.
        this.log.Debug("Indexed {ItemId} ({ItemType}).", mapped.Id, mapped.ItemType);

        if (total % ProgressEvery == 0)
        {
            // What replaces it at Information: enough to see a long run moving,
            // amortised over ProgressEvery rows instead of paid on every one.
            this.log.Information("Indexed {Count} items so far.", total);
        }

        await source.OnItemCommittedAsync(mapped, cancellationToken);

        summary.Timing.Commit.Add(PushTiming.MicrosecondsSince(commitStarted));
        summary.Timing.RowTotal.Add(PushTiming.MicrosecondsSince(rowStarted));
    }

    /// <summary>One prepared item, waiting for a writer.</summary>
    /// <param name="Mapped">The row as the source yielded it.</param>
    /// <param name="Item">The item to write.</param>
    /// <param name="StartedAt">When this row began, for the timing table.</param>
    private readonly record struct Pending(PushItem Mapped, ExternalItem Item, long StartedAt);

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

    private ExternalItem BuildItem(PushItem mapped, List<Acl> acl, PushSummary summary)
    {
        ExternalSchemaRules.ValidateItemId(mapped.Id);

        TruncationResult content = ContentTruncator.Truncate(
            mapped.Content ?? string.Empty, this.options.DataSource.MaxContentBytes);

        // Taken here because this is the only place the real byte count exists -
        // Value.Length would be characters, and the gap matters when the question
        // is whether twenty of these fit in one 30 MB Graph $batch.
        summary.Timing.ContentBytes.Add(content.FinalBytes);

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
