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

**Ten questions offer "Planned" as well as yes/no**, and the distinction matters:
**Unknown** means nobody has checked, so go and check. **Planned** means somebody
checked, it is not there today, and it is coming — which obliges a design decision
now rather than a lookup. They are 0.2, 1.4, 2.5, 2.6, 3.1, 3.2, 3.3, 4.1, 4.2
and 5.4.

Six of those would **break a working connector**: row filters, masking, tag
policies, exceptions and validity periods all make a guard refuse, so a green
pilot becomes exit 4 on the day the change lands, with no code change on either
side. `ROUTING-DECISIONS.md` already names this — *"Ranger masking arriving on a
table that did not have it ... silently, unless somebody is watching"* — and this
is where the watching starts.

The other four **unblock** something, and 5.4 is the one that cannot wait: a
registered schema is append-only, so a sensitivity property has to be registered
before the connection reaches Ready or adding it later means deleting the
connection and every item in it.

**Planned without a date is only a softer "No".** Put the date in the notes.

| Marker | Meaning |
|---|---|
| **BLOCKING** | A wrong answer makes the connector unsafe or pointless. Do not design without it |
| **SIZING** | Changes effort, not viability |
| **OPERATIONAL** | Needed before go-live, not before design |

---

## Why these questions and not others

Three principles generate the whole list, and a question that is an instance of
none of them is probably not worth asking:

1. **The copy must never out-grant the original** — under-granting costs content,
   over-granting is an incident, and they do not get equal treatment.
2. **Know what the target cannot represent** — a Microsoft 365 permission is a
   static list of group IDs written at crawl time, and each of those three words
   is a hard limit.
3. **Usefulness is a separate axis from correctness** — a safe index carrying
   nothing readable fails as completely as one that leaks.

They are stated in full, with the reasoning and the cross-platform detail, in
[DESIGN-PRINCIPLES.md](DESIGN-PRINCIPLES.md) — including why per-user enforcement
cannot be indexed on any platform, and what each source concept becomes on the
way into a Graph ACL. **Read that first if you are onboarding a source type this
repository has not met.**

---

## Section 0 · Routing — does this belong in an index at all?

Ask before anything else. Three of these five have stopped a source outright.

| # | Ask | Question | Marker | Why |
|------|---|---|---|
| 0.1 | Tenant admin | Does every person who will ask hold a Microsoft 365 Copilot licence? | **BLOCKING** | If not, the destination is not Graph and none of the rest applies |
| 0.2 | Data owner | Does the organisation own this content outright, or is any of it licensed from a vendor? | **BLOCKING** | Licensed content collides with redistribution, per-seat entitlement, derived-data and AI-use clauses at once. Indexing makes a persistent copy — that is the whole objection. One licensed feed **joined into** a table moves the whole table, and the join is invisible from the connector's side |
| 0.3 | Data owner | Is the answer people want text that sits still, or a number computed across rows? | **BLOCKING** | An index holds documents and cannot compute a sum. Aggregates belong in a semantic model. Warehouses are mostly this |
| 0.4 | Data owner | Must a deleted record stop appearing **immediately**? | **BLOCKING** | No index path offers that. Deletion is bounded by the crawl interval on every route |
| 0.5 | Source platform, Security & IAM | Is access enforced per user at the source — row filters, masks, label security? | **BLOCKING** | See section 3. This is the question that most often ends the conversation |

---

## Section 1 · The source and the item

| # | Ask | Question | Marker | Why |
|------|---|---|---|
| 1.1 | Data owner | What is one item? A row, a file, a document, a table's description? | **BLOCKING** | Decides the schema, the ID and the corpus count |
| 1.2 | Source platform | How many items, today and in a year? | **SIZING** | Past roughly half a million, crawl duration and schema grain bind before the tenant's index does |
| 1.3 | Data owner, Source platform | Which view, table or collection is authoritative? | **BLOCKING** | A connector reads one named object. "Whichever is freshest" is not an answer a query can hold |
| 1.4 | Source platform | Can the source expose a **view** rather than a base table? | **SIZING** | A view is where soft-delete filters, column narrowing and at-rest masking live. It is the single highest-leverage request in this document |
| 1.5 | Source platform | What is the stable, unique key for an item? | **BLOCKING** | The item ID must be stable across crawls. A key that changes republishes as a new item and orphans the old one |
| 1.6 | Data owner | Is there a URL a person can open to see the real record? | **SIZING** | Without it, a Copilot result cannot be clicked through to the source |

---

## Section 2 · Identity and the ACL

> ## The ACL rule: one AD group per connector
>
> **Every item a connector writes is granted to a single AD group — the
> entitlement for that source — and to nothing else.** This holds even where the
> source system supports per-item access control, and **CDP is the case that
> matters**: its connectors can derive a per-item ACL from Ranger, and that
> derivation is deliberately not used to grant.
>
> **The safety condition this creates.** Per-item ACLs made safety automatic; one
> group makes it conditional, on a single rule:
>
> > *The AD group must be entitled to the least-accessible item in the corpus.*
>
> Any indexed object more restricted than the group is over-granted. Uniform
> accessibility stops being something the connector derives and becomes something
> the **scope** has to guarantee.
>
> **What the per-item derivation becomes.** Not dead code — the verifier. The
> Ranger groups a CDP connector can derive are exactly what proves the rule
> holds: for each object, assert the AD group's population is a subset of what
> the source grants. Anything failing that is excluded from scope, or the group
> is wrong.
>
> **Three consequences worth stating.** The source's groups no longer need to map
> to Entra groups at all, which removes a blocker that could otherwise end an
> Oracle or Teradata pilot. Permission changes at the source no longer have to
> reach the index, so the ACL staleness bound largely goes away. And **revocation
> moves from the source to AD** — removing someone's Ranger grant no longer
> removes their access to indexed content; removing them from the group does.
>
> **The refusals matter more, not less.** Under a uniform ACL, an object whose
> access is narrower than the group is precisely what must not be indexed, so
> every guard becomes a primary defence rather than a backstop.

The heart of the onboarding. Almost every question here can end the project.

| # | Ask | Question | Marker | Why |
|------|---|---|---|
| 2.1 | Security & IAM, Data owner | **Which single AD group (entitlement) holds everyone who may see this source's data?** | **BLOCKING** | One group per connector, for all of it. Not a list, and not a per-item derivation — see the rule above |
| 2.1a | Data owner, Security & IAM | **Is every object in scope accessible to everyone in that group?** | **BLOCKING** | The safety condition. Any object more restricted than the group is over-granted the moment it is indexed. Where the source can be queried for its own grants, verify rather than assume |
| 2.2 | Security & IAM | Are grants made to **groups**, or to named individuals? | **BLOCKING** | A Graph ACL carries Entra **group** object IDs. A grant to an individual may be *unrepresentable* rather than merely unimplemented |
| 2.3 | Security & IAM | Do the source's groups map to **Entra groups**, and by what mechanism? | **SIZING** | No longer blocking: the ACL needs one group id, not a mapping. Still wanted, because a mapping is what lets you *verify* 2.1a mechanically rather than by inspection |
| 2.4 | Tenant admin | Can you supply the **Entra group object ID** — not a name? | **OPERATIONAL** | Graph takes IDs, and under the one-group rule there is exactly one to supply |
| 2.5 | Security & IAM | Are there **exceptions** carved out of a grant — allow-exceptions, deny-exceptions? | **BLOCKING** | An exception a connector cannot read is read as absent. An unread *allow-exception* admits exactly the people the policy excludes |
| 2.6 | Security & IAM | Do any grants carry a **validity period** or a **time condition**? | **BLOCKING** | A Graph permission has no clock. A time-varying grant cannot be mirrored, only re-crawled often enough to bound the drift |
| 2.7 | Data owner, Security & IAM | How quickly must a permission change reach the index? | **SIZING** | Largely answered by the one-group rule: the index ACL is static, so a permission change at the source does not need to reach it. What does matter is **revocation**, which now runs through AD rather than the source |

---

## Section 3 · Per-user enforcement — the refusal questions

If any answer here is yes, the affected objects **cannot be indexed** and the
connector must refuse rather than read a partial view.

| # | Ask | Question | Marker | Why |
|------|---|---|---|
| 3.1 | Source platform | Are there **row filters** — different rows for different callers? | **BLOCKING** | The crawl identity's rows are not everyone's rows. Indexing them publishes one identity's view to every reader |
| 3.2 | Source platform | Are there **column masks or redaction** — different values for different callers? | **BLOCKING** | Same, per column. An index holds one copy and cannot vary it per reader |
| 3.3 | Security & IAM | Is there **label-based** or **classification-based** access control? | **BLOCKING** | Tag- and label-driven policies are usually invisible to a connector reading only resource-level grants |
| 3.4 | Source platform | If yes to any: can a **view** apply the restriction at rest and be granted group-wise instead? | **SIZING** | This converts an unindexable object into an indexable one and is almost always the cheapest fix |

---

## Section 4 · Change detection

| # | Ask | Question | Marker | Why |
|------|---|---|---|
| 4.1 | Source platform | Is there a reliable **modification timestamp** on every item? | **BLOCKING** | Without one there is no incremental read and every crawl is a full crawl |
| 4.2 | Source platform | Does it update on **every** change, including changes to children? | **BLOCKING** | A parent whose timestamp does not move when a child changes silently misses edits |
| 4.3 | Source platform | Can two items share the timestamp exactly? | **SIZING** | They can, which is why the marker is a **composite** `(timestamp, key)`. Confirm the key breaks ties totally |
| 4.4 | Source platform, Data owner | How are **deletions** represented — hard delete, soft-delete flag, or nothing? | **BLOCKING** | A hard delete cannot be detected incrementally. It needs a full-crawl inventory diff |
| 4.5 | Data owner | What proportion of the corpus could legitimately vanish between crawls? | **OPERATIONAL** | Sets the delete threshold. Too low blocks a legitimate purge; too high lets a broken source empty the index |

---

## Section 5 · Content and usefulness

Nothing here is a security question. All of it decides whether the pilot is worth
running.

| # | Ask | Question | Marker | Why |
|------|---|---|---|
| 5.1 | Data owner | Is the content **text a person would read**, or codes, tokens and measures? | **BLOCKING** | Grounding quality is the binding constraint on a connector's value. Identifiers index correctly and answer nothing |
| 5.2 | Source platform, Security & IAM | Are any fields **encrypted or tokenized at rest**? | **BLOCKING** | Ciphertext indexes without complaint and is useless. Nothing downstream can tell it from text — those fields must be excluded by name |
| 5.3 | Data owner | Are items larger than ~3.5 MB of text? | **SIZING** | Content is truncated with a visible marker. Confirm the head is the meaningful part |
| 5.4 | Security & IAM, Data owner | Do items carry a **sensitivity or classification label** to be preserved? | **OPERATIONAL** | Labels can be carried into Graph so labelled content stays labelled |
| 5.5 | Data owner | Is the content in one language, and does it need stemming or tokenisation the index does not do? | **SIZING** | Affects recall more than anything else on this page |

---

## Section 6 · Connectivity and credentials

| # | Ask | Question | Marker | Why |
|------|---|---|---|
| 6.1 | Network | What host, port and protocol, and from which network zone? | **OPERATIONAL** | Firewall requests have the longest lead time of anything here. Raise them first |
| 6.2 | Security & IAM, Source platform | How does the connector authenticate to the source? | **BLOCKING** | Decides whether a secret exists at all. Kerberos and wallets carry none |
| 6.3 | Security & IAM, Operations | If a password: where is it held, who rotates it, and how often? | **OPERATIONAL** | It must come from a vault or credential store, never from configuration |
| 6.4 | Source platform, Security & IAM | What is the **least privilege** the crawl identity needs? | **BLOCKING** | Read on the named view and nothing else. Confirm it cannot read the base tables |
| 6.5 | Network | Is outbound 443 to Graph direct or proxied? | **OPERATIONAL** | All Graph traffic can be forced through one proxy for egress review |
| 6.6 | Network | Which certificates must the connector host trust? | **OPERATIONAL** | Both to the source and to Graph |

---

## Section 7 · Operations and go-live

| # | Ask | Question | Marker | Why |
|------|---|---|---|
| 7.1 | Operations, Data owner | How often should it crawl, and full versus incremental? | **OPERATIONAL** | Also sets the ACL staleness bound — see 2.7 |
| 7.2 | Operations | Who is woken when a run fails? | **OPERATIONAL** | A connector nobody is paged for is a connector that stops silently |
| 7.3 | Operations | What reconciles source against index? | **OPERATIONAL** | Without a per-source reconciliation query, a silent divergence is never detected |
| 7.4 | Data owner, Operations | Who owns the connection, and who accepts the staleness bound in writing? | **OPERATIONAL** | Not an engineering question, and it blocks go-live regardless |
| 7.5 | Operations, Source platform | Is a **ConnectorState** database available, and who owns it? | **BLOCKING** | Four things need it: incremental reads, the single-instance run lock, run history, and both reconcilers. Without it the connector reads in full every run and cannot be reconciled at all — and an empty inventory is not evidence the index is empty |
| 7.6 | Operations, Security & IAM | Which **read-only login** will run the reconcilers? | **OPERATIONAL** | Not the connector's own: `sql/25` DENYs `crawl_writer` SELECT on the crawl views on purpose |

---

# Source-specific questions

## SQL Server

| # | Ask | Question | Marker | How to answer it |
|------|---|---|---|
| S.1 | Source platform | Is **Row-Level Security** applied? | **BLOCKING** | `SELECT * FROM sys.security_predicates`. **Stops nothing in code:** four connectors refuse a source that enforces per user, the SQL one does not, so this answer is the only control |
| S.2 | Source platform | Is **Dynamic Data Masking** applied to any column? | **BLOCKING** | `SELECT * FROM sys.masked_columns WHERE object_id = OBJECT_ID('<VIEW>')`. Also unguarded |
| S.3 | Source platform | Are any columns **Always Encrypted**? | **BLOCKING** | Ciphertext indexes silently and is useless to every reader. Also unguarded — MongoDB refuses its equivalent, SQL Server does not |
| S.4 | Source platform | Which view is authoritative, and can the push identity read **views only**? | **BLOCKING** | Control SQL-7: the crawl identity should not reach the base tables |
| S.5 | Security & IAM, Source platform | Entra token, Windows integrated, or SQL login from the vault? | **BLOCKING** | Control SQL-1 sets the preference order |
| S.6 | Source platform | Modification timestamp, and does it **cascade from children**? | **BLOCKING** | `sql/26` installs the cascading triggers; without them incremental misses hierarchy edits |
| S.7 | Source platform | Which column is the soft-delete flag? | **BLOCKING** | Control SQL-8: the filter must not be bypassable by editing the tool |
| S.8 | Network, Source platform | Is `Encrypt=true` available with a trusted certificate? | **OPERATIONAL** | Control SQL-3 rejects `TrustServerCertificate=true` in Production |
| S.9 | Data owner | What proportion of rows may legitimately disappear between crawls? | **OPERATIONAL** | Sets `Settings:MaxDeletePercent`; the default of 10 is a guess until somebody chooses |

## CDP — Hive, HDFS, Atlas

| # | Ask | Question | Marker | How to answer it |
|------|---|---|---|
| C.1 | Data owner | Which of the **three connectors** are in scope? | **BLOCKING** | Section 0 of `CDP-PILOT-PARAMETERS.md`. Decides whether one, two or three get built |
| C.2 | Security & IAM | Are Ranger **security zones** in use? | **BLOCKING** | **Stops the run.** Exit 4, `RefuseSecurityZones`, control CDP-17 |
| C.3 | Security & IAM | **Tag policies** on `cm_tag`, and is **Tagsync** running against Atlas? | **BLOCKING** | **Stops the run** if any tag policy denies or masks: exit 4, control CDP-19. One that only grants is ignored. `Settings:RangerTagService` defaults to `cm_tag` |
| C.4 | Security & IAM | Any **allowExceptions / denyExceptions** on read-carrying policies? | **BLOCKING** | `allowExceptions` are now **evaluated** — the groups are subtracted from the grant. `denyExceptions` are read and logged. Control CDP-18 |
| C.5 | Security & IAM | Do any policies grant to **named users** rather than groups? | **BLOCKING** | `RoutingEvaluator` reads `item.Groups` and never `item.Users` |
| C.6 | Security & IAM | Any **validitySchedules** or item **conditions**? | **BLOCKING** | **Stops the run.** A time-varying grant has no representation in a Graph permission, which is a static snapshot with no clock. Control CDP-18 |
| C.7 | Security & IAM, Source platform | Any in-scope Hive table carrying a **row filter or column mask**? | **BLOCKING** | **Stops that table.** Controls CDP-1 and CDP-2 |
| C.8 | Source platform | The exact **Ranger service names**? | **BLOCKING** | `cm_hive` and `cm_hdfs` are assumptions until confirmed |
| C.9 | Source platform, Security & IAM | Columns **tokenized at rest**, or masked by Ranger? | **BLOCKING** | Opposite outcomes: a mask skips the table, at-rest indexes tokens |
| C.10 | Security & IAM | HDFS **encryption zone**, and does the account need a KMS key ACL? | **BLOCKING** | KMS appears in the tag policy permission set |
| C.11 | Security & IAM, Operations | Kerberos realm, keytab location, rotation owner, gMSA? | **OPERATIONAL** | aes256 is mandatory after the cipher change |
| C.12 | Network | Ranger Admin URL and TLS port; is a firewall opening needed? | **OPERATIONAL** | Longest lead time on the CDP path |
| C.13 | Data owner | Which cluster and data domain; is QA or UAT the pilot? | **BLOCKING** | A domain may span more than one cluster, so naming it alone leaves the environment ambiguous |

## Oracle

| # | Ask | Question | Marker | How to answer it |
|------|---|---|---|
| O.1 | Source platform | Is there a **VPD policy** on the view? | **BLOCKING** | `SELECT * FROM ALL_POLICIES WHERE OBJECT_NAME = '<VIEW>'` |
| O.2 | Source platform | Is **Oracle Label Security** in use? | **BLOCKING** | `SELECT * FROM ALL_SA_TABLE_POLICIES WHERE TABLE_NAME = '<VIEW>'` |
| O.3 | Source platform | Is **Data Redaction** applied? | **BLOCKING** | `SELECT * FROM REDACTION_POLICIES WHERE OBJECT_NAME = '<VIEW>'` |
| O.4 | Source platform | Is **Real Application Security** in use? | **BLOCKING** | `SELECT COUNT(*) FROM DBA_XS_SECURITY_CLASSES` |
| O.5 | Security & IAM | Do Oracle **roles map to directory principals** — Enterprise User Security, or Kerberos? | **BLOCKING** | If not, grants are unrepresentable in a Graph ACL and there is no connector to build |
| O.6 | Security & IAM, Source platform | Which **roles** hold SELECT, and what are their Entra equivalents? | **BLOCKING** | `SELECT * FROM ALL_TAB_PRIVS WHERE TABLE_NAME = '<VIEW>'` |
| O.7 | Source platform | Is there a modification timestamp column? | **BLOCKING** | `ORA_ROWSCN` is **not** an answer: it is block-level unless the table was built with `ROWDEPENDENCIES`, so rows sharing a block share a marker |
| O.8 | Source platform, Network | Easy Connect string or TNS alias, and is a wallet in play? | **OPERATIONAL** | Oracle has no Server-plus-Database pair; one value carries both |
| O.9 | Source platform | Database version 12c or later? | **SIZING** | The reader uses `FETCH FIRST`, which 11g does not have |
| O.10 | Data owner, Security & IAM | Which column carries **classifications**, if labels are wanted? | **OPERATIONAL** | The connector reads a `CLASSIFICATIONS` column, comma-separated. The label property is registered whether or not you use it, because a registered schema is append-only |

## MongoDB

| # | Ask | Question | Marker | How to answer it |
|------|---|---|---|
| M.1 | Source platform | Which **collections**, and is the granularity of access the whole collection? | **BLOCKING** | Mongo has no document-level security in the engine. One ACL serves a whole collection |
| M.2 | Source platform | Is the source a **view** using `$redact` or `$$USER_ROLES`? | **BLOCKING** | That is per-caller enforcement and cannot be indexed |
| M.3 | Source platform, Security & IAM | Are any fields under **CSFLE or Queryable Encryption**? | **BLOCKING** | They arrive as ciphertext and must be excluded by name, or the index fills with unusable values |
| M.4 | Data owner, Source platform | What is the **declared projection** — which fields become which properties? | **BLOCKING** | Mongo has no fixed schema and Graph needs declared properties. This cannot be inferred, because an inferred schema changes when a document does |
| M.5 | Source platform | Does every document carry an **`updatedAt`**? | **BLOCKING** | `_id` encodes **creation** time, not modification. Without `updatedAt` there is no resumable marker and every crawl is full |
| M.6 | Security & IAM | Which **roles** grant read, and how do they map to Entra groups? | **BLOCKING** | `db.getRoles({showPrivileges: true})` |
| M.7 | Source platform | Are documents deleted outright, or flagged? | **BLOCKING** | A hard delete needs a full-crawl inventory diff |
| M.8 | Source platform | Replica set or sharded, and is there a read-preference requirement? | **OPERATIONAL** | A crawl should read from a secondary where one exists |
| M.9 | Data owner, Source platform | Is the source a **GridFS bucket** rather than a collection? | **SIZING** | Detected, not configured — a bucket is the pair `<name>.files` and `<name>.chunks`. In bucket mode each file's text is extracted and items are typed File |
| M.10 | Data owner, Security & IAM | Which field carries **classifications**, if labels are wanted? | **OPERATIONAL** | An array or a comma-separated string |

## Teradata

| # | Ask | Question | Marker | How to answer it |
|------|---|---|---|
| T.1 | Source platform | Does the table carry a **row-level security constraint**? | **BLOCKING** | `SELECT * FROM DBC.SecConstraintsV` and check for constraint columns on the table |
| T.2 | Source platform | Are there **column-level** constraints? | **BLOCKING** | Same view. Same refusal |
| T.3 | Data owner | Which tables are **text** rather than measures? | **BLOCKING** | The scoping question for a warehouse. Most of it belongs in a semantic model, not an index |
| T.4 | Security & IAM, Source platform | Which **roles** hold SELECT, and their Entra equivalents? | **BLOCKING** | `SELECT * FROM DBC.AllRightsV WHERE TableName = '<TABLE>'` |
| T.5 | Source platform | Is there a modification timestamp per in-scope table? | **BLOCKING** | Teradata has no universal one |
| T.6 | Source platform | Can the crawl identity read `DBC.ColumnsV` and `DBC.SecConstraintsV`? | **BLOCKING** | Without both grants the security guard cannot run, and the connector fails **closed** rather than reading on |
| T.7 | Data owner | Is the estate the system of record, or a downstream copy? | **SIZING** | Indexing a copy indexes yesterday's answer, and nobody reading the result will know |
| T.8 | Operations, Source platform | Is there a query band or workload rule a crawl must set? | **OPERATIONAL** | A long full-corpus read can land in the wrong workload class and be throttled |
| T.9 | Data owner, Security & IAM | Which column carries **classifications**, if labels are wanted? | **OPERATIONAL** | Same `CLASSIFICATIONS` contract as Oracle |

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