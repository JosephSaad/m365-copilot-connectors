-- ===========================================================================
-- 10-timesheet-source.sql
--
-- The three level test case: Customer -> Engagement -> TimeEntry.
--
--   Level 1  dbo.Customers     who is billed (the account)
--   Level 2  dbo.Engagements   a contracted body of work for that customer
--   Level 3  dbo.TimeEntries   one consultant's logged hours on an engagement
--
-- This is a SECOND, INDEPENDENT source. It does not touch dbo.Tickets and does
-- not replace it: the ticket test case keeps its own table, its own connection
-- and its own tool. Run this against the same database and both coexist.
--
-- Every table carries the same two operational columns as dbo.Tickets, for the
-- same reasons:
--
--   LastModified  UTC, maintained by the application. The push tool re-reads
--                 everything each run, but the column is here so this source can
--                 be moved behind the agent-hosted connector later without a
--                 migration. Write it with SYSUTCDATETIME(), never GETDATE():
--                 the connector compares against UTC and a local-time column is
--                 wrong by the UTC offset. See docs/TROUBLESHOOTING.md stage 1.
--
--   IsDeleted     soft delete. SqlHierarchyPush excludes these rows rather than
--                 deleting their items, so a deleted row leaves an orphan in the
--                 index — deploy/Compare-SourceToIndex.ps1 finds them. This is a
--                 property of the direct push path, not of this schema.
--
-- Run 10, then 11 (sample data), then 12 (views), then 13 (the grant).
-- ===========================================================================

USE [Ops];
GO

-- ---------------------------------------------------------------------------
-- Level 1: the customer. One row per billed account.
-- ---------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.Customers', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Customers
    (
        CustomerId          INT            NOT NULL CONSTRAINT PK_Customers PRIMARY KEY,

        -- The single most important searchable value in the whole model. It is
        -- copied onto every descendant item so that a search for the customer
        -- returns their engagements and time entries too. See 12-timesheet-views.sql.
        CustomerName        NVARCHAR(200)  NOT NULL,

        CustomerCode        NVARCHAR(20)   NOT NULL,
        Industry            NVARCHAR(100)  NOT NULL,
        Region              NVARCHAR(50)   NOT NULL,
        AccountManager      NVARCHAR(100)  NOT NULL,
        AccountManagerEmail NVARCHAR(200)  NOT NULL,

        -- Free text. Becomes the customer item's indexed content.
        Notes               NVARCHAR(MAX)  NOT NULL CONSTRAINT DF_Customers_Notes DEFAULT (N''),

        LastModified        DATETIME2      NOT NULL CONSTRAINT DF_Customers_LM DEFAULT SYSUTCDATETIME(),
        IsDeleted           BIT            NOT NULL CONSTRAINT DF_Customers_Del DEFAULT (0)
    );

    CREATE UNIQUE INDEX UX_Customers_Code ON dbo.Customers (CustomerCode);
    CREATE INDEX IX_Customers_LastModified ON dbo.Customers (LastModified, CustomerId) INCLUDE (IsDeleted);
END
GO

-- ---------------------------------------------------------------------------
-- Level 2: the engagement. Many per customer.
-- ---------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.Engagements', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Engagements
    (
        EngagementId    INT            NOT NULL CONSTRAINT PK_Engagements PRIMARY KEY,

        -- The parent link. NOT NULL and enforced: an engagement with no customer
        -- would produce an item with no customer text on it, which would be
        -- invisible to exactly the search this test case exists to demonstrate.
        CustomerId      INT            NOT NULL,

        EngagementCode  NVARCHAR(30)   NOT NULL,
        EngagementName  NVARCHAR(200)  NOT NULL,
        Practice        NVARCHAR(100)  NOT NULL,
        Status          NVARCHAR(30)   NOT NULL,
        StartDate       DATE           NOT NULL,
        EndDate         DATE           NULL,
        ContractValue   DECIMAL(18, 2) NOT NULL CONSTRAINT DF_Engagements_Value DEFAULT (0),
        ProjectManager  NVARCHAR(100)  NOT NULL,

        -- Free text. Becomes the engagement item's indexed content.
        Scope           NVARCHAR(MAX)  NOT NULL CONSTRAINT DF_Engagements_Scope DEFAULT (N''),

        LastModified    DATETIME2      NOT NULL CONSTRAINT DF_Engagements_LM DEFAULT SYSUTCDATETIME(),
        IsDeleted       BIT            NOT NULL CONSTRAINT DF_Engagements_Del DEFAULT (0),

        CONSTRAINT FK_Engagements_Customers FOREIGN KEY (CustomerId)
            REFERENCES dbo.Customers (CustomerId)
    );

    CREATE UNIQUE INDEX UX_Engagements_Code ON dbo.Engagements (EngagementCode);
    CREATE INDEX IX_Engagements_Customer ON dbo.Engagements (CustomerId) INCLUDE (IsDeleted);
    CREATE INDEX IX_Engagements_LastModified ON dbo.Engagements (LastModified, EngagementId) INCLUDE (IsDeleted);
END
GO

-- ---------------------------------------------------------------------------
-- Level 3: the time entry. Many per engagement. The high volume table.
-- ---------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.TimeEntries', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.TimeEntries
    (
        TimeEntryId     INT            NOT NULL CONSTRAINT PK_TimeEntries PRIMARY KEY,
        EngagementId    INT            NOT NULL,

        ConsultantName  NVARCHAR(100)  NOT NULL,
        ConsultantEmail NVARCHAR(200)  NOT NULL,
        WorkDate        DATE           NOT NULL,
        Hours           DECIMAL(5, 2)  NOT NULL,
        Billable        BIT            NOT NULL CONSTRAINT DF_TimeEntries_Billable DEFAULT (1),
        WorkType        NVARCHAR(50)   NOT NULL,

        -- What the consultant actually wrote. This is the richest text in the
        -- model and the reason a time entry is worth indexing at all.
        Narrative       NVARCHAR(MAX)  NOT NULL CONSTRAINT DF_TimeEntries_Narr DEFAULT (N''),

        LastModified    DATETIME2      NOT NULL CONSTRAINT DF_TimeEntries_LM DEFAULT SYSUTCDATETIME(),
        IsDeleted       BIT            NOT NULL CONSTRAINT DF_TimeEntries_Del DEFAULT (0),

        CONSTRAINT FK_TimeEntries_Engagements FOREIGN KEY (EngagementId)
            REFERENCES dbo.Engagements (EngagementId),

        -- A negative or absurd day is a data entry slip, and it would be
        -- reported as fact by Copilot. Cheaper to reject here.
        CONSTRAINT CK_TimeEntries_Hours CHECK (Hours > 0 AND Hours <= 24)
    );

    CREATE INDEX IX_TimeEntries_Engagement ON dbo.TimeEntries (EngagementId) INCLUDE (IsDeleted, Hours, Billable);
    CREATE INDEX IX_TimeEntries_LastModified ON dbo.TimeEntries (LastModified, TimeEntryId) INCLUDE (IsDeleted);
    CREATE INDEX IX_TimeEntries_WorkDate ON dbo.TimeEntries (WorkDate) INCLUDE (Hours, Billable);
END
GO

-- Verification: three tables, two foreign keys, the hierarchy intact.
SELECT  t.name AS table_name,
        (SELECT COUNT(*) FROM sys.columns c WHERE c.object_id = t.object_id) AS columns,
        (SELECT COUNT(*) FROM sys.foreign_keys f WHERE f.parent_object_id = t.object_id) AS foreign_keys
FROM    sys.tables AS t
WHERE   t.name IN (N'Customers', N'Engagements', N'TimeEntries')
ORDER BY t.name;
GO
