---
title: Deploying the crawl state database
description: Standing up and running ConnectorState — the six scripts and their order, the two service accounts, the delete guard and how to clear it, retention, backup posture, and what a lost or rewound state database costs.
---

# Deploying the crawl state database

Step-by-step instructions for `ConnectorState`, the database that holds
everything the Graph connector agent used to remember on a push tool's behalf.

**This is not a connector deployment.** No service is installed, nothing is
registered in the Microsoft 365 admin centre, and the database serves every push
tool at once rather than one of them. It is stood up once per environment, before
the first push tool that uses it, and then it is a database like any other:
backed up, purged, and monitored.

| | |
|---|---|
| **What it is** | One SQL Server database, `ConnectorState`, holding one schema, `crawl`. |
| **Where it runs** | Any instance the push host can reach. It does **not** have to be the instance holding the source data, and there is a good argument for it not being. |
| **What it holds** | Item IDs, item types, two hashes per item, run history, throttle history, checkpoints, cached identity mappings. **No item content and no property value** — see [section 3](#3-the-two-service-accounts). |
| **Who writes to it** | The connector, through sixteen procedures. Never through a table. |
| **Who reads it** | The dashboard, through six views and seven procedures. Never through a table. |
| **Scripts** | `sql/20` through `sql/25`, run in that order, once. |

If what you want is *why* this exists and what the source system has to
guarantee for it to work, that is
[`SOURCE-CONTRACT.md`](SOURCE-CONTRACT.md) and it is a twenty minute read that
saves an afternoon here. If what you want is the column list, that is
[`CRAWL-STATE-REFERENCE.md`](CRAWL-STATE-REFERENCE.md).

**The engine side is not in this document.** The configuration keys a push tool
uses to reach this database are not settled yet, so everything below is the
database side: what to run, as whom, what it should print, and what to do when
the delete guard fires. Nothing here depends on an `appsettings.json` key.

---

## 1. What it is, and why there is a database at all

A push tool talks to Microsoft Graph directly. There is no connector agent
between it and the tenant, and the agent is what used to remember three things:
what had already been sent, what each item looked like when it was sent, and how
far the last crawl got. Without that memory a push cannot detect a deletion,
cannot skip an unchanged item, and cannot resume. `ConnectorState` is that
memory, written down where it can be queried and audited.

**Why a separate database rather than a schema in `Ops`.** The connector's SQL
identity is read-only on the customer's data, and `sql/01` and `sql/13` go to
some trouble to keep it that way. Crawl state has to be *written* by that same
process. Putting it in `Ops` would mean granting write access inside the database
that holds the customer's records, and a security reviewer would read the grant
as "the search connector can write to `Ops`". Two databases keep both sentences
true at once: read-only on the data, read-write on its own bookkeeping. The
restore, retention and backup postures also differ, and by a lot — losing crawl
state costs one full recrawl, losing `Ops` costs the business.

---

## 2. Prerequisites, and the order the scripts run in

**Before you start:**

- A SQL Server instance the push host can reach. The scripts use nothing beyond
  the features in SQL Server 2016 SP1 — table types, filtered indexes, `OFFSET`
  paging, and `CREATE OR ALTER`, which is the one that sets the floor. Any
  supported version will take them.
- Data and log volumes with known paths. You will edit them into `sql/20`; see
  [section 4](#4-what-to-edit-in-sql20-before-you-run-it).
- The Windows accounts the connector and the dashboard run as. `sql/25` names
  `CONTOSO\svc_gca_reader` and `CONTOSO\svc_connector_dashboard`; replace both.
- Two levels of access for yourself, and they are not the same person in every
  estate: server-level rights to create a database and a login, and `db_owner`
  inside the new database. If a DBA holds the first and you hold the second, run
  the scripts in two sittings and split them at the line below.

**Run them in this order. The order is not a convention — each script assumes
the objects the one before it created, and `sql/25` grants `EXECUTE` on
procedures by name, so a grant on a procedure that does not exist yet fails
rather than being deferred.**

| # | Script | Creates | Run as | Runs in |
|---|---|---|---|---|
| 1 | `sql/20-crawl-state-database.sql` | The database, the `crawl` schema, six table types | `dbcreator` or `sysadmin` | `master`, then `ConnectorState` |
| 2 | `sql/21-crawl-state-tables.sql` | Eight tables and their indexes | `db_owner` on `ConnectorState` | `ConnectorState` |
| 3 | `sql/22-crawl-state-views.sql` | Six views | `db_owner` | `ConnectorState` |
| 4 | `sql/23-crawl-state-procedures.sql` | Eighteen procedures — the write path | `db_owner` | `ConnectorState` |
| 5 | `sql/24-crawl-state-reporting.sql` | Seven procedures — the dashboard's read path | `db_owner` | `ConnectorState` |
| 6 | `sql/25-crawl-state-least-privilege.sql` | Two logins, two users, two roles, the grants and the denials | `securityadmin` **and** `db_owner` — see below | `master`, then `ConnectorState` |

**`sql/25` needs rights in two places, and its own header understates one of
them.** The first half runs in `master` and issues `CREATE LOGIN`, which needs
`ALTER ANY LOGIN` — held by `securityadmin` and `sysadmin` and by nothing at
database level. The second half creates users, roles and grants inside
`ConnectorState`, which `db_owner` covers. `db_owner` alone cannot run the file:
it will fail on the first `CREATE LOGIN`. If the logins already exist because the
accounts are used elsewhere on the instance — and `CONTOSO\svc_gca_reader`
usually does exist already, because it is the identity that reads `Ops` — the
`IF NOT EXISTS` guards skip that half and `db_owner` is enough.

**Re-running.** `sql/21` is guarded object by object and will not alter a table
that already exists; a schema change ships as its own numbered migration rather
than as an edit to that file. `sql/22`, `sql/23` and `sql/24` are `CREATE OR
ALTER` throughout, so re-running one of them is how a changed view or procedure
is deployed. `sql/25` is idempotent and safe to re-run after any change to the
roles — running it is the cheapest way to prove the permission set has not
drifted.

**One thing to watch when re-running `sql/20`.** The `CREATE DATABASE` is
guarded, but the three `ALTER DATABASE` statements after it are not, and the
read-committed-snapshot one carries `WITH ROLLBACK IMMEDIATE`. Against a live
database that rolls back every transaction in flight and disconnects the
sessions holding them, which on a running crawl is an aborted run. Re-run
`sql/20` when nothing is crawling, or not at all.

---

## 3. The two service accounts

**Read this section first if you are reviewing rather than deploying.**

There are two principals, two roles, and **no table permission for either of
them**. That is the whole design, and it is what makes "what can each of these
two things do to this database" a question you answer by reading one file rather
than by auditing the queries a program happens to send.

| | `crawl_writer` | `crawl_reader` |
|---|---|---|
| Member | `svc_gca_reader` — the connector | `svc_connector_dashboard` — the dashboard |
| Where it runs | The push host | The web tier |
| May execute | The sixteen write procedures in `sql/23`, by name | The seven reporting procedures in `sql/24`, by name |
| May select | Nothing | The six views in `sql/22`, by name |
| Table permissions | **None.** Denied `INSERT`, `UPDATE`, `DELETE`, `ALTER`, `REFERENCES` and `SELECT` on the schema | **None.** Denied `INSERT`, `UPDATE`, `DELETE`, `ALTER`, `REFERENCES` on the schema |
| Also granted | `EXECUTE` on four of the six table types, which a table-valued parameter requires | — |

Five properties are worth stating plainly, because each one is a question a
reviewer asks:

- **The writer cannot read a table and the reader cannot write one.** Neither
  role holds a permission on any object of type `U`. The writes reach the tables
  through ownership chaining inside the procedures, which is unbroken here
  because the schema and its tables share an owner. Denying the caller direct DML
  therefore costs the connector nothing and bounds what a SQL injection defect in
  it could reach.
- **The denials are explicit, not merely absent.** Without them, a later
  `ALTER ROLE db_datareader ADD MEMBER svc_connector_dashboard` — the single most
  common reaction to a failing dashboard query — would silently grant the web
  tier read access to every table. `DENY` wins over `GRANT`, so that change has
  no effect and the person makes the correct fix instead.
- **`CONTROL` is deliberately absent from every `DENY` list.** `DENY CONTROL`
  denies every permission it implies, including the `EXECUTE` granted above, so
  it would break the connector while the `GRANT` rows still suggested access was
  configured. `sql/01` makes the same choice for the same reason.
- **The grants name procedures individually rather than granting `EXECUTE` on
  the schema.** A schema-level grant would include every procedure added later,
  which means a future procedure with a different threat profile is granted to
  the connector by the act of creating it. Naming them makes adding one a
  decision somebody has to take.
- **Two procedures are granted to neither role.**
  `crawl.uspResetCheckpoint` rewinds a connection to a full recrawl, and
  `crawl.uspPurgeHistory` deletes run history. A connector that could rewind
  itself after a bad run could do it unnoticed, and a connector that could purge
  its own history could erase the evidence of exactly the run whose history
  matters. Both are reachable by `db_owner`, which is what an operator and the
  retention job connect as.

**The reader is deliberately not denied `SELECT` at schema level**, and the
comment in `sql/25` says why: object-level `GRANT` and schema-level `DENY` are
evaluated at different scopes, and relying on that interaction would be clever
where the file is trying to be obvious. The reader's read access is bounded by
having no grant on anything except the six views; the DML denials are what stop
it writing.

**What neither role can reach, because it does not exist.** The store holds an
item's ID, its type, two `BINARY(32)` hashes, a byte count and some run numbers.
There is no content column, no property value, no title, no narrative, no
customer name. That is a property of the schema in `sql/21` rather than a filter
applied on the way out, which is why a view added to `sql/22` later cannot leak
one by accident. The two free-text columns in the whole schema are
`crawl.Run.ErrorKind` and `crawl.Run.ErrorMessage`, and the rule for those —
never a property value, never row content — is a constraint on the caller that
the schema cannot enforce. It is the same rule the logging policy applies
upstream, and for a stronger reason here: this database is readable by a wider
group than `Ops` is.

---

## 4. What to edit in `sql/20` before you run it

Four edits, all in `sql/20`, all before the first run. Nothing in `sql/21`
through `sql/24` needs touching; `sql/25` needs the two account names.

**1. The file paths.** The script ships with the instance layout it was written
against:

```sql
FILENAME = N'D:\SQLData\ConnectorState.mdf'
FILENAME = N'D:\SQLLogs\ConnectorState_log.ldf'
```

Change both to the instance's own data and log paths. A path the service account
cannot write to fails the `CREATE DATABASE` outright, which is the good failure;
a path on the wrong volume succeeds and is found months later.

**2. The sizes, if the corpus warrants it.**

```sql
SIZE = 256MB, FILEGROWTH = 128MB      -- data
SIZE =  64MB, FILEGROWTH =  64MB      -- log
```

These are starting points rather than limits, and they are sized for
bookkeeping rather than for data: the inventory is one narrow row per indexed
item — an ID, a type, two 32-byte hashes and a handful of numbers — so a
corpus in the low millions is still a small database. Size the data file for the
live item count plus the run history you intend to keep (see
[section 6](#6-retention)), and leave the growth increments **fixed** rather than
changing them to percentages. Percentage growth on a file that is already large
is how a crawl stalls for thirty seconds in the middle of a run and gets
diagnosed as a network problem.

Both `SIZE` clauses sit inside the `IF DB_ID(N'ConnectorState') IS NULL` guard,
so editing them after the database exists changes nothing. Resize an existing
database with `ALTER DATABASE ... MODIFY FILE` instead.

**3. The recovery model, only if your estate forces it.** `RECOVERY SIMPLE` is
the one policy choice the file makes for you, and [section 7](#7-backup-posture)
is the argument for it. If the estate mandates `FULL` for every database, change
that line — nothing downstream depends on the model — and read section 7 before
you do, because a `FULL` database with no log backup is how a volume fills.

**4. The database name, if it must differ.** `ConnectorState` is hard-coded in
the `USE` statement at the top of every subsequent script and in the
`IF DB_ID(...)` guard. Renaming it means editing all six files, so decide before
the first run rather than after.

`sql/25` additionally needs `CONTOSO\svc_gca_reader` and
`CONTOSO\svc_connector_dashboard` replaced with the accounts the connector
service and the dashboard's application pool actually run as. Each appears three
times — the `IF NOT EXISTS` guard, the `CREATE LOGIN`, and the `FOR LOGIN` clause
of the `CREATE USER` — so replace every occurrence, not the first.

---

## 5. Verification: what each script prints

Every script ends with a verification query. Run each script interactively and
read the result rather than checking only that no error appeared. Every one of
them is a sequence of batches, and a batch that fails leaves the batches after it
to run, so a script that produced errors somewhere in the middle still reaches
its verification query and still prints something.

| Script | Result sets | A healthy result |
|---|---|---|
| `sql/20` | 2 | One row: `ConnectorState`, `SIMPLE`, `is_read_committed_snapshot_on = 1`. Then six rows: `ItemIdList`, `ItemStateList`, `ItemTypeCountList`, `PhaseTimingList`, `PrincipalKeyList`, `ThrottleEventList` |
| `sql/21` | 1 | Eight rows — `Checkpoint`, `Connection`, `Item`, `PrincipalMap`, `Run`, `RunItemType`, `RunPhaseTiming`, `ThrottleEvent` — every `row_count` zero on a first run |
| `sql/22` | 7 | Six rows naming the views, then six empty result sets. The empty sets are the real test: they prove each view *executes*, not merely that it compiled |
| `sql/23` | 1 | **Eighteen rows.** The query is not filtered by name, so it returns every procedure in the `crawl` schema |
| `sql/24` | 1 | **Seven rows**, filtered by name to this file's own procedures |
| `sql/25` | 2 | The permission inventory, then the finding query, which must return **no rows** |

**The count in `sql/23` is the check that matters most.** Eighteen is the number
of procedures that file defines. Sixteen or seventeen means a `CREATE OR ALTER`
batch failed and the error is further up the output where a long script scrolls
it out of sight. Find the missing name in the list, scroll back to its batch, and
fix it before running `sql/25` — a `GRANT EXECUTE` on a procedure that does not
exist fails, and the failure names the procedure.

Note that the comment above `sql/23`'s verification query says "the sixteen
write-path procedures" and the one in `sql/24` says "the six reporting
procedures". Both under-count: `sql/23` defines eighteen, of which sixteen are
granted to `crawl_writer` and two are operator-only, and `sql/24` defines seven.
The queries are right and the comments are stale.

**Reading `sql/25`'s inventory.** Expect, for `crawl_writer`, twenty `GRANT`
rows — sixteen procedures and four table types — and six `DENY` rows against the
schema. For `crawl_reader`, thirteen `GRANT` rows — seven procedures and six
views — and five `DENY` rows. The four table-type grants display awkwardly: the
query's `LEFT JOIN` to `sys.objects` does not match a type, so those rows report
`object_type` as `SCHEMA` and an object name derived from the type's ID. They are
correct grants displayed badly, not findings.

`sql/20` defines six table types and `sql/25` grants four. `PrincipalKeyList` and
`ThrottleEventList` are ahead of the procedures that will take them — no
parameter in `sql/23` is declared as either — so the missing grants are not a
deployment error today. They become one the moment a procedure starts taking one,
and the symptom will be a permission error at the call site that reads as though
the procedure were missing.

**The finding query is the one to keep.** It returns any direct table permission
held by either role, and the expected result is zero rows for the life of the
deployment. Run it after any change to the roles, and put it in whatever
evidence pack the estate keeps — it is the query that proves the claim in
[section 3](#3-the-two-service-accounts) rather than restating it.

```sql
USE [ConnectorState];

SELECT  dp.name AS principal_name, o.name AS table_name, p.permission_name, p.state_desc
FROM        sys.database_permissions AS p
INNER JOIN  sys.database_principals  AS dp ON dp.principal_id = p.grantee_principal_id
INNER JOIN  sys.objects              AS o  ON o.object_id = p.major_id
WHERE       dp.name IN (N'crawl_writer', N'crawl_reader')
  AND       o.type = 'U'
  AND       p.state_desc = 'GRANT';
```

**Then leave it alone until a connector runs.** There is nothing to seed. A
connection registers itself: `crawl.uspRegisterConnection` is called at the start
of every run and inserts the row the first time it sees one. An empty
`crawl.Connection` after deployment is correct, not a missed step.

---

## 6. Retention

`crawl.uspPurgeHistory` is the only thing that removes history from this
database, and it is run from a scheduled job rather than by the connector. The
two other procedures that issue a `DELETE` — `uspSaveRunTiming` and
`uspRecordRunItemTypes` — only clear one run's own rows before re-inserting them.

```sql
EXEC crawl.uspPurgeHistory
        @ConnectionId      = N'consultingwork',
        @KeepRunDays       = 90,
        @KeepTombstoneDays = 180;
```

It returns one row: `RunsPurged`, `TombstonesPurged`, `PrincipalsPurged`.

**What it purges**

| | Kept for | Notes |
|---|---|---|
| `crawl.Run` rows, and their `ThrottleEvent` and `RunPhaseTiming` children | `@KeepRunDays`, default 90 | Closed runs only, and only those nothing still points at — see below |
| Tombstoned items — `crawl.Item` rows in state 3 | `@KeepTombstoneDays`, default 180 | On their own, longer clock. An item deleted and re-created inside the window is recognised as a resurrection; outside it, it is treated as new. Both are correct, only the first is free |
| Expired `crawl.PrincipalMap` entries | Thirty days past `ExpiresUtc`, **not a parameter** | The thirty days is hard-coded in the procedure. There is no setting for it |

**What it will not purge, and why**

- **A run any live inventory row still points at.** The delete sweep compares
  `Item.LastSeenRunId` against a run ID, and a dangling reference makes that
  arithmetic meaningless in a way nothing reports. In practice this means the
  most recent successful full run, and everything after it, survives any
  retention setting. That is correct rather than a limitation: those are the runs
  the sweep is about to reason from.
- **The run the checkpoint points at**, for the same reason.
- **Any run still open** — status 1. An abandoned process leaves one of those,
  and `crawl.uspBeginRun` reaps it on the connection's next run. Purge is not the
  mechanism for that.

Everything runs inside one transaction with `XACT_ABORT ON`, so a purge either
completes or changes nothing.

**Before the first purge, check one thing.** The procedure deletes the
`ThrottleEvent` and `RunPhaseTiming` rows belonging to a purgeable run, but it
does not delete that run's `crawl.RunItemType` rows — and `RunItemType` carries
`FK_RunItemType_Run` back to `crawl.Run` with no cascade. A purge that selects a
run which recorded a per-item-type breakdown will therefore fail on the foreign
key and, because of `XACT_ABORT`, roll the whole purge back. Nothing is lost when
that happens, but nothing is purged either, and the job reports the reference
error rather than a row count. Run the purge by hand once against one connection
before scheduling it, and read what it returns.

### A weekly SQL Agent job

`uspPurgeHistory` takes one connection at a time and has no all-connections
mode, so the job step loops. Run it as an identity that is `db_owner` on
`ConnectorState`: neither `crawl_writer` nor `crawl_reader` is granted this
procedure, deliberately.

```sql
USE [msdb];
GO

EXEC msdb.dbo.sp_add_job
        @job_name    = N'ConnectorState - purge crawl history',
        @description = N'Weekly retention for the crawl state store. Runs crawl.uspPurgeHistory '
                     + N'once per registered connection. See docs/CRAWL-STATE-DEPLOYMENT.md section 6.',
        @enabled     = 1,
        @owner_login_name = N'sa';
GO

EXEC msdb.dbo.sp_add_jobstep
        @job_name       = N'ConnectorState - purge crawl history',
        @step_name      = N'Purge every connection',
        @subsystem      = N'TSQL',
        @database_name  = N'ConnectorState',
        @retry_attempts = 0,
        @on_success_action = 1,     -- quit reporting success
        @on_fail_action    = 2,     -- quit reporting failure; the history is the evidence
        @command = N'
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @ConnectionId NVARCHAR(64);

DECLARE Connections CURSOR LOCAL FAST_FORWARD FOR
    SELECT ConnectionId FROM crawl.Connection ORDER BY ConnectionId;

OPEN Connections;
FETCH NEXT FROM Connections INTO @ConnectionId;

WHILE @@FETCH_STATUS = 0
BEGIN
    RAISERROR (N''Purging %s'', 0, 1, @ConnectionId) WITH NOWAIT;

    EXEC crawl.uspPurgeHistory
            @ConnectionId      = @ConnectionId,
            @KeepRunDays       = 90,
            @KeepTombstoneDays = 180;

    FETCH NEXT FROM Connections INTO @ConnectionId;
END

CLOSE Connections;
DEALLOCATE Connections;';
GO

EXEC msdb.dbo.sp_add_schedule
        @schedule_name          = N'Weekly - Sunday 03:00',
        @freq_type              = 8,        -- weekly
        @freq_interval          = 1,        -- Sunday
        @freq_recurrence_factor = 1,
        @active_start_time      = 030000;
GO

EXEC msdb.dbo.sp_attach_schedule
        @job_name      = N'ConnectorState - purge crawl history',
        @schedule_name = N'Weekly - Sunday 03:00';
GO

EXEC msdb.dbo.sp_add_jobserver
        @job_name = N'ConnectorState - purge crawl history';
GO
```

Three notes on the schedule:

- **Put it outside every crawl window.** The purge takes a transaction across
  `crawl.Run` and `crawl.Item`, which are the two tables a running crawl is
  writing. Read-committed snapshot keeps readers off writers, not writers off
  each other.
- **`@on_fail_action = 2` is deliberate.** A purge that fails should show as a
  failed job, because the two realistic causes — the foreign key above, and a
  connection whose inventory has grown past the transaction the instance will
  hold — are both things somebody should look at rather than a retry should
  absorb.
- **`RAISERROR ... WITH NOWAIT` names the connection in the job history.** With
  one purge per connection in one step, the alternative is a failure that says
  only that the step failed.

---

## 7. Backup posture

**`RECOVERY SIMPLE` is deliberate.** The worst case for losing this database
entirely, between backups, is a single full recrawl: every write in the schema is
an upsert, so a connector meeting an empty store rebuilds it by doing its job.
That costs time and Microsoft Graph write quota. It does not cost correctness,
and it cannot produce a wrong index — see [section 9](#9-if-the-state-database-is-lost-or-rewound).
Paying for point-in-time recovery of a cache is paying for the wrong thing.

What that argument does **not** cover is the run history. Crawl history, throttle
history and per-run timings are evidence rather than cache: nothing rebuilds them,
and in a regulated estate "prove the connector ran nightly for the last quarter"
is a question somebody eventually asks. If that is the case here, the reason to
back this database up is the audit trail, not the inventory. Say so in the
backup ticket, because it changes the retention the backup needs rather than the
frequency.

A proportionate posture, absent a policy that says otherwise:

| | |
|---|---|
| Full backup | Weekly, aligned with the retention job rather than with `Ops` |
| Differential | Not needed. The database is small and the recovery objective is "one recrawl" |
| Log backup | Not applicable under `SIMPLE` |
| Restore test | Once, at deployment, to prove the path exists. There is nothing about a restore of this database that is delicate |

**If the estate mandates `FULL` for every database.** Change the
`ALTER DATABASE [ConnectorState] SET RECOVERY SIMPLE` line in `sql/20`, or run
`ALTER DATABASE [ConnectorState] SET RECOVERY FULL` afterwards — nothing
downstream reads the recovery model. Then **schedule log backups in the same
change**. Under `FULL` the log is truncated by a log backup and by nothing else,
and this database's write pattern is a batch of upserts every crawl interval,
which generates log continuously. A `FULL` database with no log backup fills the
log volume, and it fills it during a crawl, which stops the crawl and everything
else on that volume. This is the one place where following the estate's default
without the accompanying job is worse than the deviation the default was
protecting against.

---

## 8. The delete guard

**This is the most important operational section in this document.** Read it
before the first production crawl, not after the first refusal.

### What the guard is

`crawl.uspGetPendingDeletes` is the procedure that turns "the source stopped
returning this item" into "delete it from the index". It refuses to answer in
four situations, and one of them is a judgement about size:

| Check | Result |
|---|---|
| `@RunId` is not a run | `THROW 50004` — *Unknown RunId* |
| `@RunId` belongs to a different connection | `THROW 50005` — *RunId belongs to a different connection. Refusing to sweep* |
| The run's mode is incremental | `THROW 50006` — an incremental run reads a subset, so absence from it means nothing |
| The run is a dry run | An **empty result set**, and no row changes state |
| The sweep would remove more than `@MaxDeletePercent` of the live corpus | `THROW 50007`, with the numbers in the message |

`@MaxDeletePercent` defaults to `10.00` and the comparison is strictly greater,
so a sweep of exactly ten per cent proceeds. `@OverrideGuard` defaults to `0`.

**Why ten per cent.** A real day's deletions in a ticketing or engagement corpus
are a fraction of a per cent. Ten per cent is not a plausible day; it is the
signature of a source that returned too few rows and completed cleanly, and each
of the realistic causes presents identically:

- a view redefined or dropped,
- a `WHERE` clause that stopped matching after a data change,
- a permission quietly revoked, so the connector now reads a subset,
- a source database restored to an earlier point,
- item IDs that changed — a normalisation edit, a prefix change — so every item
  looks new and the entire previous corpus looks deleted.

Without the guard, each of those is faithfully carried out against the index.

**The refusal happens before anything moves.** The percentage is computed, the
`THROW` fires, and the `UPDATE` that moves rows to pending delete is never
reached. A refused sweep leaves the inventory exactly as it was. The run fails;
the index is untouched.

### The message you will see

```
Delete sweep refused. It would remove 4182 of 11904 live items (35.13%),
above the 10.00% guard. This is far more likely to be a source that returned
too few rows - a dropped view, a revoked permission, a filter that matched
nothing - than a real deletion of that size. Verify the source count, then
re-run with the guard raised deliberately.
```

The two numbers in it are the whole investigation's starting point: how many
items the store holds live for this connection, and how many of them this run
did not see.

### Investigating a refusal

Work through these in order. The first three take a minute and settle most
refusals.

**1. What did the run actually read?** A run that read far fewer rows than its
predecessor is the answer, and it is one query:

```sql
SELECT TOP (10) RunId, Mode, Status, StartedUtc,
       ItemsRead, ItemsWritten, ItemsUnchanged, UnchangedPercent
FROM   crawl.vwRunHistory
WHERE  ConnectionId = N'consultingwork'
ORDER BY StartedUtc DESC;
```

`ItemsRead` collapsing while the source is believed unchanged is a source
problem, not a deletion.

**2. Is `UnchangedPercent` near zero on this run?** If it is, and the previous
runs sat above ninety, **the item IDs have changed**. Every item looked new, was
rewritten, and the entire previous corpus now looks deleted. Do not override
anything. Find the change that altered ID composition — a prefix, a case
normalisation, a trimming rule — and revert it; see the determinism rules in
[`SOURCE-CONTRACT.md`](SOURCE-CONTRACT.md#4-deterministic-item-ids). Overriding
here deletes the whole corpus and re-uploads it under new IDs, which breaks every
existing citation and deep link and costs the quota twice.

**3. Which kinds of item went missing?**

```sql
DECLARE @RunId BIGINT = 4471;          -- the run that was refused

SELECT   ItemType, COUNT(*) AS Missing
FROM     crawl.Item
WHERE    ConnectionId  = N'consultingwork'
  AND    State         = 1
  AND    LastSeenRunId < @RunId
GROUP BY ItemType
ORDER BY Missing DESC;
```

One item type missing entirely, with the others intact, is a query or a view
problem — a whole family stopped being returned. Losses spread evenly across
every type point at a permission change or a restored source.

**4. Sample the missing IDs and look them up in the source by hand.**

```sql
SELECT TOP (20) ItemId, ItemType, LastWrittenUtc, UnchangedStreak
FROM   crawl.Item
WHERE  ConnectionId  = N'consultingwork'
  AND  State         = 1
  AND  LastSeenRunId < @RunId
ORDER BY ItemId;
```

Twenty IDs is enough. If the records are still in the source and the connector's
query does not return them, the connector's query is the fault. If they are
genuinely gone, the deletion is real and you can move on to clearing the guard.

**5. Reconcile against the expected record count** the source team gave you in
the [`SOURCE-CONTRACT.md` checklist](SOURCE-CONTRACT.md#checklist-to-send-the-source-team).
That number exists for exactly this moment. Without it, "is 7,722 the right
number of live customers" is a question nobody in the room can answer.

### Clearing it deliberately

Only after the investigation says the deletion is real.

There are two ways, and the first is strongly preferred because it leaves a
number in the audit trail rather than a switch:

```sql
-- Raise the threshold to a figure above the observed percentage,
-- chosen because you verified it, not to make the error go away.
EXEC crawl.uspGetPendingDeletes
        @ConnectionId     = N'consultingwork',
        @RunId            = 4471,
        @MaxDeletePercent = 40.00;
```

```sql
-- The blunt form. Removes the guard entirely for this call.
EXEC crawl.uspGetPendingDeletes
        @ConnectionId  = N'consultingwork',
        @RunId         = 4471,
        @OverrideGuard = 1;
```

**Four things to know before running either.**

- **Running it moves the items to pending delete, and that is the decision.**
  The procedure updates the state and returns the list; it does not call Graph.
  The `DELETE`s follow, because every subsequent sweep returns the whole pending
  backlog, not only what it found itself. Between the two you have committed to
  the deletion without having carried it out.
- **The state can be walked back, but not by a procedure.** An item returns to
  live only when it is written again — `crawl.uspRecordWritten` sets state 1
  whatever the row was before, which is also how a resurrected record recovers on
  its own. Nothing in `sql/23` moves an item from pending delete back to live
  without writing it, so an operator correcting a mistake is issuing an `UPDATE`
  against `crawl.Item` as `db_owner`. Neither role can, by design.
- **The guard is measured over the connection, not per item type.** A connection
  holding a large family and a small one can lose the small family entirely and
  still sit under ten per cent. The guard catches the catastrophic case; query 3
  above is what catches the rest, and it is worth running after a large sweep
  even when nothing was refused.
- **Do not put the override in the scheduled path.** A guard that is always
  overridden is a guard that has been deleted, and the failure it exists for is
  silent and irreversible. Raising `@MaxDeletePercent` for one run is an
  operator action with a reason attached; raising it permanently is a decision to
  stop checking.

### The related signal: deletes that stay pending

A row that sits in `crawl.vwPendingDeletes` across several runs is a `DELETE`
Graph refused and kept refusing. It is the failure the connector agent used to
absorb silently — an item the source dropped, still answering searches.

Watch it with the run number rather than with the clock:

```sql
SELECT   p.ConnectionId, p.ItemType, COUNT(*) AS Stuck, MIN(p.LastSeenRunId) AS OldestSeenRun
FROM     crawl.vwPendingDeletes AS p
GROUP BY p.ConnectionId, p.ItemType
ORDER BY Stuck DESC;
```

**Do not build the alert on `AgeMinutes`.** The view computes it from
`LastWrittenUtc`, and nothing stamps that column when an item moves to pending
delete — `uspGetPendingDeletes` sets the state and only the state. On a healthy
corpus, where most items were last written weeks ago, every freshly pending item
is already "weeks old" the moment it is marked, so a rule of the form "anything
older than one crawl interval" fires on the first sweep and every sweep after it.
`LastSeenRunId`, compared against the newest completed full run for the
connection, is the number that actually distinguishes a backlog from a sweep in
progress.

---

## 9. If the state database is lost or rewound

Both cases are safe. Neither produces a wrong index. Both cost Graph write
quota, and one of them costs evidence.

### The database is lost entirely

Re-run `sql/20` through `sql/25`. There is nothing to seed and nothing to
re-register by hand: `crawl.uspRegisterConnection` runs at the start of every
crawl and inserts the connection row when it does not find one.

What the next run does, and why each part is safe:

| | What happens | Why it is safe |
|---|---|---|
| Inventory | Empty, so every item is a cache miss and every item is written | Every write is an upsert against Graph. The index converges on the same content it already had |
| Deletes | None are issued. The live count is zero, so the sweep finds nothing missing and the percentage guard computes zero | The store cannot conclude a deletion from an inventory it does not have, and it does not try |
| Checkpoint | Absent. `crawl.uspBeginRun` returns `FullCrawlDue = 1` because `HasCheckpoint = 0`, whatever the mode asked for | An incremental read with no marker reads from the beginning of time — a full crawl that has told the sweep it was not one. The flag is what stops that |
| Run history | Gone, and not recoverable | This is the real loss. See [section 7](#7-backup-posture) |

The cost is one full crawl's worth of Graph writes and one full crawl's worth of
time. On a corpus where a steady-state run writes two per cent of the items, that
is fifty runs' quota in one night.

**Deletions that occurred while the store was gone are missed until the next full
crawl** — and once the inventory has been rebuilt, they are not "missed" so much
as never known: the rebuilt inventory records the source as it is now, so items
already deleted before the rebuild simply never enter it. They stay in the index
until something else removes them. If the index is believed to contain items the
source has dropped, `deploy/Compare-SourceToIndex.ps1` is still the tool that
finds them; the state store never claimed to be the only check.

### The database is restored to an earlier point

The inventory, the run history and the checkpoint all move back together, which
is what keeps it consistent: no `Item` row can point at a run that no longer
exists, because both came from the same backup.

| | What happens | Why it is safe |
|---|---|---|
| Items changed since the restore point | Their stored hashes are stale, so they compare as changed and are rewritten | Upsert. Correct content, at the cost of the write |
| Items created since the restore point | Absent from the restored inventory, so they are written as new | Upsert against an item ID Graph already holds. Same content, same ID |
| Items deleted from the source since the restore point | Present again as live rows. The next **full** crawl will not see them and will sweep them | This is the case that can trip the percentage guard. It is a real deletion; investigate as in section 8, then clear the guard deliberately |
| Items already tombstoned before the restore point | Live again in the restored copy, so the sweep re-issues their `DELETE`s | The connector passes 404s to `crawl.uspConfirmDeletes` as confirmations — an item Graph says is not there is an item that is not there — so they tombstone again |
| Checkpoint | Rewinds to the restored marker, so the next incremental read re-reads a window it has already read | The hashes absorb it: rows read again compare unchanged and are marked seen rather than written. `crawl.uspSaveCheckpoint` then moves the marker forward again from the restored value |

**Run one full crawl before trusting delete detection again**, and expect the
guard to have an opinion about it. A restore of any age is exactly the situation
the guard was written for, and the correct response is the investigation in
section 8 rather than an override typed from memory.

**What a restore cannot do is corrupt the index.** The state store is a record of
what the connector believes it wrote. Every operation that reconciles it with
reality is an upsert or a re-read, so a store that is behind converges on the
next full crawl and a store that is empty rebuilds itself. The only irreversible
operation in the whole design is a delete, and that is the one thing behind a
guard.

---

## Where to look next

| | |
|---|---|
| [`SOURCE-CONTRACT.md`](SOURCE-CONTRACT.md) | What the source system has to guarantee for any of this to work, and what a source that meets only some of it gets |
| [`CRAWL-STATE-REFERENCE.md`](CRAWL-STATE-REFERENCE.md) | Every table, view and procedure, with columns, parameters and error numbers |
| [`SECURITY.md`](SECURITY.md) | The `STATE-*` control rows, each with the query that proves it |
| [`TROUBLESHOOTING-DIRECT-PUSH.md`](TROUBLESHOOTING-DIRECT-PUSH.md) | When the push tool itself is the thing misbehaving |
