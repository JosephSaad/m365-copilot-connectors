// ---------------------------------------------------------------------------
// DbPushSource.cs
// Reads a connector's query on any ADO.NET provider and yields one item per row.
//
// This is SqlPushSource with the concrete SqlClient types widened to their
// System.Data.Common base classes and the connection string delegated to the
// connector. The read loop, the ordinal-only failure message and the skip
// accounting are deliberately identical: they are behaviours the runbook quotes
// and the tests pin, and a second relational family that reported row failures
// differently would be a second thing to learn for no gain.
//
// RequiresOrderedCommit is false here for the same reason it is false in
// SqlPushSource - this source re-reads its whole query every run and keeps no
// marker - and it carries the same warning. A connector on this path that adds
// incremental reads must delete that override in the same change, or the
// checkpoint it introduces can outrun the writes it is meant to describe.
// ---------------------------------------------------------------------------

namespace PushCore.Db;

using System.Data.Common;
using System.Runtime.CompilerServices;
using Connector.Security.Configuration;

/// <summary>Reads a connector's query on its own provider and yields one item per row.</summary>
public sealed class DbPushSource : IPushSource
{
    private readonly IDbPushConnector connector;
    private readonly PushSourceContext context;

    private int skipped;

    /// <summary>Initializes a new instance of the <see cref="DbPushSource"/> class.</summary>
    /// <param name="connector">The connector supplying the provider, query and mapping.</param>
    /// <param name="context">Configuration, credential and logger.</param>
    public DbPushSource(IDbPushConnector connector, PushSourceContext context)
    {
        this.connector = connector;
        this.context = context;
    }

    /// <inheritdoc/>
    public int Skipped => this.skipped;

    /// <inheritdoc/>
    /// <remarks>See the file header: no marker here, so nothing for out-of-order
    /// completion to move past.</remarks>
    public bool RequiresOrderedCommit => false;

    /// <inheritdoc/>
    public async IAsyncEnumerable<PushItem> ReadAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        PushOptions options = this.context.Options;
        string query = this.connector.BuildQuery(options);

        // Resolved here rather than in the connector so that every credential on
        // this path goes through the caching provider the engine redacts around.
        string? secret = null;
        string? key = this.connector.SecretKey;

        if (!string.IsNullOrWhiteSpace(key))
        {
            string? name = options.KeyVault.SecretName(key);

            if (!string.IsNullOrWhiteSpace(name))
            {
                // Secrets is null whenever no vault is configured, which is the
                // normal case for a connector that authenticates with Kerberos or
                // a wallet. A connector that declared a SecretKey and got here
                // anyway was constructed around startup validation rather than
                // through it, so say that rather than dereferencing null.
                if (this.context.Secrets is null)
                {
                    throw new InvalidOperationException(
                        $"Connector '{this.connector.Key}' authenticates with the secret '{name}', but no " +
                        "secret provider is configured. DbSourceRules requires KeyVault:Uri for a connector " +
                        "that declares a SecretKey, so this connector was built without running that check.");
                }

                secret = await this.context.Secrets.GetSecretAsync(name, cancellationToken);
            }
        }

        DbConnection? connection = this.connector.Factory.CreateConnection()
            ?? throw new InvalidOperationException(
                $"The provider factory for connector '{this.connector.Key}' returned no connection. " +
                "A DbProviderFactory that cannot create a connection is a packaging fault: check that " +
                "the provider assembly shipped with the connector.");

        await using (connection)
        {
            connection.ConnectionString = this.connector.BuildConnectionString(options, secret);

            await connection.OpenAsync(cancellationToken);

            // Before the query, never after: a refusal has to happen while
            // nothing has been read, so a guarded source cannot half-crawl.
            await this.connector.GuardAsync(connection, options, cancellationToken);

            await using DbCommand command = connection.CreateCommand();
            command.CommandText = query;

            // Query timeout, not connect timeout: a full-corpus read of a large
            // view legitimately outlives the seconds a connection attempt gets.
            command.CommandTimeout = options.DataSource.CommandTimeoutSeconds;

            await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

            int rowOrdinal = 0;

            while (await reader.ReadAsync(cancellationToken))
            {
                rowOrdinal++;

                PushItem? mapped;

                try
                {
                    mapped = this.connector.MapRow(reader, options);
                }
                catch (Exception ex)
                {
                    // Locate the failure without logging row content: the ordinal
                    // is policy-safe and turns "which row killed the run" from
                    // bisection into a lookup.
                    throw new InvalidOperationException(
                        $"Row {rowOrdinal} could not be mapped. " +
                        "The row's content is deliberately not logged; find it in the source by ordinal.",
                        ex);
                }

                if (mapped is null)
                {
                    // The connector looked at the row and declined it. Counted so
                    // the summary still adds up against the source.
                    this.skipped++;
                    continue;
                }

                yield return mapped;
            }
        }
    }

    /// <inheritdoc/>
    public ValueTask OnItemCommittedAsync(PushItem item, CancellationToken cancellationToken)
    {
        // Nothing to record: this source reads everything the query returns on
        // every run. See the file header.
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask OnCrawlCompletedAsync(CancellationToken cancellationToken)
    {
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        // The command and reader are scoped to the iterator and the connection to
        // the await using inside it, so ending the enumeration - normally or by
        // exception - closes all three.
        return ValueTask.CompletedTask;
    }
}
