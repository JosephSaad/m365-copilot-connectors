-- ===========================================================================
-- 14-timesheet-scale-data.sql
--
-- Multiplies the sql/11 fixture by a fold factor, so the connector can be run
-- against a corpus large enough to behave like one.
--
-- WHY THIS EXISTS. The shipped fixture is 1,118 items, and at that size every
-- interesting number is too small to mean anything: a run takes three seconds,
-- one deletion is 0.09% of the corpus so the delete guard can never be tested
-- near its threshold, no batch ever fills, and a tenant has no reason to
-- throttle. Several claims in docs/GO-LIVE-READINESS.md section 2 are unproven
-- for exactly that reason and cannot be proven by a bigger machine - only by
-- more rows.
--
-- WHAT IT MAKES, at the default @Folds = 100:
--
--     1,200 customers        (12 x 100)
--     6,200 engagements      (62 x 100)
--   105,300 time entries  (1,053 x 100)
--   ------------------------------------
--   ~111,500 external items, against 1,118 today
--
-- HOW. Fold 0 is the original data and is never touched. Folds 1..N-1 copy it
-- with an ID offset, so every existing row keeps its ID and every existing test
-- that names time6053 or cust12 still means the same row. Names get a fold
-- suffix so a search result is traceable to the copy it came from.
--
-- REFERENTIAL INTEGRITY IS PRESERVED WITHIN A FOLD. A copied engagement points
-- at the copied customer from its own fold, and a copied time entry at the
-- copied engagement from its own fold. Copies never reference across folds,
-- because that would make the rollups in sql/12 - TotalHours, ChildCount -
-- silently wrong in a way no constraint would catch.
--
-- THE TRIGGERS ARE DISABLED FOR THE LOAD, and this is not an optimisation.
-- sql/26 installs AFTER INSERT, UPDATE triggers that cascade a parent's change
-- down to its descendants. Inserting a hundred thousand time entries with those
-- live means a hundred thousand cascading updates, each re-reading a widening
-- table. The load would not finish in a useful time. EffectiveLastModified is
-- set directly in the INSERT instead and verified afterwards, which is the same
-- thing sql/26's own backfill does for the same reason.
--
-- Re-runnable: it refuses if the requested folds are already present, so a
-- second run cannot double the corpus by accident. To go back to the shipped
-- fixture, the rollback block at the foot deletes every fold above 0.
--
-- Run against the SOURCE database (Ops). Verification block at the foot.
-- ===========================================================================

USE [Ops];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

-- ---------------------------------------------------------------------------
-- 1. How many folds, and the offsets that keep them apart.
--
-- The offsets are wide enough that a fold's IDs cannot collide with another's
-- at any fold count this script will accept, and they are round numbers so an
-- ID read in a log says which fold it came from at a glance: customer 47012 is
-- fold 47's copy of customer 12.
-- ---------------------------------------------------------------------------

DECLARE @Folds INT = 100;      -- 1 = the shipped fixture alone; 100 = 100x

DECLARE @CustomerOffset    INT = 1000;
DECLARE @EngagementOffset  INT = 1000;
DECLARE @TimeEntryOffset   INT = 100000;

IF @Folds < 1 OR @Folds > 500
BEGIN
    THROW 50200, N'@Folds must be between 1 and 500. Above that the ID offsets in this script begin to collide.', 1;
END

-- ---------------------------------------------------------------------------
-- 2. Refuse to double an already-scaled corpus.
--
-- The realistic mistake is running this twice, and the failure would be silent:
-- primary key violations on the fold that already exists, a rolled-back
-- transaction, and a corpus that looks untouched but took ten minutes to not
-- change. Checking first says so in one line.
-- ---------------------------------------------------------------------------

IF EXISTS (SELECT 1 FROM dbo.Customers WHERE CustomerId >= @CustomerOffset)
BEGIN
    DECLARE @Existing INT =
        (SELECT MAX(CustomerId) / @CustomerOffset FROM dbo.Customers WHERE CustomerId >= @CustomerOffset) + 1;

    DECLARE @Refuse NVARCHAR(400) =
        CONCAT(N'This source is already scaled to about ', @Existing,
               N' folds. Run the rollback block at the foot of this file first, or drop and rebuild from sql/10 to sql/13.');

    THROW 50201, @Refuse, 1;
END

PRINT CONCAT('Scaling the timesheet fixture to ', @Folds, ' folds.');
GO

-- ---------------------------------------------------------------------------
-- 3. Disable the cascade triggers for the load.
-- ---------------------------------------------------------------------------

DISABLE TRIGGER dbo.trgCustomers_Effective   ON dbo.Customers;
DISABLE TRIGGER dbo.trgEngagements_Effective ON dbo.Engagements;
DISABLE TRIGGER dbo.trgTimeEntries_Effective ON dbo.TimeEntries;
PRINT '  triggers disabled for the load';
GO

-- ---------------------------------------------------------------------------
-- 4. The folds.
--
-- One transaction per fold rather than one for the whole run. A hundred folds
-- in a single transaction is a log file nobody sized for, and a failure at fold
-- ninety would discard eighty-nine that were fine. Each fold is complete and
-- consistent on its own, so stopping part way leaves a smaller corpus rather
-- than a broken one.
-- ---------------------------------------------------------------------------

DECLARE @Folds INT = 100;
DECLARE @CustomerOffset   INT = 1000;
DECLARE @EngagementOffset INT = 1000;
DECLARE @TimeEntryOffset  INT = 100000;

DECLARE @f INT = 1;

WHILE @f < @Folds
BEGIN
    BEGIN TRANSACTION;

    INSERT INTO dbo.Customers
        (CustomerId, CustomerName, CustomerCode, Industry, Region,
         AccountManager, AccountManagerEmail, Notes, LastModified, IsDeleted, EffectiveLastModified)
    SELECT  c.CustomerId + (@f * @CustomerOffset),
            CONCAT(c.CustomerName, N' (fold ', @f, N')'),
            CONCAT(c.CustomerCode, N'-F', @f),
            c.Industry, c.Region, c.AccountManager, c.AccountManagerEmail, c.Notes,
            c.LastModified, c.IsDeleted, c.EffectiveLastModified
    FROM    dbo.Customers AS c
    WHERE   c.CustomerId < @CustomerOffset;

    INSERT INTO dbo.Engagements
        (EngagementId, CustomerId, EngagementCode, EngagementName, Practice, Status,
         StartDate, EndDate, ContractValue, ProjectManager, Scope, LastModified, IsDeleted, EffectiveLastModified)
    SELECT  e.EngagementId + (@f * @EngagementOffset),
            e.CustomerId   + (@f * @CustomerOffset),          -- same fold, never across
            CONCAT(e.EngagementCode, N'-F', @f),
            CONCAT(e.EngagementName, N' (fold ', @f, N')'),
            e.Practice, e.Status, e.StartDate, e.EndDate, e.ContractValue,
            e.ProjectManager, e.Scope, e.LastModified, e.IsDeleted, e.EffectiveLastModified
    FROM    dbo.Engagements AS e
    WHERE   e.EngagementId < @EngagementOffset;

    INSERT INTO dbo.TimeEntries
        (TimeEntryId, EngagementId, ConsultantName, ConsultantEmail, WorkDate, Hours,
         Billable, WorkType, Narrative, LastModified, IsDeleted, EffectiveLastModified)
    SELECT  t.TimeEntryId  + (@f * @TimeEntryOffset),
            t.EngagementId + (@f * @EngagementOffset),        -- same fold
            t.ConsultantName, t.ConsultantEmail, t.WorkDate, t.Hours,
            t.Billable, t.WorkType, t.Narrative,
            t.LastModified,
            t.IsDeleted,                                      -- keeps the 8 deliberate soft deletes per fold
            t.EffectiveLastModified
    FROM    dbo.TimeEntries AS t
    WHERE   t.TimeEntryId < @TimeEntryOffset;

    COMMIT TRANSACTION;

    IF @f % 10 = 0
    BEGIN
        RAISERROR (N'  fold %d of %d loaded', 0, 1, @f, @Folds) WITH NOWAIT;
    END

    SET @f = @f + 1;
END
GO

-- ---------------------------------------------------------------------------
-- 5. Re-enable the triggers.
--
-- Before the verification, not after: a run that fell over between the two
-- would otherwise leave a source that silently stops maintaining
-- EffectiveLastModified, which is the failure sql/26's own header calls the
-- quiet one.
-- ---------------------------------------------------------------------------

ENABLE TRIGGER dbo.trgCustomers_Effective   ON dbo.Customers;
ENABLE TRIGGER dbo.trgEngagements_Effective ON dbo.Engagements;
ENABLE TRIGGER dbo.trgTimeEntries_Effective ON dbo.TimeEntries;
PRINT '  triggers re-enabled';
GO

-- ---------------------------------------------------------------------------
-- 6. Verification.
--
-- The row counts are arithmetic and would be caught by anything. The two that
-- matter are the orphan checks: an engagement pointing at a customer in another
-- fold, or a time entry at an engagement in another fold, would satisfy every
-- foreign key and still make the rollups wrong - a customer showing another
-- fold's hours. Nothing else in this database would notice.
-- ---------------------------------------------------------------------------

SELECT  N'customers'    AS entity, COUNT(*) AS rows_now FROM dbo.Customers
UNION ALL
SELECT  N'engagements',  COUNT(*) FROM dbo.Engagements
UNION ALL
SELECT  N'time entries', COUNT(*) FROM dbo.TimeEntries
UNION ALL
SELECT  N'external items (the view)', COUNT(*) FROM dbo.vwExternalItems;
GO

SELECT  N'engagement in a different fold from its customer' AS check_name,
        COUNT(*) AS offenders
FROM    dbo.Engagements AS e
JOIN    dbo.Customers   AS c ON c.CustomerId = e.CustomerId
WHERE   e.EngagementId / 1000 <> c.CustomerId / 1000

UNION ALL

SELECT  N'time entry in a different fold from its engagement',
        COUNT(*)
FROM    dbo.TimeEntries  AS t
JOIN    dbo.Engagements  AS e ON e.EngagementId = t.EngagementId
WHERE   t.TimeEntryId / 100000 <> e.EngagementId / 1000

UNION ALL

SELECT  N'triggers left disabled',
        COUNT(*)
FROM    sys.triggers
WHERE   parent_id > 0 AND is_disabled = 1;
GO

-- ---------------------------------------------------------------------------
-- 7. Rollback, to return to the shipped fixture.
--
-- Children first: the foreign keys have no cascade, and deleting customers
-- first would fail on a table with a hundred thousand rows still pointing at
-- them. Deliberately not run by this script.
--
--   DELETE FROM dbo.TimeEntries WHERE TimeEntryId  >= 100000;
--   DELETE FROM dbo.Engagements WHERE EngagementId >= 1000;
--   DELETE FROM dbo.Customers   WHERE CustomerId   >= 1000;
--
-- The crawl state store still holds an item per deleted row. The next FULL
-- crawl will try to remove all of them at once, which is the delete guard's
-- whole purpose - it will refuse, correctly, and loudly. Either raise
-- Settings:MaxDeletePercent for that one run deliberately, or reset the
-- connection's inventory, and do not reach for Settings:OverrideDeleteGuard
-- without reading what it turns off.
-- ---------------------------------------------------------------------------
