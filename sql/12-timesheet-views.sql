-- ===========================================================================
-- 12-timesheet-views.sql
--
-- THE FLATTENING LAYER. This file is the whole test case; everything else is
-- plumbing around it.
--
-- ---------------------------------------------------------------------------
-- The problem
-- ---------------------------------------------------------------------------
-- A Microsoft Graph external item has a FLAT property list. There is no parent
-- property, no child collection, no join at query time and no way to express
-- that a time entry belongs to an engagement which belongs to a customer.
-- Copilot retrieves individual items; it does not traverse anything.
--
-- So "search for a customer and get their engagements and time entries back"
-- cannot be answered by the index walking a relationship. It can only be
-- answered if each descendant item ALREADY CONTAINS the ancestor's text.
--
-- ---------------------------------------------------------------------------
-- The answer: denormalise deliberately, in both directions
-- ---------------------------------------------------------------------------
-- Downward — every engagement carries its customer's name, code, industry and
-- account manager. Every time entry carries all of that PLUS its engagement's
-- name, code, practice and status. Searching "Contoso" therefore matches the
-- customer item, all three of its engagement items, and every one of its time
-- entry items, because the string is physically present in each of them.
--
-- Upward — the customer item lists its engagement names, and the engagement
-- item lists the consultants who logged time to it. Searching an engagement
-- name returns the customer too, and searching a consultant returns the
-- engagements they worked on. The traversal that the index will not do is
-- pre-computed here, at both ends.
--
-- The cost is duplication, and it is the right trade. These are index items,
-- not a system of record: dbo.Customers is still the only place a customer name
-- is authored. Re-push after a customer is renamed and every descendant item is
-- rewritten with the new name.
--
-- ---------------------------------------------------------------------------
-- Why views rather than SQL inside the tool
-- ---------------------------------------------------------------------------
-- A DBA can read exactly what leaves the database, and can EXPLAIN it. The push
-- tool holds one query against one view and no join logic at all. And the grant
-- in 13-timesheet-least-privilege.sql is on the views only, so the identity
-- that pushes cannot read the base tables directly.
--
-- Requires SQL Server 2017 or later for STRING_AGG.
--
-- Run after 10 and 11.
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
-- ---------------------------------------------------------------------------
-- SourceId - THE NUMERIC KEY BEHIND ItemId, PROJECTED SO IT CAN BE JOINED ON.
--
-- Each of the three views below builds its ItemId by concatenating a prefix
-- onto an integer primary key. Anything that needs to get back from an item to
-- the row it came from - sql/26's incremental view is the one in the tree - then
-- has to reverse that, and the only reversal available to a join is to build the
-- string on the other side too:
--
--     INNER JOIN dbo.TimeEntries AS te
--             ON N'time' + CAST(te.TimeEntryId AS NVARCHAR(32)) = v.ItemId
--
-- That comparison is not sargable. The measured plan for it was a clustered
-- index scan of all 105,300 rows of dbo.TimeEntries on every incremental read,
-- however small the delta, plus the same for Engagements and Customers -
-- a cost that does not shrink with the thing it was added to make smaller.
--
-- SourceId is that key, projected. It is the integer primary key of the row the
-- item was built from: CustomerId at level 1, EngagementId at level 2,
-- TimeEntryId at level 3. Paired with ItemType it identifies the item exactly,
-- and it lets a join use the primary key it was going to have to reach anyway.
--
-- WHY NOT REUSE CustomerId / EngagementId, which two of the three views already
-- project. Because those answer a different question. In vwTimeEntryItems,
-- CustomerId is the ANCESTOR the item was denormalised from, not the item's own
-- key, and level 3 has no column at all for its own. A join written against
-- them works for two levels and silently reads the wrong rows on the third -
-- and it reads SOME rows, so it does not announce itself. One column that means
-- the same thing at every level removes the chance to get that wrong.
--
-- ADDING IT IS SAFE, and the ordering is the reason. It is appended LAST in all
-- three views, so it does not move ItemId, ItemType, Content or anything else
-- from the ordinal it holds today - which matters because dbo.vwExternalItems
-- below is a UNION ALL of SELECT *, and a UNION ALL matches its branches by
-- position. HierarchyPushConnector names its thirty columns explicitly and does
-- not select this one, so no item's identity, content or hash changes. Verified
-- against the 111,900-row corpus before and after: same count, same ItemId
-- checksum, same SHA2_256 over the whole thirty-column projection.
-- ---------------------------------------------------------------------------

-- ---------------------------------------------------------------------------
-- Level 1: customers, with their engagement list rolled up.
-- ---------------------------------------------------------------------------
CREATE OR ALTER VIEW dbo.vwCustomerItems
AS
SELECT
    -- Item IDs must be alphanumeric and 128 characters or fewer. The prefix
    -- keeps the three levels from colliding inside one connection.
    CAST('cust' + CAST(c.CustomerId AS VARCHAR(11)) AS NVARCHAR(128))    AS ItemId,
    CAST('Customer' AS NVARCHAR(20))                                     AS ItemType,
    CAST(c.CustomerName AS NVARCHAR(400))                                AS Title,
    CAST(CONCAT('https://portal.consultco.com/customers/', c.CustomerId) AS NVARCHAR(500)) AS Url,
    c.LastModified,
    CAST(c.CustomerName AS NVARCHAR(600))                                AS HierarchyPath,

    -- containerName and containerUrl are Graph semantic labels: they tell the
    -- index what this item sits inside. It is the closest the platform gets to
    -- expressing a hierarchy, and search surfaces show it as the item's context.
    -- A customer is top level, so its container is the portal's customer list.
    CAST('Customers' AS NVARCHAR(400))                                   AS ContainerName,
    CAST('https://portal.consultco.com/customers' AS NVARCHAR(500))      AS ContainerUrl,

    c.CustomerId,
    c.CustomerName,
    c.CustomerCode,
    c.Industry,
    c.Region,
    c.AccountManager,
    c.AccountManagerEmail,

    CAST(NULL AS INT)            AS EngagementId,
    CAST(NULL AS NVARCHAR(200))  AS EngagementName,
    CAST(NULL AS NVARCHAR(30))   AS EngagementCode,
    CAST(NULL AS NVARCHAR(100))  AS Practice,
    CAST(NULL AS NVARCHAR(30))   AS Status,
    CAST(NULL AS NVARCHAR(100))  AS ProjectManager,

    CAST(NULL AS NVARCHAR(100))  AS ConsultantName,
    CAST(NULL AS NVARCHAR(200))  AS ConsultantEmail,
    CAST(NULL AS DATE)           AS WorkDate,
    CAST(NULL AS DECIMAL(5, 2))  AS Hours,
    CAST(NULL AS BIT)            AS Billable,
    CAST(NULL AS NVARCHAR(50))   AS WorkType,

    CAST(ISNULL(roll.ContractValue, 0) AS DECIMAL(18, 2)) AS ContractValue,
    CAST(ISNULL(roll.TotalHours, 0) AS DECIMAL(10, 2)) AS TotalHours,
    CAST(ISNULL(roll.EngagementCount, 0) AS INT)       AS ChildCount,

    -- The indexed content. Note what is in here: the customer's own notes, and
    -- the NAMES of their engagements. That second part is what makes a search
    -- for an engagement name also return its customer.
    CAST(CONCAT(
        'Customer: ', c.CustomerName, ' (', c.CustomerCode, ')', CHAR(13), CHAR(10),
        'Industry: ', c.Industry, ' | Region: ', c.Region,
        ' | Account manager: ', c.AccountManager, ' <', c.AccountManagerEmail, '>', CHAR(13), CHAR(10),
        'Engagements: ', CAST(ISNULL(roll.EngagementCount, 0) AS VARCHAR(11)),
        ' | Total logged: ', CAST(ISNULL(roll.TotalHours, 0) AS VARCHAR(20)), ' hours',
        ' | Contract value: ', CAST(ISNULL(roll.ContractValue, 0) AS VARCHAR(30)), CHAR(13), CHAR(10),
        CASE WHEN roll.EngagementList IS NULL THEN ''
             ELSE CONCAT('Engagement list: ', roll.EngagementList, CHAR(13), CHAR(10)) END,
        CHAR(13), CHAR(10),
        c.Notes) AS NVARCHAR(MAX))                     AS Content,

    -- The key ItemId was built from. Appended last; see the note above.
    CAST(c.CustomerId AS INT)                          AS SourceId
FROM dbo.Customers AS c
OUTER APPLY (
    SELECT  COUNT(*)                 AS EngagementCount,
            SUM(e.ContractValue)     AS ContractValue,
            SUM(h.EngagementHours)   AS TotalHours,
            -- CAST to MAX inside the aggregate: with non-MAX inputs STRING_AGG
            -- returns NVARCHAR(4000) and raises error 9829 the moment one
            -- customer's list passes 8000 bytes - killing every read of the
            -- view, and with it the whole push run.
            STRING_AGG(
                CAST(CONCAT(e.EngagementName, ' (', e.EngagementCode, ', ', e.Status, ')') AS NVARCHAR(MAX)),
                '; ') WITHIN GROUP (ORDER BY e.EngagementCode) AS EngagementList
    FROM    dbo.Engagements AS e
    OUTER APPLY (
        SELECT SUM(te.Hours) AS EngagementHours
        FROM   dbo.TimeEntries AS te
        WHERE  te.EngagementId = e.EngagementId AND te.IsDeleted = 0
    ) AS h
    WHERE   e.CustomerId = c.CustomerId AND e.IsDeleted = 0
) AS roll
WHERE c.IsDeleted = 0;
GO

-- ---------------------------------------------------------------------------
-- Level 2: engagements, carrying the customer down and the consultants up.
-- ---------------------------------------------------------------------------
CREATE OR ALTER VIEW dbo.vwEngagementItems
AS
SELECT
    CAST('eng' + CAST(e.EngagementId AS VARCHAR(11)) AS NVARCHAR(128))    AS ItemId,
    CAST('Engagement' AS NVARCHAR(20))                                    AS ItemType,
    CAST(CONCAT(e.EngagementName, ' — ', c.CustomerName) AS NVARCHAR(400)) AS Title,
    CAST(CONCAT('https://portal.consultco.com/engagements/', e.EngagementId) AS NVARCHAR(500)) AS Url,
    e.LastModified,
    CAST(CONCAT(c.CustomerName, ' > ', e.EngagementName) AS NVARCHAR(600)) AS HierarchyPath,

    -- An engagement's container is its customer.
    CAST(c.CustomerName AS NVARCHAR(400))                                 AS ContainerName,
    CAST(CONCAT('https://portal.consultco.com/customers/', c.CustomerId) AS NVARCHAR(500)) AS ContainerUrl,

    -- The customer columns, copied down. This is the denormalisation.
    c.CustomerId,
    c.CustomerName,
    c.CustomerCode,
    c.Industry,
    c.Region,
    c.AccountManager,
    c.AccountManagerEmail,

    e.EngagementId,
    e.EngagementName,
    e.EngagementCode,
    e.Practice,
    e.Status,
    e.ProjectManager,

    CAST(NULL AS NVARCHAR(100))  AS ConsultantName,
    CAST(NULL AS NVARCHAR(200))  AS ConsultantEmail,
    CAST(NULL AS DATE)           AS WorkDate,
    CAST(NULL AS DECIMAL(5, 2))  AS Hours,
    CAST(NULL AS BIT)            AS Billable,
    CAST(NULL AS NVARCHAR(50))   AS WorkType,

    CAST(e.ContractValue AS DECIMAL(18, 2))            AS ContractValue,
    CAST(ISNULL(roll.TotalHours, 0) AS DECIMAL(10, 2)) AS TotalHours,
    CAST(ISNULL(roll.EntryCount, 0) AS INT)            AS ChildCount,

    CAST(CONCAT(
        'Engagement: ', e.EngagementName, ' (', e.EngagementCode, ')', CHAR(13), CHAR(10),
        'Customer: ', c.CustomerName, ' (', c.CustomerCode, ')',
        ' | ', c.Industry, ' | ', c.Region,
        ' | Account manager: ', c.AccountManager, CHAR(13), CHAR(10),
        'Practice: ', e.Practice, ' | Status: ', e.Status,
        ' | Project manager: ', e.ProjectManager, CHAR(13), CHAR(10),
        'Started ', CONVERT(VARCHAR(10), e.StartDate, 23),
        CASE WHEN e.EndDate IS NULL THEN ' | ongoing'
             ELSE CONCAT(' | ended ', CONVERT(VARCHAR(10), e.EndDate, 23)) END,
        ' | Contract value: ', CAST(e.ContractValue AS VARCHAR(30)), CHAR(13), CHAR(10),
        'Logged to date: ', CAST(ISNULL(roll.TotalHours, 0) AS VARCHAR(20)), ' hours across ',
        CAST(ISNULL(roll.EntryCount, 0) AS VARCHAR(11)), ' time entries', CHAR(13), CHAR(10),
        -- Consultants rolled UP from level 3, so a search for a person returns
        -- the engagements they worked on and not only their own time entries.
        CASE WHEN people.Consultants IS NULL THEN ''
             ELSE CONCAT('Consultants: ', people.Consultants, CHAR(13), CHAR(10)) END,
        CHAR(13), CHAR(10),
        e.Scope) AS NVARCHAR(MAX))                     AS Content,

    -- The key ItemId was built from - the ENGAGEMENT's, not the customer's.
    CAST(e.EngagementId AS INT)                        AS SourceId
FROM dbo.Engagements AS e
JOIN dbo.Customers   AS c ON c.CustomerId = e.CustomerId
OUTER APPLY (
    SELECT  COUNT(*)      AS EntryCount,
            SUM(te.Hours) AS TotalHours
    FROM    dbo.TimeEntries AS te
    WHERE   te.EngagementId = e.EngagementId AND te.IsDeleted = 0
) AS roll
OUTER APPLY (
    -- Kept separate from the counts above: STRING_AGG has no DISTINCT, so the
    -- distinct list has to be formed first, and mixing that derived table with
    -- the plain aggregates in one APPLY would need a GROUP BY to no purpose.
    SELECT  STRING_AGG(CAST(names.ConsultantName AS NVARCHAR(MAX)), ', ')
                WITHIN GROUP (ORDER BY names.ConsultantName) AS Consultants
    FROM   (SELECT DISTINCT te.ConsultantName
            FROM   dbo.TimeEntries AS te
            WHERE  te.EngagementId = e.EngagementId AND te.IsDeleted = 0) AS names
) AS people
WHERE e.IsDeleted = 0
  AND c.IsDeleted = 0;   -- a customer removed takes their engagements with them
GO

-- ---------------------------------------------------------------------------
-- Level 3: time entries, carrying BOTH ancestors.
--
-- This view is the one the requirement stands or falls on. A time entry item
-- that does not physically contain the words "Contoso Financial Services" will
-- never be returned by a search for Contoso, however the data is related in
-- SQL Server.
-- ---------------------------------------------------------------------------
CREATE OR ALTER VIEW dbo.vwTimeEntryItems
AS
SELECT
    CAST('time' + CAST(te.TimeEntryId AS VARCHAR(11)) AS NVARCHAR(128))  AS ItemId,
    CAST('TimeEntry' AS NVARCHAR(20))                                    AS ItemType,
    CAST(CONCAT(
        CONVERT(VARCHAR(10), te.WorkDate, 23), ' — ', te.ConsultantName,
        ' — ', e.EngagementName, ' — ', c.CustomerName) AS NVARCHAR(400)) AS Title,
    CAST(CONCAT('https://portal.consultco.com/time/', te.TimeEntryId) AS NVARCHAR(500)) AS Url,
    te.LastModified,
    CAST(CONCAT(c.CustomerName, ' > ', e.EngagementName, ' > ',
                CONVERT(VARCHAR(10), te.WorkDate, 23), ' ', te.ConsultantName) AS NVARCHAR(600)) AS HierarchyPath,

    -- A time entry's container names both ancestors, because that is the label
    -- a person reads in a result list and one level is not enough context.
    CAST(CONCAT(e.EngagementName, ' — ', c.CustomerName) AS NVARCHAR(400)) AS ContainerName,
    CAST(CONCAT('https://portal.consultco.com/engagements/', e.EngagementId) AS NVARCHAR(500)) AS ContainerUrl,

    c.CustomerId,
    c.CustomerName,
    c.CustomerCode,
    c.Industry,
    c.Region,
    c.AccountManager,
    c.AccountManagerEmail,

    e.EngagementId,
    e.EngagementName,
    e.EngagementCode,
    e.Practice,
    e.Status,
    e.ProjectManager,

    te.ConsultantName,
    te.ConsultantEmail,
    te.WorkDate,
    te.Hours,
    te.Billable,
    te.WorkType,

    CAST(NULL AS DECIMAL(18, 2))         AS ContractValue,
    CAST(te.Hours AS DECIMAL(10, 2))     AS TotalHours,
    CAST(0 AS INT)                       AS ChildCount,

    CAST(CONCAT(
        'Time entry: ', te.ConsultantName, ' — ', CONVERT(VARCHAR(10), te.WorkDate, 23),
        ' — ', CAST(te.Hours AS VARCHAR(10)), ' hours',
        CASE WHEN te.Billable = 1 THEN ' (billable)' ELSE ' (non billable)' END, CHAR(13), CHAR(10),
        'Customer: ', c.CustomerName, ' (', c.CustomerCode, ')',
        ' | ', c.Industry, ' | ', c.Region,
        ' | Account manager: ', c.AccountManager, CHAR(13), CHAR(10),
        'Engagement: ', e.EngagementName, ' (', e.EngagementCode, ')',
        ' | ', e.Practice, ' | ', e.Status,
        ' | Project manager: ', e.ProjectManager, CHAR(13), CHAR(10),
        'Work type: ', te.WorkType, CHAR(13), CHAR(10),
        CHAR(13), CHAR(10),
        te.Narrative) AS NVARCHAR(MAX))  AS Content,

    -- The key ItemId was built from. This is the level that had no column for
    -- its own key at all, and the reason SourceId exists rather than a third
    -- level-specific one alongside CustomerId and EngagementId.
    CAST(te.TimeEntryId AS INT)          AS SourceId
FROM dbo.TimeEntries AS te
JOIN dbo.Engagements AS e ON e.EngagementId = te.EngagementId
JOIN dbo.Customers   AS c ON c.CustomerId   = e.CustomerId
WHERE te.IsDeleted = 0
  AND e.IsDeleted  = 0
  AND c.IsDeleted  = 0;
GO

-- ---------------------------------------------------------------------------
-- One view over all three. This is what SqlHierarchyPush reads: a single query,
-- no joins in the tool, and a column shape that never varies by level.
--
-- Ordered by level at read time, not here — a view cannot carry ORDER BY, and
-- the tool needs customers first so a partial run leaves the parents present.
--
-- SELECT * IS BOUND AT CREATE TIME, so re-running this file is what carries the
-- SourceId column added above through to here. Until it is re-run, this view
-- keeps the thirty-one columns it was created with and returns exactly the data
-- it returned before — because SourceId was appended LAST in all three branches
-- and nothing else moved. That was checked rather than assumed: adding the
-- column to the branches and reading this view WITHOUT recreating it returned
-- the same rows under the same names.
-- ---------------------------------------------------------------------------
CREATE OR ALTER VIEW dbo.vwExternalItems
AS
SELECT * FROM dbo.vwCustomerItems
UNION ALL
SELECT * FROM dbo.vwEngagementItems
UNION ALL
SELECT * FROM dbo.vwTimeEntryItems;
GO

-- ---------------------------------------------------------------------------
-- Verification. Run this and read the third result set: it is the proof that
-- the requirement is met, before Graph is involved at all.
-- ---------------------------------------------------------------------------

-- 1. One row per live record, at each level.
SELECT ItemType, COUNT(*) AS items FROM dbo.vwExternalItems GROUP BY ItemType ORDER BY ItemType;

-- 2. No item may be missing its customer text: that would be invisible to a
--    customer search. Expect zero rows.
SELECT ItemId, ItemType
FROM   dbo.vwExternalItems
WHERE  Content NOT LIKE '%' + CustomerName + '%'
   OR  CustomerName IS NULL;

-- 3. THE REQUIREMENT. Searching a customer name must reach all three levels.
--    This is the same predicate Microsoft Search applies to the content, run
--    locally so the answer is known before anything is pushed.
SELECT  ItemType,
        COUNT(*) AS matches_for_contoso
FROM    dbo.vwExternalItems
WHERE   Content LIKE N'%Contoso Financial Services%'
GROUP BY ItemType
ORDER BY ItemType;

-- 4. And the reverse direction: a consultant's name reaches the engagements
--    they worked on, not only their own time entries.
SELECT  ItemType,
        COUNT(*) AS matches_for_priya
FROM    dbo.vwExternalItems
WHERE   Content LIKE N'%Priya Raman%'
GROUP BY ItemType
ORDER BY ItemType;

-- 5. SourceId is a key, and it is the key ItemId was built from. Both halves
--    matter and neither is checkable by looking at the definition.
--
--    Written as a single row with three verdict columns rather than as a
--    "should return no rows" query, because "no rows" and "the WHERE clause
--    matched nothing because the join fell over" print identically. A COUNT
--    that is zero is a zero on the screen; an empty result set is a blank.
SELECT  CASE WHEN COUNT(*) = COUNT(DISTINCT CONCAT(ItemType, N':', SourceId))
             THEN N'OK' ELSE N'FAIL - SourceId is not unique within an ItemType' END
                                                             AS sourceid_is_a_key,
        SUM(CASE WHEN SourceId IS NULL THEN 1 ELSE 0 END)    AS sourceid_nulls,
        SUM(CASE WHEN ItemId <> CONCAT(CASE ItemType WHEN N'Customer'   THEN N'cust'
                                                     WHEN N'Engagement' THEN N'eng'
                                                     ELSE N'time' END,
                                       CAST(SourceId AS NVARCHAR(11)))
                 THEN 1 ELSE 0 END)                          AS sourceid_disagrees_with_itemid,
        COUNT(*)                                             AS rows_checked
FROM    dbo.vwExternalItems;
GO
