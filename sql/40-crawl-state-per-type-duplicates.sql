-- ===========================================================================
-- 40-crawl-state-per-type-duplicates.sql
--
-- Adds ItemsDuplicate to crawl.RunItemType, so the per-kind breakdown counts
-- duplicates the way the run row already does.
--
-- WHAT THIS FIXES. crawl.Run.ItemsDuplicate has always been recorded, and a
-- non-zero value there is a real finding: the PUT is an upsert, so a repeated
-- item ID silently overwrites the earlier row while the count claims both. What
-- the run row cannot say is WHICH KIND of thing repeated - and that is the whole
-- question, because "three customers repeated" and "three time entries
-- repeated" point at completely different joins in the source query. The
-- drill-down page shows every other counter per kind and stopped at this one.
--
-- WHY THIS SCRIPT IS LONGER THAN THE COLUMN IT ADDS. crawl.ItemTypeCountList is
-- a TABLE TYPE, and a table type cannot be altered - it can only be dropped and
-- recreated. It cannot be dropped while any procedure references it, so
-- uspRecordRunItemTypes has to go first. And DROP...CREATE loses permissions
-- where CREATE OR ALTER keeps them, so the grants sql/25 issued to crawl_writer
-- on BOTH the procedure and the type are destroyed by this script and have to be
-- put back by it. A migration that adds a column and silently removes the push
-- identity's ability to record anything is worse than no migration.
--
-- The re-grant is guarded on the role existing: sql/25 is optional on a
-- single-machine rig, and a script that fails because a role it did not create
-- is absent would be its own kind of wrong. In the documented fresh-deployment
-- order this script runs BEFORE sql/25, so the role is usually absent here and
-- sql/25 issues both grants itself. The re-grant is what matters on an upgrade,
-- where sql/25 ran long ago and nothing else would put them back.
--
-- Run against ConnectorState AFTER sql/20-23 and BEFORE sql/24, which selects
-- the column this file adds and will not compile without it. On an upgrade,
-- where sql/24 and sql/25 have long since run, run this and then re-run sql/24.
-- Idempotent. Verification at the foot, including an explicit check that the
-- grants came back.
-- ===========================================================================

USE [ConnectorState];
GO

-- ---------------------------------------------------------------------------
-- SET OPTIONS ARE STORED WITH THE MODULE, NOT SUPPLIED BY THE CALLER.
--
-- SQL Server records QUOTED_IDENTIFIER as it stands in THIS session at CREATE
-- time and replays that stored setting on every execution, whatever the caller
-- has set. sqlcmd connects with it OFF; SSMS connects with it ON. crawl.Item
-- carries a filtered index, and any UPDATE against a table with one is refused
-- unless this was ON when the module was created - at EXECUTION, days after a
-- deployment that reported success. sql/30 checks the result.
-- ---------------------------------------------------------------------------
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
GO

-- ---------------------------------------------------------------------------
-- 1. The column.
--
-- Guarded, so re-running is safe, and NOT NULL with a default of 0 so every row
-- already recorded reads as "no duplicates of this kind" rather than NULL. NULL
-- would be honest about not having measured it, but it would also make every
-- historical row render as blank on the drill-down page, and a blank in a column
-- of numbers reads as a bug in the page rather than as an older schema.
-- ---------------------------------------------------------------------------

IF COL_LENGTH(N'crawl.RunItemType', N'ItemsDuplicate') IS NULL
BEGIN
    ALTER TABLE [crawl].[RunItemType]
        ADD ItemsDuplicate INT NOT NULL
            CONSTRAINT DF_RunItemType_Duplicate DEFAULT (0);

    PRINT 'crawl.RunItemType.ItemsDuplicate added.';
END
ELSE
BEGIN
    PRINT 'crawl.RunItemType.ItemsDuplicate already present.';
END
GO

-- ---------------------------------------------------------------------------
-- 2. The table type, which means dropping the procedure that uses it first.
--
-- Order matters and is not negotiable: DROP PROCEDURE, DROP TYPE, CREATE TYPE,
-- CREATE PROCEDURE, re-grant. Attempting the type first fails with "cannot drop
-- because it is being referenced", which is a clear message but leaves the
-- script half applied if it is not the first statement to run.
-- ---------------------------------------------------------------------------

IF TYPE_ID(N'crawl.ItemTypeCountList') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.table_types tt
                   JOIN sys.columns c ON c.object_id = tt.type_table_object_id
                   WHERE tt.name = N'ItemTypeCountList' AND c.name = N'ItemsDuplicate')
BEGIN
    PRINT 'Recreating crawl.ItemTypeCountList with ItemsDuplicate.';

    IF OBJECT_ID(N'crawl.uspRecordRunItemTypes', N'P') IS NOT NULL
    BEGIN
        DROP PROCEDURE [crawl].[uspRecordRunItemTypes];
    END

    DROP TYPE [crawl].[ItemTypeCountList];
END
GO

IF TYPE_ID(N'crawl.ItemTypeCountList') IS NULL
BEGIN
    CREATE TYPE [crawl].[ItemTypeCountList] AS TABLE
    (
        ItemType       NVARCHAR(64) NOT NULL,
        ItemsWritten   INT          NOT NULL,
        ItemsUnchanged INT          NOT NULL,
        ItemsDeleted   INT          NOT NULL,
        ItemsSkipped   INT          NOT NULL,
        ItemsFailed    INT          NOT NULL,

        -- Appended last, matching how sql/12's SourceId was added: a column in
        -- the middle would silently renumber every ordinal a caller binds by
        -- position, and SqlDataRecord binds by position.
        ItemsDuplicate INT          NOT NULL,

        BytesWritten   BIGINT       NOT NULL,
        PRIMARY KEY CLUSTERED (ItemType)
    );

    PRINT 'crawl.ItemTypeCountList created.';
END
GO

-- ---------------------------------------------------------------------------
-- 3. The procedure.
--
-- Body copied from sql/23 with the one column threaded through. If sql/23 is
-- ever re-run after this script it will put its own older body back and the
-- column will stop being recorded, silently - the same standing hazard sql/28,
-- sql/29 and sql/33 carry. The verification below is what catches it.
-- ---------------------------------------------------------------------------

CREATE OR ALTER PROCEDURE [crawl].[uspRecordRunItemTypes]
    @RunId  BIGINT,
    @Counts [crawl].[ItemTypeCountList] READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- MERGE rather than DELETE-then-INSERT: a run that reports twice, which a
    -- retry of the closing sequence would do, must not lose the first report's
    -- rows for kinds the second did not mention.
    MERGE [crawl].[RunItemType] AS target
    USING @Counts AS source
        ON target.RunId = @RunId AND target.ItemType = source.ItemType
    WHEN MATCHED THEN
        UPDATE SET ItemsWritten   = source.ItemsWritten,
                   ItemsUnchanged = source.ItemsUnchanged,
                   ItemsDeleted   = source.ItemsDeleted,
                   ItemsSkipped   = source.ItemsSkipped,
                   ItemsFailed    = source.ItemsFailed,
                   ItemsDuplicate = source.ItemsDuplicate,
                   BytesWritten   = source.BytesWritten
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (RunId, ItemType, ItemsWritten, ItemsUnchanged, ItemsDeleted,
                ItemsSkipped, ItemsFailed, ItemsDuplicate, BytesWritten)
        VALUES (@RunId, source.ItemType, source.ItemsWritten, source.ItemsUnchanged,
                source.ItemsDeleted, source.ItemsSkipped, source.ItemsFailed,
                source.ItemsDuplicate, source.BytesWritten);
END
GO

-- ---------------------------------------------------------------------------
-- 4. Put the grants back.
--
-- DROP destroyed them. Both of them: the procedure's EXECUTE and the TYPE's
-- EXECUTE, which is the one that is easy to forget because a table type having
-- a permission at all is unusual. Without it the push identity gets "The
-- EXECUTE permission was denied on the object 'ItemTypeCountList'" at the end of
-- every run, after the crawl has already done all its work.
--
-- Guarded on the role existing, because sql/25 is optional on a rig that runs
-- everything as one account.
-- ---------------------------------------------------------------------------

IF DATABASE_PRINCIPAL_ID(N'crawl_writer') IS NOT NULL
BEGIN
    GRANT EXECUTE ON OBJECT::[crawl].[uspRecordRunItemTypes] TO [crawl_writer];
    GRANT EXECUTE ON TYPE::[crawl].[ItemTypeCountList]       TO [crawl_writer];

    PRINT 'Re-granted EXECUTE on uspRecordRunItemTypes and ItemTypeCountList to crawl_writer.';
END
ELSE
BEGIN
    PRINT 'Role crawl_writer does not exist, so nothing to re-grant. Run sql/25 if you expected it.';
END
GO

-- ---------------------------------------------------------------------------
-- 5. Verification.
--
-- The grant checks are the ones worth reading. Everything above can succeed
-- while leaving the push identity unable to close a run.
-- ---------------------------------------------------------------------------

SELECT  N'RunItemType has ItemsDuplicate' AS check_name,
        CASE WHEN COL_LENGTH(N'crawl.RunItemType', N'ItemsDuplicate') IS NOT NULL
             THEN N'OK' ELSE N'FAIL' END AS verdict

UNION ALL

SELECT  N'ItemTypeCountList has ItemsDuplicate',
        CASE WHEN EXISTS (SELECT 1 FROM sys.table_types tt
                          JOIN sys.columns c ON c.object_id = tt.type_table_object_id
                          WHERE tt.name = N'ItemTypeCountList' AND c.name = N'ItemsDuplicate')
             THEN N'OK' ELSE N'FAIL' END

UNION ALL

SELECT  N'uspRecordRunItemTypes writes ItemsDuplicate',
        CASE WHEN OBJECT_DEFINITION(OBJECT_ID(N'crawl.uspRecordRunItemTypes')) LIKE N'%ItemsDuplicate%'
             THEN N'OK' ELSE N'FAIL - sql/23 may have been re-run over this' END

UNION ALL

-- Absent and empty are different. If the role does not exist there is nothing
-- to check, and that must not read the same as a grant being in place.
SELECT  N'crawl_writer can execute the procedure',
        CASE WHEN DATABASE_PRINCIPAL_ID(N'crawl_writer') IS NULL THEN N'n/a - role not present'
             WHEN EXISTS (SELECT 1 FROM sys.database_permissions p
                          JOIN sys.objects o ON o.object_id = p.major_id
                          WHERE o.name = N'uspRecordRunItemTypes'
                            AND p.permission_name = N'EXECUTE'
                            AND p.state = N'G'
                            AND p.grantee_principal_id = DATABASE_PRINCIPAL_ID(N'crawl_writer'))
             THEN N'OK' ELSE N'FAIL - re-run section 4 or sql/25' END

UNION ALL

SELECT  N'crawl_writer can execute the table type',
        CASE WHEN DATABASE_PRINCIPAL_ID(N'crawl_writer') IS NULL THEN N'n/a - role not present'
             WHEN EXISTS (SELECT 1 FROM sys.database_permissions p
                          WHERE p.class_desc = N'TYPE'
                            AND p.major_id = TYPE_ID(N'crawl.ItemTypeCountList')
                            AND p.permission_name = N'EXECUTE'
                            AND p.state = N'G'
                            AND p.grantee_principal_id = DATABASE_PRINCIPAL_ID(N'crawl_writer'))
             THEN N'OK' ELSE N'FAIL - re-run section 4 or sql/25' END;
GO
