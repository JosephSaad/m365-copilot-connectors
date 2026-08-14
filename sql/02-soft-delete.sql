-- ===========================================================================
-- 02-soft-delete.sql
--
-- Adds the soft delete column the incremental crawl needs, and the index the
-- composite watermark reads.
--
-- Without IsDeleted the connector cannot report a deletion between full crawls:
-- a row that disappears stays in the Copilot index until the next periodic full
-- crawl removes it. Set DataSource:SoftDeleteEnabled to false only if this
-- migration cannot be applied, and accept that gap.
--
-- Run once per environment, before enabling the connection.
-- ===========================================================================

USE [Ops];
GO

-- 1. The delete marker. NOT NULL with a default so existing rows are live.
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.Tickets') AND name = N'IsDeleted')
BEGIN
    ALTER TABLE dbo.Tickets
        ADD IsDeleted BIT NOT NULL CONSTRAINT DF_Tickets_IsDeleted DEFAULT (0);
END
GO

-- 2. The crawl reads in (LastModified, TicketId) order and resumes from a
--    composite watermark, so this index is what keeps an incremental crawl a
--    seek rather than a scan. TicketId is the clustered key, so it is present
--    in the leaf either way; naming it keeps the intent obvious to a DBA.
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.Tickets') AND name = N'IX_Tickets_LastModified_TicketId')
BEGIN
    CREATE NONCLUSTERED INDEX IX_Tickets_LastModified_TicketId
        ON dbo.Tickets (LastModified, TicketId)
        INCLUDE (IsDeleted);
END
GO

-- 3. The older single column index is redundant once the composite one exists.
IF EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.Tickets') AND name = N'IX_Tickets_LastModified')
BEGIN
    DROP INDEX IX_Tickets_LastModified ON dbo.Tickets;
END
GO

/* ---------------------------------------------------------------------------
   How the application must delete tickets from now on.

   A hard DELETE is invisible to the incremental crawl. Soft delete instead, and
   touch LastModified so the row is picked up:

       UPDATE dbo.Tickets
       SET    IsDeleted = 1,
              LastModified = SYSUTCDATETIME()
       WHERE  TicketId = @TicketId;

   The connector then emits an IncrementalCrawlItem of type DeletedItem for that
   row, and the agent removes it from the index. Rows with IsDeleted = 1 are
   excluded from full crawls, so they are removed by that route as well.

   Purging tombstones: only delete rows with IsDeleted = 1 once you are certain
   a crawl has run since they were marked. A row purged before the crawl sees it
   stays in the index until the next full crawl.
--------------------------------------------------------------------------- */

-- Verification: the column, its default and the index all exist.
SELECT  c.name AS column_name, t.name AS data_type, c.is_nullable
FROM    sys.columns AS c
JOIN    sys.types   AS t ON t.user_type_id = c.user_type_id
WHERE   c.object_id = OBJECT_ID(N'dbo.Tickets') AND c.name = N'IsDeleted';

SELECT  name AS index_name, type_desc
FROM    sys.indexes
WHERE   object_id = OBJECT_ID(N'dbo.Tickets');
GO
