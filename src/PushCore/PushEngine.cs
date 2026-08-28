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
using Serilog;

/// <summary>Runs one connector against one connection.</summary>
public sealed class PushEngine
{
    private const int MaxWriteAttempts = 5;

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
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string lastItemId = "(none)";
        int rowOrdinal = 0;

        // Driven by hand rather than with await foreach for one reason: the time
        // spent inside MoveNextAsync is the time the SOURCE took to produce the
        // row, and await foreach leaves nowhere to stand to measure it. The body
        // below is the loop body it has always been, and the using block is
        // scoped to the loop so the enumerator is still disposed on the way out
        // of it - before OnCrawlCompletedAsync, exactly as await foreach did.
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
                        summary.Skipped++;
                        this.log.Warning(
                            "Item {ItemId} has no grants and was not written. " +
                            "The source could resolve no group for it.",
                            mapped.Id);
                        continue;
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
                    summary.Duplicates++;
                    this.log.Warning(
                        "Item {ItemId} appeared more than once (row {RowOrdinal}); the later row overwrote the earlier item.",
                        mapped.Id,
                        rowOrdinal);
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

                    // Measured like any other row. A dry run writes nothing, so what it
                    // reports IS the whole non-Graph cost of the pipeline - the cheapest
                    // way to find out how much of a slow run is not Graph's fault, and
                    // it needs no tenant at all.
                    summary.Timing.RowTotal.Add(PushTiming.MicrosecondsSince(rowStarted));

                    // No commit callback: a dry run writes nothing, so it must leave
                    // the watermark exactly where it found it.
                    continue;
                }

                await this.WriteWithRetryAsync(mapped.Id, item, summary, cancellationToken);

                long commitStarted = PushTiming.Now();

                lastItemId = mapped.Id;
                summary.Count(mapped.ItemType);
                this.log.Information("Indexed {ItemId} ({ItemType}).", mapped.Id, mapped.ItemType);

                // Only now. Everything above can throw, and every one of those paths
                // leaves the source's marker on the last item that really landed.
                await source.OnItemCommittedAsync(mapped, cancellationToken);

                summary.Timing.Commit.Add(PushTiming.MicrosecondsSince(commitStarted));
                summary.Timing.RowTotal.Add(PushTiming.MicrosecondsSince(rowStarted));
            }
        }

        // Candidates the source itself declined, so the summary reconciles
        // against the source rather than only against what was written.
        summary.Skipped += source.Skipped;

        if (!this.dryRun)
        {
            // Reached only by falling out of the loop, which means the
            // enumeration ended without throwing and every write returned.
            await source.OnCrawlCompletedAsync(cancellationToken);
        }

        return summary;
    }

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
            summary.Truncated++;

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
                        summary.ThrottleWaits++;
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
