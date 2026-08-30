-- ===========================================================================
-- 23-crawl-state-procedures.sql
--
-- The connector's entire write surface. Nineteen procedures are defined here;
-- sql/25 grants seventeen of them to crawl_writer and deliberately withholds
-- uspResetCheckpoint and uspPurgeHistory. Behind all of them, no table
-- permission at all - sql/25 grants EXECUTE here and DENY on everything in
-- sql/21, so what a compromised or misconfigured connector can do to this
-- database is bounded by what these procedures are willing to do.
--
-- The dashboard executes none of these. Its read path is sql/24, granted to a
-- different role, so a defect in the web tier cannot advance a checkpoint,
-- close a run, or sweep a corpus.
--
-- That boundary is not decoration. Two of these can destroy a corpus if they
-- are wrong, and both carry a guard that a direct UPDATE would not have:
--
--   uspGetPendingDeletes  refuses outright for an incremental run, because
--                         absence from a partial read means nothing, and
--                         refuses a sweep that would delete more than
--                         @MaxDeletePercent of the live corpus without an
--                         explicit override. A source whose query returns zero
--                         rows - a view dropped, a WHERE clause that silently
--                         matched nothing, a permission revoked - would
--                         otherwise present as "every item was deleted" and be
--                         faithfully carried out against the index.
--
--   uspPurgeHistory       will not purge a run that any live inventory row
--                         still points at, because LastSeenRunId is compared
--                         against run IDs and a dangling one makes the next
--                         sweep's arithmetic meaningless.
--
-- Run after sql/22, before sql/24 and sql/25.
--
-- CREATE OR ALTER throughout, for the reason sql/22 gives: a procedure is a
-- definition, and re-running this file is how a change to one is deployed.
-- ===========================================================================

USE [ConnectorState];
GO

/* ===========================================================================
   CONNECTION LIFECYCLE
   =========================================================================== */

/* ---------------------------------------------------------------------------
   uspRegisterConnection

   Called at the start of every run, before uspBeginRun. Idempotent by design:
   the connector does not know or care whether this connection has been seen
   before, and making it ask would be a round trip that answers nothing.

   ExpectedIntervalMinutes is passed every time rather than only on insert, so
   changing the schedule in the connector's configuration is enough to change
   what "late" means on the dashboard. A schedule recorded once at creation and
   never revisited is a schedule that is wrong within a quarter.
--------------------------------------------------------------------------- */

CREATE OR ALTER PROCEDURE [crawl].[uspRegisterConnection]
    @ConnectionId            NVARCHAR(64),
    @ConnectorKey            NVARCHAR(64),
    @DisplayName             NVARCHAR(256),
    @ExpectedIntervalMinutes INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    UPDATE  [crawl].[Connection]
    SET     ConnectorKey            = @ConnectorKey,
            DisplayName             = @DisplayName,
            ExpectedIntervalMinutes = @ExpectedIntervalMinutes,
            UpdatedUtc              = SYSUTCDATETIME()
    WHERE   ConnectionId = @ConnectionId;

    IF @@ROWCOUNT = 0
    BEGIN
        INSERT INTO [crawl].[Connection]
            (ConnectionId, ConnectorKey, DisplayName, ExpectedIntervalMinutes)
        VALUES
            (@ConnectionId, @ConnectorKey, @DisplayName, @ExpectedIntervalMinutes);
    END
END
GO

/* ---------------------------------------------------------------------------
   uspBeginRun

   Opens a run and, in the same call, answers the two questions the engine has
   to ask before it reads anything:

     * Is a full crawl due? An incremental read is only meaningful if a full one
       has established the baseline it is a delta against. Never run a full
       crawl, or run one so long ago that the checkpoint has outlived its
       usefulness, and this says so - which is feature 1's missed-run catch-up,
       reduced to the one decision a database can actually make.

     * Was the last run abandoned? A process killed between its first read and
       its close leaves a row in status 1 for ever. Those are reaped here rather
       than by a separate job, because the only moment anyone reliably cares is
       just before starting the next one.

   @FullEveryHours is the connector's policy, not this database's. Passing it in
   rather than storing it keeps the decision reviewable in appsettings beside
   everything else that shapes a run.

   Returns one row: RunId, the mode the run is ACTUALLY opened in, and the flags
   the engine acts on. The returned Mode may differ from the one requested -
   see the comment beside the INSERT, which is the whole reason this procedure
   decides rather than reports.
--------------------------------------------------------------------------- */

CREATE OR ALTER PROCEDURE [crawl].[uspBeginRun]
    @ConnectionId    NVARCHAR(64),
    @Mode            TINYINT,        -- 1 full, 2 incremental
    @HostName        NVARCHAR(128),
    @ProcessId       INT,
    @ToolVersion     NVARCHAR(64),
    @IsDryRun        BIT = 0,
    @FullEveryHours  INT = 168,      -- weekly, matching the runbook's default
    @AbandonAfterHours INT = 12
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @Mode NOT IN (1, 2)
        THROW 50001, 'Mode must be 1 (full) or 2 (incremental).', 1;

    IF NOT EXISTS (SELECT 1 FROM [crawl].[Connection] WHERE ConnectionId = @ConnectionId)
        THROW 50002, 'Unknown ConnectionId. Call crawl.uspRegisterConnection first.', 1;

    DECLARE @Reaped INT = 0;

    -- Reap first, so the count this run reports is the backlog it inherited
    -- rather than a number that includes itself.
    UPDATE  [crawl].[Run]
    SET     Status       = 4,
            CompletedUtc = SYSUTCDATETIME(),
            ErrorKind    = N'abandoned',
            ErrorMessage = N'No completion was recorded. The process is assumed to have died; '
                         + N'this row was closed by a later run.'
    WHERE   ConnectionId = @ConnectionId
      AND   Status = 1
      AND   StartedUtc < DATEADD(HOUR, -@AbandonAfterHours, SYSUTCDATETIME());

    SET @Reaped = @@ROWCOUNT;

    DECLARE @LastFullSuccessUtc DATETIME2(3) =
    (
        SELECT MAX(CompletedUtc)
        FROM   [crawl].[Run]
        WHERE  ConnectionId = @ConnectionId AND Mode = 1 AND Status = 2 AND IsDryRun = 0
    );

    DECLARE @HasCheckpoint BIT =
        CASE WHEN EXISTS (SELECT 1 FROM [crawl].[Checkpoint]
                          WHERE ConnectionId = @ConnectionId AND MarkerTime IS NOT NULL)
             THEN 1 ELSE 0 END;

    -- A full crawl is due when there has never been one, when the last one has
    -- aged out, or when there is no checkpoint for an incremental read to start
    -- from. The third case is the one that is easy to forget and expensive to
    -- get wrong: an incremental read with no marker reads from the beginning of
    -- time, which is a full crawl that has told the sweep it was not one.
    DECLARE @FullCrawlDue BIT =
        CASE WHEN @LastFullSuccessUtc IS NULL
               OR @HasCheckpoint = 0
               OR @LastFullSuccessUtc < DATEADD(HOUR, -@FullEveryHours, SYSUTCDATETIME())
             THEN 1 ELSE 0 END;

    -- THE ROW RECORDS THE MODE THE RUN WILL ACTUALLY READ IN, not the one that
    -- was asked for. This is load-bearing and was wrong in the first draft.
    --
    -- uspGetPendingDeletes reads Run.Mode, and LastFullSuccessUtc only advances
    -- for Mode = 1. Storing the requested mode while returning the escalated one
    -- produces a run that reads the entire source, is recorded as incremental,
    -- has its delete sweep refused with error 50006, and never advances the
    -- full-crawl baseline - so FullCrawlDue stays 1 for ever and NO sweep ever
    -- runs on that connection again. Every part of that failure is silent.
    DECLARE @ActualMode TINYINT = CASE WHEN @FullCrawlDue = 1 THEN 1 ELSE @Mode END;

    INSERT INTO [crawl].[Run]
        (ConnectionId, Mode, Status, HostName, ProcessId, ToolVersion, IsDryRun)
    VALUES
        (@ConnectionId, @ActualMode, 1, @HostName, @ProcessId, @ToolVersion, @IsDryRun);

    SELECT
        CAST(SCOPE_IDENTITY() AS BIGINT) AS RunId,
        @ActualMode                      AS Mode,
        @FullCrawlDue                    AS FullCrawlDue,
        @LastFullSuccessUtc              AS LastFullSuccessUtc,
        @HasCheckpoint                   AS HasCheckpoint,
        @Reaped                          AS AbandonedRunsReaped;
END
GO

/* ---------------------------------------------------------------------------
   uspCompleteRun / uspFailRun

   Exactly one of these closes a run, and the CK_Run_Completed constraint on the
   table means a row cannot leave status 1 without a CompletedUtc. Counters are
   written here in one statement rather than incremented per item: the engine
   already holds them in memory for its summary line, and a per-item UPDATE
   would put a second round trip beside every Graph write for numbers nobody
   reads until the run ends.

   uspFailRun deliberately still records the counters. A run that died after
   nine hundred of a thousand items wrote nine hundred items, and a failure row
   with zeroes in it invites the reader to conclude nothing happened.
--------------------------------------------------------------------------- */

CREATE OR ALTER PROCEDURE [crawl].[uspCompleteRun]
    @RunId          BIGINT,
    @ItemsRead      INT,
    @ItemsWritten   INT,
    @ItemsUnchanged INT,
    @ItemsDeleted   INT,
    @ItemsSkipped   INT,
    @ItemsDuplicate INT,
    @ThrottleWaits  INT,
    @BatchesSent    INT,
    @BytesWritten   BIGINT,

    -- A SUCCESSFUL run can still have failed items, and the first draft had
    -- nowhere to put them. On the batching path one refused item does not stop
    -- the other nineteen, so a run completes having written 1,117 of 1,118 -
    -- which is a success with a number attached, not a failure, and a row
    -- reading zero here would hide the one item that never made it.
    @ItemsFailed    INT = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- 2 SUCCEEDED, 5 PARTIAL. A run that finished with items refused is not a
    -- success, and the word matters more than it looks: succeeded is the one
    -- value that stops anybody looking, and a refused write records no hash, so
    -- the corpus does not look wrong afterwards either. The first run to hit
    -- this refused 191 items and was stored as a success.
    --
    -- Not 3. Failed means the run died - no totals, an ErrorKind, a crawl to
    -- repeat in full. A partial run completed, kept every hash it earned, and
    -- needs only its refused items retried, which the next run does by itself.
    -- Collapsing the two would send somebody to repeat a crawl that does not
    -- need repeating.
    --
    -- Status 5 needs sql/29, which widens CK_Run_Status. Without it this
    -- UPDATE throws on the first partial run rather than silently storing a
    -- success, which is the right way round for a missing migration.
    UPDATE  [crawl].[Run]
    SET     Status         = CASE WHEN @ItemsFailed > 0 THEN 5 ELSE 2 END,
            CompletedUtc   = SYSUTCDATETIME(),
            ItemsRead      = @ItemsRead,
            ItemsWritten   = @ItemsWritten,
            ItemsUnchanged = @ItemsUnchanged,
            ItemsDeleted   = @ItemsDeleted,
            ItemsSkipped   = @ItemsSkipped,
            ItemsDuplicate = @ItemsDuplicate,
            ItemsFailed    = @ItemsFailed,
            ThrottleWaits  = @ThrottleWaits,
            BatchesSent    = @BatchesSent,
            BytesWritten   = @BytesWritten
    WHERE   RunId = @RunId AND Status = 1;

    IF @@ROWCOUNT = 0
        THROW 50003, 'Run is not open. It was already closed, reaped as abandoned, or does not exist.', 1;
END
GO

CREATE OR ALTER PROCEDURE [crawl].[uspFailRun]
    @RunId          BIGINT,
    @ErrorKind      NVARCHAR(64),
    @ErrorMessage   NVARCHAR(2000),
    @ItemsRead      INT = 0,
    @ItemsWritten   INT = 0,
    @ItemsUnchanged INT = 0,
    @ItemsDeleted   INT = 0,
    @ItemsSkipped   INT = 0,
    @ItemsDuplicate INT = 0,
    @ItemsFailed    INT = 0,
    @ThrottleWaits  INT = 0,
    @BatchesSent    INT = 0,
    @BytesWritten   BIGINT = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    UPDATE  [crawl].[Run]
    SET     Status         = 3,
            CompletedUtc   = SYSUTCDATETIME(),
            ErrorKind      = @ErrorKind,
            ErrorMessage   = @ErrorMessage,
            ItemsRead      = @ItemsRead,
            ItemsWritten   = @ItemsWritten,
            ItemsUnchanged = @ItemsUnchanged,
            ItemsDeleted   = @ItemsDeleted,
            ItemsSkipped   = @ItemsSkipped,
            ItemsDuplicate = @ItemsDuplicate,
            ItemsFailed    = @ItemsFailed,
            ThrottleWaits  = @ThrottleWaits,
            BatchesSent    = @BatchesSent,
            BytesWritten   = @BytesWritten
    WHERE   RunId = @RunId AND Status = 1;

    IF @@ROWCOUNT = 0
        THROW 50003, 'Run is not open. It was already closed, reaped as abandoned, or does not exist.', 1;
END
GO

/* ===========================================================================
   THE INVENTORY - features 2, 3, 4 and 10
   =========================================================================== */

/* ---------------------------------------------------------------------------
   uspGetItemState

   Given the IDs the engine is about to consider, return what is on record for
   them. The engine compares the hashes itself rather than asking the database
   to decide, for two reasons: the comparison is a byte compare it can do
   without a round trip once it has the row, and keeping the decision in one
   place - PushEngine - means the rule that governs a write lives beside the
   write.

   Items with no row come back absent rather than as nulls. Absent means new,
   and new means write, which is the correct default for anything this store
   has never seen.

   ONLY LIVE ITEMS ARE RETURNED, and that filter is the difference between a
   resurrection working and an item disappearing for good. A tombstoned item
   whose source record came back has hashes that still match, so returning it
   would have the engine conclude "unchanged", skip the write, and leave the
   item out of the index permanently. A pending-delete item is worse: it would
   also be skipped, and uspRecordUnchanged only touches State = 1, so its
   LastSeenRunId would stay stale and the next sweep would delete it again.
   Filtered out, both come back as absent, which means new, which means write -
   and uspRecordWritten sets State back to 1.
--------------------------------------------------------------------------- */

CREATE OR ALTER PROCEDURE [crawl].[uspGetItemState]
    @ConnectionId NVARCHAR(64),
    @Items        [crawl].[ItemIdList] READONLY
AS
BEGIN
    SET NOCOUNT ON;

    SELECT  i.ItemId,
            i.ItemType,
            i.ContentHash,
            i.AclHash,
            i.ContentBytes,
            i.State,
            i.LastWrittenRunId,
            i.UnchangedStreak
    FROM    [crawl].[Item] AS i
    INNER JOIN @Items      AS q ON q.ItemId = i.ItemId
    WHERE   i.ConnectionId = @ConnectionId
      AND   i.State = 1;
END
GO

/* ---------------------------------------------------------------------------
   uspRecordWritten

   Called after Graph has confirmed the writes, never before. The ordering is
   the same rule IPushSource.OnItemCommittedAsync enforces upstream and matters
   for the same reason: a hash recorded before the write means the next run sees
   an item as unchanged and skips it, so a single failure between the two turns
   into an item that is permanently stale and permanently invisible.

   Sets LastSeenRunId and LastWrittenRunId together and resets UnchangedStreak,
   because an item that was written is by definition not unchanged.
--------------------------------------------------------------------------- */

CREATE OR ALTER PROCEDURE [crawl].[uspRecordWritten]
    @ConnectionId NVARCHAR(64),
    @RunId        BIGINT,
    @Items        [crawl].[ItemStateList] READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(3) = SYSUTCDATETIME();

    BEGIN TRANSACTION;

    UPDATE  i
    SET     i.ItemType         = s.ItemType,
            i.ContentHash      = s.ContentHash,
            i.AclHash          = s.AclHash,
            i.ContentBytes     = s.ContentBytes,
            i.LastSeenRunId    = @RunId,
            i.LastWrittenRunId = @RunId,
            i.LastWrittenUtc   = @Now,
            i.UnchangedStreak  = 0,
            i.PendingSinceUtc  = NULL,
            i.DeletedUtc       = NULL,
            -- An item that was written is live again whatever it was before.
            -- This is what makes a resurrected item - deleted from the source,
            -- then restored - come back cleanly rather than staying tombstoned.
            i.State            = 1
    FROM    [crawl].[Item] AS i
    INNER JOIN @Items      AS s ON s.ItemId = i.ItemId
    WHERE   i.ConnectionId = @ConnectionId;

    INSERT INTO [crawl].[Item]
        (ConnectionId, ItemId, ItemType, ContentHash, AclHash, ContentBytes,
         FirstSeenRunId, LastSeenRunId, LastWrittenRunId, LastWrittenUtc, State, UnchangedStreak)
    SELECT
        @ConnectionId, s.ItemId, s.ItemType, s.ContentHash, s.AclHash, s.ContentBytes,
        @RunId, @RunId, @RunId, @Now, 1, 0
    FROM    @Items AS s
    WHERE   NOT EXISTS
    (
        SELECT 1 FROM [crawl].[Item] AS i
        WHERE i.ConnectionId = @ConnectionId AND i.ItemId = s.ItemId
    );

    COMMIT TRANSACTION;
END
GO

/* ---------------------------------------------------------------------------
   uspRecordUnchanged

   The other half of feature 3, and the one that makes feature 2 safe.

   An item whose hashes matched is NOT written to Graph - that is the whole
   point - but it must still be marked seen, or the delete sweep will conclude
   the source stopped returning it and remove it from the index. Skipping the
   write and skipping the mark are one line apart in the engine and produce
   opposite outcomes: the first is the optimisation, the second silently empties
   the corpus one run at a time.

   UnchangedStreak is incremented rather than set, which is what makes the
   argument for incremental reads measurable: an inventory where the median
   streak is thirty is an inventory being re-read thirty times for nothing.
--------------------------------------------------------------------------- */

CREATE OR ALTER PROCEDURE [crawl].[uspRecordUnchanged]
    @ConnectionId NVARCHAR(64),
    @RunId        BIGINT,
    @Items        [crawl].[ItemIdList] READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    UPDATE  i
    SET     i.LastSeenRunId   = @RunId,
            i.UnchangedStreak = i.UnchangedStreak + 1
    FROM    [crawl].[Item] AS i
    INNER JOIN @Items      AS q ON q.ItemId = i.ItemId
    WHERE   i.ConnectionId = @ConnectionId
      AND   i.State = 1;
END
GO

/* ---------------------------------------------------------------------------
   uspGetPendingDeletes

   Feature 2, and the most dangerous procedure in this file.

   It is called only after a FULL crawl has enumerated to the end without
   throwing. Every word of that sentence is a precondition:

     full         - an incremental run reads a slice, so absence from it carries
                    no information at all. Passing an incremental RunId here is
                    refused rather than interpreted.

     enumerated
     to the end   - a run that died halfway has "not seen" every item after the
                    failure point. NOTHING IN THIS PROCEDURE CAN CHECK THAT.
                    The run is still open when the sweep runs, so its status says
                    only "running"; the guarantee comes entirely from the engine
                    calling this on the success path and nowhere else. The
                    percentage guard below is what stands between that convention
                    and a corpus, which is why it is not optional.

   THE PERCENTAGE GUARD. Even a correct full run can be catastrophically wrong
   about what exists: a view dropped, a WHERE clause that matched nothing, a
   permission quietly revoked, a source database restored to last month. Each of
   those presents identically - a run that read fewer rows than it should have
   and completed cleanly - and each would sweep the difference out of the index.
   So a sweep that would remove more than @MaxDeletePercent of the live corpus
   returns nothing and reports why. Clearing it is a deliberate act with a
   number in it, not a retry.

   Ten percent is the default because a real day's deletions in a ticketing or
   engagement corpus are a fraction of a percent, and because the guard is only
   useful if it fires before the damage rather than after.
--------------------------------------------------------------------------- */

CREATE OR ALTER PROCEDURE [crawl].[uspGetPendingDeletes]
    @ConnectionId      NVARCHAR(64),
    @RunId             BIGINT,
    @MaxDeletePercent  DECIMAL(5, 2) = 10.00,
    @OverrideGuard     BIT = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Mode TINYINT, @RunConnection NVARCHAR(64), @IsDryRun BIT;

    SELECT  @Mode = Mode, @RunConnection = ConnectionId, @IsDryRun = IsDryRun
    FROM    [crawl].[Run]
    WHERE   RunId = @RunId;

    IF @RunConnection IS NULL
        THROW 50004, 'Unknown RunId.', 1;

    IF @RunConnection <> @ConnectionId
        THROW 50005, 'RunId belongs to a different connection. Refusing to sweep.', 1;

    -- THROW's message must be a literal or a variable and REJECTS an expression,
    -- so the two-line string this used to concatenate made the whole CREATE OR
    -- ALTER batch a parse error - which would have left the procedure absent and
    -- then failed sql/25's GRANT on it. Assigned first, thrown second.
    IF @Mode <> 1
    BEGIN
        DECLARE @NotFull NVARCHAR(400) =
            N'Delete detection is only valid after a full crawl. An incremental run reads a subset, '
          + N'so absence from it means nothing.';

        THROW 50006, @NotFull, 1;
    END

    -- A dry run must not move an item to pending delete: nothing was written,
    -- so nothing may be concluded about what the source no longer returns.
    IF @IsDryRun = 1
    BEGIN
        SELECT TOP (0) CAST(NULL AS NVARCHAR(128)) AS ItemId, CAST(NULL AS NVARCHAR(64)) AS ItemType;
        RETURN;
    END

    DECLARE @LiveCount INT =
        (SELECT COUNT(*) FROM [crawl].[Item] WHERE ConnectionId = @ConnectionId AND State = 1);

    DECLARE @MissingCount INT =
        (SELECT COUNT(*) FROM [crawl].[Item]
         WHERE ConnectionId = @ConnectionId AND State = 1 AND LastSeenRunId < @RunId);

    DECLARE @MissingPercent DECIMAL(5, 2) =
        CASE WHEN @LiveCount = 0 THEN 0
             ELSE CAST(100.0 * @MissingCount / @LiveCount AS DECIMAL(5, 2)) END;

    IF @OverrideGuard = 0 AND @MissingPercent > @MaxDeletePercent
    BEGIN
        -- THE PERCENT SIGNS ARE DOUBLED, AND THEY HAVE TO BE. An error message
        -- is a format string, so a lone % is read as a specifier: it is
        -- swallowed, and a sequence like '%)' destroys the rest of the message.
        -- This message is almost nothing but percentages, so undoubled it
        -- arrives EMPTY - the exception raises, the guard holds, and the
        -- operator is told only that error 50007 happened.
        --
        -- That is the worst message in this file to lose. Every other THROW
        -- here is a literal with no % in it; this is the only one that needs
        -- the doubling, and it is the one refusal whose whole value is the two
        -- numbers it carries. It was found by tripping the guard deliberately
        -- and reading what came back, which is the only way it could be found:
        -- the SQL is valid, the exception is correct, and the text is empty.
        DECLARE @Message NVARCHAR(2000) =
            CONCAT(N'Delete sweep refused. It would remove ', @MissingCount, N' of ', @LiveCount,
                   N' live items (', @MissingPercent, N'%%), above the ', @MaxDeletePercent,
                   N'%% guard. This is far more likely to be a source that returned too few rows - ',
                   N'a dropped view, a revoked permission, a filter that matched nothing - than a ',
                   N'real deletion of that size. Verify the source count, then re-run with the ',
                   N'guard raised deliberately.');

        THROW 50007, @Message, 1;
    END

    BEGIN TRANSACTION;

    -- PendingSinceUtc is stamped here and only here. Without it the age of a
    -- pending delete would have to be derived from LastWrittenUtc, which is when
    -- the item was last WRITTEN - so on a corpus of long-unchanged items every
    -- freshly pending row would read as weeks old and the "older than one crawl
    -- interval" alert would fire on every single sweep.
    UPDATE  [crawl].[Item]
    SET     State = 2,
            PendingSinceUtc = SYSUTCDATETIME()
    WHERE   ConnectionId = @ConnectionId
      AND   State = 1
      AND   LastSeenRunId < @RunId;

    COMMIT TRANSACTION;

    -- Everything awaiting removal, including anything a previous run left
    -- behind. A DELETE that Graph refused last night is retried tonight rather
    -- than waiting for the item to be missed a second time.
    SELECT  ItemId, ItemType
    FROM    [crawl].[Item]
    WHERE   ConnectionId = @ConnectionId AND State = 2
    ORDER BY ItemId;
END
GO

/* ---------------------------------------------------------------------------
   uspConfirmDeletes

   Called with the IDs Graph confirmed removed - including the 404s, because an
   item Graph says is not there is an item that is not there, and treating that
   as a failure would keep it in the pending list for ever.

   Items are tombstoned rather than deleted from the table. A tombstone is what
   lets a resurrected item be recognised as one, and what stops a corpus that
   churns from looking, in the run history, like a corpus that is shrinking.
   uspPurgeHistory is what eventually removes them.
--------------------------------------------------------------------------- */

CREATE OR ALTER PROCEDURE [crawl].[uspConfirmDeletes]
    @ConnectionId NVARCHAR(64),
    @RunId        BIGINT,
    @Items        [crawl].[ItemIdList] READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    UPDATE  i
    SET     i.State            = 3,
            i.LastWrittenRunId = @RunId,
            i.LastWrittenUtc   = SYSUTCDATETIME(),
            i.PendingSinceUtc  = NULL,
            i.DeletedUtc       = SYSUTCDATETIME()
    FROM    [crawl].[Item] AS i
    INNER JOIN @Items      AS q ON q.ItemId = i.ItemId
    WHERE   i.ConnectionId = @ConnectionId
      AND   i.State = 2;

    SELECT @@ROWCOUNT AS Confirmed;
END
GO

/* ===========================================================================
   CHECKPOINTING - feature 9
   =========================================================================== */

CREATE OR ALTER PROCEDURE [crawl].[uspGetCheckpoint]
    @ConnectionId NVARCHAR(64)
AS
BEGIN
    SET NOCOUNT ON;

    SELECT  MarkerTime, MarkerKey, RunId, RunCount, UpdatedUtc
    FROM    [crawl].[Checkpoint]
    WHERE   ConnectionId = @ConnectionId;
END
GO

/* ---------------------------------------------------------------------------
   uspSaveCheckpoint

   The marker only ever moves forward. The comparison is on the composite pair,
   in the same order the source reads in, because that is what makes the
   ordering total - two rows sharing a modification timestamp to the millisecond
   are separated by the key, and a marker of the timestamp alone either re-reads
   that whole group for ever or loses whichever of them had not been written.

   Refusing to move it backwards is not paranoia about the caller. It is what
   makes two runs overlapping - an operator running the tool by hand while the
   scheduled one is still going - lose nothing instead of resetting the slower
   one's progress.
--------------------------------------------------------------------------- */

CREATE OR ALTER PROCEDURE [crawl].[uspSaveCheckpoint]
    @ConnectionId NVARCHAR(64),
    @RunId        BIGINT,
    @MarkerTime   DATETIME2(3),
    @MarkerKey    NVARCHAR(256)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    UPDATE  [crawl].[Checkpoint]
    SET     MarkerTime = @MarkerTime,
            MarkerKey  = @MarkerKey,
            RunId      = @RunId,
            RunCount   = RunCount + 1,
            UpdatedUtc = SYSUTCDATETIME()
    WHERE   ConnectionId = @ConnectionId
      AND   (MarkerTime IS NULL
             OR @MarkerTime > MarkerTime
             OR (@MarkerTime = MarkerTime AND @MarkerKey > MarkerKey));

    IF @@ROWCOUNT = 0 AND NOT EXISTS (SELECT 1 FROM [crawl].[Checkpoint] WHERE ConnectionId = @ConnectionId)
    BEGIN
        INSERT INTO [crawl].[Checkpoint] (ConnectionId, MarkerTime, MarkerKey, RunId, RunCount)
        VALUES (@ConnectionId, @MarkerTime, @MarkerKey, @RunId, 1);
    END

    COMMIT TRANSACTION;

    SELECT  MarkerTime, MarkerKey, RunCount
    FROM    [crawl].[Checkpoint]
    WHERE   ConnectionId = @ConnectionId;
END
GO

/* ---------------------------------------------------------------------------
   uspResetCheckpoint

   Forces the next run to read from the beginning. Separate from
   uspSaveCheckpoint precisely because that one refuses to move backwards: the
   only way to rewind is to say so explicitly, in a call whose name appears in
   an audit log and cannot be reached by passing an odd value to the routine
   path.
--------------------------------------------------------------------------- */

CREATE OR ALTER PROCEDURE [crawl].[uspResetCheckpoint]
    @ConnectionId NVARCHAR(64),
    @Reason       NVARCHAR(256)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    UPDATE  [crawl].[Checkpoint]
    SET     MarkerTime = NULL,
            MarkerKey  = NULL,
            UpdatedUtc = SYSUTCDATETIME()
    WHERE   ConnectionId = @ConnectionId;

    SELECT @@ROWCOUNT AS Reset, @Reason AS Reason;
END
GO

/* ===========================================================================
   IDENTITY MAPPING - feature 6
   =========================================================================== */

/* ---------------------------------------------------------------------------
   uspResolvePrincipals

   Returns the cached answers for a set of source principals, unexpired ones
   only. Anything absent from the result is a cache miss the caller resolves
   against the directory and writes back with uspCachePrincipal.

   Batched rather than one call per principal because an ACL-per-item source
   asks about the same twenty groups for every one of a hundred thousand items,
   and the round trip is the cost, not the lookup.

   Takes PrincipalKeyList rather than ItemIdList, and the two are NOT
   interchangeable even though both are one string column. An item ID is capped
   at 128 characters by Graph; a source principal is not, and an Active
   Directory distinguished name routinely runs past it. Through the narrower
   type a long principal could be cached at full length and never looked up
   again - or, worse, truncated into a match against a different principal's
   row, stamping an item with the wrong group.
--------------------------------------------------------------------------- */

CREATE OR ALTER PROCEDURE [crawl].[uspResolvePrincipals]
    @ConnectionId NVARCHAR(64),
    @SourceType   NVARCHAR(32),
    @Principals   [crawl].[PrincipalKeyList] READONLY
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE  m
    SET     m.HitCount = m.HitCount + 1
    FROM    [crawl].[PrincipalMap] AS m
    INNER JOIN @Principals         AS q ON q.SourceKey = m.SourceKey
    WHERE   m.ConnectionId = @ConnectionId
      AND   m.SourceType   = @SourceType
      AND   m.ExpiresUtc   > SYSUTCDATETIME();

    SELECT  m.SourceKey,
            m.EntraObjectId,
            m.EntraType,
            m.ResolvedUtc,
            m.ExpiresUtc
    FROM    [crawl].[PrincipalMap] AS m
    INNER JOIN @Principals         AS q ON q.SourceKey = m.SourceKey
    WHERE   m.ConnectionId = @ConnectionId
      AND   m.SourceType   = @SourceType
      AND   m.ExpiresUtc   > SYSUTCDATETIME();
END
GO

/* ---------------------------------------------------------------------------
   uspCachePrincipal

   A null @EntraObjectId is a negative entry and is deliberately storable: it
   records that this principal resolved to nothing, which is the answer that
   otherwise gets re-asked on every item of every run for ever.

   The caller passes ONE @TtlMinutes and is expected to pass a shorter one for a
   negative entry than a positive one. The database does not enforce that split
   and cannot: it has no way to know which answers were expensive to obtain. The
   asymmetry it rests on is real - a stale positive entry stamps an ACL with a
   group that no longer means what it did, while a stale negative one only costs
   a lookup that would have failed anyway - but it lives in the resolver, not
   here. sql/21's comment describing "both TTLs" describes the caller's policy.
--------------------------------------------------------------------------- */

CREATE OR ALTER PROCEDURE [crawl].[uspCachePrincipal]
    @ConnectionId    NVARCHAR(64),
    @SourceType      NVARCHAR(32),
    @SourceKey       NVARCHAR(256),
    @EntraObjectId   UNIQUEIDENTIFIER = NULL,
    @EntraType       NVARCHAR(16)     = NULL,
    @TtlMinutes      INT              = 720
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(3) = SYSUTCDATETIME();
    DECLARE @Expires DATETIME2(3) = DATEADD(MINUTE, @TtlMinutes, @Now);

    UPDATE  [crawl].[PrincipalMap]
    SET     EntraObjectId = @EntraObjectId,
            EntraType     = @EntraType,
            ResolvedUtc   = @Now,
            ExpiresUtc    = @Expires
    WHERE   ConnectionId = @ConnectionId AND SourceType = @SourceType AND SourceKey = @SourceKey;

    IF @@ROWCOUNT = 0
    BEGIN
        INSERT INTO [crawl].[PrincipalMap]
            (ConnectionId, SourceType, SourceKey, EntraObjectId, EntraType, ResolvedUtc, ExpiresUtc)
        VALUES
            (@ConnectionId, @SourceType, @SourceKey, @EntraObjectId, @EntraType, @Now, @Expires);
    END
END
GO

/* ===========================================================================
   OBSERVABILITY - features 5 and 7
   =========================================================================== */

/* ---------------------------------------------------------------------------
   uspRecordThrottles

   The run's buffered refusals, flushed in one call when it closes.

   OccurredUtc is passed in rather than defaulted, and that matters more than it
   looks. The events are buffered in the connector for the whole run - writing
   each one when it happened would put a database round trip inside the write
   loop's catch block, on precisely the run that is already struggling. Letting
   the column default at flush time would then stamp every event in the run with
   the same instant, which destroys the only thing the raw events are kept for:
   whether the throttling was clustered in one bad minute or spread evenly
   across the hour. Those two argue for opposite changes.
--------------------------------------------------------------------------- */

CREATE OR ALTER PROCEDURE [crawl].[uspRecordThrottles]
    @RunId  BIGINT,
    @Events [crawl].[ThrottleEventList] READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    INSERT INTO [crawl].[ThrottleEvent]
        (RunId, OccurredUtc, StatusCode, RetryAfterSeconds, Endpoint, AttemptNumber)
    SELECT
        @RunId, OccurredUtc, StatusCode, RetryAfterSeconds, Endpoint, AttemptNumber
    FROM @Events;
END
GO

/* ---------------------------------------------------------------------------
   uspRecordThrottle

   The single-event form, kept for a caller that has one to record and no buffer
   to flush - the schema-registration poll, which throttles before any run-level
   buffering exists.
--------------------------------------------------------------------------- */

CREATE OR ALTER PROCEDURE [crawl].[uspRecordThrottle]
    @RunId             BIGINT,
    @StatusCode        INT,
    @RetryAfterSeconds INT = NULL,
    @Endpoint          NVARCHAR(32) = N'item',
    @AttemptNumber     INT = 1,
    @OccurredUtc       DATETIME2(3) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    INSERT INTO [crawl].[ThrottleEvent]
        (RunId, OccurredUtc, StatusCode, RetryAfterSeconds, Endpoint, AttemptNumber)
    VALUES
        (@RunId, ISNULL(@OccurredUtc, SYSUTCDATETIME()), @StatusCode,
         @RetryAfterSeconds, @Endpoint, @AttemptNumber);
END
GO

/* ---------------------------------------------------------------------------
   uspSaveRunTiming

   PushTiming's whole table, in one call at the end of the run. Written once
   rather than per phase because the seven rows are one fact about one run, and
   a partial timing table is more misleading than none.
--------------------------------------------------------------------------- */

CREATE OR ALTER PROCEDURE [crawl].[uspSaveRunTiming]
    @RunId   BIGINT,
    @Phases  [crawl].[PhaseTimingList] READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    DELETE FROM [crawl].[RunPhaseTiming] WHERE RunId = @RunId;

    INSERT INTO [crawl].[RunPhaseTiming]
        (RunId, Phase, Unit, SampleCount, TotalMicroseconds,
         P50Microseconds, P95Microseconds, P99Microseconds, MaxMicroseconds)
    SELECT
        @RunId, Phase, Unit, SampleCount, TotalMicroseconds,
        P50Microseconds, P95Microseconds, P99Microseconds, MaxMicroseconds
    FROM @Phases;

    COMMIT TRANSACTION;
END
GO

/* ---------------------------------------------------------------------------
   uspPurgeHistory

   Retention, run from a scheduled job rather than by the connector.

   The guard is the one that stops a routine cleanup from breaking the delete
   sweep: a run row may not be removed while any LIVE inventory row still points
   at it through LastSeenRunId, because the sweep compares run IDs and a
   dangling reference makes that comparison meaningless in a way nothing
   reports. In practice that means the most recent successful full run and
   anything after it survive any retention setting, which is correct.

   Tombstones are purged on their own, longer, clock: an item deleted and
   re-created inside the tombstone window is recognised as a resurrection, and
   outside it is treated as new. Both are correct; only the first is free.
--------------------------------------------------------------------------- */

CREATE OR ALTER PROCEDURE [crawl].[uspPurgeHistory]
    @ConnectionId               NVARCHAR(64),
    @KeepRunDays                INT = 90,
    @KeepTombstoneDays          INT = 180,

    -- How long an EXPIRED principal-cache entry is kept past its expiry. A
    -- parameter rather than the hard-coded 30 days it started as, because the
    -- other two retention windows are parameters and one that is not is the one
    -- somebody edits the procedure to change.
    @KeepExpiredPrincipalDays   INT = 30
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @RunCutoff       DATETIME2(3) = DATEADD(DAY, -@KeepRunDays, SYSUTCDATETIME());
    DECLARE @TombstoneCutoff DATETIME2(3) = DATEADD(DAY, -@KeepTombstoneDays, SYSUTCDATETIME());

    BEGIN TRANSACTION;

    DECLARE @Purgeable TABLE (RunId BIGINT PRIMARY KEY);

    INSERT INTO @Purgeable (RunId)
    SELECT  r.RunId
    FROM    [crawl].[Run] AS r
    WHERE   r.ConnectionId = @ConnectionId
      AND   r.Status <> 1
      AND   r.StartedUtc < @RunCutoff
      AND   NOT EXISTS
      (
          SELECT 1
          FROM   [crawl].[Item] AS i
          WHERE  i.ConnectionId = @ConnectionId
            AND  i.State = 1
            AND  (i.LastSeenRunId = r.RunId OR i.LastWrittenRunId = r.RunId OR i.FirstSeenRunId = r.RunId)
      )
      AND   NOT EXISTS
      (
          SELECT 1 FROM [crawl].[Checkpoint] AS c
          WHERE c.ConnectionId = @ConnectionId AND c.RunId = r.RunId
      );

    -- Every child before the parent. RunItemType is the one that is easy to
    -- forget and impossible to miss at runtime: FK_RunItemType_Run has no
    -- cascade, so omitting it throws 547 and - with XACT_ABORT ON - rolls the
    -- whole purge back, meaning retention silently never runs.
    DELETE t FROM [crawl].[ThrottleEvent]  AS t INNER JOIN @Purgeable AS p ON p.RunId = t.RunId;
    DELETE g FROM [crawl].[RunPhaseTiming] AS g INNER JOIN @Purgeable AS p ON p.RunId = g.RunId;
    DELETE k FROM [crawl].[RunItemType]    AS k INNER JOIN @Purgeable AS p ON p.RunId = k.RunId;
    DELETE r FROM [crawl].[Run]            AS r INNER JOIN @Purgeable AS p ON p.RunId = r.RunId;

    DECLARE @RunsPurged INT = @@ROWCOUNT;

    -- Aged on DeletedUtc, not LastWrittenUtc. The second is when the item was
    -- last WRITTEN, so an item unchanged for a year was already past any
    -- reasonable window the moment it became a tombstone - purged before anyone
    -- could see it had been deleted at all.
    DELETE  FROM [crawl].[Item]
    WHERE   ConnectionId = @ConnectionId
      AND   State = 3
      AND   DeletedUtc < @TombstoneCutoff;

    DECLARE @TombstonesPurged INT = @@ROWCOUNT;

    DELETE  FROM [crawl].[PrincipalMap]
    WHERE   ConnectionId = @ConnectionId
      AND   ExpiresUtc < DATEADD(DAY, -@KeepExpiredPrincipalDays, SYSUTCDATETIME());

    DECLARE @PrincipalsPurged INT = @@ROWCOUNT;

    COMMIT TRANSACTION;

    SELECT @RunsPurged AS RunsPurged, @TombstonesPurged AS TombstonesPurged,
           @PrincipalsPurged AS PrincipalsPurged;
END
GO

/* ---------------------------------------------------------------------------
   uspRecordRunItemTypes

   The per-kind breakdown of one run, written once at the end from
   PushSummary.ByType. Feature 5's drill-down grain.

   Called before uspCompleteRun, so that a dashboard reading a completed run
   always finds the breakdown present. The other order would leave a window -
   short, but exactly the window a monitoring poll lands in - where a run reads
   as finished and its detail page is empty.
--------------------------------------------------------------------------- */

CREATE OR ALTER PROCEDURE [crawl].[uspRecordRunItemTypes]
    @RunId  BIGINT,
    @Counts [crawl].[ItemTypeCountList] READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    DELETE FROM [crawl].[RunItemType] WHERE RunId = @RunId;

    INSERT INTO [crawl].[RunItemType]
        (RunId, ItemType, ItemsWritten, ItemsUnchanged, ItemsDeleted, ItemsSkipped, ItemsFailed, BytesWritten)
    SELECT
        @RunId, ItemType, ItemsWritten, ItemsUnchanged, ItemsDeleted, ItemsSkipped, ItemsFailed, BytesWritten
    FROM @Counts;

    COMMIT TRANSACTION;
END
GO

-- Verification: the nineteen procedures this file defines - seventeen granted
-- to crawl_writer in sql/25, plus uspResetCheckpoint and uspPurgeHistory, which
-- are deliberately not.
SELECT  s.name AS schema_name, p.name AS procedure_name, p.modify_date
FROM    sys.procedures AS p
JOIN    sys.schemas    AS s ON s.schema_id = p.schema_id
WHERE   s.name = N'crawl'
ORDER BY p.name;
GO
