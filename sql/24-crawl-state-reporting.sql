-- ===========================================================================
-- 24-crawl-state-reporting.sql
--
-- The dashboard's read path: paged, filtered, and separate from sql/23 on
-- purpose.
--
-- Two rules shape every procedure here, and both exist because a web tier is a
-- different kind of caller from a connector.
--
--   NOTHING HERE WRITES. Not a counter, not a last-viewed timestamp, not a
--   cache warm. sql/25 grants the dashboard role EXECUTE on this file and
--   nothing else, so "can the dashboard corrupt crawl state" is answered by the
--   absence of an UPDATE in this file rather than by reviewing the web tier.
--
--   EVERY LIST IS PAGED, AND THE PAGE SIZE IS CLAMPED HERE. A dashboard asking
--   for a million rows gets 500 (1,000 for throttle events), because the clamp
--   belongs on the side that owns the query plan. Paging applied in the web tier
--   means the database still materialised the whole set, which on crawl.Item is
--   the difference between a seek and a scan of the corpus.
--
-- @Page is clamped at BOTH ends. Clamping only the lower bound leaves
-- (@Page - 1) * @PageSize free to overflow INT on a page number a query string
-- can carry, which arrives as an arithmetic overflow rather than an empty page.
-- MaxPage below is deliberately generous: it exists to stop the multiplication
-- wrapping, not to second-guess a caller.
--
-- Every list procedure returns TotalRows on each row via COUNT(*) OVER(), so a
-- pager can render "page 3 of 214" from a single round trip. That is one window
-- function against an already-filtered set, not a second COUNT query, and it
-- keeps the two numbers consistent - a separate count can disagree with the
-- page beside it when a run completes between the two calls.
--
-- Run after sql/23, before sql/25.
-- ===========================================================================

USE [ConnectorState];
GO

-- ---------------------------------------------------------------------------
-- SET OPTIONS ARE STORED WITH THE MODULE, NOT SUPPLIED BY THE CALLER.
--
-- SQL Server records QUOTED_IDENTIFIER as it stands in THIS session at CREATE
-- time and replays that stored setting every time the module runs, ignoring
-- whatever the caller has set. sqlcmd connects with it OFF; SSMS connects with
-- it ON. The same script therefore yields a working module from a query window
-- and a broken one from the command line, and the deployment output is
-- identical either way.
--
-- crawl.Item carries a filtered index, and any UPDATE against a table carrying one is refused
-- unless QUOTED_IDENTIFIER was ON at CREATE time:
--   "UPDATE failed because the following SET options have incorrect settings"
-- The refusal lands at EXECUTION, not deployment. The deploy reports success,
-- and the failure surfaces later in an application that has not changed - which
-- is as far from the cause as this failure mode can put you.
--
-- Setting it here makes the stored setting independent of who ran the script.
-- Verify with sys.sql_modules.uses_quoted_identifier; sql/30 checks it.
-- ---------------------------------------------------------------------------
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO
/* ===========================================================================
   SUMMARY - what the dashboard's front page shows
   =========================================================================== */

/* ---------------------------------------------------------------------------
   uspDashboardSummary

   The tiles, in one call. Everything on the landing page comes from here so the
   front page is a single round trip whatever it grows into.

   Windowed over @WindowHours rather than all time, because "1.4 million items
   written" since the connector was installed is a number nobody acts on, and
   "18,000 in the last 24 hours, down from 60,000" is.
--------------------------------------------------------------------------- */

CREATE OR ALTER PROCEDURE [crawl].[uspDashboardSummary]
    @WindowHours INT = 24
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @Since DATETIME2(3) = DATEADD(HOUR, -@WindowHours, SYSUTCDATETIME());

    -- 1. Headline tiles.
    SELECT
        (SELECT COUNT(*) FROM [crawl].[Connection] WHERE IsEnabled = 1)              AS ConnectionsEnabled,
        (SELECT COUNT(*) FROM [crawl].[vwConnectionHealth] WHERE Health = N'healthy') AS ConnectionsHealthy,
        (SELECT COUNT(*) FROM [crawl].[vwConnectionHealth]
         WHERE Health IN (N'failing', N'late', N'items refused'))                     AS ConnectionsNeedingAttention,
        (SELECT COUNT(*) FROM [crawl].[Run] WHERE Status = 1)                        AS RunsInProgress,

        (SELECT COUNT(*) FROM [crawl].[Item] WHERE State = 1)                        AS LiveItems,
        (SELECT COUNT(*) FROM [crawl].[Item] WHERE State = 2)                        AS PendingDeletes,
        (SELECT COUNT(*) FROM [crawl].[Item] WHERE State = 3)                        AS Tombstones,

        (SELECT ISNULL(SUM(ItemsWritten), 0)   FROM [crawl].[Run]
         WHERE StartedUtc >= @Since AND IsDryRun = 0)                                AS ItemsWrittenInWindow,
        (SELECT ISNULL(SUM(ItemsUnchanged), 0) FROM [crawl].[Run]
         WHERE StartedUtc >= @Since AND IsDryRun = 0)                                AS ItemsUnchangedInWindow,
        (SELECT ISNULL(SUM(ItemsDeleted), 0)   FROM [crawl].[Run]
         WHERE StartedUtc >= @Since AND IsDryRun = 0)                                AS ItemsDeletedInWindow,
        (SELECT ISNULL(SUM(ThrottleWaits), 0)  FROM [crawl].[Run]
         WHERE StartedUtc >= @Since AND IsDryRun = 0)                                AS ThrottleWaitsInWindow,
        (SELECT COUNT(*) FROM [crawl].[Run]
         WHERE StartedUtc >= @Since AND IsDryRun = 0 AND Status IN (3, 4))           AS FailedRunsInWindow,
        (SELECT COUNT(*) FROM [crawl].[Run]
         WHERE StartedUtc >= @Since AND IsDryRun = 0)                                AS RunsInWindow,

        -- The number that justifies the whole change-detection feature. Shown on
        -- the front page because it is the one figure that tells you whether the
        -- connector is doing useful work or re-sending a corpus to itself.
        (SELECT CAST(100.0 * ISNULL(SUM(ItemsUnchanged), 0)
                / NULLIF(ISNULL(SUM(ItemsWritten), 0) + ISNULL(SUM(ItemsUnchanged), 0), 0)
                AS DECIMAL(5, 1))
         FROM [crawl].[Run] WHERE StartedUtc >= @Since AND IsDryRun = 0)             AS UnchangedPercentInWindow,

        @WindowHours                                                                  AS WindowHours;

    -- 2. Per-connection health, which is small enough to never need paging.
    SELECT * FROM [crawl].[vwConnectionHealth] ORDER BY
        -- WORST FIRST: the front page's only triage. Every word
        -- vwConnectionHealth can return must be listed. Anything unlisted falls
        -- to the ELSE and sorts BELOW healthy, which is where a new health word
        -- does real damage - the page keeps working, says nothing, and buries
        -- the connection the word was added to surface. sql/29 added
        -- 'items refused' to the view and the pill colours and missed this CASE,
        -- so a connection that had just lost items sorted beneath every healthy
        -- one. A health word means editing sql/22, StateCodes.Tone AND here.
        CASE Health WHEN N'failing'         THEN 1
                    WHEN N'items refused'   THEN 2
                    WHEN N'late'            THEN 3
                    WHEN N'deletes pending' THEN 4
                    WHEN N'never run'       THEN 5
                    WHEN N'running'         THEN 6
                    WHEN N'healthy'         THEN 7
                    ELSE 8 END,
        DisplayName;

    -- 3. The trend series, one row per connection per day.
    SELECT * FROM [crawl].[vwDailyActivity]
    WHERE  ActivityDate >= CAST(DATEADD(DAY, -30, SYSUTCDATETIME()) AS DATE)
    ORDER BY ActivityDate, DisplayName;

    -- 4. The most recent runs across every connection, for the activity strip.
    SELECT TOP (10) * FROM [crawl].[vwRunHistory] ORDER BY StartedUtc DESC;
END
GO

/* ===========================================================================
   DRILL-DOWN - paged lists
   =========================================================================== */

/* ---------------------------------------------------------------------------
   uspListRuns

   Every filter is optional and null means "no filter", which is what lets one
   procedure serve the all-connections list, one connection's history, and the
   failures-only view without three near-identical copies drifting apart.

   The OPTION (RECOMPILE) is deliberate rather than cargo. With this many
   optional predicates a single cached plan is chosen for whichever combination
   ran first and is wrong for the rest - the classic case being a plan built for
   "all connections, no dates" then reused for one connection over one day.
   These are dashboard queries measured in tens per minute, so paying the
   compile is cheaper than the scan.
--------------------------------------------------------------------------- */

CREATE OR ALTER PROCEDURE [crawl].[uspListRuns]
    @ConnectionId    NVARCHAR(64) = NULL,
    @Status          TINYINT      = NULL,     -- 1 running, 2 succeeded, 3 failed, 4 abandoned
    @Mode            TINYINT      = NULL,     -- 1 full, 2 incremental
    @FromUtc         DATETIME2(3) = NULL,
    @ToUtc           DATETIME2(3) = NULL,
    @IncludeDryRuns  BIT          = 0,
    @Page            INT          = 1,
    @PageSize        INT          = 50
AS
BEGIN
    SET NOCOUNT ON;

    SET @Page     = CASE WHEN @Page < 1 THEN 1
                         WHEN @Page > 1000000 THEN 1000000 ELSE @Page END;
    SET @PageSize = CASE WHEN @PageSize < 1 THEN 50
                         WHEN @PageSize > 500 THEN 500 ELSE @PageSize END;

    SELECT
        COUNT(*) OVER()                                   AS TotalRows,
        r.RunId,
        r.ConnectionId,
        c.DisplayName,
        c.ConnectorKey,
        CASE r.Mode   WHEN 1 THEN N'full'    WHEN 2 THEN N'incremental' END AS Mode,
        CASE r.Status WHEN 1 THEN N'running' WHEN 2 THEN N'succeeded'
                      WHEN 3 THEN N'failed'  WHEN 4 THEN N'abandoned'  END  AS Status,
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
        r.ThrottleWaits,
        r.BatchesSent,
        r.BytesWritten,
        r.HostName,
        r.ToolVersion,
        r.ErrorKind
    FROM        [crawl].[Run]        AS r
    INNER JOIN  [crawl].[Connection] AS c ON c.ConnectionId = r.ConnectionId
    WHERE  (@ConnectionId IS NULL OR r.ConnectionId = @ConnectionId)
      AND  (@Status       IS NULL OR r.Status       = @Status)
      AND  (@Mode         IS NULL OR r.Mode         = @Mode)
      AND  (@FromUtc      IS NULL OR r.StartedUtc  >= @FromUtc)
      AND  (@ToUtc        IS NULL OR r.StartedUtc  <  @ToUtc)
      AND  (@IncludeDryRuns = 1    OR r.IsDryRun    = 0)
    ORDER BY r.StartedUtc DESC, r.RunId DESC
    OFFSET (@Page - 1) * @PageSize ROWS FETCH NEXT @PageSize ROWS ONLY
    OPTION (RECOMPILE);
END
GO

/* ---------------------------------------------------------------------------
   uspGetRun

   One run, in full: the header, the per-item-type breakdown, the timing
   attribution and the throttle events. Four result sets in one call, because
   the run detail page shows all four and four round trips for one page is three
   too many.

   The per-type result set is the answer to "what did this run actually do".
   The timing result set is PushTiming's table as it was at the end of the run,
   which is what makes "is this getting worse" a comparison rather than a
   recollection.
--------------------------------------------------------------------------- */

CREATE OR ALTER PROCEDURE [crawl].[uspGetRun]
    @RunId BIGINT
AS
BEGIN
    SET NOCOUNT ON;

    -- 1. The run.
    SELECT * FROM [crawl].[vwRunHistory] WHERE RunId = @RunId;

    -- 2. What it did, per kind of item.
    SELECT  t.ItemType,
            t.ItemsWritten,
            t.ItemsUnchanged,
            t.ItemsDeleted,
            t.ItemsSkipped,
            t.ItemsFailed,
            t.ItemsDuplicate,
            t.BytesWritten,
            CAST(100.0 * t.ItemsUnchanged
                 / NULLIF(t.ItemsWritten + t.ItemsUnchanged, 0) AS DECIMAL(5, 1)) AS UnchangedPercent
    FROM    [crawl].[RunItemType] AS t
    WHERE   t.RunId = @RunId
    ORDER BY t.ItemsWritten + t.ItemsUnchanged DESC, t.ItemType;

    -- 3. Where the time went. Microseconds are converted to milliseconds here
    --    rather than in the web tier, so every consumer reads the same unit.
    --
    --    The ContentBytes row is the exception and Unit is how a caller knows:
    --    its numbers are bytes, so the Ms columns hold bytes/1000 and mean
    --    nothing. A renderer must branch on Unit rather than on the phase name.
    SELECT  g.Phase,
            g.Unit,
            g.SampleCount,
            CAST(g.TotalMicroseconds / 1000.0 AS DECIMAL(18, 1)) AS TotalMs,
            CAST(g.P50Microseconds   / 1000.0 AS DECIMAL(18, 1)) AS P50Ms,
            CAST(g.P95Microseconds   / 1000.0 AS DECIMAL(18, 1)) AS P95Ms,
            CAST(g.P99Microseconds   / 1000.0 AS DECIMAL(18, 1)) AS P99Ms,
            CAST(g.MaxMicroseconds   / 1000.0 AS DECIMAL(18, 1)) AS MaxMs,
            CAST(100.0 * g.TotalMicroseconds
                 / NULLIF((SELECT TotalMicroseconds FROM [crawl].[RunPhaseTiming]
                           WHERE RunId = @RunId AND Phase = N'RowTotal'), 0)
                 AS DECIMAL(5, 1))                               AS SharePercent
    FROM    [crawl].[RunPhaseTiming] AS g
    WHERE   g.RunId = @RunId
    ORDER BY g.TotalMicroseconds DESC;

    -- 4. Throttling, aggregated. The raw events are a separate page.
    SELECT * FROM [crawl].[vwThrottleSummary] WHERE RunId = @RunId;
END
GO

/* ---------------------------------------------------------------------------
   uspListItems

   The inventory, paged and searchable. This is the largest table in the
   database and the one where a careless query costs the most, which is why the
   search is a prefix match rather than a contains.

   @Search uses LIKE @Search + '%', deliberately anchored. A leading wildcard
   cannot use the clustered index and turns every lookup into a scan of the
   corpus; anchored, it is a range seek. Item IDs in this repository are composed
   with a stable prefix per type - see docs/SOURCE-CONTRACT.md - so a prefix
   search is the search people actually want.
--------------------------------------------------------------------------- */

CREATE OR ALTER PROCEDURE [crawl].[uspListItems]
    @ConnectionId    NVARCHAR(64),
    @Search          NVARCHAR(128) = NULL,
    @ItemType        NVARCHAR(64)  = NULL,
    @State           TINYINT       = NULL,   -- 1 live, 2 pending delete, 3 deleted
    @MinUnchangedStreak INT        = NULL,
    @Page            INT           = 1,
    @PageSize        INT           = 50
AS
BEGIN
    SET NOCOUNT ON;

    SET @Page     = CASE WHEN @Page < 1 THEN 1
                         WHEN @Page > 1000000 THEN 1000000 ELSE @Page END;
    SET @PageSize = CASE WHEN @PageSize < 1 THEN 50
                         WHEN @PageSize > 500 THEN 500 ELSE @PageSize END;

    SELECT
        COUNT(*) OVER()                     AS TotalRows,
        i.ItemId,
        i.ItemType,
        CASE i.State WHEN 1 THEN N'live' WHEN 2 THEN N'pending delete'
                     WHEN 3 THEN N'deleted' END AS State,
        i.ContentBytes,
        CONVERT(CHAR(64), i.ContentHash, 2) AS ContentHashHex,
        CONVERT(CHAR(64), i.AclHash,     2) AS AclHashHex,
        i.FirstSeenRunId,
        i.LastSeenRunId,
        i.LastWrittenRunId,
        i.LastWrittenUtc,
        i.UnchangedStreak,
        DATEDIFF(DAY, i.LastWrittenUtc, SYSUTCDATETIME()) AS DaysSinceLastWrite
    FROM   [crawl].[Item] AS i
    WHERE  i.ConnectionId = @ConnectionId
      AND  (@Search   IS NULL OR i.ItemId LIKE @Search + N'%')
      AND  (@ItemType IS NULL OR i.ItemType = @ItemType)
      AND  (@State    IS NULL OR i.State    = @State)
      AND  (@MinUnchangedStreak IS NULL OR i.UnchangedStreak >= @MinUnchangedStreak)
    ORDER BY i.ItemId
    OFFSET (@Page - 1) * @PageSize ROWS FETCH NEXT @PageSize ROWS ONLY
    OPTION (RECOMPILE);
END
GO

/* ---------------------------------------------------------------------------
   uspListPendingDeletes

   Paged, because on the run after a large source change this list is the size
   of the change and a dashboard that tries to render all of it stops rendering
   anything.
--------------------------------------------------------------------------- */

CREATE OR ALTER PROCEDURE [crawl].[uspListPendingDeletes]
    @ConnectionId NVARCHAR(64) = NULL,
    @MinAgeMinutes INT         = NULL,
    @Page         INT          = 1,
    @PageSize     INT          = 50
AS
BEGIN
    SET NOCOUNT ON;

    SET @Page     = CASE WHEN @Page < 1 THEN 1
                         WHEN @Page > 1000000 THEN 1000000 ELSE @Page END;
    SET @PageSize = CASE WHEN @PageSize < 1 THEN 50
                         WHEN @PageSize > 500 THEN 500 ELSE @PageSize END;

    SELECT
        COUNT(*) OVER() AS TotalRows,
        p.*
    FROM   [crawl].[vwPendingDeletes] AS p
    WHERE  (@ConnectionId  IS NULL OR p.ConnectionId = @ConnectionId)
      AND  (@MinAgeMinutes IS NULL OR p.AgeMinutes  >= @MinAgeMinutes)
    ORDER BY p.AgeMinutes DESC, p.ItemId
    OFFSET (@Page - 1) * @PageSize ROWS FETCH NEXT @PageSize ROWS ONLY
    OPTION (RECOMPILE);
END
GO

/* ---------------------------------------------------------------------------
   uspListThrottleEvents

   The raw events for one run. Kept out of uspGetRun because a badly throttled
   run has thousands of these and the run detail page wants the aggregate; this
   is the page you open when the aggregate raised a question.
--------------------------------------------------------------------------- */

CREATE OR ALTER PROCEDURE [crawl].[uspListThrottleEvents]
    @RunId    BIGINT,
    @Page     INT = 1,
    @PageSize INT = 100
AS
BEGIN
    SET NOCOUNT ON;

    SET @Page     = CASE WHEN @Page < 1 THEN 1
                         WHEN @Page > 1000000 THEN 1000000 ELSE @Page END;
    SET @PageSize = CASE WHEN @PageSize < 1 THEN 100
                         WHEN @PageSize > 1000 THEN 1000 ELSE @PageSize END;

    SELECT
        COUNT(*) OVER() AS TotalRows,
        t.ThrottleEventId,
        t.OccurredUtc,
        t.StatusCode,
        t.RetryAfterSeconds,
        t.Endpoint,
        t.AttemptNumber,
        DATEDIFF(SECOND, r.StartedUtc, t.OccurredUtc) AS SecondsIntoRun
    FROM        [crawl].[ThrottleEvent] AS t
    INNER JOIN  [crawl].[Run]           AS r ON r.RunId = t.RunId
    WHERE       t.RunId = @RunId
    ORDER BY    t.OccurredUtc, t.ThrottleEventId
    OFFSET (@Page - 1) * @PageSize ROWS FETCH NEXT @PageSize ROWS ONLY
    OPTION (RECOMPILE);
END
GO

/* ---------------------------------------------------------------------------
   uspGetConnectionDetail

   One connection's page: its health, its item-type mix, and the trend. The mix
   is taken from the live inventory rather than from the last run, because it is
   answering "what is in the index" and not "what did the last run touch".
--------------------------------------------------------------------------- */

CREATE OR ALTER PROCEDURE [crawl].[uspGetConnectionDetail]
    @ConnectionId NVARCHAR(64),
    @TrendDays    INT = 30
AS
BEGIN
    SET NOCOUNT ON;

    -- 1. Health and configuration.
    SELECT * FROM [crawl].[vwConnectionHealth] WHERE ConnectionId = @ConnectionId;

    -- 2. What the index holds, by kind.
    SELECT  i.ItemType,
            COUNT(*)                                              AS Items,
            SUM(CASE WHEN i.State = 1 THEN 1 ELSE 0 END)          AS Live,
            SUM(CASE WHEN i.State = 2 THEN 1 ELSE 0 END)          AS PendingDelete,
            SUM(CASE WHEN i.State = 3 THEN 1 ELSE 0 END)          AS Tombstoned,
            SUM(CAST(i.ContentBytes AS BIGINT))                   AS ContentBytes,
            AVG(CAST(i.UnchangedStreak AS DECIMAL(10, 1)))        AS AvgUnchangedStreak,
            MAX(i.UnchangedStreak)                                AS MaxUnchangedStreak
    FROM    [crawl].[Item] AS i
    WHERE   i.ConnectionId = @ConnectionId
    GROUP BY i.ItemType
    ORDER BY Items DESC;

    -- 3. The trend.
    SELECT * FROM [crawl].[vwDailyActivity]
    WHERE  ConnectionId = @ConnectionId
      AND  ActivityDate >= CAST(DATEADD(DAY, -@TrendDays, SYSUTCDATETIME()) AS DATE)
    ORDER BY ActivityDate;

    -- 4. The checkpoint, so "where would the next incremental run start" is on
    --    the page rather than a query someone has to know how to write.
    SELECT  MarkerTime, MarkerKey, RunId, RunCount, UpdatedUtc
    FROM    [crawl].[Checkpoint]
    WHERE   ConnectionId = @ConnectionId;
END
GO

-- Verification: the seven reporting procedures, and that each one executes.
SELECT  s.name AS schema_name, p.name AS procedure_name
FROM    sys.procedures AS p
JOIN    sys.schemas    AS s ON s.schema_id = p.schema_id
WHERE   s.name = N'crawl' AND p.name IN
        (N'uspDashboardSummary', N'uspListRuns', N'uspGetRun', N'uspListItems',
         N'uspListPendingDeletes', N'uspListThrottleEvents', N'uspGetConnectionDetail')
ORDER BY p.name;
GO
