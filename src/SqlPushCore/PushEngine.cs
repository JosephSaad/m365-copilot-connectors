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
using SqlTicketsConnector.Security.Content;
using SqlTicketsConnector.Security.Schema;
using SqlTicketsConnector.Security.Sql;

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
            this.log.Information(
                "Dry run: reading and mapping {DisplayName}, writing nothing to Graph.",
                this.connector.DisplayName);
        }
        else
        {
            await this.EnsureConnectionAsync();
            await this.EnsureSchemaAsync();
        }

        return await this.PushItemsAsync(cancellationToken);
    }

    /// <summary>Creates the external connection. Idempotent.</summary>
    /// <returns>A task for the operation.</returns>
    public async Task EnsureConnectionAsync()
    {
        string connectionId = this.options.Graph.ConnectionId;

        try
        {
            ExternalConnection? existing = await this.graph.External.Connections[connectionId].GetAsync();

            this.log.Information(
                "Connection {ConnectionId} already exists. State {State}.", connectionId, existing?.State);
            return;
        }
        catch (ODataError ex) when (ex.ResponseStatusCode == 404)
        {
            // Not found, fall through to create.
        }

        await this.graph.External.Connections.PostAsync(new ExternalConnection
        {
            Id = connectionId,
            Name = this.options.Graph.ConnectionName,
            Description = this.options.Graph.Description,
        });

        this.log.Information("Connection {ConnectionId} created.", connectionId);
    }

    /// <summary>Registers the schema and polls until the connection is Ready.</summary>
    /// <returns>A task for the operation.</returns>
    public async Task EnsureSchemaAsync()
    {
        string connectionId = this.options.Graph.ConnectionId;

        ExternalConnection? connection = await this.graph.External.Connections[connectionId].GetAsync();

        if (connection?.State == ConnectionState.Ready)
        {
            this.log.Information("Schema already registered.");
            return;
        }

        Schema schema = this.connector.BuildSchema();

        await this.graph.External.Connections[connectionId].Schema.PatchAsync(schema);

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
            await Task.Delay(TimeSpan.FromSeconds(30));

            ConnectionState? state = (await this.graph.External.Connections[connectionId].GetAsync())?.State;
            this.log.Information("Connection state {State}.", state);

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

        await using SqlConnection connection = await this.connections.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(query, connection);
        command.CommandTimeout = this.options.DataSource.ConnectTimeoutSeconds;

        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            PushItem? mapped = this.connector.MapRow(reader, this.options);

            if (mapped is null)
            {
                summary.Skipped++;
                continue;
            }

            ExternalItem item = this.BuildItem(mapped, acl, summary);

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

                summary.Count(mapped.ItemType);
                continue;
            }

            await this.WriteWithRetryAsync(mapped.Id, item, summary, cancellationToken);

            summary.Count(mapped.ItemType);
            this.log.Information("Indexed {ItemId} ({ItemType}).", mapped.Id, mapped.ItemType);
        }

        return summary;
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
                Value = id.Trim(),
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
            catch (ODataError ex) when (ex.ResponseStatusCode == 429 && attempt < MaxWriteAttempts)
            {
                TimeSpan wait = GraphThrottling.RetryAfter(ex) ?? GraphThrottling.Backoff(attempt);
                summary.ThrottleWaits++;

                this.log.Warning(
                    "Throttled writing {ItemId}. Waiting {Seconds}s before attempt {Next} of {Max}.",
                    itemId,
                    (int)wait.TotalSeconds,
                    attempt + 1,
                    MaxWriteAttempts);

                await Task.Delay(wait, cancellationToken);
            }
        }
    }
}
