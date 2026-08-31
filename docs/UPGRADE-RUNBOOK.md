---
title: Upgrade and rollback
description: What a version is in this repository, the concrete v1.4 to v1.5 upgrade — which SQL scripts to run and in what order — how to back out, and the additive-only rule that makes rollback possible, together with the one migration that currently breaks it and what that costs.
---

# Upgrade and rollback

The pieces have existed for a while: versioned releases, a deployment zip,
ordered SQL migrations, and a workflow that retires superseded releases. What
has not existed is a page that says *"1.4 → 1.5: run these scripts, swap the
binaries; to back out, binaries back, schema stays"*. This is that page, plus
the rule that keeps the second half of that sentence true — and an honest
account of where it is currently false.

---

## 1. What a version is here

| | |
|---|---|
| **Tags** | `vX.Y.Z` on `main` and `vX.Y.Z-net9` on `release/net9`. Both lines carry the **same version** and are released together; the `-net9` line is the .NET 9 build for Visual Studio 2022 and is otherwise identical |
| **Releases** | `build.yml` builds each tag and creates a **draft** release carrying the deployment zip. A person reviews and publishes it |
| **The zip** | Compiled binaries plus the `deploy/` scripts and the `sql/` directory. `Build.ps1` produces the same artefact locally, so an air-gapped estate never has to reach GitHub |
| **Retirement** | `release-retire.yml` deletes superseded **releases** and never touches **tags**. The retention unit is a *version*, so both lines of a kept version are kept |

### ⚠️ The rollback target must still be downloadable, and retirement is irreversible

`release-retire.yml` is explicit that *a deleted release cannot be restored —
the assets are gone*. It deletes the release page and its binaries; the tag, and
therefore the source, survives.

The consequence for this runbook: **do not retire the release you would roll
back to.** Keep at least the current and the previous version published — the
workflow's `keep` input exists for exactly this and defaults to reporting rather
than deleting. If the previous release has already been retired, rollback is not
blocked, but it changes shape: you must check out the tag and run `Build.ps1` to
reproduce the zip, on a machine with the right .NET SDK, under time pressure.
That is a materially worse position than downloading a file, and it is the
avoidable half of a bad night.

**Keep the zip you deployed.** Archive it beside the configuration on the
deployment host or in the change record. It is the only artefact that is
guaranteed to be byte-identical to what is running.

---

## 2. Upgrading v1.4 → v1.5

The change from v1.4.0 to v1.5.0 is eight new SQL scripts, three scripts edited
in place, and a binary swap. Establish which of them apply to your deployment
first: **five touch `ConnectorState`, three touch the `Ops` source database, and
one writes a SQL Agent job into `msdb`.** They are not interchangeable and the
`Ops` ones are only worth deploying where `sql/26` is.

| Script | Database | What it does | Additive? |
|---|---|---|---|
| `sql/33-crawl-state-negative-ttl.sql` | `ConnectorState` | Two principal TTL columns, plus the clamp in `uspCachePrincipal` | Columns yes; the procedure is a `CREATE OR ALTER` with a new **defaulted** parameter, so old callers still bind |
| `sql/34-crawl-state-live-item-ids.sql` | `ConnectorState` | `uspListLiveItemIds`, read-only, for the dry-run delete preview | **Yes** — new object |
| `sql/40-crawl-state-per-type-duplicates.sql` | `ConnectorState` | `ItemsDuplicate` on `crawl.RunItemType`, **and recreates the `ItemTypeCountList` table type** | ⚠️ **No.** See section 4 |
| `sql/41-crawl-state-compare-and-see.sql` | `ConnectorState` | `uspCompareAndSee`, the one-call compare | **Yes** — new object |
| `sql/42-verify-least-privilege.sql` | `ConnectorState` | Verification only. Exercises the least-privilege model with probe users | **Yes** — read-only apart from its own probe users |
| `sql/24-crawl-state-reporting.sql` | `ConnectorState` | **Edited in place.** `uspGetRun` now returns `ItemsDuplicate` per item type | Yes, but it **depends on `sql/40`** — see the warning below |
| `sql/31-timesheet-trigger-health.sql` | `Ops` | The trigger health check | **Yes** |
| `sql/32-timesheet-trigger-health-job.sql` | `msdb` | The SQL Agent job for it | Drops and recreates **its own** job |
| `sql/35-timesheet-incremental-parity.sql` | `Ops` | Verification only, entirely `SELECT` | **Yes** |
| `sql/12-timesheet-views.sql` | `Ops` | **Edited in place.** Item views | Additive columns |
| `sql/26-timesheet-incremental.sql` | `Ops` | **Edited in place.** The cascading timestamp | ⚠️ **Do not re-run wholesale against a populated source** — see section 10 of [`CRAWL-STATE-DEPLOYMENT.md`](CRAWL-STATE-DEPLOYMENT.md#10-sql26-making-the-timesheet-source-readable-incrementally) |

### ⚠️ Run `sql/40` before `sql/24`

`sql/24` creates `uspGetRun`, which selects `t.ItemsDuplicate` from
`crawl.RunItemType`. That column is added by `sql/40`. Deferred name resolution
covers a missing *table*; it does **not** cover a missing *column* on a table
that already exists — so `CREATE OR ALTER PROCEDURE` fails outright with
`Msg 207, Invalid column name 'ItemsDuplicate'`.

On an upgrade this is usually invisible, because `sql/40` runs before you
re-run `sql/24`. On a **fresh** deployment it is not: the ordered table in
section 2 of [`CRAWL-STATE-DEPLOYMENT.md`](CRAWL-STATE-DEPLOYMENT.md#2-prerequisites-and-the-order-the-scripts-run-in)
places `sql/24` at step 5 and `sql/40` at step 11, which fails at step 5. If you
are standing up a new environment at v1.5, run `sql/40` before `sql/24`, or run
`sql/24` twice.

### The procedure

1. **Take a backup first, and know that you can restore it.** Not the estate's
   nightly — a backup taken now, immediately before the change, so the rollback
   position is unambiguous. `deploy\Backup-ConnectorState.ps1` does this in
   under a second against a corpus of this size, and
   [`DISASTER-RECOVERY.md`](DISASTER-RECOVERY.md) covers the rehearsal that
   makes the file trustworthy. Record the manifest's `toolVersions` list: it is
   the record of which binaries wrote the state you are about to migrate.

2. **Stop the schedule.** Disable the scheduled task, or stop the service. Do
   not rely on the gap between runs — a migration that lands mid-crawl races a
   writer set.

3. **Confirm nothing is running.** A run left in status `running` is reaped as
   `abandoned` by the next `uspBeginRun`, but you want to know now rather than
   discover it later:

   ```sql
   SELECT RunId, Status, StartedUtc, HostName FROM crawl.Run WHERE Status = 1;
   ```

4. **Run the SQL scripts**, in the order in the table above — `sql/33`, `34`,
   `40`, `41`, then re-run `sql/24`, then the `Ops` scripts if that source is
   deployed. Every one of them is guarded and safe to re-run, with the single
   exception of `sql/26`.

5. **Run the verifications.** `sql/42` against `ConnectorState`, then
   `sql/30-verify-set-options.sql` against `ConnectorState` **and** `Ops`.
   `sql/30` is not optional after a migration: `QUOTED_IDENTIFIER` is stored per
   module as it stood in the session that created it, and `sqlcmd` connects with
   it **off** while SSMS connects with it **on**. The same script therefore
   produces a working module from a query window and a broken one from the
   command line, with identical output in both cases. The failure surfaces days
   later as error 1934 on a crawl nobody changed.

   ⚠️ **`sql/40` destroys two grants and `sql/42` is what notices.** Recreating a
   table type drops every permission on it. Re-run `sql/25` if `sql/42`
   reports anything refused for `crawl_writer`.

6. **Swap the binaries.** Unzip the new release over the deployment directory
   and run `deploy\Install-Connector.ps1`.

7. **Dry-run once**, then run once for real, then re-enable the schedule.

8. **Confirm the version.** `crawl.Run.ToolVersion` on the new row is the
   authority on what actually ran — not the folder you unzipped into:

   ```sql
   SELECT TOP 5 RunId, ToolVersion, Status, StartedUtc FROM crawl.Run ORDER BY RunId DESC;
   ```

---

## 3. Backing out

**The short version: binaries back, schema stays.**

Do **not** attempt to reverse the migrations. There is no down-script in this
repository and writing one under pressure is how a rollback becomes an incident.
The schema is designed so that reversing it is unnecessary — mostly, and section
4 is about the exception.

| | |
|---|---|
| Binaries | Restore the previous release's zip over the deployment directory and re-run `Install-Connector.ps1` |
| Schema | **Leave it.** The v1.5 schema is intended to be readable by the v1.4 binary |
| Configuration | Revert any keys the new version introduced |
| State data | **Leave it.** Nothing in the state store needs rewinding: every value the newer binary wrote is either a column the old one ignores or a value it tolerates |
| The backup from step 1 | Only if something is genuinely wrong with the *data*. Restoring it discards every crawl since the upgrade along with its run history, and per [`DISASTER-RECOVERY.md`](DISASTER-RECOVERY.md) a restore of any age should be followed by a full crawl before delete detection is trusted again |

⚠️ **Roll the binaries back, not the database.** Restoring the database to undo a
code change is almost always the wrong instrument: it throws away evidence to
solve a problem in logic, and it re-arms the delete guard for no reason.

### Rolling back across a hash version costs a full rewrite, in both directions

`crawl.Connection.HashVersion` records the framing the stored hashes were
computed with. The escalation is **symmetric**: going back from 2 to 1 is treated
exactly like going forward from 1 to 2, and it should be — a rolled-back binary
really does hash differently from the one that wrote those rows.

Observed on the Live Test 2 rig. A build at version 2 against a connection
recorded at 1:

```
[WRN] The hash framing changed from version 1 to 2. ... escalated to full and
      will rewrite the corpus. This is a migration, not a fault ...
```

and then the shipping build at version 1 against the same connection, now
recorded at 2:

```
[WRN] The hash framing changed from version 2 to 1. ...
```

Both escalate, both announce it exactly once, and the next run is silent. So a
rollback that crosses a `HashVersion` boundary costs **one full write cycle on
every connection**, the same as the upgrade did. Budget for it in the rollback
window rather than discovering it there — on the 111,800-item corpus that is
about 75 minutes per connection.

**A caveat that matters if you ever test this.** Bumping the constant alone
proves the *signalling* and nothing else. `HashVersion` is never an input to
`ItemHasher.HashContent`; it is a declaration that a developer changed the
framing. Move only the number and the run escalates, announces a rewrite, and
then finds every hash still matching — `unchanged`, zero writes. The warning is
exact for a real framing change and overstates a synthetic one.

---

## 4. The additive-only property, and where it currently fails

The claim worth writing down while it is still nearly true:

> **Every migration in this repository so far only adds.**

That is what makes "binaries back, schema stays" safe. A new column with a
default is invisible to a binary that does not select it. A new procedure is
invisible to a binary that does not call it. A new *defaulted* parameter on an
existing procedure is invisible to a caller that does not pass it.

**It was checked rather than assumed, and it does not hold. `sql/40` breaks it.**

### What `sql/40` actually does

Two things. The first is genuinely additive:

```sql
ALTER TABLE [crawl].[RunItemType]
    ADD ItemsDuplicate INT NOT NULL
        CONSTRAINT DF_RunItemType_Duplicate DEFAULT (0);
```

The second is not:

```sql
    DROP TYPE [crawl].[ItemTypeCountList];
...
    CREATE TYPE [crawl].[ItemTypeCountList] AS TABLE
    (
        ItemType       NVARCHAR(64) NOT NULL,
        ...
        ItemsDuplicate INT          NOT NULL,
        BytesWritten   BIGINT       NOT NULL,
```

The type gains an eighth column. Its own comment explains that the column was
appended **last** so that a caller binding by position keeps its existing
ordinals — which is correct, and insufficient. Appending last protects the
ordinals. It does nothing about the **count**.

### Why that breaks a rolled-back binary

`crawl.ItemTypeCountList` is a table-valued parameter, and the connector fills it
with `SqlDataRecord`, which binds **by position and requires an exact column
count**. The v1.5 binary declares eight columns. The v1.4.0 binary declares
seven — `ItemType`, `ItemsWritten`, `ItemsUnchanged`, `ItemsDeleted`,
`ItemsSkipped`, `ItemsFailed`, `BytesWritten` — with no `ItemsDuplicate`.

Roll the binaries back onto the v1.5 schema and every call to
`uspRecordRunItemTypes` is rejected:

```
Trying to pass a table-valued parameter with 7 column(s) where the
corresponding user-defined table type requires 8 column(s).
```

### ⚠️ And it fails silently, which is the part that matters

If that threw and stopped the run, it would be a bad afternoon and an obvious
diagnosis. It does not. The call site catches it:

```csharp
catch (Exception ex)
{
    this.log.Warning(RedactedException.Wrap(ex),
        "crawl.uspRecordRunItemTypes failed for run {RunId}. The run still closes; its detail page will "
        + "show no per-type breakdown.", run);
}
```

So a v1.4 binary rolled back onto a v1.5 schema **completes every run, reports
success, and writes no `crawl.RunItemType` rows at all** — the entire per-item-type
breakdown, gone, with nothing but a `Warning` in a log to say so. Every
dashboard and every report built on that grain quietly goes blank while the
connector reports itself healthy.

This is the same shape of failure as section 1 of
[`DISASTER-RECOVERY.md`](DISASTER-RECOVERY.md): **the system does not break, it
goes quiet.** That is the failure mode this codebase keeps having to defend
against, and it is why the rule below is worth enforcing rather than merely
stating.

### How this was tested

By reading both binaries rather than by running them — stated plainly because
the difference matters:

- `git show v1.4.0:src/PushCore.State/SqlCrawlStateStore.cs` declares **seven**
  `SqlMetaData` columns for this TVP.
- The same file at `HEAD` declares **eight**, and sets the new ordinal
  explicitly.
- `sql/40` creates an eight-column type, and `sql/21` — the base schema v1.4 was
  written against — has no `ItemsDuplicate` on `RunItemType` at all.

The arity mismatch is therefore certain from the source. What was **not** done is
building both binaries and running a v1.4 crawl against a v1.5 database; the
exact wording of the runtime error above is the documented behaviour of
`SqlDataRecord` TVP binding rather than an error captured on this rig. The
mismatch is not in doubt. The message text is quoted from the platform's
contract, not from a transcript.

### What else was checked, and passed

| Migration | Verdict |
|---|---|
| `sql/28` (hash version) | **Safe.** `NOT NULL` **with** `DEFAULT (1)`; adds a new procedure rather than altering one. Shipped in v1.4 anyway |
| `sql/29` (`partial` status) | **Safe for rollback, not strictly additive.** It drops and recreates `CK_Run_Status` to admit status 5, and rewrites existing rows. But no C# parses the tinyint — the views return words, and the dashboard's `StateCodes` returns null for anything it does not recognise rather than guessing. An older dashboard renders `partial` with a neutral pill. Degraded, not broken. Shipped in v1.4 |
| `sql/33` (principal TTL) | **Safe.** The new parameter is defaulted, and `CREATE OR ALTER` preserves the grant. Behaviour changes — a rolled-back binary asking for a 720-minute negative TTL silently gets 60 — but in the conservative direction |
| `sql/34`, `sql/41` | **Safe.** New objects only |
| `sql/24` (edited in place) | **Safe backwards** — a v1.4 dashboard reads its columns by name and ignores the extra one. **Not safe forwards**: the v1.5 dashboard requires it, so "run `sql/33`–`42`" alone is not a sufficient upgrade instruction. Hence its row in section 2 |

---

## 5. The rule that keeps rollback possible

> **A migration must remain backward-compatible with the previous release's
> binary for one full version. If it cannot be, it is not a migration — it is a
> breaking change, and it needs a deprecation cycle rather than a runbook.**

In practice, for this schema:

| Change | Allowed? | Why |
|---|---|---|
| Add a table, view, procedure, index | **Yes** | Invisible to a binary that does not use it |
| Add a column with a `DEFAULT`, or nullable | **Yes** | Old `INSERT`s omitting it still succeed |
| Add a parameter to a procedure **with a default** | **Yes** | Old callers still bind |
| Widen a `CHECK` constraint | **Yes, with care** | Only if the old binary tolerates the new values — prove it, as `sql/29` can be |
| Add a column to an existing **table type** | ⚠️ **No** | TVPs bind by position **and count**. This is what `sql/40` did |
| Add a **required** parameter to an existing procedure | **No** | Old callers fail immediately |
| Drop or rename anything; narrow a type; add `NOT NULL` without a default | **No** | Breaks the old binary outright |

### The table-type rule, stated separately because it is the one that was missed

**Never alter a table type in place. Version it.**

Adding a column to `crawl.ItemTypeCountList` looks like adding a column to a
table, and it is not: a table type is part of a **calling convention**. Changing
it is changing a method signature that two artefacts have to agree on, and only
one of them is being deployed.

The compatible shape is to add a new type alongside the old one and overload the
procedure — `ItemTypeCountList` kept as it was, `ItemTypeCountList2` carrying the
new column, and `uspRecordRunItemTypes` accepting either until the old binary is
out of support. Then drop the old type in a later version, once no supported
release still passes it.

### Checklist for a new migration

1. Is it additive by the table above? If not, it needs a versioned type or a
   deprecation cycle, not a runbook entry.
2. Does it touch a **table type**? If so, stop and version it.
3. Does it alter an existing procedure? Every new parameter needs a default, and
   use `CREATE OR ALTER` rather than `DROP`/`CREATE` — dropping destroys the
   grants, which `sql/42` will then report and `sql/25` has to repair.
4. Does anything it adds need a grant? `sql/25` grants `EXECUTE` **by name**, and
   a name it has never heard of gets nothing. This is not hypothetical: `sql/34`
   shipped without a grant and the dry-run delete preview was refused under least
   privilege, which nothing noticed because every crawl on the reference rig
   connects as sysadmin.
5. Run `sql/42`, then `sql/30`, against every database that holds modules.
6. Add it to the table in section 2, and to the ordered table in
   [`CRAWL-STATE-DEPLOYMENT.md`](CRAWL-STATE-DEPLOYMENT.md).

---

## Where to look next

| | |
|---|---|
| [Disaster recovery](DISASTER-RECOVERY.md) | What a backup is worth, per table; the restore rehearsal; and re-provisioning the credential |
| [Crawl state deployment](CRAWL-STATE-DEPLOYMENT.md) | The full ordered script table, the delete guard, and retention |
| [Runbook](RUNBOOK.md) | Scheduling, certificate rotation, and exit codes |
| [Go-live readiness](GO-LIVE-READINESS.md) | Section 7, which orders this work against everything else outstanding |
