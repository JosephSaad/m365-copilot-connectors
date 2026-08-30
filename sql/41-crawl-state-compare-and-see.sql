-- ===========================================================================
-- 41-crawl-state-compare-and-see.sql
--
-- Adds crawl.uspCompareAndSee: one round trip that decides what changed AND
-- records what did not.
--
-- WHAT IT REPLACES. Every window of rows used to cost at least two calls:
-- uspGetItemState, which returned a full row for every candidate so the
-- connector could compare hashes in memory, and then uspRecordUnchanged per
-- write chunk to mark the untouched ones seen. On a 111,900-row crawl that was
-- 560 lookups returning 111,900 rows, plus 5,595 recording calls.
--
-- This folds both into one. The comparison happens where the data already is,
-- so the procedure returns ONLY the items that need writing - on a steady-state
-- corpus that is a handful of rows instead of the whole corpus - and marks the
-- rest seen in the same statement.
--
-- WHY MARKING SEEN HERE IS SAFE, which is the only interesting question.
-- "Seen" answers exactly one question: did the source still return this item
-- this run? The delete sweep diffs on it, and an item missed becomes an item
-- deleted from the index. An UNCHANGED item needs nothing from Graph, so the
-- moment its hashes match, the answer is already known and cannot be changed by
-- anything that happens later in the run.
--
-- The connector's per-chunk commit prefix exists to stop a failed write
-- UNDER-recording - four items landed, the fifth failed, and the four must
-- still be recorded or the next sweep deletes them. Marking unchanged items
-- seen earlier moves in the SAFE direction: more items seen, never fewer, and
-- only ever items the source demonstrably returned. A run that dies before
-- completing is recorded failed and no sweep runs at all.
--
-- WHAT IT DOES NOT DO. It does not record WRITES. A hash written before Graph
-- has confirmed the write means the next run sees the item as unchanged and
-- skips it, so one failure becomes an item that is permanently stale and
-- permanently invisible. uspRecordWritten still runs after the write returns,
-- and that ordering is not negotiable.
--
-- Run against ConnectorState AFTER sql/20-25. Idempotent.
-- ===========================================================================

USE [ConnectorState];
GO

-- ---------------------------------------------------------------------------
-- SET OPTIONS ARE STORED WITH THE MODULE, NOT SUPPLIED BY THE CALLER. sqlcmd
-- connects with QUOTED_IDENTIFIER OFF and SSMS with it ON; crawl.Item carries a
-- filtered index, and an UPDATE against it from a module created with the option
-- off is refused at EXECUTION, days after a deployment that reported success.
-- This procedure updates crawl.Item, so this is load-bearing here rather than
-- ceremonial. sql/30 checks the result.
-- ---------------------------------------------------------------------------
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
GO

CREATE OR ALTER PROCEDURE [crawl].[uspCompareAndSee]
    @ConnectionId NVARCHAR(64),
    @RunId        BIGINT,
    @Candidates   [crawl].[ItemStateList] READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- The run has to belong to the connection. Without this a mis-wired caller
    -- could stamp one connection's items with another connection's run id, and
    -- the damage would only appear at the next sweep - as deletions.
    IF NOT EXISTS (SELECT 1 FROM [crawl].[Run]
                   WHERE RunId = @RunId AND ConnectionId = @ConnectionId)
    BEGIN
        THROW 50041, 'uspCompareAndSee: RunId does not belong to this connection.', 1;
    END

    -- One statement, one transaction. The UPDATE and the SELECT below must see
    -- the same rows: a row that changes between them would either be marked seen
    -- and also returned for writing (harmless, one wasted write) or neither
    -- (an item silently dropped from this run, which the sweep would then
    -- delete). Only the second is dangerous, and this is what stops it.
    BEGIN TRANSACTION;

    -- 1. Unchanged: both hashes match and the row is live. Marked seen, streak
    --    incremented, nothing returned.
    --
    --    State = 1 matters. An item currently pending delete whose hashes match
    --    is NOT simply unchanged - it is an item the source has started
    --    returning again, and it has to go back through the write path to leave
    --    state 2. Treating it as unchanged here would leave it pending for ever
    --    while the source kept offering it.
    UPDATE  i
    SET     i.LastSeenRunId   = @RunId,
            i.UnchangedStreak = i.UnchangedStreak + 1
    FROM    [crawl].[Item] AS i
    INNER JOIN @Candidates AS c
            ON  c.ItemId = i.ItemId
    WHERE   i.ConnectionId = @ConnectionId
      AND   i.State        = 1
      AND   i.ContentHash  = c.ContentHash
      AND   i.AclHash      = c.AclHash;

    -- 2. Everything else: absent, hash-different, or not live. Returned for the
    --    caller to write.
    --
    --    NOT EXISTS against the same predicate as the UPDATE rather than a
    --    LEFT JOIN with an IS NULL test: the two have to agree exactly, and
    --    writing the condition once in a form that can be read beside the UPDATE
    --    is what keeps them agreeing.
    SELECT  c.ItemId
    FROM    @Candidates AS c
    WHERE   NOT EXISTS (
                SELECT  1
                FROM    [crawl].[Item] AS i
                WHERE   i.ConnectionId = @ConnectionId
                  AND   i.ItemId       = c.ItemId
                  AND   i.State        = 1
                  AND   i.ContentHash  = c.ContentHash
                  AND   i.AclHash      = c.AclHash);

    COMMIT TRANSACTION;
END
GO

PRINT 'crawl.uspCompareAndSee created or altered.';
GO

-- ---------------------------------------------------------------------------
-- The grant. CREATE OR ALTER keeps an existing one, but this procedure is new,
-- so there is nothing to keep. Guarded on the role existing: sql/25 is optional
-- on a rig that runs everything as one account.
-- ---------------------------------------------------------------------------

IF DATABASE_PRINCIPAL_ID(N'crawl_writer') IS NOT NULL
BEGIN
    GRANT EXECUTE ON OBJECT::[crawl].[uspCompareAndSee] TO [crawl_writer];
    PRINT 'Granted EXECUTE on uspCompareAndSee to crawl_writer.';
END
ELSE
BEGIN
    PRINT 'Role crawl_writer does not exist, so nothing to grant. Run sql/25 if you expected it.';
END
GO

-- ---------------------------------------------------------------------------
-- Verification.
-- ---------------------------------------------------------------------------

SELECT  N'uspCompareAndSee exists' AS check_name,
        CASE WHEN OBJECT_ID(N'crawl.uspCompareAndSee', N'P') IS NOT NULL
             THEN N'OK' ELSE N'FAIL' END AS verdict

UNION ALL

SELECT  N'it was created with QUOTED_IDENTIFIER ON',
        CASE WHEN (SELECT m.uses_quoted_identifier FROM sys.sql_modules m
                   WHERE m.object_id = OBJECT_ID(N'crawl.uspCompareAndSee')) = 1
             THEN N'OK' ELSE N'FAIL - it updates crawl.Item and will be refused at execution' END

UNION ALL

SELECT  N'crawl_writer can execute it',
        CASE WHEN DATABASE_PRINCIPAL_ID(N'crawl_writer') IS NULL THEN N'n/a - role not present'
             WHEN EXISTS (SELECT 1 FROM sys.database_permissions p
                          JOIN sys.objects o ON o.object_id = p.major_id
                          WHERE o.name = N'uspCompareAndSee'
                            AND p.permission_name = N'EXECUTE' AND p.state = N'G'
                            AND p.grantee_principal_id = DATABASE_PRINCIPAL_ID(N'crawl_writer'))
             THEN N'OK' ELSE N'FAIL' END;
GO
