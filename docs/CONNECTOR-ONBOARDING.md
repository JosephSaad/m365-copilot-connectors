---
title: Onboarding a new connector
description: The questions to ask before a new source is connected — the general set every connector needs answered, and the source-specific set for Oracle, MongoDB and Teradata.
---

# Onboarding a new connector

The questions to answer **before** a connector is designed, in the order that
lets you stop early. Capture them with
[`connector-onboarding-form.html`](connector-onboarding-form.html), which
produces the same sections as Markdown or JSON.

This exists because every defect this project has found in eighteen months came
from one of three places: an access-control construct the connector could not
see, a change the connector could not detect, or content the connector indexed
correctly and uselessly. Each section below is one of those three, asked early
enough to be cheap.

**How to read the markers.**

| Marker | Meaning |
|---|---|
| **BLOCKING** | A wrong answer makes the connector unsafe or pointless. Do not design without it |
| **SIZING** | Changes effort, not viability |
| **OPERATIONAL** | Needed before go-live, not before design |

---

## Why these questions and not others

Three principles generate the whole list. Every question below is an instance of
one of them, and a question that is an instance of none of them is probably not
worth asking.

**1. The copy must never out-grant the original.** A connector translates one
system's access decision into Graph's. Under-granting costs content;
over-granting puts data in front of someone the source refuses. The two are not
equally bad, so the questions that detect over-granting come first.

**2. Know what the target cannot represent.** A Graph permission is a static
snapshot of Entra group IDs written at crawl time. It cannot express a rule that
varies by user at query time, a rule that changes on a date, or a grant to a
named individual. Where the source has such a rule, no amount of connector work
mirrors it — the honest options are refuse, or exclude from scope.

**3. Usefulness is a separate axis from correctness.** A table indexed perfectly
safely, whose content is ciphertext, tokens or a numeric measure, fails the pilot
as completely as one indexed wrongly. Some questions below have no security
consequence at all and still decide whether to proceed.

---

## Section 0 · Routing — does this belong in an index at all?

Ask before anything else. Three of these five have stopped a source outright.

| # | Question | Marker | Why |
|---|---|---|---|
| 0.1 | Does every person who will ask hold a Microsoft 365 Copilot licence? | **BLOCKING** | If not, the destination is not Graph and none of the rest applies |
| 0.2 | Does the organisation own this content outright, or is any of it licensed from a vendor? | **BLOCKING** | Licensed content collides with redistribution, per-seat entitlement, derived-data and AI-use clauses at once. Indexing makes a persistent copy — that is the whole objection. One licensed feed **joined into** a table moves the whole table, and the join is invisible from the connector's side |
| 0.3 | Is the answer people want text that sits still, or a number computed across rows? | **BLOCKING** | An index holds documents and cannot compute a sum. Aggregates belong in a semantic model. Warehouses are mostly this |
| 0.4 | Must a deleted record stop appearing **immediately**? | **BLOCKING** | No index path offers that. Deletion is bounded by the crawl interval on every route |
| 0.5 | Is access enforced per user at the source — row filters, masks, label security? | **BLOCKING** | See section 3. This is the question that most often ends the conversation |

---

## Section 1 · The source and the item

| # | Question | Marker | Why |
|---|---|---|---|
| 1.1 | What is one item? A row, a file, a document, a table's description? | **BLOCKING** | Decides the schema, the ID and the corpus count |
| 1.2 | How many items, today and in a year? | **SIZING** | Past roughly half a million, crawl duration and schema grain bind before the tenant's index does |
| 1.3 | Which view, table or collection is authoritative? | **BLOCKING** | A connector reads one named object. "Whichever is freshest" is not an answer a query can hold |
| 1.4 | Can the source expose a **view** rather than a base table? | **SIZING** | A view is where soft-delete filters, column narrowing and at-rest masking live. It is the single highest-leverage request in this document |
| 1.5 | What is the stable, unique key for an item? | **BLOCKING** | The item ID must be stable across crawls. A key that changes republishes as a new item and orphans the old one |
| 1.6 | Is there a URL a person can open to see the real record? | **SIZING** | Without it, a Copilot result cannot be clicked through to the source |

---

## Section 2 · Identity and the ACL

The heart of the onboarding. Almost every question here can end the project.

| # | Question | Marker | Why |
|---|---|---|---|
| 2.1 | Who is allowed to see each item, and where is that recorded? | **BLOCKING** | Every connector needs one answer: a configured ACL for the whole corpus, or a per-item ACL derived from the source |
| 2.2 | Are grants made to **groups**, or to named individuals? | **BLOCKING** | A Graph ACL carries Entra **group** object IDs. A grant to an individual may be *unrepresentable* rather than merely unimplemented |
| 2.3 | Do the source's groups map to **Entra groups**, and by what mechanism? | **BLOCKING** | Database-local roles, POSIX groups and LDAP groups are not Entra groups. Without a mapping the ACL cannot be written at all |
| 2.4 | Can you supply the **Entra group object IDs** — not names? | **OPERATIONAL** | Graph takes IDs. Names are resolved by somebody, and it is better to know who |
| 2.5 | Are there **exceptions** carved out of a grant — allow-exceptions, deny-exceptions? | **BLOCKING** | An exception a connector cannot read is read as absent. An unread *allow-exception* admits exactly the people the policy excludes |
| 2.6 | Do any grants carry a **validity period** or a **time condition**? | **BLOCKING** | A Graph permission has no clock. A time-varying grant cannot be mirrored, only re-crawled often enough to bound the drift |
| 2.7 | How quickly must a permission change reach the index? | **BLOCKING** | Permission changes usually do not change content, so only a **full** crawl re-derives ACLs. That interval is the staleness bound, and somebody has to accept it in writing |

---

## Section 3 · Per-user enforcement — the refusal questions

If any answer here is yes, the affected objects **cannot be indexed** and the
connector must refuse rather than read a partial view.

| # | Question | Marker | Why |
|---|---|---|---|
| 3.1 | Are there **row filters** — different rows for different callers? | **BLOCKING** | The crawl identity's rows are not everyone's rows. Indexing them publishes one identity's view to every reader |
| 3.2 | Are there **column masks or redaction** — different values for different callers? | **BLOCKING** | Same, per column. An index holds one copy and cannot vary it per reader |
| 3.3 | Is there **label-based** or **classification-based** access control? | **BLOCKING** | Tag- and label-driven policies are usually invisible to a connector reading only resource-level grants |
| 3.4 | If yes to any: can a **view** apply the restriction at rest and be granted group-wise instead? | **SIZING** | This converts an unindexable object into an indexable one and is almost always the cheapest fix |

---

## Section 4 · Change detection

| # | Question | Marker | Why |
|---|---|---|---|
| 4.1 | Is there a reliable **modification timestamp** on every item? | **BLOCKING** | Without one there is no incremental read and every crawl is a full crawl |
| 4.2 | Does it update on **every** change, including changes to children? | **BLOCKING** | A parent whose timestamp does not move when a child changes silently misses edits |
| 4.3 | Can two items share the timestamp exactly? | **SIZING** | They can, which is why the marker is a **composite** `(timestamp, key)`. Confirm the key breaks ties totally |
| 4.4 | How are **deletions** represented — hard delete, soft-delete flag, or nothing? | **BLOCKING** | A hard delete cannot be detected incrementally. It needs a full-crawl inventory diff |
| 4.5 | What proportion of the corpus could legitimately vanish between crawls? | **OPERATIONAL** | Sets the delete threshold. Too low blocks a legitimate purge; too high lets a broken source empty the index |

---

## Section 5 · Content and usefulness

Nothing here is a security question. All of it decides whether the pilot is worth
running.

| # | Question | Marker | Why |
|---|---|---|---|
| 5.1 | Is the content **text a person would read**, or codes, tokens and measures? | **BLOCKING** | Grounding quality is the binding constraint on a connector's value. Identifiers index correctly and answer nothing |
| 5.2 | Are any fields **encrypted or tokenized at rest**? | **BLOCKING** | Ciphertext indexes without complaint and is useless. Nothing downstream can tell it from text — those fields must be excluded by name |
| 5.3 | Are items larger than ~3.5 MB of text? | **SIZING** | Content is truncated with a visible marker. Confirm the head is the meaningful part |
| 5.4 | Do items carry a **sensitivity or classification label** to be preserved? | **OPERATIONAL** | Labels can be carried into Graph so labelled content stays labelled |
| 5.5 | Is the content in one language, and does it need stemming or tokenisation the index does not do? | **SIZING** | Affects recall more than anything else on this page |

---

## Section 6 · Connectivity and credentials

| # | Question | Marker | Why |
|---|---|---|---|
| 6.1 | What host, port and protocol, and from which network zone? | **OPERATIONAL** | Firewall requests have the longest lead time of anything here. Raise them first |
| 6.2 | How does the connector authenticate to the source? | **BLOCKING** | Decides whether a secret exists at all. Kerberos and wallets carry none |
| 6.3 | If a password: where is it held, who rotates it, and how often? | **OPERATIONAL** | It must come from a vault or credential store, never from configuration |
| 6.4 | What is the **least privilege** the crawl identity needs? | **BLOCKING** | Read on the named view and nothing else. Confirm it cannot read the base tables |
| 6.5 | Is outbound 443 to Graph direct or proxied? | **OPERATIONAL** | All Graph traffic can be forced through one proxy for egress review |
| 6.6 | Which certificates must the connector host trust? | **OPERATIONAL** | Both to the source and to Graph |

---

## Section 7 · Operations and go-live

| # | Question | Marker | Why |
|---|---|---|---|
| 7.1 | How often should it crawl, and full versus incremental? | **OPERATIONAL** | Also sets the ACL staleness bound — see 2.7 |
| 7.2 | Who is woken when a run fails? | **OPERATIONAL** | A connector nobody is paged for is a connector that stops silently |
| 7.3 | What reconciles source against index? | **OPERATIONAL** | Without a per-source reconciliation query, a silent divergence is never detected |
| 7.4 | Who owns the connection, and who accepts the staleness bound in writing? | **OPERATIONAL** | Not an engineering question, and it blocks go-live regardless |

---

# Source-specific questions

## SQL Server

| # | Question | Marker | How to answer it |
|---|---|---|---|
| S.1 | Is **Row-Level Security** applied? | **BLOCKING** | `SELECT * FROM sys.security_predicates` — and note the SQL connector does **not** currently guard this |
| S.2 | Is **Dynamic Data Masking** applied to any column? | **BLOCKING** | `SELECT * FROM sys.masked_columns WHERE object_id = OBJECT_ID('<VIEW>')` |
| S.3 | Are any columns **Always Encrypted**? | **BLOCKING** | Ciphertext indexes silently and is useless to every reader |
| S.4 | Which view is authoritative, and can the push identity read **views only**? | **BLOCKING** | Control SQL-7: the crawl identity should not reach the base tables |
| S.5 | Entra token, Windows integrated, or SQL login from the vault? | **BLOCKING** | Control SQL-1 sets the preference order |
| S.6 | Modification timestamp, and does it **cascade from children**? | **BLOCKING** | `sql/26` installs the cascading triggers; without them incremental misses hierarchy edits |
| S.7 | Which column is the soft-delete flag? | **BLOCKING** | Control SQL-8: the filter must not be bypassable by editing the tool |
| S.8 | Is `Encrypt=true` available with a trusted certificate? | **OPERATIONAL** | Control SQL-3 rejects `TrustServerCertificate=true` in Production |
| S.9 | What proportion of rows may legitimately disappear between crawls? | **OPERATIONAL** | Sets `Settings:MaxDeletePercent`; the default of 10 is a guess until somebody chooses |

## CDP — Hive, HDFS, Atlas

| # | Question | Marker | How to answer it |
|---|---|---|---|
| C.1 | Which of the **three connectors** are in scope? | **BLOCKING** | Section 0 of `CDP-PILOT-PARAMETERS.md`. Decides whether one, two or three get built |
| C.2 | Are Ranger **security zones** in use? | **BLOCKING** | Yes stops the run: `RefuseSecurityZones`, exit 4, control CDP-17 |
| C.3 | **Tag policies** on `cm_tag`, and is **Tagsync** running against Atlas? | **BLOCKING** | The connector reads resource services only and never the tag service |
| C.4 | Any **allowExceptions / denyExceptions** on read-carrying policies? | **BLOCKING** | Neither is parsed. An unread `allowExceptions` admits exactly the people the policy excludes |
| C.5 | Do any policies grant to **named users** rather than groups? | **BLOCKING** | `RoutingEvaluator` reads `item.Groups` and never `item.Users` |
| C.6 | Any **validitySchedules** or item **conditions**? | **BLOCKING** | Not parsed, and a time-varying grant has no representation in a Graph ACL |
| C.7 | Any in-scope Hive table carrying a **row filter or column mask**? | **BLOCKING** | Those tables cannot be indexed at all: CDP-1 and CDP-2 |
| C.8 | The exact **Ranger service names**? | **BLOCKING** | `cm_hive` and `cm_hdfs` are assumptions until confirmed |
| C.9 | Columns **tokenized at rest**, or masked by Ranger? | **BLOCKING** | Opposite outcomes: a mask skips the table, at-rest indexes tokens |
| C.10 | HDFS **encryption zone**, and does the account need a KMS key ACL? | **BLOCKING** | KMS appears in the tag policy permission set |
| C.11 | Kerberos realm, keytab location, rotation owner, gMSA? | **OPERATIONAL** | aes256 is mandatory after the cipher change |
| C.12 | Ranger Admin URL and TLS port; is a firewall opening needed? | **OPERATIONAL** | Longest lead time on the CDP path |
| C.13 | Which cluster and data domain; is QA or UAT the pilot? | **BLOCKING** | ACZ has a footprint on more than one cluster |

## Oracle

| # | Question | Marker | How to answer it |
|---|---|---|---|
| O.1 | Is there a **VPD policy** on the view? | **BLOCKING** | `SELECT * FROM ALL_POLICIES WHERE OBJECT_NAME = '<VIEW>'` |
| O.2 | Is **Oracle Label Security** in use? | **BLOCKING** | `SELECT * FROM ALL_SA_TABLE_POLICIES WHERE TABLE_NAME = '<VIEW>'` |
| O.3 | Is **Data Redaction** applied? | **BLOCKING** | `SELECT * FROM REDACTION_POLICIES WHERE OBJECT_NAME = '<VIEW>'` |
| O.4 | Is **Real Application Security** in use? | **BLOCKING** | `SELECT COUNT(*) FROM DBA_XS_SECURITY_CLASSES` |
| O.5 | Do Oracle **roles map to directory principals** — Enterprise User Security, or Kerberos? | **BLOCKING** | If not, grants are unrepresentable in a Graph ACL and there is no connector to build |
| O.6 | Which **roles** hold SELECT, and what are their Entra equivalents? | **BLOCKING** | `SELECT * FROM ALL_TAB_PRIVS WHERE TABLE_NAME = '<VIEW>'` |
| O.7 | Is there a modification timestamp column? | **BLOCKING** | `ORA_ROWSCN` is **not** an answer: it is block-level unless the table was built with `ROWDEPENDENCIES`, so rows sharing a block share a marker |
| O.8 | Easy Connect string or TNS alias, and is a wallet in play? | **OPERATIONAL** | Oracle has no Server-plus-Database pair; one value carries both |
| O.9 | Database version 12c or later? | **SIZING** | The reader uses `FETCH FIRST`, which 11g does not have |

## MongoDB

| # | Question | Marker | How to answer it |
|---|---|---|---|
| M.1 | Which **collections**, and is the granularity of access the whole collection? | **BLOCKING** | Mongo has no document-level security in the engine. One ACL serves a whole collection |
| M.2 | Is the source a **view** using `$redact` or `$$USER_ROLES`? | **BLOCKING** | That is per-caller enforcement and cannot be indexed |
| M.3 | Are any fields under **CSFLE or Queryable Encryption**? | **BLOCKING** | They arrive as ciphertext and must be excluded by name, or the index fills with unusable values |
| M.4 | What is the **declared projection** — which fields become which properties? | **BLOCKING** | Mongo has no fixed schema and Graph needs declared properties. This cannot be inferred, because an inferred schema changes when a document does |
| M.5 | Does every document carry an **`updatedAt`**? | **BLOCKING** | `_id` encodes **creation** time, not modification. Without `updatedAt` there is no resumable marker and every crawl is full |
| M.6 | Which **roles** grant read, and how do they map to Entra groups? | **BLOCKING** | `db.getRoles({showPrivileges: true})` |
| M.7 | Are documents deleted outright, or flagged? | **BLOCKING** | A hard delete needs a full-crawl inventory diff |
| M.8 | Replica set or sharded, and is there a read-preference requirement? | **OPERATIONAL** | A crawl should read from a secondary where one exists |

## Teradata

| # | Question | Marker | How to answer it |
|---|---|---|---|
| T.1 | Does the table carry a **row-level security constraint**? | **BLOCKING** | `SELECT * FROM DBC.SecConstraintsV` and check for constraint columns on the table |
| T.2 | Are there **column-level** constraints? | **BLOCKING** | Same view. Same refusal |
| T.3 | Which tables are **text** rather than measures? | **BLOCKING** | The scoping question for a warehouse. Most of it belongs in a semantic model, not an index |
| T.4 | Which **roles** hold SELECT, and their Entra equivalents? | **BLOCKING** | `SELECT * FROM DBC.AllRightsV WHERE TableName = '<TABLE>'` |
| T.5 | Is there a modification timestamp per in-scope table? | **BLOCKING** | Teradata has no universal one |
| T.6 | Is the estate the system of record, or a downstream copy? | **SIZING** | Indexing a copy indexes yesterday's answer, and nobody reading the result will know |
| T.7 | Is there a query band or workload rule a crawl must set? | **OPERATIONAL** | A long full-corpus read can land in the wrong workload class and be throttled |

---

## What the answers produce

| Answers | What they decide |
|---|---|
| Section 0 | Whether to build at all |
| Sections 2 and 3 | The ACL derivation, and whether the connector must refuse |
| Section 4 | Incremental or full-only, and the delete strategy |
| Section 5 | Whether the corpus is worth indexing |
| Sections 6 and 7 | Deployment, and what blocks go-live |
| Source-specific | The guard query the connector runs before its first read |
