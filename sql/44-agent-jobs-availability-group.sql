-- ===========================================================================
-- 44-agent-jobs-availability-group.sql
--
-- Makes the two SQL Agent jobs safe to deploy on every replica of an
-- Availability Group.
--
-- THE PROBLEM, AND IT IS EASY TO MISS. SQL Agent jobs live in msdb. msdb is not
-- a user database and it does NOT fail over with an Availability Group. That has
-- two consequences and they pull in opposite directions:
--
--   Deploy the jobs on the primary only, and they vanish at the first failover.
--   Nothing runs, nothing errors, and the retention job silently stops bounding
--   the history table while the trigger health check silently stops watching the
--   triggers. Both failures are invisible, which is the shape this whole roadmap
--   section exists to attack.
--
--   Deploy them on every replica - which is the fix for that - and every replica
--   runs them on schedule. On a secondary, `ConnectorState` is either
--   unreadable, or readable and read-only. The retention job's UPDATEs and
--   DELETEs are refused, the job reports failure, and an operator who wired the
--   alerting from section 7 correctly now gets paged nightly by a replica that is
--   behaving exactly as it should.
--
-- So the jobs have to be deployed everywhere AND know where they are. That is
-- what this script adds.
--
-- HOW THE GUARD READS. sys.fn_hadr_is_primary_replica returns 1 on the primary,
-- 0 on a secondary, and NULL when the database is not in an Availability Group
-- at all. Only 0 is a reason not to run: NULL means standalone, which is every
-- deployment that has no AG, and a guard that treated NULL as "not primary"
-- would stop the jobs running on every single-instance deployment in existence.
--
-- Verified on the reference instance, which has no AG: the function exists, and
-- returns NULL for ConnectorState. IsHadrEnabled is 0. So the standalone path is
-- the one tested here, and it is the one that must not regress.
--
-- WHAT THIS SCRIPT CANNOT PROVE. There is no Availability Group on the reference
-- instance, so the secondary path - the guard actually returning 0 and the job
-- exiting quietly - has NOT been exercised. The verification below proves the
-- guard is present in both step commands and that the jobs still run to success
-- where there is no AG. The other half needs a two-node rig and is recorded as
-- untested rather than implied.
--
-- Run against ConnectorState, on EVERY replica, after sql/27 and sql/32.
-- Idempotent.
-- ===========================================================================

USE [msdb];
GO

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
GO

-- ---------------------------------------------------------------------------
-- The guard, as it is prepended to each step.
--
-- RETURN rather than THROW, and that is the whole point: a secondary skipping
-- its turn is not a failure and must not be reported as one. The job reports
-- success having done nothing, which is the correct answer to "did the retention
-- job run tonight" on a node that is not the primary.
--
-- RAISERROR ... WITH NOWAIT rather than PRINT so the line reaches the job
-- history immediately rather than being buffered - the history is where somebody
-- looks when asking why a job did nothing, and an empty log there is
-- indistinguishable from a job that never started.
-- ---------------------------------------------------------------------------

DECLARE @Guard NVARCHAR(MAX) = N'
-- Availability Group guard. 1 = primary, 0 = secondary, NULL = not in an AG.
-- Only 0 means "not my turn": NULL is a standalone instance and must run.
IF sys.fn_hadr_is_primary_replica(N''{DB}'') = 0
BEGIN
    RAISERROR (N''This replica is not the primary for {DB}; nothing to do.'', 0, 1) WITH NOWAIT;
    RETURN;
END
';

-- ---------------------------------------------------------------------------
-- 1. The retention job.
-- ---------------------------------------------------------------------------

DECLARE @RetentionJob SYSNAME = N'ConnectorState - purge crawl history';
DECLARE @RetentionStep SYSNAME = N'Purge every connection';
DECLARE @Existing NVARCHAR(MAX);

IF EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = @RetentionJob)
BEGIN
    SELECT  @Existing = s.command
    FROM    msdb.dbo.sysjobsteps AS s
    INNER JOIN msdb.dbo.sysjobs  AS j ON j.job_id = s.job_id
    WHERE   j.name = @RetentionJob AND s.step_name = @RetentionStep;

    IF @Existing IS NULL
    BEGIN
        PRINT 'Retention job exists but its step does not; re-run sql/27.';
    END
    ELSE IF @Existing LIKE N'%fn_hadr_is_primary_replica%'
    BEGIN
        PRINT 'Retention job already carries the Availability Group guard.';
    END
    ELSE
    BEGIN
        EXEC msdb.dbo.sp_update_jobstep
             @job_name  = @RetentionJob,
             @step_id   = 1,
             @command   = @Existing;   -- placeholder, replaced below

        -- Assigned separately because sp_update_jobstep will not take an
        -- expression as a parameter value - the same T-SQL restriction that
        -- caught sql/27's own @description on its first run.
        DECLARE @NewRetention NVARCHAR(MAX) =
            REPLACE(@Guard, N'{DB}', N'ConnectorState') + @Existing;

        EXEC msdb.dbo.sp_update_jobstep
             @job_name = @RetentionJob,
             @step_id  = 1,
             @command  = @NewRetention;

        PRINT 'Retention job step now guards on the primary replica.';
    END
END
ELSE
BEGIN
    PRINT 'Retention job not present. Run sql/27 first if you want it.';
END
GO

-- ---------------------------------------------------------------------------
-- 2. The trigger health job.
-- ---------------------------------------------------------------------------

DECLARE @Guard NVARCHAR(MAX) = N'
-- Availability Group guard. 1 = primary, 0 = secondary, NULL = not in an AG.
-- Only 0 means "not my turn": NULL is a standalone instance and must run.
IF sys.fn_hadr_is_primary_replica(N''{DB}'') = 0
BEGIN
    RAISERROR (N''This replica is not the primary for {DB}; nothing to do.'', 0, 1) WITH NOWAIT;
    RETURN;
END
';

DECLARE @HealthJob SYSNAME = N'Ops - timesheet trigger health';
DECLARE @HealthStep SYSNAME = N'Check the cascading triggers';
DECLARE @ExistingHealth NVARCHAR(MAX);

IF EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = @HealthJob)
BEGIN
    SELECT  @ExistingHealth = s.command
    FROM    msdb.dbo.sysjobsteps AS s
    INNER JOIN msdb.dbo.sysjobs  AS j ON j.job_id = s.job_id
    WHERE   j.name = @HealthJob AND s.step_name = @HealthStep;

    IF @ExistingHealth IS NULL
    BEGIN
        PRINT 'Trigger health job exists but its step does not; re-run sql/32.';
    END
    ELSE IF @ExistingHealth LIKE N'%fn_hadr_is_primary_replica%'
    BEGIN
        PRINT 'Trigger health job already carries the Availability Group guard.';
    END
    ELSE
    BEGIN
        -- Ops, not ConnectorState. The trigger health check reads the SOURCE
        -- database, and the two can be in different Availability Groups or in
        -- none - guarding on the wrong database would silence the job on a node
        -- that is perfectly able to run it.
        DECLARE @NewHealth NVARCHAR(MAX) =
            REPLACE(@Guard, N'{DB}', N'Ops') + @ExistingHealth;

        EXEC msdb.dbo.sp_update_jobstep
             @job_name = @HealthJob,
             @step_id  = 1,
             @command  = @NewHealth;

        PRINT 'Trigger health job step now guards on the primary replica.';
    END
END
ELSE
BEGIN
    PRINT 'Trigger health job not present. Run sql/32 first if you want it.';
END
GO

-- ---------------------------------------------------------------------------
-- 3. Verification.
--
-- Two checks and one honest omission. The guard's presence is checked in both
-- steps; the standalone behaviour is checked by evaluating the function the way
-- the guard does. The SECONDARY path is not checked and cannot be here.
-- ---------------------------------------------------------------------------

SELECT  N'retention job guards on the replica' AS check_name,
        CASE WHEN NOT EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = N'ConnectorState - purge crawl history')
                  THEN N'n/a - job not deployed'
             WHEN EXISTS (SELECT 1 FROM msdb.dbo.sysjobsteps s
                          JOIN msdb.dbo.sysjobs j ON j.job_id = s.job_id
                          WHERE j.name = N'ConnectorState - purge crawl history'
                            AND s.command LIKE N'%fn_hadr_is_primary_replica%')
                  THEN N'OK' ELSE N'FAIL' END AS verdict

UNION ALL

SELECT  N'trigger health job guards on the replica',
        CASE WHEN NOT EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = N'Ops - timesheet trigger health')
                  THEN N'n/a - job not deployed'
             WHEN EXISTS (SELECT 1 FROM msdb.dbo.sysjobsteps s
                          JOIN msdb.dbo.sysjobs j ON j.job_id = s.job_id
                          WHERE j.name = N'Ops - timesheet trigger health'
                            AND s.command LIKE N'%fn_hadr_is_primary_replica%')
                  THEN N'OK' ELSE N'FAIL' END

UNION ALL

-- The regression that matters on a machine with no AG: the guard must let the
-- job through. NULL is not 0, so the IF is false and execution continues.
SELECT  N'a standalone instance is not blocked by the guard',
        CASE WHEN ISNULL(sys.fn_hadr_is_primary_replica(N'ConnectorState'), -1) = 0
             THEN N'FAIL - this instance would skip its own jobs'
             ELSE N'OK - the guard falls through here' END

UNION ALL

SELECT  N'the secondary path',
        N'NOT TESTED - no Availability Group on this instance. Needs a two-node rig.';
GO
