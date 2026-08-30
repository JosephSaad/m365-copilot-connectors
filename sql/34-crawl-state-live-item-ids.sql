-- ===========================================================================
-- 34-crawl-state-live-item-ids.sql
--
-- Adds crawl.uspListLiveItemIds: every live item ID for one connection.
--
-- WHY A DRY RUN NEEDS THIS. A dry run can already say what it would write and
-- what it would skip, because it can ask the store about the rows it just read.
-- It could not say what the delete sweep would REMOVE, and that is the half of
-- a preview an operator actually loses sleep over: writes are additive and a
-- wrong one is corrected next run, while a sweep takes items out of the index
-- and a search stops answering.
--
-- uspGetPendingDeletes cannot answer it. That procedure MUTATES - it moves rows
-- to state 2 and stamps PendingSinceUtc - and it returns nothing at all on a dry
-- run, for a good reason stated in its own body: a dry run records no item
-- state, so every item would look unseen and the whole corpus would be marked
-- pending. Asking it in preview mode would either corrupt the store or answer
-- "none", and "none" is the most dangerous possible wrong answer here.
--
-- So the preview is computed the other way round: the engine holds the IDs the
-- source yielded this run, this procedure returns the IDs the index holds, and
-- the difference is what a real sweep would delete. Read-only, one SELECT, no
-- transaction.
--
-- ON RETURNING THE WHOLE SET. For 111,900 items this is roughly 1.5 MB of IDs
-- and about a second. That is affordable because a dry run is something a person
-- waits for on purpose, and the alternative - paging, or a server-side diff
-- against a table-valued parameter of everything just read - is more machinery
-- for a path that runs when somebody is watching it.
--
-- Run against ConnectorState. Idempotent.
-- ===========================================================================

USE [ConnectorState];
GO

-- ---------------------------------------------------------------------------
-- SET OPTIONS ARE STORED WITH THE MODULE, NOT SUPPLIED BY THE CALLER.
--
-- SQL Server records QUOTED_IDENTIFIER as it stands in THIS session at CREATE
-- time and replays that stored setting every time the module runs, ignoring
-- whatever the caller has set. sqlcmd connects with it OFF; SSMS connects with
-- it ON. crawl.Item carries a filtered index, and any UPDATE against a table
-- carrying one is refused unless QUOTED_IDENTIFIER was ON at CREATE time. The
-- refusal lands at EXECUTION, not deployment.
--
-- Verify with sys.sql_modules.uses_quoted_identifier; sql/30 checks it.
-- ---------------------------------------------------------------------------
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
GO

CREATE OR ALTER PROCEDURE [crawl].[uspListLiveItemIds]
    @ConnectionId NVARCHAR(128)
AS
BEGIN
    SET NOCOUNT ON;

    -- State 1 only. An item already pending delete (2) is not something a
    -- preview should report as newly deleted - it is reported by the sweep
    -- itself as a retry, and counting it here would show the same item twice
    -- to somebody trying to decide whether the number is alarming.
    --
    -- Ordered so two previews of an unchanged corpus produce identical output.
    -- A diff that reorders itself between runs cannot be diffed.
    SELECT      ItemId
    FROM        [crawl].[Item]
    WHERE       ConnectionId = @ConnectionId
      AND       State = 1
    ORDER BY    ItemId;
END
GO

PRINT 'crawl.uspListLiveItemIds created or altered.';
GO

-- ---------------------------------------------------------------------------
-- Verification. The count must match vwItemInventory's live count for the same
-- connection; if it does not, one of the two is filtering on something the
-- other is not, and the preview would understate or overstate a deletion.
--
-- Note this reports per connection rather than asserting a single number: an
-- empty result here means "no connections", which is a different thing from
-- "the procedure agrees", and the two must not look alike.
-- ---------------------------------------------------------------------------

DECLARE @Connections int = (SELECT COUNT(*) FROM [crawl].[Connection]);

IF @Connections = 0
BEGIN
    PRINT 'No connections registered, so nothing to verify against. Not a pass.';
END
ELSE
BEGIN
    SELECT  c.ConnectionId,
            (SELECT COUNT(*) FROM [crawl].[Item] i
             WHERE i.ConnectionId = c.ConnectionId AND i.State = 1) AS LiveItems
    FROM    [crawl].[Connection] AS c
    ORDER BY c.ConnectionId;
END
GO
