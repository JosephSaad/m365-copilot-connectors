-- ---------------------------------------------------------------------------
-- 01-create-hive-test-data.hql
-- The Hive half of the CDP test case: one table that should be indexed, and
-- one that must not be.
--
-- Run it with beeline against a Kerberised HiveServer2:
--
--     kinit
--     beeline -u "jdbc:hive2://hs2-01.corp.example:10001/default;transportMode=http;
--                 httpPath=cliservice;principal=hive/_HOST@CORP.EXAMPLE;ssl=true" \
--             -f hadoop/01-create-hive-test-data.hql
--
-- WHAT THE TWO TABLES ARE FOR
--
--   contracts.contract        Ordinary. A Ranger policy grants select on the
--                             whole table to a group, so the connector indexes
--                             it and stamps that group on every item.
--
--   contracts.contract_ppi    The negative case. Script 02 puts a Ranger ROW
--                             FILTER on this table. A row filter shows
--                             different rows to different people at query
--                             time, and an index holds one copy - so the
--                             connector must REFUSE to index it and route it
--                             to a live query instead.
--
--                             If rows from this table ever reach the index,
--                             the routing rule has regressed. That is the whole
--                             point of the table.
--
-- last_modified_ts is the watermark column and contract_ref breaks its ties.
-- Both are needed: two rows can share a timestamp, and a marker of only the
-- timestamp loses whichever of them had not been written when a run stopped.
-- ---------------------------------------------------------------------------

CREATE DATABASE IF NOT EXISTS contracts
COMMENT 'CaseWorks CDP connector test case';

USE contracts;

DROP TABLE IF EXISTS contract;

CREATE TABLE contract (
    contract_ref      STRING  COMMENT 'Natural key. Breaks watermark ties, so it must be unique',
    counterparty      STRING,
    status            STRING,
    owner             STRING,
    value_amount      DOUBLE,
    currency          STRING,
    start_date        TIMESTAMP,
    end_date          TIMESTAMP,
    notes             STRING  COMMENT 'Free text; becomes part of the indexed body',
    last_modified_ts  TIMESTAMP COMMENT 'Watermark column. UTC'
)
STORED AS PARQUET
TBLPROPERTIES ('comment' = 'Indexable: table-wide select, no row filter, no mask');

INSERT INTO contract VALUES
  ('C-1000', 'Northwind Traders Limited', 'Open', 'priya.raman',
   1250000.00, 'GBP',
   TIMESTAMP '2026-01-01 00:00:00', TIMESTAMP '2028-12-31 00:00:00',
   'Master services agreement for settlement reconciliation. Termination for convenience requires ninety days written notice. Liability capped at twelve months of fees.',
   TIMESTAMP '2026-08-20 10:00:00'),

  ('C-1001', 'Contoso Financial Services', 'Under review', 'daniel.okafor',
   480000.00, 'GBP',
   TIMESTAMP '2026-04-01 00:00:00', TIMESTAMP '2027-03-31 00:00:00',
   'Custody agreement renewal. Counterparty has requested a higher liability cap and a shorter notice period. Legal review outstanding.',
   TIMESTAMP '2026-08-20 10:00:00'),

  -- Same timestamp as C-1001 on purpose: this is the pair that proves the
  -- composite watermark. A run interrupted between them must resume at C-1002
  -- rather than re-reading both or skipping it.
  ('C-1002', 'Fabrikam Custody', 'Open', 'priya.raman',
   95000.00, 'EUR',
   TIMESTAMP '2025-11-15 00:00:00', TIMESTAMP '2026-11-14 00:00:00',
   'Custody services for the European book. Auto-renews annually unless either party gives sixty days notice.',
   TIMESTAMP '2026-08-20 10:00:00'),

  ('C-1003', 'Tailspin Brokerage', 'Closed', 'amara.nwosu',
   210000.00, 'USD',
   TIMESTAMP '2024-06-01 00:00:00', TIMESTAMP '2026-05-31 00:00:00',
   'Introducing broker agreement, terminated at expiry. Retained for the seven year records retention period.',
   TIMESTAMP '2026-08-21 09:30:00'),

  -- No contract_ref. The row mapping returns null for this one, so it is
  -- skipped and counted rather than given an invented item ID.
  (NULL, 'Unknown counterparty', 'Draft', 'system',
   0.00, 'GBP', NULL, NULL,
   'A row with no natural key, to prove the connector skips rather than invents one.',
   TIMESTAMP '2026-08-21 09:31:00');

-- ---------------------------------------------------------------------------
-- The table that must never be indexed.
-- ---------------------------------------------------------------------------

DROP TABLE IF EXISTS contract_ppi;

CREATE TABLE contract_ppi (
    contract_ref      STRING,
    counterparty      STRING,
    owning_desk       STRING COMMENT 'The column the row filter keys on',
    settlement_notes  STRING,
    last_modified_ts  TIMESTAMP
)
STORED AS PARQUET
TBLPROPERTIES ('comment' = 'NOT indexable: script 02 puts a Ranger row filter on this table');

INSERT INTO contract_ppi VALUES
  ('C-2000', 'Northwind Traders Limited', 'emea-desk',
   'Settlement instructions for the EMEA desk only.', TIMESTAMP '2026-08-20 11:00:00'),
  ('C-2001', 'Contoso Financial Services', 'amer-desk',
   'Settlement instructions for the AMER desk only.', TIMESTAMP '2026-08-20 11:00:00'),
  ('C-2002', 'Fabrikam Custody', 'apac-desk',
   'Settlement instructions for the APAC desk only.', TIMESTAMP '2026-08-20 11:00:00');

-- Statistics, so a preflight COUNT is answered from metadata rather than a scan.
ANALYZE TABLE contract COMPUTE STATISTICS;
ANALYZE TABLE contract_ppi COMPUTE STATISTICS;

SELECT 'contract' AS table_name, COUNT(*) AS rows FROM contract
UNION ALL
SELECT 'contract_ppi', COUNT(*) FROM contract_ppi;
