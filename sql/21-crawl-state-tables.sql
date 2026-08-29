-- ===========================================================================
-- 21-crawl-state-tables.sql
--
-- The eight tables, and the indexes that make the two hot paths seeks.
--
-- Each table exists because one of the ten agent features needs it. The mapping
-- is deliberate and worth keeping in mind when changing anything here:
--
--   crawl.Connection      1  scheduling: what interval was this meant to run at
--   crawl.Run             5  admin-centre substitute: crawl history and health
--   crawl.Item            2  delete detection - the inventory to diff against
--                         3  change detection - the hashes to compare
--                         4  duplicate detection across a resumed run
--                        10  quota efficiency, which is 3 by another name
--   crawl.Checkpoint      9  checkpointing: where the last run got to
--   crawl.PrincipalMap    6  identity mapping, cached with a TTL
--   crawl.ThrottleEvent   7  throttling, made visible after the fact
--   crawl.RunPhaseTiming  5  where the time went, per run, kept
--   crawl.RunItemType     5  what the run did, per kind of item - the drill-down
--                            grain the dashboard pages through
--
-- Run once per environment, after sql/20 and before sql/22.
--
-- Idempotent: every object is created only if absent, so this file is safe to
-- re-run against an environment that is already up. It does not ALTER an
-- existing table - a schema change ships as its own numbered migration, because
-- silently widening a column under a running connector is how a deployment
-- becomes unreviewable.
-- ===========================================================================

USE [ConnectorState];
GO

/* ---------------------------------------------------------------------------
   1. crawl.Connection

   One row per Graph external connection this store serves. It exists so the
   store can answer questions about a connection that has never run - "is this
   configured and enabled" - and so missed-run detection has an expectation to
   compare against.

   ExpectedIntervalMinutes is the whole of feature 1 that a database can hold.
   The push tool cannot schedule itself; Task Scheduler or cron does that. What
   it CAN do is know it was supposed to have run forty minutes ago and did not,
   and say so - which is the half of scheduling that actually catches problems.
   Null means "no expectation", and the health view reports staleness rather
   than lateness for those.
--------------------------------------------------------------------------- */

IF OBJECT_ID(N'crawl.Connection', N'U') IS NULL
BEGIN
    CREATE TABLE [crawl].[Connection]
    (
        ConnectionId            NVARCHAR(64)   NOT NULL,
        ConnectorKey            NVARCHAR(64)   NOT NULL,
        DisplayName             NVARCHAR(256)  NOT NULL,
        ExpectedIntervalMinutes INT            NULL,
        IsEnabled               BIT            NOT NULL
            CONSTRAINT DF_Connection_IsEnabled DEFAULT (1),
        CreatedUtc              DATETIME2(3)   NOT NULL
            CONSTRAINT DF_Connection_CreatedUtc DEFAULT (SYSUTCDATETIME()),
        UpdatedUtc              DATETIME2(3)   NOT NULL
            CONSTRAINT DF_Connection_UpdatedUtc DEFAULT (SYSUTCDATETIME()),

        CONSTRAINT PK_Connection PRIMARY KEY CLUSTERED (ConnectionId),
        CONSTRAINT CK_Connection_Interval CHECK (ExpectedIntervalMinutes IS NULL OR ExpectedIntervalMinutes > 0)
    );
END
GO

/* ---------------------------------------------------------------------------
   2. crawl.Run

   One row per crawl run, opened before the first read and closed by exactly one
   of uspCompleteRun or uspFailRun. A row still in status 1 with an old
   StartedUtc is a run whose process died without either - the health view calls
   those abandoned, and uspBeginRun reaps them, because a run that never closed
   would otherwise hold the inventory's LastSeenRunId at a value no sweep can
   safely diff against.

   Mode matters to the delete sweep and nothing else. Only a FULL run may
   conclude that an item the source stopped returning has been deleted; an
   incremental run reads a slice by definition, so absence from it means
   nothing. Getting this backwards deletes the corpus, which is why Mode is
   stored rather than inferred and why uspGetPendingDeletes refuses to answer
   for an incremental run at all.
--------------------------------------------------------------------------- */

IF OBJECT_ID(N'crawl.Run', N'U') IS NULL
BEGIN
    CREATE TABLE [crawl].[Run]
    (
        RunId            BIGINT         IDENTITY(1, 1) NOT NULL,
        ConnectionId     NVARCHAR(64)   NOT NULL,

        -- 1 = full, 2 = incremental.
        Mode             TINYINT        NOT NULL,

        -- 1 = running, 2 = succeeded, 3 = failed, 4 = abandoned.
        Status           TINYINT        NOT NULL,

        StartedUtc       DATETIME2(3)   NOT NULL
            CONSTRAINT DF_Run_StartedUtc DEFAULT (SYSUTCDATETIME()),
        CompletedUtc     DATETIME2(3)   NULL,

        -- Who ran it. A push tool is operator-run and may be run from more than
        -- one host; when two runs overlap, this is how you find the second one.
        HostName         NVARCHAR(128)  NOT NULL,
        ProcessId        INT            NOT NULL,
        ToolVersion      NVARCHAR(64)   NOT NULL,
        IsDryRun         BIT            NOT NULL
            CONSTRAINT DF_Run_IsDryRun DEFAULT (0),

        ItemsRead        INT            NOT NULL CONSTRAINT DF_Run_ItemsRead      DEFAULT (0),
        ItemsWritten     INT            NOT NULL CONSTRAINT DF_Run_ItemsWritten   DEFAULT (0),
        ItemsUnchanged   INT            NOT NULL CONSTRAINT DF_Run_ItemsUnchanged DEFAULT (0),
        ItemsDeleted     INT            NOT NULL CONSTRAINT DF_Run_ItemsDeleted   DEFAULT (0),
        ItemsSkipped     INT            NOT NULL CONSTRAINT DF_Run_ItemsSkipped   DEFAULT (0),
        ItemsFailed      INT            NOT NULL CONSTRAINT DF_Run_ItemsFailed    DEFAULT (0),
        ItemsDuplicate   INT            NOT NULL CONSTRAINT DF_Run_ItemsDuplicate DEFAULT (0),
        ThrottleWaits    INT            NOT NULL CONSTRAINT DF_Run_ThrottleWaits  DEFAULT (0),
        BytesWritten     BIGINT         NOT NULL CONSTRAINT DF_Run_BytesWritten   DEFAULT (0),
        BatchesSent      INT            NOT NULL CONSTRAINT DF_Run_BatchesSent    DEFAULT (0),

        -- Kind is a short stable token the runbook can index on; Message is for
        -- a person. Neither may carry a property value or row content: this
        -- database is more widely readable than Ops, and the whole point of the
        -- logging policy upstream is undone by putting the row in the error.
        ErrorKind        NVARCHAR(64)   NULL,
        ErrorMessage     NVARCHAR(2000) NULL,

        CONSTRAINT PK_Run PRIMARY KEY CLUSTERED (RunId),
        CONSTRAINT FK_Run_Connection FOREIGN KEY (ConnectionId)
            REFERENCES [crawl].[Connection] (ConnectionId),
        CONSTRAINT CK_Run_Mode   CHECK (Mode   IN (1, 2)),
        CONSTRAINT CK_Run_Status CHECK (Status IN (1, 2, 3, 4)),
        CONSTRAINT CK_Run_Completed CHECK
            ((Status = 1 AND CompletedUtc IS NULL) OR (Status <> 1 AND CompletedUtc IS NOT NULL))
    );
END
GO

-- The health view asks for the newest run per connection, and uspBeginRun asks
-- for the newest COMPLETED full run. Descending on StartedUtc makes both a seek
-- of one row rather than a scan of the history.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'crawl.Run') AND name = N'IX_Run_Connection_Started')
BEGIN
    CREATE NONCLUSTERED INDEX IX_Run_Connection_Started
        ON [crawl].[Run] (ConnectionId, StartedUtc DESC)
        INCLUDE (Mode, Status, CompletedUtc, ItemsWritten, ItemsDeleted);
END
GO

-- Reaping abandoned runs is a filtered scan of the few rows still open, which
-- is a tiny index rather than a predicate over every run ever made.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'crawl.Run') AND name = N'IX_Run_Open')
BEGIN
    CREATE NONCLUSTERED INDEX IX_Run_Open
        ON [crawl].[Run] (ConnectionId, StartedUtc)
        WHERE Status = 1;
END
GO

/* ---------------------------------------------------------------------------
   3. crawl.Item - the inventory

   The single most important table here, because it is the one that turns
   "everything the source returned" into "everything the index holds". Four of
   the ten features are this table.

   THE TWO HASHES ARE SEPARATE ON PURPOSE. Content and ACL change for different
   reasons and at different rates: a document's text is edited by a person, its
   permissions by a group membership change that touches ten thousand items at
   once. Keeping them apart means the engine can say which of the two moved,
   and a future optimisation that rewrites only the ACL has somewhere to stand.
   Hashing them together would be one column and would answer neither question.

   LastSeenRunId is what the delete sweep diffs on: after a full run completes,
   every live row whose LastSeenRunId is older than that run is an item the
   source no longer returns. LastWrittenRunId is different and is not
   interchangeable - an unchanged item is SEEN every run and WRITTEN rarely, and
   confusing the two either deletes the corpus or never deletes anything.

   State exists so a delete that Graph refused is not forgotten. An item moves
   live -> pending delete when a sweep identifies it, and pending delete ->
   deleted only when Graph confirms the removal. A row stuck in pending delete
   is a real operational signal, and vwPendingDeletes surfaces it.
--------------------------------------------------------------------------- */

IF OBJECT_ID(N'crawl.Item', N'U') IS NULL
BEGIN
    CREATE TABLE [crawl].[Item]
    (
        ConnectionId      NVARCHAR(64)   NOT NULL,
        ItemId            NVARCHAR(128)  NOT NULL,
        ItemType          NVARCHAR(64)   NOT NULL,

        ContentHash       BINARY(32)     NOT NULL,
        AclHash           BINARY(32)     NOT NULL,
        ContentBytes      INT            NOT NULL,

        FirstSeenRunId    BIGINT         NOT NULL,
        LastSeenRunId     BIGINT         NOT NULL,
        LastWrittenRunId  BIGINT         NOT NULL,
        LastWrittenUtc    DATETIME2(3)   NOT NULL,

        -- 1 = live, 2 = pending delete, 3 = deleted.
        State             TINYINT        NOT NULL
            CONSTRAINT DF_Item_State DEFAULT (1),

        -- When the sweep moved this item to pending delete, and null whenever it
        -- is not. It exists because "how long has this deletion been stuck" has
        -- no other honest source: LastWrittenUtc answers when the item was last
        -- WRITTEN, so on a corpus of long-unchanged items every freshly pending
        -- row would read as weeks old and any age-based alert would fire on
        -- every sweep. Cleared when the delete is confirmed and when the item
        -- comes back.
        PendingSinceUtc   DATETIME2(3)   NULL,

        -- How many consecutive runs found this item unchanged. Not needed by any
        -- decision here; it is the number that makes the case for incremental
        -- reads to the SQL team, because "94% of items were unchanged for the
        -- last thirty runs" is an argument and "the push is slow" is not.
        UnchangedStreak   INT            NOT NULL
            CONSTRAINT DF_Item_UnchangedStreak DEFAULT (0),

        CONSTRAINT PK_Item PRIMARY KEY CLUSTERED (ConnectionId, ItemId),
        CONSTRAINT FK_Item_Connection FOREIGN KEY (ConnectionId)
            REFERENCES [crawl].[Connection] (ConnectionId),
        CONSTRAINT CK_Item_State CHECK (State IN (1, 2, 3)),
        CONSTRAINT CK_Item_Bytes CHECK (ContentBytes >= 0),

        -- The two halves of State 2 travel together or the age is a lie.
        CONSTRAINT CK_Item_Pending CHECK
            ((State = 2 AND PendingSinceUtc IS NOT NULL) OR (State <> 2 AND PendingSinceUtc IS NULL))
    );
END
GO

-- The delete sweep: live rows for a connection not seen by the current run.
-- Filtered to live rows because the sweep never asks about anything else, which
-- keeps the index proportional to the live corpus rather than to its history.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'crawl.Item') AND name = N'IX_Item_Sweep')
BEGIN
    CREATE NONCLUSTERED INDEX IX_Item_Sweep
        ON [crawl].[Item] (ConnectionId, LastSeenRunId)
        INCLUDE (ItemType)
        WHERE State = 1;
END
GO

-- Anything not live: the pending-delete backlog, and the tombstones the purge
-- eventually removes. Small by definition, so this stays cheap.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'crawl.Item') AND name = N'IX_Item_NotLive')
BEGIN
    CREATE NONCLUSTERED INDEX IX_Item_NotLive
        ON [crawl].[Item] (ConnectionId, State, LastWrittenUtc)
        WHERE State <> 1;
END
GO

/* ---------------------------------------------------------------------------
   4. crawl.Checkpoint

   One marker per connection, composite for the reason CrawlCheckpoint.cs and
   Watermark.cs already argue at length: two rows can share a modification
   timestamp to the millisecond, and a marker of only the timestamp either
   re-reads that whole group for ever or loses whichever of them had not been
   written when the run stopped. The pair makes the ordering total and "strictly
   after the marker" exact.

   RunId records which run last advanced it, so a marker that looks wrong can be
   traced to the run that set it rather than guessed at.
--------------------------------------------------------------------------- */

IF OBJECT_ID(N'crawl.Checkpoint', N'U') IS NULL
BEGIN
    CREATE TABLE [crawl].[Checkpoint]
    (
        ConnectionId  NVARCHAR(64)   NOT NULL,
        MarkerTime    DATETIME2(3)   NULL,
        MarkerKey     NVARCHAR(256)  NULL,
        RunId         BIGINT         NOT NULL,
        RunCount      INT            NOT NULL CONSTRAINT DF_Checkpoint_RunCount DEFAULT (0),
        UpdatedUtc    DATETIME2(3)   NOT NULL CONSTRAINT DF_Checkpoint_UpdatedUtc DEFAULT (SYSUTCDATETIME()),

        CONSTRAINT PK_Checkpoint PRIMARY KEY CLUSTERED (ConnectionId),
        CONSTRAINT FK_Checkpoint_Connection FOREIGN KEY (ConnectionId)
            REFERENCES [crawl].[Connection] (ConnectionId)
    );
END
GO

/* ---------------------------------------------------------------------------
   5. crawl.PrincipalMap

   Feature 6. The agent resolves source identities to Entra when it stamps an
   ACL; a push has to do it itself, and doing it per item means a Graph
   directory lookup per row for values that change monthly at most.

   EntraObjectId is nullable and a null is meaningful: it is a NEGATIVE cache
   entry, recording that this source principal resolved to nothing. Without it a
   cluster group that has no Entra counterpart is looked up on every item of
   every run for ever, which is the single most expensive thing an unbounded
   resolver does. ExpiresUtc is what keeps a negative entry from being
   permanent - the group may be created tomorrow.

   Negative entries should expire faster than positive ones. The store passes
   both TTLs; this table only records the answer.
--------------------------------------------------------------------------- */

IF OBJECT_ID(N'crawl.PrincipalMap', N'U') IS NULL
BEGIN
    CREATE TABLE [crawl].[PrincipalMap]
    (
        ConnectionId   NVARCHAR(64)     NOT NULL,

        -- 'AdGroup', 'PosixGroup', 'RangerGroup', 'Upn'. Free text rather than a
        -- lookup table: the set grows with every new source family, and a
        -- foreign key here would make adding one a migration.
        SourceType     NVARCHAR(32)     NOT NULL,
        SourceKey      NVARCHAR(256)    NOT NULL,

        EntraObjectId  UNIQUEIDENTIFIER NULL,
        EntraType      NVARCHAR(16)     NULL,

        ResolvedUtc    DATETIME2(3)     NOT NULL CONSTRAINT DF_PrincipalMap_ResolvedUtc DEFAULT (SYSUTCDATETIME()),
        ExpiresUtc     DATETIME2(3)     NOT NULL,
        HitCount       INT              NOT NULL CONSTRAINT DF_PrincipalMap_HitCount DEFAULT (0),

        CONSTRAINT PK_PrincipalMap PRIMARY KEY CLUSTERED (ConnectionId, SourceType, SourceKey),
        CONSTRAINT FK_PrincipalMap_Connection FOREIGN KEY (ConnectionId)
            REFERENCES [crawl].[Connection] (ConnectionId),
        CONSTRAINT CK_PrincipalMap_Type CHECK (EntraType IS NULL OR EntraType IN (N'group', N'user'))
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'crawl.PrincipalMap') AND name = N'IX_PrincipalMap_Expiry')
BEGIN
    CREATE NONCLUSTERED INDEX IX_PrincipalMap_Expiry
        ON [crawl].[PrincipalMap] (ExpiresUtc)
        INCLUDE (ConnectionId);
END
GO

/* ---------------------------------------------------------------------------
   6. crawl.ThrottleEvent

   Feature 7 is already implemented in the engine - it backs off, it honours
   Retry-After. What it could not do before this table is tell you afterwards
   that it did. One row per refusal, which turns "the run was slow" into "the
   tenant returned 41 429s between 02:10 and 02:14, asking for 20 seconds each"
   - the difference between a capacity conversation and a guess.

   Deliberately not aggregated on write. Aggregation is vwThrottleSummary's job,
   and keeping the raw events means a question nobody anticipated - were they
   clustered, did they follow the batch size change - is still answerable.
--------------------------------------------------------------------------- */

IF OBJECT_ID(N'crawl.ThrottleEvent', N'U') IS NULL
BEGIN
    CREATE TABLE [crawl].[ThrottleEvent]
    (
        ThrottleEventId   BIGINT       IDENTITY(1, 1) NOT NULL,
        RunId             BIGINT       NOT NULL,
        OccurredUtc       DATETIME2(3) NOT NULL CONSTRAINT DF_ThrottleEvent_OccurredUtc DEFAULT (SYSUTCDATETIME()),
        StatusCode        INT          NOT NULL,
        RetryAfterSeconds INT          NULL,

        -- 'item' for a single PUT, 'batch' for a $batch sub-request, 'schema'
        -- for the registration poll. Which surface is being throttled decides
        -- whether turning writers down would help at all.
        Endpoint          NVARCHAR(32) NOT NULL,
        AttemptNumber     INT          NOT NULL,

        CONSTRAINT PK_ThrottleEvent PRIMARY KEY CLUSTERED (ThrottleEventId),
        CONSTRAINT FK_ThrottleEvent_Run FOREIGN KEY (RunId) REFERENCES [crawl].[Run] (RunId)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'crawl.ThrottleEvent') AND name = N'IX_ThrottleEvent_Run')
BEGIN
    CREATE NONCLUSTERED INDEX IX_ThrottleEvent_Run
        ON [crawl].[ThrottleEvent] (RunId, OccurredUtc);
END
GO

/* ---------------------------------------------------------------------------
   7. crawl.RunPhaseTiming

   PushTiming.Report() renders a table an operator reads once in a log file and
   then loses. Persisted per run, the same numbers answer a question the log
   never can: is this getting worse, and since when.

   Percentiles rather than means, for the reason PushTiming.cs gives - one row
   that waited sixty seconds behind a Retry-After moves a mean and tells you
   nothing about the other thousand. Microseconds because that is the unit the
   engine measures in, and converting on the way in would round away the
   fast phases.
--------------------------------------------------------------------------- */

IF OBJECT_ID(N'crawl.RunPhaseTiming', N'U') IS NULL
BEGIN
    CREATE TABLE [crawl].[RunPhaseTiming]
    (
        RunId             BIGINT       NOT NULL,

        -- 'SourceRead', 'Prepare', 'WriteInFlight', 'WriteBackoff', 'Commit',
        -- 'RowTotal', 'ContentBytes'. Matching PushTiming's own series names,
        -- which are property names rather than the report's display labels,
        -- because they land in a primary key and must not be re-worded.
        Phase             NVARCHAR(32) NOT NULL,

        -- What the numbers below are measured in: 'microseconds' for the six
        -- timing phases, 'bytes' for ContentBytes. Stored rather than inferred
        -- from the phase name, so a report can render a unit it was not written
        -- to know about.
        Unit              NVARCHAR(16) NOT NULL
            CONSTRAINT DF_RunPhaseTiming_Unit DEFAULT (N'microseconds'),

        SampleCount       BIGINT       NOT NULL,
        TotalMicroseconds BIGINT       NOT NULL,
        P50Microseconds   BIGINT       NOT NULL,
        P95Microseconds   BIGINT       NOT NULL,
        P99Microseconds   BIGINT       NOT NULL,
        MaxMicroseconds   BIGINT       NOT NULL,

        CONSTRAINT PK_RunPhaseTiming PRIMARY KEY CLUSTERED (RunId, Phase),
        CONSTRAINT FK_RunPhaseTiming_Run FOREIGN KEY (RunId) REFERENCES [crawl].[Run] (RunId)
    );
END
GO

/* ---------------------------------------------------------------------------
   8. crawl.RunItemType - the work, per connector, per run, per kind of thing

   crawl.Run says a run wrote 1,118 items. This says it wrote 12 customers, 62
   engagements and 1,044 time entries, and that the customers were all unchanged
   while every time entry was rewritten. That second sentence is the one that
   tells an operator what actually happened, and it is the grain the dashboard
   drills down to.

   It is a separate table rather than more columns on crawl.Run because the set
   of item types is the connector's, not this schema's: a new connector invents
   its own kinds, and a design that needed a migration for each one would be
   a design nobody added a connector to.

   Written once at the end of a run, from PushSummary.ByType, in a single call.
--------------------------------------------------------------------------- */

IF OBJECT_ID(N'crawl.RunItemType', N'U') IS NULL
BEGIN
    CREATE TABLE [crawl].[RunItemType]
    (
        RunId          BIGINT       NOT NULL,
        ItemType       NVARCHAR(64) NOT NULL,

        ItemsWritten   INT          NOT NULL CONSTRAINT DF_RunItemType_Written   DEFAULT (0),
        ItemsUnchanged INT          NOT NULL CONSTRAINT DF_RunItemType_Unchanged DEFAULT (0),
        ItemsDeleted   INT          NOT NULL CONSTRAINT DF_RunItemType_Deleted   DEFAULT (0),
        ItemsSkipped   INT          NOT NULL CONSTRAINT DF_RunItemType_Skipped   DEFAULT (0),
        ItemsFailed    INT          NOT NULL CONSTRAINT DF_RunItemType_Failed    DEFAULT (0),
        BytesWritten   BIGINT       NOT NULL CONSTRAINT DF_RunItemType_Bytes     DEFAULT (0),

        CONSTRAINT PK_RunItemType PRIMARY KEY CLUSTERED (RunId, ItemType),
        CONSTRAINT FK_RunItemType_Run FOREIGN KEY (RunId) REFERENCES [crawl].[Run] (RunId)
    );
END
GO

-- Verification: the eight tables and their row counts.
SELECT  s.name AS schema_name,
        t.name AS table_name,
        SUM(CASE WHEN p.index_id IN (0, 1) THEN p.rows ELSE 0 END) AS row_count
FROM    sys.tables      AS t
JOIN    sys.schemas     AS s ON s.schema_id = t.schema_id
JOIN    sys.partitions  AS p ON p.object_id = t.object_id
WHERE   s.name = N'crawl'
GROUP BY s.name, t.name
ORDER BY t.name;
GO
