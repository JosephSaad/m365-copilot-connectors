-- ===========================================================================
-- 31-timesheet-trigger-health.sql
--
-- dbo.uspCheckEffectiveTriggers: the check that says out loud whether sql/26's
-- cascading triggers are still doing their job.
--
-- THE FAILURE THIS EXISTS TO CATCH. sql/26 installs three AFTER triggers that
-- maintain EffectiveLastModified, and every incremental crawl reads
-- "EffectiveLastModified > @marker" and nothing else. Disable one of them and
-- the source carries on exactly as before: it accepts writes, it returns rows,
-- it raises nothing, and the column simply stops moving. Every incremental
-- crawl from that moment on reads a delta that is missing those rows, reports
-- success, and writes a corpus that is quietly wrong. There is no error, no
-- failed run, no gap in the history, and no item that looks stale - the index
-- holds a value that WAS correct and is now not.
--
-- Nothing in the estate detects this. The connector cannot: from its side the
-- source did not return those rows, which is indistinguishable from the source
-- not having changed. crawl.vwConnectionHealth cannot: the runs succeed. The
-- content hashes cannot: an item that is never read is never hashed.
--
-- AND IT IS NOT HYPOTHETICAL. sql/26's own section 4 disables all three
-- triggers to run its backfill and re-enables them in a later batch. A
-- connection that drops, a session killed between the two, a deployment
-- interrupted at the wrong second - any of those leaves the source in exactly
-- this state, from a script that reported no error because it never got to one.
--
-- WHAT is_disabled DOES NOT COVER, which is why this is a procedure and not a
-- one-line query. A trigger can be present, enabled, and still not maintain the
-- column:
--
--   * recreated as INSTEAD OF rather than AFTER - the write it replaces never
--     reaches the table at all;
--   * recreated FOR INSERT only, so every UPDATE goes unstamped, which is the
--     case that matters most because a rename is an update;
--   * altered so the body no longer assigns EffectiveLastModified;
--   * on a table whose EffectiveLastModified column or IX_*_Effective index has
--     since been dropped.
--
-- Each of those reads as a healthy trigger in sys.triggers and in Object
-- Explorer. So this checks the catalogue for the ones a catalogue can see, and
-- then - the part that covers the ones it cannot - PERFORMS A WRITE and looks
-- at whether the column moved, inside a transaction that is rolled back.
--
-- AND WHAT sql/26's OWN VERIFICATION DOES NOT COVER. Its first query lists
-- descendants whose timestamp is behind an ancestor's. That is a real check and
-- it is included here, but it is an EFFECT check: it finds staleness only after
-- an ancestor has actually been written since the trigger stopped. A trigger
-- disabled on a quiet Sunday is invisible to it until the first rename lands,
-- by which time the crawls in between have already missed rows. The catalogue
-- checks and the probe are the cause; that query is the consequence. Reporting
-- either without the other leaves a real hole.
--
-- WHAT IT DOES ON FAILURE. THROW, by default, so a SQL Agent job step goes red
-- and lands the reason in job history. sql/32 schedules it. Call it with
-- @Throw = 0 to read the findings without the error.
--
-- Run against Ops, after sql/26. Idempotent. Verification block at the foot.
-- ===========================================================================

USE [Ops];
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
-- It bites HERE specifically, and not only in the state database: the probe
-- below issues an UPDATE, and an UPDATE from a module created with the option
-- OFF is refused outright against a table carrying a filtered index. A health
-- check that fails with error 1934 the first time it runs from a scheduled job
-- - having deployed clean from a query window - is the exact shape of the
-- defect sql/30 was written for.
--
-- Setting it here makes the stored setting independent of who ran the script.
-- Verify with sys.sql_modules.uses_quoted_identifier; sql/30 checks it.
-- ---------------------------------------------------------------------------
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO
SET NOCOUNT ON;
GO

CREATE OR ALTER PROCEDURE dbo.uspCheckEffectiveTriggers
    -- The write probe. On by default, because the catalogue checks alone cannot
    -- tell a working trigger from an altered one, and "the trigger exists" is
    -- precisely the reassurance this whole file exists to distrust. Turn it off
    -- in an estate whose change control will not accept a rolled-back write
    -- against a production table - and understand what is given up: everything
    -- from "altered so the body no longer assigns the column" onwards.
    @Probe BIT = 1,
    -- THROW on a finding. On by default so a job step fails. Off for reading.
    @Throw BIT = 1
AS
BEGIN
    SET NOCOUNT ON;

    -- The probe takes exclusive row locks on live source tables, and Ops does
    -- NOT have read-committed snapshot on - a concurrent crawl reading those
    -- rows will wait behind it. Three defences, all about being the one that
    -- gives way:
    --   LOCK_TIMEOUT      the probe abandons rather than queueing behind a
    --                     long-running crawl. Reported as SKIPPED, not as a
    --                     failure: a timeout says nothing about the trigger.
    --   DEADLOCK_PRIORITY the health check is always the victim. A monitoring
    --                     query that can kill a crawl is worse than the thing
    --                     it monitors.
    SET LOCK_TIMEOUT 5000;
    SET DEADLOCK_PRIORITY LOW;

    DECLARE @Findings TABLE
    (
        Seq       INT            NOT NULL,
        CheckName NVARCHAR(120)  NOT NULL,
        Verdict   NVARCHAR(10)   NOT NULL,   -- OK | FAIL | SKIPPED | INFO
        -- Wide on purpose. A CONCAT that overruns this column raises 8152 and
        -- takes the whole check down - a health check that fails because its own
        -- diagnostic was too long is the least useful failure available.
        Detail    NVARCHAR(1000) NOT NULL
    );

    /* -----------------------------------------------------------------------
       1. What the catalogue knows.

       The expected set is a literal list LEFT JOINed to sys.triggers, and that
       direction is the whole point. Reading sys.triggers and checking every row
       it returns finds a disabled trigger and misses a DELETED one entirely -
       the query returns two rows instead of three, every one of them healthy,
       and an empty answer for the third looks like no news. Starting from the
       expected list makes an absent trigger a row that says ABSENT.

       parent_id is matched as well as name, so a trigger of the right name
       moved to the wrong table reads as absent from the right one, which is
       what it is.
    ----------------------------------------------------------------------- */

    DECLARE @T TABLE
    (
        Seq         INT      NOT NULL,
        TriggerName SYSNAME  NOT NULL,
        TableName   SYSNAME  NOT NULL,
        IndexName   SYSNAME  NOT NULL,
        ObjectId    INT      NULL,
        IsDisabled  BIT      NULL,
        IsInsteadOf BIT      NULL,
        OnInsert    BIT      NULL,
        OnUpdate    BIT      NULL,
        NamesColumn BIT      NULL
    );

    INSERT INTO @T (Seq, TriggerName, TableName, IndexName)
    VALUES  (1, N'trgCustomers_Effective',   N'dbo.Customers',   N'IX_Customers_Effective'),
            (2, N'trgEngagements_Effective', N'dbo.Engagements', N'IX_Engagements_Effective'),
            (3, N'trgTimeEntries_Effective', N'dbo.TimeEntries', N'IX_TimeEntries_Effective');

    UPDATE  x
    SET     x.ObjectId    = t.object_id,
            x.IsDisabled  = t.is_disabled,
            x.IsInsteadOf = t.is_instead_of_trigger,
            x.OnInsert    = CASE WHEN EXISTS (SELECT 1 FROM sys.trigger_events AS e
                                              WHERE e.object_id = t.object_id AND e.type_desc = N'INSERT')
                                 THEN 1 ELSE 0 END,
            x.OnUpdate    = CASE WHEN EXISTS (SELECT 1 FROM sys.trigger_events AS e
                                              WHERE e.object_id = t.object_id AND e.type_desc = N'UPDATE')
                                 THEN 1 ELSE 0 END,
            x.NamesColumn = CASE WHEN m.definition LIKE N'%EffectiveLastModified%' THEN 1 ELSE 0 END
    FROM    @T AS x
    INNER JOIN sys.triggers AS t
            ON  t.name         = x.TriggerName
            AND t.parent_class = 1                       -- object trigger, not DDL
            AND t.parent_id    = OBJECT_ID(x.TableName)
    LEFT JOIN sys.sql_modules AS m ON m.object_id = t.object_id;

    -- Will it fire at all. One row per trigger, and the Detail carries the word
    -- that distinguishes the three ways of not firing.
    INSERT INTO @Findings (Seq, CheckName, Verdict, Detail)
    SELECT  10 + x.Seq,
            CONCAT(N'trigger ', x.TriggerName, N' fires on ', x.TableName),
            CASE WHEN x.ObjectId IS NULL OR x.IsDisabled = 1 OR x.IsInsteadOf = 1
                 THEN N'FAIL' ELSE N'OK' END,
            CASE
                WHEN x.ObjectId IS NULL
                    THEN N'ABSENT - no trigger of this name on this table. Every incremental crawl since it went is missing rows. Re-run sql/26 section 3, then its section 4 backfill.'
                WHEN x.IsDisabled = 1
                    THEN N'DISABLED - present and not firing. The source still accepts writes; EffectiveLastModified has stopped moving. ENABLE TRIGGER, then re-run sql/26 section 4 to repair the rows written meanwhile.'
                WHEN x.IsInsteadOf = 1
                    THEN N'INSTEAD OF - this must be an AFTER trigger. An INSTEAD OF trigger replaces the write rather than following it.'
                ELSE N'enabled, AFTER'
            END
    FROM    @T AS x;

    -- Fires on both events. SKIPPED rather than FAIL where the trigger is
    -- absent: it is one fault, and counting it three times would make the
    -- failure count say something untrue about how much is wrong.
    INSERT INTO @Findings (Seq, CheckName, Verdict, Detail)
    SELECT  20 + x.Seq,
            CONCAT(N'trigger ', x.TriggerName, N' covers INSERT and UPDATE'),
            CASE WHEN x.ObjectId IS NULL              THEN N'SKIPPED'
                 WHEN x.OnInsert = 1 AND x.OnUpdate = 1 THEN N'OK'
                 ELSE N'FAIL' END,
            CASE WHEN x.ObjectId IS NULL
                     THEN N'not checked - the trigger is absent, reported above'
                 WHEN x.OnUpdate = 0
                     THEN N'FOR INSERT only. A rename is an UPDATE, and a rename is the change this column exists to propagate - so the cascade this source depends on never runs.'
                 WHEN x.OnInsert = 0
                     THEN N'FOR UPDATE only. Inserted rows keep the column default rather than a stamped value.'
                 ELSE N'INSERT, UPDATE' END
    FROM    @T AS x;

    -- The body still assigns the column. Weaker than the probe below and worth
    -- having anyway: it is the only body check left when @Probe = 0.
    INSERT INTO @Findings (Seq, CheckName, Verdict, Detail)
    SELECT  30 + x.Seq,
            CONCAT(N'trigger ', x.TriggerName, N' body names EffectiveLastModified'),
            CASE WHEN x.ObjectId IS NULL    THEN N'SKIPPED'
                 WHEN x.NamesColumn = 1     THEN N'OK'
                 ELSE N'FAIL' END,
            CASE WHEN x.ObjectId IS NULL
                     THEN N'not checked - the trigger is absent, reported above'
                 WHEN x.NamesColumn = 1
                     THEN N'the definition references the column. A text match only - it does not prove the assignment runs, which is what the probe is for.'
                 ELSE N'the definition does not mention EffectiveLastModified at all. Something replaced this trigger with a different one under the same name.' END
    FROM    @T AS x;

    -- The column and its index. A dropped column breaks the trigger loudly on
    -- the next write; a dropped index breaks nothing and turns every
    -- incremental read into a scan, which looks like the source getting slower.
    INSERT INTO @Findings (Seq, CheckName, Verdict, Detail)
    SELECT  40 + x.Seq,
            CONCAT(N'EffectiveLastModified and ', x.IndexName, N' on ', x.TableName),
            CASE WHEN c.name IS NOT NULL AND i.name IS NOT NULL THEN N'OK' ELSE N'FAIL' END,
            CONCAT(N'column: ', CASE WHEN c.name IS NULL THEN N'ABSENT' ELSE N'present' END,
                   N' | index: ',
                   CASE WHEN i.name IS NULL
                        THEN N'ABSENT - reads still return the right rows and every one of them scans'
                        ELSE N'present' END)
    FROM    @T AS x
    LEFT JOIN sys.columns AS c
           ON c.object_id = OBJECT_ID(x.TableName) AND c.name = N'EffectiveLastModified'
    LEFT JOIN sys.indexes AS i
           ON i.object_id = OBJECT_ID(x.TableName) AND i.name = x.IndexName;

    /* -----------------------------------------------------------------------
       2. The consequence check - sql/26's verification query, as counts.

       Counts rather than rows, because this runs unattended. A query that
       "should return no rows" reports its pass as a blank, and a blank is also
       what a query that never ran leaves behind. A zero is a zero.
    ----------------------------------------------------------------------- */

    DECLARE @EngBehindCust INT, @TeBehindEng INT, @TeBehindCust INT;

    SELECT  @EngBehindCust = COUNT(*)
    FROM    dbo.Engagements  AS e
    INNER JOIN dbo.Customers AS c ON c.CustomerId = e.CustomerId
    WHERE   e.EffectiveLastModified < c.EffectiveLastModified;

    SELECT  @TeBehindEng = COUNT(*)
    FROM    dbo.TimeEntries    AS te
    INNER JOIN dbo.Engagements AS e ON e.EngagementId = te.EngagementId
    WHERE   te.EffectiveLastModified < e.EffectiveLastModified;

    SELECT  @TeBehindCust = COUNT(*)
    FROM    dbo.TimeEntries    AS te
    INNER JOIN dbo.Engagements AS e ON e.EngagementId = te.EngagementId
    INNER JOIN dbo.Customers   AS c ON c.CustomerId   = e.CustomerId
    WHERE   te.EffectiveLastModified < c.EffectiveLastModified;

    INSERT INTO @Findings (Seq, CheckName, Verdict, Detail)
    VALUES
        (51, N'no engagement is behind its customer',
             CASE WHEN @EngBehindCust = 0 THEN N'OK' ELSE N'FAIL' END,
             CONCAT(@EngBehindCust, N' engagement(s) carry a customer name older than the customer''s own timestamp')),
        (52, N'no time entry is behind its engagement',
             CASE WHEN @TeBehindEng = 0 THEN N'OK' ELSE N'FAIL' END,
             CONCAT(@TeBehindEng, N' time entr(ies) behind their engagement')),
        (53, N'no time entry is behind its customer',
             CASE WHEN @TeBehindCust = 0 THEN N'OK' ELSE N'FAIL' END,
             CONCAT(@TeBehindCust, N' time entr(ies) behind their customer'));

    /* -----------------------------------------------------------------------
       3. The probe. Write, look, roll back.

       Three separate short transactions rather than one long one, so the
       exclusive locks each takes are held for a single statement rather than
       for the whole check.

       Each level picks the row with the OLDEST EffectiveLastModified. That is
       not arbitrary: the triggers guard against recursion with
       "WHERE EffectiveLastModified < @Now", so a row already stamped in the
       future would legitimately not move and the probe would report a working
       trigger as broken. The oldest row cannot be in that state.

       The two upper levels also pick the row with the FEWEST descendants, which
       bounds how much the cascade locks. A customer probe genuinely does update
       every one of that customer's time entries - that is what the trigger is
       for - so the choice is between a small cascade and a large one, not
       between a cascade and none.

       ROLLBACK is what makes this safe to run on a schedule. Nothing is
       persisted, so no row's EffectiveLastModified moves and no incremental
       crawl sees a delta this check invented.
    ----------------------------------------------------------------------- */

    IF @Probe = 0
    BEGIN
        INSERT INTO @Findings (Seq, CheckName, Verdict, Detail)
        VALUES (60, N'live write probe', N'SKIPPED',
                N'not run - called with @Probe = 0. The catalogue checks above cannot detect a trigger that is present, enabled and altered to do nothing.');
    END
    ELSE IF @@TRANCOUNT > 0
    BEGIN
        -- Refusing rather than probing. The probe ends in ROLLBACK, which in a
        -- caller's open transaction would roll back the CALLER'S work, not the
        -- probe's. Reported as SKIPPED so it is never mistaken for a pass.
        INSERT INTO @Findings (Seq, CheckName, Verdict, Detail)
        VALUES (60, N'live write probe', N'SKIPPED',
                N'not run - called inside an open transaction, and the probe ends in ROLLBACK. Call it outside a transaction.');
    END
    ELSE
    BEGIN
        DECLARE @Id INT, @ChildId INT, @Before DATETIME2(3), @After DATETIME2(3),
                @ChildBefore DATETIME2(3), @ChildAfter DATETIME2(3),
                @Verdict NVARCHAR(10), @Detail NVARCHAR(500);

        -- ---- level 3: dbo.TimeEntries -------------------------------------
        SET @Id = NULL; SET @Before = NULL; SET @After = NULL;

        BEGIN TRY
            SELECT TOP (1) @Id = te.TimeEntryId
            FROM   dbo.TimeEntries AS te
            WHERE  te.EffectiveLastModified < SYSUTCDATETIME()
            ORDER BY te.EffectiveLastModified, te.TimeEntryId;

            IF @Id IS NULL
            BEGIN
                SET @Verdict = N'SKIPPED';
                SET @Detail  = N'no time entry has a timestamp in the past, so no row can be probed without a false failure. An empty table reaches here too - check the row count before reading this as a pass.';
            END
            ELSE
            BEGIN
                BEGIN TRANSACTION;
                    SELECT @Before = te.EffectiveLastModified FROM dbo.TimeEntries AS te WHERE te.TimeEntryId = @Id;
                    UPDATE dbo.TimeEntries SET LastModified = LastModified WHERE TimeEntryId = @Id;
                    SELECT @After = te.EffectiveLastModified FROM dbo.TimeEntries AS te WHERE te.TimeEntryId = @Id;
                ROLLBACK TRANSACTION;

                SET @Verdict = CASE WHEN @After > @Before THEN N'OK' ELSE N'FAIL' END;
                SET @Detail  = CASE WHEN @After > @Before
                                    THEN CONCAT(N'TimeEntryId ', @Id, N': an UPDATE moved EffectiveLastModified from ',
                                                CONVERT(NVARCHAR(30), @Before, 126), N' to ',
                                                CONVERT(NVARCHAR(30), @After, 126), N'. Rolled back.')
                                    ELSE CONCAT(N'TimeEntryId ', @Id, N': an UPDATE did NOT move EffectiveLastModified - it is still ',
                                                CONVERT(NVARCHAR(30), @Before, 126),
                                                N'. The column is not being maintained. Read check 13 for why: if it says ABSENT or DISABLED that is the cause; if it says enabled, the trigger''s body is not doing what sql/26 says it does.') END;
            END
        END TRY
        BEGIN CATCH
            IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
            SET @Verdict = CASE WHEN ERROR_NUMBER() = 1222 THEN N'SKIPPED' ELSE N'FAIL' END;
            SET @Detail  = CASE WHEN ERROR_NUMBER() = 1222
                                THEN N'lock timeout - the probe gave way to something already holding the row. Says nothing about the trigger; re-run outside the crawl window.'
                                ELSE CONCAT(N'error ', ERROR_NUMBER(), N': ', ERROR_MESSAGE()) END;
        END CATCH

        INSERT INTO @Findings (Seq, CheckName, Verdict, Detail)
        VALUES (63, N'probe: an UPDATE to a time entry moves its timestamp', @Verdict, @Detail);

        -- ---- level 2: dbo.Engagements, and its cascade to time entries ----
        SET @Id = NULL; SET @ChildId = NULL;
        SET @Before = NULL; SET @After = NULL; SET @ChildBefore = NULL; SET @ChildAfter = NULL;

        BEGIN TRY
            SELECT TOP (1) @Id = e.EngagementId
            FROM   dbo.Engagements AS e
            CROSS APPLY (SELECT COUNT(*) AS n FROM dbo.TimeEntries AS te WHERE te.EngagementId = e.EngagementId) AS d
            WHERE  e.EffectiveLastModified < SYSUTCDATETIME()
              AND  d.n > 0
            ORDER BY d.n, e.EngagementId;

            IF @Id IS NULL
            BEGIN
                SET @Verdict = N'SKIPPED';
                SET @Detail  = N'no engagement with a past timestamp and at least one time entry, so the cascade cannot be probed. An empty or childless source reaches here too.';
            END
            ELSE
            BEGIN
                BEGIN TRANSACTION;
                    SELECT @Before = e.EffectiveLastModified FROM dbo.Engagements AS e WHERE e.EngagementId = @Id;
                    SELECT TOP (1) @ChildId = te.TimeEntryId, @ChildBefore = te.EffectiveLastModified
                    FROM   dbo.TimeEntries AS te WHERE te.EngagementId = @Id ORDER BY te.TimeEntryId;

                    UPDATE dbo.Engagements SET LastModified = LastModified WHERE EngagementId = @Id;

                    SELECT @After = e.EffectiveLastModified FROM dbo.Engagements AS e WHERE e.EngagementId = @Id;
                    SELECT @ChildAfter = te.EffectiveLastModified FROM dbo.TimeEntries AS te WHERE te.TimeEntryId = @ChildId;
                ROLLBACK TRANSACTION;

                SET @Verdict = CASE WHEN @After > @Before AND @ChildAfter > @ChildBefore THEN N'OK' ELSE N'FAIL' END;
                SET @Detail  =
                    CONCAT(N'EngagementId ', @Id, N': own timestamp ',
                           CASE WHEN @After > @Before THEN N'moved' ELSE N'DID NOT MOVE' END,
                           N'; its time entry ', @ChildId, N' ',
                           CASE WHEN @ChildAfter > @ChildBefore
                                THEN N'moved with it'
                                ELSE N'DID NOT MOVE - the cascade to descendants is not running, so a renamed engagement leaves its time entries carrying the old name' END,
                           N'. Rolled back. Where something DID NOT MOVE, read the "fires on" check above for whether the trigger is absent, disabled, or present and no longer doing its job.');
            END
        END TRY
        BEGIN CATCH
            IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
            SET @Verdict = CASE WHEN ERROR_NUMBER() = 1222 THEN N'SKIPPED' ELSE N'FAIL' END;
            SET @Detail  = CASE WHEN ERROR_NUMBER() = 1222
                                THEN N'lock timeout - the probe gave way. Says nothing about the trigger; re-run outside the crawl window.'
                                ELSE CONCAT(N'error ', ERROR_NUMBER(), N': ', ERROR_MESSAGE()) END;
        END CATCH

        INSERT INTO @Findings (Seq, CheckName, Verdict, Detail)
        VALUES (62, N'probe: an UPDATE to an engagement cascades to its time entries', @Verdict, @Detail);

        -- ---- level 1: dbo.Customers, and its cascade to engagements -------
        SET @Id = NULL; SET @ChildId = NULL;
        SET @Before = NULL; SET @After = NULL; SET @ChildBefore = NULL; SET @ChildAfter = NULL;

        BEGIN TRY
            SELECT TOP (1) @Id = c.CustomerId
            FROM   dbo.Customers AS c
            CROSS APPLY (SELECT COUNT(*) AS n FROM dbo.Engagements AS e WHERE e.CustomerId = c.CustomerId) AS d
            WHERE  c.EffectiveLastModified < SYSUTCDATETIME()
              AND  d.n > 0
            ORDER BY d.n, c.CustomerId;

            IF @Id IS NULL
            BEGIN
                SET @Verdict = N'SKIPPED';
                SET @Detail  = N'no customer with a past timestamp and at least one engagement, so the cascade cannot be probed. An empty or childless source reaches here too.';
            END
            ELSE
            BEGIN
                BEGIN TRANSACTION;
                    SELECT @Before = c.EffectiveLastModified FROM dbo.Customers AS c WHERE c.CustomerId = @Id;
                    SELECT TOP (1) @ChildId = e.EngagementId, @ChildBefore = e.EffectiveLastModified
                    FROM   dbo.Engagements AS e WHERE e.CustomerId = @Id ORDER BY e.EngagementId;

                    UPDATE dbo.Customers SET LastModified = LastModified WHERE CustomerId = @Id;

                    SELECT @After = c.EffectiveLastModified FROM dbo.Customers AS c WHERE c.CustomerId = @Id;
                    SELECT @ChildAfter = e.EffectiveLastModified FROM dbo.Engagements AS e WHERE e.EngagementId = @ChildId;
                ROLLBACK TRANSACTION;

                SET @Verdict = CASE WHEN @After > @Before AND @ChildAfter > @ChildBefore THEN N'OK' ELSE N'FAIL' END;
                SET @Detail  =
                    CONCAT(N'CustomerId ', @Id, N': own timestamp ',
                           CASE WHEN @After > @Before THEN N'moved' ELSE N'DID NOT MOVE' END,
                           N'; its engagement ', @ChildId, N' ',
                           CASE WHEN @ChildAfter > @ChildBefore
                                THEN N'moved with it'
                                ELSE N'DID NOT MOVE - a renamed customer leaves its engagements and time entries carrying the old name, which is the defect sql/26 exists to prevent' END,
                           N'. Rolled back. Where something DID NOT MOVE, read the "fires on" check above for whether the trigger is absent, disabled, or present and no longer doing its job.');
            END
        END TRY
        BEGIN CATCH
            IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
            SET @Verdict = CASE WHEN ERROR_NUMBER() = 1222 THEN N'SKIPPED' ELSE N'FAIL' END;
            SET @Detail  = CASE WHEN ERROR_NUMBER() = 1222
                                THEN N'lock timeout - the probe gave way. Says nothing about the trigger; re-run outside the crawl window.'
                                ELSE CONCAT(N'error ', ERROR_NUMBER(), N': ', ERROR_MESSAGE()) END;
        END CATCH

        INSERT INTO @Findings (Seq, CheckName, Verdict, Detail)
        VALUES (61, N'probe: an UPDATE to a customer cascades to its engagements', @Verdict, @Detail);
    END

    /* -----------------------------------------------------------------------
       4. Context, reported rather than judged.

       'nested triggers' off would stop trgCustomers_Effective's update of
       dbo.Engagements from firing trgEngagements_Effective. That happens to be
       harmless as sql/26 is written, because the customer trigger updates time
       entries itself rather than relying on the engagement trigger to do it -
       but the next person to simplify one of those triggers on the assumption
       that the other cascades needs to know which world they are in. INFO, not
       FAIL: nothing is wrong today.
    ----------------------------------------------------------------------- */

    -- A scalar subquery rather than a FROM over sys.configurations, so this row
    -- exists even if the setting cannot be read. A SELECT-driven INSERT that
    -- matches nothing inserts nothing, and a missing INFO row would be silence
    -- where the whole file is about not accepting silence as an answer.
    INSERT INTO @Findings (Seq, CheckName, Verdict, Detail)
    VALUES  (90, N'server option: nested triggers', N'INFO',
             CONCAT(N'nested triggers = ',
                    ISNULL(CAST((SELECT CAST(c.value_in_use AS INT) FROM sys.configurations AS c
                                 WHERE c.name = N'nested triggers') AS NVARCHAR(10)), N'unreadable'),
                    N'. sql/26 does not depend on nesting - each trigger updates every level below it directly - but a trigger simplified to rely on it would silently stop cascading if this were 0.'));

    /* -----------------------------------------------------------------------
       5. The verdict.
    ----------------------------------------------------------------------- */

    SELECT  Seq, CheckName AS check_name, Verdict AS verdict, Detail AS detail
    FROM    @Findings
    ORDER BY Seq;

    DECLARE @Failed  INT = (SELECT COUNT(*) FROM @Findings WHERE Verdict = N'FAIL');
    DECLARE @Skipped INT = (SELECT COUNT(*) FROM @Findings WHERE Verdict = N'SKIPPED');
    DECLARE @Ok      INT = (SELECT COUNT(*) FROM @Findings WHERE Verdict = N'OK');

    SELECT  CASE WHEN @Failed > 0 THEN N'FAIL' WHEN @Skipped > 0 THEN N'PASS WITH SKIPS' ELSE N'PASS' END AS verdict,
            @Ok AS checks_ok, @Failed AS checks_failed, @Skipped AS checks_skipped;

    IF @Failed > 0 AND @Throw = 1
    BEGIN
        DECLARE @Names NVARCHAR(2000) =
            (SELECT STRING_AGG(CAST(CheckName AS NVARCHAR(MAX)), N'; ') WITHIN GROUP (ORDER BY Seq)
             FROM @Findings WHERE Verdict = N'FAIL');

        -- CONCAT rather than a format specifier: THROW takes a literal message
        -- and does no substitution, so a %d here would print as %d.
        DECLARE @Message NVARCHAR(2048) = CONCAT(
            N'Timesheet trigger health: ', @Failed, N' check(s) FAILED in ', DB_NAME(),
            N'. EffectiveLastModified is not being maintained, and every incremental crawl is reading a delta that is missing rows - silently, because the source still returns and accepts everything else. Failing checks: ',
            -- LEFT so that the actionable sentence after it survives. THROW
            -- takes at most 2048 characters and assignment into a shorter
            -- variable truncates SILENTLY, so an unbounded list of check names
            -- would quietly eat the instruction on how to see the detail.
            LEFT(@Names, 1200),
            N' | Run EXEC dbo.uspCheckEffectiveTriggers @Throw = 0 for the detail column.');

        THROW 50310, @Message, 1;
    END
END
GO

PRINT 'dbo.uspCheckEffectiveTriggers created or altered.';
GO

-- ---------------------------------------------------------------------------
-- 6. Deliberately NOT granted to the push identity.
--
-- Same reasoning as sql/25 applies to uspPurgeHistory. The probe writes to
-- dbo.Customers, dbo.Engagements and dbo.TimeEntries - rolled back, but the
-- permission it needs is a real one - and sql/13 exists to keep the identity
-- that pushes from reaching the base tables at all. A monitoring procedure is
-- not a reason to hand it back. This runs as the job owner or as an operator.
-- ---------------------------------------------------------------------------

-- ---------------------------------------------------------------------------
-- 7. Verification. Run it, and read the second result set.
--
-- On a healthy source: verdict PASS, checks_failed 0. A row with verdict INFO
-- is context and is not counted either way; a row with SKIPPED means a check
-- did not run and is reported as neither pass nor fail on purpose.
-- ---------------------------------------------------------------------------

EXEC dbo.uspCheckEffectiveTriggers @Probe = 1, @Throw = 0;
GO
