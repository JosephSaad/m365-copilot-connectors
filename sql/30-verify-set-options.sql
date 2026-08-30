-- ===========================================================================
-- 30-verify-set-options.sql
--
-- Fails the deployment if any module was created with QUOTED_IDENTIFIER OFF.
--
-- WHY THIS NEEDS A SCRIPT OF ITS OWN. SQL Server stores QUOTED_IDENTIFIER with
-- each module as it stood in the session that created it, and replays that
-- stored setting on every execution regardless of what the caller sets. sqlcmd
-- connects with it OFF and SSMS connects with it ON, so the identical script
-- produces a working module from a query window and a broken one from the
-- command line - with identical deployment output in both cases.
--
-- The consequence is not cosmetic. Any UPDATE against a table carrying a
-- filtered index is refused when the module holds QUOTED_IDENTIFIER OFF, and
-- crawl.Item carries one. uspBeginRun then throws error 1934 the next time a
-- connector starts, days after the deploy, in an application nobody changed:
--
--   UPDATE failed because the following SET options have incorrect settings:
--   'QUOTED_IDENTIFIER'.
--
-- That is how this was found - a crawl that could not open a run, hours after a
-- deployment that reported success.
--
-- Every module-creating script now sets both options at the top, which makes
-- the stored setting independent of the client. This script is the check that
-- says so out loud, because the setting is invisible in the object definition
-- and nothing else about a wrong one looks wrong.
--
-- Run LAST, against each database that holds modules. Exits non-zero on a
-- finding so a pipeline stops here rather than at the connector.
-- ===========================================================================

SET NOCOUNT ON;
GO

-- The offenders, by name, so the fix is "re-run these scripts" and not a hunt.
SELECT  DB_NAME()                        AS [Database],
        SCHEMA_NAME(o.schema_id)         AS [Schema],
        o.name                           AS [Module],
        o.type_desc                      AS [Type],
        m.uses_quoted_identifier         AS [QuotedIdentifier],
        m.uses_ansi_nulls                AS [AnsiNulls]
FROM    sys.sql_modules AS m
INNER JOIN sys.objects  AS o ON o.object_id = m.object_id
WHERE   o.is_ms_shipped = 0
  AND   (m.uses_quoted_identifier = 0 OR m.uses_ansi_nulls = 0)
ORDER BY o.name;
GO

-- The verdict. THROW rather than PRINT: a deployment that carries on past this
-- has produced modules that will fail at execution, and the whole point is to
-- surface that now, while the person who ran the script is still watching.
DECLARE @Bad int =
(
    SELECT  COUNT(*)
    FROM    sys.sql_modules AS m
    INNER JOIN sys.objects  AS o ON o.object_id = m.object_id
    WHERE   o.is_ms_shipped = 0
      AND   (m.uses_quoted_identifier = 0 OR m.uses_ansi_nulls = 0)
);

IF @Bad > 0
BEGIN
    -- CONCAT rather than a format specifier: THROW takes a literal message and
    -- does no substitution, so a %d here would be printed as %d.
    DECLARE @Message nvarchar(400) = CONCAT(
        @Bad, N' module(s) in ', DB_NAME(), N' were created with QUOTED_IDENTIFIER or ',
        N'ANSI_NULLS OFF and will fail at execution against filtered indexes. ',
        N'Re-run the scripts that create them; each now sets both options.');

    THROW 50030, @Message, 1;
END

PRINT CONCAT('SET options OK: every module in ', DB_NAME(),
             ' carries QUOTED_IDENTIFIER ON and ANSI_NULLS ON.');
GO
