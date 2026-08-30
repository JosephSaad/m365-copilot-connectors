-- ===========================================================================
-- 22-crawl-state-views.sql
--
-- The read surface: everything the dashboard and any monitoring system see.
--
-- A push tool gets no row on the Microsoft 365 admin centre page, and nothing
-- in this repository can change that. What it can do is keep the information
-- that page would have shown, and serve it somewhere an operator can look.
-- These six views are that information; src/ConnectorState.Dashboard is the
-- somewhere, and sql/25 is where its role is granted SELECT on them.
--
-- They are the ONLY part of this database anything other than the connector
-- touches. sql/25 grants the dashboard role SELECT on these views and nothing
-- else - no table access, not even read - so the web tier physically cannot
-- reach a column these views chose to omit, and "what can the dashboard see"
-- is answered by reading this file rather than by trusting its queries.
--
-- Nothing here exposes a property value or item content, because the store
-- never holds any: an item is an ID, a type, two hashes and a byte count. That
-- is a property of the schema, not a filter applied on the way out, which is
-- why a new view added here cannot leak one by accident.
--
-- Run after sql/21 and before sql/23.
--
-- CREATE OR ALTER throughout: a view is a definition, so re-running this file
-- to pick up a change is the intended way to deploy one. That is not true of
-- the tables in sql/21, which is why those are guarded instead.
-- ===========================================================================

USE [ConnectorState];
GO

/* ---------------------------------------------------------------------------
   1. crawl.vwRunHistory

   Every run, most recent first, with the numbers a person actually compares
   between two runs: how long, how many, and the rate. The rate is the column
   that makes a regression obvious - "3,200 items in 41 minutes" needs arithmetic
   before it means anything, and "1.3 items/sec" does not.

   NULLIF on the duration is load-bearing rather than defensive. A run that
   completes inside the same millisecond it started - a dry run over an empty
   source, which happens in every smoke test - would otherwise divide by zero
   and take the whole view down with it.
--------------------------------------------------------------------------- */

CREATE OR ALTER VIEW [crawl].[vwRunHistory]
AS
SELECT
    r.RunId,
    r.ConnectionId,
    c.DisplayName,
    c.ConnectorKey,
    CASE r.Mode   WHEN 1 THEN N'full'    WHEN 2 THEN N'incremental' END      AS Mode,
    CASE r.Status WHEN 1 THEN N'running'   WHEN 2 THEN N'succeeded'
                  WHEN 3 THEN N'failed'    WHEN 4 THEN N'abandoned'
                  WHEN 5 THEN N'partial'   END                      AS Status,
    r.IsDryRun,
    r.StartedUtc,
    r.CompletedUtc,
    DATEDIFF(SECOND, r.StartedUtc, ISNULL(r.CompletedUtc, SYSUTCDATETIME())) AS DurationSeconds,
    r.ItemsRead,
    r.ItemsWritten,
    r.ItemsUnchanged,
    r.ItemsDeleted,
    r.ItemsSkipped,
    r.ItemsFailed,
    r.ItemsDuplicate,

    -- The number the whole change-detection feature exists to move. A healthy
    -- steady-state run is mostly unchanged; a run at 0% is either the first one
    -- or a sign the hashes are not matching, which is a defect and not a
    -- workload change.
    CAST(100.0 * r.ItemsUnchanged
         / NULLIF(r.ItemsWritten + r.ItemsUnchanged, 0) AS DECIMAL(5, 1))    AS UnchangedPercent,

    CAST(1.0 * (r.ItemsWritten + r.ItemsUnchanged)
         / NULLIF(DATEDIFF(SECOND, r.StartedUtc, r.CompletedUtc), 0)
         AS DECIMAL(10, 2))                                                  AS ItemsPerSecond,

    r.ThrottleWaits,
    r.BatchesSent,
    r.BytesWritten,
    r.HostName,
    r.ToolVersion,
    r.ErrorKind,
    r.ErrorMessage
FROM        [crawl].[Run]        AS r
INNER JOIN  [crawl].[Connection] AS c ON c.ConnectionId = r.ConnectionId;
GO

/* ---------------------------------------------------------------------------
   2. crawl.vwConnectionHealth

   One row per connection: is it late, is it failing, and when did it last
   actually work. This is the view a monitoring system polls, and the one whose
   shape should stay stable.

   Health is deliberately a single computed word rather than five columns a
   dashboard has to combine, because the combining is where every estate invents
   its own slightly different rule. The order of the CASE arms is the priority
   order: a connection that is disabled is not also late, and a run in progress
   is not stale however old the last completed one is.

   MinutesSinceLastSuccess is against the last SUCCEEDED run, not the last run.
   A connection failing every fifteen minutes is perfectly punctual and
   completely broken, and a freshness measure that counts failures as activity
   reports it as healthy.
--------------------------------------------------------------------------- */

CREATE OR ALTER VIEW [crawl].[vwConnectionHealth]
AS
WITH LastRun AS
(
    SELECT  ConnectionId, RunId, Mode, Status, StartedUtc, CompletedUtc, ErrorKind, ErrorMessage,
            ROW_NUMBER() OVER (PARTITION BY ConnectionId ORDER BY StartedUtc DESC, RunId DESC) AS rn
    FROM    [crawl].[Run]
    WHERE   IsDryRun = 0
),
LastSuccess AS
(
    SELECT  ConnectionId, RunId, CompletedUtc, ItemsWritten, ItemsDeleted, ItemsUnchanged,
            ROW_NUMBER() OVER (PARTITION BY ConnectionId ORDER BY CompletedUtc DESC, RunId DESC) AS rn
    FROM    [crawl].[Run]
    WHERE   Status = 2 AND IsDryRun = 0
),
-- Consecutive failures counted back from the most recent run. The window stops
-- at the first success, which is what "consecutive" has to mean for an alert
-- rule to be worth writing.
FailureStreak AS
(
    SELECT  r.ConnectionId, COUNT(*) AS ConsecutiveFailures
    FROM    [crawl].[Run] AS r
    WHERE   r.IsDryRun = 0
      AND   r.Status IN (3, 4)
      AND   r.StartedUtc > ISNULL(
                (SELECT MAX(s.StartedUtc) FROM [crawl].[Run] AS s
                 WHERE s.ConnectionId = r.ConnectionId AND s.Status = 2 AND s.IsDryRun = 0),
                '1900-01-01')
    GROUP BY r.ConnectionId
)
SELECT
    c.ConnectionId,
    c.DisplayName,
    c.ConnectorKey,
    c.IsEnabled,
    c.ExpectedIntervalMinutes,

    lr.RunId                                                        AS LastRunId,
    CASE lr.Status WHEN 1 THEN N'running'   WHEN 2 THEN N'succeeded'
                   WHEN 3 THEN N'failed'    WHEN 4 THEN N'abandoned'
                   WHEN 5 THEN N'partial'   END AS LastRunStatus,
    lr.StartedUtc                                                   AS LastRunStartedUtc,
    ls.CompletedUtc                                                 AS LastSuccessUtc,
    DATEDIFF(MINUTE, ls.CompletedUtc, SYSUTCDATETIME())             AS MinutesSinceLastSuccess,
    ISNULL(fs.ConsecutiveFailures, 0)                               AS ConsecutiveFailures,

    ls.ItemsWritten                                                 AS LastSuccessItemsWritten,
    ls.ItemsUnchanged                                               AS LastSuccessItemsUnchanged,
    ls.ItemsDeleted                                                 AS LastSuccessItemsDeleted,

    ISNULL(counts.LiveItemCount, 0)                                 AS LiveItemCount,
    ISNULL(counts.PendingDeleteCount, 0)                            AS PendingDeleteCount,

    CASE
        WHEN c.IsEnabled = 0                       THEN N'disabled'
        WHEN lr.RunId IS NULL                      THEN N'never run'
        WHEN lr.Status = 1                         THEN N'running'
        WHEN ISNULL(fs.ConsecutiveFailures, 0) > 0 THEN N'failing'

        -- Below 'failing' and above 'late': a connection whose most recent run
        -- lost items is not healthy, and is not failing either. Ranked here
        -- because a run that DIED is the more urgent of the two - this one at
        -- least wrote what it could - while both outrank a schedule that has
        -- merely slipped.
        --
        -- Keyed on the LAST run only. Items refused three runs ago were retried
        -- since, because a refused write records no hash; leaving the word on
        -- would make it permanent and unactionable.
        WHEN lr.Status = 5                         THEN N'items refused'
        WHEN c.ExpectedIntervalMinutes IS NOT NULL
             AND DATEDIFF(MINUTE, ls.CompletedUtc, SYSUTCDATETIME())
                 > c.ExpectedIntervalMinutes * 2   THEN N'late'
        WHEN ISNULL(counts.PendingDeleteCount, 0) > 0 THEN N'deletes pending'
        ELSE N'healthy'
    END                                                             AS Health,

    lr.ErrorKind,
    lr.ErrorMessage
FROM        [crawl].[Connection]  AS c
LEFT JOIN   LastRun               AS lr ON lr.ConnectionId = c.ConnectionId AND lr.rn = 1
LEFT JOIN   LastSuccess           AS ls ON ls.ConnectionId = c.ConnectionId AND ls.rn = 1
LEFT JOIN   FailureStreak         AS fs ON fs.ConnectionId = c.ConnectionId
-- One aggregate over crawl.Item per connection instead of three correlated
-- subqueries, two of which counted State = 2 twice - once for the column and
-- once for the Health CASE arm. crawl.Item is the largest table here, so this
-- is the difference between one pass and three on the health page.
LEFT JOIN
(
    SELECT  ConnectionId,
            SUM(CASE WHEN State = 1 THEN 1 ELSE 0 END) AS LiveItemCount,
            SUM(CASE WHEN State = 2 THEN 1 ELSE 0 END) AS PendingDeleteCount
    FROM    [crawl].[Item]
    GROUP BY ConnectionId
) AS counts ON counts.ConnectionId = c.ConnectionId;
GO

/* ---------------------------------------------------------------------------
   3. crawl.vwPendingDeletes

   Items the sweep decided are gone and Graph has not yet confirmed removed.

   On a healthy connection this view is empty for all but a few seconds per run.
   A row that persists across runs means a DELETE was refused and retried and
   refused again, and it is exactly the failure the agent used to absorb
   silently: an item the source dropped, still answering searches.

   AgeMinutes is here so an alert can be written as "anything older than one
   crawl interval", which is the rule that catches a stuck delete without firing
   on every run in progress.
--------------------------------------------------------------------------- */

CREATE OR ALTER VIEW [crawl].[vwPendingDeletes]
AS
SELECT
    i.ConnectionId,
    c.DisplayName,
    i.ItemId,
    i.ItemType,
    i.LastSeenRunId,
    i.LastWrittenUtc,
    i.PendingSinceUtc,

    -- Time spent PENDING, not time since the item was last written. Those are
    -- wildly different on a corpus that mostly does not change: aged on
    -- LastWrittenUtc, every freshly pending row reads as weeks old and the alert
    -- below fires on every sweep, which is the fastest way to get an alert
    -- switched off.
    DATEDIFF(MINUTE, i.PendingSinceUtc, SYSUTCDATETIME()) AS AgeMinutes,

    -- Which run last saw it alive, so the operator can look at that run's
    -- numbers rather than reasoning from the item alone.
    (SELECT MAX(r.StartedUtc) FROM [crawl].[Run] AS r
     WHERE r.RunId = i.LastSeenRunId)                    AS LastSeenRunStartedUtc
FROM        [crawl].[Item]       AS i
INNER JOIN  [crawl].[Connection] AS c ON c.ConnectionId = i.ConnectionId
WHERE       i.State = 2;
GO

/* ---------------------------------------------------------------------------
   4. crawl.vwItemInventory

   What the index is believed to hold. "Believed" is the honest word: this table
   records what the connector wrote, and drift between it and the real index is
   possible - a tenant-side purge, a manual deletion, a run that died between
   the Graph write and the state upsert.

   deploy/Compare-SourceToIndex.ps1 is still how that drift is FOUND. This view
   is how you know what to compare against, which that script previously had to
   reconstruct by re-reading the source.

   Content hashes are exposed as hex rather than binary because every tool that
   will read this - a notebook, a spreadsheet export, a ticket - mangles
   VARBINARY differently and none of them mangles a string.
--------------------------------------------------------------------------- */

CREATE OR ALTER VIEW [crawl].[vwItemInventory]
AS
SELECT
    i.ConnectionId,
    c.DisplayName,
    i.ItemId,
    i.ItemType,
    CASE i.State WHEN 1 THEN N'live' WHEN 2 THEN N'pending delete' WHEN 3 THEN N'deleted' END AS State,
    i.ContentBytes,
    CONVERT(CHAR(64), i.ContentHash, 2) AS ContentHashHex,
    CONVERT(CHAR(64), i.AclHash,     2) AS AclHashHex,
    i.FirstSeenRunId,
    i.LastSeenRunId,
    i.LastWrittenRunId,
    i.LastWrittenUtc,
    i.UnchangedStreak,
    DATEDIFF(DAY, i.LastWrittenUtc, SYSUTCDATETIME()) AS DaysSinceLastWrite
FROM        [crawl].[Item]       AS i
INNER JOIN  [crawl].[Connection] AS c ON c.ConnectionId = i.ConnectionId;
GO

/* ---------------------------------------------------------------------------
   5. crawl.vwThrottleSummary

   One row per run that was throttled at all, aggregating the raw events.

   The two columns that decide what to do about it are TotalRetryAfterSeconds
   and DistinctMinutes. A run that lost ten minutes across four minutes of wall
   clock is being throttled hard and briefly - lower the writer count. A run
   that lost the same ten minutes spread evenly over an hour is at its
   sustainable rate and the writer count is not the lever.

   Runs with no throttling do not appear. That is intentional: this view is read
   when something is slow, and padding it with zeros for every healthy run makes
   the rows that matter harder to find.
--------------------------------------------------------------------------- */

CREATE OR ALTER VIEW [crawl].[vwThrottleSummary]
AS
SELECT
    t.RunId,
    r.ConnectionId,
    c.DisplayName,
    r.StartedUtc                                            AS RunStartedUtc,
    CASE r.Mode WHEN 1 THEN N'full' WHEN 2 THEN N'incremental' END AS Mode,
    COUNT(*)                                                AS ThrottleEvents,
    SUM(CASE WHEN t.StatusCode = 429 THEN 1 ELSE 0 END)     AS Refusals429,
    SUM(CASE WHEN t.StatusCode BETWEEN 500 AND 599 THEN 1 ELSE 0 END) AS ServerErrors,
    SUM(ISNULL(t.RetryAfterSeconds, 0))                     AS TotalRetryAfterSeconds,
    MAX(ISNULL(t.RetryAfterSeconds, 0))                     AS MaxRetryAfterSeconds,
    COUNT(DISTINCT DATEDIFF(MINUTE, r.StartedUtc, t.OccurredUtc)) AS DistinctMinutes,
    MIN(t.OccurredUtc)                                      AS FirstEventUtc,
    MAX(t.OccurredUtc)                                      AS LastEventUtc,
    MAX(t.AttemptNumber)                                    AS DeepestRetry
FROM        [crawl].[ThrottleEvent] AS t
INNER JOIN  [crawl].[Run]           AS r ON r.RunId = t.RunId
INNER JOIN  [crawl].[Connection]    AS c ON c.ConnectionId = r.ConnectionId
GROUP BY    t.RunId, r.ConnectionId, c.DisplayName, r.StartedUtc, r.Mode;
GO

/* ---------------------------------------------------------------------------
   6. crawl.vwDailyActivity

   One row per connection per day. This is the dashboard's trend series, and it
   exists as a view rather than as a query in the web tier for one reason: the
   date arithmetic and the divide-by-zero guards are the parts that get subtly
   wrong when they are retyped, and a chart that is subtly wrong is worse than
   no chart because nobody checks it twice.

   Failed runs are counted but contribute no items, so a day of failures shows
   as a visible trough beside a red count rather than as a gap the eye skips.
   Dry runs are excluded throughout - they write nothing and would flatter every
   average they touched.
--------------------------------------------------------------------------- */

CREATE OR ALTER VIEW [crawl].[vwDailyActivity]
AS
SELECT
    r.ConnectionId,
    c.DisplayName,
    CAST(r.StartedUtc AS DATE)                                     AS ActivityDate,
    COUNT(*)                                                       AS Runs,
    SUM(CASE WHEN r.Status = 2 THEN 1 ELSE 0 END)                  AS Succeeded,
    SUM(CASE WHEN r.Status IN (3, 4) THEN 1 ELSE 0 END)            AS Failed,
    SUM(r.ItemsWritten)                                            AS ItemsWritten,
    SUM(r.ItemsUnchanged)                                          AS ItemsUnchanged,
    SUM(r.ItemsDeleted)                                            AS ItemsDeleted,
    SUM(r.ThrottleWaits)                                           AS ThrottleWaits,
    SUM(r.BytesWritten)                                            AS BytesWritten,

    CAST(100.0 * SUM(r.ItemsUnchanged)
         / NULLIF(SUM(r.ItemsWritten) + SUM(r.ItemsUnchanged), 0)
         AS DECIMAL(5, 1))                                         AS UnchangedPercent,

    -- Averaged over SUCCEEDED runs only. A failed run's duration is the time it
    -- took to break, and including it makes a bad day look like a slow one.
    CAST(AVG(CASE WHEN r.Status = 2
                  THEN 1.0 * DATEDIFF(SECOND, r.StartedUtc, r.CompletedUtc) END)
         AS DECIMAL(10, 1))                                        AS AvgDurationSeconds
FROM        [crawl].[Run]        AS r
INNER JOIN  [crawl].[Connection] AS c ON c.ConnectionId = r.ConnectionId
WHERE       r.IsDryRun = 0
GROUP BY    r.ConnectionId, c.DisplayName, CAST(r.StartedUtc AS DATE);
GO

-- Verification: the six views exist and each one runs.
SELECT  s.name AS schema_name, v.name AS view_name
FROM    sys.views   AS v
JOIN    sys.schemas AS s ON s.schema_id = v.schema_id
WHERE   s.name = N'crawl'
ORDER BY v.name;

SELECT TOP (0) * FROM [crawl].[vwRunHistory];
SELECT TOP (0) * FROM [crawl].[vwConnectionHealth];
SELECT TOP (0) * FROM [crawl].[vwPendingDeletes];
SELECT TOP (0) * FROM [crawl].[vwItemInventory];
SELECT TOP (0) * FROM [crawl].[vwThrottleSummary];
SELECT TOP (0) * FROM [crawl].[vwDailyActivity];
GO
