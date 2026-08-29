---
title: What a source must guarantee
description: The constraints a source system has to meet before a direct push can detect deletions, skip unchanged items, and resume — and what each connector gets if it can only meet some of them.
---

# What a source must guarantee

A direct push has no Graph connector agent behind it, so everything the agent
remembered on your behalf is now remembered in `ConnectorState` (`sql/20`
through `sql/25`). That store can do its job **only if the source system holds
up its end**, and the end it has to hold up is smaller than people expect — but
it is not empty, and every item on it has a failure mode that is silent.

This document is what to send the team that owns the source before a pilot. It
is deliberately phrased as *what must be true*, not *what to build*: most
sources already satisfy the hard requirements without knowing it.

---

## The short version

| | Requirement | Without it |
|---|---|---|
| **1** | Every record has an identifier that is **unique and immutable** | Deletions and updates are both wrong. This is the only non-negotiable one |
| **2** | Identifiers are **never reused** for a different record | One record inherits another's search result |
| **3** | A full read returns **every live record** | The delete sweep removes whatever the read missed |
| **4** | The mapping from record to item ID is **deterministic** | Every run re-writes the entire corpus |
| 5 | Records carry a reliable **last-modified timestamp** | Incremental crawls are impossible; see [Tier 2](#tier-2--no-timestamp-differencing-only) |
| 6 | That timestamp is **monotonic and set on every change** | Incremental crawls silently miss edits |

**1 to 4 are hard requirements.** A source that cannot meet them should not use
the direct push path at all.

**5 and 6 are what buy you incremental crawls.** A source that cannot meet them
still works — it just reads everything, every time, and relies on hash
differencing to avoid *writing* everything. That trade is explained below and is
usually acceptable well past the point people assume.

**Notably absent: a soft-delete flag.** The push path does not need one. See
[How deletion actually works](#how-deletion-actually-works).

---

## The hard requirements

### 1. A unique, immutable identifier

Every record must have a key that identifies it for its entire life and **never
changes**, no matter what else about the record changes.

A database primary key is normally this. A composite of business columns
normally is not.

> **Why it is absolute.** The item ID is the only thing tying a row in the
> source to an item in the Copilot index. Change the key and the connector
> cannot tell the difference between "this record was edited" and "one record
> was deleted and a different one created". It will do the second: the old ID
> disappears from the read, so the delete sweep removes it from the index, and
> the new ID is written as a new item. Every citation, deep link and pinned
> result pointing at the old item breaks, and nothing reports an error.

**Good keys:** an identity column, a GUID stored on the row, a natural key the
business genuinely never re-issues (an invoice number).

**Bad keys, all of which have been proposed at some point:**

- Email address, username, or anything else a person can change
- A composite of `(CustomerName, ProjectName)` — a rename rewrites the corpus
- Row position, `ROW_NUMBER()`, or anything derived from ordering
- A hash of the record's *content* — which changes precisely when the record is
  edited, i.e. exactly when you need it to stay the same

### 2. Identifiers are never reused

Once a record is deleted, its key must not be assigned to a different record.

> **Why.** The store tombstones deleted items rather than forgetting them, so a
> returning key is treated as a **resurrection** of the same record —
> `uspRecordWritten` moves the row back to live. If the key belongs to a
> different record now, the index quietly keeps the old item's history and
> serves the new item under it. Identity columns and GUIDs are safe. Sequences
> that get reset are not.

### 3. A full read returns every live record

When the connector runs a **full** crawl, the query must return every record
that should be in the index. Not most. Every one.

> **Why.** This is the read the delete sweep diffs against. Anything absent from
> a completed full crawl is concluded to have been deleted from the source and
> is removed from the index.
>
> The realistic failure is not a missing row — it is a *missing set*: a view
> redefined, a `WHERE` clause that stopped matching after a data change, a
> permission revoked so the connector now sees a subset, a database restored to
> an earlier point. Each of those produces a run that completes cleanly and
> reads too little.

**This is guarded, not merely documented.** `crawl.uspGetPendingDeletes` refuses
any sweep that would remove more than `@MaxDeletePercent` of the live corpus
(default 10%) and says so with the numbers in the error. Clearing that guard
requires passing an override deliberately. It is the single most important
safety property in the whole state store, and it exists because requirement 3 is
the one most likely to be violated by accident.

### 4. Deterministic item IDs

The same source record must map to the same item ID on every run, on every host,
in every process.

> **Why.** Change detection compares a stored hash against a computed one, keyed
> by item ID. A non-deterministic ID means the store never finds the previous
> row, so every item looks new, every item is rewritten, and the delete sweep
> then removes the entire previous corpus because none of the old IDs were seen.
> A run like that is indistinguishable from correct operation except by its
> duration and its Graph quota consumption.

**Rules for composing an ID** (Graph requires alphanumeric, 128 characters max):

- Derive it only from the immutable key and a constant type prefix —
  `cust-10482`, `eng-99213`, `te-4410027`
- Never include a timestamp, a run number, a random value, or `Guid.NewGuid()`
- Normalise case and trim whitespace **once**, in the connector, and never
  change that normalisation afterwards — changing it renames every item, which
  is a delete-and-recreate of the corpus
- Prefix by type so two families cannot collide, and so the dashboard's prefix
  search (`crawl.uspListItems`) is useful

---

## Incremental crawls: the two tiers

### Tier 1 — a last-modified timestamp (strongly preferred)

If records carry a reliable last-modified timestamp, the connector reads **only
what changed** since its checkpoint. This is the difference between a nightly
job that touches a thousand rows and one that touches a hundred thousand.

The timestamp must be:

- **Set on every change** that should reach the index, including changes made by
  bulk updates, triggers, ETL, and direct DBA edits. A column maintained by the
  application layer only is a column that misses exactly the changes nobody
  tells you about.
- **Monotonic** — never moves backwards. A restored row that keeps its old
  timestamp is a row the incremental crawl will never see again.
- **UTC**, or at minimum unambiguous across a daylight-saving transition. An
  hour that occurs twice is an hour of edits that get read twice or not at all.
- **Indexed** together with the key: `(LastModified, RecordId)`. Without the
  index the incremental read is a scan of the whole table, which costs more than
  the full crawl it replaced.

The checkpoint is **composite** — `(LastModified, ItemId)` — and this is not
optional. Two records can share a timestamp to the millisecond; a marker holding
only the timestamp either re-reads that whole group forever or loses whichever
of them had not been written when the run stopped. The pair makes the ordering
total and "strictly after the marker" exact. `crawl.uspSaveCheckpoint` compares
on the pair and refuses to move backwards.

**Hierarchy warning, and it is the live blocker on the SQL pilot.** When a
connector flattens a hierarchy — a time entry carrying its engagement's and
customer's names for searchability — the timestamp must be **hierarchy-aware**.
Renaming a customer changes the correct indexed text of every descendant row. If
`LastModified` only moves on the row that was edited, an incremental crawl
updates the customer and leaves a thousand descendants carrying a name that no
longer exists. A view that exposes
`GREATEST(te.LastModified, e.LastModified, c.LastModified)` solves it; nothing
in the connector can.

For the three-level timesheet source this is implemented:
`sql/26-timesheet-incremental.sql` adds a
persisted `EffectiveLastModified` to all three tables, keeps it true with
cascading triggers, and exposes `dbo.vwExternalItemsIncremental` for the
connector to read. It is deliberately **not** part of the state-database
deployment — it alters the source, needs SQL Server 2022, and is optional until
the full source read outgrows the crawl window. The trade-off between triggers, a
computed view and an application-maintained column is set out in
[`CRAWL-STATE-DEPLOYMENT.md` section 10](CRAWL-STATE-DEPLOYMENT.md#10-sql26-making-the-timesheet-source-readable-incrementally).

### Tier 2 — no timestamp: differencing only

If the source has no usable timestamp, **the connector still works**. It reads
everything every run and uses the content and ACL hashes in `crawl.Item` to
decide what to *write*.

What that costs and what it saves:

| | Tier 1 (timestamp) | Tier 2 (differencing) |
|---|---|---|
| Rows read from the source each run | Only changed | **All** |
| Items written to Graph each run | Only changed | Only changed |
| Graph write quota consumed | Proportional to churn | Proportional to churn |
| Source read time | Proportional to churn | Proportional to corpus |
| Delete detection | Full crawls only | Every run |
| Resumable after a crash | Yes | Only from the start |

**The saving is on the expensive side.** Reading a hundred thousand rows out of
SQL Server is seconds; writing a hundred thousand items to Graph is hours. Tier 2
keeps the hours and pays the seconds. That is why a source with no timestamp is
a reason to plan capacity, not a reason to reject the push path.

The point at which Tier 2 stops being viable is when the *source read* alone
exceeds the crawl window — a heavily denormalised view over tens of millions of
rows, or a source behind a slow API with per-request cost.

**Every Tier 2 run is a full crawl by definition**, which has a pleasant side
effect: delete detection runs every time rather than weekly, so a deletion
reaches Copilot on the next run instead of at the next scheduled full crawl.

---

## How deletion actually works

**The source is not asked whether a record was deleted.** It is never asked, and
it does not need a soft-delete column, a tombstone table, a deleted-records
feed, or a trigger.

The mechanism is a diff against the store:

1. `crawl.Item` holds one row per item the connector has written, with the ID of
   the run that last **saw** it.
2. Every item a full crawl sees — whether written or skipped as unchanged — has
   its `LastSeenRunId` set to the current run.
3. When the full crawl finishes cleanly, any live row still carrying an older
   `LastSeenRunId` was not returned by the source. It is moved to *pending
   delete* and a `DELETE` is issued to Graph.
4. `crawl.uspConfirmDeletes` tombstones it only once Graph confirms. A refused
   delete stays pending and is retried on the next run rather than being
   forgotten.

Three consequences worth stating plainly:

- **A hard `DELETE` in the source is detected.** So is a row that falls out of
  the query's `WHERE` clause, a row whose permissions changed so the connector
  can no longer see it, and a record archived to another table. All four are
  "the source stopped returning it", which is the only question being asked.
- **Skipping the write is not skipping the mark.** An item found unchanged is
  still marked seen (`crawl.uspRecordUnchanged`). These are one line apart in
  the engine and produce opposite outcomes — the first is the optimisation, the
  second would empty the corpus one run at a time.
- **`sql/02-soft-delete.sql` is for the agent-hosted path only.** That connector
  emits `DeletedItem` on an incremental crawl and genuinely needs `IsDeleted`.
  The push path does not, and a source that has the column gains nothing on this
  path beyond excluding those rows from the read — after which the sweep removes
  them anyway.

---

## ACLs

Two additional constraints apply when items carry their own permissions:

- **Group identifiers must be stable.** The ACL hash is computed over the
  resolved grants; a group whose identifier churns makes every item look changed
  on every run, which silently converts a Tier 1 source back into a Tier 2 one.
- **Source principals must be resolvable to Entra.** `crawl.PrincipalMap` caches
  the mapping with a TTL, including negative results, so an unresolvable group
  costs one lookup rather than one per item. It cannot invent a mapping that
  does not exist — an item whose grants all fail to resolve is skipped rather
  than written to nobody, and the run reports it.

---

## Checklist to send the source team

Copy this into the pilot parameters document
([`SQL-PILOT-PARAMETERS.md`](SQL-PILOT-PARAMETERS.md)) and ask for it in
writing.

- [ ] Name the column that is the **immutable unique key**. Confirm it never
      changes and is never reused.
- [ ] Confirm the full-read query returns **every live record**, and name who
      would know if that stopped being true.
- [ ] State the **expected record count**, so the first run can be reconciled
      against it and the delete guard has a sanity baseline.
- [ ] Is there a **last-modified timestamp**? If yes: is it UTC, is it monotonic,
      is it set by *every* write path including bulk and DBA edits, and is
      `(LastModified, Key)` indexed?
- [ ] If the connector flattens a hierarchy: does the timestamp move on the
      **descendants** when an ancestor is renamed?
- [ ] Expected **daily change volume** and **daily deletion volume**, as a
      percentage of the corpus — this sets the delete guard's threshold.
- [ ] If items carry their own ACLs: name the **group identifier** and confirm
      it is stable.

---

## Where this is enforced

| Requirement | Enforced or detected by |
|---|---|
| Item ID charset and length | `ExternalSchemaRules.ValidateItemId` — throws before the write |
| Duplicate IDs within a run | `PushEngine.Prepare` — counted and logged, later row wins |
| Determinism | Not enforceable. Detected as a run with 0% unchanged in `crawl.vwRunHistory` |
| Full read completeness | `crawl.uspGetPendingDeletes` percentage guard |
| Checkpoint monotonicity | `crawl.uspSaveCheckpoint` — refuses to move backwards |
| Timestamp presence | The connector declares its tier; a Tier 1 claim with no checkpoint forces a full crawl in `crawl.uspBeginRun` |
| Hierarchy-aware timestamps | Not enforceable from this side — the source has to maintain them. `sql/26-timesheet-incremental.sql` is the worked implementation for the timesheet source, and its first verification query returns any descendant whose effective timestamp is behind an ancestor's |

The row worth looking at twice is **determinism**, because nothing can enforce
it. The symptom is a connector whose `UnchangedPercent` never rises above zero
in `crawl.vwRunHistory` after the first run. On a healthy Tier 1 or Tier 2
source that figure settles well above 90% within a few runs. If it does not, the
item IDs are not stable and the corpus is being rewritten every night.
