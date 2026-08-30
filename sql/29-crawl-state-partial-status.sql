-- ===========================================================================
-- 29-crawl-state-partial-status.sql
--
-- Adds run status 5, "partial": the run finished, and some items did not.
--
-- THE STATE THAT HAD NOWHERE TO GO. A run either threw, and was recorded
-- failed, or it did not, and was recorded succeeded. There was no third
-- outcome - so a run that wrote 110,590 items and had 191 refused by Graph was
-- stored as a success, because it completed. Nothing in the store, the views,
-- the health word or the failed-runs tile distinguished it from a clean run.
--
-- That is not a display problem. The items are absent from the index, and a
-- failed write records no hash, so nothing about the corpus looks wrong
-- afterwards either. Succeeded was the one word guaranteed to stop anybody
-- looking.
--
-- WHY NOT JUST MARK IT FAILED. Because that is wrong in the other direction and
-- differently expensive. Failed means the run died: no totals, an ErrorKind, a
-- crawl that has to be repeated in full. A partial run completed, recorded
-- every hash it earned, and needs only its refused items retried - which the
-- next run does by itself, since their hashes were never written. Reporting
-- those two as one word sends an operator to repeat a crawl that does not need
-- repeating, and hides that a clean-looking corpus has holes in it.
--
-- STATUS 5, NOT A RENUMBERING. Existing codes keep their meanings, so no row in
-- any deployed database changes and no stored query means something new. 1
-- running, 2 succeeded, 3 failed, 4 abandoned, 5 partial.
--
-- Run against ConnectorState AFTER re-running sql/22 and sql/23, which carry
-- the view wording and the procedure that now assigns it. Idempotent.
-- Verification block at the foot.
-- ===========================================================================

USE [ConnectorState];
GO

-- QUOTED_IDENTIFIER ON IS REQUIRED, NOT TIDINESS. sqlcmd connects with it OFF
-- while SSMS connects with it ON, so a script that works when pasted into a
-- query window fails from the command line with "the following SET options have
-- incorrect settings" - which reads as a permissions or syntax problem and is
-- neither. crawl.Item carries a filtered index, and any UPDATE touching a table
-- with one is refused unless this is ON. Found the first time this script was
-- run through sqlcmd, after the constraint had already been widened.
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
GO

-- ---------------------------------------------------------------------------
-- 1. Widen the constraint.
--
-- Dropped and recreated rather than altered - a CHECK constraint cannot be
-- modified in place. WITH CHECK on the way back in so the existing rows are
-- re-validated rather than trusted: a constraint recreated WITH NOCHECK is a
-- constraint the optimiser stops believing, and one nobody notices is untrusted
-- until a query plan quietly worsens.
-- ---------------------------------------------------------------------------

IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_Run_Status')
BEGIN
    ALTER TABLE [crawl].[Run] DROP CONSTRAINT CK_Run_Status;
END
GO

ALTER TABLE [crawl].[Run] WITH CHECK
    ADD CONSTRAINT CK_Run_Status CHECK (Status IN (1, 2, 3, 4, 5));
GO

PRINT 'CK_Run_Status now admits 5 (partial).';
GO

-- ---------------------------------------------------------------------------
-- 2. Reclassify the runs already on record.
--
-- A run stored as succeeded while having refused items was mis-stated when it
-- was written, and leaving it that way would mean the first partial run in the
-- history is the one nobody can find. Only status 2 is touched: a failed run
-- stays failed whatever its counters say, because it did not complete.
-- ---------------------------------------------------------------------------

UPDATE  [crawl].[Run]
SET     Status = 5
WHERE   Status = 2
  AND   ItemsFailed > 0;

PRINT CONCAT('Reclassified ', @@ROWCOUNT, ' completed run(s) that had refused items as partial.');
GO

-- ---------------------------------------------------------------------------
-- 3. Verification.
--
-- The second query is the one to read. A partial run must keep its totals -
-- that is the whole difference between it and a failure - so a partial row with
-- no ItemsWritten would mean this migration had relabelled the wrong thing.
-- ---------------------------------------------------------------------------

SELECT  Status,
        CASE Status WHEN 1 THEN N'running'   WHEN 2 THEN N'succeeded'
                    WHEN 3 THEN N'failed'    WHEN 4 THEN N'abandoned'
                    WHEN 5 THEN N'partial'   END AS Word,
        COUNT(*)          AS Runs,
        SUM(ItemsWritten) AS TotalWritten,
        SUM(ItemsFailed)  AS TotalFailed
FROM    [crawl].[Run]
GROUP BY Status
ORDER BY Status;
GO

SELECT  N'a partial run kept its totals' AS check_name,
        CASE WHEN NOT EXISTS (SELECT 1 FROM [crawl].[Run] WHERE Status = 5 AND ItemsWritten = 0)
             THEN N'OK' ELSE N'FAIL - a partial run with nothing written is a failure mislabelled' END AS verdict

UNION ALL

SELECT  N'no completed run still hides refused items',
        CASE WHEN NOT EXISTS (SELECT 1 FROM [crawl].[Run] WHERE Status = 2 AND ItemsFailed > 0)
             THEN N'OK' ELSE N'FAIL - re-run this script after sql/23' END;
GO
