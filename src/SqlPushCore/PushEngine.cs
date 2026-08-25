// ---------------------------------------------------------------------------
// PushEngine.cs
// Everything that is the same whatever the source is.
//
// Create the connection, register the schema and wait for Ready, read the
// query, map each row through the connector, truncate, attach the ACL, PUT with
// backoff, count. A connector supplies four things and none of this.
//
// Two behaviours are worth calling out because they are policy, not mechanics:
//
//   * Nothing here logs a property value or content. Item ID, item type and
//     byte counts only. The row is customer data and the log is a file on a
//     server that a wider group can read than can read the database.
//
//   * A push never deletes. A row excluded from the query - soft deleted,
//     filtered out, outside MaxItems - leaves its item in the index. That is a
//     property of this model rather than an oversight, and
//     deploy/Compare-SourceToIndex.ps1 is how the orphans are found.
// ---------------------------------------------------------------------------

namespace SqlPushCore;

using Microsoft.Data.SqlClient;
using Microsoft.Graph;
using Microsoft.Graph.Models.ExternalConnectors;
using Microsoft.Graph.Models.ODataErrors;
using Serilog;
using SqlConnector.Security.Content;
using SqlConnector.Security.Schema;
using SqlConnector.Security.Sql;

/// <summary>Runs one connector against one connection.</summary>
public sealed class PushEngine
{
    private const int MaxWriteAttempts = 5;

    private readonly IPushConnector connector;
    private readonly PushOptions options;
    private readonly GraphServiceClient graph;
    private readonly SqlConnectionFactory connections;
    private readonly ILogger log;
    private readonly bool dryRun;

    /// <summary>Initializes a new instance of the <see cref="PushEngine"/> class.</summary>
    /// <param name="connector">The source description.</param>
    /// <param name="options">Validated configuration.</param>
    /// <param name="graph">An authenticated Graph client.</param>
    /// <param name="connections">The SQL connection factory.</param>
    /// <param name="log">Where to report progress.</param>
    /// <param name="dryRun">When true, reads and maps but writes nothing.</param>
    public PushEngine(
        IPushConnector connector,
        PushOptions options,
        GraphServiceClient graph,
        SqlConnectionFactory connections,
        ILogger log,
        bool dryRun)
    {
        this.connector = connector;
        this.options = options;
        this.graph = graph;
        this.connections = connections;
        this.log = log;
        this.dryRun = dryRun;
    }

    /// <summary>Creates the connection and schema if needed, then pushes every row.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What the run wrote.</returns>
    public async Task<PushSummary> RunAsync(CancellationToken cancellationToken = default)
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
        }
        else
        {
            await this.EnsureConnectionAsync(cancellationToken);
            await this.EnsureSchemaAsync(cancellationToken);
        }

        return await this.PushItemsAsync(cancellationToken);
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

        if (connection?.State == ConnectionState.Ready)
        {
            // The connection exists and is live - but is it OURS? A registered
            // schema cannot be replaced, and the PUT is an upsert, so pushing
            // into a connection another connector registered would silently
            // corrupt its index. Instead of each connector naming the others'
            // connection IDs - a list that goes stale the day a connector is
            // added - compare what is actually registered against what this
            // connector builds. Any foreign property means a foreign connection.
            Schema? registered = null;

            try
            {
                registered = await this.graph.External.Connections[connectionId].Schema
                    .GetAsync(cancellationToken: cancellationToken);
            }
            catch (ODataError ex) when (ex.ResponseStatusCode == 404)
            {
                // Ready with no readable schema: nothing to compare.
            }

            VerifySchemaOwnership(connectionId, this.connector.BuildSchema(), registered, this.log);

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

        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);

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
                TimeSpan wait = GraphThrottling.RetryAfter(ex) ?? TimeSpan.FromSeconds(30);
                this.log.Warning(
                    "Status poll returned {Status}; polling again in {Seconds}s.",
                    ex.ResponseStatusCode,
                    (int)wait.TotalSeconds);
                await Task.Delay(wait, cancellationToken);
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

    /// <summary>Reads the source and writes one item per row.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What the run wrote.</returns>
    public async Task<PushSummary> PushItemsAsync(CancellationToken cancellationToken = default)
    {
        string query = this.connector.BuildQuery(this.options);

        // Entra group principals, never Everyone. Every item in a connection
        // gets the same ACL: a child row is at least as sensitive as its parent,
        // so there is no argument for trimming them differently here.
        List<Acl> acl = BuildAcl(this.options);

        var summary = new PushSummary();
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string lastItemId = "(none)";
        int rowOrdinal = 0;

        await using SqlConnection connection = await this.connections.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(query, connection);
        // Query timeout, not connect timeout: a full-corpus read of a large view
        // legitimately outlives the 30 seconds a connection attempt gets.
        command.CommandTimeout = this.options.DataSource.CommandTimeoutSeconds;

        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            rowOrdinal++;

            PushItem? mapped;
            ExternalItem item;

            try
            {
                mapped = this.connector.MapRow(reader, this.options);

                if (mapped is null)
                {
                    summary.Skipped++;
                    continue;
                }

                item = this.BuildItem(mapped, acl, summary);
            }
            catch (Exception ex)
            {
                // Locate the failure without logging row content: ordinal and the
                // neighbouring item ID are policy-safe and turn "which row killed
                // the run" from bisection into a lookup.
                throw new InvalidOperationException(
                    $"Row {rowOrdinal} could not be mapped (the item before it was {lastItemId}). " +
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
                    acl.Count);

                lastItemId = mapped.Id;
                summary.Count(mapped.ItemType);
                continue;
            }

            await this.WriteWithRetryAsync(mapped.Id, item, summary, cancellationToken);

            lastItemId = mapped.Id;
            summary.Count(mapped.ItemType);
            this.log.Information("Indexed {ItemId} ({ItemType}).", mapped.Id, mapped.ItemType);
        }

        return summary;
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

    private ExternalItem BuildItem(PushItem mapped, List<Acl> acl, PushSummary summary)
    {
        ExternalSchemaRules.ValidateItemId(mapped.Id);

        TruncationResult content = ContentTruncator.Truncate(
            mapped.Content ?? string.Empty, this.options.DataSource.MaxContentBytes);

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
            Properties = new Properties { AdditionalData = mapped.Properties },
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
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await this.graph.External.Connections[this.options.Graph.ConnectionId]
                    .Items[itemId].PutAsync(item, cancellationToken: cancellationToken);
                return;
            }
            catch (ODataError ex) when (
                ex.ResponseStatusCode is 429 or 502 or 503 or 504 && attempt < MaxWriteAttempts)
            {
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

                await Task.Delay(wait, cancellationToken);
            }
            catch (HttpRequestException ex) when (attempt < MaxWriteAttempts)
            {
                TimeSpan wait = GraphThrottling.Backoff(attempt);

                this.log.Warning(
                    "Write of {ItemId} failed in transit ({Message}). Waiting {Seconds}s before attempt " +
                    "{Next} of {Max}.",
                    itemId,
                    ex.Message,
                    (int)wait.TotalSeconds,
                    attempt + 1,
                    MaxWriteAttempts);

                await Task.Delay(wait, cancellationToken);
            }
            catch (ODataError ex)
            {
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
}
