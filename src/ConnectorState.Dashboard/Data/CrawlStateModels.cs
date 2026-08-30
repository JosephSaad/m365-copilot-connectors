// ---------------------------------------------------------------------------
// CrawlStateModels.cs
// One record per result set returned by sql/22 and sql/24, and nothing else.
//
// These types exist so a page never touches a SqlDataReader and never indexes a
// column by number. They are shaped by the SQL, not by what a page would find
// convenient: a property here means a column the crawl_reader role is actually
// granted, and adding one means changing sql/22 or sql/24 first. That direction
// of dependency is the point - a model that invents a field is a page that will
// silently show a default, and on a monitoring dashboard a plausible default is
// worse than an error.
//
// NULLABILITY IS COPIED FROM THE SCHEMA, NOT GUESSED. Several of these columns
// are null for reasons that matter and that a non-null default would erase:
// UnchangedPercent is null when a run wrote and matched nothing, which is a
// different fact from 0%; MinutesSinceLastSuccess is null for a connection that
// has never succeeded, which is not "0 minutes ago"; LastSeenRunStartedUtc is
// null when the run that last saw a pending delete has been purged by
// retention. Every nullable below traces to one of those.
//
// NOTHING HERE CAN CARRY ITEM CONTENT, because the store does not hold any. An
// item in crawl.Item is an ID, a type, two hashes and a byte count - see the
// header of sql/22. There is no property bag, no title and no body to model,
// which is why no amount of adding to this file could leak one.
// ---------------------------------------------------------------------------

namespace ConnectorState.Dashboard.Data;

/// <summary>The headline figures on the front page. From uspDashboardSummary, set 1.</summary>
public sealed record DashboardTiles
{
    /// <summary>Gets the count of connections that are not disabled.</summary>
    public int ConnectionsEnabled { get; init; }

    /// <summary>Gets the count of connections whose health is "healthy".</summary>
    public int ConnectionsHealthy { get; init; }

    /// <summary>Gets the count of connections that are failing or late.</summary>
    public int ConnectionsNeedingAttention { get; init; }

    /// <summary>Gets the count of runs currently in status "running".</summary>
    public int RunsInProgress { get; init; }

    /// <summary>Gets the number of items the inventory believes are live in the index.</summary>
    public int LiveItems { get; init; }

    /// <summary>Gets the number of items a sweep has marked for deletion and Graph has not confirmed.</summary>
    public int PendingDeletes { get; init; }

    /// <summary>Gets the number of items whose deletion Graph has confirmed.</summary>
    public int Tombstones { get; init; }

    /// <summary>Gets the items written in the window, excluding dry runs.</summary>
    public int ItemsWrittenInWindow { get; init; }

    /// <summary>Gets the items found unchanged in the window, excluding dry runs.</summary>
    public int ItemsUnchangedInWindow { get; init; }

    /// <summary>Gets the items deleted in the window, excluding dry runs.</summary>
    public int ItemsDeletedInWindow { get; init; }

    /// <summary>Gets the number of throttle waits in the window, excluding dry runs.</summary>
    public int ThrottleWaitsInWindow { get; init; }

    /// <summary>Gets the count of failed or abandoned runs in the window.</summary>
    public int FailedRunsInWindow { get; init; }

    /// <summary>Gets the count of runs in the window, excluding dry runs.</summary>
    public int RunsInWindow { get; init; }

    /// <summary>
    /// Gets the proportion of touched items that were already current. Null when
    /// nothing was written or matched in the window, which is not zero percent.
    /// </summary>
    public decimal? UnchangedPercentInWindow { get; init; }

    /// <summary>Gets the window the figures above cover, echoed back by the procedure.</summary>
    public int WindowHours { get; init; }
}

/// <summary>One connection's health. From crawl.vwConnectionHealth.</summary>
public sealed record ConnectionHealthRow
{
    /// <summary>Gets the connection identifier, as registered by the connector.</summary>
    public string ConnectionId { get; init; } = string.Empty;

    /// <summary>Gets the connection's display name.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Gets the key identifying which connector owns this connection.</summary>
    public string ConnectorKey { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether the connection is enabled.</summary>
    public bool IsEnabled { get; init; }

    /// <summary>Gets the interval the connection is expected to run at, if one is configured.</summary>
    public int? ExpectedIntervalMinutes { get; init; }

    /// <summary>Gets the identifier of the most recent non-dry run, or null if there has never been one.</summary>
    public long? LastRunId { get; init; }

    /// <summary>Gets the status of that run.</summary>
    public string? LastRunStatus { get; init; }

    /// <summary>Gets when that run started.</summary>
    public DateTime? LastRunStartedUtc { get; init; }

    /// <summary>Gets when the last SUCCEEDED run completed. Null if none ever has.</summary>
    public DateTime? LastSuccessUtc { get; init; }

    /// <summary>
    /// Gets the freshness measure. Against the last success, not the last run: a
    /// connection failing every fifteen minutes is punctual and broken.
    /// </summary>
    public int? MinutesSinceLastSuccess { get; init; }

    /// <summary>Gets the number of failures since the last success.</summary>
    public int ConsecutiveFailures { get; init; }

    /// <summary>Gets the items written by the last successful run.</summary>
    public int? LastSuccessItemsWritten { get; init; }

    /// <summary>Gets the items found unchanged by the last successful run.</summary>
    public int? LastSuccessItemsUnchanged { get; init; }

    /// <summary>Gets the items deleted by the last successful run.</summary>
    public int? LastSuccessItemsDeleted { get; init; }

    /// <summary>Gets the number of items the inventory believes are live for this connection.</summary>
    public int LiveItemCount { get; init; }

    /// <summary>Gets the number of items awaiting a delete Graph has not confirmed.</summary>
    public int PendingDeleteCount { get; init; }

    /// <summary>
    /// Gets the single computed word the view decides: disabled, never run,
    /// running, failing, late, deletes pending, or healthy. Computed in SQL so
    /// every consumer agrees on the rule.
    /// </summary>
    public string Health { get; init; } = string.Empty;

    /// <summary>Gets the short stable failure token from the last run.</summary>
    public string? ErrorKind { get; init; }

    /// <summary>Gets the operator-facing failure message from the last run.</summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>One connection's activity on one day. From crawl.vwDailyActivity.</summary>
public sealed record DailyActivityRow
{
    /// <summary>Gets the connection identifier.</summary>
    public string ConnectionId { get; init; } = string.Empty;

    /// <summary>Gets the connection's display name.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Gets the UTC date the runs started on.</summary>
    public DateTime ActivityDate { get; init; }

    /// <summary>Gets the number of runs that day.</summary>
    public int Runs { get; init; }

    /// <summary>Gets how many of them succeeded.</summary>
    public int Succeeded { get; init; }

    /// <summary>Gets how many failed or were abandoned.</summary>
    public int Failed { get; init; }

    /// <summary>Gets the items written that day.</summary>
    public int ItemsWritten { get; init; }

    /// <summary>Gets the items found unchanged that day.</summary>
    public int ItemsUnchanged { get; init; }

    /// <summary>Gets the items deleted that day.</summary>
    public int ItemsDeleted { get; init; }

    /// <summary>Gets the throttle waits that day.</summary>
    public int ThrottleWaits { get; init; }

    /// <summary>Gets the bytes written that day.</summary>
    public long BytesWritten { get; init; }

    /// <summary>Gets the proportion of touched items already current, or null if none were.</summary>
    public decimal? UnchangedPercent { get; init; }

    /// <summary>Gets the mean duration of the day's SUCCEEDED runs, or null if none succeeded.</summary>
    public decimal? AvgDurationSeconds { get; init; }
}

/// <summary>One run, as crawl.vwRunHistory presents it.</summary>
public sealed record RunHistoryRow
{
    /// <summary>Gets the run identifier.</summary>
    public long RunId { get; init; }

    /// <summary>Gets the connection the run belongs to.</summary>
    public string ConnectionId { get; init; } = string.Empty;

    /// <summary>Gets the connection's display name.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Gets the key identifying which connector ran.</summary>
    public string ConnectorKey { get; init; } = string.Empty;

    /// <summary>Gets "full" or "incremental". Only a full run may conclude an item was deleted.</summary>
    public string? Mode { get; init; }

    /// <summary>Gets "running", "succeeded", "failed" or "abandoned".</summary>
    public string? Status { get; init; }

    /// <summary>Gets a value indicating whether the run wrote nothing by design.</summary>
    public bool IsDryRun { get; init; }

    /// <summary>Gets when the run started, in UTC.</summary>
    public DateTime StartedUtc { get; init; }

    /// <summary>Gets when the run finished, or null while it is still running.</summary>
    public DateTime? CompletedUtc { get; init; }

    /// <summary>Gets the elapsed seconds, measured to now while the run is open.</summary>
    public int DurationSeconds { get; init; }

    /// <summary>Gets the rows the source returned.</summary>
    public int ItemsRead { get; init; }

    /// <summary>Gets the items written to Graph.</summary>
    public int ItemsWritten { get; init; }

    /// <summary>Gets the items whose hashes matched, so nothing was sent.</summary>
    public int ItemsUnchanged { get; init; }

    /// <summary>Gets the items deleted.</summary>
    public int ItemsDeleted { get; init; }

    /// <summary>Gets the rows the connector declined to index.</summary>
    public int ItemsSkipped { get; init; }

    /// <summary>Gets the items that failed to write.</summary>
    public int ItemsFailed { get; init; }

    /// <summary>Gets the rows that repeated an item ID already seen this run.</summary>
    public int ItemsDuplicate { get; init; }

    /// <summary>
    /// Gets the proportion of touched items already current. The number the whole
    /// change-detection feature exists to move; null when the run touched nothing.
    /// </summary>
    public decimal? UnchangedPercent { get; init; }

    /// <summary>Gets the throughput, or null for a run that has not completed.</summary>
    public decimal? ItemsPerSecond { get; init; }

    /// <summary>Gets the number of times the run waited on a 429 or a 5xx.</summary>
    public int ThrottleWaits { get; init; }

    /// <summary>Gets the number of batches sent.</summary>
    public int BatchesSent { get; init; }

    /// <summary>Gets the bytes written.</summary>
    public long BytesWritten { get; init; }

    /// <summary>Gets the host the run executed on. A push tool is operator-run and may run from several.</summary>
    public string HostName { get; init; } = string.Empty;

    /// <summary>Gets the version of the tool that ran.</summary>
    public string ToolVersion { get; init; } = string.Empty;

    /// <summary>Gets the short stable failure token, or null for a run that did not fail.</summary>
    public string? ErrorKind { get; init; }

    /// <summary>
    /// Gets the operator-facing failure message. Carries no row content by policy -
    /// see the header of crawl.Run in sql/21.
    /// </summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>One row of the paged run list. From uspListRuns.</summary>
public sealed record RunListRow
{
    /// <summary>Gets the run identifier.</summary>
    public long RunId { get; init; }

    /// <summary>Gets the connection the run belongs to.</summary>
    public string ConnectionId { get; init; } = string.Empty;

    /// <summary>Gets the connection's display name.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Gets the key identifying which connector ran.</summary>
    public string ConnectorKey { get; init; } = string.Empty;

    /// <summary>Gets "full" or "incremental".</summary>
    public string? Mode { get; init; }

    /// <summary>Gets "running", "succeeded", "failed" or "abandoned".</summary>
    public string? Status { get; init; }

    /// <summary>Gets a value indicating whether the run wrote nothing by design.</summary>
    public bool IsDryRun { get; init; }

    /// <summary>Gets when the run started, in UTC.</summary>
    public DateTime StartedUtc { get; init; }

    /// <summary>Gets when the run finished, or null while it is still running.</summary>
    public DateTime? CompletedUtc { get; init; }

    /// <summary>Gets the elapsed seconds, measured to now while the run is open.</summary>
    public int DurationSeconds { get; init; }

    /// <summary>Gets the rows the source returned.</summary>
    public int ItemsRead { get; init; }

    /// <summary>Gets the items written to Graph.</summary>
    public int ItemsWritten { get; init; }

    /// <summary>Gets the items whose hashes matched.</summary>
    public int ItemsUnchanged { get; init; }

    /// <summary>Gets the items deleted.</summary>
    public int ItemsDeleted { get; init; }

    /// <summary>Gets the rows the connector declined to index.</summary>
    public int ItemsSkipped { get; init; }

    /// <summary>Gets the items that failed to write.</summary>
    public int ItemsFailed { get; init; }

    /// <summary>Gets the rows that repeated an item ID already seen this run.</summary>
    public int ItemsDuplicate { get; init; }

    /// <summary>Gets the number of times the run waited on a 429 or a 5xx.</summary>
    public int ThrottleWaits { get; init; }

    /// <summary>Gets the number of batches sent.</summary>
    public int BatchesSent { get; init; }

    /// <summary>Gets the bytes written.</summary>
    public long BytesWritten { get; init; }

    /// <summary>Gets the host the run executed on.</summary>
    public string HostName { get; init; } = string.Empty;

    /// <summary>Gets the version of the tool that ran.</summary>
    public string ToolVersion { get; init; } = string.Empty;

    /// <summary>Gets the short stable failure token, or null for a run that did not fail.</summary>
    public string? ErrorKind { get; init; }
}

/// <summary>What a run did, per kind of item. From uspGetRun, set 2.</summary>
public sealed record RunItemTypeRow
{
    /// <summary>Gets the connector's own name for this kind of item.</summary>
    public string ItemType { get; init; } = string.Empty;

    /// <summary>Gets the items of this type written.</summary>
    public int ItemsWritten { get; init; }

    /// <summary>Gets the items of this type found unchanged.</summary>
    public int ItemsUnchanged { get; init; }

    /// <summary>Gets the items of this type deleted.</summary>
    public int ItemsDeleted { get; init; }

    /// <summary>Gets the rows of this type the connector declined.</summary>
    public int ItemsSkipped { get; init; }

    /// <summary>Gets the items of this type that failed to write.</summary>
    public int ItemsFailed { get; init; }

    /// <summary>Gets the IDs of this type that repeated an earlier row's.</summary>
    /// <remarks>
    /// Per kind because the run-level total cannot say which join is wrong.
    /// A repeated ID is an upsert overwriting an earlier item while the count
    /// claims both, so a non-zero here is a defect in the source query rather
    /// than a fact about the corpus.
    /// </remarks>
    public int ItemsDuplicate { get; init; }

    /// <summary>Gets the bytes written for this type.</summary>
    public long BytesWritten { get; init; }

    /// <summary>Gets the proportion already current, or null if this type was not touched.</summary>
    public decimal? UnchangedPercent { get; init; }
}

/// <summary>Where a run's time went. From uspGetRun, set 3, in milliseconds.</summary>
public sealed record RunPhaseTimingRow
{
    /// <summary>Gets the phase name as PushTiming recorded it.</summary>
    public string Phase { get; init; } = string.Empty;

    /// <summary>Gets how many measurements the percentiles are drawn from.</summary>
    public long SampleCount { get; init; }

    /// <summary>Gets the total time in this phase, in milliseconds.</summary>
    public decimal TotalMs { get; init; }

    /// <summary>Gets the median, in milliseconds.</summary>
    public decimal P50Ms { get; init; }

    /// <summary>Gets the 95th percentile, in milliseconds.</summary>
    public decimal P95Ms { get; init; }

    /// <summary>Gets the 99th percentile, in milliseconds.</summary>
    public decimal P99Ms { get; init; }

    /// <summary>Gets the slowest single measurement, in milliseconds.</summary>
    public decimal MaxMs { get; init; }

    /// <summary>
    /// Gets this phase's share of the RowTotal phase. Null when the run recorded
    /// no RowTotal, which is what an older tool version looks like.
    /// </summary>
    public decimal? SharePercent { get; init; }
}

/// <summary>Throttling for one run, aggregated. From crawl.vwThrottleSummary.</summary>
public sealed record ThrottleSummaryRow
{
    /// <summary>Gets the run identifier.</summary>
    public long RunId { get; init; }

    /// <summary>Gets the connection the run belongs to.</summary>
    public string ConnectionId { get; init; } = string.Empty;

    /// <summary>Gets the connection's display name.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Gets when the run started.</summary>
    public DateTime RunStartedUtc { get; init; }

    /// <summary>Gets "full" or "incremental".</summary>
    public string? Mode { get; init; }

    /// <summary>Gets the total number of throttle events.</summary>
    public int ThrottleEvents { get; init; }

    /// <summary>Gets how many were an explicit 429.</summary>
    public int Refusals429 { get; init; }

    /// <summary>Gets how many were a 5xx.</summary>
    public int ServerErrors { get; init; }

    /// <summary>Gets the seconds the service asked the run to wait, summed.</summary>
    public int TotalRetryAfterSeconds { get; init; }

    /// <summary>Gets the longest single Retry-After honoured.</summary>
    public int MaxRetryAfterSeconds { get; init; }

    /// <summary>
    /// Gets the count of distinct minutes-into-run that saw an event. Read
    /// against TotalRetryAfterSeconds: ten minutes lost across four is a rate
    /// problem, ten across sixty is the sustainable rate.
    /// </summary>
    public int DistinctMinutes { get; init; }

    /// <summary>Gets the first event's timestamp.</summary>
    public DateTime FirstEventUtc { get; init; }

    /// <summary>Gets the last event's timestamp.</summary>
    public DateTime LastEventUtc { get; init; }

    /// <summary>Gets the deepest retry attempt reached.</summary>
    public int DeepestRetry { get; init; }
}

/// <summary>One raw 429 or 5xx. From uspListThrottleEvents.</summary>
public sealed record ThrottleEventRow
{
    /// <summary>Gets the event identifier.</summary>
    public long ThrottleEventId { get; init; }

    /// <summary>Gets when the service refused the write, in UTC.</summary>
    public DateTime OccurredUtc { get; init; }

    /// <summary>Gets the HTTP status the service returned.</summary>
    public int StatusCode { get; init; }

    /// <summary>
    /// Gets the Retry-After the service sent. Null means it sent none and the
    /// engine fell back to its own backoff - see PushCore/GraphThrottling.cs.
    /// </summary>
    public int? RetryAfterSeconds { get; init; }

    /// <summary>Gets the endpoint that refused. A short token, not a URL with an item ID in it.</summary>
    public string Endpoint { get; init; } = string.Empty;

    /// <summary>Gets which attempt this was, from 1.</summary>
    public int AttemptNumber { get; init; }

    /// <summary>Gets the offset from the run's start, which is how a cluster of events becomes visible.</summary>
    public int SecondsIntoRun { get; init; }
}

/// <summary>One item in the inventory. From uspListItems.</summary>
public sealed record ItemRow
{
    /// <summary>Gets the item identifier as pushed to Graph.</summary>
    public string ItemId { get; init; } = string.Empty;

    /// <summary>Gets the connector's own name for this kind of item.</summary>
    public string ItemType { get; init; } = string.Empty;

    /// <summary>Gets "live", "pending delete" or "deleted".</summary>
    public string? State { get; init; }

    /// <summary>Gets the size of the content that was hashed. Not the content.</summary>
    public int ContentBytes { get; init; }

    /// <summary>Gets the content hash as hex. Changes when the source text changes.</summary>
    public string ContentHashHex { get; init; } = string.Empty;

    /// <summary>Gets the ACL hash as hex. Kept separate because permissions change for different reasons.</summary>
    public string AclHashHex { get; init; } = string.Empty;

    /// <summary>Gets the run that first indexed this item.</summary>
    public long FirstSeenRunId { get; init; }

    /// <summary>Gets the run that last found this item in the source. Moves every run.</summary>
    public long LastSeenRunId { get; init; }

    /// <summary>Gets the run that last actually wrote this item. Moves rarely, and that is the point.</summary>
    public long LastWrittenRunId { get; init; }

    /// <summary>Gets when the item was last written, in UTC.</summary>
    public DateTime LastWrittenUtc { get; init; }

    /// <summary>Gets how many consecutive runs found this item already current.</summary>
    public int UnchangedStreak { get; init; }

    /// <summary>Gets the days since the last write.</summary>
    public int DaysSinceLastWrite { get; init; }
}

/// <summary>An item a sweep marked for deletion that Graph has not confirmed. From uspListPendingDeletes.</summary>
public sealed record PendingDeleteRow
{
    /// <summary>Gets the connection the item belongs to.</summary>
    public string ConnectionId { get; init; } = string.Empty;

    /// <summary>Gets the connection's display name.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Gets the item identifier.</summary>
    public string ItemId { get; init; } = string.Empty;

    /// <summary>Gets the connector's own name for this kind of item.</summary>
    public string ItemType { get; init; } = string.Empty;

    /// <summary>Gets the run that last found this item alive in the source.</summary>
    public long LastSeenRunId { get; init; }

    /// <summary>Gets when the item was last written.</summary>
    public DateTime LastWrittenUtc { get; init; }

    /// <summary>
    /// Gets how long the delete has been pending. A row older than one crawl
    /// interval is a DELETE that was refused and retried and refused again -
    /// an item the source dropped, still answering searches.
    /// </summary>
    public int AgeMinutes { get; init; }

    /// <summary>Gets when that run started, or null if retention has purged it.</summary>
    public DateTime? LastSeenRunStartedUtc { get; init; }
}

/// <summary>What the index holds for one connection, by kind. From uspGetConnectionDetail, set 2.</summary>
public sealed record ItemTypeMixRow
{
    /// <summary>Gets the connector's own name for this kind of item.</summary>
    public string ItemType { get; init; } = string.Empty;

    /// <summary>Gets the total rows of this type, in any state.</summary>
    public int Items { get; init; }

    /// <summary>Gets how many are live.</summary>
    public int Live { get; init; }

    /// <summary>Gets how many are awaiting a delete.</summary>
    public int PendingDelete { get; init; }

    /// <summary>Gets how many are confirmed deleted.</summary>
    public int Tombstoned { get; init; }

    /// <summary>Gets the summed content size of this type. Not the content.</summary>
    public long ContentBytes { get; init; }

    /// <summary>Gets the mean unchanged streak across this type.</summary>
    public decimal? AvgUnchangedStreak { get; init; }

    /// <summary>Gets the longest unchanged streak in this type.</summary>
    public int MaxUnchangedStreak { get; init; }
}

/// <summary>Where the next incremental run would start. From uspGetConnectionDetail, set 4.</summary>
public sealed record CheckpointRow
{
    /// <summary>Gets the watermark time, if the connector keeps a time marker.</summary>
    public DateTime? MarkerTime { get; init; }

    /// <summary>Gets the watermark key, if the connector keeps a key marker.</summary>
    public string? MarkerKey { get; init; }

    /// <summary>Gets the run that last advanced the checkpoint.</summary>
    public long RunId { get; init; }

    /// <summary>Gets how many runs have advanced it.</summary>
    public int RunCount { get; init; }

    /// <summary>Gets when it was last advanced.</summary>
    public DateTime UpdatedUtc { get; init; }
}

/// <summary>A connection identifier and its name, for filter controls.</summary>
/// <param name="ConnectionId">The connection identifier.</param>
/// <param name="DisplayName">The connection's display name.</param>
public sealed record ConnectionRef(string ConnectionId, string DisplayName);

/// <summary>The front page, in one round trip. All four sets of uspDashboardSummary.</summary>
/// <param name="Tiles">The headline figures.</param>
/// <param name="Connections">Per-connection health, worst first.</param>
/// <param name="Trend">Thirty days of activity, one row per connection per day.</param>
/// <param name="RecentRuns">The ten most recent runs across every connection.</param>
public sealed record DashboardSummary(
    DashboardTiles Tiles,
    IReadOnlyList<ConnectionHealthRow> Connections,
    IReadOnlyList<DailyActivityRow> Trend,
    IReadOnlyList<RunHistoryRow> RecentRuns);

/// <summary>One run in full. All four sets of uspGetRun.</summary>
/// <param name="Run">The run header, or null when no such run exists.</param>
/// <param name="ByItemType">What it did, per kind of item.</param>
/// <param name="Timing">Where the time went.</param>
/// <param name="Throttling">Throttling aggregated, or null if the run was never throttled.</param>
public sealed record RunDetail(
    RunHistoryRow? Run,
    IReadOnlyList<RunItemTypeRow> ByItemType,
    IReadOnlyList<RunPhaseTimingRow> Timing,
    ThrottleSummaryRow? Throttling);

/// <summary>One connection in full. All four sets of uspGetConnectionDetail.</summary>
/// <param name="Health">The health row, or null when no such connection exists.</param>
/// <param name="ItemTypes">What the index holds, by kind.</param>
/// <param name="Trend">Daily activity over the requested window.</param>
/// <param name="Checkpoint">The checkpoint, or null if the connector keeps none.</param>
public sealed record ConnectionDetail(
    ConnectionHealthRow? Health,
    IReadOnlyList<ItemTypeMixRow> ItemTypes,
    IReadOnlyList<DailyActivityRow> Trend,
    CheckpointRow? Checkpoint);
