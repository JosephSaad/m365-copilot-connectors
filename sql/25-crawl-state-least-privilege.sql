-- ===========================================================================
-- 25-crawl-state-least-privilege.sql
--
-- Two principals, two roles, and no table permission for either.
--
-- The connector writes crawl state. The dashboard reads it. Those are different
-- programs with different failure modes running on different machines, and
-- giving them one login because they touch one database is how a defect in a
-- web page ends up advancing a checkpoint.
--
--   crawl_writer   the connector.  EXECUTE on sql/23 only.
--   crawl_reader   the dashboard.  EXECUTE on sql/24 and SELECT on the sql/22
--                                  views. Nothing else, and no write of any
--                                  kind - not even a last-viewed timestamp.
--
-- Neither role has a permission on any table in sql/21. Both are DENYed
-- explicitly on the schema as well as simply not granted, because a future
-- ALTER ROLE that adds one of these to db_datareader would otherwise widen
-- access silently. DENY wins over GRANT, so the denial survives it.
--
-- CONTROL is deliberately absent from every DENY list here, for the reason
-- sql/01 gives: DENY CONTROL denies every permission it implies, including the
-- EXECUTE granted above, so it would silently break the connector while the
-- GRANT rows suggest access is configured.
--
-- Run last. The master half issues CREATE LOGIN, so this needs securityadmin at
-- the SERVER level - db_owner on ConnectorState alone is not enough unless both
-- logins already exist.
-- Replace CONTOSO\svc_gca_reader and CONTOSO\svc_connector_dashboard with the
-- accounts the connector service and the IIS application pool run as.
-- ===========================================================================

USE [master];
GO

/* ---------------------------------------------------------------------------
   1. Logins.

   Windows accounts, matching the pattern sql/01 uses for the source database.
   The connector already runs as svc_gca_reader against Ops; reusing that
   identity here is correct - it is the same program, and a second account would
   mean a second credential to rotate for no separation that this file does not
   already provide through roles.

   The dashboard gets its own, because it is a different program.
--------------------------------------------------------------------------- */

IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'CONTOSO\svc_gca_reader')
BEGIN
    CREATE LOGIN [CONTOSO\svc_gca_reader] FROM WINDOWS;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'CONTOSO\svc_connector_dashboard')
BEGIN
    CREATE LOGIN [CONTOSO\svc_connector_dashboard] FROM WINDOWS;
END
GO

USE [ConnectorState];
GO

/* ---------------------------------------------------------------------------
   2. Users and roles.
--------------------------------------------------------------------------- */

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'svc_gca_reader')
BEGIN
    CREATE USER [svc_gca_reader] FOR LOGIN [CONTOSO\svc_gca_reader];
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'svc_connector_dashboard')
BEGIN
    CREATE USER [svc_connector_dashboard] FOR LOGIN [CONTOSO\svc_connector_dashboard];
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'crawl_writer' AND type = 'R')
BEGIN
    CREATE ROLE [crawl_writer];
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'crawl_reader' AND type = 'R')
BEGIN
    CREATE ROLE [crawl_reader];
END
GO

ALTER ROLE [crawl_writer] ADD MEMBER [svc_gca_reader];
ALTER ROLE [crawl_reader] ADD MEMBER [svc_connector_dashboard];
GO

/* ---------------------------------------------------------------------------
   3. crawl_writer - the connector.

   EXECUTE on each write procedure by name. Not EXECUTE on the schema: a schema
   grant would automatically include every procedure added later, which means a
   future reporting procedure with a different threat profile is granted to the
   connector by the act of creating it. Naming them makes adding one a decision.

   The table types need EXECUTE as well. That is not a typo - passing a
   table-valued parameter requires EXECUTE on the type, and omitting it produces
   a permission error at the call site that reads as though the procedure were
   missing.
--------------------------------------------------------------------------- */

GRANT EXECUTE ON OBJECT::[crawl].[uspRegisterConnection]  TO [crawl_writer];
GRANT EXECUTE ON OBJECT::[crawl].[uspBeginRun]            TO [crawl_writer];
GRANT EXECUTE ON OBJECT::[crawl].[uspCompleteRun]         TO [crawl_writer];
GRANT EXECUTE ON OBJECT::[crawl].[uspFailRun]             TO [crawl_writer];
GRANT EXECUTE ON OBJECT::[crawl].[uspGetItemState]        TO [crawl_writer];
GRANT EXECUTE ON OBJECT::[crawl].[uspRecordWritten]       TO [crawl_writer];
GRANT EXECUTE ON OBJECT::[crawl].[uspRecordUnchanged]     TO [crawl_writer];
GRANT EXECUTE ON OBJECT::[crawl].[uspGetPendingDeletes]   TO [crawl_writer];
GRANT EXECUTE ON OBJECT::[crawl].[uspConfirmDeletes]      TO [crawl_writer];
GRANT EXECUTE ON OBJECT::[crawl].[uspGetCheckpoint]       TO [crawl_writer];
GRANT EXECUTE ON OBJECT::[crawl].[uspSaveCheckpoint]      TO [crawl_writer];
GRANT EXECUTE ON OBJECT::[crawl].[uspResolvePrincipals]   TO [crawl_writer];
GRANT EXECUTE ON OBJECT::[crawl].[uspCachePrincipal]      TO [crawl_writer];
GRANT EXECUTE ON OBJECT::[crawl].[uspRecordThrottle]      TO [crawl_writer];
GRANT EXECUTE ON OBJECT::[crawl].[uspRecordThrottles]     TO [crawl_writer];
GRANT EXECUTE ON OBJECT::[crawl].[uspRecordRunItemTypes]  TO [crawl_writer];
GRANT EXECUTE ON OBJECT::[crawl].[uspSaveRunTiming]       TO [crawl_writer];
GO

-- Table-valued parameter types.
GRANT EXECUTE ON TYPE::[crawl].[ItemIdList]         TO [crawl_writer];
GRANT EXECUTE ON TYPE::[crawl].[ItemStateList]      TO [crawl_writer];
GRANT EXECUTE ON TYPE::[crawl].[ItemTypeCountList]  TO [crawl_writer];
GRANT EXECUTE ON TYPE::[crawl].[PhaseTimingList]    TO [crawl_writer];
GRANT EXECUTE ON TYPE::[crawl].[PrincipalKeyList]   TO [crawl_writer];
GRANT EXECUTE ON TYPE::[crawl].[ThrottleEventList]  TO [crawl_writer];
GO

/* ---------------------------------------------------------------------------
   Deliberately NOT granted to crawl_writer:

     uspResetCheckpoint   Rewinding a connection to a full recrawl is an
                          operator action with a reason attached, not something
                          a connector should be able to do to itself after a bad
                          run. An operator runs it as themselves.

     uspPurgeHistory      Retention is a scheduled maintenance job with its own
                          identity. A connector that could purge its own history
                          could erase the evidence of a bad run, which is
                          exactly the run whose history matters.

   Both are reachable by db_owner, which is what the maintenance job and the
   operator connect as.
--------------------------------------------------------------------------- */

/* ---------------------------------------------------------------------------
   4. crawl_reader - the dashboard.

   SELECT on the six views, EXECUTE on the seven reporting procedures, and
   nothing else. The views are the only object in this database the dashboard
   can name, and none of them exposes a column the schema does not already hold
   - which is to say none of them can expose item content, because the store has
   never held any.
--------------------------------------------------------------------------- */

GRANT SELECT ON OBJECT::[crawl].[vwRunHistory]        TO [crawl_reader];
GRANT SELECT ON OBJECT::[crawl].[vwConnectionHealth]  TO [crawl_reader];
GRANT SELECT ON OBJECT::[crawl].[vwPendingDeletes]    TO [crawl_reader];
GRANT SELECT ON OBJECT::[crawl].[vwItemInventory]     TO [crawl_reader];
GRANT SELECT ON OBJECT::[crawl].[vwThrottleSummary]   TO [crawl_reader];
GRANT SELECT ON OBJECT::[crawl].[vwDailyActivity]     TO [crawl_reader];
GO

GRANT EXECUTE ON OBJECT::[crawl].[uspDashboardSummary]     TO [crawl_reader];
GRANT EXECUTE ON OBJECT::[crawl].[uspListRuns]             TO [crawl_reader];
GRANT EXECUTE ON OBJECT::[crawl].[uspGetRun]               TO [crawl_reader];
GRANT EXECUTE ON OBJECT::[crawl].[uspListItems]            TO [crawl_reader];
GRANT EXECUTE ON OBJECT::[crawl].[uspListPendingDeletes]   TO [crawl_reader];
GRANT EXECUTE ON OBJECT::[crawl].[uspListThrottleEvents]   TO [crawl_reader];
GRANT EXECUTE ON OBJECT::[crawl].[uspGetConnectionDetail]  TO [crawl_reader];
GO

/* ---------------------------------------------------------------------------
   5. The denials.

   Explicit, on the schema, for both roles. Without these, a later
   "ALTER ROLE db_datareader ADD MEMBER svc_connector_dashboard" - the single
   most common thing anyone does when a dashboard query fails - would silently
   grant the web tier read access to every table. With them, that change has no
   effect and the person makes the correct fix instead.

   INSERT, UPDATE, DELETE and ALTER are denied to BOTH roles including the
   writer, because the writer's writes go through procedures. A procedure
   executes under its owner's rights for the tables it touches (ownership
   chaining, unbroken here because schema and tables share an owner), so denying
   the caller direct DML does not affect it and does bound what a SQL injection
   in a connector could reach.
--------------------------------------------------------------------------- */

DENY INSERT, UPDATE, DELETE, ALTER, REFERENCES ON SCHEMA::[crawl] TO [crawl_writer];
DENY SELECT                                    ON SCHEMA::[crawl] TO [crawl_writer];
GO

DENY INSERT, UPDATE, DELETE, ALTER, REFERENCES ON SCHEMA::[crawl] TO [crawl_reader];
GO

-- The reader's SELECT grants above are on individual views and survive the
-- schema-level DENY only because object-level GRANT and schema-level DENY are
-- evaluated at different scopes with DENY winning at the SAME scope. To keep
-- that unambiguous rather than clever, the reader is NOT denied SELECT at the
-- schema level - its access is bounded by having no grant on anything except
-- the six views, and the DML denials above are what stop it writing.

/* ---------------------------------------------------------------------------
   6. Verification.

   Run this after any change to the roles. The expected result is: crawl_writer
   with EXECUTE on seventeen procedures and six types and nothing else, and
   crawl_reader with EXECUTE on seven procedures and SELECT on six views.

   Anything with a permission on a TABLE is a finding.
--------------------------------------------------------------------------- */

-- Table types live in sys.types, not sys.objects, so a LEFT JOIN to sys.objects
-- alone renders every GRANT EXECUTE ON TYPE:: row as a SCHEMA grant with a
-- nonsense name - correct permissions, misleading display, and someone
-- eventually reports it as a finding. Both are joined, and the class is read
-- from p.class_desc rather than inferred.
SELECT
    dp.name      AS principal_name,
    dp.type_desc AS principal_type,
    p.permission_name,
    p.state_desc,
    p.class_desc AS object_class,
    COALESCE(
        SCHEMA_NAME(o.schema_id) + N'.' + o.name,
        SCHEMA_NAME(t.schema_id) + N'.' + t.name,
        SCHEMA_NAME(p.major_id))  AS object_name,
    COALESCE(o.type_desc, CASE WHEN t.name IS NOT NULL THEN N'TABLE_TYPE' END, N'SCHEMA') AS object_type
FROM        sys.database_permissions AS p
INNER JOIN  sys.database_principals  AS dp ON dp.principal_id = p.grantee_principal_id
LEFT JOIN   sys.objects              AS o  ON o.object_id = p.major_id AND p.class = 1
LEFT JOIN   sys.types                AS t  ON t.user_type_id = p.major_id AND p.class = 6
WHERE       dp.name IN (N'crawl_writer', N'crawl_reader')
ORDER BY    dp.name, p.state_desc DESC, object_type, object_name;

-- The finding query: any direct table permission for either role. Expected to
-- return nothing.
--
-- p.class = 1 is not optional here, even though the inventory query above reads
-- fine without it. sys.database_permissions.major_id means different things per
-- class - an object_id for class 1, a user_type_id for class 6 - so joining to
-- sys.objects unfiltered lets a TYPE grant whose id happens to match a table's
-- object_id surface as a table grant nobody made. This is the query an auditor
-- runs and the one whose output has to be trusted literally, so it filters
-- rather than relying on two id spaces not colliding.
SELECT  dp.name AS principal_name, o.name AS table_name, p.permission_name, p.state_desc
FROM        sys.database_permissions AS p
INNER JOIN  sys.database_principals  AS dp ON dp.principal_id = p.grantee_principal_id
INNER JOIN  sys.objects              AS o  ON o.object_id = p.major_id
WHERE       dp.name IN (N'crawl_writer', N'crawl_reader')
  AND       p.class = 1
  AND       o.type = 'U'
  AND       p.state_desc = 'GRANT';
GO
