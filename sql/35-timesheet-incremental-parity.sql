-- ===========================================================================
-- 35-timesheet-incremental-parity.sql
--
-- Proves the three things that make an incremental read of this source safe,
-- none of which can be established by reading sql/26's definition.
--
-- WHY THIS FILE EXISTS AT ALL.
--
-- HierarchyPushConnector reads dbo.vwExternalItems on a full crawl and
-- dbo.vwExternalItemsIncremental on an incremental one, and it does NOT choose
-- once - the engine escalates to a full crawl on a hash-version change and
-- every Settings:FullEveryHours, and falls back to the full read whenever there
-- is no checkpoint to resume from. So a single connection alternates between
-- the two views for the rest of its life.
--
-- That makes projection parity a correctness property rather than tidiness. The
-- engine decides what to WRITE by comparing a SHA-256 over the item it built
-- against the one crawl.Item holds. If the two views disagree about any of the
-- thirty columns the connector selects - a trailing space, a different
-- precision, a NULL where the other has an empty string - then every alternation
-- rewrites the whole corpus while reporting an ordinary successful run. There is
-- no error, no bad item and nothing in the log to find afterwards; the only
-- symptom is UnchangedPercent collapsing to zero in crawl.vwRunHistory every
-- time the crawl mode flips, and a night of Graph write quota.
--
-- Check 2 below is therefore not a smoke test. It is the check that says the
-- two reads are the same read.
--
-- THE OTHER TWO. Check 3 establishes that (EffectiveLastModified, ItemId) is a
-- TOTAL order over the corpus - the composite checkpoint is meaningless if the
-- pair can repeat, because "strictly after the marker" would then be ambiguous
-- for the rows that share it. Check 4 establishes that the seek the incremental
-- predicate needs actually has an index behind it; without one the incremental
-- read is a scan of the whole hierarchy, which costs more than the full crawl it
-- replaced (docs/SOURCE-CONTRACT.md, Tier 1, fourth bullet).
--
-- SAFE TO RUN AT ANY TIME. Every statement is a SELECT. It creates nothing,
-- alters nothing and takes no locks beyond a read.
--
-- Run after sql/12 and sql/26. Read every verdict column: each one prints a
-- word, never an empty result set, because this repository has twice shipped a
-- check whose pass condition was "no rows" and whose actual state was "the query
-- matched nothing".
-- ===========================================================================

USE [Ops];
GO

-- No module is created here, so the stored SET options of sql/30's concern do
-- not arise. Set anyway: the fingerprint below concatenates with N'' literals
-- and QUOTED_IDENTIFIER OFF would change how a string literal is parsed in a
-- way that is silent rather than fatal.
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

/* ---------------------------------------------------------------------------
   1. Column parity: every column the connector's SELECT names exists.

   This is the failure the backlog item described. HierarchyPushConnector emits
   an explicit thirty-column SELECT; sql/26's view used to project twelve, so
   pointing Source:ItemView at it failed on nineteen invalid column names,
   LastModified among them - the view named that column EffectiveLastModified
   and had dropped the row's own.

   Written as a LEFT JOIN from an expected-column list to sys.columns rather
   than as "SELECT * FROM the view and see", because a missing column has to be
   NAMED. "Invalid column name" from SQL Server names one per attempt; this
   names all of them at once.

   EffectiveLastModified is in the list because the incremental read selects it
   as well - it is the marker, and it is the one column the full view does not
   have. That asymmetry is the whole point of there being two views.
--------------------------------------------------------------------------- */

DECLARE @Expected TABLE (name SYSNAME PRIMARY KEY);

INSERT INTO @Expected (name) VALUES
    (N'ItemId'), (N'ItemType'), (N'Title'), (N'Url'), (N'LastModified'),
    (N'HierarchyPath'), (N'ContainerName'), (N'ContainerUrl'),
    (N'CustomerId'), (N'CustomerName'), (N'CustomerCode'), (N'Industry'),
    (N'Region'), (N'AccountManager'),
    (N'EngagementId'), (N'EngagementName'), (N'EngagementCode'),
    (N'Practice'), (N'Status'), (N'ProjectManager'),
    (N'ConsultantName'), (N'ConsultantEmail'), (N'WorkDate'), (N'Hours'),
    (N'Billable'), (N'WorkType'),
    (N'ContractValue'), (N'TotalHours'), (N'ChildCount'), (N'Content'),
    (N'EffectiveLastModified');

SELECT  CASE WHEN COUNT(*) = 0 THEN N'PASS - the incremental view projects every column the connector selects'
             ELSE N'FAIL - the connector would fail on ' + CAST(COUNT(*) AS NVARCHAR(11))
                  + N' invalid column name(s), listed below'
        END                                        AS column_parity,
        COUNT(*)                                   AS missing_columns
FROM    @Expected AS e
LEFT JOIN sys.columns AS c
       ON c.object_id = OBJECT_ID(N'dbo.vwExternalItemsIncremental')
      AND c.name      = e.name
WHERE   c.name IS NULL;

SELECT  e.name AS missing_column_name
FROM    @Expected AS e
LEFT JOIN sys.columns AS c
       ON c.object_id = OBJECT_ID(N'dbo.vwExternalItemsIncremental')
      AND c.name      = e.name
WHERE   c.name IS NULL;
GO

/* ---------------------------------------------------------------------------
   2. Item parity: the two views produce byte-identical items.

   A SHA2_256 per row over exactly the thirty columns HierarchyPushConnector
   selects, computed against both views and compared item by item through a FULL
   OUTER JOIN on ItemId. The answer is three counts, not an empty result set:
   items in both that differ, items only the full view returns, items only the
   incremental view returns.

   Why each column is wrapped in ISNULL(..., N'<null>') and joined with an
   explicit separator rather than passed to CONCAT_WS: CONCAT_WS drops NULLs
   entirely, so (a, NULL, b) and (a, b, NULL) produce the same string. Two rows
   that differ only in which column is null would then hash the same, which is
   precisely the difference this check exists to catch - a view branch that
   projects an empty string where its sibling projects NULL changes the item the
   connector builds, because PushItem.AddIfPresent omits an empty value and
   includes a present one.

   Datetimes go through style 126 (ISO 8601) so the comparison does not depend
   on the session's language, and the decimals through their declared scale so a
   trailing zero cannot make two equal numbers hash differently.
--------------------------------------------------------------------------- */

WITH fingerprint AS
(
    SELECT  ItemId,
            HASHBYTES('SHA2_256', CONCAT(
                ISNULL(CONVERT(NVARCHAR(MAX), ItemId), N'<null>'), N'|',
                ISNULL(CONVERT(NVARCHAR(MAX), ItemType), N'<null>'), N'|',
                ISNULL(CONVERT(NVARCHAR(MAX), Title), N'<null>'), N'|',
                ISNULL(CONVERT(NVARCHAR(MAX), Url), N'<null>'), N'|',
                ISNULL(CONVERT(NVARCHAR(33), LastModified, 126), N'<null>'), N'|',
                ISNULL(CONVERT(NVARCHAR(MAX), HierarchyPath), N'<null>'), N'|',
                ISNULL(CONVERT(NVARCHAR(MAX), ContainerName), N'<null>'), N'|',
                ISNULL(CONVERT(NVARCHAR(MAX), ContainerUrl), N'<null>'), N'|',
                ISNULL(CONVERT(NVARCHAR(11), CustomerId), N'<null>'), N'|',
                ISNULL(CONVERT(NVARCHAR(MAX), CustomerName), N'<null>'), N'|',
                ISNULL(CONVERT(NVARCHAR(MAX), CustomerCode), N'<null>'), N'|',
                ISNULL(CONVERT(NVARCHAR(MAX), Industry), N'<null>'), N'|',
                ISNULL(CONVERT(NVARCHAR(MAX), Region), N'<null>'), N'|',
                ISNULL(CONVERT(NVARCHAR(MAX), AccountManager), N'<null>'), N'|',
                ISNULL(CONVERT(NVARCHAR(11), EngagementId), N'<null>'), N'|',
                ISNULL(CONVERT(NVARCHAR(MAX), EngagementName), N'<null>'), N'|',
                ISNULL(CONVERT(NVARCHAR(MAX), EngagementCode), N'<null>'), N'|',
                ISNULL(CONVERT(NVARCHAR(MAX), Practice), N'<null>'), N'|',
                ISNULL(CONVERT(NVARCHAR(MAX), Status), N'<null>'), N'|',
                ISNULL(CONVERT(NVARCHAR(MAX), ProjectManager), N'<null>'), N'|',
                ISNULL(CONVERT(NVARCHAR(MAX), ConsultantName), N'<null>'), N'|',
                ISNULL(CONVERT(NVARCHAR(MAX), ConsultantEmail), N'<null>'), N'|',
                ISNULL(CONVERT(NVARCHAR(33), WorkDate, 126), N'<null>'), N'|',
                ISNULL(CONVERT(NVARCHAR(40), Hours), N'<null>'), N'|',
                ISNULL(CONVERT(NVARCHAR(1), Billable), N'<null>'), N'|',
                ISNULL(CONVERT(NVARCHAR(MAX), WorkType), N'<null>'), N'|',
                ISNULL(CONVERT(NVARCHAR(40), ContractValue), N'<null>'), N'|',
                ISNULL(CONVERT(NVARCHAR(40), TotalHours), N'<null>'), N'|',
                ISNULL(CONVERT(NVARCHAR(11), ChildCount), N'<null>'), N'|',
                ISNULL(CONVERT(NVARCHAR(MAX), Content), N'<null>'))) AS RowHash
    FROM    dbo.vwExternalItems
),
fingerprint_inc AS
(
    SELECT  ItemId,
            HASHBYTES('SHA2_256', CONCAT(
                ISNULL(CONVERT(NVARCHAR(MAX), ItemId), N'<null>'), N'|',
                ISNULL(CONVERT(NVARCHAR(MAX), ItemType), N'<null>'), N'|',
                ISNULL(CONVERT(NVARCHAR(MAX), Title), N'<null>'), N'|',
                ISNULL(CONVERT(NVARCHAR(MAX), Url), N'<null>'), N'|',
                ISNULL(CONVERT(NVARCHAR(33), LastModified, 126), N'<null>'), N'|',
                ISNULL(CONVERT(NVARCHAR(MAX), HierarchyPath), N'<null>'), N'|',
                ISNULL(CONVERT(NVARCHAR(MAX), ContainerName), N'<null>'), N'|',
                ISNULL(CONVERT(NVARCHAR(MAX), ContainerUrl), N'<null>'), N'|',
                ISNULL(CONVERT(NVARCHAR(11), CustomerId), N'<null>'), N'|',
                ISNULL(CONVERT(NVARCHAR(MAX), CustomerName), N'<null>'), N'|',
                ISNULL(CONVERT(NVARCHAR(MAX), CustomerCode), N'<null>'), N'|',
                ISNULL(CONVERT(NVARCHAR(MAX), Industry), N'<null>'), N'|',
                ISNULL(CONVERT(NVARCHAR(MAX), Region), N'<null>'), N'|',
                ISNULL(CONVERT(NVARCHAR(MAX), AccountManager), N'<null>'), N'|',
                ISNULL(CONVERT(NVARCHAR(11), EngagementId), N'<null>'), N'|',
                ISNULL(CONVERT(NVARCHAR(MAX), EngagementName), N'<null>'), N'|',
                ISNULL(CONVERT(NVARCHAR(MAX), EngagementCode), N'<null>'), N'|',
                ISNULL(CONVERT(NVARCHAR(MAX), Practice), N'<null>'), N'|',
                ISNULL(CONVERT(NVARCHAR(MAX), Status), N'<null>'), N'|',
                ISNULL(CONVERT(NVARCHAR(MAX), ProjectManager), N'<null>'), N'|',
                ISNULL(CONVERT(NVARCHAR(MAX), ConsultantName), N'<null>'), N'|',
                ISNULL(CONVERT(NVARCHAR(MAX), ConsultantEmail), N'<null>'), N'|',
                ISNULL(CONVERT(NVARCHAR(33), WorkDate, 126), N'<null>'), N'|',
                ISNULL(CONVERT(NVARCHAR(40), Hours), N'<null>'), N'|',
                ISNULL(CONVERT(NVARCHAR(1), Billable), N'<null>'), N'|',
                ISNULL(CONVERT(NVARCHAR(MAX), WorkType), N'<null>'), N'|',
                ISNULL(CONVERT(NVARCHAR(40), ContractValue), N'<null>'), N'|',
                ISNULL(CONVERT(NVARCHAR(40), TotalHours), N'<null>'), N'|',
                ISNULL(CONVERT(NVARCHAR(11), ChildCount), N'<null>'), N'|',
                ISNULL(CONVERT(NVARCHAR(MAX), Content), N'<null>'))) AS RowHash
    FROM    dbo.vwExternalItemsIncremental
)
SELECT  CASE WHEN SUM(CASE WHEN f.ItemId IS NULL OR i.ItemId IS NULL
                                OR f.RowHash <> i.RowHash THEN 1 ELSE 0 END) = 0
              AND COUNT(*) > 0
             THEN N'PASS - both views build the same item, column for column'
             ELSE N'FAIL - the two views disagree; an alternating crawl would rewrite the corpus'
        END                                                            AS item_parity,
        COUNT(*)                                                       AS items_compared,
        SUM(CASE WHEN f.ItemId IS NOT NULL AND i.ItemId IS NOT NULL
                  AND f.RowHash <> i.RowHash THEN 1 ELSE 0 END)        AS differing_items,
        SUM(CASE WHEN i.ItemId IS NULL THEN 1 ELSE 0 END)              AS in_full_only,
        SUM(CASE WHEN f.ItemId IS NULL THEN 1 ELSE 0 END)              AS in_incremental_only
FROM       fingerprint     AS f
FULL OUTER JOIN fingerprint_inc AS i ON i.ItemId = f.ItemId;
GO

/* ---------------------------------------------------------------------------
   3. The marker is usable as a checkpoint.

   Three properties, and each one has a distinct failure:

     NOT NULL      - "EffectiveLastModified > @marker" is UNKNOWN for a null and
                     therefore never true, so a null row is skipped by every
                     incremental crawl for ever and found only by a full one.
     PAIR UNIQUE   - the checkpoint is (marker, ItemId) and the resume rule is
                     "strictly after the pair". If the pair can repeat, the rule
                     is ambiguous for exactly the rows that repeat it: one of
                     them is read again, the other is not, and which is which is
                     down to the plan.
     NOT AHEAD OF NOW - a source clock ahead of the server's writes a marker the
                     next run's ceiling excludes, which stalls the crawl silently
                     rather than loudly.

   The tie count beside them is informational and is the number worth looking at
   twice: it is how many items share their timestamp with at least one other. On
   this source the cascading triggers stamp an entire customer subtree with ONE
   value, so that number is large by design - which is why a timestamp-only
   marker was never an option here.
--------------------------------------------------------------------------- */

SELECT  CASE WHEN nulls = 0 AND duplicate_pairs = 0 AND ahead_of_now = 0
             THEN N'PASS - (EffectiveLastModified, ItemId) is a total order over the corpus'
             ELSE N'FAIL - the composite marker cannot resume unambiguously; see the counts'
        END                        AS marker_is_a_total_order,
        *
FROM
(
    SELECT  COUNT(*)                                                          AS items,
            SUM(CASE WHEN EffectiveLastModified IS NULL THEN 1 ELSE 0 END)    AS nulls,
            COUNT(*) - COUNT(DISTINCT CONCAT(CONVERT(NVARCHAR(33), EffectiveLastModified, 126),
                                             N'|', ItemId))                   AS duplicate_pairs,
            SUM(CASE WHEN EffectiveLastModified > SYSUTCDATETIME() THEN 1 ELSE 0 END)
                                                                              AS ahead_of_now,
            COUNT(DISTINCT EffectiveLastModified)                             AS distinct_markers
    FROM    dbo.vwExternalItemsIncremental
) AS m;

-- How much of the corpus shares its timestamp with something else. A composite
-- marker is doing real work whenever this is not zero, and on this source it is
-- most of the corpus.
SELECT  SUM(n)                                          AS items_in_a_tie_group,
        MAX(n)                                          AS largest_tie_group,
        COUNT(*)                                        AS tie_groups
FROM   (SELECT COUNT(*) AS n
        FROM   dbo.vwExternalItemsIncremental
        GROUP BY EffectiveLastModified
        HAVING COUNT(*) > 1) AS g;
GO

/* ---------------------------------------------------------------------------
   4. The seek has an index behind it.

   The incremental predicate is
       EffectiveLastModified > @t OR (EffectiveLastModified = @t AND ItemId > @k)
   ordered by (EffectiveLastModified, ItemId), and it is pushed down into each
   of the three branches of the view, where it lands on the BASE TABLE's
   (EffectiveLastModified, key) index. Without those indexes the read is a scan
   of the whole hierarchy however small the delta, which is the failure mode
   docs/SOURCE-CONTRACT.md warns about: an incremental crawl that costs more
   than the full crawl it replaced.

   Checked from sys.indexes rather than from a plan, because a plan is a
   snapshot of one parameter value and this is a property of the schema. The
   key_ordinal test is what makes it a real check: an index on
   (CustomerId, EffectiveLastModified) satisfies "an index mentioning the
   column" and satisfies nothing else.
--------------------------------------------------------------------------- */

SELECT  CASE WHEN COUNT(*) = 3
             THEN N'PASS - all three base tables lead their index with EffectiveLastModified'
             ELSE N'FAIL - only ' + CAST(COUNT(*) AS NVARCHAR(11))
                  + N' of 3 base tables can seek the marker; the incremental read is a scan'
        END                      AS marker_index,
        COUNT(*)                 AS tables_covered
FROM    sys.index_columns AS ic
INNER JOIN sys.columns    AS c  ON c.object_id = ic.object_id AND c.column_id = ic.column_id
INNER JOIN sys.indexes    AS ix ON ix.object_id = ic.object_id AND ix.index_id = ic.index_id
WHERE   ic.object_id IN (OBJECT_ID(N'dbo.Customers'),
                         OBJECT_ID(N'dbo.Engagements'),
                         OBJECT_ID(N'dbo.TimeEntries'))
  AND   c.name = N'EffectiveLastModified'
  AND   ic.key_ordinal = 1
  AND   ic.is_included_column = 0;
GO
