-- ===========================================================================
-- 00-sample-source.sql
-- Minimal source table for a connector test. Production databases should run
-- 02-soft-delete.sql against their existing table instead of this script.
-- ===========================================================================

CREATE TABLE dbo.Tickets
(
    TicketId     INT            NOT NULL PRIMARY KEY,
    Title        NVARCHAR(255)  NOT NULL,
    Status       NVARCHAR(50)   NOT NULL,
    AssignedTo   NVARCHAR(100)  NOT NULL,
    Body         NVARCHAR(MAX)  NOT NULL,
    LastModified DATETIME2      NOT NULL CONSTRAINT DF_Tickets_LM DEFAULT SYSUTCDATETIME(),

    -- Soft delete marker. The incremental crawl reports rows set to 1 as
    -- deletions; full crawls skip them. See 02-soft-delete.sql.
    IsDeleted    BIT            NOT NULL CONSTRAINT DF_Tickets_IsDeleted DEFAULT (0)
);
GO

INSERT INTO dbo.Tickets (TicketId, Title, Status, AssignedTo, Body)
VALUES
 (1001, N'VPN drops every 30 minutes on Wi-Fi',      N'Open',     N'jsmith@contoso.com',
        N'Users on the fourth floor report the Always On VPN tunnel dropping roughly every 30 minutes when connected to corporate Wi-Fi. Wired connections are unaffected. Suspected IKEv2 rekey interval mismatch on the RRAS server.'),
 (1002, N'SharePoint search returns stale results',  N'Resolved', N'akhan@contoso.com',
        N'Search results for the Policies library were up to six hours behind. Continuous crawl had stalled on the content source. Restarting the SharePoint Search Host Controller service and resetting the index resolved it.'),
 (1003, N'Purview DLP policy blocking legitimate email', N'In Progress', N'mlee@contoso.com',
        N'The Financial Data DLP policy is matching internal cost centre codes as credit card numbers. Need to tighten the confidence level and add a supporting keyword dictionary before re-enabling enforcement.');
GO

-- The crawl orders by (LastModified, TicketId) and resumes from a composite
-- watermark, so the index covers both columns.
CREATE NONCLUSTERED INDEX IX_Tickets_LastModified_TicketId
    ON dbo.Tickets (LastModified, TicketId)
    INCLUDE (IsDeleted);
GO
