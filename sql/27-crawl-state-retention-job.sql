-- ===========================================================================
-- 27-crawl-state-retention-job.sql
--
-- The SQL Agent job that runs crawl.uspPurgeHistory, which sql/23 defines and
-- nothing until now scheduled.
--
-- This is the only script in the sql/20-27 set that writes to msdb rather than
-- to ConnectorState, and the only one whose absence is silent. Every other gap
-- in this set announces itself: a missing table throws, a missing grant throws,
-- a missing procedure throws at the first EXEC. Retention that was never
-- scheduled throws nothing at all. The database simply grows, and the first
-- symptom is a backup window or a disk, months later, with nothing in any log
-- to connect it to a decision nobody made.
--
-- IDENTITY. The job runs as its owner, which must be db_owner on
-- ConnectorState. Neither crawl_writer nor crawl_reader is granted
-- uspPurgeHistory, deliberately - see sql/25. Do not "fix" a permission error
-- from this job by granting the procedure to the connector's login; that hands
-- the thing that writes history the ability to delete it.
--
-- WHAT IT KEEPS, and why the two clocks differ:
--
--   @KeepRunDays              90   run history and its timing children
--   @KeepTombstoneDays       180   deleted items, on their own longer clock
--   @KeepExpiredPrincipalDays 30   cached principal resolutions
--
-- Tombstones outlive runs because a tombstone is how a re-created item is
-- recognised as a resurrection rather than as something new. Purge it early and
-- the item comes back with a fresh identity, which is a correct outcome that
-- costs a full rewrite. Ninety days of run history is what makes a "this got
-- slower" conversation answerable; it is the cheapest of the three to keep and
-- the first one anyone misses.
--
-- SCHEDULING. Sunday 03:00 by default, and the important part is not the hour
-- but that it is OUTSIDE every crawl window. The purge takes one transaction
-- across crawl.Run and crawl.Item, which are the two tables a running crawl is
-- writing. Read-committed snapshot keeps readers off writers; it does not keep
-- writers off each other. Move the schedule before you move the crawl.
--
-- RE-RUNNING. Idempotent: an existing job of this name is dropped and rebuilt,
-- so editing the retention numbers below and re-running is the supported way to
-- change them. Dropping the job discards its run history, which is the one
-- thing this script destroys - copy sysjobhistory first if that matters.
--
-- Run in the ConnectorState instance's msdb. Requires SQLAgentOperatorRole or
-- sysadmin. If SQL Agent is not installed - Express, for one - none of this
-- works and retention has to be scheduled by whatever else the estate runs.
-- Verification block at the foot.
-- ===========================================================================

USE [msdb];
GO

SET NOCOUNT ON;
GO

-- ---------------------------------------------------------------------------
-- 1. Refuse early, and say which of the two reasons it is.
--
-- Both checks fail the same way at sp_add_jobstep otherwise: an error naming a
-- database, which reads as a typo rather than as "SQL Agent is not running" or
-- "you deployed the job to the wrong instance".
-- ---------------------------------------------------------------------------

IF DB_ID(N'ConnectorState') IS NULL
BEGIN
    THROW 50100,
        N'ConnectorState does not exist on this instance. Run sql/20 to sql/25 first, and check you are connected to the instance that hosts the state database rather than the source.',
        1;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.dm_server_services
               WHERE servicename LIKE N'SQL Server Agent%' AND status_desc = N'Running')
BEGIN
    -- A warning, not a THROW. The job is still worth creating on an instance
    -- where Agent is stopped but installed - somebody starts it later, and a
    -- job that is already there starts running. What must not happen is this
    -- passing silently on an instance with no Agent at all.
    RAISERROR (N'SQL Server Agent is not running on this instance. The job will be created but will never fire until Agent is started. If this is SQL Server Express, Agent is not available at all and retention must be scheduled elsewhere - see docs/CRAWL-STATE-DEPLOYMENT.md section 6.', 10, 1) WITH NOWAIT;
END
GO

-- ---------------------------------------------------------------------------
-- 2. Drop any previous version, so this file is re-runnable.
-- ---------------------------------------------------------------------------

IF EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = N'ConnectorState - purge crawl history')
BEGIN
    EXEC msdb.dbo.sp_delete_job
            @job_name = N'ConnectorState - purge crawl history',
            @delete_unused_schedule = 1;

    RAISERROR (N'Existing job dropped and will be recreated. Its run history went with it.', 10, 1) WITH NOWAIT;
END
GO

-- ---------------------------------------------------------------------------
-- 3. The job.
-- ---------------------------------------------------------------------------

EXEC msdb.dbo.sp_add_job
        @job_name    = N'ConnectorState - purge crawl history',
        @description = N'Weekly retention for the crawl state store. Runs crawl.uspPurgeHistory '
                     + N'once per registered connection. See docs/CRAWL-STATE-DEPLOYMENT.md section 6.',
        @enabled     = 1,
        @owner_login_name = N'sa';
GO

-- uspPurgeHistory takes one connection at a time and has no all-connections
-- mode, so the step loops. RAISERROR ... WITH NOWAIT names each connection in
-- the job history as it goes: with one step covering every connection, the
-- alternative is a failure that says only that the step failed.
EXEC msdb.dbo.sp_add_jobstep
        @job_name       = N'ConnectorState - purge crawl history',
        @step_name      = N'Purge every connection',
        @subsystem      = N'TSQL',
        @database_name  = N'ConnectorState',
        @retry_attempts = 0,
        @on_success_action = 1,     -- quit reporting success
        @on_fail_action    = 2,     -- quit reporting failure; the history is the evidence
        @command = N'
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @ConnectionId NVARCHAR(64);

DECLARE Connections CURSOR LOCAL FAST_FORWARD FOR
    SELECT ConnectionId FROM crawl.Connection ORDER BY ConnectionId;

OPEN Connections;
FETCH NEXT FROM Connections INTO @ConnectionId;

WHILE @@FETCH_STATUS = 0
BEGIN
    RAISERROR (N''Purging %s'', 0, 1, @ConnectionId) WITH NOWAIT;

    EXEC crawl.uspPurgeHistory
            @ConnectionId             = @ConnectionId,
            @KeepRunDays              = 90,
            @KeepTombstoneDays        = 180,
            @KeepExpiredPrincipalDays = 30;

    FETCH NEXT FROM Connections INTO @ConnectionId;
END

CLOSE Connections;
DEALLOCATE Connections;';
GO

EXEC msdb.dbo.sp_add_schedule
        @schedule_name          = N'ConnectorState - weekly Sunday 03:00',
        @freq_type              = 8,        -- weekly
        @freq_interval          = 1,        -- Sunday
        @freq_recurrence_factor = 1,
        @active_start_time      = 030000;
GO

EXEC msdb.dbo.sp_attach_schedule
        @job_name      = N'ConnectorState - purge crawl history',
        @schedule_name = N'ConnectorState - weekly Sunday 03:00';
GO

EXEC msdb.dbo.sp_add_jobserver
        @job_name = N'ConnectorState - purge crawl history';
GO

-- ---------------------------------------------------------------------------
-- 4. Verification.
--
-- The first query is the one that matters. A job can exist, be enabled, and
-- never run, because sp_add_jobserver was not called or the schedule was not
-- attached - and both of those look exactly like a healthy job in the Object
-- Explorer tree. Three rows here, all reading OK, is the whole check.
-- ---------------------------------------------------------------------------

SELECT  N'job exists and is enabled' AS check_name,
        CASE WHEN EXISTS (SELECT 1 FROM msdb.dbo.sysjobs
                          WHERE name = N'ConnectorState - purge crawl history' AND enabled = 1)
             THEN N'OK' ELSE N'FAIL' END AS verdict

UNION ALL

SELECT  N'a schedule is attached',
        CASE WHEN EXISTS (SELECT 1
                          FROM msdb.dbo.sysjobs        AS j
                          JOIN msdb.dbo.sysjobschedules AS js ON js.job_id = j.job_id
                          WHERE j.name = N'ConnectorState - purge crawl history')
             THEN N'OK' ELSE N'FAIL - the job will never fire on its own' END

UNION ALL

SELECT  N'the job is assigned to a server',
        CASE WHEN EXISTS (SELECT 1
                          FROM msdb.dbo.sysjobs       AS j
                          JOIN msdb.dbo.sysjobservers AS jsv ON jsv.job_id = j.job_id
                          WHERE j.name = N'ConnectorState - purge crawl history')
             THEN N'OK' ELSE N'FAIL - sp_add_jobserver did not run' END;
GO

-- Run it once now rather than waiting a week to find out. On a fresh database
-- this purges nothing and takes no time; what it proves is that the job's owner
-- can actually EXECUTE the procedure, which is the failure this script exists
-- to surface early.
--
--   EXEC msdb.dbo.sp_start_job @job_name = N'ConnectorState - purge crawl history';
--
-- Then read the outcome. run_status 1 is success; anything else, open the step
-- history, where the RAISERROR lines name the connection it reached.
--
--   SELECT TOP (5) h.run_date, h.run_time, h.run_status, h.message
--   FROM   msdb.dbo.sysjobhistory AS h
--   JOIN   msdb.dbo.sysjobs       AS j ON j.job_id = h.job_id
--   WHERE  j.name = N'ConnectorState - purge crawl history'
--   ORDER BY h.instance_id DESC;
