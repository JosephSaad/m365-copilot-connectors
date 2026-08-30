-- ===========================================================================
-- 33-crawl-state-negative-ttl.sql
--
-- Moves the negative time-to-live from the caller's good intentions into the
-- database, where it can be enforced, changed, and seen.
--
-- WHAT WAS WRONG. crawl.PrincipalMap stores a null EntraObjectId as a NEGATIVE
-- cache entry: "this source principal resolved to nothing". That answer must
-- expire faster than a positive one, and the asymmetry is real in both
-- directions. A stale POSITIVE entry stamps an item's ACL with a group that no
-- longer means what it did, which is a security answer that is wrong for as
-- long as it is cached. A stale NEGATIVE entry costs a directory lookup that
-- would have failed anyway - but it also means a group created this morning is
-- invisible until tonight, and the items that needed it are pushed without it.
--
-- uspCachePrincipal took ONE @TtlMinutes and trusted the caller to pass a
-- shorter one for a negative answer. sql/23's own comment said so plainly:
-- "The database does not enforce that split and cannot". Which made the policy
-- a convention living in a resolver that, per section 4 of
-- docs/GO-LIVE-READINESS.md, nothing has called yet. A convention with no
-- caller is not a policy - it is a paragraph. The first resolver wired up gets
-- to decide it again from scratch, and the second one gets to decide it
-- differently, and nothing anywhere would report the disagreement.
--
-- WHERE THE VALUES LIVE, AND WHY. Two columns on crawl.Connection:
--
--   PrincipalTtlMinutes          720   a resolved principal
--   PrincipalNegativeTtlMinutes   60   one that resolved to nothing
--
-- Considered and rejected, in order of how tempting each was:
--
--   Literals in the procedure. Changing a cache policy would mean altering a
--   module, and the value would be invisible to anyone reading the schema. A
--   number that governs behaviour and cannot be found by looking at the data
--   is the thing this file exists to stop.
--
--   Parameters only, better documented. That IS today's arrangement. Every
--   caller can still get it right, and nothing can tell whether one did.
--
--   A separate settings table. crawl.Connection already carries the other
--   per-connection policy - ExpectedIntervalMinutes, HashVersion, IsEnabled -
--   and PrincipalMap already has a foreign key to it, so the procedure reads
--   the policy in a lookup it was entitled to make anyway. A second table
--   would be one more join to answer one more question about the same row.
--
--   A server-wide setting. Two connections against two directories do not want
--   the same number. A source whose groups are stable can afford a long
--   negative TTL; a source whose groups are provisioned during onboarding is
--   the exact case where a long one hides the group somebody just created.
--
-- THE PARAMETERS SURVIVE, and that is the point of the design rather than a
-- compromise in it. A caller may still ask for a SHORTER life than the policy
-- allows and get it. What it may no longer do is ask for a longer one for a
-- negative answer: the procedure takes the smaller of the two. The database
-- sets a floor on freshness; it does not overrule a caller that knows more.
--
-- IT IS ENFORCED AT THE ONLY WRITE PATH THERE IS. sql/25 grants crawl_writer
-- EXECUTE on procedures and no DML on any table - the verification block at the
-- foot of that file treats any table permission as a finding - so for the
-- connector's identity this procedure is not the main way into
-- crawl.PrincipalMap, it is the only way.
--
--   A CHECK CONSTRAINT ON PrincipalMap WAS THE FIRST IDEA AND IT DOES NOT WORK.
--   A CHECK cannot reference another table, so reading the per-connection cap
--   would need a scalar function - and SQL Server does not re-validate a CHECK
--   when a table the function reads changes. Lower PrincipalNegativeTtlMinutes
--   and every existing row silently violates a constraint the engine still
--   reports as trusted. A constraint that is wrong while claiming to be right
--   is worse than no constraint. The cross-column CHECK added below is a
--   different thing: it constrains two columns of one row, which is what a
--   CHECK can actually promise.
--
-- AND IT IS VISIBLE. crawl.vwPrincipalCacheTtl reports, per connection, the
-- policy in force and whether any stored negative entry outlives it. That is
-- the answer to "how would you know" - which, before this file, was "you would
-- not".
--
-- RUN ORDER. Against ConnectorState, AFTER sql/21 and sql/23. It re-creates
-- crawl.uspCachePrincipal with CREATE OR ALTER, so re-running sql/23 afterwards
-- puts the old body back and silently returns the policy to a convention -
-- re-run this file after any re-run of sql/23. Same standing hazard as sql/28
-- and sql/29, and for the same reason.
--
-- Idempotent. Verification block at the foot.
-- ===========================================================================

USE [ConnectorState];
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
-- crawl.Item carries a filtered index, and any UPDATE against a table carrying
-- one is refused unless QUOTED_IDENTIFIER was ON at CREATE time:
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
SET NOCOUNT ON;
GO

-- ---------------------------------------------------------------------------
-- 1. The columns.
--
-- NOT NULL with defaults rather than nullable. A null would be a third state -
-- "no policy recorded" - meaning the same thing as the default while needing
-- its own branch in the procedure and its own COALESCE in every query about it.
-- The defaults are the numbers sql/23 and sql/27 already assumed: 720 minutes
-- is the @TtlMinutes default the procedure shipped with, and 60 is the shorter
-- one its comment described the caller as being expected to pass.
--
-- Existing rows take the defaults on the ALTER, so a populated database comes
-- out of this file with the policy already applied rather than with a column
-- somebody has to go and fill in.
-- ---------------------------------------------------------------------------

IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID(N'[crawl].[Connection]') AND name = N'PrincipalTtlMinutes')
BEGIN
    ALTER TABLE [crawl].[Connection]
        ADD PrincipalTtlMinutes INT NOT NULL
            CONSTRAINT DF_Connection_PrincipalTtlMinutes DEFAULT (720);

    PRINT 'Added crawl.Connection.PrincipalTtlMinutes, defaulting existing rows to 720.';
END
ELSE
BEGIN
    PRINT 'crawl.Connection.PrincipalTtlMinutes already exists.';
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID(N'[crawl].[Connection]') AND name = N'PrincipalNegativeTtlMinutes')
BEGIN
    ALTER TABLE [crawl].[Connection]
        ADD PrincipalNegativeTtlMinutes INT NOT NULL
            CONSTRAINT DF_Connection_PrincipalNegativeTtlMinutes DEFAULT (60);

    PRINT 'Added crawl.Connection.PrincipalNegativeTtlMinutes, defaulting existing rows to 60.';
END
ELSE
BEGIN
    PRINT 'crawl.Connection.PrincipalNegativeTtlMinutes already exists.';
END
GO

-- ---------------------------------------------------------------------------
-- 2. The constraint that makes the split a schema fact.
--
-- This is the sentence the backlog item asked for: the database can now SEE
-- that a negative answer is meant to expire sooner. It is not a comment about
-- an intention any more - an operator who sets the negative TTL above the
-- positive one is refused, by name, at the point of trying.
--
-- The upper bound is 43,200 minutes, thirty days. Past that the retention job
-- in sql/27 purges expired principals at @KeepExpiredPrincipalDays = 30 anyway,
-- so a longer TTL would name a row that the purge has been deleting all along -
-- a policy that quietly does not apply.
--
-- WITH CHECK on the way in, so the existing rows are validated rather than
-- trusted. A constraint recreated WITH NOCHECK is one the optimiser stops
-- believing, and nobody notices until a plan quietly worsens.
-- ---------------------------------------------------------------------------

IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_Connection_PrincipalTtl')
BEGIN
    ALTER TABLE [crawl].[Connection] DROP CONSTRAINT CK_Connection_PrincipalTtl;
END
GO

ALTER TABLE [crawl].[Connection] WITH CHECK
    ADD CONSTRAINT CK_Connection_PrincipalTtl CHECK
    (
        PrincipalTtlMinutes         BETWEEN 1 AND 43200
    AND PrincipalNegativeTtlMinutes BETWEEN 1 AND 43200
    AND PrincipalNegativeTtlMinutes <= PrincipalTtlMinutes
    );
GO

PRINT 'CK_Connection_PrincipalTtl: a negative TTL may no longer exceed the positive one.';
GO

-- ---------------------------------------------------------------------------
-- 3. The procedure.
--
-- CREATE OR ALTER rather than DROP and CREATE, which matters here: ALTER keeps
-- the object's permissions, so sql/25's GRANT EXECUTE to crawl_writer survives
-- this file. A drop-and-recreate would take the grant with it and the connector
-- would fail on its next principal write, days later, with a permission error
-- against a procedure nobody had touched.
--
-- The signature is backwards compatible. @TtlMinutes keeps its position and now
-- defaults to NULL rather than 720 - "unspecified" means "use the connection's
-- policy", and the connection's policy defaults to 720, so a caller that omits
-- it gets what it always got. Every existing caller passes it explicitly:
-- SqlCrawlStateStore.CachePrincipalAsync always supplies TtlMinutes(ttl).
-- ---------------------------------------------------------------------------

CREATE OR ALTER PROCEDURE [crawl].[uspCachePrincipal]
    @ConnectionId       NVARCHAR(64),
    @SourceType         NVARCHAR(32),
    @SourceKey          NVARCHAR(256),
    @EntraObjectId      UNIQUEIDENTIFIER = NULL,
    @EntraType          NVARCHAR(16)     = NULL,
    -- The caller's requested life for this answer. NULL means "whatever the
    -- connection's policy says".
    @TtlMinutes         INT              = NULL,
    -- A caller that wants to be STRICTER than the connection's negative policy
    -- for this one answer. It can only lower the cap, never raise it.
    @NegativeTtlMinutes INT              = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(3) = SYSUTCDATETIME();
    DECLARE @PolicyTtl INT, @PolicyNegativeTtl INT;

    SELECT  @PolicyTtl         = c.PrincipalTtlMinutes,
            @PolicyNegativeTtl = c.PrincipalNegativeTtlMinutes
    FROM    [crawl].[Connection] AS c
    WHERE   c.ConnectionId = @ConnectionId;

    IF @PolicyTtl IS NULL
    BEGIN
        -- The foreign key on PrincipalMap would refuse the INSERT a few lines
        -- below anyway. Saying it here names the cause instead of leaving a
        -- constraint number and a table nobody was writing to on purpose.
        -- CONCAT rather than a format specifier: THROW takes a literal message
        -- and does no substitution, so a %s here would print as %s.
        DECLARE @Unknown NVARCHAR(400) = CONCAT(
            N'uspCachePrincipal: connection ''', @ConnectionId,
            N''' is not registered, so it has no principal cache policy. Call uspRegisterConnection first.');

        THROW 50330, @Unknown, 1;
    END

    DECLARE @Requested INT = COALESCE(@TtlMinutes,         @PolicyTtl);
    DECLARE @Cap       INT = COALESCE(@NegativeTtlMinutes, @PolicyNegativeTtl);

    -- A floor of one minute on both. A TTL of zero or less is not a short cache,
    -- it is a row that has already expired at the moment it is written - a cache
    -- that silently never hits, and never reports that it never hits.
    -- SqlCrawlStateStore.TtlMinutes rounds up to one for exactly this reason;
    -- doing it here too means a caller that is not that one cannot get it wrong.
    IF @Requested < 1 SET @Requested = 1;
    IF @Cap       < 1 SET @Cap       = 1;

    -- THE ENFORCEMENT, and it is three lines because that is all it needed to
    -- be once the numbers were somewhere the database could read them.
    --
    -- A positive answer gets what was asked for: the caller resolved it and
    -- knows what it cost. A negative answer gets the SMALLER of what was asked
    -- for and the cap - so a caller that asks for a shorter life still gets it,
    -- and a caller that asks for 720 minutes on an answer of "nothing" gets 60.
    --
    -- CASE rather than LEAST(). LEAST arrived in SQL Server 2022 and nothing in
    -- sql/20 through sql/25 needs anything above 2016; this file is not the
    -- place to raise the state store's floor by four versions to save a line.
    DECLARE @Ttl INT =
        CASE WHEN @EntraObjectId IS NOT NULL THEN @Requested
             WHEN @Requested < @Cap          THEN @Requested
             ELSE @Cap
        END;

    DECLARE @Expires DATETIME2(3) = DATEADD(MINUTE, @Ttl, @Now);

    UPDATE  [crawl].[PrincipalMap]
    SET     EntraObjectId = @EntraObjectId,
            EntraType     = @EntraType,
            ResolvedUtc   = @Now,
            ExpiresUtc    = @Expires
    WHERE   ConnectionId = @ConnectionId AND SourceType = @SourceType AND SourceKey = @SourceKey;

    IF @@ROWCOUNT = 0
    BEGIN
        INSERT INTO [crawl].[PrincipalMap]
            (ConnectionId, SourceType, SourceKey, EntraObjectId, EntraType, ResolvedUtc, ExpiresUtc)
        VALUES
            (@ConnectionId, @SourceType, @SourceKey, @EntraObjectId, @EntraType, @Now, @Expires);
    END
END
GO

PRINT 'crawl.uspCachePrincipal now enforces the negative TTL. Existing grants preserved by CREATE OR ALTER.';
GO

-- ---------------------------------------------------------------------------
-- 4. The rows already on record.
--
-- Same move sql/29 makes for mis-stated run statuses: a policy that only
-- applies to rows written after the migration leaves the ones written before it
-- as the exception nobody can find. This only ever SHORTENS a negative entry,
-- so the worst case is one directory lookup that would have been skipped.
--
-- Positive entries are not touched. Shortening those would send the resolver
-- back to the directory for answers it already has, on a schedule this file
-- invented, and nothing about a positive entry was mis-stated.
-- ---------------------------------------------------------------------------

UPDATE  m
SET     m.ExpiresUtc = DATEADD(MINUTE, c.PrincipalNegativeTtlMinutes, m.ResolvedUtc)
FROM    [crawl].[PrincipalMap]  AS m
INNER JOIN [crawl].[Connection] AS c ON c.ConnectionId = m.ConnectionId
WHERE   m.EntraObjectId IS NULL
  AND   m.ExpiresUtc > DATEADD(MINUTE, c.PrincipalNegativeTtlMinutes, m.ResolvedUtc);

PRINT CONCAT('Clamped ', @@ROWCOUNT, ' existing negative cache entr(ies) to their connection''s negative TTL. ',
             'A zero here means one of two different things - that no negative entry was over the cap, or that the table is empty - and ',
             'crawl.vwPrincipalCacheTtl below is what tells them apart.');
GO

-- ---------------------------------------------------------------------------
-- 5. Making it visible.
--
-- The complaint this file answers was that the split was something "the
-- database cannot see". A rule enforced in a procedure body is better than a
-- rule enforced in a caller, and it is still not something anybody can LOOK at.
-- This view is the looking.
--
-- LEFT JOIN from Connection, not INNER. A connection with no cached principals
-- appears with zeroes rather than vanishing, because "this connection has never
-- cached anything" and "this connection is not configured" are different
-- answers and an absent row gives neither.
-- ---------------------------------------------------------------------------

CREATE OR ALTER VIEW [crawl].[vwPrincipalCacheTtl]
AS
SELECT  c.ConnectionId,
        c.PrincipalTtlMinutes                                               AS PolicyPositiveMinutes,
        c.PrincipalNegativeTtlMinutes                                       AS PolicyNegativeMinutes,

        COUNT(m.SourceKey)                                                  AS CachedPrincipals,
        SUM(CASE WHEN m.EntraObjectId IS NOT NULL THEN 1 ELSE 0 END)        AS PositiveEntries,

        -- m.SourceKey IS NOT NULL is not redundant, and leaving it out was a
        -- real defect in the first version of this view. A negative entry is
        -- identified by a NULL EntraObjectId - and the LEFT JOIN manufactures a
        -- row of NULLs for a connection that has cached NOTHING, which then
        -- matched the test and was counted as one negative entry. The view
        -- reported CachedPrincipals 0 and NegativeEntries 1 for an empty cache.
        -- SourceKey is in the primary key and cannot be null in a real row, so
        -- it is the column that tells a stored NULL from an absent one.
        SUM(CASE WHEN m.SourceKey IS NOT NULL AND m.EntraObjectId IS NULL
                 THEN 1 ELSE 0 END)                                         AS NegativeEntries,

        -- These need no such guard: an inequality against a NULL is UNKNOWN,
        -- not true, so the manufactured row falls to the ELSE on its own.
        SUM(CASE WHEN m.ExpiresUtc > SYSUTCDATETIME() THEN 1 ELSE 0 END)    AS UnexpiredEntries,

        -- The number the whole file is about. Anything other than zero means a
        -- negative answer is being kept longer than the connection's policy
        -- allows, which after this migration can only happen if something wrote
        -- to PrincipalMap without going through uspCachePrincipal.
        SUM(CASE WHEN m.EntraObjectId IS NULL
                  AND m.ExpiresUtc > DATEADD(MINUTE, c.PrincipalNegativeTtlMinutes, m.ResolvedUtc)
                 THEN 1 ELSE 0 END)                                         AS NegativeEntriesOverPolicy,

        MAX(CASE WHEN m.EntraObjectId IS NULL
                 THEN DATEDIFF(MINUTE, m.ResolvedUtc, m.ExpiresUtc) END)    AS LongestNegativeMinutes,
        MAX(CASE WHEN m.EntraObjectId IS NOT NULL
                 THEN DATEDIFF(MINUTE, m.ResolvedUtc, m.ExpiresUtc) END)    AS LongestPositiveMinutes
FROM    [crawl].[Connection]     AS c
LEFT JOIN [crawl].[PrincipalMap] AS m ON m.ConnectionId = c.ConnectionId
GROUP BY c.ConnectionId, c.PrincipalTtlMinutes, c.PrincipalNegativeTtlMinutes;
GO

-- The dashboard is deliberately NOT granted this. sql/25 gives crawl_reader
-- SELECT on six named views and nothing else, and its verification block reads
-- anything beyond that as a finding. This is an operator's view; adding it to
-- the dashboard is a decision for sql/24 and sql/25, not a side effect here.
PRINT 'crawl.vwPrincipalCacheTtl created or altered.';
GO

-- ---------------------------------------------------------------------------
-- 6. Verification.
--
-- Section 6.3 is the one to read. The first two only say the schema changed;
-- the third says the RULE holds, by writing five cache entries through the
-- procedure and reading back what it actually stored - including the case the
-- old procedure got wrong, a caller asking for 720 minutes on an answer of
-- "nothing".
--
-- All of it runs inside a transaction that is rolled back, so nothing is left
-- in crawl.PrincipalMap and the file stays safe to re-run against a live store.
-- ---------------------------------------------------------------------------

-- 6.1 The schema.
SELECT  N'PrincipalTtlMinutes exists' AS check_name,
        CASE WHEN EXISTS (SELECT 1 FROM sys.columns
                          WHERE object_id = OBJECT_ID(N'[crawl].[Connection]') AND name = N'PrincipalTtlMinutes')
             THEN N'OK' ELSE N'FAIL' END AS verdict

UNION ALL

SELECT  N'PrincipalNegativeTtlMinutes exists',
        CASE WHEN EXISTS (SELECT 1 FROM sys.columns
                          WHERE object_id = OBJECT_ID(N'[crawl].[Connection]') AND name = N'PrincipalNegativeTtlMinutes')
             THEN N'OK' ELSE N'FAIL' END

UNION ALL

-- Existence is not enough: a constraint that exists but is not trusted has been
-- accepted without validating the rows already there, and stops constraining
-- anything the optimiser or a reader would rely on.
SELECT  N'CK_Connection_PrincipalTtl exists and is trusted',
        CASE WHEN EXISTS (SELECT 1 FROM sys.check_constraints
                          WHERE name = N'CK_Connection_PrincipalTtl' AND is_not_trusted = 0)
             THEN N'OK' ELSE N'FAIL - missing, or created WITH NOCHECK' END

UNION ALL

SELECT  N'uspCachePrincipal takes @NegativeTtlMinutes',
        CASE WHEN EXISTS (SELECT 1 FROM sys.parameters
                          WHERE object_id = OBJECT_ID(N'[crawl].[uspCachePrincipal]')
                            AND name = N'@NegativeTtlMinutes')
             THEN N'OK' ELSE N'FAIL - sql/23 has been re-run over this file' END

UNION ALL

SELECT  N'crawl.vwPrincipalCacheTtl exists',
        CASE WHEN OBJECT_ID(N'[crawl].[vwPrincipalCacheTtl]', N'V') IS NOT NULL
             THEN N'OK' ELSE N'FAIL' END;
GO

-- 6.2 The policy in force, per connection, and whether anything violates it.
SELECT * FROM [crawl].[vwPrincipalCacheTtl] ORDER BY ConnectionId;
GO

-- 6.3 THE RULE, exercised.
DECLARE @Conn NVARCHAR(64) = (SELECT TOP (1) ConnectionId FROM [crawl].[Connection] ORDER BY ConnectionId);

IF @Conn IS NULL
BEGIN
    -- SKIPPED, not OK. crawl.Connection being empty means the test did not run,
    -- and reporting that as a pass is exactly the kind of verification this
    -- repository has been bitten by.
    SELECT  N'live clamp test' AS scenario, N'SKIPPED' AS verdict,
            N'crawl.Connection has no rows, so there is no registered connection to cache a principal against. Nothing was tested. Run sql/23 uspRegisterConnection first.' AS detail;
END
ELSE
BEGIN
    DECLARE @PolicyTtl INT, @PolicyNeg INT;
    SELECT  @PolicyTtl = PrincipalTtlMinutes,
            @PolicyNeg = PrincipalNegativeTtlMinutes
    FROM    [crawl].[Connection] WHERE ConnectionId = @Conn;

    BEGIN TRANSACTION;

    -- A: a positive answer with an explicit TTL. Untouched - the caller
    --    resolved it and knows what it cost.
    EXEC [crawl].[uspCachePrincipal]
            @ConnectionId  = @Conn, @SourceType = N'Sql33Probe', @SourceKey = N'A positive, asked 720',
            @EntraObjectId = '11111111-1111-1111-1111-111111111111', @EntraType = N'group',
            @TtlMinutes    = 720;

    -- B: THE CASE THE OLD PROCEDURE GOT WRONG. A negative answer with the
    --    positive TTL - the caller forgetting the convention. Clamped.
    EXEC [crawl].[uspCachePrincipal]
            @ConnectionId  = @Conn, @SourceType = N'Sql33Probe', @SourceKey = N'B negative, asked 720',
            @EntraObjectId = NULL,
            @TtlMinutes    = 720;

    -- C: a negative answer with a SHORTER TTL than the policy. Honoured, not
    --    overruled - the database sets a floor on freshness, not a schedule.
    EXEC [crawl].[uspCachePrincipal]
            @ConnectionId  = @Conn, @SourceType = N'Sql33Probe', @SourceKey = N'C negative, asked 15',
            @EntraObjectId = NULL,
            @TtlMinutes    = 15;

    -- D: a negative answer with no TTL at all. Takes the connection's policy.
    EXEC [crawl].[uspCachePrincipal]
            @ConnectionId  = @Conn, @SourceType = N'Sql33Probe', @SourceKey = N'D negative, asked nothing',
            @EntraObjectId = NULL;

    -- E: a positive answer with no TTL at all. Takes the positive policy.
    EXEC [crawl].[uspCachePrincipal]
            @ConnectionId  = @Conn, @SourceType = N'Sql33Probe', @SourceKey = N'E positive, asked nothing',
            @EntraObjectId = '22222222-2222-2222-2222-222222222222', @EntraType = N'user';

    -- Expected values are computed from the connection's policy rather than
    -- written as literals, so the check stays correct after an operator changes
    -- the numbers - which is the whole reason they are columns.
    SELECT  x.scenario,
            x.expected_minutes,
            DATEDIFF(MINUTE, m.ResolvedUtc, m.ExpiresUtc) AS stored_minutes,
            CASE WHEN m.SourceKey IS NULL THEN N'FAIL - nothing was written'
                 WHEN DATEDIFF(MINUTE, m.ResolvedUtc, m.ExpiresUtc) = x.expected_minutes THEN N'OK'
                 ELSE N'FAIL' END AS verdict
    FROM   (VALUES
                (N'A positive, asked 720',      720),
                (N'B negative, asked 720',      CASE WHEN 720 < @PolicyNeg THEN 720 ELSE @PolicyNeg END),
                (N'C negative, asked 15',       CASE WHEN 15  < @PolicyNeg THEN 15  ELSE @PolicyNeg END),
                (N'D negative, asked nothing',  CASE WHEN @PolicyTtl < @PolicyNeg THEN @PolicyTtl ELSE @PolicyNeg END),
                (N'E positive, asked nothing',  @PolicyTtl)
           ) AS x (scenario, expected_minutes)
    LEFT JOIN [crawl].[PrincipalMap] AS m
           ON  m.ConnectionId = @Conn
           AND m.SourceType   = N'Sql33Probe'
           AND m.SourceKey    = x.scenario
    ORDER BY x.scenario;

    ROLLBACK TRANSACTION;
END
GO

-- 6.4 The probe rows are gone. A verification that leaves its own fixtures in a
--     live table would show up later as five principals nobody can account for.
SELECT  N'the clamp test left nothing behind' AS check_name,
        CASE WHEN NOT EXISTS (SELECT 1 FROM [crawl].[PrincipalMap] WHERE SourceType = N'Sql33Probe')
             THEN N'OK' ELSE N'FAIL - the rollback did not happen' END AS verdict,
        (SELECT COUNT(*) FROM [crawl].[PrincipalMap] WHERE SourceType = N'Sql33Probe') AS probe_rows_remaining;
GO

-- 6.5 The constraint REFUSES a violation, which is a different claim from the
--     one 6.1 makes. "Exists and is trusted" is metadata; a constraint can be
--     present, trusted and written so that nothing ever violates it. This tries
--     to set a negative TTL above the positive one and expects to be stopped.
--     Rolled back either way, so the connection's policy is unchanged.
DECLARE @Conn2 NVARCHAR(64) = (SELECT TOP (1) ConnectionId FROM [crawl].[Connection] ORDER BY ConnectionId);
DECLARE @Refused BIT = 0, @Error NVARCHAR(400) = N'';

IF @Conn2 IS NULL
BEGIN
    SELECT N'CK_Connection_PrincipalTtl refuses a violation' AS check_name,
           N'SKIPPED' AS verdict,
           N'no registered connection to attempt the update against - nothing was tested' AS detail;
END
ELSE
BEGIN
    BEGIN TRANSACTION;

    BEGIN TRY
        UPDATE  [crawl].[Connection]
        SET     PrincipalNegativeTtlMinutes = PrincipalTtlMinutes + 1
        WHERE   ConnectionId = @Conn2;
    END TRY
    BEGIN CATCH
        SET @Refused = 1;
        SET @Error   = CONCAT(N'error ', ERROR_NUMBER(), N' - ', LEFT(ERROR_MESSAGE(), 300));
    END CATCH

    -- XACT_ABORT is off in this batch, so a constraint violation leaves the
    -- transaction alive rather than doomed and this ROLLBACK is the one that
    -- runs. XACT_STATE is checked anyway: a doomed transaction cannot be
    -- committed and rolling one back twice is an error of its own.
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;

    SELECT  N'CK_Connection_PrincipalTtl refuses a violation' AS check_name,
            CASE WHEN @Refused = 1 THEN N'OK'
                 ELSE N'FAIL - a negative TTL longer than the positive one was ACCEPTED' END AS verdict,
            CASE WHEN @Refused = 1 THEN @Error
                 ELSE N'the UPDATE succeeded and was rolled back; the constraint is not doing anything' END AS detail;
END
GO
