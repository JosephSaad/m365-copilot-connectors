// ---------------------------------------------------------------------------
// CrawlStateQueries.cs
// The dashboard's entire read surface. Every SQL object this process can name
// is named in this file, and nowhere else.
//
// THAT IS THE POINT OF THE FILE. A reviewer asking "what can the dashboard do
// to the crawl-state database" reads one file and is finished. There are eight
// SQL objects below: the seven reporting procedures from sql/24, and one SELECT
// against crawl.vwConnectionHealth to populate the connection filter controls.
// All eight are things sql/25 grants crawl_reader. Nothing here writes, and
// there is no code path in this project that could - not a hit counter, not a
// last-viewed timestamp, not a cache warm.
//
// The enforcement is NOT this file. It is sql/25: the IIS application pool
// identity has EXECUTE on those seven procedures, SELECT on the six views, no
// permission on any table, and an explicit DENY on INSERT/UPDATE/DELETE for the
// whole crawl schema. This file is the part a reviewer can read; the GRANT
// statements are the part that holds when the reviewer is wrong.
//
// PAGING IS THE DATABASE'S JOB. Every list procedure applies OFFSET/FETCH and
// clamps its own page size, and returns TotalRows on every row through
// COUNT(*) OVER(). Nothing here fetches a set and pages it in memory: on
// crawl.Item that is a scan of the corpus per page view. The clamps repeated
// below are defence in depth and deliberately match sql/24 - if the two ever
// disagree, sql/24 wins, because it is the side that owns the query plan.
//
// EVERY PARAMETER IS A SqlParameter. Not one string is concatenated into a
// command. The filters on these pages come from a query string typed by a
// person, and @Search in particular is passed straight to a LIKE - as a
// parameter it is a value, and the procedure supplies the anchoring '%' itself.
//
// NOTHING RETURNED HERE CAN BE ITEM CONTENT. crawl.Item holds an ID, a type,
// two hashes and a byte count - see the header of sql/22. There is no column to
// select that would contain a document body, a field value or a title.
// ---------------------------------------------------------------------------

namespace ConnectorState.Dashboard.Data;

using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

/// <summary>Reads crawl state. The only type in this application that opens a SQL connection.</summary>
public sealed class CrawlStateQueries
{
    /// <summary>The largest page sql/24 will serve from uspListRuns, uspListItems and uspListPendingDeletes.</summary>
    public const int MaxListPageSize = 500;

    /// <summary>The largest page sql/24 will serve from uspListThrottleEvents.</summary>
    public const int MaxThrottleEventPageSize = 1000;

    private readonly CrawlStateOptions options;
    private readonly string connectionString;

    /// <summary>Initializes a new instance of the <see cref="CrawlStateQueries"/> class.</summary>
    /// <param name="options">The bound "CrawlState" configuration section.</param>
    public CrawlStateQueries(IOptions<CrawlStateOptions> options)
    {
        this.options = options.Value;

        // Built once. It carries no credential - see CrawlStateOptions - so
        // holding it for the lifetime of the process is not holding a secret.
        this.connectionString = this.options.BuildConnectionString();
    }

    /* =======================================================================
       1. crawl.uspDashboardSummary - the front page, four result sets.
       ======================================================================= */

    /// <summary>Reads everything the landing page shows, in one round trip.</summary>
    /// <param name="windowHours">The window the headline figures cover.</param>
    /// <param name="cancellationToken">Cancels with the request.</param>
    /// <returns>The tiles, per-connection health, a thirty-day trend and the recent runs.</returns>
    public async Task<DashboardSummary> GetDashboardSummaryAsync(
        int windowHours,
        CancellationToken cancellationToken)
    {
        await using SqlConnection connection = await this.OpenAsync(cancellationToken);
        await using SqlCommand command = this.Procedure(connection, "[crawl].[uspDashboardSummary]");

        command.Parameters.Add("@WindowHours", SqlDbType.Int).Value = windowHours;

        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

        // Set 1: the tiles. Exactly one row, always - it is a SELECT of scalar
        // subqueries with no FROM, so it cannot return none.
        List<DashboardTiles> tiles = await ReadSetAsync(reader, MapTiles, cancellationToken);

        // Set 2: per-connection health, already ordered worst first by sql/24.
        List<ConnectionHealthRow> connections =
            await ReadNextSetAsync(reader, MapConnectionHealth, cancellationToken);

        // Set 3: the trend series, one row per connection per day.
        List<DailyActivityRow> trend =
            await ReadNextSetAsync(reader, MapDailyActivity, cancellationToken);

        // Set 4: the ten most recent runs, for the activity strip.
        List<RunHistoryRow> recentRuns =
            await ReadNextSetAsync(reader, MapRunHistory, cancellationToken);

        return new DashboardSummary(
            tiles.Count > 0 ? tiles[0] : new DashboardTiles { WindowHours = windowHours },
            connections,
            trend,
            recentRuns);
    }

    /* =======================================================================
       2. crawl.uspListRuns - the paged run list.
       ======================================================================= */

    /// <summary>Reads one page of the run history. Every filter is optional; null means no filter.</summary>
    /// <param name="connectionId">Restrict to one connection, or null for all.</param>
    /// <param name="status">1 running, 2 succeeded, 3 failed, 4 abandoned, or null for all.</param>
    /// <param name="mode">1 full, 2 incremental, or null for both.</param>
    /// <param name="fromUtc">Inclusive lower bound on StartedUtc, or null.</param>
    /// <param name="toUtc">Exclusive upper bound on StartedUtc, or null.</param>
    /// <param name="includeDryRuns">True to include runs that wrote nothing by design.</param>
    /// <param name="page">The 1-based page number.</param>
    /// <param name="pageSize">Rows per page. Clamped to <see cref="MaxListPageSize"/> here and in sql/24.</param>
    /// <param name="cancellationToken">Cancels with the request.</param>
    /// <returns>The page, with the total the pager needs.</returns>
    public async Task<PagedResult<RunListRow>> ListRunsAsync(
        string? connectionId,
        byte? status,
        byte? mode,
        DateTime? fromUtc,
        DateTime? toUtc,
        bool includeDryRuns,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        page = ClampPage(page);
        pageSize = ClampPageSize(pageSize, this.options.DefaultPageSize, MaxListPageSize);

        await using SqlConnection connection = await this.OpenAsync(cancellationToken);
        await using SqlCommand command = this.Procedure(connection, "[crawl].[uspListRuns]");

        AddNVarChar(command, "@ConnectionId", 64, connectionId);
        AddTinyInt(command, "@Status", status);
        AddTinyInt(command, "@Mode", mode);
        AddDateTime2(command, "@FromUtc", fromUtc);
        AddDateTime2(command, "@ToUtc", toUtc);
        command.Parameters.Add("@IncludeDryRuns", SqlDbType.Bit).Value = includeDryRuns;
        command.Parameters.Add("@Page", SqlDbType.Int).Value = page;
        command.Parameters.Add("@PageSize", SqlDbType.Int).Value = pageSize;

        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

        return await ReadPageAsync(reader, MapRunListRow, page, pageSize, cancellationToken);
    }

    /* =======================================================================
       3. crawl.uspGetRun - one run, four result sets.
       ======================================================================= */

    /// <summary>Reads one run in full: header, per-type breakdown, timing and throttle summary.</summary>
    /// <param name="runId">The run to read.</param>
    /// <param name="cancellationToken">Cancels with the request.</param>
    /// <returns>The run detail. Its Run property is null when no such run exists.</returns>
    public async Task<RunDetail> GetRunAsync(long runId, CancellationToken cancellationToken)
    {
        await using SqlConnection connection = await this.OpenAsync(cancellationToken);
        await using SqlCommand command = this.Procedure(connection, "[crawl].[uspGetRun]");

        command.Parameters.Add("@RunId", SqlDbType.BigInt).Value = runId;

        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

        // Set 1: the run itself, from vwRunHistory. Empty for an unknown id.
        List<RunHistoryRow> run = await ReadSetAsync(reader, MapRunHistory, cancellationToken);

        // Set 2: what it did, per kind of item.
        List<RunItemTypeRow> byType = await ReadNextSetAsync(reader, MapRunItemType, cancellationToken);

        // Set 3: where the time went, already converted to milliseconds by sql/24.
        List<RunPhaseTimingRow> timing = await ReadNextSetAsync(reader, MapRunPhaseTiming, cancellationToken);

        // Set 4: throttling, aggregated. Empty for a run that was never throttled -
        // vwThrottleSummary omits clean runs on purpose, so no row here means
        // "none", not "unknown".
        List<ThrottleSummaryRow> throttling =
            await ReadNextSetAsync(reader, MapThrottleSummary, cancellationToken);

        return new RunDetail(
            run.Count > 0 ? run[0] : null,
            byType,
            timing,
            throttling.Count > 0 ? throttling[0] : null);
    }

    /* =======================================================================
       4. crawl.uspListThrottleEvents - the raw 429s and 5xxs for one run.
       ======================================================================= */

    /// <summary>Reads one page of the raw throttle events for a run.</summary>
    /// <param name="runId">The run whose events to read.</param>
    /// <param name="page">The 1-based page number.</param>
    /// <param name="pageSize">Rows per page. Clamped to <see cref="MaxThrottleEventPageSize"/>.</param>
    /// <param name="cancellationToken">Cancels with the request.</param>
    /// <returns>The page, with the total the pager needs.</returns>
    public async Task<PagedResult<ThrottleEventRow>> ListThrottleEventsAsync(
        long runId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        page = ClampPage(page);
        pageSize = ClampPageSize(pageSize, 100, MaxThrottleEventPageSize);

        await using SqlConnection connection = await this.OpenAsync(cancellationToken);
        await using SqlCommand command = this.Procedure(connection, "[crawl].[uspListThrottleEvents]");

        command.Parameters.Add("@RunId", SqlDbType.BigInt).Value = runId;
        command.Parameters.Add("@Page", SqlDbType.Int).Value = page;
        command.Parameters.Add("@PageSize", SqlDbType.Int).Value = pageSize;

        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

        return await ReadPageAsync(reader, MapThrottleEvent, page, pageSize, cancellationToken);
    }

    /* =======================================================================
       5. crawl.uspGetConnectionDetail - one connection, four result sets.
       ======================================================================= */

    /// <summary>Reads one connection in full: health, item-type mix, trend and checkpoint.</summary>
    /// <param name="connectionId">The connection to read.</param>
    /// <param name="trendDays">How many days of trend to return.</param>
    /// <param name="cancellationToken">Cancels with the request.</param>
    /// <returns>The connection detail. Its Health property is null when no such connection exists.</returns>
    public async Task<ConnectionDetail> GetConnectionDetailAsync(
        string connectionId,
        int trendDays,
        CancellationToken cancellationToken)
    {
        await using SqlConnection connection = await this.OpenAsync(cancellationToken);
        await using SqlCommand command = this.Procedure(connection, "[crawl].[uspGetConnectionDetail]");

        AddNVarChar(command, "@ConnectionId", 64, connectionId);
        command.Parameters.Add("@TrendDays", SqlDbType.Int).Value = trendDays;

        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

        // Set 1: health and configuration.
        List<ConnectionHealthRow> health = await ReadSetAsync(reader, MapConnectionHealth, cancellationToken);

        // Set 2: what the index holds, by kind. From the live inventory, which is
        // "what is indexed" and not "what the last run touched".
        List<ItemTypeMixRow> mix = await ReadNextSetAsync(reader, MapItemTypeMix, cancellationToken);

        // Set 3: the trend.
        List<DailyActivityRow> trend = await ReadNextSetAsync(reader, MapDailyActivity, cancellationToken);

        // Set 4: the checkpoint. Empty when the connector keeps none, which is
        // the normal state for a full-crawl connector like SqlGraphPush.
        List<CheckpointRow> checkpoint = await ReadNextSetAsync(reader, MapCheckpoint, cancellationToken);

        return new ConnectionDetail(
            health.Count > 0 ? health[0] : null,
            mix,
            trend,
            checkpoint.Count > 0 ? checkpoint[0] : null);
    }

    /* =======================================================================
       6. crawl.uspListItems - the paged inventory.
       ======================================================================= */

    /// <summary>Reads one page of a connection's item inventory.</summary>
    /// <param name="connectionId">The connection whose inventory to read. Required by sql/24.</param>
    /// <param name="search">
    /// An item ID PREFIX. sql/24 applies LIKE @Search + '%', anchored: a leading
    /// wildcard cannot use the clustered index and turns every lookup into a scan
    /// of the corpus.
    /// </param>
    /// <param name="itemType">Restrict to one item type, or null for all.</param>
    /// <param name="state">1 live, 2 pending delete, 3 deleted, or null for all.</param>
    /// <param name="minUnchangedStreak">Only items current for at least this many runs, or null.</param>
    /// <param name="page">The 1-based page number.</param>
    /// <param name="pageSize">Rows per page. Clamped to <see cref="MaxListPageSize"/>.</param>
    /// <param name="cancellationToken">Cancels with the request.</param>
    /// <returns>The page, with the total the pager needs.</returns>
    public async Task<PagedResult<ItemRow>> ListItemsAsync(
        string connectionId,
        string? search,
        string? itemType,
        byte? state,
        int? minUnchangedStreak,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        page = ClampPage(page);
        pageSize = ClampPageSize(pageSize, this.options.DefaultPageSize, MaxListPageSize);

        await using SqlConnection connection = await this.OpenAsync(cancellationToken);
        await using SqlCommand command = this.Procedure(connection, "[crawl].[uspListItems]");

        AddNVarChar(command, "@ConnectionId", 64, connectionId);
        AddNVarChar(command, "@Search", 128, search);
        AddNVarChar(command, "@ItemType", 64, itemType);
        AddTinyInt(command, "@State", state);
        AddInt(command, "@MinUnchangedStreak", minUnchangedStreak);
        command.Parameters.Add("@Page", SqlDbType.Int).Value = page;
        command.Parameters.Add("@PageSize", SqlDbType.Int).Value = pageSize;

        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

        return await ReadPageAsync(reader, MapItem, page, pageSize, cancellationToken);
    }

    /* =======================================================================
       7. crawl.uspListPendingDeletes - deletes Graph has not confirmed.
       ======================================================================= */

    /// <summary>Reads one page of the items awaiting a delete Graph has not confirmed.</summary>
    /// <param name="connectionId">Restrict to one connection, or null for all.</param>
    /// <param name="minAgeMinutes">
    /// Only deletes pending at least this long. The filter that separates a stuck
    /// delete from a run that is simply in progress.
    /// </param>
    /// <param name="page">The 1-based page number.</param>
    /// <param name="pageSize">Rows per page. Clamped to <see cref="MaxListPageSize"/>.</param>
    /// <param name="cancellationToken">Cancels with the request.</param>
    /// <returns>The page, with the total the pager needs.</returns>
    public async Task<PagedResult<PendingDeleteRow>> ListPendingDeletesAsync(
        string? connectionId,
        int? minAgeMinutes,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        page = ClampPage(page);
        pageSize = ClampPageSize(pageSize, this.options.DefaultPageSize, MaxListPageSize);

        await using SqlConnection connection = await this.OpenAsync(cancellationToken);
        await using SqlCommand command = this.Procedure(connection, "[crawl].[uspListPendingDeletes]");

        AddNVarChar(command, "@ConnectionId", 64, connectionId);
        AddInt(command, "@MinAgeMinutes", minAgeMinutes);
        command.Parameters.Add("@Page", SqlDbType.Int).Value = page;
        command.Parameters.Add("@PageSize", SqlDbType.Int).Value = pageSize;

        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

        return await ReadPageAsync(reader, MapPendingDelete, page, pageSize, cancellationToken);
    }

    /* =======================================================================
       8. crawl.vwConnectionHealth - the connection filter control.

       The only statement in this project that is not a procedure call. It reads
       two columns from a view sql/25 grants crawl_reader SELECT on, because the
       alternative is calling uspDashboardSummary - four result sets and the
       aggregate over crawl.Item - to populate a dropdown.
       ======================================================================= */

    /// <summary>Reads the connection identifiers and names, for filter controls.</summary>
    /// <param name="cancellationToken">Cancels with the request.</param>
    /// <returns>Every registered connection, by display name.</returns>
    public async Task<IReadOnlyList<ConnectionRef>> ListConnectionsAsync(CancellationToken cancellationToken)
    {
        const string Sql =
            "SELECT ConnectionId, DisplayName FROM [crawl].[vwConnectionHealth] " +
            "ORDER BY DisplayName, ConnectionId;";

        await using SqlConnection connection = await this.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(Sql, connection)
        {
            CommandType = CommandType.Text,
            CommandTimeout = this.options.CommandTimeoutSeconds,
        };

        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

        return await ReadSetAsync(
            reader,
            row => new ConnectionRef(row.Text("ConnectionId"), row.Text("DisplayName")),
            cancellationToken);
    }

    /* =======================================================================
       Row mapping. One method per result set shape, by column NAME - see
       RowSet.cs for why never by ordinal.
       ======================================================================= */

    private static DashboardTiles MapTiles(RowSet row) => new()
    {
        ConnectionsEnabled = row.Int32("ConnectionsEnabled"),
        ConnectionsHealthy = row.Int32("ConnectionsHealthy"),
        ConnectionsNeedingAttention = row.Int32("ConnectionsNeedingAttention"),
        RunsInProgress = row.Int32("RunsInProgress"),
        LiveItems = row.Int32("LiveItems"),
        PendingDeletes = row.Int32("PendingDeletes"),
        Tombstones = row.Int32("Tombstones"),
        ItemsWrittenInWindow = row.Int32("ItemsWrittenInWindow"),
        ItemsUnchangedInWindow = row.Int32("ItemsUnchangedInWindow"),
        ItemsDeletedInWindow = row.Int32("ItemsDeletedInWindow"),
        ThrottleWaitsInWindow = row.Int32("ThrottleWaitsInWindow"),
        FailedRunsInWindow = row.Int32("FailedRunsInWindow"),
        RunsInWindow = row.Int32("RunsInWindow"),
        UnchangedPercentInWindow = row.DecimalOrNull("UnchangedPercentInWindow"),
        WindowHours = row.Int32("WindowHours"),
    };

    private static ConnectionHealthRow MapConnectionHealth(RowSet row) => new()
    {
        ConnectionId = row.Text("ConnectionId"),
        DisplayName = row.Text("DisplayName"),
        ConnectorKey = row.Text("ConnectorKey"),
        IsEnabled = row.Bool("IsEnabled"),
        ExpectedIntervalMinutes = row.Int32OrNull("ExpectedIntervalMinutes"),
        LastRunId = row.Int64OrNull("LastRunId"),
        LastRunStatus = row.TextOrNull("LastRunStatus"),
        LastRunStartedUtc = row.TimeOrNull("LastRunStartedUtc"),
        LastSuccessUtc = row.TimeOrNull("LastSuccessUtc"),
        MinutesSinceLastSuccess = row.Int32OrNull("MinutesSinceLastSuccess"),
        ConsecutiveFailures = row.Int32("ConsecutiveFailures"),
        LastSuccessItemsWritten = row.Int32OrNull("LastSuccessItemsWritten"),
        LastSuccessItemsUnchanged = row.Int32OrNull("LastSuccessItemsUnchanged"),
        LastSuccessItemsDeleted = row.Int32OrNull("LastSuccessItemsDeleted"),
        LiveItemCount = row.Int32("LiveItemCount"),
        PendingDeleteCount = row.Int32("PendingDeleteCount"),
        Health = row.Text("Health"),
        ErrorKind = row.TextOrNull("ErrorKind"),
        ErrorMessage = row.TextOrNull("ErrorMessage"),
    };

    private static DailyActivityRow MapDailyActivity(RowSet row) => new()
    {
        ConnectionId = row.Text("ConnectionId"),
        DisplayName = row.Text("DisplayName"),
        ActivityDate = row.Time("ActivityDate"),
        Runs = row.Int32("Runs"),
        Succeeded = row.Int32("Succeeded"),
        Failed = row.Int32("Failed"),
        ItemsWritten = row.Int32("ItemsWritten"),
        ItemsUnchanged = row.Int32("ItemsUnchanged"),
        ItemsDeleted = row.Int32("ItemsDeleted"),
        ThrottleWaits = row.Int32("ThrottleWaits"),
        BytesWritten = row.Int64("BytesWritten"),
        UnchangedPercent = row.DecimalOrNull("UnchangedPercent"),
        AvgDurationSeconds = row.DecimalOrNull("AvgDurationSeconds"),
    };

    private static RunHistoryRow MapRunHistory(RowSet row) => new()
    {
        RunId = row.Int64("RunId"),
        ConnectionId = row.Text("ConnectionId"),
        DisplayName = row.Text("DisplayName"),
        ConnectorKey = row.Text("ConnectorKey"),
        Mode = row.TextOrNull("Mode"),
        Status = row.TextOrNull("Status"),
        IsDryRun = row.Bool("IsDryRun"),
        StartedUtc = row.Time("StartedUtc"),
        CompletedUtc = row.TimeOrNull("CompletedUtc"),
        DurationSeconds = row.Int32("DurationSeconds"),
        ItemsRead = row.Int32("ItemsRead"),
        ItemsWritten = row.Int32("ItemsWritten"),
        ItemsUnchanged = row.Int32("ItemsUnchanged"),
        ItemsDeleted = row.Int32("ItemsDeleted"),
        ItemsSkipped = row.Int32("ItemsSkipped"),
        ItemsFailed = row.Int32("ItemsFailed"),
        ItemsDuplicate = row.Int32("ItemsDuplicate"),
        UnchangedPercent = row.DecimalOrNull("UnchangedPercent"),
        ItemsPerSecond = row.DecimalOrNull("ItemsPerSecond"),
        ThrottleWaits = row.Int32("ThrottleWaits"),
        BatchesSent = row.Int32("BatchesSent"),
        BytesWritten = row.Int64("BytesWritten"),
        HostName = row.Text("HostName"),
        ToolVersion = row.Text("ToolVersion"),
        ErrorKind = row.TextOrNull("ErrorKind"),
        ErrorMessage = row.TextOrNull("ErrorMessage"),
    };

    private static RunListRow MapRunListRow(RowSet row) => new()
    {
        RunId = row.Int64("RunId"),
        ConnectionId = row.Text("ConnectionId"),
        DisplayName = row.Text("DisplayName"),
        ConnectorKey = row.Text("ConnectorKey"),
        Mode = row.TextOrNull("Mode"),
        Status = row.TextOrNull("Status"),
        IsDryRun = row.Bool("IsDryRun"),
        StartedUtc = row.Time("StartedUtc"),
        CompletedUtc = row.TimeOrNull("CompletedUtc"),
        DurationSeconds = row.Int32("DurationSeconds"),
        ItemsRead = row.Int32("ItemsRead"),
        ItemsWritten = row.Int32("ItemsWritten"),
        ItemsUnchanged = row.Int32("ItemsUnchanged"),
        ItemsDeleted = row.Int32("ItemsDeleted"),
        ItemsSkipped = row.Int32("ItemsSkipped"),
        ItemsFailed = row.Int32("ItemsFailed"),
        ItemsDuplicate = row.Int32("ItemsDuplicate"),
        ThrottleWaits = row.Int32("ThrottleWaits"),
        BatchesSent = row.Int32("BatchesSent"),
        BytesWritten = row.Int64("BytesWritten"),
        HostName = row.Text("HostName"),
        ToolVersion = row.Text("ToolVersion"),
        ErrorKind = row.TextOrNull("ErrorKind"),
    };

    private static RunItemTypeRow MapRunItemType(RowSet row) => new()
    {
        ItemType = row.Text("ItemType"),
        ItemsWritten = row.Int32("ItemsWritten"),
        ItemsUnchanged = row.Int32("ItemsUnchanged"),
        ItemsDeleted = row.Int32("ItemsDeleted"),
        ItemsSkipped = row.Int32("ItemsSkipped"),
        ItemsFailed = row.Int32("ItemsFailed"),
        BytesWritten = row.Int64("BytesWritten"),
        UnchangedPercent = row.DecimalOrNull("UnchangedPercent"),
    };

    private static RunPhaseTimingRow MapRunPhaseTiming(RowSet row) => new()
    {
        Phase = row.Text("Phase"),
        SampleCount = row.Int64("SampleCount"),
        TotalMs = row.Decimal("TotalMs"),
        P50Ms = row.Decimal("P50Ms"),
        P95Ms = row.Decimal("P95Ms"),
        P99Ms = row.Decimal("P99Ms"),
        MaxMs = row.Decimal("MaxMs"),
        SharePercent = row.DecimalOrNull("SharePercent"),
    };

    private static ThrottleSummaryRow MapThrottleSummary(RowSet row) => new()
    {
        RunId = row.Int64("RunId"),
        ConnectionId = row.Text("ConnectionId"),
        DisplayName = row.Text("DisplayName"),
        RunStartedUtc = row.Time("RunStartedUtc"),
        Mode = row.TextOrNull("Mode"),
        ThrottleEvents = row.Int32("ThrottleEvents"),
        Refusals429 = row.Int32("Refusals429"),
        ServerErrors = row.Int32("ServerErrors"),
        TotalRetryAfterSeconds = row.Int32("TotalRetryAfterSeconds"),
        MaxRetryAfterSeconds = row.Int32("MaxRetryAfterSeconds"),
        DistinctMinutes = row.Int32("DistinctMinutes"),
        FirstEventUtc = row.Time("FirstEventUtc"),
        LastEventUtc = row.Time("LastEventUtc"),
        DeepestRetry = row.Int32("DeepestRetry"),
    };

    private static ThrottleEventRow MapThrottleEvent(RowSet row) => new()
    {
        ThrottleEventId = row.Int64("ThrottleEventId"),
        OccurredUtc = row.Time("OccurredUtc"),
        StatusCode = row.Int32("StatusCode"),
        RetryAfterSeconds = row.Int32OrNull("RetryAfterSeconds"),
        Endpoint = row.Text("Endpoint"),
        AttemptNumber = row.Int32("AttemptNumber"),
        SecondsIntoRun = row.Int32("SecondsIntoRun"),
    };

    private static ItemRow MapItem(RowSet row) => new()
    {
        ItemId = row.Text("ItemId"),
        ItemType = row.Text("ItemType"),
        State = row.TextOrNull("State"),
        ContentBytes = row.Int32("ContentBytes"),
        ContentHashHex = row.Text("ContentHashHex"),
        AclHashHex = row.Text("AclHashHex"),
        FirstSeenRunId = row.Int64("FirstSeenRunId"),
        LastSeenRunId = row.Int64("LastSeenRunId"),
        LastWrittenRunId = row.Int64("LastWrittenRunId"),
        LastWrittenUtc = row.Time("LastWrittenUtc"),
        UnchangedStreak = row.Int32("UnchangedStreak"),
        DaysSinceLastWrite = row.Int32("DaysSinceLastWrite"),
    };

    private static PendingDeleteRow MapPendingDelete(RowSet row) => new()
    {
        ConnectionId = row.Text("ConnectionId"),
        DisplayName = row.Text("DisplayName"),
        ItemId = row.Text("ItemId"),
        ItemType = row.Text("ItemType"),
        LastSeenRunId = row.Int64("LastSeenRunId"),
        LastWrittenUtc = row.Time("LastWrittenUtc"),
        AgeMinutes = row.Int32("AgeMinutes"),
        LastSeenRunStartedUtc = row.TimeOrNull("LastSeenRunStartedUtc"),
    };

    private static ItemTypeMixRow MapItemTypeMix(RowSet row) => new()
    {
        ItemType = row.Text("ItemType"),
        Items = row.Int32("Items"),
        Live = row.Int32("Live"),
        PendingDelete = row.Int32("PendingDelete"),
        Tombstoned = row.Int32("Tombstoned"),
        ContentBytes = row.Int64("ContentBytes"),
        AvgUnchangedStreak = row.DecimalOrNull("AvgUnchangedStreak"),
        MaxUnchangedStreak = row.Int32("MaxUnchangedStreak"),
    };

    private static CheckpointRow MapCheckpoint(RowSet row) => new()
    {
        MarkerTime = row.TimeOrNull("MarkerTime"),
        MarkerKey = row.TextOrNull("MarkerKey"),
        RunId = row.Int64("RunId"),
        RunCount = row.Int32("RunCount"),
        UpdatedUtc = row.Time("UpdatedUtc"),
    };

    /* =======================================================================
       Plumbing.
       ======================================================================= */

    private static async Task<List<T>> ReadSetAsync<T>(
        SqlDataReader reader,
        Func<RowSet, T> map,
        CancellationToken cancellationToken)
    {
        var rows = new List<T>();

        // Column metadata is available as soon as the reader is positioned on a
        // result set, before the first Read, so an empty set still builds a valid
        // ordinal map rather than throwing.
        var set = new RowSet(reader);

        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(map(set));
        }

        return rows;
    }

    private static async Task<List<T>> ReadNextSetAsync<T>(
        SqlDataReader reader,
        Func<RowSet, T> map,
        CancellationToken cancellationToken)
    {
        if (!await reader.NextResultAsync(cancellationToken))
        {
            // A procedure that returned fewer sets than sql/24 defines is a
            // deployment mismatch, but an empty list renders as "no rows" and
            // keeps the rest of the page usable. The set that IS missing will
            // show as empty rather than as a 500 on the whole page.
            return new List<T>();
        }

        return await ReadSetAsync(reader, map, cancellationToken);
    }

    private static async Task<PagedResult<T>> ReadPageAsync<T>(
        SqlDataReader reader,
        Func<RowSet, T> map,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var rows = new List<T>();
        var set = new RowSet(reader);
        int totalRows = 0;

        while (await reader.ReadAsync(cancellationToken))
        {
            // Same on every row - COUNT(*) OVER() against the filtered set - so
            // reading it each time costs nothing and needs no special case for
            // the first row.
            totalRows = set.Int32("TotalRows");
            rows.Add(map(set));
        }

        return new PagedResult<T>(rows, totalRows, page, pageSize);
    }

    private static int ClampPage(int page) => page < 1 ? 1 : page;

    private static int ClampPageSize(int pageSize, int fallback, int max)
    {
        if (pageSize < 1)
        {
            return fallback < 1 ? 50 : fallback;
        }

        return pageSize > max ? max : pageSize;
    }

    private static void AddNVarChar(SqlCommand command, string name, int size, string? value)
    {
        SqlParameter parameter = command.Parameters.Add(name, SqlDbType.NVarChar, size);
        parameter.Value = string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;
    }

    private static void AddTinyInt(SqlCommand command, string name, byte? value)
    {
        SqlParameter parameter = command.Parameters.Add(name, SqlDbType.TinyInt);
        parameter.Value = value.HasValue ? value.Value : DBNull.Value;
    }

    private static void AddInt(SqlCommand command, string name, int? value)
    {
        SqlParameter parameter = command.Parameters.Add(name, SqlDbType.Int);
        parameter.Value = value.HasValue ? value.Value : DBNull.Value;
    }

    private static void AddDateTime2(SqlCommand command, string name, DateTime? value)
    {
        SqlParameter parameter = command.Parameters.Add(name, SqlDbType.DateTime2);

        // Scale 3 to match DATETIME2(3) in sql/21. A wider scale on the parameter
        // makes the predicate non-sargable against the column's type.
        parameter.Scale = 3;
        parameter.Value = value.HasValue ? value.Value : DBNull.Value;
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(this.connectionString);

        try
        {
            await connection.OpenAsync(cancellationToken);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        return connection;
    }

    private SqlCommand Procedure(SqlConnection connection, string name)
    {
        return new SqlCommand(name, connection)
        {
            CommandType = CommandType.StoredProcedure,

            // Query timeout, not connect timeout. A report over a large inventory
            // legitimately outlives the fifteen seconds a connection attempt gets.
            CommandTimeout = this.options.CommandTimeoutSeconds,
        };
    }
}
