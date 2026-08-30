---
title: Deploying the crawl state database
description: Standing up and running ConnectorState — the six scripts that build the state database and the order they run in, the two service accounts, the delete guard and how to clear it, retention, backup posture, what a lost or rewound state database costs, and sql/26, which changes the source rather than the state.
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
| **Who writes to it** | The connector, through seventeen procedures. Never through a table. |
| **Who reads it** | The dashboard, through six views and seven procedures. Never through a table. |
| **Scripts** | `sql/20` through `sql/25`, run in that order, once. `sql/26` is a separate matter — it changes the *source* database, not this one. See [section 10](#10-sql26-making-the-timesheet-source-readable-incrementally). |

If what you want is *why* this exists and what the source system has to
guarantee for it to work, that is
[`SOURCE-CONTRACT.md`](SOURCE-CONTRACT.md) and it is a twenty minute read that
saves an afternoon here. If what you want is the column list, that is
[`CRAWL-STATE-REFERENCE.md`](CRAWL-STATE-REFERENCE.md).

**Pointing a connector at it.** The database is inert until a push tool is told
where it is. One key does that, under `Settings` in the connector's
`appsettings.json`:

```json
"Settings": {
  "StateConnectionString": "Server=SQLPROD01;Database=ConnectorState;Integrated Security=true;Encrypt=true"
}
```

Absent or blank, the connector runs exactly as it did before this database
existed: it writes every item on every run and never deletes. That is a
supported configuration, not a broken one — but nothing in this document has any
effect until the key is set.

It must use Integrated Security. `CrawlStateWiring.FromSettings` refuses a
connection string containing a password rather than connecting with one: the
service identity is the database principal `sql/25` grants `crawl_writer` to,
and a password here would be a secret in a file copied to every deployment host.

The other keys a connector reads are `Batch`, `Writers`, `Incremental`,
`MaxDeletePercent`, `FullEveryHours` and `GraphProxy`. Everything below is the
database side: what to run, as whom, what it should print, and what to do when
the delete guard fires.

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

**Run them in this order. The order is not a convention, and two of the
dependencies are not deferred.** Each script assumes the objects the one before
it created. `sql/25` grants `EXECUTE` on procedures by name, so a grant on a
procedure that does not exist yet fails rather than being deferred. And `sql/24`
selects a column that `sql/40` adds, which is why `sql/40` runs before it — see
below.

| # | Script | Creates | Run as | Runs in |
|---|---|---|---|---|
| 1 | `sql/20-crawl-state-database.sql` | The database, the `crawl` schema, six table types | `dbcreator` or `sysadmin` | `master`, then `ConnectorState` |
| 2 | `sql/21-crawl-state-tables.sql` | Eight tables and their indexes | `db_owner` on `ConnectorState` | `ConnectorState` |
| 3 | `sql/22-crawl-state-views.sql` | Six views | `db_owner` | `ConnectorState` |
| 4 | `sql/23-crawl-state-procedures.sql` | Nineteen procedures — the write path | `db_owner` | `ConnectorState` |
| 5 | `sql/40-crawl-state-per-type-duplicates.sql` | `ItemsDuplicate`, and **recreates a table type** — see below. **Must run before `sql/24`** | `db_owner` | `ConnectorState` |
| 6 | `sql/24-crawl-state-reporting.sql` | Seven procedures — the dashboard's read path | `db_owner` | `ConnectorState` |
| 7 | `sql/25-crawl-state-least-privilege.sql` | Two logins, two users, two roles, the grants and the denials | `securityadmin` **and** `db_owner` — see below | `master`, then `ConnectorState` |
| 8 | `sql/28-crawl-state-hash-version.sql` | The hash-version column and the check that escalates a run | `db_owner` | `ConnectorState` |
| 9 | `sql/29-crawl-state-partial-status.sql` | Run status 5, `partial`, and reclassifies existing rows | `db_owner` | `ConnectorState` |
| 10 | `sql/33-crawl-state-negative-ttl.sql` | The two principal TTL columns and the clamp in `uspCachePrincipal` | `db_owner` | `ConnectorState` |
| 11 | `sql/34-crawl-state-live-item-ids.sql` | `uspListLiveItemIds`, read-only, for the dry-run delete preview | `db_owner` | `ConnectorState` |
| 12 | `sql/41-crawl-state-compare-and-see.sql` | `uspCompareAndSee`, the one-call compare | `db_owner` | `ConnectorState` |
| 13 | `sql/43-crawl-state-run-lock.sql` | The heartbeat lease: one live crawl per connection | `db_owner` | `ConnectorState` |
| 14 | `sql/30-verify-set-options.sql` | Nothing — it checks what everything above created | any reader | `ConnectorState`, and every other database holding modules |
| 15 | `sql/42-verify-least-privilege.sql` | Nothing — it exercises the roles `sql/25` created | `db_owner` | `ConnectorState` |
| 16 | `sql/44-agent-jobs-availability-group.sql` | Nothing in `ConnectorState` — it edits the two SQL Agent job steps | `SQLAgentOperatorRole` in `msdb` | **`msdb`, on every replica** |

⚠️ **Step 16 is the only one that does not run against `ConnectorState`, and the
only one that runs more than once.** SQL Agent jobs live in `msdb`, which is not
a user database and does **not** fail over with an Availability Group. Deploy the
jobs on the primary alone and they vanish at the first failover, silently:
nothing runs, nothing errors, retention stops bounding the history table and the
trigger health check stops watching the triggers. Deploy them on every replica
without `sql/44` and every replica runs them on schedule, so a secondary — where
`ConnectorState` is unreadable or read-only — fails its retention job nightly and
pages whoever wired the alerting. `sql/44` prepends a primary-replica guard to
both job steps so the jobs can be deployed everywhere and still act in one place.

It is safe and pointless on a standalone instance, which is the case it was
verified against: `sys.fn_hadr_is_primary_replica` returns `NULL` off an
Availability Group, the guard treats only `0` as "not my turn", and the jobs run
as before. **The secondary path has not been exercised** — that needs a two-node
rig, and `sql/44`'s own verification block says so rather than implying it works.

Skip step 16 entirely if you have no Availability Group and never will; run
`sql/27` and `sql/32` first if you want the jobs at all, since `sql/44` edits
them and reports plainly when they are absent.

⚠️ **`sql/40` runs at step 5, before `sql/24`, and the order is not cosmetic.**
`sql/24` creates `uspGetRun`, which projects `t.ItemsDuplicate` from
`crawl.RunItemType` — a column `sql/21` does not create and `sql/40` adds. SQL
Server's deferred name resolution covers a missing *table*, so a procedure may
reference one that does not exist yet; it does **not** cover a missing *column*
on a table that already exists. `crawl.RunItemType` exists from step 2 onwards,
so ordering `sql/40` after `sql/24` makes the `CREATE OR ALTER PROCEDURE` fail
outright:

```
Msg 207, Level 16, State 1, Procedure uspGetRun
Invalid column name 'ItemsDuplicate'.
```

**And it does not stop there.** `uspGetRun` is then absent, so `sql/25`'s
`GRANT EXECUTE` on it fails in turn with `Msg 15151`. One ordering mistake costs
the dashboard's drill-down page and then reports itself as a permissions problem
rather than as a schema one, which is the wrong place to start looking.

This was reproduced on a scratch database rather than reasoned about, because
"deferred name resolution does not cover columns" is exactly the kind of claim
that is easy to have backwards. It is invisible on an **upgrade**, where the
column already exists by the time `sql/24` is re-run, which is why the earlier
order survived until a new environment was stood up. `sql/40` cannot move
earlier than `sql/23` either — it `CREATE OR ALTER`s a procedure that `sql/23`
also defines, so running it first would have `sql/23` overwrite it with the
older body.
**`sql/40` recreates a table type, which destroys two grants.**
`crawl.ItemTypeCountList` cannot be altered — only dropped — and it cannot be
dropped while `uspRecordRunItemTypes` references it, so the script drops the
procedure, drops and recreates the type, recreates the procedure, and re-grants
`EXECUTE` to `crawl_writer` on **both**. The type's grant is the one that is easy
to overlook, because a table type carrying a permission at all is unusual, and
without it the push identity is refused at the *end* of every run — after the
crawl has already done all its work. The script verifies both grants and says so.
At step 5 that re-grant is belt and braces, because `sql/25` runs afterwards and
issues both grants anyway. On an **upgrade**, where `sql/25` ran long ago, it is
the only thing putting them back.

**Run `sql/30` last, and run it in `Ops` too.** It is cheap, it is read-only, and
it catches the failure mode that costs the most to diagnose: a module created
from a client whose `QUOTED_IDENTIFIER` was off, which deploys cleanly and is
refused at execution days later.
**`sql/25` needs rights in two places, and its own header understates one of
them.** The first half runs in `master` and issues `CREATE LOGIN`, which needs
`ALTER ANY LOGIN` — held by `securityadmin` and `sysadmin` and by nothing at
database level. The second half creates users, roles and grants inside
`ConnectorState`, which `db_owner` covers. `db_owner` alone cannot run the file:
it will fail on the first `CREATE LOGIN`. If the logins already exist because the
accounts are used elsewhere on the instance — and `CONTOSO\svc_gca_reader`
usually does exist already, because it is the identity that reads `Ops` — the
`IF NOT EXISTS` guards skip that half and `db_owner` is enough.

**`sql/26` is not part of this sequence.** It changes the source database, needs
SQL Server 2022, and can be run before, after or never. [Section
10](#10-sql26-making-the-timesheet-source-readable-incrementally) covers it.

**Four other scripts run against the SOURCE database, not `ConnectorState`.**
`sql/12` (the item views) and `sql/26` (the cascading timestamp) are the two the
connector reads through. `sql/31` and `sql/32` add the trigger health check and
its SQL Agent job, and are only worth deploying where `sql/26` is — a health
check for triggers that do not exist reports nothing useful. `sql/35` verifies
that the incremental view and the full view return the same items and is
read-only, so it is safe to run at any time and is the thing to run first when an
incremental crawl reads a number nobody expected.

⚠️ **Do not re-run `sql/26` wholesale against a populated source.** Its backfill
disables all three triggers and rewrites `EffectiveLastModified` on every row the
triggers have legitimately moved since the last backfill — 74,034 rows on the
reference corpus. That opens a window in which the source accepts writes while
the column stops moving, and invents an enormous delta for the next incremental
crawl. Deploy the section you changed.

**Re-running.** `sql/21` is guarded object by object and will not alter a table
that already exists; a schema change ships as its own numbered migration rather
than as an edit to that file. `sql/22`, `sql/23` and `sql/24` are `CREATE OR
ALTER` throughout, so re-running one of them is how a changed view or procedure
is deployed. `sql/25` is idempotent and safe to re-run after any change to the
roles — running it is the cheapest way to prove the permission set has not
drifted.

**Every script that creates a module or a filtered index sets
`QUOTED_IDENTIFIER ON`.** It matters for two different reasons, and `sql/30`
only checks the first.

*Stored with the module.* SQL Server records that option *with each module* as
it stood in the session that created it, and replays the stored value on every
execution regardless of what the caller sets. sqlcmd connects with it OFF; SSMS
connects with it ON. So the same file produces a working procedure from a query
window and a broken one from the command line, with identical output both times.

It is not cosmetic. `crawl.Item` carries a filtered index, and any `UPDATE`
against a table with one is refused when the calling module holds
`QUOTED_IDENTIFIER OFF`. `uspBeginRun` then fails with error 1934 — *"UPDATE
failed because the following SET options have incorrect settings"* — the next
time a connector starts, which may be days after the deployment that caused it,
in an application nobody has touched. That is how this was found: a crawl that
could not open a run, hours after a deploy that reported success.

*Required to create the index at all.* `sql/21` needs the option for a second
reason: `CREATE INDEX` refuses a **filtered** index outright unless it is ON,
and three of that file's indexes are filtered — `IX_Run_Open`, `IX_Item_Sweep`
and `IX_Item_NotLive`. Until `sql/21` set it, the command-line path produced a
database that *looked* complete — eight tables, every view and procedure, and
`sql/30` reporting OK — with those three indexes silently absent. Nothing
caught it: a failed batch does not stop the batches after it, so the three
`Msg 1934`s scrolled past mid-output, `sql/21`'s own verification still printed
its eight table names, and `sql/30` checks modules, not indexes.

⚠️ **Any environment deployed from the command line before this fix is missing
those three indexes**, and it is the delete sweep and the open-run lookup that
pay for it. Counting them is the check, and it is read-only:

```sql
SELECT i.name, i.has_filter
FROM        sys.indexes AS i
INNER JOIN  sys.objects AS o ON o.object_id = i.object_id
INNER JOIN  sys.schemas AS s ON s.schema_id = o.schema_id
WHERE       s.name = N'crawl' AND i.type = 2
ORDER BY    i.name;
```

Six rows, three with `has_filter = 1`. Anything less means re-running `sql/21`,
which is guarded object by object and will add only what is missing.

The `SET` statements at the top of `sql/12`, `sql/21` to `sql/24`, `sql/26`,
`sql/28`, `sql/29`, `sql/31`, `sql/33` to `sql/35` and `sql/40` to `sql/42` make
the result independent of the client. Run `sql/30` last, in every database
holding modules; it lists any offender by name and throws `50030` so a pipeline
stops there rather than at the connector.

**One thing to watch when re-running `sql/20`.** The `CREATE DATABASE` is
guarded, but the three `ALTER DATABASE` statements after it are not, and the
read-committed-snapshot one carries `WITH ROLLBACK IMMEDIATE`. Against a live
database that rolls back every transaction in flight and disconnects the
sessions holding them, which on a running crawl is an aborted run. Re-run
`sql/20` when nothing is crawling, or not at all.

### Running these against a database that is not called `ConnectorState`

Every script here carries a hard-coded `USE [ConnectorState];` near the top, and
the source fixture scripts carry `USE [Ops];`. That is right for the deployment
this document describes and wrong for every other database — and it fails in the
dangerous direction.

**`sqlcmd -d` does not help.** `-d` sets the *initial* database; the `USE`
statement then moves the session somewhere else. So

```
sqlcmd -d ConnectorState_DrillRestore -i sql/21-crawl-state-tables.sql
```

creates its tables in **`ConnectorState`**, reports success, and leaves you
believing the drill database was built. On an estate where the live database is
read-only by policy and the drill copy differs from it by a suffix, that is a
silent write to production.

Use `deploy/Invoke-StateScripts.ps1`, which stages each file with the name
substituted, **asserts that no reference to the original name survives**, and
only then runs anything. A rename that half worked would address two databases
at once, so one surviving reference stops the whole set before the first
statement executes. It refuses outright to target `ConnectorState` or `Ops`.

```powershell
.\deploy\Invoke-StateScripts.ps1 -Database ConnectorState_DrillRestore -TrustServerCertificate -Scripts @(
    'sql/20-crawl-state-database.sql',
    'sql/21-crawl-state-tables.sql',
    'sql/22-crawl-state-views.sql',
    'sql/23-crawl-state-procedures.sql',
    'sql/40-crawl-state-per-type-duplicates.sql',
    'sql/24-crawl-state-reporting.sql',
    'sql/28-crawl-state-hash-version.sql',
    'sql/29-crawl-state-partial-status.sql',
    'sql/33-crawl-state-negative-ttl.sql',
    'sql/34-crawl-state-live-item-ids.sql',
    'sql/41-crawl-state-compare-and-see.sql',
    'sql/43-crawl-state-run-lock.sql')
```

Three situations need it: the disaster recovery drill in
[DISASTER-RECOVERY.md](DISASTER-RECOVERY.md), which restores to
`ConnectorState_DrillRestore` precisely so the live database is never the
target; a second test rig standing beside a live one; and a second connector
estate on one instance.

One further thing that switch does and `sqlcmd` on its own does not: it passes
`-I`, so `QUOTED_IDENTIFIER` is ON for the connection. `crawl.Run` carries
filtered indexes, and an `INSERT` against a table with one fails
`Msg 1934` when that setting is OFF — which is how `sqlcmd` connects by default.

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
| May execute | The seventeen write procedures in `sql/23`, by name | The seven reporting procedures in `sql/24`, by name |
| May select | Nothing | The six views in `sql/22`, by name |
| Table permissions | **None.** Denied `INSERT`, `UPDATE`, `DELETE`, `ALTER`, `REFERENCES` and `SELECT` on the schema | **None.** Denied `INSERT`, `UPDATE`, `DELETE`, `ALTER`, `REFERENCES` on the schema |
| Also granted | `EXECUTE` on all six table types, which a table-valued parameter requires | — |

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
| `sql/23` | 1 | **Nineteen rows.** The query is not filtered by name, so it returns every procedure in the `crawl` schema — which is why the same query re-run after `sql/24` returns twenty-six |
| `sql/24` | 1 | **Seven rows**, filtered by name to this file's own procedures |
| `sql/25` | 2 | The permission inventory, then the finding query, which must return **no rows** |

**The count in `sql/23` is the check that matters most.** Nineteen is the number
of procedures that file defines — seventeen granted to `crawl_writer`, plus
`uspResetCheckpoint` and `uspPurgeHistory`, which deliberately are not. Anything
short of nineteen means a `CREATE OR ALTER` batch failed and the error is further
up the output where a long script scrolls it out of sight. Find the missing name
in the list, scroll back to its batch, and fix it before running `sql/25` — a
`GRANT EXECUTE` on a procedure that does not exist fails, and the failure names
the procedure.

**Reading `sql/25`'s inventory.** Expect, for `crawl_writer`, twenty-three
`GRANT` rows — seventeen procedures and six table types — and six `DENY` rows
against the schema. For `crawl_reader`, thirteen `GRANT` rows — seven procedures
and six views — and five `DENY` rows. The type grants render correctly: the query
joins `sys.types` as well as `sys.objects` and reads the class from
`p.class_desc`, so a table-type grant shows as `TABLE_TYPE` rather than as a
schema grant with a nonsense name.

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
        @ConnectionId             = N'consultingwork',
        @KeepRunDays              = 90,
        @KeepTombstoneDays        = 180,
        @KeepExpiredPrincipalDays = 30;
```

It returns one row: `RunsPurged`, `TombstonesPurged`, `PrincipalsPurged`.

**What it purges**

| | Kept for | Notes |
|---|---|---|
| `crawl.Run` rows, and their `ThrottleEvent`, `RunPhaseTiming` and `RunItemType` children | `@KeepRunDays`, default 90 | Closed runs only, and only those nothing still points at — see below. Every child is deleted before the parent; `FK_RunItemType_Run` has no cascade, so missing one would throw 547 and roll the whole purge back |
| Tombstoned items — `crawl.Item` rows in state 3 | `@KeepTombstoneDays`, default 180 | On their own, longer clock. An item deleted and re-created inside the window is recognised as a resurrection; outside it, it is treated as new. Both are correct, only the first is free |
| Expired `crawl.PrincipalMap` entries | `@KeepExpiredPrincipalDays` past `ExpiresUtc`, default 30 | Kept past expiry rather than at it, so a cache entry that expired yesterday is still there to be looked at when somebody asks why a group stopped resolving |

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

**Run it by hand once against one connection before scheduling it**, and read
the three counts it returns. A purge that returns zeroes on a database with
months of history is telling you something — most often that every run is still
referenced by a live inventory row, which is what happens when the corpus has not
changed since the retention window opened.

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
        -- One literal, not a concatenation. T-SQL takes a constant or a variable
        -- as a procedure parameter and not an expression, so the `+` this line
        -- used to carry failed with "Incorrect syntax near '+'" - which is how
        -- we know this snippet had never been run.
        @description = N'Weekly retention for the crawl state store. Runs crawl.uspPurgeHistory once per registered connection. See docs/CRAWL-STATE-DEPLOYMENT.md section 6.',
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
            @ConnectionId             = @ConnectionId,
            @KeepRunDays              = 90,
            @KeepTombstoneDays        = 180,
            @KeepExpiredPrincipalDays = 30;

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
  failed job. The realistic cause is a connection whose history has grown past
  the transaction the instance will comfortably hold, which is something somebody
  should look at rather than something a retry should absorb — and because the
  whole procedure is one transaction, a failure means nothing was purged, so the
  backlog is still there next week and still growing.
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

Alert on `AgeMinutes`, and set the threshold at one crawl interval:

```sql
SELECT   p.ConnectionId, p.ItemType, COUNT(*) AS Stuck,
         MAX(p.AgeMinutes) AS OldestMinutesPending
FROM     crawl.vwPendingDeletes AS p
WHERE    p.AgeMinutes > 60          -- one crawl interval for this connection
GROUP BY p.ConnectionId, p.ItemType
ORDER BY Stuck DESC;
```

`AgeMinutes` measures time spent **pending**, not time since the item was last
written. `crawl.Item.PendingSinceUtc` is stamped by `uspGetPendingDeletes` when
it moves an item to state 2, cleared by `uspConfirmDeletes` when Graph confirms
the removal, and cleared again by `uspRecordWritten` if the item comes back;
`CK_Item_Pending` keeps the column and the state travelling together, so the age
cannot quietly become a lie. That distinction is what makes the alert usable:
aged on `LastWrittenUtc` instead — which is when the item was last *written* —
every freshly pending row on a corpus that mostly does not change would read as
weeks old, and the rule would fire on every sweep until somebody switched it off.

`crawl.uspListPendingDeletes` takes `@MinAgeMinutes` against the same column, so
the dashboard's stuck-delete page and the monitoring rule agree by construction.

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

## 10. `sql/26`: making the timesheet source readable incrementally

**This one changes the source database, not the state database.** It is in the
same numbered set and it is not part of the same deployment: `sql/20` through
`sql/25` create `ConnectorState`, and `sql/26` alters `Ops`. Run it before the
state database, after it, or never — nothing in the six depends on it, and it
depends on nothing in them.

| | |
|---|---|
| **What it does** | Adds `EffectiveLastModified` to `dbo.Customers`, `dbo.Engagements` and `dbo.TimeEntries` — "when did anything that affects this row's indexed content last change" |
| **Where it runs** | `Ops`, after `sql/10` and `sql/12` |
| **Run as** | `db_owner` on `Ops`. It alters tables, creates indexes and creates triggers |
| **Version floor** | **SQL Server 2022**, for `GREATEST()` in the backfill. `sql/20` through `sql/25` need nothing above 2016, so if the source instance is older than the state instance this is the file that fails |
| **What it creates** | Three columns, three composite indexes, three `AFTER INSERT, UPDATE` triggers, a backfill, and `dbo.vwExternalItemsIncremental` |

### The problem it solves

`SqlHierarchyPush` flattens a hierarchy: a time entry carries its engagement's
name and its customer's name so that searching for the customer finds the time
entry. That denormalisation is the whole reason the connector exists, and it
means a time entry's correct indexed text depends on three rows.

Rename a customer and every one of that customer's time entries now holds a name
that no longer exists — but only `dbo.Customers.LastModified` moved. An
incremental crawl reading "rows changed since the checkpoint" re-indexes one
customer and leaves a thousand descendants stale, indefinitely, with nothing
reporting it. The connector cannot detect this: from its side the source simply
did not return those rows. This is the hierarchy warning in
[`SOURCE-CONTRACT.md`](SOURCE-CONTRACT.md#tier-1--a-last-modified-timestamp-strongly-preferred),
and it is the pilot's blocker.

### When to run it, and when not to

**Without it the connector still works.** It declares differencing, reads
everything every run, and lets the content hashes in `ConnectorState` decide what
is actually *written* — Tier 2 in `SOURCE-CONTRACT.md`. Reading a hundred
thousand rows out of SQL Server is seconds; writing a hundred thousand items to
Graph is hours, and Tier 2 already saves the hours.

So the decision rule is about the source read alone:

| | |
|---|---|
| The full source read is comfortably inside the crawl window | Stay on Tier 2. Running `sql/26` buys you nothing you can measure |
| The source read is approaching the crawl window | Run it. This is what moves the connector to Tier 1, where most runs read almost nothing |
| The estate will not take triggers on a production table | Read the two alternatives in section 6 of the script before deciding — one of them is not a real option at scale |

### The three ways to maintain the column

The script implements the first. Section 6 of the script documents the other two,
and the trade-off is worth understanding before an estate's change-control board
picks one for you.

| | How it works | What it costs |
|---|---|---|
| **Triggers** (what the script does) | Three `AFTER INSERT, UPDATE` triggers. A customer changing stamps its engagements and their time entries in one set-based statement each | Write amplification on ancestor edits: renaming a customer with a thousand time entries updates a thousand rows. That is correct — a thousand index items genuinely went stale at that moment — and the alternative is not a cheaper update, it is a thousand wrong search results |
| **A computed view** | `GREATEST(te.LastModified, e.LastModified, c.LastModified)` evaluated on read | Correct and **not seekable**. Filtering on a maximum computed across a join cannot use an index, so every incremental read scans the whole hierarchy. At the pilot's 1,118 rows that is free; at a hundred thousand it is slower than the full crawl it was meant to replace, and Tier 2 is the honest position instead |
| **Application-maintained** | Every write path sets the column itself | The same column and the same index with no triggers, and a defensible place to put the guarantee — provided *every* write path is covered. Bulk loads and DBA edits are the ones that get missed, and they get missed silently |

The triggers deliberately do **not** touch `LastModified`. That column means
"when did this row change" and other things in the estate may depend on it; the
script adds a second question rather than redefining the first.

### Verification

The script ends with three queries.

| Query | A healthy result |
|---|---|
| Any child behind its parent | **Zero rows.** A three-way `UNION ALL` covering every parent-child edge — engagement behind customer, time entry behind engagement, time entry behind customer. Each row it returns is the stale-name defect this script exists to prevent, and means the backfill did not complete or a trigger is still disabled |
| The three triggers | Three rows, `is_disabled = 0` on each. A trigger left disabled is the failure the first query detects, and this says which one |
| The view's item count by type | One row per item type, with `oldest` and `newest` bracketing the corpus |

The first query checks the **middle** edge as well as the two leaf ones, and that
is the point: an engagement item carries its customer's name too, so an
engagement that has fallen behind its customer is already serving one wrong name.
A leaf-only check would pass that source.

### Three design decisions worth knowing

They are not optional details, and each one closes a failure that is silent.

**`NOT NULL`, unlike the `LastModified` sibling.** A null in this column would be
invisible twice over: the incremental predicate `EffectiveLastModified > @marker`
is unknown for a null and therefore never true, *and* the triggers' own recursion
guard `WHERE EffectiveLastModified < @Now` is unknown too — so nothing would
repair the row either. Such a row would be skipped by every incremental crawl for
ever and found only by a full one. `NOT NULL` with a default removes the class,
and populates the existing rows on the `ALTER` rather than leaving them to the
backfill.

**`DATETIME2(3)`, matching `crawl.Checkpoint.MarkerTime` exactly.** The sibling
`LastModified` is precision 7, and converting 7 to 3 rounds to *nearest* rather
than truncating — so a marker taken from a precision-7 value can land ahead of
the row it came from, and everything in between is skipped permanently. The store
already floors a marker to whole milliseconds before saving, so this was defended
rather than live; matching the precision at the source makes the comparison exact
instead of merely defended. That matters here specifically, because the triggers
stamp an entire cascade with one value, and same-timestamp groups are exactly
what this source produces.

**The backfill runs with the triggers disabled, and its statements are
idempotent.** Both guards are needed and they do different jobs. Section 3
creates the triggers, so an unguarded `UPDATE` in section 4 would fire them, they
would overwrite the historical value with `SYSUTCDATETIME()` and cascade it
down, and the whole corpus would end up stamped with the moment the script ran —
making the ancestors-before-descendants ordering pointless. The `DISABLE TRIGGER`
around the section stops that. The `WHERE` on each statement — only touch rows
whose value differs from what it should be — stops a re-run rewriting rows that
were already correct. **The file is therefore safe to re-run**: the column and
index guards skip what exists, and the backfill corrects only rows that are
genuinely wrong.

### The one accepted cost

Each branch of `dbo.vwExternalItemsIncremental` joins a `sql/12` view back to its
base table on a **constructed** key — `N'cust-' + CAST(CustomerId AS NVARCHAR(32))
= v.ItemId` — because those views project the composed item ID and not the
numeric key it was built from. That comparison is not sargable, so the join costs
a scan per branch.

It is documented in the script rather than fixed, and the reasoning is worth
repeating: the incremental predicate still seeks `IX_*_Effective`, which is where
the saving actually is, so this is a constant per run rather than a cost that
grows with the size of the delta. The clean fix is upstream — have the `sql/12`
views project their numeric key alongside `ItemId` — and those views are read by
the agent-hosted path too, which puts the change outside this file's blast
radius. It is a deliberate deferral, not a defect in `sql/26`.

---

## 11. Where `Settings:StateConnectionString` is allowed to live

Everything in this document is inert until the connector is told where the state
database is, and that one setting has nowhere obvious to go. The build refuses
it in a tracked `appsettings.json` — `SecretHygiene.targets` fails on any key
matching `connectionstring`, and rightly, since that is the key shape a password
arrives in. `PushOptions.Load` reads exactly one JSON file and layers nothing, so
there is no environment variable or user-secrets provider to fall back on.

Three routes. They are not equivalent, and the differences are about what each
one costs the build gate rather than about convenience.

**Put the real file in the published output, and leave the tracked one alone.**
This is the one to take. Configuration is read from `AppContext.BaseDirectory` —
beside the executable, not from the source tree — so a published folder outside
the repository is where the deployed `appsettings.json` already belongs. No
build-time scan reaches it, the shipped placeholder stays clean, and the setting
survives a rebuild because nothing rebuilds into that folder.

```powershell
dotnet publish src\SqlGraphPush -c Release -o C:\Connectors\SqlGraphPush
# then edit C:\Connectors\SqlGraphPush\appsettings.json
```

**Build the rig with the scan switched off for that build.** Documented at the
head of `SecretHygiene.targets` and deliberately noisy in the log:

```powershell
dotnet build SqlTicketsConnector.sln -p:SkipAppSettingsSecretScan=true
```

Per-build, leaves the shipped control alone, and is the right answer when you
genuinely want the value in the source tree during development. It is not a
deployment.

**Add the key to the allowlist.** `AppSettingsSecretScanAllowedPaths` in
`build/SecretHygiene.targets`. This permanently widens a shipped security
control, so take it only if the setting must be a normal committed key forever —
and note the precedent that file sets for itself: `Auth:ClientSecretCredentialTarget`
is allowlisted *and* paired with a startup check that rejects a value of the
wrong shape, because an allowlist entry without one is a hole. The equivalent
check here already exists — `CrawlStateWiring` refuses a connection string
containing a password — but it matches by substring rather than by parsing, and
tightening that is an open item in the readiness document. Widening the gate
before closing that is the wrong order.

Whichever route, the connection string is Integrated Security. A password in it
is refused at startup, so there is no secret in this value to protect — which is
what makes the first route sufficient rather than a compromise.

---

## 12. Choosing `Settings:MaxDeletePercent`

The default is 10, and it is a placeholder rather than a recommendation. The
guard exists to catch a *correct* run that read too little — a dropped view, a
revoked permission, a filter that stopped matching, a source restored to last
month — and the threshold that does that is a property of the source, not of the
connector. Set too high it never fires; set below the source's normal daily
churn it fires every day until somebody disables it, which is worse than not
having it.

Measure before choosing. Against the source, over a period long enough to
include a month-end:

```sql
-- Deletions per day as a percentage of the live corpus, highest first.
-- Run against the SOURCE database. Substitute the real table and its
-- soft-delete column; on a source that hard-deletes, this cannot be measured
-- retrospectively at all and the number has to come from whoever owns it.
SELECT   TOP (30)
         CAST(DeletedUtc AS DATE)                                   AS Day,
         COUNT(*)                                                   AS Deleted,
         CAST(100.0 * COUNT(*)
              / NULLIF((SELECT COUNT(*) FROM dbo.Tickets WHERE IsDeleted = 0), 0)
              AS DECIMAL(5, 2))                                     AS PercentOfLive
FROM     dbo.Tickets
WHERE    IsDeleted = 1
  AND    DeletedUtc >= DATEADD(DAY, -90, SYSUTCDATETIME())
GROUP BY CAST(DeletedUtc AS DATE)
ORDER BY PercentOfLive DESC;
```

Take the highest legitimate day, and leave headroom above it — a threshold set
exactly at the observed maximum fires the first time the business has a slightly
bigger day than it has ever had. If the answer is under 1%, the default of 10 is
already generous and there is nothing to do but record that it was checked.

Two things worth knowing before setting it low. The guard compares against the
live corpus at sweep time, so a small source has coarse granularity: at 1,118
items every single deletion is 0.09%, and a threshold under 1 is a threshold that
cannot distinguish nine deletions from ninety. And the sweep runs only on a full
crawl, so on a weekly full-crawl cadence the percentage is a week's deletions,
not a day's — the number to compare is `FullEveryHours` worth of churn.

`Settings:OverrideDeleteGuard` is the deliberate bypass for the day the guard is
right to fire and you have verified the source anyway. It is a per-run decision
and belongs in a runbook step, never in a configuration file.

---

## Where to look next

| | |
|---|---|
| [`SOURCE-CONTRACT.md`](SOURCE-CONTRACT.md) | What the source system has to guarantee for any of this to work, and what a source that meets only some of it gets |
| [`CRAWL-STATE-REFERENCE.md`](CRAWL-STATE-REFERENCE.md) | Every table, view and procedure, with columns, parameters and error numbers |
| [`SECURITY.md`](SECURITY.md) | The `STATE-*` control rows, each with the query that proves it |
| [`TROUBLESHOOTING-DIRECT-PUSH.md`](TROUBLESHOOTING-DIRECT-PUSH.md) | When the push tool itself is the thing misbehaving |
