-- ===========================================================================
-- 32-timesheet-trigger-health-job.sql
--
-- The SQL Agent job that runs dbo.uspCheckEffectiveTriggers, which sql/31
-- defines and nothing until now scheduled.
--
-- WHY THIS FILE EXISTS SEPARATELY FROM sql/31. A check nobody runs is a comment.
-- The failure it detects - a disabled cascading trigger - has no symptom that
-- brings anybody to a keyboard: the source keeps accepting writes, the crawls
-- keep succeeding, and the corpus quietly stops matching the source. There is
-- no moment at which somebody thinks to go and look. So the looking has to be
-- on a clock, and the clock has to be somewhere other than in the head of the
-- person who deployed sql/26.
--
-- This is the second script in the set that writes to msdb rather than to a
-- data database; sql/27 is the other, and this one follows its shape
-- deliberately so there is one pattern to learn rather than two.
--
-- SCHEDULING. Daily 02:30, and the two things that matter about that are not
-- the hour:
--
--   DAILY, not weekly. This is a detection latency, not a maintenance window.
--   A week of undetected staleness is a week of incremental crawls that each
--   reported success while missing rows, and every one of those runs has to be
--   made good by a full recrawl afterwards. Retention can wait seven days;
--   this cannot.
--
--   BEFORE THE FIRST CRAWL OF THE DAY, and outside every crawl window. The
--   probe in sql/31 takes exclusive row locks, and Ops does not have
--   read-committed snapshot on, so a crawl reading those rows waits behind it.
--   sql/31 defends itself with a five second LOCK_TIMEOUT and
--   DEADLOCK_PRIORITY LOW - it gives way rather than blocking - but a check
--   that keeps giving way is a check that keeps reporting SKIPPED. Move this
--   schedule to sit just ahead of the first crawl in THIS estate's timetable;
--   02:30 is a placeholder that happens to be half an hour ahead of sql/27's
--   Sunday purge.
--
-- WHAT A FAILURE LOOKS LIKE. The step runs the procedure with its default
-- @Throw = 1, so a finding raises error 50310 and the step goes red with the
-- failing check names in its message. @notify_level_eventlog is set explicitly
-- to 2 so that failure also reaches the Windows event log, which is the only
-- part of this that anything outside SQL Server can watch.
--
-- RE-RUNNING. Idempotent: an existing job of this name is dropped and rebuilt,
-- so editing the schedule below and re-running is the supported way to move it.
-- Dropping the job discards its run history.
--
-- Run in the Ops instance's msdb. Requires SQLAgentOperatorRole or sysadmin.
--
-- IF SQL AGENT IS NOT AVAILABLE - SQL Server Express has no Agent at all, and
-- it is not a service that is stopped, it is a component that is not installed
-- - then none of this file works and there is nothing to fix inside SQL Server.
-- The check still runs; only the clock is missing. Use
-- deploy/Test-TriggerHealth.ps1 from Windows Task Scheduler, which calls the
-- same procedure and exits non-zero on a finding so the scheduled task records
-- a failure. Section 5 below has the command line. Do not substitute "somebody
-- runs it after each deployment": the failure this catches arrives without a
-- deployment.
--
-- Verification block at the foot.
-- ===========================================================================

USE [msdb];
GO

SET NOCOUNT ON;
GO

-- ---------------------------------------------------------------------------
-- 1. Refuse early, and say which of the three reasons it is.
--
-- All three otherwise surface as one error from sp_add_jobstep naming a
-- database, which reads as a typo and is not one.
-- ---------------------------------------------------------------------------

IF DB_ID(N'Ops') IS NULL
BEGIN
    THROW 50320,
        N'Ops does not exist on this instance. This job runs against the SOURCE database, not the crawl state store - check you are connected to the instance that hosts the timesheet source. Run sql/10 and sql/12 first.',
        1;
END
GO

-- The procedure, not just the database. A job created against a missing
-- procedure is created successfully, scheduled successfully, and fails every
-- night at 02:30 with "Could not find stored procedure" - which is a real
-- alert, about the wrong thing, arriving too late to be about a deployment.
IF OBJECT_ID(N'Ops.dbo.uspCheckEffectiveTriggers', N'P') IS NULL
BEGIN
    THROW 50321,
        N'Ops.dbo.uspCheckEffectiveTriggers does not exist. Run sql/31 against Ops before scheduling it here.',
        1;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.dm_server_services
               WHERE servicename LIKE N'SQL Server Agent%' AND status_desc = N'Running')
BEGIN
    -- A warning, not a THROW, and for the same reason as in sql/27: on an
    -- instance where Agent is installed but stopped, a job that is already
    -- there starts running the moment somebody starts it. What must not happen
    -- is this passing silently where there is no Agent at all.
    RAISERROR (N'SQL Server Agent is not running on this instance. The job will be created but will never fire until Agent is started. If this is SQL Server Express, Agent is not available at all: schedule deploy/Test-TriggerHealth.ps1 through Windows Task Scheduler instead - see section 5 at the foot of this file.', 10, 1) WITH NOWAIT;
END
GO

-- ---------------------------------------------------------------------------
-- 2. Drop any previous version, so this file is re-runnable.
-- ---------------------------------------------------------------------------

IF EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = N'Ops - timesheet trigger health')
BEGIN
    EXEC msdb.dbo.sp_delete_job
            @job_name = N'Ops - timesheet trigger health',
            @delete_unused_schedule = 1;

    RAISERROR (N'Existing job dropped and will be recreated. Its run history went with it.', 10, 1) WITH NOWAIT;
END
GO

-- ---------------------------------------------------------------------------
-- 3. The job.
--
-- Every argument below is a literal. A stored procedure parameter cannot be an
-- expression in T-SQL - @description = N'a' + N'b' is rejected outright - so
-- there is nothing to be gained by composing these and a deployment to be lost.
-- ---------------------------------------------------------------------------

EXEC msdb.dbo.sp_add_job
        @job_name    = N'Ops - timesheet trigger health',
        @description = N'Daily check that sql/26 cascading triggers still maintain EffectiveLastModified. A failure here means incremental crawls are silently missing rows. See sql/31-timesheet-trigger-health.sql.',
        @enabled     = 1,
        @owner_login_name = N'sa',
        -- 2 = write to the Windows event log on failure only. Explicit rather
        -- than defaulted, because it is the single hook by which anything
        -- outside SQL Server can learn that this failed.
        @notify_level_eventlog = 2;
GO

-- One step, one EXEC. @Throw is left at its default of 1 on purpose: the whole
-- value of a scheduled run is that a finding turns into a red step with the
-- failing check names in its message. Passing @Throw = 0 here would produce a
-- job that succeeds every night while reporting the failure to nobody.
--
-- @retry_attempts = 0. A disabled trigger does not fix itself between attempts,
-- and the one transient outcome the procedure has - a lock timeout on the probe
-- - is already reported as SKIPPED rather than as a failure, so there is
-- nothing here for a retry to rescue.
EXEC msdb.dbo.sp_add_jobstep
        @job_name       = N'Ops - timesheet trigger health',
        @step_name      = N'Check the cascading triggers',
        @subsystem      = N'TSQL',
        @database_name  = N'Ops',
        @retry_attempts = 0,
        @on_success_action = 1,     -- quit reporting success
        @on_fail_action    = 2,     -- quit reporting failure; the history is the evidence
        @command = N'EXEC dbo.uspCheckEffectiveTriggers @Probe = 1, @Throw = 1;';
GO

EXEC msdb.dbo.sp_add_schedule
        @schedule_name          = N'Ops - daily 02:30',
        @freq_type              = 4,        -- daily
        @freq_interval          = 1,        -- every day
        @active_start_time      = 023000;
GO

EXEC msdb.dbo.sp_attach_schedule
        @job_name      = N'Ops - timesheet trigger health',
        @schedule_name = N'Ops - daily 02:30';
GO

EXEC msdb.dbo.sp_add_jobserver
        @job_name = N'Ops - timesheet trigger health';
GO

-- ---------------------------------------------------------------------------
-- 4. Verification.
--
-- Three rows, all reading OK. The second and third are the ones worth having:
-- a job can exist and be enabled and never fire, because no schedule was
-- attached or sp_add_jobserver was not called, and both of those look exactly
-- like a healthy job in the Object Explorer tree.
-- ---------------------------------------------------------------------------

SELECT  N'job exists and is enabled' AS check_name,
        CASE WHEN EXISTS (SELECT 1 FROM msdb.dbo.sysjobs
                          WHERE name = N'Ops - timesheet trigger health' AND enabled = 1)
             THEN N'OK' ELSE N'FAIL' END AS verdict

UNION ALL

SELECT  N'a schedule is attached',
        CASE WHEN EXISTS (SELECT 1
                          FROM msdb.dbo.sysjobs         AS j
                          JOIN msdb.dbo.sysjobschedules AS js ON js.job_id = j.job_id
                          WHERE j.name = N'Ops - timesheet trigger health')
             THEN N'OK' ELSE N'FAIL - the job will never fire on its own' END

UNION ALL

SELECT  N'the job is assigned to a server',
        CASE WHEN EXISTS (SELECT 1
                          FROM msdb.dbo.sysjobs       AS j
                          JOIN msdb.dbo.sysjobservers AS jsv ON jsv.job_id = j.job_id
                          WHERE j.name = N'Ops - timesheet trigger health')
             THEN N'OK' ELSE N'FAIL - sp_add_jobserver did not run' END;
GO

-- Run it once now rather than waiting until 02:30 to find out. On a healthy
-- source this takes a second and proves the thing three verification rows
-- above cannot: that the job's OWNER can actually execute the procedure and
-- write to the source tables the probe touches.
--
--   EXEC msdb.dbo.sp_start_job @job_name = N'Ops - timesheet trigger health';
--
-- Then read the outcome. run_status 1 is success; anything else, the message
-- column carries the 50310 text naming the failing checks.
--
--   SELECT TOP (5) h.run_date, h.run_time, h.run_status, h.message
--   FROM   msdb.dbo.sysjobhistory AS h
--   JOIN   msdb.dbo.sysjobs       AS j ON j.job_id = h.job_id
--   WHERE  j.name = N'Ops - timesheet trigger health'
--   ORDER BY h.instance_id DESC;

-- ---------------------------------------------------------------------------
-- 5. Where there is no SQL Agent.
--
-- SQL Server Express has no Agent - not stopped, absent - so everything above
-- is unavailable and there is no setting that restores it. The check itself is
-- unaffected; only the clock is missing. Schedule it from outside:
--
--   powershell -NoProfile -ExecutionPolicy Bypass ^
--     -File deploy\Test-TriggerHealth.ps1 -SqlInstance localhost -Database Ops
--
-- as a daily Windows Scheduled Task. The script sets Encrypt and does NOT set
-- TrustServerCertificate, matching deploy/Test-SqlSource.ps1 and the connector's
-- own factory, so an instance with a self-signed certificate refuses the
-- connection until either the certificate is trusted or the operator adds
-- -AllowUntrustedServerCertificate deliberately.
--
-- The script exits 1 on a finding and 0
-- otherwise, so Task Scheduler records the failure in its Last Run Result -
-- which is the same signal a red job step gives, from the only scheduler the
-- edition has. Set the task to run whether or not the user is logged on, under
-- an account that is a member of db_owner on Ops.
--
-- Everything else in that estate that needs a clock - sql/27's retention among
-- it - has the same problem and the same answer.
-- ---------------------------------------------------------------------------
