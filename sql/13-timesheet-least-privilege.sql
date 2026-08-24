-- ===========================================================================
-- 13-timesheet-least-privilege.sql
--
-- The principal SqlHierarchyPush reads with, and its grant.
--
-- The grant is on the VIEWS ONLY. The push identity cannot read dbo.Customers,
-- dbo.Engagements or dbo.TimeEntries directly, which means:
--
--   * the shape of what leaves the database is fixed by 12-timesheet-views.sql
--     and cannot be widened by changing the tool;
--   * the soft delete filter cannot be bypassed by accident, because it lives
--     inside the view rather than in a WHERE clause somebody might edit;
--   * a reviewer can see exactly which columns are indexable by reading one
--     file, without reading any C#.
--
-- Ownership chaining does the rest: because the views and the base tables share
-- an owner (dbo), SELECT on the view is sufficient and no permission on the
-- tables is needed or granted.
--
-- This grant is SEPARATE from the ticket test case's grant in
-- 01-least-privilege.sql. Both can exist in the same database; give them
-- separate principals unless you have a reason not to, so that revoking one
-- test case does not disturb the other.
--
-- Run as sysadmin or securityadmin, once per environment, after 12.
-- Use the variant matching DataSource:SqlAuthMode and delete the others.
-- ===========================================================================

/* ---------------------------------------------------------------------------
   Variant A: Windows integrated  (SqlAuthMode = WindowsIntegrated)

   SqlHierarchyPush usually runs interactively on an operator workstation, so
   this is frequently a named person rather than a service account. That is a
   legitimate choice for a seeding tool and a poor one for anything scheduled.
--------------------------------------------------------------------------- */

USE [master];
GO

IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'CONTOSO\svc_hierarchy_reader')
BEGIN
    CREATE LOGIN [CONTOSO\svc_hierarchy_reader] FROM WINDOWS;
END
GO

USE [Ops];
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'svc_hierarchy_reader')
BEGIN
    CREATE USER [svc_hierarchy_reader] FOR LOGIN [CONTOSO\svc_hierarchy_reader];
END
GO

-- The whole grant: four views, SELECT, nothing else.
GRANT SELECT ON OBJECT::dbo.vwExternalItems   TO [svc_hierarchy_reader];
GRANT SELECT ON OBJECT::dbo.vwCustomerItems   TO [svc_hierarchy_reader];
GRANT SELECT ON OBJECT::dbo.vwEngagementItems TO [svc_hierarchy_reader];
GRANT SELECT ON OBJECT::dbo.vwTimeEntryItems  TO [svc_hierarchy_reader];
GO

-- Explicit denial on the base tables. Ownership chaining already means no grant
-- is required, so this changes nothing today; it exists so that a future role
-- membership cannot silently widen this identity's reach past the views.
DENY SELECT, INSERT, UPDATE, DELETE, ALTER, CONTROL ON OBJECT::dbo.Customers   TO [svc_hierarchy_reader];
DENY SELECT, INSERT, UPDATE, DELETE, ALTER, CONTROL ON OBJECT::dbo.Engagements TO [svc_hierarchy_reader];
DENY SELECT, INSERT, UPDATE, DELETE, ALTER, CONTROL ON OBJECT::dbo.TimeEntries TO [svc_hierarchy_reader];
GO


/* ---------------------------------------------------------------------------
   Variant B: Entra ID  (SqlAuthMode = EntraId)
   Azure SQL, or a SQL Server enabled for Entra authentication. The principal is
   the app registration itself, so the same certificate authenticates to Graph
   and to the database.
--------------------------------------------------------------------------- */

-- USE [Ops];
-- GO
-- CREATE USER [sql-hierarchy-push] FROM EXTERNAL PROVIDER;   -- app registration display name
-- GO
-- GRANT SELECT ON OBJECT::dbo.vwExternalItems   TO [sql-hierarchy-push];
-- GRANT SELECT ON OBJECT::dbo.vwCustomerItems   TO [sql-hierarchy-push];
-- GRANT SELECT ON OBJECT::dbo.vwEngagementItems TO [sql-hierarchy-push];
-- GRANT SELECT ON OBJECT::dbo.vwTimeEntryItems  TO [sql-hierarchy-push];
-- GO


/* ---------------------------------------------------------------------------
   Variant C: SQL login  (SqlAuthMode = SqlLogin)
   Last resort. The password is set out of band and stored as the Key Vault
   secret named in KeyVault:Secrets:SqlPassword — never here, and never in
   appsettings.json. Set it with an ALTER LOGIN you type interactively.
--------------------------------------------------------------------------- */

-- USE [master];
-- GO
-- CREATE LOGIN [hierarchy_reader] WITH PASSWORD = N'<set this interactively, do not commit it>';
-- GO
-- USE [Ops];
-- GO
-- CREATE USER [hierarchy_reader] FOR LOGIN [hierarchy_reader];
-- GO
-- GRANT SELECT ON OBJECT::dbo.vwExternalItems   TO [hierarchy_reader];
-- GRANT SELECT ON OBJECT::dbo.vwCustomerItems   TO [hierarchy_reader];
-- GRANT SELECT ON OBJECT::dbo.vwEngagementItems TO [hierarchy_reader];
-- GRANT SELECT ON OBJECT::dbo.vwTimeEntryItems  TO [hierarchy_reader];
-- GO


-- ---------------------------------------------------------------------------
-- Verification. Expect exactly four rows, all SELECT, all on views, and no row
-- naming a base table.
-- ---------------------------------------------------------------------------
SELECT  pr.name          AS principal_name,
        o.name           AS object_name,
        o.type_desc      AS object_type,
        pe.permission_name,
        pe.state_desc
FROM    sys.database_permissions AS pe
JOIN    sys.database_principals  AS pr ON pr.principal_id = pe.grantee_principal_id
JOIN    sys.objects              AS o  ON o.object_id = pe.major_id
WHERE   pr.name IN (N'svc_hierarchy_reader', N'sql-hierarchy-push', N'hierarchy_reader')
ORDER BY pr.name, o.name, pe.permission_name;
GO
