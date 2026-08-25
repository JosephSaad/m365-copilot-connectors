-- ===========================================================================
-- 01-least-privilege.sql
--
-- Creates the principal the connector reads dbo.Tickets with, and grants it
-- SELECT on that table and nothing else.
--
-- Run as a member of sysadmin or securityadmin, once per environment, against
-- the instance named in DataSource:Server.
--
-- Three variants are included. Use the one that matches DataSource:SqlAuthMode
-- in appsettings.json and delete the others rather than running all three.
-- ===========================================================================

/* ---------------------------------------------------------------------------
   Variant A: Windows integrated authentication  (SqlAuthMode = WindowsIntegrated)

   The connector service runs as a domain service account and carries no
   credential of its own. This is the configuration shipped in appsettings.json.
   Replace CONTOSO\svc_gca_reader with the account the Windows service runs as.
--------------------------------------------------------------------------- */

USE [master];
GO

IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'CONTOSO\svc_gca_reader')
BEGIN
    CREATE LOGIN [CONTOSO\svc_gca_reader] FROM WINDOWS;
END
GO

USE [Ops];
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'svc_gca_reader')
BEGIN
    CREATE USER [svc_gca_reader] FOR LOGIN [CONTOSO\svc_gca_reader];
END
GO

-- SELECT on one table. No db_datareader, no schema level grant, no EXECUTE.
GRANT SELECT ON OBJECT::dbo.Tickets TO [svc_gca_reader];
GO

-- Explicitly deny the rest of the surface, so a future ALTER ROLE cannot widen
-- access by accident. CONTROL is deliberately NOT in this list: DENY CONTROL on
-- an object denies every permission it implies - including the SELECT granted
-- above - so it would silently block every crawl while the GRANT row suggests
-- access is configured.
DENY INSERT, UPDATE, DELETE, ALTER ON OBJECT::dbo.Tickets TO [svc_gca_reader];
GO


/* ---------------------------------------------------------------------------
   Variant B: Entra ID authentication  (SqlAuthMode = EntraId)

   Only applicable to Azure SQL or a SQL Server enabled for Entra authentication.
   The connector presents an access token obtained with its client certificate,
   so the database principal is created from the Entra application, external.
--------------------------------------------------------------------------- */

-- USE [Ops];
-- GO
-- CREATE USER [sql-tickets-connector] FROM EXTERNAL PROVIDER;   -- app registration display name
-- GO
-- GRANT SELECT ON OBJECT::dbo.Tickets TO [sql-tickets-connector];
-- GO
-- DENY INSERT, UPDATE, DELETE, ALTER ON OBJECT::dbo.Tickets TO [sql-tickets-connector];
-- GO


/* ---------------------------------------------------------------------------
   Variant C: SQL login  (SqlAuthMode = SqlLogin, last resort)

   The password is generated here, stored in Key Vault under the name in
   KeyVault:Secrets:SqlPassword, and never written to a file, a script or a
   deployment variable. Generate it in the vault first, then paste it into this
   session interactively; do not commit the value.
--------------------------------------------------------------------------- */

-- USE [master];
-- GO
-- CREATE LOGIN [svc_gca_reader] WITH PASSWORD = N'<paste from Key Vault, do not commit>',
--     CHECK_POLICY = ON, CHECK_EXPIRATION = ON;
-- GO
-- USE [Ops];
-- GO
-- CREATE USER [svc_gca_reader] FOR LOGIN [svc_gca_reader];
-- GO
-- GRANT SELECT ON OBJECT::dbo.Tickets TO [svc_gca_reader];
-- GO
-- DENY INSERT, UPDATE, DELETE, ALTER ON OBJECT::dbo.Tickets TO [svc_gca_reader];
-- GO


/* ---------------------------------------------------------------------------
   Verification. Run after whichever variant you used. Expect: one GRANT row
   (SELECT on dbo.Tickets), the DENY rows created above (four verbs), and the
   database-level CONNECT that CREATE USER granted implicitly. Any OTHER grant
   row is the finding to investigate.
--------------------------------------------------------------------------- */

USE [Ops];
GO

SELECT  dp.name              AS principal_name,
        dp.type_desc         AS principal_type,
        perm.permission_name,
        perm.state_desc,
        OBJECT_NAME(perm.major_id) AS object_name
FROM    sys.database_permissions AS perm
JOIN    sys.database_principals  AS dp ON dp.principal_id = perm.grantee_principal_id
WHERE   dp.name = N'svc_gca_reader'
ORDER BY object_name, perm.permission_name;
GO
