-- ===========================================================================
-- 42-verify-least-privilege.sql
--
-- Does the least-privilege model actually work?
-- Blocker 1 says the model "is deployed but has never been exercised by the
-- accounts it is written for". That is still true of the ACCOUNTS - there are no
-- CONTOSO logins on this machine and there cannot be. But the PERMISSION SET can
-- be exercised without them, and never has been: every crawl in this project's
-- history has connected as a sysadmin, so no run has ever proved that
-- crawl_writer can do what the connector needs, or that it cannot do what
-- sql/25 denies it.
--
-- That matters more since sql/40, which drops and recreates a table type and
-- would have silently destroyed two of crawl_writer's grants. Nothing on this
-- rig would have noticed, because nothing runs as crawl_writer.
--
-- A user WITHOUT LOGIN plus EXECUTE AS is the cheap way to ask. It needs no
-- account, no password and no domain, and HAS_PERMS_BY_NAME answers the question
-- without executing anything - so this probe cannot mutate a single row.
--
-- WHAT IT FOUND THE FIRST TIME IT RAN. uspListLiveItemIds, added by sql/34 after
-- sql/25 was written, had no grant at all - sql/25 grants EXECUTE by NAME, and a
-- name it has never heard of gets nothing. The dry-run delete preview was
-- therefore refused under least privilege, and nothing on the reference rig
-- noticed because every crawl there connects as a sysadmin. Every procedure
-- added to this schema from now on should face this script before it ships.
--
-- Read-only apart from creating and dropping its own probe users. It executes
-- nothing: HAS_PERMS_BY_NAME answers the question without running a procedure,
-- so this cannot mutate a row even by accident.
--
-- Run against ConnectorState, after sql/25 and after any script that adds a
-- procedure or a table type. Needs no domain, no account and no password, which
-- is the point: the model can be exercised anywhere, even where the accounts it
-- is written for cannot exist.
-- ===========================================================================

SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;

IF DATABASE_PRINCIPAL_ID('lp_probe_writer') IS NOT NULL DROP USER lp_probe_writer;
IF DATABASE_PRINCIPAL_ID('lp_probe_reader') IS NOT NULL DROP USER lp_probe_reader;

CREATE USER lp_probe_writer WITHOUT LOGIN;
CREATE USER lp_probe_reader WITHOUT LOGIN;
ALTER ROLE crawl_writer ADD MEMBER lp_probe_writer;
ALTER ROLE crawl_reader ADD MEMBER lp_probe_reader;
GO

-- ---------------------------------------------------------------------------
-- 1. Everything the connector calls, checked as crawl_writer.
--
-- The list is the connector's actual call set, including the three procedures
-- added after sql/25 was written - uspCheckHashVersion, uspListLiveItemIds and
-- uspCompareAndSee - which is exactly where a grant goes missing: sql/25 grants
-- by name, and a name it has never heard of gets nothing.
-- ---------------------------------------------------------------------------

EXECUTE AS USER = 'lp_probe_writer';

SELECT  'writer: ' + p.n AS check_name,
        CASE WHEN HAS_PERMS_BY_NAME('crawl.' + p.n, 'OBJECT', 'EXECUTE') = 1
             THEN N'OK' ELSE N'DENIED - the connector cannot complete a run' END AS verdict
FROM    (VALUES
            ('uspRegisterConnection'), ('uspBeginRun'), ('uspCompleteRun'), ('uspFailRun'),
            ('uspGetItemState'), ('uspRecordWritten'), ('uspRecordUnchanged'),
            ('uspGetPendingDeletes'), ('uspConfirmDeletes'),
            ('uspGetCheckpoint'), ('uspSaveCheckpoint'),
            ('uspResolvePrincipals'), ('uspCachePrincipal'),
            ('uspRecordThrottle'), ('uspRecordThrottles'),
            ('uspRecordRunItemTypes'), ('uspSaveRunTiming'),
            ('uspCheckHashVersion'), ('uspListLiveItemIds'), ('uspCompareAndSee')
        ) AS p(n)
ORDER BY p.n;

-- The table types. A missing EXECUTE here is the failure that arrives at the END
-- of a run, after the crawl has done all its work - which is what sql/40 nearly
-- caused by dropping and recreating one.
SELECT  'writer type: ' + t.n AS check_name,
        CASE WHEN HAS_PERMS_BY_NAME('crawl.' + t.n, 'TYPE', 'EXECUTE') = 1
             THEN N'OK' ELSE N'DENIED - a run fails at its closing call' END AS verdict
FROM    (VALUES ('ItemIdList'), ('ItemStateList'), ('ItemTypeCountList'),
                ('PhaseTimingList'), ('PrincipalKeyList'), ('ThrottleEventList')) AS t(n)
ORDER BY t.n;

-- And the denials, which are the half nobody checks. A writer that can SELECT
-- crawl.Item directly, or UPDATE it, has the permission set of an administrator
-- wearing the name of a service account.
SELECT  'writer is DENIED ' + d.what AS check_name,
        CASE WHEN HAS_PERMS_BY_NAME('crawl.Item', 'OBJECT', d.perm) = 0
             THEN N'OK - refused' ELSE N'FAIL - crawl_writer can ' + d.what + N' crawl.Item directly' END AS verdict
FROM    (VALUES ('SELECT', 'SELECT'), ('INSERT', 'INSERT'),
                ('UPDATE', 'UPDATE'), ('DELETE', 'DELETE')) AS d(what, perm);

REVERT;
GO

-- ---------------------------------------------------------------------------
-- 2. The dashboard's set, checked as crawl_reader.
-- ---------------------------------------------------------------------------

EXECUTE AS USER = 'lp_probe_reader';

SELECT  'reader: ' + p.n AS check_name,
        CASE WHEN HAS_PERMS_BY_NAME('crawl.' + p.n, 'OBJECT', 'EXECUTE') = 1
             THEN N'OK' ELSE N'DENIED - a dashboard page returns 500' END AS verdict
FROM    (VALUES ('uspDashboardSummary'), ('uspListRuns'), ('uspGetRun'), ('uspListItems'),
                ('uspListPendingDeletes'), ('uspListThrottleEvents'), ('uspGetConnectionDetail')) AS p(n)
ORDER BY p.n;

SELECT  'reader view: ' + v.n AS check_name,
        CASE WHEN HAS_PERMS_BY_NAME('crawl.' + v.n, 'OBJECT', 'SELECT') = 1
             THEN N'OK' ELSE N'DENIED' END AS verdict
FROM    (VALUES ('vwRunHistory'), ('vwConnectionHealth'), ('vwPendingDeletes'),
                ('vwItemInventory'), ('vwThrottleSummary'), ('vwDailyActivity')) AS v(n)
ORDER BY v.n;

-- The reader must not be able to write, and must not be able to run the write
-- path's procedures. Two roles that share no permission is the claim.
SELECT  'reader cannot write crawl.Item' AS check_name,
        CASE WHEN HAS_PERMS_BY_NAME('crawl.Item', 'OBJECT', 'UPDATE') = 0
             THEN N'OK - refused' ELSE N'FAIL' END AS verdict
UNION ALL
SELECT  'reader cannot run uspBeginRun',
        CASE WHEN HAS_PERMS_BY_NAME('crawl.uspBeginRun', 'OBJECT', 'EXECUTE') = 0
             THEN N'OK - refused' ELSE N'FAIL - the two roles share a permission' END;

REVERT;
GO

-- ---------------------------------------------------------------------------
-- 3. Clean up. The probe users exist only for this script.
-- ---------------------------------------------------------------------------

DROP USER lp_probe_writer;
DROP USER lp_probe_reader;

SELECT  'probe users removed' AS check_name,
        CASE WHEN DATABASE_PRINCIPAL_ID('lp_probe_writer') IS NULL
              AND DATABASE_PRINCIPAL_ID('lp_probe_reader') IS NULL
             THEN N'OK' ELSE N'FAIL - remove them by hand' END AS verdict;
GO
