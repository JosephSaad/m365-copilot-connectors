---
title: Crawl state schema reference
description: Every object in the ConnectorState database — six table types, eight tables, six views and twenty-five procedures — with columns, parameters, result sets and error numbers.
---

# Crawl state schema reference

Reference for the `crawl` schema in `ConnectorState`. This is the document to
have open when writing a report against the store or working out why a query
returns what it returns. For standing the database up, the roles, the delete
guard and retention, see
[`CRAWL-STATE-DEPLOYMENT.md`](CRAWL-STATE-DEPLOYMENT.md).

**Conventions that hold everywhere in this schema:**

- Every object is in `[crawl]`. Nothing is created in `dbo`, so a grant on `dbo`
  grants nothing here.
- Every time is UTC and `DATETIME2(3)` — millisecond precision, and the
  precision is load-bearing in the checkpoint.
- Hashes are `BINARY(32)`, not hex strings. The views expose them as
  `CHAR(64)` hex because every tool that reads a report mangles `VARBINARY`
  differently.
- Durations are stored in microseconds and reported in milliseconds.
- Names: `usp` prefix for procedures, `vw` for views, no prefix for tables.

| | Count | File |
|---|---|---|
| Table types | 6 | `sql/20-crawl-state-database.sql` |
| Tables | 8 | `sql/21-crawl-state-tables.sql` |
| Views | 6 | `sql/22-crawl-state-views.sql` |
| Write procedures | 18 | `sql/23-crawl-state-procedures.sql` |
| Reporting procedures | 7 | `sql/24-crawl-state-reporting.sql` |
| Roles | 2 | `sql/25-crawl-state-least-privilege.sql` |

---

## Enumerations

Stored as `TINYINT` and translated to words by the views. The numbers are the
contract; the words are for people.

| Column | Value | Meaning |
|---|---|---|
| `Run.Mode` | 1 | `full` — enumerates everything. Only a full run may conclude a deletion |
| | 2 | `incremental` — reads a slice. Absence from it means nothing |
| `Run.Status` | 1 | `running` — opened, not yet closed |
| | 2 | `succeeded` |
| | 3 | `failed` |
| | 4 | `abandoned` — closed by a later run's reaper, not by the process that opened it |
| `Item.State` | 1 | `live` |
| | 2 | `pending delete` — the sweep identified it, Graph has not confirmed |
| | 3 | `deleted` — tombstoned. Removed by `uspPurgeHistory` after `@KeepTombstoneDays` |

Free-text vocabularies, not constrained by the schema:

| Column | Values used by the engine |
|---|---|
| `RunPhaseTiming.Phase` | `SourceRead`, `Prepare`, `WriteInFlight`, `WriteBackoff`, `Commit`, `RowTotal`, `ContentBytes` |
| `ThrottleEvent.Endpoint` | `item` (a single `PUT`), `batch` (a `$batch` sub-request), `schema` (the registration poll) |
| `PrincipalMap.SourceType` | `AdGroup`, `PosixGroup`, `RangerGroup`, `Upn` |
| `PrincipalMap.EntraType` | `group`, `user` — the only two the check constraint allows, plus `NULL` |
| `Connection.ConnectorKey` | The connector's own key, e.g. `sqltickets`, `consultingwork`, `cdphdfsdocs` |

`vwConnectionHealth.Health` is computed, and the arms are evaluated in this
priority order: `disabled`, `never run`, `running`, `failing`, `late`,
`deletes pending`, `healthy`.

---

## Error numbers

| Number | Raised by | Meaning |
|---|---|---|
| 50001 | `uspBeginRun` | `@Mode` was not 1 or 2 |
| 50002 | `uspBeginRun` | Unknown `ConnectionId`. `uspRegisterConnection` was not called first |
| 50003 | `uspCompleteRun`, `uspFailRun` | The run is not open — already closed, reaped as abandoned, or never existed |
| 50004 | `uspGetPendingDeletes` | Unknown `RunId` |
| 50005 | `uspGetPendingDeletes` | The `RunId` belongs to a different connection |
| 50006 | `uspGetPendingDeletes` | Delete detection was asked for after an incremental run |
| 50007 | `uspGetPendingDeletes` | The sweep would exceed `@MaxDeletePercent`. The message carries the missing count, the live count, the percentage and the threshold |

---

## Table types

Table-valued parameters. Passing one requires `EXECUTE` on the type as well as
on the procedure; without it the call fails with a permission error that reads as
though the procedure were missing.

`crawl_writer` holds `EXECUTE` on four of the six. `PrincipalKeyList` and
`ThrottleEventList` are defined in `sql/20` but no procedure in `sql/23` declares
a parameter of either type yet, and `sql/25` grants neither — they are ahead of
the procedures that will take them.

### `crawl.ItemStateList`

What the engine knows about an item after preparing it and before writing it.

| Column | Type |
|---|---|
| `ItemId` | `NVARCHAR(128)`, clustered primary key |
| `ItemType` | `NVARCHAR(64)` |
| `ContentHash` | `BINARY(32)` |
| `AclHash` | `BINARY(32)` |
| `ContentBytes` | `INT` |

The primary key means a batch carrying the same item ID twice is rejected when
the parameter is filled, before the procedure is entered.

### `crawl.ItemIdList`

A list of item IDs.

| Column | Type |
|---|---|
| `ItemId` | `NVARCHAR(128)`, clustered primary key |

Also used by `uspResolvePrincipals`, where `ItemId` carries a *source principal
key* rather than an item ID. Note the width: `PrincipalMap.SourceKey` is
`NVARCHAR(256)`, so a principal key longer than 128 characters can be cached by
`uspCachePrincipal` but cannot be passed to the batched lookup. `PrincipalKeyList`
below exists to close that, and `uspResolvePrincipals` does not take it yet.

### `crawl.PrincipalKeyList`

Source principal keys, at their real width. Deliberately not `ItemIdList` even
though both are one string column: an item ID is capped at 128 characters by
Graph and a source principal is not — an Active Directory distinguished name
routinely runs past it, and a truncating lookup either matches nothing for ever
or matches a different principal's row and stamps an item with the wrong group.

| Column | Type |
|---|---|
| `SourceKey` | `NVARCHAR(256)`, clustered primary key |

**No procedure declares a parameter of this type yet.**

### `crawl.ThrottleEventList`

Throttle events, batched — buffered in the connector for the whole run and
flushed in one round trip rather than one per refusal, which matters most on the
run that produced hundreds of them.

| Column | Type |
|---|---|
| `OccurredUtc` | `DATETIME2(3)` |
| `StatusCode` | `INT` |
| `RetryAfterSeconds` | `INT`, null |
| `Endpoint` | `NVARCHAR(32)` |
| `AttemptNumber` | `INT` |

No primary key: two identical refusals in the same millisecond are legitimate
data, not a duplicate. **No procedure declares a parameter of this type yet** —
`uspRecordThrottle` still takes one event at a time.

### `crawl.ItemTypeCountList`

The per-item-type breakdown of one run, for `uspRecordRunItemTypes`.

| Column | Type |
|---|---|
| `ItemType` | `NVARCHAR(64)`, clustered primary key |
| `ItemsWritten` | `INT` |
| `ItemsUnchanged` | `INT` |
| `ItemsDeleted` | `INT` |
| `ItemsSkipped` | `INT` |
| `ItemsFailed` | `INT` |
| `BytesWritten` | `BIGINT` |

### `crawl.PhaseTimingList`

One run's timing attribution, for `uspSaveRunTiming`.

| Column | Type |
|---|---|
| `Phase` | `NVARCHAR(32)`, clustered primary key |
| `Unit` | `NVARCHAR(16)` — `microseconds` for the six timing phases, `bytes` for `ContentBytes`. No default on the type, so a caller must supply it |
| `SampleCount` | `BIGINT` |
| `TotalMicroseconds` | `BIGINT` |
| `P50Microseconds` | `BIGINT` |
| `P95Microseconds` | `BIGINT` |
| `P99Microseconds` | `BIGINT` |
| `MaxMicroseconds` | `BIGINT` |

`uspSaveRunTiming` does not carry `Unit` through to the table — see that
procedure below.

---

## Tables

### `crawl.Connection`

One row per Graph external connection this store serves. Exists so the store can
answer questions about a connection that has never run, and so lateness has an
expectation to compare against.

| Column | Type | Notes |
|---|---|---|
| `ConnectionId` | `NVARCHAR(64)` | Clustered primary key. The Graph external connection ID |
| `ConnectorKey` | `NVARCHAR(64)` | Which connector owns it |
| `DisplayName` | `NVARCHAR(256)` | Carried into every view for readability |
| `ExpectedIntervalMinutes` | `INT`, null | `CHECK` null or greater than zero. Null means no expectation, and the health view reports staleness rather than lateness |
| `IsEnabled` | `BIT` | Default 1 |
| `CreatedUtc` | `DATETIME2(3)` | Default `SYSUTCDATETIME()` |
| `UpdatedUtc` | `DATETIME2(3)` | Default `SYSUTCDATETIME()`; set by `uspRegisterConnection` on every run |

### `crawl.Run`

One row per crawl run. Opened by `uspBeginRun` before the first read, closed by
exactly one of `uspCompleteRun` or `uspFailRun`.

| Column | Type | Notes |
|---|---|---|
| `RunId` | `BIGINT IDENTITY(1,1)` | Clustered primary key |
| `ConnectionId` | `NVARCHAR(64)` | `FK_Run_Connection` |
| `Mode` | `TINYINT` | 1 full, 2 incremental. `CK_Run_Mode` |
| `Status` | `TINYINT` | 1 to 4. `CK_Run_Status` |
| `StartedUtc` | `DATETIME2(3)` | Default `SYSUTCDATETIME()` |
| `CompletedUtc` | `DATETIME2(3)`, null | `CK_Run_Completed`: null if and only if status is 1 |
| `HostName` | `NVARCHAR(128)` | Which host ran it. Two overlapping runs are told apart by this and `ProcessId` |
| `ProcessId` | `INT` | |
| `ToolVersion` | `NVARCHAR(64)` | |
| `IsDryRun` | `BIT` | Default 0. Excluded from `vwConnectionHealth` and `vwDailyActivity` |
| `ItemsRead` | `INT` | Default 0. All ten counters are written once at close, not incremented per item |
| `ItemsWritten` | `INT` | Default 0 |
| `ItemsUnchanged` | `INT` | Default 0 |
| `ItemsDeleted` | `INT` | Default 0 |
| `ItemsSkipped` | `INT` | Default 0 |
| `ItemsFailed` | `INT` | Default 0. Settable by `uspFailRun` only — `uspCompleteRun` has no parameter for it, so a succeeded run always reports zero. The per-type breakdown in `crawl.RunItemType` does carry it |
| `ItemsDuplicate` | `INT` | Default 0. No per-type equivalent |
| `ThrottleWaits` | `INT` | Default 0 |
| `BytesWritten` | `BIGINT` | Default 0 |
| `BatchesSent` | `INT` | Default 0 |
| `ErrorKind` | `NVARCHAR(64)`, null | A short stable token to index alerts on |
| `ErrorMessage` | `NVARCHAR(2000)`, null | For a person. Neither column may carry a property value or row content |

### `crawl.Item`

The inventory: one row per item the connector believes the index holds. The
delete sweep, change detection, duplicate detection and the quota argument are
all this table.

| Column | Type | Notes |
|---|---|---|
| `ConnectionId` | `NVARCHAR(64)` | Clustered primary key with `ItemId`. **No foreign key to `crawl.Connection`** |
| `ItemId` | `NVARCHAR(128)` | The Graph external item ID |
| `ItemType` | `NVARCHAR(64)` | Updated by `uspRecordWritten`; not touched by `uspRecordUnchanged` |
| `ContentHash` | `BINARY(32)` | Compared by the engine, not by the database |
| `AclHash` | `BINARY(32)` | Separate from the content hash on purpose: content and permissions change for different reasons and at different rates |
| `ContentBytes` | `INT` | `CK_Item_Bytes`: not negative |
| `FirstSeenRunId` | `BIGINT` | Set on insert, never updated |
| `LastSeenRunId` | `BIGINT` | **What the delete sweep diffs on.** Set by both `uspRecordWritten` and `uspRecordUnchanged` |
| `LastWrittenRunId` | `BIGINT` | Set only when the item was actually written, and by `uspConfirmDeletes` |
| `LastWrittenUtc` | `DATETIME2(3)` | Set by `uspRecordWritten` and `uspConfirmDeletes`. **Not** set when an item moves to pending delete |
| `State` | `TINYINT` | Default 1. `CK_Item_State`: 1, 2 or 3 |
| `UnchangedStreak` | `INT` | Default 0. Incremented by `uspRecordUnchanged`, reset by `uspRecordWritten`. Used by nothing here; it is the number that makes the case for incremental reads |

`LastSeenRunId` and `LastWrittenRunId` are not interchangeable. An unchanged item
is seen every run and written rarely; confusing the two either deletes the corpus
or never deletes anything.

### `crawl.Checkpoint`

One marker per connection. Composite, because two source rows can share a
modification timestamp to the millisecond.

| Column | Type | Notes |
|---|---|---|
| `ConnectionId` | `NVARCHAR(64)` | Clustered primary key, `FK_Checkpoint_Connection` |
| `MarkerTime` | `DATETIME2(3)`, null | Null means no marker — `uspBeginRun` treats that as a full crawl being due |
| `MarkerKey` | `NVARCHAR(256)`, null | The tiebreak within a millisecond. Compared as a string |
| `RunId` | `BIGINT` | Which run last advanced it. No foreign key |
| `RunCount` | `INT` | Default 0. Incremented on every advance |
| `UpdatedUtc` | `DATETIME2(3)` | Default `SYSUTCDATETIME()` |

### `crawl.PrincipalMap`

The identity cache: source principal to Entra object, with a TTL.

| Column | Type | Notes |
|---|---|---|
| `ConnectionId` | `NVARCHAR(64)` | Clustered primary key with the next two, `FK_PrincipalMap_Connection` |
| `SourceType` | `NVARCHAR(32)` | Free text — the set grows with every source family, and a lookup table would make adding one a migration |
| `SourceKey` | `NVARCHAR(256)` | |
| `EntraObjectId` | `UNIQUEIDENTIFIER`, null | **Null is meaningful**: a negative cache entry, recording that this principal resolved to nothing |
| `EntraType` | `NVARCHAR(16)`, null | `CK_PrincipalMap_Type`: null, `group` or `user` |
| `ResolvedUtc` | `DATETIME2(3)` | Default `SYSUTCDATETIME()` |
| `ExpiresUtc` | `DATETIME2(3)` | Not null. Computed by `uspCachePrincipal` from `@TtlMinutes` |
| `HitCount` | `INT` | Default 0. Incremented by `uspResolvePrincipals` on every cache hit |

### `crawl.ThrottleEvent`

One row per refusal. Raw, not aggregated on write — aggregation is
`vwThrottleSummary`'s job, and keeping the events means a question nobody
anticipated is still answerable.

| Column | Type | Notes |
|---|---|---|
| `ThrottleEventId` | `BIGINT IDENTITY(1,1)` | Clustered primary key |
| `RunId` | `BIGINT` | `FK_ThrottleEvent_Run` |
| `OccurredUtc` | `DATETIME2(3)` | Default `SYSUTCDATETIME()` |
| `StatusCode` | `INT` | 429, or a 5xx |
| `RetryAfterSeconds` | `INT`, null | Null when the response carried no `Retry-After` |
| `Endpoint` | `NVARCHAR(32)` | Which surface was throttled — this decides whether turning writers down would help at all |
| `AttemptNumber` | `INT` | How deep into the retry chain this was |

### `crawl.RunPhaseTiming`

`PushTiming`'s report, persisted per run, so "is this getting worse" is a
comparison rather than a recollection.

| Column | Type | Notes |
|---|---|---|
| `RunId` | `BIGINT` | Clustered primary key with `Phase`, `FK_RunPhaseTiming_Run` |
| `Phase` | `NVARCHAR(32)` | Matching `PushTiming`'s own property names rather than its display labels, because they land in a primary key and must not be re-worded |
| `Unit` | `NVARCHAR(16)` | Default `N'microseconds'`. Intended to be `bytes` for the `ContentBytes` phase, so a report can render a unit it was not written to know about. `uspSaveRunTiming` does not insert it, so in practice every row takes the default |
| `SampleCount` | `BIGINT` | |
| `TotalMicroseconds` | `BIGINT` | `RowTotal` is the denominator `uspGetRun` uses for the share percentage |
| `P50Microseconds` | `BIGINT` | Percentiles rather than means: one row that waited sixty seconds behind a `Retry-After` moves a mean and says nothing about the other thousand |
| `P95Microseconds` | `BIGINT` | |
| `P99Microseconds` | `BIGINT` | |
| `MaxMicroseconds` | `BIGINT` | |

### `crawl.RunItemType`

What a run did, per kind of item. A separate table rather than more columns on
`crawl.Run` because the set of item types belongs to the connector, not to this
schema.

| Column | Type | Notes |
|---|---|---|
| `RunId` | `BIGINT` | Clustered primary key with `ItemType`, `FK_RunItemType_Run` |
| `ItemType` | `NVARCHAR(64)` | |
| `ItemsWritten` | `INT` | Default 0 |
| `ItemsUnchanged` | `INT` | Default 0 |
| `ItemsDeleted` | `INT` | Default 0 |
| `ItemsSkipped` | `INT` | Default 0 |
| `ItemsFailed` | `INT` | Default 0 |
| `BytesWritten` | `BIGINT` | Default 0 |

`uspPurgeHistory` does not delete from this table. See
[`CRAWL-STATE-DEPLOYMENT.md` section 6](CRAWL-STATE-DEPLOYMENT.md#6-retention).

### Indexes

Beyond the clustered primary keys:

| Index | On | Key | Filter / include |
|---|---|---|---|
| `IX_Run_Connection_Started` | `Run` | `ConnectionId`, `StartedUtc DESC` | Includes `Mode`, `Status`, `CompletedUtc`, `ItemsWritten`, `ItemsDeleted` |
| `IX_Run_Open` | `Run` | `ConnectionId`, `StartedUtc` | `WHERE Status = 1` — the abandoned-run reaper |
| `IX_Item_Sweep` | `Item` | `ConnectionId`, `LastSeenRunId` | `WHERE State = 1`, includes `ItemType`. The delete sweep |
| `IX_Item_NotLive` | `Item` | `ConnectionId`, `State`, `LastWrittenUtc` | `WHERE State <> 1`. The pending backlog and the tombstones |
| `IX_PrincipalMap_Expiry` | `PrincipalMap` | `ExpiresUtc` | Includes `ConnectionId` |
| `IX_ThrottleEvent_Run` | `ThrottleEvent` | `RunId`, `OccurredUtc` | |

---

## Views

The only objects in this database anything other than the connector reads.
`crawl_reader` holds `SELECT` on these six and on nothing else.

### `crawl.vwRunHistory`

Every run, with the numbers a person compares between two of them. Includes dry
runs.

| Column | Notes |
|---|---|
| `RunId`, `ConnectionId`, `DisplayName`, `ConnectorKey` | |
| `Mode` | `full` or `incremental` |
| `Status` | `running`, `succeeded`, `failed`, `abandoned` |
| `IsDryRun`, `StartedUtc`, `CompletedUtc` | |
| `DurationSeconds` | Against `SYSUTCDATETIME()` while the run is still open |
| `ItemsRead`, `ItemsWritten`, `ItemsUnchanged`, `ItemsDeleted`, `ItemsSkipped`, `ItemsFailed`, `ItemsDuplicate` | |
| `UnchangedPercent` | `DECIMAL(5,1)`. Written over written plus unchanged. **A steady-state run above 90% is healthy; one stuck at 0% after the first run means the item IDs are not stable** |
| `ItemsPerSecond` | `DECIMAL(10,2)`. Null while the run is open — the divisor uses `CompletedUtc` |
| `ThrottleWaits`, `BatchesSent`, `BytesWritten` | |
| `HostName`, `ToolVersion`, `ErrorKind`, `ErrorMessage` | |

### `crawl.vwConnectionHealth`

One row per connection, whether or not it has ever run. Dry runs are excluded
throughout. This is the view a monitoring system polls.

| Column | Notes |
|---|---|
| `ConnectionId`, `DisplayName`, `ConnectorKey`, `IsEnabled`, `ExpectedIntervalMinutes` | Straight from `crawl.Connection` |
| `LastRunId`, `LastRunStatus`, `LastRunStartedUtc` | The most recent non-dry run |
| `LastSuccessUtc` | The most recent **succeeded** run |
| `MinutesSinceLastSuccess` | Against the last success, not the last run. A connection failing every fifteen minutes is punctual and broken |
| `ConsecutiveFailures` | Runs in status 3 or 4 since the last success |
| `LastSuccessItemsWritten`, `LastSuccessItemsUnchanged`, `LastSuccessItemsDeleted` | |
| `LiveItemCount`, `PendingDeleteCount` | Counted from `crawl.Item` |
| `Health` | One computed word. `late` fires at twice `ExpectedIntervalMinutes` past the last success, and only when an interval is configured |
| `ErrorKind`, `ErrorMessage` | From the last run |

### `crawl.vwPendingDeletes`

Items the sweep marked and Graph has not confirmed removed. Empty for all but a
few seconds per run on a healthy connection.

| Column | Notes |
|---|---|
| `ConnectionId`, `DisplayName`, `ItemId`, `ItemType` | |
| `LastSeenRunId` | The run that last saw the item alive. **This is the column to age a backlog by** |
| `LastWrittenUtc` | When the item was last *written* — not when it became pending |
| `AgeMinutes` | Derived from `LastWrittenUtc`, so on a corpus of long-unchanged items it is large the moment an item is marked. Not a measure of time spent pending |
| `LastSeenRunStartedUtc` | So the operator can look at that run's numbers |

### `crawl.vwItemInventory`

What the index is believed to hold. "Believed" is the honest word: drift between
this and the real index is possible, and `deploy/Compare-SourceToIndex.ps1` is
still how it is found.

| Column | Notes |
|---|---|
| `ConnectionId`, `DisplayName`, `ItemId`, `ItemType` | |
| `State` | `live`, `pending delete`, `deleted` |
| `ContentBytes` | |
| `ContentHashHex`, `AclHashHex` | `CHAR(64)` |
| `FirstSeenRunId`, `LastSeenRunId`, `LastWrittenRunId`, `LastWrittenUtc` | |
| `UnchangedStreak` | |
| `DaysSinceLastWrite` | |

### `crawl.vwThrottleSummary`

One row per run that was throttled at all. Runs with no throttling do not appear.

| Column | Notes |
|---|---|
| `RunId`, `ConnectionId`, `DisplayName`, `RunStartedUtc`, `Mode` | |
| `ThrottleEvents` | Every event, whatever the status code |
| `Refusals429` | |
| `ServerErrors` | Status 500 to 599 |
| `TotalRetryAfterSeconds` | Missing headers count as zero |
| `MaxRetryAfterSeconds` | |
| `DistinctMinutes` | Distinct minutes-into-the-run in which an event landed. **Read with `TotalRetryAfterSeconds`**: ten minutes lost across four is throttling to lower the writer count for; ten minutes across sixty is the sustainable rate |
| `FirstEventUtc`, `LastEventUtc` | |
| `DeepestRetry` | The highest `AttemptNumber` reached |

### `crawl.vwDailyActivity`

One row per connection per day. The dashboard's trend series. Dry runs excluded.

| Column | Notes |
|---|---|
| `ConnectionId`, `DisplayName`, `ActivityDate` | `ActivityDate` is `StartedUtc` cast to `DATE` |
| `Runs`, `Succeeded`, `Failed` | `Failed` counts status 3 and 4 together |
| `ItemsWritten`, `ItemsUnchanged`, `ItemsDeleted`, `ThrottleWaits`, `BytesWritten` | |
| `UnchangedPercent` | `DECIMAL(5,1)` |
| `AvgDurationSeconds` | Over **succeeded** runs only. A failed run's duration is the time it took to break |

---

## Write procedures

`sql/23`. Sixteen of the eighteen are granted to `crawl_writer`; the two marked
**operator only** are granted to neither role and are reachable by `db_owner`.

### `crawl.uspRegisterConnection`

Upserts the connection row. Called at the start of every run; idempotent.

| Parameter | Type | Default |
|---|---|---|
| `@ConnectionId` | `NVARCHAR(64)` | — |
| `@ConnectorKey` | `NVARCHAR(64)` | — |
| `@DisplayName` | `NVARCHAR(256)` | — |
| `@ExpectedIntervalMinutes` | `INT` | `NULL` |

**Returns** nothing. `@ExpectedIntervalMinutes` is applied on every call, not
only on insert, so changing the schedule in the connector changes what "late"
means on the dashboard.

### `crawl.uspBeginRun`

Opens a run, reaps abandoned ones for the connection, and answers whether a full
crawl is due.

| Parameter | Type | Default |
|---|---|---|
| `@ConnectionId` | `NVARCHAR(64)` | — |
| `@Mode` | `TINYINT` | — |
| `@HostName` | `NVARCHAR(128)` | — |
| `@ProcessId` | `INT` | — |
| `@ToolVersion` | `NVARCHAR(64)` | — |
| `@IsDryRun` | `BIT` | `0` |
| `@FullEveryHours` | `INT` | `168` |
| `@AbandonAfterHours` | `INT` | `12` |

**Returns** one row: `RunId`, `FullCrawlDue`, `LastFullSuccessUtc`,
`HasCheckpoint`, `AbandonedRunsReaped`.

`FullCrawlDue` is 1 when there has never been a successful full run, when there
is no checkpoint, or when the last successful full run is older than
`@FullEveryHours`. The middle case is the one that is easy to forget: an
incremental read with no marker reads from the beginning of time, which is a full
crawl that has told the sweep it was not one.

Throws 50001 on a bad mode, 50002 on an unregistered connection.

### `crawl.uspCompleteRun`

Closes a run as succeeded and writes its counters.

| Parameter | Type | Default |
|---|---|---|
| `@RunId` | `BIGINT` | — |
| `@ItemsRead` | `INT` | — |
| `@ItemsWritten` | `INT` | — |
| `@ItemsUnchanged` | `INT` | — |
| `@ItemsDeleted` | `INT` | — |
| `@ItemsSkipped` | `INT` | — |
| `@ItemsDuplicate` | `INT` | — |
| `@ThrottleWaits` | `INT` | — |
| `@BatchesSent` | `INT` | — |
| `@BytesWritten` | `BIGINT` | — |

**Returns** nothing. Throws 50003 if the run is not open. There is no
`@ItemsFailed` parameter.

### `crawl.uspFailRun`

Closes a run as failed. Records the counters as well: a run that died after nine
hundred of a thousand items wrote nine hundred items, and a failure row full of
zeroes invites the reader to conclude nothing happened.

| Parameter | Type | Default |
|---|---|---|
| `@RunId` | `BIGINT` | — |
| `@ErrorKind` | `NVARCHAR(64)` | — |
| `@ErrorMessage` | `NVARCHAR(2000)` | — |
| `@ItemsRead` | `INT` | `0` |
| `@ItemsWritten` | `INT` | `0` |
| `@ItemsUnchanged` | `INT` | `0` |
| `@ItemsDeleted` | `INT` | `0` |
| `@ItemsSkipped` | `INT` | `0` |
| `@ItemsDuplicate` | `INT` | `0` |
| `@ItemsFailed` | `INT` | `0` |
| `@ThrottleWaits` | `INT` | `0` |
| `@BatchesSent` | `INT` | `0` |
| `@BytesWritten` | `BIGINT` | `0` |

**Returns** nothing. Throws 50003 if the run is not open.

### `crawl.uspGetItemState`

What the store holds for a batch of item IDs.

| Parameter | Type | Default |
|---|---|---|
| `@ConnectionId` | `NVARCHAR(64)` | — |
| `@Items` | `crawl.ItemIdList READONLY` | — |

**Returns** one row per **known** item: `ItemId`, `ItemType`, `ContentHash`,
`AclHash`, `ContentBytes`, `State`, `LastWrittenRunId`, `UnchangedStreak`.

Items with no row come back absent rather than as nulls. Absent means new, and
new means write. `State` is returned because a row that is not live must be
written regardless of whether its hashes match — see `uspRecordUnchanged`.

### `crawl.uspRecordWritten`

Upserts the inventory after Graph confirmed the writes. Never before: a hash
recorded before the write means the next run sees the item as unchanged and skips
it, so one failure between the two makes an item permanently stale and
permanently invisible.

| Parameter | Type | Default |
|---|---|---|
| `@ConnectionId` | `NVARCHAR(64)` | — |
| `@RunId` | `BIGINT` | — |
| `@Items` | `crawl.ItemStateList READONLY` | — |

**Returns** nothing. Sets `LastSeenRunId` and `LastWrittenRunId` together, resets
`UnchangedStreak` to zero, and sets `State` to 1 whatever it was — which is how a
resurrected item, and an item wrongly swept into pending delete, comes back
cleanly.

### `crawl.uspRecordUnchanged`

Marks items seen without writing them.

| Parameter | Type | Default |
|---|---|---|
| `@ConnectionId` | `NVARCHAR(64)` | — |
| `@RunId` | `BIGINT` | — |
| `@Items` | `crawl.ItemIdList READONLY` | — |

**Returns** nothing. Sets `LastSeenRunId` and increments `UnchangedStreak`.

**It updates live rows only** — the `WHERE` carries `State = 1`. An item sitting
in pending delete is not revived by being found unchanged, and does not have its
`LastSeenRunId` advanced, so it stays in the pending backlog and is deleted
again. An item the store returns in a non-live state has to be *written*, not
marked unchanged, and nothing in the database enforces that.

### `crawl.uspGetPendingDeletes`

Moves items the run did not see into pending delete and returns the whole pending
backlog. The most dangerous procedure in the schema; the operational guide is
[`CRAWL-STATE-DEPLOYMENT.md` section 8](CRAWL-STATE-DEPLOYMENT.md#8-the-delete-guard).

| Parameter | Type | Default |
|---|---|---|
| `@ConnectionId` | `NVARCHAR(64)` | — |
| `@RunId` | `BIGINT` | — |
| `@MaxDeletePercent` | `DECIMAL(5,2)` | `10.00` |
| `@OverrideGuard` | `BIT` | `0` |

**Returns** `ItemId` and `ItemType` for every row in state 2 for the connection —
including anything a previous run left behind — ordered by `ItemId`.

Refusal order: 50004 unknown run, 50005 wrong connection, 50006 not a full run,
then an empty result set for a dry run, then the percentage guard (50007). The
guard compares **strictly greater**, so exactly `@MaxDeletePercent` proceeds, and
a connection with no live items computes zero rather than dividing by it. The
`UPDATE` is reached only after every check passes, so a refusal leaves the
inventory untouched.

### `crawl.uspConfirmDeletes`

Tombstones the items Graph confirmed removed — including the 404s, because an
item Graph says is not there is an item that is not there.

| Parameter | Type | Default |
|---|---|---|
| `@ConnectionId` | `NVARCHAR(64)` | — |
| `@RunId` | `BIGINT` | — |
| `@Items` | `crawl.ItemIdList READONLY` | — |

**Returns** one row: `Confirmed`, the number of rows moved. Only rows in state 2
are affected. Sets `LastWrittenRunId` and `LastWrittenUtc`.

### `crawl.uspGetCheckpoint`

| Parameter | Type | Default |
|---|---|---|
| `@ConnectionId` | `NVARCHAR(64)` | — |

**Returns** `MarkerTime`, `MarkerKey`, `RunId`, `RunCount`, `UpdatedUtc`, or no
rows at all if the connection has never checkpointed.

### `crawl.uspSaveCheckpoint`

Advances the marker. **Forward only.**

| Parameter | Type | Default |
|---|---|---|
| `@ConnectionId` | `NVARCHAR(64)` | — |
| `@RunId` | `BIGINT` | — |
| `@MarkerTime` | `DATETIME2(3)` | — |
| `@MarkerKey` | `NVARCHAR(256)` | — |

**Returns** the stored `MarkerTime`, `MarkerKey` and `RunCount` after the
attempt — which is how a caller learns its update was refused.

The update applies only when the stored marker is null, when `@MarkerTime` is
later, or when the times are equal and `@MarkerKey` sorts higher. Refusing to
move backwards is what makes two overlapping runs — an operator running the tool
by hand while the scheduled one is still going — lose nothing instead of
resetting the slower one's progress.

### `crawl.uspResetCheckpoint` — operator only

Nulls the marker, forcing the next run to read from the beginning.

| Parameter | Type | Default |
|---|---|---|
| `@ConnectionId` | `NVARCHAR(64)` | — |
| `@Reason` | `NVARCHAR(256)` | — |

**Returns** one row: `Reset` (rows affected — zero if the connection has no
checkpoint row) and `Reason` echoed back. It is a separate procedure precisely
because `uspSaveCheckpoint` refuses to rewind: the only way back is to say so in
a call whose name appears in an audit log. `@Reason` is echoed, not stored.

### `crawl.uspResolvePrincipals`

Cache lookup for a batch of source principals.

| Parameter | Type | Default |
|---|---|---|
| `@ConnectionId` | `NVARCHAR(64)` | — |
| `@SourceType` | `NVARCHAR(32)` | — |
| `@Principals` | `crawl.ItemIdList READONLY` | `ItemId` carries the source key, capped at 128 characters. `crawl.PrincipalKeyList` exists for this and is not used here yet |

**Returns** one row per **unexpired** hit: `SourceKey`, `EntraObjectId`,
`EntraType`, `ResolvedUtc`, `ExpiresUtc`. Anything absent is a miss the caller
resolves against the directory and writes back. A row returned with a null
`EntraObjectId` is a negative cache hit — the principal resolved to nothing — and
is not the same as an absent row. `HitCount` is incremented for every row
returned.

### `crawl.uspCachePrincipal`

Upserts one cache entry.

| Parameter | Type | Default |
|---|---|---|
| `@ConnectionId` | `NVARCHAR(64)` | — |
| `@SourceType` | `NVARCHAR(32)` | — |
| `@SourceKey` | `NVARCHAR(256)` | — |
| `@EntraObjectId` | `UNIQUEIDENTIFIER` | `NULL` — stores a negative entry |
| `@EntraType` | `NVARCHAR(16)` | `NULL` |
| `@TtlMinutes` | `INT` | `720` |

**Returns** nothing. There is one TTL parameter, not two: the shorter TTL for a
negative entry is a convention the caller applies by passing a smaller number,
and the table records only the resulting `ExpiresUtc`.

### `crawl.uspRecordThrottle`

| Parameter | Type | Default |
|---|---|---|
| `@RunId` | `BIGINT` | — |
| `@StatusCode` | `INT` | — |
| `@RetryAfterSeconds` | `INT` | `NULL` |
| `@Endpoint` | `NVARCHAR(32)` | `N'item'` |
| `@AttemptNumber` | `INT` | `1` |

**Returns** nothing. One row per refusal, one call per row; nothing is aggregated
on write. `crawl.ThrottleEventList` exists for a batched flush and no procedure
takes it yet.

### `crawl.uspSaveRunTiming`

| Parameter | Type | Default |
|---|---|---|
| `@RunId` | `BIGINT` | — |
| `@Phases` | `crawl.PhaseTimingList READONLY` | — |

**Returns** nothing. Deletes the run's existing timing rows and re-inserts, in
one transaction, so calling it twice replaces rather than duplicates. A partial
timing table is more misleading than none, which is why it is one call at the end
of the run rather than one per phase.

The insert names `Phase`, `SampleCount` and the five microsecond columns. It does
**not** name `Unit`, so `crawl.RunPhaseTiming.Unit` takes its default of
`microseconds` on every row — including the `ContentBytes` row, which is the one
case the column was added for. Read the phase name, not the unit, until the
procedure carries it through.

### `crawl.uspRecordRunItemTypes`

| Parameter | Type | Default |
|---|---|---|
| `@RunId` | `BIGINT` | — |
| `@Counts` | `crawl.ItemTypeCountList READONLY` | — |

**Returns** nothing. Delete-then-insert in one transaction, like
`uspSaveRunTiming`. Called **before** `uspCompleteRun`, so a dashboard reading a
completed run always finds the breakdown present rather than landing in the
window where the run reads as finished and its detail page is empty.

### `crawl.uspPurgeHistory` — operator only

Retention. Run from a scheduled job, one connection per call.

| Parameter | Type | Default |
|---|---|---|
| `@ConnectionId` | `NVARCHAR(64)` | — |
| `@KeepRunDays` | `INT` | `90` |
| `@KeepTombstoneDays` | `INT` | `180` |

**Returns** one row: `RunsPurged`, `TombstonesPurged`, `PrincipalsPurged`.

Purges closed runs older than `@KeepRunDays` that no **live** inventory row
references through `FirstSeenRunId`, `LastSeenRunId` or `LastWrittenRunId` and
that the checkpoint does not point at, together with their `ThrottleEvent` and
`RunPhaseTiming` rows; then tombstoned items last written before
`@KeepTombstoneDays`; then `PrincipalMap` rows more than **thirty days** past
expiry, which is hard-coded rather than a parameter. All in one transaction with
`XACT_ABORT ON`.

It does not delete `crawl.RunItemType` rows, which carry a foreign key to
`crawl.Run` with no cascade. See
[`CRAWL-STATE-DEPLOYMENT.md` section 6](CRAWL-STATE-DEPLOYMENT.md#6-retention).

---

## Reporting procedures

`sql/24`. All seven are granted to `crawl_reader`. None of them writes anything —
not a counter, not a last-viewed timestamp — so "can the dashboard corrupt crawl
state" is answered by the absence of an `UPDATE` in that file.

Every list procedure returns `TotalRows` on each row via `COUNT(*) OVER()`, so a
pager renders "page 3 of 214" from one round trip, and page size is clamped in
the procedure rather than in the web tier. `@Page` below 1 becomes 1. All four
carry `OPTION (RECOMPILE)`, deliberately: with this many optional predicates a
single cached plan is chosen for whichever combination ran first and is wrong for
the rest.

### `crawl.uspDashboardSummary`

The landing page, in one call.

| Parameter | Type | Default |
|---|---|---|
| `@WindowHours` | `INT` | `24` |

**Returns four result sets:**

1. Tiles: `ConnectionsEnabled`, `ConnectionsHealthy`, `ConnectionsNeedingAttention`, `RunsInProgress`, `LiveItems`, `PendingDeletes`, `Tombstones`, `ItemsWrittenInWindow`, `ItemsUnchangedInWindow`, `ItemsDeletedInWindow`, `ThrottleWaitsInWindow`, `FailedRunsInWindow`, `RunsInWindow`, `UnchangedPercentInWindow`, `WindowHours`.
2. `crawl.vwConnectionHealth`, every row, ordered worst first.
3. `crawl.vwDailyActivity` for the last 30 days — fixed, not derived from `@WindowHours`.
4. The ten most recent rows of `crawl.vwRunHistory`.

`ConnectionsNeedingAttention` counts `failing` and `late` only. The item counts
are across every connection.

### `crawl.uspListRuns`

Paged run history. Every filter is optional; null means no filter.

| Parameter | Type | Default |
|---|---|---|
| `@ConnectionId` | `NVARCHAR(64)` | `NULL` |
| `@Status` | `TINYINT` | `NULL` |
| `@Mode` | `TINYINT` | `NULL` |
| `@FromUtc` | `DATETIME2(3)` | `NULL` — inclusive |
| `@ToUtc` | `DATETIME2(3)` | `NULL` — exclusive |
| `@IncludeDryRuns` | `BIT` | `0` |
| `@Page` | `INT` | `1` |
| `@PageSize` | `INT` | `50`, clamped to 500 |

**Returns** `TotalRows` plus the run columns: `RunId`, `ConnectionId`,
`DisplayName`, `ConnectorKey`, `Mode`, `Status`, `IsDryRun`, `StartedUtc`,
`CompletedUtc`, `DurationSeconds`, the seven item counters, `ThrottleWaits`,
`BatchesSent`, `BytesWritten`, `HostName`, `ToolVersion`, `ErrorKind`. Ordered
newest first. `ErrorMessage` is deliberately not in this list — it is on
`uspGetRun`.

### `crawl.uspGetRun`

One run in full.

| Parameter | Type | Default |
|---|---|---|
| `@RunId` | `BIGINT` | — |

**Returns four result sets:**

1. The `crawl.vwRunHistory` row.
2. Per item type: `ItemType`, `ItemsWritten`, `ItemsUnchanged`, `ItemsDeleted`, `ItemsSkipped`, `ItemsFailed`, `BytesWritten`, `UnchangedPercent`. Busiest type first.
3. Timing: `Phase`, `SampleCount`, `TotalMs`, `P50Ms`, `P95Ms`, `P99Ms`, `MaxMs`, `SharePercent` — microseconds converted here so every consumer reads the same unit. `SharePercent` is against the `RowTotal` phase.
4. The `crawl.vwThrottleSummary` row, if the run was throttled at all.

### `crawl.uspListItems`

The inventory, paged and searchable.

| Parameter | Type | Default |
|---|---|---|
| `@ConnectionId` | `NVARCHAR(64)` | — required |
| `@Search` | `NVARCHAR(128)` | `NULL` — **prefix match**, `LIKE @Search + '%'` |
| `@ItemType` | `NVARCHAR(64)` | `NULL` |
| `@State` | `TINYINT` | `NULL` |
| `@MinUnchangedStreak` | `INT` | `NULL` |
| `@Page` | `INT` | `1` |
| `@PageSize` | `INT` | `50`, clamped to 500 |

**Returns** `TotalRows`, `ItemId`, `ItemType`, `State` (as a word),
`ContentBytes`, `ContentHashHex`, `AclHashHex`, `FirstSeenRunId`,
`LastSeenRunId`, `LastWrittenRunId`, `LastWrittenUtc`, `UnchangedStreak`,
`DaysSinceLastWrite`. Ordered by `ItemId`.

The search is anchored on purpose. A leading wildcard cannot use the clustered
index and turns every lookup into a scan of the corpus; anchored, it is a range
seek, and item IDs carry a stable type prefix.

### `crawl.uspListPendingDeletes`

| Parameter | Type | Default |
|---|---|---|
| `@ConnectionId` | `NVARCHAR(64)` | `NULL` — all connections |
| `@MinAgeMinutes` | `INT` | `NULL` |
| `@Page` | `INT` | `1` |
| `@PageSize` | `INT` | `50`, clamped to 500 |

**Returns** `TotalRows` plus every column of `crawl.vwPendingDeletes`, oldest
first. `@MinAgeMinutes` filters on that view's `AgeMinutes`, which measures time
since the last write rather than time spent pending — see the view above before
building an alert on it.

### `crawl.uspListThrottleEvents`

The raw events for one run, kept out of `uspGetRun` because a badly throttled run
has thousands of them.

| Parameter | Type | Default |
|---|---|---|
| `@RunId` | `BIGINT` | — |
| `@Page` | `INT` | `1` |
| `@PageSize` | `INT` | `100`, clamped to 1000 |

**Returns** `TotalRows`, `ThrottleEventId`, `OccurredUtc`, `StatusCode`,
`RetryAfterSeconds`, `Endpoint`, `AttemptNumber`, `SecondsIntoRun`. Chronological.

### `crawl.uspGetConnectionDetail`

| Parameter | Type | Default |
|---|---|---|
| `@ConnectionId` | `NVARCHAR(64)` | — |
| `@TrendDays` | `INT` | `30` |

**Returns four result sets:**

1. The `crawl.vwConnectionHealth` row.
2. What the index holds by kind: `ItemType`, `Items`, `Live`, `PendingDelete`, `Tombstoned`, `ContentBytes`, `AvgUnchangedStreak`, `MaxUnchangedStreak`. From the live inventory, not from the last run — it answers "what is in the index", not "what did the last run touch".
3. `crawl.vwDailyActivity` for `@TrendDays`.
4. The checkpoint: `MarkerTime`, `MarkerKey`, `RunId`, `RunCount`, `UpdatedUtc`.

---

## Permissions

`sql/25`. Neither role holds a permission on any table.

| Object | `crawl_writer` | `crawl_reader` |
|---|---|---|
| The eight tables | — (denied `INSERT`, `UPDATE`, `DELETE`, `ALTER`, `REFERENCES`, `SELECT` on the schema) | — (denied `INSERT`, `UPDATE`, `DELETE`, `ALTER`, `REFERENCES` on the schema) |
| The six views | — | `SELECT`, granted by name |
| `ItemIdList`, `ItemStateList`, `ItemTypeCountList`, `PhaseTimingList` | `EXECUTE` | — |
| `PrincipalKeyList`, `ThrottleEventList` | — (granted to nobody; nothing takes them yet) | — |
| Sixteen procedures in `sql/23` | `EXECUTE`, granted by name | — |
| `uspResetCheckpoint`, `uspPurgeHistory` | — | — |
| The seven procedures in `sql/24` | — | `EXECUTE`, granted by name |

The writes reach the tables through ownership chaining inside the procedures,
which is unbroken because the schema and its tables share an owner. `CONTROL`
appears in no `DENY` list, deliberately: it implies `EXECUTE`, so denying it
would break the connector while the `GRANT` rows still suggested access was
configured.

The verification queries at the end of `sql/25` are the evidence. The second one
returns any direct table permission either role holds, and the expected result is
no rows.
