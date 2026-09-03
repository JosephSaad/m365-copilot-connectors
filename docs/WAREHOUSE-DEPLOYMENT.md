---
title: Deploying the Oracle, Teradata and MongoDB connectors
description: What each of the three needs before it runs, the refusal each one enforces, and the two questions that decide whether the pilot is worth running at all.
---

# Deploying the Oracle, Teradata and MongoDB connectors

Three direct-push connectors that bypass the Graph connector agent. They share
the chassis with the SQL and CDP tools — schema registration, ACLs, `$batch`,
throttling, change detection, the delete sweep and its guard, checkpointing,
redaction, the run lock and the exit codes are all `PushCore` and identical.
What differs is the read, and one refusal each.

> **None of the three has ever run against a real instance.** Every assertion
> below is from the code and the tests, not from a deployment. Treat first-run
> behaviour as unobserved — the same caveat
> [What-is-Next](What-is-Next.md) records for CDP.

---

## Before anything else — two questions

Both are in [CONNECTOR-ONBOARDING.md](CONNECTOR-ONBOARDING.md), and both can end
the project rather than shape it.

| | Question | Why it decides |
|---|---|---|
| **1** | Do the source's roles map to **Entra groups**, and by what mechanism? | A Graph ACL carries Entra **group object IDs**. Oracle roles and Teradata roles are database-local; MongoDB roles are database-local. Without a mapping the ACL cannot be written at all, and a grant to a named individual is *unrepresentable* rather than merely unimplemented |
| **2** | Is the content **text a person would read**? | A warehouse is mostly measures, and an index cannot compute a sum. See [ROUTING-DECISIONS](ROUTING-DECISIONS.md) section 8: most of a Teradata estate belongs in a semantic model, not here |

---

## The document contract

Each connector reads one named object with a fixed shape. The object is
configured; the columns are not.

**Oracle and Teradata** — `Source:ItemView` names a view exposing:

| Column | Purpose |
|---|---|
| `RECORD_ID` | The stable key. A NULL is skipped rather than coalesced, because coalescing would collapse every such row onto one item under the PUT upsert |
| `TITLE`, `STATUS`, `OWNER` | Properties |
| `BODY` | The indexed content |
| `LAST_MODIFIED` | UTC, and the watermark — see below |
| `CLASSIFICATIONS` | Comma-separated, may be empty. Feeds the sensitivity mapping |
| `IS_DELETED` | Only when `DataSource:SoftDeleteEnabled` is true |

**MongoDB** — `Source:ItemView` names a **collection**, whose documents carry
`_id`, `title`, `body`, `status`, `owner`, an optional `classifications` array,
an optional `updatedAt`, and `isDeleted` when soft delete is on.

It may instead name a **GridFS bucket**, which is detected rather than
configured: a bucket is the pair `<name>.files` and `<name>.chunks`. Reading one
as an ordinary collection would index its metadata documents — filename, length,
chunkSize — which looks like a working crawl and is worth nothing. In bucket
mode each file's text is extracted and items are typed `File`.

---

## What each connector refuses, and why it cannot be disabled

Every refusal below is the same argument: the crawl identity's view is not
everyone's view, and an index holds one copy that cannot vary per reader. Each
runs **before the first read**, so a refusal happens while nothing has been
indexed.

### Oracle

Refuses when the view carries any of four per-session features:

| Feature | Catalogue view |
|---|---|
| Virtual Private Database | `ALL_POLICIES` |
| Oracle Label Security | `ALL_SA_TABLE_POLICIES` |
| Data Redaction | `REDACTION_POLICIES` |
| Real Application Security | `DBA_XS_*` |

The catalogue views queried are the `ALL_` ones rather than `DBA_`, deliberately:
a least-privileged crawl identity is not a DBA, and asking for `DBA_` would make
the guard fail on exactly the deployments that configured privilege correctly.
`ALL_` shows what this session can see, which is the right question — a policy
this account cannot see is a policy that does not constrain it.

`ORA-00942` is treated as **absence**, not failure: Label Security and Data
Redaction are separately licensed, and a missing catalogue view genuinely means
the feature is not installed.

### Teradata

Refuses when the table carries a row- or column-level **security constraint**.
Both are expressed the same way — a constraint defined in
`DBC.SecConstraintsV`, and a column of that name on the table — so the check is
the intersection of the two.

**An unreadable `DBC` stops the run**, and this is the opposite of Oracle's
`ORA-00942` handling on purpose. `DBC.SecConstraintsV` exists on every Teradata
system, so failing to read it means the crawl identity lacks a grant — which is
an *unknown* answer, and an unknown answer to "is this enforced per user" has to
fail closed. Grant the crawl identity `SELECT` on `DBC.ColumnsV` and
`DBC.SecConstraintsV`.

### MongoDB

Refuses a **view** as a class. A Mongo view can apply `$redact` against
`$$USER_ROLES` or match on the caller, and the driver cannot distinguish a
redacting view from a plain one — so the category is refused rather than
guessed at. Point the connector at the underlying collection, or materialise
the view into one.

Refuses an **encrypted field** — CSFLE or Queryable Encryption, binary subtype
6. This one is not a leak: ciphertext indexes without complaint and is useless
to every reader, and nothing downstream can tell it from text. Exclude the field
or decrypt it into a materialised collection.

---

## Incremental reads

**Oracle and Teradata read incrementally** from `LAST_MODIFIED`. That is a claim
about the view rather than a setting: the column must be UTC, monotonic, and
must move on **every** change including bulk updates and direct edits. Where it
cannot, the connector must be changed to declare no watermark column, and it
then reads in full every run — which is always safe.

`ORA_ROWSCN` is **not** an acceptable substitute. It is block-level unless the
table was created with `ROWDEPENDENCIES`, so two rows sharing a block share a
marker and one of them is skipped for ever.

**MongoDB reads in full every run.** An ObjectId `_id` encodes a *creation*
time, not a modification time, so it cannot carry a resume marker. If the
documents carry `updatedAt` the connector can be moved to the marker tier; if
they do not, change streams are not a substitute — a tailed oplog is a different
engine from a resumable `(marker, id)` checkpoint and does not satisfy the
contract every other source honours.

---

## Configuration

Each connector ships an `appsettings.json` whose `_note` block states its
contract and its refusal. The settings that differ from the SQL template:

| Setting | Oracle | Teradata | MongoDB |
|---|---|---|---|
| `DataSource:Server` | Easy Connect (`host:port/service`) or a TNS alias | System name or COP alias | `mongodb://` or `mongodb+srv://` URI |
| `DataSource:Database` | **not read** — Oracle has no Server-plus-Database pair | The database | The database |
| `DataSource:SqlAuthMode` | `SqlLogin`, or `Integrated` for a wallet or Kerberos | `SqlLogin` (TD2), or `Integrated` (KRB5) | `SqlLogin` |
| Vault secret key | `OraclePassword` | `TeradataPassword` | `MongoPassword` |

`Integrated` passes **no credential through the process at all**, and the
connector asks for no vault secret in that mode.

---

## Least privilege

Controls **DB-1** to **DB-3** in [SECURITY.md](SECURITY.md). In short: the crawl
identity holds `SELECT` on the one named view and nothing else; Teradata
additionally needs `DBC.ColumnsV` and `DBC.SecConstraintsV` or its guard cannot
run; no credential appears in configuration; and the resume marker is bound
rather than interpolated.

---

## Reconciliation

Two scripts, and neither is sufficient alone.

| | Direction | Covers |
|---|---|---|
| [`Compare-InventoryToIndex.ps1`](../deploy/Compare-InventoryToIndex.ps1) | inventory → Graph | `LOST` and `ORPHAN`. Needs no source, so it works for all five connectors |
| [`Compare-SourceToInventory.ps1`](../deploy/Compare-SourceToInventory.ps1) | source → inventory | Records the connector has **never written**. Oracle, Teradata and MongoDB |

Run both. Each prints what it did not check at the end of the run, so a clean
result from one is not mistaken for full coverage.

---

## Exit codes

The chassis's, unchanged: **0** success, **2** configuration invalid, **3**
credential, **4** ingestion — which is where every refusal above surfaces — and
**5** another run holds the lease.
