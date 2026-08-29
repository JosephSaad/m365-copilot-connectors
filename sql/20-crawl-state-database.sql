-- ===========================================================================
-- 20-crawl-state-database.sql
--
-- Creates the database that gives a direct push the memory the Graph connector
-- agent used to keep for it.
--
-- The agent remembers three things on your behalf: what it has already sent,
-- what each item looked like when it sent it, and how far it got. A push tool
-- has none of that, which is why a push cannot detect a deletion, cannot skip
-- an unchanged row, and cannot resume. This database is that memory, made
-- explicit and auditable.
--
-- WHY A SEPARATE DATABASE, NOT A SCHEMA IN Ops.
-- The connector reads Ops with SELECT and nothing else - sql/01 and sql/13 go
-- to some trouble to DENY the rest. Crawl state has to be WRITTEN by that same
-- process. Putting it in Ops would mean granting write access inside the
-- database that holds the customer's records, and the grant would be visible to
-- anyone reviewing the connector as "the search connector can write to Ops".
-- A separate database keeps the two sentences true at once: read-only on the
-- data, read-write on its own bookkeeping. Restore, retention and backup
-- schedules also differ - losing crawl state costs one full recrawl, losing Ops
-- costs the business.
--
-- The connector never touches these tables directly. sql/25 grants EXECUTE on
-- the write procedures in sql/23 and DENY on everything else, so the connector's
-- write surface is sixteen procedures rather than eight tables - and the
-- dashboard's read surface is the views in sql/22 plus the paged procedures in
-- sql/24, with no table permission of any kind. That is what makes "what can
-- each of these two things do to this database" answerable by reading one file
-- rather than by auditing their queries.
--
-- Run once per environment, as a member of dbcreator or sysadmin, BEFORE
-- sql/21 through 25, which assume the database and the schema exist.
-- ===========================================================================

USE [master];
GO

/* ---------------------------------------------------------------------------
   1. The database.

   Sized for bookkeeping, not for data. The inventory is one narrow row per
   indexed item: a million-item corpus is well under a gigabyte, and the run
   history is bounded by crawl.uspPurgeHistory rather than by growth.

   RECOVERY SIMPLE is deliberate and is the one place this file makes a policy
   choice for you. The worst case for losing this database between backups is a
   single full recrawl, which is a cost in time and Graph quota rather than in
   correctness - every write is an upsert. Paying for point-in-time recovery of
   a cache is paying for the wrong thing. If your estate mandates FULL for every
   database, change it here; nothing downstream depends on the model.

   THE FILE PATHS BELOW ARE PLACEHOLDERS AND WILL FAIL ON ANY INSTANCE WITHOUT A
   D: DRIVE. That is deliberate rather than defaulted: a CREATE DATABASE that
   silently lands in the instance's default data directory is one nobody notices
   until the volume it chose fills up. Edit them, or delete the ON PRIMARY and
   LOG ON clauses entirely to accept the instance defaults knowingly.

   Adjust the file paths to match the instance. The sizes are starting points,
   not limits: autogrowth is on and in fixed increments rather than percentages,
   because percentage growth on a file that is already large is how a crawl
   stalls for thirty seconds in the middle of a run.
--------------------------------------------------------------------------- */

IF DB_ID(N'ConnectorState') IS NULL
BEGIN
    CREATE DATABASE [ConnectorState]
    ON PRIMARY
    (
        NAME     = N'ConnectorState',
        FILENAME = N'D:\SQLData\ConnectorState.mdf',
        SIZE     = 256MB,
        FILEGROWTH = 128MB
    )
    LOG ON
    (
        NAME     = N'ConnectorState_log',
        FILENAME = N'D:\SQLLogs\ConnectorState_log.ldf',
        SIZE     = 64MB,
        FILEGROWTH = 64MB
    );
END
GO

-- GUARDED, because this file is meant to be re-runnable and these three
-- statements are the ones that are not idempotent in the way that matters.
-- Unguarded, SET RECOVERY SIMPLE silently reverts the FULL override the header
-- above explicitly invites an estate to make, and RCSI's WITH ROLLBACK
-- IMMEDIATE kills every live session on a database that is already serving a
-- crawl.
--
-- The recovery model is set only while the database is still EMPTY - that is,
-- on the first run, before sql/21 creates anything. After that the operator's
-- choice stands: this file states a preference and does not enforce one.
IF EXISTS (SELECT 1 FROM sys.databases
           WHERE name = N'ConnectorState' AND recovery_model_desc <> N'SIMPLE')
   AND NOT EXISTS (SELECT 1 FROM [ConnectorState].sys.tables)
BEGIN
    ALTER DATABASE [ConnectorState] SET RECOVERY SIMPLE;
END
GO

IF EXISTS (SELECT 1 FROM sys.databases
           WHERE name = N'ConnectorState' AND is_read_committed_snapshot_on = 0)
BEGIN
    ALTER DATABASE [ConnectorState] SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;
END
GO

-- Auto-close off: a connector that runs every fifteen minutes would otherwise
-- pay database startup on most runs, which shows up as a slow first query and
-- gets misdiagnosed as a network problem.
IF EXISTS (SELECT 1 FROM sys.databases WHERE name = N'ConnectorState' AND is_auto_close_on = 1)
BEGIN
    ALTER DATABASE [ConnectorState] SET AUTO_CLOSE OFF;
END
GO

USE [ConnectorState];
GO

/* ---------------------------------------------------------------------------
   2. The schema.

   Everything lives in [crawl]. Nothing is created in dbo, so a grant on dbo -
   the one people write by habit - grants nothing here.
--------------------------------------------------------------------------- */

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'crawl')
BEGIN
    EXEC(N'CREATE SCHEMA [crawl] AUTHORIZATION [dbo];');
END
GO

/* ---------------------------------------------------------------------------
   3. Table types.

   The push engine reports item state in batches, not one row per round trip.
   At 3.5 seconds a row the network was never the bottleneck; at the rates
   batching makes possible it would be, and a per-item UPDATE would put a second
   round trip beside every Graph write.

   ItemStateList carries what the engine knows after preparing an item and
   before writing it. The hashes are BINARY(32) rather than a hex string: it is
   half the bytes, it compares as a value rather than by collation, and a
   collation mismatch between two databases would silently make every item look
   changed - the failure mode being a full rewrite of the corpus that nothing
   reports as wrong.
--------------------------------------------------------------------------- */

IF TYPE_ID(N'crawl.ItemStateList') IS NULL
BEGIN
    CREATE TYPE [crawl].[ItemStateList] AS TABLE
    (
        ItemId        NVARCHAR(128) NOT NULL,
        ItemType      NVARCHAR(64)  NOT NULL,
        ContentHash   BINARY(32)    NOT NULL,
        AclHash       BINARY(32)    NOT NULL,
        ContentBytes  INT           NOT NULL,
        PRIMARY KEY CLUSTERED (ItemId)
    );
END
GO

IF TYPE_ID(N'crawl.ItemIdList') IS NULL
BEGIN
    CREATE TYPE [crawl].[ItemIdList] AS TABLE
    (
        ItemId NVARCHAR(128) NOT NULL,
        PRIMARY KEY CLUSTERED (ItemId)
    );
END
GO

-- Source principals, and NOT the same type as ItemIdList even though both are
-- one string column. An item ID is capped at 128 characters by Graph; a source
-- principal is not, and an Active Directory distinguished name routinely runs
-- past it. Reusing ItemIdList here would let a principal be CACHED at its full
-- length and never LOOKED UP again, because the lookup would silently truncate
-- and match nothing - or worse, match a different principal's row and stamp an
-- item with the wrong group.
IF TYPE_ID(N'crawl.PrincipalKeyList') IS NULL
BEGIN
    CREATE TYPE [crawl].[PrincipalKeyList] AS TABLE
    (
        SourceKey NVARCHAR(256) NOT NULL,
        PRIMARY KEY CLUSTERED (SourceKey)
    );
END
GO

-- Throttle events, batched. They are buffered in the connector for the whole
-- run and flushed once at the end, so the flush is a single round trip rather
-- than one per refusal - which matters most on exactly the run that produced
-- hundreds of them.
IF TYPE_ID(N'crawl.ThrottleEventList') IS NULL
BEGIN
    CREATE TYPE [crawl].[ThrottleEventList] AS TABLE
    (
        OccurredUtc       DATETIME2(3) NOT NULL,
        StatusCode        INT          NOT NULL,
        RetryAfterSeconds INT          NULL,
        Endpoint          NVARCHAR(32) NOT NULL,
        AttemptNumber     INT          NOT NULL
    );
END
GO

-- The per-item-type breakdown of one run. PushSummary already counts by type
-- for its log line - "Customer=12, Engagement=62, TimeEntry=1044" - and that
-- breakdown is the only thing that says WHICH work a run did rather than how
-- much. Aggregated away, a run that indexed a thousand time entries and no
-- customers looks exactly like one that did the reverse.
IF TYPE_ID(N'crawl.ItemTypeCountList') IS NULL
BEGIN
    CREATE TYPE [crawl].[ItemTypeCountList] AS TABLE
    (
        ItemType       NVARCHAR(64) NOT NULL,
        ItemsWritten   INT          NOT NULL,
        ItemsUnchanged INT          NOT NULL,
        ItemsDeleted   INT          NOT NULL,
        ItemsSkipped   INT          NOT NULL,
        ItemsFailed    INT          NOT NULL,
        BytesWritten   BIGINT       NOT NULL,
        PRIMARY KEY CLUSTERED (ItemType)
    );
END
GO

-- The timing attribution table from PushTiming.Report(), persisted per run so
-- "was last Tuesday's run throttle-bound" is a query rather than a log hunt.
IF TYPE_ID(N'crawl.PhaseTimingList') IS NULL
BEGIN
    CREATE TYPE [crawl].[PhaseTimingList] AS TABLE
    (
        Phase             NVARCHAR(32) NOT NULL,

        -- 'microseconds' for every timing phase, 'bytes' for ContentBytes.
        -- PushTiming holds one series for content size alongside its six timing
        -- series, and without this column the byte percentiles sit in fields
        -- named Microseconds - a reader would have to know which phase is the
        -- odd one out, and eventually one would not.
        Unit              NVARCHAR(16) NOT NULL,

        SampleCount       BIGINT       NOT NULL,
        TotalMicroseconds BIGINT       NOT NULL,
        P50Microseconds   BIGINT       NOT NULL,
        P95Microseconds   BIGINT       NOT NULL,
        P99Microseconds   BIGINT       NOT NULL,
        MaxMicroseconds   BIGINT       NOT NULL,
        PRIMARY KEY CLUSTERED (Phase)
    );
END
GO

-- Verification: the database, the schema and the six table types.
SELECT  name AS database_name, recovery_model_desc, is_read_committed_snapshot_on
FROM    sys.databases
WHERE   name = N'ConnectorState';

SELECT  s.name AS schema_name, t.name AS type_name
FROM    sys.table_types AS t
JOIN    sys.schemas     AS s ON s.schema_id = t.schema_id
WHERE   s.name = N'crawl'
ORDER BY t.name;
GO
