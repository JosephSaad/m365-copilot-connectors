---
title: The eight sources, routed
description: Two SQL, three CDP and three warehouse sources put through the Copilot Router — the verdict for each, the two premises that close half the gates, and what would change any of them.
---

# The eight sources, routed

The eight sources this repository can read or is being extended to read — two
SQL, three CDP, and three warehouse platforms — put through the
[Copilot Router](copilot-router.html) and the rule behind it in
[COPILOT-ROUTING.md](COPILOT-ROUTING.md).

This is a record of answers rather than an argument for them. The reasoning
lives in the routing document; what is here is which leaf each source landed on,
what that commits you to, and what would move it.

Date: 2026-08-30.

---

## The premises

Two facts close two of the five gates before any individual source is
considered. Both were stated by the customer rather than assumed.

| # | Stated | What it closes |
|---|---|---|
| **P1** | Every asker holds a Microsoft 365 Copilot licence | The **reach** gate. Foundry and Copilot Studio drop out; every route below stays on the Microsoft 365 and Graph plane, funded by M365 licensing with no Fabric footprint |
| **P2** | The organisation owns all eight sources outright — none is vendor-licensed | The **ownership** gate, and the entire vendor branch with it: no redistribution clause, no per-seat entitlement, no derived-data restriction, no AI-use clause, and no need for the negotiated rider the dashed path requires |

> **P2 was stated for the original five, and is assumed for sources 6 to 8.**
> Nobody has confirmed that the Oracle, MongoDB and Teradata estates are free of
> vendor-licensed content, and a warehouse is exactly where a market-data,
> credit-bureau or reference feed lands. One licensed feed joined into a table
> moves that table to the vendor branch entirely, and the join is invisible from
> the connector's side. Confirm per schema before any of the three is designed.

**P1 also decides the economics, not only the eligibility.** A crawl costs the
same whether ten people search or ten thousand, so the index is the one route
whose cost stops growing — it gets cheaper per head as the audience grows. The
alternatives run the other way: a federated connector needs a Copilot add-on
licence **and an individual consent for every user**, and per-call meters scale
linearly with headcount. With everyone licensed, `INDEX IT` is not merely
permitted, it is the dominant answer for anything that *can* be indexed.

**If either premise changes, re-run the router.** P2 is the fragile one: a
single vendor-licensed feed joined into any of these sources moves that source
to the vendor branch on its own, and the join is usually invisible from the
connector's side.

---

## What is still deciding

Three gates survive the premises. None of them is settled by owning the data.

| Gate | The question | Bites |
|---|---|---|
| **Shape of access** | Group-shaped, or enforced per user at the source? | CDP Hive, **Oracle**, **Teradata**, **MongoDB views** |
| **Content or computed** | Text that sits still, or a number derived across rows? | SQL hierarchy, **Teradata** |
| **Deletion SLA** | Must a removed record stop appearing immediately? | None — see the note below |

**The deletion gate no longer splits the index leaf here.** It used to pick
agent-hosted over direct push, because only a crawl detects deletions for you.
This repository's direct push now detects them itself through the crawl state
store's delete sweep — `crawl.Item.LastSeenRunId`, the sweep, and the delete
guard — which is live-tested. The SLA is therefore bounded by the crawl
interval on either path, and the hosting question decides instead. Nothing here
offers immediate removal; if a record must stop appearing the moment it is
deleted, no index path qualifies at all.

---

## The eight verdicts

| | Source | Route | Leaf |
|---|---|---|---|
| **1** | SQL tickets | `INDEX IT` | Synced connector, direct push |
| **2** | SQL hierarchy | **Split** — `INDEX IT` + `MODEL IT` | Direct push for the entities; a semantic model for the aggregates |
| **3** | CDP HDFS documents | `INDEX IT` | Synced connector, direct push |
| **4** | CDP Hive contracts | **Split on Ranger** — `INDEX IT` *or* `MODEL IT` | Direct push where unmasked; a semantic model where not |
| **5** | CDP Atlas catalogue | `INDEX IT` | Synced connector, direct push |
| **6** | Oracle | **Split on VPD, OLS and redaction** — `INDEX IT` *or* `MODEL IT` | Direct push where no per-user policy applies; a semantic model where one does |
| **7** | MongoDB | `INDEX IT`, **with a declared projection** | Direct push per collection; refuses a redacting view, and needs encrypted fields excluded rather than indexed |
| **8** | Teradata | **Mostly `MODEL IT`**, split on row-level security | A semantic model for the computed majority; direct push for text tables carrying no security constraint |

---

## 1 · SQL tickets

**`INDEX IT` → direct push.**

Owned content, group-shaped access, text that sits still. A ticket and its case
notes are the shape the index is for: someone asks about a ticket, and the
answer is that ticket. Every gate clears without argument.

Direct push rather than agent-hosted because the deletion gate no longer forces
a Windows host, and direct push runs anywhere with outbound HTTPS.

---

## 2 · SQL hierarchy

**Split. `INDEX IT` for the entities, `MODEL IT` for the aggregates.**

Ownership was never the question here — this source fails the *content or
computed* gate on one of its three item types and passes on the other two.

| Item type | Volume in the reference corpus | Content or computed | Route |
|---|---|---|---|
| Customer | 1,200 | Content — names, notes, status | Index |
| Engagement | 6,200 | Content — scope, dates, status | Index |
| **TimeEntry** | **104,400** | **Computed** — every real question is a sum | **Model** |

Nobody asks about one time entry. The questions that matter — hours against
budget, utilisation, what was billed to a customer last quarter — are numbers
derived across thousands of rows. Retrieval hands the model ten of them, and it
will answer fluently and wrongly from those ten, which is the worst available
failure mode. **Indexing a computed value also freezes it**, and a frozen number
that disagrees with the live report is a control problem rather than a
nuisance.

The `sql/12` views already shape Customer correctly, folding the engagement
rollup into the indexed content rather than leaving it to be joined at question
time. That is the pattern; it is simply outnumbered.

**Storage mode for the model half:** the source is on-premises SQL Server, and
**Direct Lake cannot use a gateway** — cloud connections only, no on-premises
gateway and no VNet gateway. So the choice is **Import** where a governed second
copy is permitted, **DirectQuery** where it is not, and DirectQuery's source
load scales with user concurrency.

**Two consequences worth costing before anyone starts.** This split crosses
planes: the connector half is M365-funded with no Fabric footprint, and the
model half needs an **F SKU with its own capacity admin and its own approval
path**. And under P1 the audience is large, so budget **F64 or above** — that is
the threshold at which viewers stop needing individual Pro licences, and a
smaller SKU is a false economy at that headcount.

**Recommended sequence:** ship the Customer and Engagement items now, and leave
the time entries out of the index entirely until somebody asks a computed
question. Then it is a Fabric business case with a number attached rather than
104,400 items spent on a guess.

---

## 3 · CDP HDFS documents

**`INDEX IT` → direct push.** Conditional on the documents existing.

The textbook connector case when the files are genuinely documents — reports,
contracts, specifications in a landing zone. Highest value per item of anything
on this list, because such files are already the size and shape of an answer.

**Not viable against the Hive warehouse.** Files under a warehouse directory are
Parquet or ORC, from which no text is extracted, and on CDP 7.1.9 managed tables
are transactional so the directory holds `base_` and `delta_` subdirectories
that mean nothing unmerged. Section 0 of
[CDP-PILOT-PARAMETERS.md](CDP-PILOT-PARAMETERS.md) covers this and the security
reason behind it.

Extraction covers PDF, the OpenXML family and plain text. Legacy binary formats,
`.msg`, archives, images and scanned PDFs index by name and metadata only —
there is no OCR — so question 3.6 on the parameter sheet has to be answered
before this route is committed to.

---

## 4 · CDP Hive contracts

**Split, and Ranger decides — not preference, and not ownership.**

Owning the data does not make a column mask go away. The *shape of access* gate
asks who enforces it and whether they still can afterwards, and on a Hive table
that is a cluster setting.

| Ranger state of the table | Route |
|---|---|
| No row filter, no column mask | `INDEX IT` → direct push. Table-level grants map to Entra groups and survive the copy |
| **Row filter or column mask present** | **Cannot be indexed.** `HivePushSource` refuses to read it at all |

The refusal is not caution. The rows the service account sees are the rows *its*
filter admits, so indexing them publishes one identity's view of the data to
everyone granted the item.

**For the masked tables, the recommended answer under P1 is to change the shape
rather than the route.** Build a Hive **view** that applies the masking at rest
and grant it group-wise; it stops being a per-user enforcement problem and
becomes indexable — flat cost, every M365 surface. This is the highest-leverage
move on the list, and question 3.2 of the parameter sheet already prefers a view
to a base table for a related reason.

A federated connector would also keep enforcement at the source and still answer
in Copilot chat, but under P1 it is the expensive choice: a Copilot add-on
licence plus an individual consent for every user, to reach content the
organisation owns.

If some tables genuinely need per-user row-level security preserved, model those
and accept the Fabric plane. Note what that costs on CDP:

- **Impala, not Hive.** Hive on Tez is batch, so DirectQuery against it lands in
  tens of seconds; Impala supports both Import and DirectQuery and is the
  recommended path.
- **Direct Lake is out** — on-premises HDFS, and Direct Lake cannot use a
  gateway.
- **Per-user identity needs the on-premises data gateway plus Kerberos
  constrained delegation.** Knox with LDAP is simpler and flattens every caller
  into one proxy identity, at which point row-level enforcement is gone.

> **Settle Ranger enforcement versus Power BI row-level security before design.**
> They are two enforcement engines over the same rows. Deciding late means either
> duplicating the policy in DAX or discovering the gateway flattened everyone
> into a service account.

Questions **5.2** and **5.3** on the parameter sheet are what tell you how much
of Hive is masked. Until they are answered this scenario cannot be scoped.

---

## 5 · CDP Atlas catalogue

**`INDEX IT` → direct push.**

The one that looks like it should be modelled and should not be. Descriptive
metadata — table names, column names, descriptions, classifications — is a
search problem over names and descriptions, so it belongs in the index however
structured it looks. *Which table holds contract terms* is a good question for
an index and a poor one for a semantic model.

Small item count, high value per item, cheap against the quota, and it makes the
estate navigable rather than merely searchable. Worth running **alongside** the
Hive connector rather than instead of it.

`hdfs_path` entities catalogue nothing, for the reason given at the end of the
parameter sheet.

---

## 6 · Oracle

**Split, and Oracle's own per-user features decide — the same shape as Hive.**

Grants are role-shaped and survive the copy, when they are grants to a role.
`DBA_ROLE_PRIVS` and `DBA_TAB_PRIVS` give the mapping; a grant made directly to a
database user does not survive, for the reason item 0 gives about Ranger
user-level grants — a Graph ACL carries Entra **group** object IDs and has
nowhere to put an individual.

Four features enforce per user at query time, and any one of them on an
in-scope table refuses the index:

| Feature | Where it shows | What it does |
|---|---|---|
| **VPD** (Virtual Private Database) | `DBA_POLICIES` | A policy function appends a predicate per session, so two callers running one query see different rows |
| **Oracle Label Security** | `DBA_SA_TABLE_POLICIES` | Compares a row's label against the session's clearance |
| **Real Application Security** | `DBA_XS_*` | Application users and ACLs evaluated inside the database |
| **Data Redaction** | `REDACTION_POLICIES` | Masks column values per session — the column-mask analogue exactly |

The refusal is the Hive refusal, restated: the rows the service account sees are
the rows *its* policy admits, so indexing them publishes one identity's view to
everyone granted the item.

**The identity mapping is the harder half.** Oracle roles are database-local.
Unless Enterprise User Security or Kerberos maps them to directory principals,
the grants are not merely unread — they are **unrepresentable**, and no amount of
connector work fixes that. Settle it before design, not during.

**The watermark needs choosing.** `ORA_ROWSCN` is tempting and wrong by default:
it is block-level unless the table was created with `ROWDEPENDENCIES`, so two
rows sharing a block share a marker. Either the table carries its own
modification timestamp or the source is not incrementally readable.

---

## 7 · MongoDB

**`INDEX IT` per collection — but a projection has to be declared first, and two
things can make a collection unindexable.**

Mongo differs from every other source here in that **access is collection-scoped
and there is no document-level security in the engine.** RBAC roles grant on a
database or a collection, so the ACL is a property of the collection rather than
of the item. That is simpler than SQL, and it means one resolved ACL serves
every document in the collection.

Two cases still refuse:

| Case | Why |
|---|---|
| **A view that redacts per caller** — `$redact` against `$$USER_ROLES`, or a `$match` on the caller | Per-user at query time. The same refusal as a Ranger mask |
| **Encrypted fields** — CSFLE or Queryable Encryption | The field is ciphertext at rest, so the connector would index ciphertext without knowing. Not a leak; the indexed content is simply useless |

That second row is the tokenized-at-rest case the CDP parameter sheet asks
about, arriving on a different platform. It fails **usefully closed only if
somebody notices** — nothing in the pipeline can tell ciphertext from text.
Encrypted fields have to be excluded from the projection by name.

**The projection is not optional.** Graph indexes declared properties and Mongo
has no fixed schema, so the mapping has to be declared per collection rather than
inferred. An inferred schema changes the moment a document does, and a schema
change means re-registration — which is a connection-level operation, not a
per-item one.

**Change detection is the real risk.** There is no universal modification
timestamp. An ObjectId `_id` encodes a **creation** time, not a modification
time, so it cannot carry the marker. Either the collection maintains its own
`updatedAt`, or the connector runs full crawls only, or it consumes change
streams — and a tailed oplog is a different engine from a resumable
`(marker, id)` checkpoint and does not satisfy that contract. **Establish which
before committing to incremental reads on Mongo at all.**

---

## 8 · Teradata

**Mostly `MODEL IT`, and the split that remains is row-level security.**

Teradata is usually the warehouse, and that decides more than its security model
does. The *content or computed* gate bites here the way it bites SQL hierarchy:
most of what a warehouse holds is a measure, and every real question of it is a
sum across rows. A sum is not text that sits still, an index cannot compute one,
and publishing pre-aggregated rows answers only the questions somebody
anticipated. **Model the computed majority and index the text minority** — the
descriptive tables, the reference data, the documentation-shaped columns.

For what remains indexable, the access shape is the familiar one:

| Teradata state of the table | Route |
|---|---|
| Role grants only — `DBC.AllRightsV`, `DBC.RoleMembersV` | `INDEX IT` → direct push. Role-shaped and survives the copy |
| **A row-level or column-level security constraint** — `DBC.SecConstraintsV`, and a constraint column on the table | **Cannot be indexed.** Constraint UDFs are evaluated per session, so the service account's rows are not everyone's rows |

The watermark question is SQL Server's, unchanged: Teradata has no universal
modification timestamp, so an in-scope table either carries one or is read in
full.

---

## The constraint that is actually binding

With P1 and P2 granted, the limit on all eight is no longer legal or
architectural. It is **the Copilot connector item quota and the quality of the
grounding**, in that order.

The quota is licensed, **tenant-wide and shared** with every connector in the
tenant, readable only from `connectionQuota.itemsRemaining` on the Graph beta
endpoint, and nothing in this connector watches it — the first sign of being
wrong is error 1008 or 1009 in the middle of a crawl.

Owning everything creates the temptation to index everything. Scenario 2 is the
worked example of why not: 104,400 of its 111,800 items are single time entries,
93% of the corpus spent on items nobody will ever retrieve individually. Fewer,
larger, more narrative items ground better and cost less.

---

## What each one still needs

| | Blocked on |
|---|---|
| 1 | Nothing. Live-tested twice at 111,800 items |
| 2 | A decision on whether the computed half is wanted at all, and an F SKU if it is |
| 3 | Parameter sheet **0.1**, **0.2**, **3.6** — and confirmation that documents exist outside the warehouse |
| 4 | Parameter sheet **5.2** and **5.3**. Cannot be scoped until Ranger's masking is known |
| 5 | Parameter sheet **2.5**, **3.5**, and entity-read in the `cm_atlas` Ranger service |
| 6 | Confirmation of P2 per schema; whether Oracle roles map to directory principals at all; a `DBA_POLICIES` / `REDACTION_POLICIES` inventory; a modification-timestamp column, since `ORA_ROWSCN` will not serve |
| 7 | A declared projection per collection; the list of encrypted fields to exclude; and an answer on change detection, because without an `updatedAt` there is no resumable marker |
| 8 | Which tables are text rather than measures — the split is the scoping question here; a `DBC.SecConstraintsV` inventory; a modification timestamp per in-scope table |

**Sources 6 to 8 have no code at all yet.** They are routed, not built: the
verdicts above are the design input to `ADDING-A-PUSH-CONNECTOR.md`.

**Reconciliation now covers all five**, which it did not when this page was
written. `Compare-SourceToIndex.ps1` reads `dbo.Tickets` and does not
generalise — four of the five sources have no SQL Server table to read.
`Compare-InventoryToIndex.ps1` needs no source at all: it reconciles
`crawl.vwItemInventory`, the connector's own record of every item it wrote, kept
in `ConnectorState` by the `PushCore.State` every connector shares. It reports
per item type, because the reference path is the **hierarchy** connector, which
writes three types into one connection and would hide a single-type divergence
in a combined total. What it cannot see is a source record that was never
pushed — that direction needs a source query, which is the half that does not
generalise, and the run says so rather than implying coverage it does not have. Three further facts govern
what building them costs: `PushCore.Sql` is bound to `Microsoft.Data.SqlClient`
rather than `DbConnection`, so the relational path does not extend to Oracle or
Teradata unchanged; each new driver adds a pin to `Directory.Packages.props` and
a regeneration of the offline package list that `Test-OfflinePackageList.ps1`
gates on; and each source needs its ACL derivation settled before its reader,
not after.

**All three CDP scenarios share one caveat: none has ever run against a real
cluster.** Both live tests exercised the SQL direct-push path. The CDP code is
tested against fakes, and the reconciliation script does not generalise to it.
Run `deploy/Test-CdpSource.ps1` on the connector host **as the service account**
before believing any estimate here — a probe run by a human tests the human's
Kerberos ticket, their Ranger grants and their HDFS group memberships, all of
which can pass while the service account fails.

---

## What would change these answers

- **A vendor-licensed feed joined into any source** — that source moves to the
  vendor branch, and the join is invisible from the connector's side.
- **Askers without a Copilot licence** — Foundry or Copilot Studio return to the
  table, and the M365 plane stops being the only destination.
- **A requirement that deleted records disappear immediately** — no index path
  qualifies, and all eight change.
- **Ranger masking arriving on a table that did not have it** — scenario 4 moves
  from indexable to not, silently, unless somebody is watching. The connector
  will refuse rather than leak, but the refusal is the first anyone hears of it.

---

*Related: [the routing rule in full](COPILOT-ROUTING.md) · [the interactive router](copilot-router.html) · [what we need from the CDP team](CDP-PILOT-PARAMETERS.md) · [how the items appear](COPILOT-SURFACING.md) · [capacity planning](CAPACITY-PLANNING.md)*
