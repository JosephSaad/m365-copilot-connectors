-- ===========================================================================
-- 28-crawl-state-hash-version.sql
--
-- Records which version of the hash framing produced the hashes now stored, so
-- that changing that framing is a detected migration rather than a silent
-- overnight rewrite of the whole corpus.
--
-- THE FAILURE THIS PREVENTS. Every incremental run asks one question: does the
-- hash I just computed match the one on record? A change to how hashes are
-- FRAMED - a new field, a different separator, another normalisation rule -
-- makes every answer "no", for every item, at once. The run then behaves
-- exactly as it would if the entire source had changed overnight: it rewrites
-- the corpus, reports complete success, and burns a day of Graph write quota.
-- Nothing is wrong afterwards, which is the problem. There is no error to find,
-- no item to repair, and the only evidence is a bill and a slow night.
--
-- It has already happened once here in miniature. The v1.3.1 ItemHasher fix -
-- Unspecified DateTimes being shifted by the HOST's offset - meant two
-- connectors in different timezones each saw every one of the other's items as
-- changed, on every run, with both runs reporting success. That was a defect;
-- this column is about the same thing happening on purpose, as a deliberate
-- improvement to the hasher, and being noticed when it does.
--
-- WHY PER CONNECTION AND NOT PER ITEM. The readiness note that asked for this
-- said "beside each stored hash", which would mean a column on crawl.Item. That
-- is the wrong place, for one blocking reason and one design reason.
--
-- The blocking one: the hashes reach crawl.Item through the ItemStateList table
-- type, and SQL Server cannot ALTER a table type. Adding a column means
-- dropping and recreating it, which means dropping every procedure that
-- references it first - a far larger change against a deployed database than
-- the problem justifies.
--
-- The design one: within a connection, two hash versions cannot meaningfully
-- coexist. One connector binary writes every item, so the version is a property
-- of the writer, not of the row. Recording it per item would store the same
-- number a million times to answer a question that has one answer.
--
-- WHAT PER-CONNECTION GIVES UP, stated so nobody discovers it later: gradual
-- rehashing. With a per-item version a migration could rehash in batches across
-- several runs. With this, a version change is one full rewrite, taken
-- deliberately. At pilot scale that is minutes. If a corpus ever grows to where
-- a single full rewrite is not acceptable, this column is the thing to revisit,
-- and moving it to crawl.Item is the change to make.
--
-- Idempotent, and safe on a populated database: the column is added with a
-- default of 1, which is the framing every hash currently on record was
-- computed with. Run against ConnectorState. Verification block at the foot.
-- ===========================================================================

USE [ConnectorState];
GO

SET NOCOUNT ON;
GO

-- ---------------------------------------------------------------------------
-- 1. The column.
--
-- TINYINT because a hash framing that reaches version 255 has other problems.
-- NOT NULL with a default rather than nullable: "no version recorded" and
-- "version 1" would otherwise be different states meaning the same thing, and
-- the comparison in uspBeginRun would need to know that.
-- ---------------------------------------------------------------------------

IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID(N'[crawl].[Connection]') AND name = N'HashVersion')
BEGIN
    ALTER TABLE [crawl].[Connection]
        ADD HashVersion TINYINT NOT NULL
            CONSTRAINT DF_Connection_HashVersion DEFAULT (1);

    PRINT 'Added crawl.Connection.HashVersion, defaulting existing rows to 1.';
END
ELSE
BEGIN
    PRINT 'crawl.Connection.HashVersion already exists.';
END
GO

-- ---------------------------------------------------------------------------
-- 2. The procedure that reads and advances it.
--
-- Deliberately NOT folded into uspBeginRun. Begin already does five things and
-- is the most load-bearing procedure in the file; a version check that failed
-- inside it would be a version check that stopped runs. This one is called
-- first, answers in one row, and the engine decides what to do about it.
--
-- It ADVANCES the stored version as a side effect, which is the part to
-- understand before calling it. Returning WasChanged = 1 twice for the same
-- upgrade would have the second run rewrite a corpus the first run already
-- rewrote. So the version moves when it is reported, and the report is the
-- caller's single chance to act on it - which is why the engine logs it at
-- Warning and escalates the run in the same breath.
--
-- A DOWNGRADE is reported too, and is not an error. Rolling the connector back
-- is a legitimate response to a bad release, and the corpus then has to be
-- rewritten in the other direction for exactly the same reason.
-- ---------------------------------------------------------------------------

CREATE OR ALTER PROCEDURE [crawl].[uspCheckHashVersion]
    @ConnectionId NVARCHAR(64),
    @HashVersion  TINYINT
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @Recorded TINYINT;

    -- A connection this database has never seen has nothing to migrate: its
    -- first run writes everything anyway, so this must not report a change and
    -- send a full crawl that was already full.
    SELECT  @Recorded = HashVersion
    FROM    [crawl].[Connection]
    WHERE   ConnectionId = @ConnectionId;

    IF @Recorded IS NULL
    BEGIN
        SELECT  CAST(0 AS BIT) AS WasChanged,
                @HashVersion   AS PreviousVersion,
                @HashVersion   AS CurrentVersion;
        RETURN;
    END

    IF @Recorded <> @HashVersion
    BEGIN
        UPDATE  [crawl].[Connection]
        SET     HashVersion = @HashVersion,
                UpdatedUtc  = SYSUTCDATETIME()
        WHERE   ConnectionId = @ConnectionId;
    END

    SELECT  CASE WHEN @Recorded <> @HashVersion THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END AS WasChanged,
            @Recorded    AS PreviousVersion,
            @HashVersion AS CurrentVersion;
END
GO

-- ---------------------------------------------------------------------------
-- 3. The grant.
--
-- crawl_writer only. The dashboard has no business advancing a version, and
-- this procedure writes - which is why it is not in sql/24 with the reporting
-- procedures. Guarded so this file runs on an instance where sql/25 created no
-- roles at all, which is every workgroup machine and every CI runner.
-- ---------------------------------------------------------------------------

IF DATABASE_PRINCIPAL_ID(N'crawl_writer') IS NOT NULL
BEGIN
    GRANT EXECUTE ON [crawl].[uspCheckHashVersion] TO [crawl_writer];
    PRINT 'Granted EXECUTE on crawl.uspCheckHashVersion to crawl_writer.';
END
ELSE
BEGIN
    PRINT 'Role crawl_writer does not exist here; skipping the grant. Run sql/25 where the principals are real.';
END
GO

-- ---------------------------------------------------------------------------
-- 4. Verification.
--
-- The second query is the one worth running twice. Called with the version a
-- connection is already on it must report WasChanged = 0; called with a
-- different one it reports 1 ONCE and 0 thereafter, because reporting it twice
-- would rewrite the corpus twice.
-- ---------------------------------------------------------------------------

SELECT  N'column exists' AS check_name,
        CASE WHEN EXISTS (SELECT 1 FROM sys.columns
                          WHERE object_id = OBJECT_ID(N'[crawl].[Connection]') AND name = N'HashVersion')
             THEN N'OK' ELSE N'FAIL' END AS verdict

UNION ALL

SELECT  N'procedure exists',
        CASE WHEN OBJECT_ID(N'[crawl].[uspCheckHashVersion]', N'P') IS NOT NULL
             THEN N'OK' ELSE N'FAIL' END

UNION ALL

SELECT  N'every connection carries a version',
        CASE WHEN NOT EXISTS (SELECT 1 FROM [crawl].[Connection] WHERE HashVersion IS NULL)
             THEN N'OK' ELSE N'FAIL' END;
GO

-- Against a real connection, with the version it is already on. Expect
-- WasChanged = 0, twice.
--
--   DECLARE @c NVARCHAR(64) = (SELECT TOP (1) ConnectionId FROM crawl.Connection ORDER BY ConnectionId);
--   EXEC crawl.uspCheckHashVersion @ConnectionId = @c, @HashVersion = 1;
--   EXEC crawl.uspCheckHashVersion @ConnectionId = @c, @HashVersion = 1;
--
-- Then simulate an upgrade. Expect WasChanged = 1 and then 0 - and put it back
-- afterwards, or the next real run will rewrite the corpus for no reason.
--
--   EXEC crawl.uspCheckHashVersion @ConnectionId = @c, @HashVersion = 2;
--   EXEC crawl.uspCheckHashVersion @ConnectionId = @c, @HashVersion = 2;
--   EXEC crawl.uspCheckHashVersion @ConnectionId = @c, @HashVersion = 1;
