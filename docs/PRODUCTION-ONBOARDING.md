# Production onboarding — SQL and CDP

**Purpose.** What has to be true before either connector is a supported
production service rather than a pilot that happens to be running. Every row
names an owner and a state, because the common failure is not a missing control
— it is a control nobody owns.

**How this differs from the pilot documents.** The
[SQL](SQL-PILOT-PARAMETERS.md) and [CDP](CDP-PILOT-PARAMETERS.md) parameter
lists ask *what do we need to connect*. This one asks *what breaks at 03:00, who
is woken, and what do they do*. A pilot that works proves the pipeline; it
proves nothing about the six months after.

**How this differs from go-live readiness.**
[`GO-LIVE-READINESS.md`](GO-LIVE-READINESS.md) asks *is the code proven* —
feature by feature, what is built, what is part-built, and the six verification
tasks nobody has run yet. This document asks *is the service owned*. Neither
replaces the other: clearing every blocker there still leaves every Owner cell
here blank, and filling this in does not make an unexecuted SQL script run.

Legend: **Gate** = production does not start without it · **Check** = must be
answered and recorded, an answer of "accepted" is valid.

---

## 1 · Go / no-go gates

Not negotiable, and all four are cheaper to settle now than to discover.

| # | Type | What must be true | Why | Owner | State |
|---|---|---|---|---|---|
| 1.1 | Gate | **The deletion SLA is met by the chosen path** | Since v1.3.0 a direct push does delete, but only on a *full* crawl and only with a state store configured: absence from an incremental read means nothing, so the SLA is bounded by the full-crawl cadence (`Settings:FullEveryHours`, default 168), not by the run interval. An agent-hosted crawl deletes on its next incremental pass. Without `Settings:StateConnectionString` neither applies and nothing is ever deleted. If the agreed SLA is tighter than the chosen mechanism, the SLA has to change — engineering cannot close this gap | | |
| 1.2 | Gate | **The ACL staleness bound is written into the risk register** | A permission change at the source does not alter a row's timestamp, so only the periodic full recrawl re-derives who may see an item. At the default of 7 daily runs, a revocation can take a week to reach the index. That is a number somebody must accept in writing | | |
| 1.3 | Gate | **Someone owns the connection**, by name, not by team | A Graph connection is a durable object with a quota, a schema that cannot be edited, and items that outlive the person who created them | | |
| 1.4 | Gate | **A backout exists and has been rehearsed** | Deleting the connection removes every item in it. That is the backout. It should be a decision somebody has already thought about, not one taken during an incident | | |

## 2 · Identity, secrets and their expiry

| # | Type | What must be true | Why | Owner | State |
|---|---|---|---|---|---|
| 2.1 | Gate | **No secret exists in configuration, environment or logs** | The build fails on a credential-shaped key with a value, and history is scanned. This is a property to keep, not a state to reach once | | |
| 2.2 | Gate | **Certificate expiry is monitored**, not just warned about | The connector warns daily inside the expiry window and errors once expired. A warning nobody routes to a person is not monitoring — confirm where that log line goes | | |
| 2.3 | Gate | **Certificate rotation has been performed once**, in a rehearsal | Rotation is install-new, restart, confirm from the log, remove-old. Rehearse it before the first one happens under time pressure | | |
| 2.4 | Check | The **gMSA's password rotation** is confirmed working, and the host can still reach a domain controller | Active Directory rotates it on its own schedule. This is the design's strength and it fails silently if the host loses the DC | | |
| 2.5 | Check | If a SQL login is used, the **Key Vault secret has an owner and a rotation cadence** | The connector re-reads on an authentication failure exactly once. A rotated secret nobody told anyone about surfaces as an exit code 3 | | |

## 3 · Access model

| # | Type | What must be true | Why | Owner | State |
|---|---|---|---|---|---|
| 3.1 | Gate | **The ACL on a sample of real items has been verified against the source**, as a real user | Search is security-trimmed, so testing as an administrator proves nothing. Run it as somebody in the group and as somebody who should see nothing | | |
| 3.2 | Gate | **A revocation path is agreed** — what happens when someone loses access urgently | The honest answer within the index model is: remove them from the Entra group, and the item stops being returned. Confirm that is understood and sufficient | | |
| 3.3 | Check | Every cluster or source group is **mapped**, and unmapped groups are known | An unmapped group means the item is granted to nobody and skipped. Silent under-granting is safe but looks like missing content | | |
| 3.4 | Check | **No licensed third-party content is in scope**, or a rider permits it | Indexing licensed content is a redistribution and entitlement event, and vendors audit seat counts | | |

## 4 · Scheduling, capacity and cost

| # | Type | What must be true | Why | Owner | State |
|---|---|---|---|---|---|
| 4.1 | Gate | **Something invokes the run, and something notices when it does not** | Direct push has no scheduler. A task that silently stops looks exactly like a source where nothing changed | | |
| 4.2 | Gate | **The item quota has been checked against licence count**, with headroom for growth | Quota is a tenant-wide ceiling. Discovering it during a full crawl is the expensive way | | |
| 4.3 | Check | `ItemBudget` is set to a **deliberate** number | It refuses to start when scope exceeds it — a guard against a source that grew by an order of magnitude overnight | | |
| 4.4 | Check | `MaxErrorRatePercent` is agreed | The run aborts above it rather than limping to a "success" with a third of the data missing | | |
| 4.5 | Check | For CDP with Power BI in the picture, **capacity licensing is confirmed** | Copilot over a semantic model needs paid Fabric F2+ or Premium P1+. Trial capacities are not supported — the usual reason a pilot works and production does not | | |

## 5 · Monitoring and audit

| # | Type | What must be true | Why | Owner | State |
|---|---|---|---|---|---|
| 5.1 | Gate | **Exit codes are alerted on**, distinctly | `2` configuration, `3` credential rejected, `4` ingestion failed. They mean different things and route to different people; alerting on "non-zero" wastes the distinction | | |
| 5.2 | Gate | **Logs have a retention period and a reader** | For direct push the log is the *only* health signal — nothing appears in the admin centre. If nobody reads it, the connector has no monitoring at all | | |
| 5.3 | Check | The **run summary** is captured per run: written, skipped, truncated, errors | `skipped=` is the number that tells you the access model is behaving. A sudden rise means policies changed | | |
| 5.4 | Check | Someone can answer **"who saw this record, and when"** | Ask now whether that question will ever be put to you. The answer differs sharply by path | | |

## 6 · Change management

| # | Type | What must be true | Why | Owner | State |
|---|---|---|---|---|---|
| 6.1 | Gate | **The schema is final** | A registered schema is append-only: no property's type, annotation or label can change. Correcting one means deleting the connection and every item in it | | |
| 6.2 | Gate | A **release process** exists for the connector itself | Which build is in production, how it is upgraded, and how it is rolled back | | |
| 6.3 | Check | Source schema changes have a **notification path** to whoever owns the connection | A renamed column silently empties a field. The database team rarely knows an index depends on them | | |
| 6.4 | Check | **Orphan reconciliation** is scheduled, if the path is direct push **without a state store** | With `Settings:StateConnectionString` set, the delete sweep is the reconciliation and this row is satisfied by 1.1. Without one the push never deletes, rows leaving scope keep their items indefinitely, and a periodic reconciliation is the only thing that finds them — note that `deploy/Compare-SourceToIndex.ps1` predates the inventory and still rebuilds its picture by re-reading the source | | |

## 7 · CDP only

| # | Type | What must be true | Why | Owner | State |
|---|---|---|---|---|---|
| 7.1 | Gate | **Ranger security zones**, tag-based policies, allow-exceptions and validity periods have been answered | The connector refuses to run against a zoned service rather than reading it zone-blind. The others are read as absent today, which grants more widely than intended | | |
| 7.2 | Gate | **Kerberos realm trust is durable**, and its expiry or renewal is understood | A trust that lapses takes the connector with it, and presents as a credential failure rather than as a trust problem | | |
| 7.3 | Check | The **policy count** reported by the connector matches Ranger Admin | Confirms the policy list is being read in full rather than truncated at a page boundary | | |
| 7.4 | Check | Behaviour on a **row-filtered or masked table** is understood and accepted | Its rows are never indexed; its catalogue entry is, for exactly the people granted select. That asymmetry is deliberate and worth stating to whoever signs off | | |

## 8 · SQL only

| # | Type | What must be true | Why | Owner | State |
|---|---|---|---|---|---|
| 8.1 | Gate | If agent-hosted: the **agent host has a patching owner** and is in the backup scope | It is a Windows server somebody must own. This is the cost this path pays for its capabilities | | |
| 8.2 | Gate | The **view is under change control** | It is the contract between the database and the index. An unreviewed edit to it is an unreviewed change to what Copilot can see | | |
| 8.3 | Check | The **soft-delete filter lives inside the view** | Not in a `WHERE` clause in the tool, where a later edit can quietly drop it | | |
| 8.4 | Check | The **deep-link URL template** resolves for a real record | Citations are how people verify an answer. A broken link is usually found after go-live | | |

---

## Sign-off

| Area | Name | Date |
|---|---|---|
| Connection owner (1.3) | | |
| Security — access model and staleness bound (1.2, 3.x) | | |
| Operations — scheduling, alerting, runbook (4.x, 5.x) | | |
| Data owner — scope and licensing (3.4) | | |

---

## The three that get skipped

**1.2, the ACL staleness bound.** It is the only control here that is a *number
somebody has to accept* rather than a task somebody has to do, which is why it
slides. A revocation at the source can take up to a week to reach the index at
the default cadence. Lower the number and pay in reads and writes; accept it and
write it down. Doing neither is the failure mode.

**5.2, logs having a reader.** On the direct-push path there is no admin-centre
health view. The log is not the backup signal — it is the only one.

**6.1, the schema being final.** Append-only is easy to nod at and expensive to
learn. The correction is deleting the connection and every item in it.
