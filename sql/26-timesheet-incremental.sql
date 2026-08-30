-- ===========================================================================
-- 26-timesheet-incremental.sql
--
-- Makes the three-level timesheet source readable INCREMENTALLY, by giving
-- every row a modification time that accounts for its ancestors.
--
-- THE PROBLEM THIS SOLVES, STATED PRECISELY.
--
-- SqlHierarchyPush flattens a hierarchy into flat index items: a time entry
-- carries its engagement's name and its customer's name, so that searching for
-- the customer finds the time entry. That denormalisation is the whole reason
-- the connector exists.
--
-- It also means a time entry's CORRECT indexed text depends on three rows. Rename
-- a customer and every one of that customer's time entries now holds a name that
-- no longer exists - but only dbo.Customers.LastModified moved. An incremental
-- crawl reading "rows changed since the checkpoint" would re-index one customer
-- and leave a thousand descendants stale, indefinitely, with nothing reporting
-- it. The connector cannot detect this: from its side the source simply did not
-- return those rows.
--
-- So the source has to expose a hierarchy-aware timestamp. That is what this
-- script adds: EffectiveLastModified, meaning "when did anything that affects
-- this row's indexed content last change".
--
-- WITHOUT THIS SCRIPT the connector still works correctly - it declares
-- SourceChangeDetection.Differencing, reads everything every run, and lets the
-- content hashes in ConnectorState decide what is actually written. That is
-- Tier 2 in docs/SOURCE-CONTRACT.md and it is genuinely fine up to the point
-- where the source read itself outgrows the crawl window. This script is what
-- moves the connector to Tier 1, where most runs read almost nothing.
--
-- Requires SQL Server 2022 or later, and only in section 4's backfill and the
-- section 6 snippet, both of which use GREATEST(). The columns, indexes,
-- triggers and view need nothing above 2016 - so an older source instance can
-- have all of those by rewriting three UPDATE statements in the MAX-over-VALUES
-- form. It is the only file in the set with any such requirement: sql/20
-- through sql/25 build the state database and need nothing above 2016.
--
-- Run once per environment, after sql/10 and sql/12. Independent of sql/20-25:
-- this changes the SOURCE database, those create the state database.
-- ===========================================================================

USE [Ops];
GO

-- ---------------------------------------------------------------------------
-- SET OPTIONS ARE STORED WITH THE MODULE, NOT SUPPLIED BY THE CALLER.
--
-- SQL Server records QUOTED_IDENTIFIER as it stands in THIS session at CREATE
-- time and replays that stored setting every time the module runs, ignoring
-- whatever the caller has set. sqlcmd connects with it OFF; SSMS connects with
-- it ON. The same script therefore yields a working module from a query window
-- and a broken one from the command line, and the deployment output is
-- identical either way.
--
-- Filtered indexes and indexed views are the usual trigger, and any UPDATE against a table carrying one is refused
-- unless QUOTED_IDENTIFIER was ON at CREATE time:
--   "UPDATE failed because the following SET options have incorrect settings"
-- The refusal lands at EXECUTION, not deployment. The deploy reports success,
-- and the failure surfaces later in an application that has not changed - which
-- is as far from the cause as this failure mode can put you.
--
-- Setting it here makes the stored setting independent of who ran the script.
-- Verify with sys.sql_modules.uses_quoted_identifier; sql/30 checks it.
-- ---------------------------------------------------------------------------
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO
/* ---------------------------------------------------------------------------
   1. The column.

   Persisted rather than computed on read, because the point of it is to be
   SEEKABLE. A view that computes the maximum of three joined columns is
   correct and cannot use an index - every incremental read would scan the whole
   hierarchy, which costs more than the full crawl it replaced. Section 6 has
   that fallback for an estate that cannot take the triggers in section 3.

   NOT NULL, and that is a correctness requirement rather than tidiness. A null
   here is invisible twice over: the incremental predicate is
   "EffectiveLastModified > @marker", which is UNKNOWN for a null and therefore
   never true, AND the triggers' recursion guard is
   "WHERE EffectiveLastModified < @Now", which is UNKNOWN too - so the trigger
   will not repair the row either. A row that acquired a null would be skipped
   by every incremental crawl for ever and found only by a full one. NOT NULL
   with a default removes the whole class, and populates existing rows on the
   ALTER rather than leaving them for the backfill.

   DATETIME2(3), matching crawl.Checkpoint.MarkerTime exactly, which the sibling
   LastModified column (precision 7) does not. Converting 7 to 3 rounds to
   NEAREST, so a marker taken from a (7) value can land AHEAD of the row it came
   from, and every row in between is then skipped permanently. The engine floors
   to whole milliseconds before saving to defend against this; matching the
   precision at the source means the comparison is exact rather than merely
   defended, which matters here because the triggers stamp an entire cascade
   with one value and same-timestamp groups are exactly what this source
   produces.
--------------------------------------------------------------------------- */

IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID(N'dbo.Customers') AND name = N'EffectiveLastModified')
BEGIN
    ALTER TABLE dbo.Customers ADD EffectiveLastModified DATETIME2(3) NOT NULL
        CONSTRAINT DF_Customers_ELM DEFAULT SYSUTCDATETIME();
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID(N'dbo.Engagements') AND name = N'EffectiveLastModified')
BEGIN
    ALTER TABLE dbo.Engagements ADD EffectiveLastModified DATETIME2(3) NOT NULL
        CONSTRAINT DF_Engagements_ELM DEFAULT SYSUTCDATETIME();
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID(N'dbo.TimeEntries') AND name = N'EffectiveLastModified')
BEGIN
    ALTER TABLE dbo.TimeEntries ADD EffectiveLastModified DATETIME2(3) NOT NULL
        CONSTRAINT DF_TimeEntries_ELM DEFAULT SYSUTCDATETIME();
END
GO

/* ---------------------------------------------------------------------------
   2. The indexes.

   Composite, and in this order, because the connector reads
   "WHERE EffectiveLastModified > @markerTime
      OR (EffectiveLastModified = @markerTime AND Id > @markerKey)
    ORDER BY EffectiveLastModified, Id"
   which is a seek on exactly this key. The tie-breaking key column is what makes
   the ordering total - two rows can share a timestamp to the millisecond, and a
   marker of only the timestamp either re-reads that whole group for ever or
   loses whichever of them had not been written when the run stopped.

   IsDeleted is included rather than keyed: the query filters on it but never
   ranges over it, so including it keeps the read covering without widening the
   key that every insert has to maintain.
--------------------------------------------------------------------------- */

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'dbo.Customers') AND name = N'IX_Customers_Effective')
BEGIN
    CREATE NONCLUSTERED INDEX IX_Customers_Effective
        ON dbo.Customers (EffectiveLastModified, CustomerId) INCLUDE (IsDeleted);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'dbo.Engagements') AND name = N'IX_Engagements_Effective')
BEGIN
    CREATE NONCLUSTERED INDEX IX_Engagements_Effective
        ON dbo.Engagements (EffectiveLastModified, EngagementId) INCLUDE (IsDeleted, CustomerId);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'dbo.TimeEntries') AND name = N'IX_TimeEntries_Effective')
BEGIN
    CREATE NONCLUSTERED INDEX IX_TimeEntries_Effective
        ON dbo.TimeEntries (EffectiveLastModified, TimeEntryId) INCLUDE (IsDeleted, EngagementId);
END
GO

/* ---------------------------------------------------------------------------
   3. The triggers that keep it true.

   Three rules, and the third is the one that matters:

     a customer changes    -> its own EffectiveLastModified moves
     an engagement changes -> its own moves
     an ANCESTOR changes   -> every descendant's moves

   Rule three is the expensive one and it is not optional. Renaming a customer
   with a thousand time entries updates a thousand rows, and that is correct:
   a thousand index items genuinely became stale at that moment. The alternative
   is not "a cheaper update", it is "a thousand wrong search results".

   NOT FOR REPLICATION is omitted deliberately - if this estate ever replicates
   Ops, the replica needs these timestamps too.

   Each trigger guards against recursion by only touching rows whose value would
   actually change. Without that guard, a customer update cascades to engagements,
   which cascades to time entries, which - if a future trigger ever wrote back up
   the tree - would loop.

   IMPORTANT: these triggers set EffectiveLastModified only. They deliberately do
   NOT touch LastModified, because that column means "when did THIS row change"
   and other things in the estate may depend on it. The two are different
   questions and this script adds the second rather than redefining the first.
--------------------------------------------------------------------------- */

CREATE OR ALTER TRIGGER dbo.trgCustomers_Effective
ON dbo.Customers
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT EXISTS (SELECT 1 FROM inserted) RETURN;

    DECLARE @Now DATETIME2 = SYSUTCDATETIME();

    -- The customer itself.
    UPDATE  c
    SET     c.EffectiveLastModified = @Now
    FROM    dbo.Customers AS c
    INNER JOIN inserted   AS i ON i.CustomerId = c.CustomerId
    WHERE   c.EffectiveLastModified < @Now;

    -- Its engagements.
    UPDATE  e
    SET     e.EffectiveLastModified = @Now
    FROM    dbo.Engagements AS e
    INNER JOIN inserted     AS i ON i.CustomerId = e.CustomerId
    WHERE   e.EffectiveLastModified < @Now;

    -- And their time entries. One statement rather than a cursor: a customer
    -- rename is a single set-based update however many descendants it has.
    UPDATE  te
    SET     te.EffectiveLastModified = @Now
    FROM    dbo.TimeEntries  AS te
    INNER JOIN dbo.Engagements AS e ON e.EngagementId = te.EngagementId
    INNER JOIN inserted        AS i ON i.CustomerId  = e.CustomerId
    WHERE   te.EffectiveLastModified < @Now;
END
GO

CREATE OR ALTER TRIGGER dbo.trgEngagements_Effective
ON dbo.Engagements
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT EXISTS (SELECT 1 FROM inserted) RETURN;

    DECLARE @Now DATETIME2 = SYSUTCDATETIME();

    UPDATE  e
    SET     e.EffectiveLastModified = @Now
    FROM    dbo.Engagements AS e
    INNER JOIN inserted     AS i ON i.EngagementId = e.EngagementId
    WHERE   e.EffectiveLastModified < @Now;

    UPDATE  te
    SET     te.EffectiveLastModified = @Now
    FROM    dbo.TimeEntries AS te
    INNER JOIN inserted     AS i ON i.EngagementId = te.EngagementId
    WHERE   te.EffectiveLastModified < @Now;
END
GO

CREATE OR ALTER TRIGGER dbo.trgTimeEntries_Effective
ON dbo.TimeEntries
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT EXISTS (SELECT 1 FROM inserted) RETURN;

    DECLARE @Now DATETIME2 = SYSUTCDATETIME();

    UPDATE  te
    SET     te.EffectiveLastModified = @Now
    FROM    dbo.TimeEntries AS te
    INNER JOIN inserted     AS i ON i.TimeEntryId = te.TimeEntryId
    WHERE   te.EffectiveLastModified < @Now;
END
GO

/* ---------------------------------------------------------------------------
   4. Backfill.

   Sets every existing row to the maximum of its own and its ancestors'
   LastModified, which is the value the triggers would have produced had they
   always existed.

   THE TRIGGERS ARE DISABLED AROUND THIS SECTION, and without that none of it
   works. Section 3 created them; an UPDATE here would fire them, they would
   immediately overwrite the historical value with SYSUTCDATETIME() and cascade
   that to every descendant, and the whole corpus would end up stamped with the
   moment the script ran. The ancestors-before-descendants ordering below would
   be pointless, and - worse - re-running this file would re-stamp everything
   and make the NEXT incremental crawl read the entire source. Disabled, the
   backfill writes the values the comments describe.

   The WHERE clauses make it idempotent as well, so a re-run corrects only rows
   that are actually wrong. Both guards are needed: the disable stops the
   cascade, the predicate stops the rewrite.

   GREATEST() is used throughout. It arrived in SQL Server 2022 and this estate
   is on 2022, so the older MAX-over-VALUES workaround would be noise. Nothing
   else in sql/20 through sql/26 needs a version above 2016, so 2022 is a
   requirement of THIS file rather than of the state store - if the source
   database is ever older than the state database, this is the file that fails.

   Run in this order - ancestors before descendants - so each level reads
   already-corrected values from the level above.
--------------------------------------------------------------------------- */

DISABLE TRIGGER dbo.trgCustomers_Effective   ON dbo.Customers;
DISABLE TRIGGER dbo.trgEngagements_Effective ON dbo.Engagements;
DISABLE TRIGGER dbo.trgTimeEntries_Effective ON dbo.TimeEntries;
GO

-- All three in ONE transaction, and one batch. Separate batches would let a
-- failure in the middle statement - a lock timeout on a large Engagements table
-- is the realistic one - leave the sequence half applied while the batches after
-- it carried on regardless: the time-entry update would run against partly
-- corrected ancestors, and the deployment would look finished with descendants
-- behind their parents. XACT_ABORT ON rolls the whole thing back instead, and
-- the ENABLE TRIGGER batch below still runs, so a failed backfill leaves the
-- source exactly as it was found with its triggers live.
SET XACT_ABORT ON;

BEGIN TRANSACTION;

UPDATE  dbo.Customers
SET     EffectiveLastModified = LastModified
WHERE   EffectiveLastModified <> LastModified;

UPDATE  e
SET     e.EffectiveLastModified = GREATEST(e.LastModified, c.EffectiveLastModified)
FROM    dbo.Engagements AS e
INNER JOIN dbo.Customers AS c ON c.CustomerId = e.CustomerId
WHERE   e.EffectiveLastModified <> GREATEST(e.LastModified, c.EffectiveLastModified);

UPDATE  te
SET     te.EffectiveLastModified = GREATEST(te.LastModified, e.EffectiveLastModified)
FROM    dbo.TimeEntries   AS te
INNER JOIN dbo.Engagements AS e ON e.EngagementId = te.EngagementId
WHERE   te.EffectiveLastModified <> GREATEST(te.LastModified, e.EffectiveLastModified);

COMMIT TRANSACTION;
GO

-- Unconditional on purpose. Whether the backfill committed or rolled back, the
-- triggers must come back on: leaving them disabled is the one outcome that
-- fails silently, because the source would keep accepting writes while quietly
-- no longer maintaining the column every incremental crawl depends on.
ENABLE TRIGGER dbo.trgCustomers_Effective   ON dbo.Customers;
ENABLE TRIGGER dbo.trgEngagements_Effective ON dbo.Engagements;
ENABLE TRIGGER dbo.trgTimeEntries_Effective ON dbo.TimeEntries;
GO

/* ---------------------------------------------------------------------------
   5. The view the connector reads.

   Same shape as dbo.vwExternalItems in sql/12, with EffectiveLastModified
   added. The connector selects from this one when Settings:Incremental is on.

   THE JOIN IS ON THE NUMERIC KEY. Each branch joins a sql/12 view back to its
   base table on SourceId, which sql/12 projects for exactly this purpose: the
   integer primary key the item's ItemId was concatenated from.

   It used to join on the constructed string -
   N'cust' + CAST(c.CustomerId AS NVARCHAR(32)) = v.ItemId - because the sql/12
   views projected the composed ItemId and not the key behind it. That
   comparison is not sargable: the plan built the string on BOTH sides with a
   Compute Scalar and then had nothing to seek with, so each branch scanned its
   base table whole and joined on text.

   MEASURED, and the honest number rather than the flattering one. A/B on the
   111,900-item corpus, swapping only this view's definition, at a fixed marker
   returning 293 items, with a typed DATETIME2(3) marker and OPTION (RECOMPILE)
   so the optimiser sees the value a sniffed parameter would:

                    constructed string      SourceId
     TimeEntries              12,069          6,994
     Engagements               1,308            661
     Customers                   117            117
     ------------------------------------------------
     base tables              13,494          7,772   (-42%)
     Worktable               271,194        271,194   (unchanged)

   The plan improves in the two upper branches outright: the customer branch is
   now an Index Seek on IX_Customers_Effective feeding a Clustered Index Seek on
   PK_Customers, where it was two Compute Scalars and a loop join on text, and
   the engagement branch is a Merge Join on EngagementId where it was a Hash
   Match on two built strings.

   WHAT THIS DID NOT FIX, said plainly. The time-entry branch still hash-joins
   the delta seek to a Clustered Index Scan of PK_TimeEntries. That is now the
   optimiser's costing choice rather than a consequence of the predicate - the
   join is an integer equality and could be seeked - but dbo.vwTimeEntryItems is
   itself a three-table join, and the optimiser costs building it whole and
   hashing more cheaply than driving 293 seeks through it. The Worktable figure,
   which dominates both columns, is the STRING_AGG and rollup spools inside the
   sql/12 views and has nothing to do with how this view joins.

   The next lever, if that constant ever matters, is not another join: it is to
   project EffectiveLastModified from the sql/12 views themselves, so this view
   needs no join at all and the marker predicate lands directly on the base
   table's index. That is a larger change to the file the agent-hosted path also
   reads, and it is a different item from this one.

   The three IsDeleted filters are unchanged from sql/12 and are unrelated to
   deletion detection: the push path detects deletions by diffing its own
   inventory in ConnectorState, not by reading a flag - see
   docs/SOURCE-CONTRACT.md. A row excluded here simply stops being returned,
   which is exactly what the sweep is looking for.
--------------------------------------------------------------------------- */

CREATE OR ALTER VIEW dbo.vwExternalItemsIncremental
AS
SELECT  ItemId,
        ItemType,
        EffectiveLastModified,
        Title,
        Content,
        CustomerName,
        EngagementName,
        ConsultantName,
        Hours,
        Billable,
        WorkDate,
        Url
FROM
(
    SELECT  v.ItemId,
            v.ItemType,
            c.EffectiveLastModified,
            v.Title, v.Content, v.CustomerName, v.EngagementName,
            v.ConsultantName, v.Hours, v.Billable, v.WorkDate, v.Url
    FROM    dbo.vwCustomerItems AS v
    INNER JOIN dbo.Customers    AS c ON c.CustomerId = v.SourceId

    UNION ALL

    SELECT  v.ItemId,
            v.ItemType,
            e.EffectiveLastModified,
            v.Title, v.Content, v.CustomerName, v.EngagementName,
            v.ConsultantName, v.Hours, v.Billable, v.WorkDate, v.Url
    FROM    dbo.vwEngagementItems AS v
    INNER JOIN dbo.Engagements    AS e ON e.EngagementId = v.SourceId

    UNION ALL

    SELECT  v.ItemId,
            v.ItemType,
            te.EffectiveLastModified,
            v.Title, v.Content, v.CustomerName, v.EngagementName,
            v.ConsultantName, v.Hours, v.Billable, v.WorkDate, v.Url
    FROM    dbo.vwTimeEntryItems AS v
    INNER JOIN dbo.TimeEntries   AS te ON te.TimeEntryId = v.SourceId
) AS unioned;
GO

/* ---------------------------------------------------------------------------
   6. If triggers are not acceptable in this estate.

   Some change-control regimes will not take a trigger on a production table.
   The fallback is a view that computes the value on read:

       CREATE OR ALTER VIEW dbo.vwTimeEntryEffective AS
       SELECT te.TimeEntryId,
              GREATEST(te.LastModified, e.LastModified, c.LastModified)
                  AS EffectiveLastModified
       FROM   dbo.TimeEntries   AS te
       JOIN   dbo.Engagements   AS e ON e.EngagementId = te.EngagementId
       JOIN   dbo.Customers     AS c ON c.CustomerId   = e.CustomerId;

   It is CORRECT. It is not seekable: filtering on a maximum computed across a
   join cannot use an index, so every incremental read scans the whole hierarchy.
   At the pilot's 1,118 rows that is free. At a hundred thousand it is slower
   than the full crawl it was meant to replace, and the connector should stay on
   Tier 2 differencing instead - which reads the same rows but at least writes
   only what moved.

   The decision rule: if the source read is comfortably inside the crawl window,
   the view fallback is fine. If it is not, the triggers are the only thing that
   changes the answer, and Tier 2 is the honest interim position.

   THE THIRD OPTION, if neither is acceptable: have the application populate
   EffectiveLastModified itself on every write path. That is the same column and
   the same index without the triggers, and it moves the guarantee from the
   database to the application - which is a defensible place for it, provided
   every write path is covered. Bulk loads and DBA edits are the ones that get
   missed.
--------------------------------------------------------------------------- */

/* ---------------------------------------------------------------------------
   7. Verification.

   The first query should return zero rows. Any row it returns is a descendant
   whose effective timestamp is older than an ancestor's - which is exactly the
   stale-name defect this script exists to prevent, and means the backfill did
   not complete or a trigger is still disabled.

   It checks all three parent-child edges, not only the two that involve a time
   entry. An engagement that has fallen behind its customer is a real staleness
   - the engagement item carries the customer's name too - and checking only the
   leaf level would pass a source that is already serving one wrong name.
--------------------------------------------------------------------------- */

SELECT  N'engagement behind its customer' AS finding,
        CAST(e.EngagementId AS NVARCHAR(32)) AS id,
        e.EffectiveLastModified AS child_effective,
        c.EffectiveLastModified AS parent_effective
FROM    dbo.Engagements  AS e
INNER JOIN dbo.Customers AS c ON c.CustomerId = e.CustomerId
WHERE   e.EffectiveLastModified < c.EffectiveLastModified

UNION ALL

SELECT  N'time entry behind its engagement',
        CAST(te.TimeEntryId AS NVARCHAR(32)),
        te.EffectiveLastModified,
        e.EffectiveLastModified
FROM    dbo.TimeEntries     AS te
INNER JOIN dbo.Engagements  AS e ON e.EngagementId = te.EngagementId
WHERE   te.EffectiveLastModified < e.EffectiveLastModified

UNION ALL

SELECT  N'time entry behind its customer',
        CAST(te.TimeEntryId AS NVARCHAR(32)),
        te.EffectiveLastModified,
        c.EffectiveLastModified
FROM    dbo.TimeEntries     AS te
INNER JOIN dbo.Engagements  AS e ON e.EngagementId = te.EngagementId
INNER JOIN dbo.Customers    AS c ON c.CustomerId   = e.CustomerId
WHERE   te.EffectiveLastModified < c.EffectiveLastModified;

-- The three triggers exist and are enabled.
SELECT  name AS trigger_name, is_disabled
FROM    sys.triggers
WHERE   name IN (N'trgCustomers_Effective', N'trgEngagements_Effective', N'trgTimeEntries_Effective');

-- And the view returns the expected item count.
SELECT  ItemType, COUNT(*) AS items, MIN(EffectiveLastModified) AS oldest,
        MAX(EffectiveLastModified) AS newest
FROM    dbo.vwExternalItemsIncremental
GROUP BY ItemType;

-- The same counts as a verdict, because the query above cannot fail - it can
-- only be misread. A GROUP BY that matches nothing prints no rows at all, and
-- an empty result set and a correct one differ by a glance; these differ by a
-- word.
--
-- The comparison is against dbo.vwExternalItems rather than against a literal
-- count, and that is deliberate. The invariant worth checking is not a number,
-- it is that the incremental view returns THE SAME ITEMS as the full one: a
-- join that drops a level, duplicates one, or matches the wrong rows breaks
-- this, while a corpus that legitimately grew does not. A literal makes the
-- check report a failure every time the source gets bigger, and a check that
-- cries wolf is a check nobody reads. For the record, the two known corpora
-- are 1,118 items for the shipped sql/11 fixture - 12 customers, 62
-- engagements and 1,044 of 1,052 time entries, eight being soft-deleted on
-- purpose - and 111,900 for sql/14's scale load.
--
-- The joins in section 5 are now on SourceId, so the failure mode this
-- paragraph used to warn about is gone in a specific way worth knowing: if
-- sql/12 has not been re-run and the views do not project SourceId, the CREATE
-- OR ALTER above fails outright with "Invalid column name 'SourceId'" rather
-- than succeeding and returning nothing.
--
-- f.items > 0 is in the verdict on purpose. Without it, two empty views agree
-- with each other perfectly and the check reports PASS on a source that has
-- nothing in it.
SELECT  CASE WHEN f.items = i.items AND f.items = m.matched AND f.items > 0
             THEN N'PASS'
             ELSE N'FAIL - the incremental view and dbo.vwExternalItems do not return the same items; see the per-type counts above'
        END                       AS verdict,
        f.items                   AS items_in_vwExternalItems,
        i.items                   AS items_in_vwExternalItemsIncremental,
        m.matched                 AS itemids_in_both,
        i.items - m.matched       AS in_incremental_only,
        f.items - m.matched       AS in_full_only
FROM       (SELECT COUNT(*) AS items FROM dbo.vwExternalItems)            AS f
CROSS JOIN (SELECT COUNT(*) AS items FROM dbo.vwExternalItemsIncremental) AS i
CROSS JOIN (SELECT COUNT(*) AS matched
            FROM   dbo.vwExternalItems               AS a
            INNER JOIN dbo.vwExternalItemsIncremental AS b ON b.ItemId = a.ItemId) AS m;
GO
