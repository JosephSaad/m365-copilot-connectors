// ---------------------------------------------------------------------------
// HivePushSource.cs
// A Hive or Impala table, read in watermark order, and only if Ranger says it
// may be indexed at all.
//
// The routing check happens before the query, not after it. A table carrying a
// row filter or a column mask must not be READ by this connector, never mind
// written: the rows the service account sees are the rows ITS filter admits,
// and indexing those would publish one user's view of the data to everyone
// granted the item. So the decision comes first, and a refusal means the source
// yields nothing and says why.
//
// The query is built rather than configured. A configured query would be a
// place for someone to put a join that the routing check knows nothing about,
// and the whole guarantee here is that what is read is exactly the table whose
// policies were evaluated.
// ---------------------------------------------------------------------------

namespace CdpConnector.Source.Hive;

using System.Runtime.CompilerServices;
using CdpConnector.Source.Ranger;
using CdpConnector.Source.Watermark;
using PushCore;
using Serilog;

/// <summary>Reads one table and yields one item per row.</summary>
public sealed class HivePushSource : IPushSource
{
    private readonly CdpSettings settings;
    private readonly PushOptions options;
    private readonly IHiveRowReader reader;
    private readonly RoutingEvaluator routing;
    private readonly IReadOnlyList<PushAclEntry> grants;
    private readonly CheckpointStore checkpoints;
    private readonly CrawlCheckpoint checkpoint;
    private readonly Func<HiveRow, PushOptions, PushItem?> map;
    private readonly ILogger log;

    private int skipped;
    private string pendingMarkerTime = string.Empty;
    private string pendingMarkerKey = string.Empty;

    /// <summary>Initializes a new instance of the <see cref="HivePushSource"/> class.</summary>
    /// <param name="settings">Validated CDP settings.</param>
    /// <param name="options">Validated configuration.</param>
    /// <param name="reader">Runs the query.</param>
    /// <param name="routing">The Ranger verdicts for the SQL service.</param>
    /// <param name="grants">The grants every row of this table carries.</param>
    /// <param name="checkpoints">Where the watermark is kept.</param>
    /// <param name="map">The connector's row mapping.</param>
    /// <param name="log">Where to report progress.</param>
    public HivePushSource(
        CdpSettings settings,
        PushOptions options,
        IHiveRowReader reader,
        RoutingEvaluator routing,
        IReadOnlyList<PushAclEntry> grants,
        CheckpointStore checkpoints,
        Func<HiveRow, PushOptions, PushItem?> map,
        ILogger log)
    {
        this.settings = settings;
        this.options = options;
        this.reader = reader;
        this.routing = routing;
        this.grants = grants;
        this.checkpoints = checkpoints;
        this.checkpoint = checkpoints.Read();
        this.map = map;
        this.log = log;
    }

    /// <inheritdoc/>
    public int Skipped => this.skipped;

    /// <summary>Splits Source:ItemView into its database and table halves.</summary>
    /// <param name="itemView">The configured value, "database.table" or bare.</param>
    /// <returns>The two parts; the database is "default" when unqualified.</returns>
    public static (string Database, string Table) SplitTable(string itemView)
    {
        string[] parts = itemView.Split('.');

        return parts.Length == 2 ? (parts[0], parts[1]) : ("default", itemView);
    }

    /// <summary>Builds the query for one table, in watermark order.</summary>
    /// <param name="settings">Validated CDP settings.</param>
    /// <param name="options">Validated configuration.</param>
    /// <param name="checkpoint">Where the last run got to.</param>
    /// <returns>The HiveQL to run.</returns>
    public static string BuildQuery(CdpSettings settings, PushOptions options, CrawlCheckpoint checkpoint)
    {
        (string database, string table) = SplitTable(options.Source.ItemView);

        string top = options.Source.MaxItems > 0
            ? $" LIMIT {options.Source.MaxItems.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            : string.Empty;

        if (string.IsNullOrWhiteSpace(settings.HiveWatermarkColumn))
        {
            // No audit column: every run reads the whole table. That is exactly
            // what the SQL family does today, and saying so plainly is better
            // than a watermark that silently misses updates.
            return $"SELECT * FROM `{database}`.`{table}`{top}";
        }

        string watermark = settings.HiveWatermarkColumn;
        string key = settings.HiveKeyColumn;

        // The composite resume rule, expressed in HiveQL: strictly after the
        // marker, with ties broken by the key so a row sharing a timestamp with
        // the resume point is not lost. Identifiers are validated as identifiers
        // in configuration; the marker is a literal, so it is quoted and any
        // quote inside it doubled.
        string where = checkpoint.HasMarker
            ? $" WHERE (`{watermark}` > {Literal(checkpoint.MarkerTime)}) " +
              $"OR (`{watermark}` = {Literal(checkpoint.MarkerTime)} AND `{key}` > {Literal(checkpoint.MarkerKey)})"
            : string.Empty;

        return $"SELECT * FROM `{database}`.`{table}`{where} ORDER BY `{watermark}`, `{key}`{top}";
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<PushItem> ReadAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        (string database, string table) = SplitTable(this.options.Source.ItemView);

        RoutingDecision decision = this.routing.EvaluateTable(database, table);

        if (!decision.MayIndex)
        {
            // Not an error. The table has an answer - query it live - and the
            // run reports that answer rather than failing.
            this.log.Warning(
                "{Database}.{Table} is not indexed. {Reason} Ranger polic(y/ies): {PolicyIds}. " +
                "Route this table to a live query under the user's own identity instead.",
                database,
                table,
                decision.Reason,
                string.Join(", ", decision.PolicyIds));

            yield break;
        }

        if (this.grants.Count == 0)
        {
            this.log.Warning(
                "{Database}.{Table} resolves to no Entra group, so nothing from it is indexed.", database, table);

            yield break;
        }

        string query = BuildQuery(this.settings, this.options, this.checkpoint);

        this.log.Information(
            "Reading {Database}.{Table}{Mode}.",
            database,
            table,
            this.checkpoint.HasMarker && !string.IsNullOrWhiteSpace(this.settings.HiveWatermarkColumn)
                ? " from watermark " + this.checkpoint.MarkerTime
                : " in full");

        int rowOrdinal = 0;

        await foreach (HiveRow row in this.reader.QueryAsync(query, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            rowOrdinal++;

            PushItem? item;

            try
            {
                item = this.map(row, this.options);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Row {rowOrdinal} of {database}.{table} could not be mapped. " +
                    "The row's content is deliberately not logged; find it in the source by ordinal.",
                    ex);
            }

            if (item is null)
            {
                this.skipped++;
                continue;
            }

            // Every row of a table shares the table's grants: Ranger's
            // table-wide select is what admitted it, and a row filter - the only
            // thing that would make rows differ - has already disqualified the
            // table from being indexed at all.
            item.Acl = this.grants;

            if (!string.IsNullOrWhiteSpace(this.settings.HiveWatermarkColumn))
            {
                item.Properties["_markerTime"] = row.Text(this.settings.HiveWatermarkColumn);
                item.Properties["_markerKey"] = row.Text(this.settings.HiveKeyColumn);
            }

            yield return item;
        }
    }

    /// <inheritdoc/>
    public ValueTask OnItemCommittedAsync(PushItem item, CancellationToken cancellationToken)
    {
        if (item.Properties.TryGetValue("_markerTime", out object? time) &&
            item.Properties.TryGetValue("_markerKey", out object? key))
        {
            this.pendingMarkerTime = (string)time;
            this.pendingMarkerKey = (string)key;
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask OnCrawlCompletedAsync(CancellationToken cancellationToken)
    {
        CrawlCheckpoint stored = this.checkpoints.Read();

        if (this.pendingMarkerTime.Length > 0)
        {
            stored.MarkerTime = this.pendingMarkerTime;
            stored.MarkerKey = this.pendingMarkerKey;
        }

        stored.RunCount = this.checkpoint.RunCount + 1;
        stored.LastCompletedUtc = DateTimeOffset.UtcNow.ToString("o");
        this.checkpoints.Write(stored);

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        return this.reader.DisposeAsync();
    }

    /// <summary>Quotes a value as a HiveQL string literal.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The literal.</returns>
    private static string Literal(string value)
    {
        return "'" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
                          .Replace("'", "\\'", StringComparison.Ordinal) + "'";
    }
}
