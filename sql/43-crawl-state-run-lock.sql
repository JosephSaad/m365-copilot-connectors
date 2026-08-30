-- ===========================================================================
-- 43-crawl-state-run-lock.sql
--
-- One connection, one live crawl. Enforced by the database, on a heartbeat.
--
-- WHAT IS UNPROTECTED TODAY. Nothing stops two processes crawling the same
-- connection at once. The store reaps runs that were ABANDONED - a row left at
-- status 1 for longer than @AbandonAfterHours - but it has never refused a
-- second run that starts while a first is genuinely still going. Two ways that
-- happens in practice, and neither is exotic: a scheduled task that overruns its
-- interval and fires again, and two hosts in an active/passive pair that both
-- reach their scheduled time.
--
-- The consequence is not a duplicate crawl. It is two DELETE SWEEPS. Each run
-- diffs the corpus against what IT has seen, so the second sweep sees every item
-- the first has not reached yet as unseen, and offers them for deletion. The
-- MaxDeletePercent guard is the only thing standing between that and an emptied
-- index, and a guard is a backstop rather than a design.
--
-- WHY A HEARTBEAT AND NOT A LOCK. sp_getapplock is the obvious tool and does not
-- fit: SqlCrawlStateStore opens a connection per call, so a session-scoped lock
-- would be released the moment uspBeginRun returned. A transaction-scoped one
-- would have to span the whole crawl, holding a transaction open for an hour.
--
-- WHY A HEARTBEAT AND NOT JUST "REFUSE IF STATUS = 1". Because that turns a
-- crash into an outage. If the host dies mid-crawl the row stays at status 1,
-- and a bare refusal locks the connection out until @AbandonAfterHours elapses -
-- twelve hours by default, during which deletions and ACL revocations stop
-- propagating. That is the failure this whole roadmap section exists to avoid,
-- reintroduced by the fix for it.
--
-- So: the running process stamps HeartbeatUtc. A run whose heartbeat is fresh
-- holds the lease and a second run is refused. A run whose heartbeat has gone
-- stale is dead, is reaped on the spot, and the lease is free. Recovery time
-- becomes the grace period - minutes - instead of twelve hours, and the twelve
-- hour reaper stays as the backstop for a process that died before its first
-- heartbeat.
--
-- Run against ConnectorState after sql/20-25. Idempotent. Verification at the
-- foot, including a live acquire/refuse/expire sequence.
-- ===========================================================================

USE [ConnectorState];
GO

-- ---------------------------------------------------------------------------
-- SET OPTIONS ARE STORED WITH THE MODULE, NOT SUPPLIED BY THE CALLER. sqlcmd
-- connects with QUOTED_IDENTIFIER OFF and SSMS with it ON; crawl.Item carries a
-- filtered index, and a module created with the option off is refused at
-- EXECUTION against it. uspBeginRun writes to crawl.Run and reaps rows, so this
-- is load-bearing rather than ceremonial. sql/30 checks the result.
-- ---------------------------------------------------------------------------
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
GO

-- ---------------------------------------------------------------------------
-- 1. The heartbeat column.
--
-- NULL means "this run has not reported since it started", which is different
-- from "it started long ago" and has to stay distinguishable: a run that has
-- never beaten falls back to StartedUtc for its liveness, so a process that dies
-- during its first seconds is still reaped by the grace period rather than
-- holding the lease until the twelve-hour reaper notices.
-- ---------------------------------------------------------------------------

IF COL_LENGTH(N'crawl.Run', N'HeartbeatUtc') IS NULL
BEGIN
    ALTER TABLE [crawl].[Run] ADD HeartbeatUtc DATETIME2(3) NULL;
    PRINT 'crawl.Run.HeartbeatUtc added.';
END
ELSE
BEGIN
    PRINT 'crawl.Run.HeartbeatUtc already present.';
END
GO

-- ---------------------------------------------------------------------------
-- 2. The heartbeat itself.
--
-- Deliberately trivial and deliberately not transactional. It is called on a
-- timer during a crawl, it must never block the crawl, and a missed beat is
-- survivable - the grace period is several beats wide precisely so that one
-- slow round trip does not hand the lease away mid-run.
--
-- It touches only its own row and only while that row is running, so a beat
-- arriving after the run has closed is a no-op rather than a resurrection.
-- ---------------------------------------------------------------------------

CREATE OR ALTER PROCEDURE [crawl].[uspHeartbeatRun]
    @RunId BIGINT
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE  [crawl].[Run]
    SET     HeartbeatUtc = SYSUTCDATETIME()
    WHERE   RunId = @RunId
      AND   Status = 1;
END
GO

PRINT 'crawl.uspHeartbeatRun created or altered.';
GO

-- ---------------------------------------------------------------------------
-- 3. The lease check, spliced into uspBeginRun.
--
-- The body below is sql/23's, with one block added before the INSERT and the
-- reaper widened. If sql/23 is re-run after this script it will put its own
-- older body back and the lease will silently stop being enforced - the same
-- standing hazard sql/28, sql/29, sql/33 and sql/40 carry. The verification at
-- the foot is what catches it.
-- ---------------------------------------------------------------------------

CREATE OR ALTER PROCEDURE [crawl].[uspBeginRun]
    @ConnectionId    NVARCHAR(64),
    @Mode            TINYINT,        -- 1 full, 2 incremental
    @HostName        NVARCHAR(128),
    @ProcessId       INT,
    @ToolVersion     NVARCHAR(64),
    @IsDryRun        BIT = 0,
    @FullEveryHours  INT = 168,      -- weekly, matching the runbook's default
    @AbandonAfterHours INT = 12,

    -- How long a run may go without a heartbeat before it is presumed dead.
    -- Three minutes against a sixty-second beat: wide enough that one slow round
    -- trip, one long GC pause or one retry storm cannot hand the lease away
    -- mid-crawl, narrow enough that a crashed host stops blocking its own
    -- replacement within an alerting interval rather than a working day.
    @LeaseGraceSeconds INT = 180
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @Mode NOT IN (1, 2)
        THROW 50001, 'Mode must be 1 (full) or 2 (incremental).', 1;

    IF NOT EXISTS (SELECT 1 FROM [crawl].[Connection] WHERE ConnectionId = @ConnectionId)
        THROW 50002, 'Unknown ConnectionId. Call crawl.uspRegisterConnection first.', 1;

    DECLARE @Reaped INT = 0;
    DECLARE @Now DATETIME2(3) = SYSUTCDATETIME();

    -- SERIALIZABLE and one transaction around the whole decision. Without it two
    -- processes can both read "no live run" and both insert one, which is
    -- precisely the race this procedure exists to lose. The lock is held for the
    -- few milliseconds this takes, not for the crawl.
    SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
    BEGIN TRANSACTION;

    -- Reap first, so the count this run reports is the backlog it inherited
    -- rather than a number that includes itself.
    --
    -- Two reasons a run is dead, and the heartbeat one is now the fast path.
    -- A run is presumed dead when its last beat - or its start, if it never beat
    -- - is older than the grace period. The @AbandonAfterHours arm stays as the
    -- backstop for rows written before this column existed, which have a NULL
    -- heartbeat and an old StartedUtc and would otherwise be reaped by the
    -- grace arm the first time this runs. That is the correct outcome for them,
    -- but the second arm makes it true regardless of which arm fires.
    UPDATE  [crawl].[Run]
    SET     Status       = 4,
            CompletedUtc = @Now,
            ErrorKind    = N'abandoned',
            ErrorMessage = N'No completion was recorded and the heartbeat stopped. The process is '
                         + N'assumed to have died; this row was closed by a later run.'
    WHERE   ConnectionId = @ConnectionId
      AND   Status = 1
      AND   (
                ISNULL(HeartbeatUtc, StartedUtc) < DATEADD(SECOND, -@LeaseGraceSeconds, @Now)
             OR StartedUtc < DATEADD(HOUR, -@AbandonAfterHours, @Now)
            );

    SET @Reaped = @@ROWCOUNT;

    -- THE LEASE. Anything still at status 1 after the reap has beaten recently
    -- and is genuinely alive, so this run does not start.
    --
    -- THROW rather than a quiet return, because "another instance is already
    -- crawling" is a fact the caller has to be able to act on. PushHost turns it
    -- into exit code 5, which a scheduler can treat as "skipped" rather than
    -- "failed" - a distinction that matters when the alternative is an operator
    -- being paged nightly for a job that is working exactly as designed.
    DECLARE @HolderRunId BIGINT, @HolderHost NVARCHAR(128), @HolderPid INT,
            @HolderStarted DATETIME2(3), @HolderBeat DATETIME2(3);

    SELECT  TOP (1)
            @HolderRunId = RunId, @HolderHost = HostName, @HolderPid = ProcessId,
            @HolderStarted = StartedUtc, @HolderBeat = HeartbeatUtc
    FROM    [crawl].[Run]
    WHERE   ConnectionId = @ConnectionId AND Status = 1
    ORDER BY RunId;

    IF @HolderRunId IS NOT NULL
    BEGIN
        -- NO EXPLICIT ROLLBACK HERE, and that is not an omission. XACT_ABORT is
        -- ON, so the THROW below terminates the transaction and rolls it back on
        -- its own. Writing it out as well breaks a caller that wraps this in
        -- INSERT ... EXEC, which SQL Server refuses with error 3915 - "cannot use
        -- the ROLLBACK statement within an INSERT-EXEC statement". The
        -- verification block at the foot of this script is exactly such a caller,
        -- and it caught this on the first run: the refusal arrived as 3915
        -- instead of 50043, so the lease looked like it was working while
        -- reporting the wrong reason.
        --
        -- Rolling back also discards this call's reap. That is correct rather
        -- than merely tolerable: the whole decision is one atomic question, and
        -- a reap the caller was refused the right to act on is better retried by
        -- whoever does get the lease.

        -- CONCAT, not a format specifier: THROW takes a literal message and does
        -- no substitution, so a %d here would be printed as %d.
        DECLARE @Held NVARCHAR(400) = CONCAT(
            N'Connection ', @ConnectionId, N' is already being crawled by run ', @HolderRunId,
            N' on ', ISNULL(@HolderHost, N'(unknown host)'), N' (pid ', ISNULL(@HolderPid, 0),
            N'), started ', CONVERT(NVARCHAR(30), @HolderStarted, 126),
            N', last heartbeat ', ISNULL(CONVERT(NVARCHAR(30), @HolderBeat, 126), N'(none yet)'),
            N'. This run is refused so the two do not sweep deletions against each other.');

        THROW 50043, @Held, 1;
    END

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
               OR @LastFullSuccessUtc < DATEADD(HOUR, -@FullEveryHours, @Now)
             THEN 1 ELSE 0 END;

    DECLARE @ActualMode TINYINT = CASE WHEN @FullCrawlDue = 1 THEN 1 ELSE @Mode END;

    INSERT  [crawl].[Run]
            (ConnectionId, Mode, Status, StartedUtc, HostName, ProcessId, ToolVersion,
             IsDryRun, HeartbeatUtc)
    VALUES  (@ConnectionId, @ActualMode, 1, @Now, @HostName, @ProcessId, @ToolVersion,
             @IsDryRun, @Now);

    DECLARE @RunId BIGINT = SCOPE_IDENTITY();

    COMMIT TRANSACTION;

    -- ALL SIX COLUMNS, in sql/23's order and under sql/23's names. The caller
    -- reads them by name, so dropping one is not a smaller result set - it is an
    -- IndexOutOfRangeException naming the missing column, at the first run after
    -- deployment. Which is exactly what the first draft of this script did: it
    -- returned four, and the crawl died on "FullCrawlDue".
    SELECT  @RunId              AS RunId,
            @ActualMode         AS Mode,
            @FullCrawlDue       AS FullCrawlDue,
            @LastFullSuccessUtc AS LastFullSuccessUtc,
            @HasCheckpoint      AS HasCheckpoint,
            @Reaped             AS AbandonedRunsReaped;
END
GO

PRINT 'crawl.uspBeginRun now enforces a heartbeat lease.';
GO

-- ---------------------------------------------------------------------------
-- 4. Grants. uspHeartbeatRun is new, so there is nothing for CREATE OR ALTER to
-- preserve. Guarded on the role existing, because sql/25 is optional on a rig
-- that runs everything as one account.
-- ---------------------------------------------------------------------------

IF DATABASE_PRINCIPAL_ID(N'crawl_writer') IS NOT NULL
BEGIN
    GRANT EXECUTE ON OBJECT::[crawl].[uspHeartbeatRun] TO [crawl_writer];
    PRINT 'Granted EXECUTE on uspHeartbeatRun to crawl_writer.';
END
ELSE
BEGIN
    PRINT 'Role crawl_writer does not exist, so nothing to grant. Run sql/25 if you expected it.';
END
GO

-- ---------------------------------------------------------------------------
-- 5. Verification, including a live acquire / refuse / expire sequence.
--
-- Static checks cannot tell whether the lease actually refuses anything, and a
-- lock that does not lock is the worst possible outcome here - it reads as
-- protection while providing none. So the block below opens a real run, proves a
-- second is refused, ages the heartbeat, proves the lease is then free, and
-- closes what it opened. It uses a throwaway connection id so it cannot touch a
-- real one.
-- ---------------------------------------------------------------------------

DECLARE @Probe NVARCHAR(64) = N'__runlock_probe';
DECLARE @Verdict NVARCHAR(200);
DECLARE @First BIGINT, @Second BIGINT;

EXEC [crawl].[uspRegisterConnection]
     @ConnectionId = @Probe, @ConnectorKey = N'probe', @DisplayName = N'run lock probe';

-- Clear anything a previous aborted probe left behind.
UPDATE [crawl].[Run] SET Status = 4, CompletedUtc = SYSUTCDATETIME()
WHERE ConnectionId = @Probe AND Status = 1;

DECLARE @Held TABLE (RunId BIGINT, Mode TINYINT, FullCrawlDue BIT, LastFullSuccessUtc DATETIME2(3), HasCheckpoint BIT, AbandonedRunsReaped INT);

INSERT @Held
EXEC [crawl].[uspBeginRun] @ConnectionId = @Probe, @Mode = 1, @HostName = N'probe-a',
     @ProcessId = 1, @ToolVersion = N'probe';

SELECT @First = RunId FROM @Held;

-- The second must be refused.
BEGIN TRY
    DECLARE @Held2 TABLE (RunId BIGINT, Mode TINYINT, FullCrawlDue BIT, LastFullSuccessUtc DATETIME2(3), HasCheckpoint BIT, AbandonedRunsReaped INT);

    INSERT @Held2
    EXEC [crawl].[uspBeginRun] @ConnectionId = @Probe, @Mode = 1, @HostName = N'probe-b',
         @ProcessId = 2, @ToolVersion = N'probe';

    SELECT @Second = RunId FROM @Held2;
    SET @Verdict = N'FAIL - a second run was allowed while the first held the lease';
END TRY
BEGIN CATCH
    SET @Verdict = CASE WHEN ERROR_NUMBER() = 50043
                        THEN N'OK - refused with 50043'
                        ELSE CONCAT(N'FAIL - refused with the wrong error: ', ERROR_NUMBER()) END;
END CATCH

SELECT N'a second concurrent run is refused' AS check_name, @Verdict AS verdict;

-- Age the heartbeat past the grace period; the lease must then be free, and the
-- dead run must be reaped rather than left at status 1 for ever.
UPDATE [crawl].[Run] SET HeartbeatUtc = DATEADD(SECOND, -600, SYSUTCDATETIME())
WHERE  RunId = @First;

DECLARE @Held3 TABLE (RunId BIGINT, Mode TINYINT, FullCrawlDue BIT, LastFullSuccessUtc DATETIME2(3), HasCheckpoint BIT, AbandonedRunsReaped INT);

INSERT @Held3
EXEC [crawl].[uspBeginRun] @ConnectionId = @Probe, @Mode = 1, @HostName = N'probe-c',
     @ProcessId = 3, @ToolVersion = N'probe';

SELECT  N'a stale heartbeat frees the lease' AS check_name,
        CASE WHEN EXISTS (SELECT 1 FROM @Held3) THEN N'OK' ELSE N'FAIL' END AS verdict
UNION ALL
SELECT  N'the dead run was reaped, not left running',
        CASE WHEN (SELECT Status FROM [crawl].[Run] WHERE RunId = @First) = 4
             THEN N'OK - status 4 abandoned' ELSE N'FAIL' END
UNION ALL
SELECT  N'the reap was counted for the run that inherited it',
        CASE WHEN (SELECT TOP (1) AbandonedRunsReaped FROM @Held3) >= 1
             THEN N'OK' ELSE N'FAIL - the count did not reach the caller' END;

-- Close the probe and remove its rows entirely; this is a test fixture, not
-- history worth keeping in a table an operator reads.
DELETE FROM [crawl].[Run] WHERE ConnectionId = @Probe;
DELETE FROM [crawl].[Connection] WHERE ConnectionId = @Probe;

SELECT  N'probe rows removed' AS check_name,
        CASE WHEN NOT EXISTS (SELECT 1 FROM [crawl].[Run] WHERE ConnectionId = @Probe)
              AND NOT EXISTS (SELECT 1 FROM [crawl].[Connection] WHERE ConnectionId = @Probe)
             THEN N'OK' ELSE N'FAIL - remove them by hand' END AS verdict;
GO

-- THE RESULT SHAPE, checked rather than trusted. The caller binds by column
-- name, so a missing column is an exception at the first real run rather than a
-- smaller answer - and the first draft of this script shipped five of six.
DECLARE @Shape TABLE (RunId BIGINT, Mode TINYINT, FullCrawlDue BIT,
                      LastFullSuccessUtc DATETIME2(3), HasCheckpoint BIT, AbandonedRunsReaped INT);

SELECT  N'uspBeginRun returns all six columns' AS check_name,
        CASE WHEN (SELECT COUNT(*) FROM sys.dm_exec_describe_first_result_set(
                       N'EXEC [crawl].[uspBeginRun] @ConnectionId = N''x'', @Mode = 1,
                          @HostName = N''x'', @ProcessId = 1, @ToolVersion = N''x''', NULL, 0)
                   WHERE name IN (N'RunId', N'Mode', N'FullCrawlDue',
                                  N'LastFullSuccessUtc', N'HasCheckpoint', N'AbandonedRunsReaped')) = 6
             THEN N'OK' ELSE N'FAIL - a caller binding by name will throw at the first run' END AS verdict;
GO

-- And the standing hazard, stated as a check rather than a comment.
SELECT  N'uspBeginRun still enforces the lease' AS check_name,
        CASE WHEN OBJECT_DEFINITION(OBJECT_ID(N'crawl.uspBeginRun')) LIKE N'%50043%'
             THEN N'OK' ELSE N'FAIL - sql/23 has been re-run over this; re-run sql/43' END AS verdict;
GO
