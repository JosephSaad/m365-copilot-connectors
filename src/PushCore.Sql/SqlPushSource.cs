// ---------------------------------------------------------------------------
// SqlPushSource.cs
// A query and a row mapping, presented to the engine as a source.
//
// This is the code that used to be the middle of PushEngine.PushItemsAsync, and
// it behaves the same way it did there: open a connection through the shared
// factory, run the connector's query with the command timeout rather than the
// connect timeout, map each row, and let a mapping failure name the row ordinal
// without logging the row.
//
// There is no watermark here. A SQL push re-reads its whole query every run -
// the model has always been "the query decides what exists" - so the commit
// callbacks have nothing to record. They are still the engine's to call: a
// future SQL connector that wants incremental reads implements them here and
// gets the failed-crawl guarantee for free, rather than inventing its own.
// ---------------------------------------------------------------------------

namespace PushCore.Sql;

using System.Runtime.CompilerServices;
using Connector.Security.Configuration;
using Microsoft.Data.SqlClient;

/// <summary>Reads a connector's query and yields one item per row.</summary>
public sealed class SqlPushSource : IPushSource
{
    private readonly ISqlPushConnector connector;
    private readonly PushSourceContext context;

    private int skipped;

    /// <summary>Initializes a new instance of the <see cref="SqlPushSource"/> class.</summary>
    /// <param name="connector">The connector supplying the query and the mapping.</param>
    /// <param name="context">Configuration, credential and logger.</param>
    public SqlPushSource(ISqlPushConnector connector, PushSourceContext context)
    {
        this.connector = connector;
        this.context = context;
    }

    /// <inheritdoc/>
    public int Skipped => this.skipped;

    /// <inheritdoc/>
    /// <remarks>
    /// False, and it is the file header above that earns it: a SQL push re-reads
    /// its whole query every run, so OnItemCommittedAsync below has nothing to
    /// record and does nothing. There is no marker here, so there is no marker
    /// for out-of-order completion to move past, and the engine may write these
    /// items several at a time.
    ///
    /// If a future SQL connector implements incremental reads, it implements
    /// them here - and it must delete this override in the same change, or the
    /// checkpoint it introduces can outrun the writes it is meant to describe.
    /// </remarks>
    public bool RequiresOrderedCommit => false;

    /// <inheritdoc/>
    public async IAsyncEnumerable<PushItem> ReadAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        PushOptions options = this.context.Options;
        string query = this.connector.BuildQuery(options);

        var connections = new Connector.Security.Sql.SqlConnectionFactory(
            options.DataSource,
            options.Environment,
            this.context.Secrets,
            options.KeyVault.SecretName(KeyVaultOptions.SqlPasswordKey),
            this.context.Credential,
            this.context.Log);

        await using SqlConnection connection = await connections.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(query, connection);

        // Query timeout, not connect timeout: a full-corpus read of a large view
        // legitimately outlives the 30 seconds a connection attempt gets.
        command.CommandTimeout = options.DataSource.CommandTimeoutSeconds;

        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

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
                // Locate the failure without logging row content: the ordinal is
                // policy-safe and turns "which row killed the run" from bisection
                // into a lookup.
                throw new InvalidOperationException(
                    $"Row {rowOrdinal} could not be mapped. " +
                    "The row's content is deliberately not logged; find it in the source by ordinal.",
                    ex);
            }

            if (mapped is null)
            {
                // The connector looked at the row and declined it - a null key, a
                // record it does not index. Counted so the summary still adds up
                // against the source.
                this.skipped++;
                continue;
            }

            yield return mapped;
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
        // The connection, command and reader are scoped to the iterator, so
        // ending the enumeration - normally or by exception - closes them.
        return ValueTask.CompletedTask;
    }
}
