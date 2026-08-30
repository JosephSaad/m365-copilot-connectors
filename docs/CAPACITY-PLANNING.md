---
title: Capacity planning
description: Will this still work at ten times the corpus? Graph's published ceilings, this rig's measured throughput, what scales linearly and what has stopped doing so, and the storage growth of ConnectorState.
---

# Capacity planning

The question this document exists to answer is **"will this still work at ten
times the corpus?"** — and to answer it with arithmetic somebody can check
rather than with reassurance.

It is written because section 7 of [`GO-LIVE-READINESS.md`](GO-LIVE-READINESS.md)
says, as Tier 2 item 13, that *"the 100-fold test proved 111,900 items; nobody
has written down Graph's per-connection ceiling against the corpus growth
forecast."* Both halves of that sentence turned out to be harder than they read.
The 111,900 is real and is the basis of everything below. **Graph's
per-connection ceiling, on the other hand, is not a published number at all** —
see section 2 — which is a finding rather than a gap in the research, and it
changes what a capacity plan for this product can honestly promise.

---

## 0. How to read the numbers in this document

Every figure is marked, once, and the marking is load-bearing:

**MEASURED** — observed on this rig, against the live `ConnectorState` database
and a real Microsoft 365 tenant. Reproducible. Where a figure comes from a
`crawl.` table, the query is given so it can be re-run against a different
estate and produce that estate's number instead.

**PUBLISHED** — stated by Microsoft in documentation, with the page named. Not
observed here. A published limit is a promise about the service, not an
observation of it, and Microsoft's own limits page carries the warning that
*"the specific limits described in this article are subject to change."*

**DERIVED** — arithmetic on top of one of the above. A derivation inherits the
weakness of its weakest input, and where the input is a single observation the
derivation says so.

The three are never mixed inside one sentence. If a number here is not marked,
that is a defect in this document.

---

## 1. The corpus this was measured on

**MEASURED.** One connection, `consultingwork`, holding 111,900 live items:

| Item type | Items |
|---|---|
| `Customer` | 1,200 |
| `Engagement` | 6,200 |
| `TimeEntry` | 104,500 |
| **Total** | **111,900** |

```sql
SELECT ItemType, COUNT(*) FROM crawl.Item GROUP BY ItemType;
```

**MEASURED.** The items are small, and the whole capacity story turns on that:

| Content bytes per item | Value |
|---|---|
| p50 | 491 |
| p95 | 660 |
| p99 | 767 |
| max | 904 |
| mean | 506 |

From `crawl.RunPhaseTiming` for the 100-fold run, phase `ContentBytes`:
`TotalMicroseconds` 56,633,584 over 111,900 samples. (The `ContentBytes` phase
records bytes in the microseconds column; the unit is named in `Unit`.)

**This is a small-item corpus and it is not representative of a document
source.** The published item ceiling is 30 MB of parsed text (section 2), and
the largest item here is 904 bytes — four and a half orders of magnitude below
it. Every throughput figure below is per *item*, and a corpus of PDFs will move
far fewer items per second while moving far more bytes. Where that changes a
conclusion, this document says so; where it does not, the reason is given.

---

## 2. What Microsoft publishes, and the number it does not

### 2.1 The published limits

**PUBLISHED**, from *Copilot connectors API limits*
(`learn.microsoft.com/graph/connecting-external-content-api-limits`):

| Limit | Value | What it constrains here |
|---|---|---|
| Properties definable in a connection schema | **128** | The hierarchy connector projects 30. No pressure at 10x; the schema does not grow with the corpus |
| Item size (request body when ingesting an item) | **30 MB** | Parsed text, not source bytes — Microsoft's note says that is typically 10% of the original file for docx/ppt/PDF. This rig's max item is 904 bytes |
| External groups per Microsoft 365 tenant | **100,000** | Only relevant if a source's ACLs are projected as *external* groups. This connector grants Entra group object IDs, so it consumes none of this quota |
| External groups per user, for a search query | **10,000** | As above |
| Group administration throttling threshold | **1,000 requests/sec** | Not used by this connector |
| Activities per `activities` call | **20** | Activities are not implemented — Tier 1 item 10 |

**PUBLISHED**, from *Microsoft Graph service-specific throttling limits*
(`learn.microsoft.com/graph/throttling-limits`), the one global figure that
applies to everything:

| Limit | Value |
|---|---|
| Any request type, per app across all tenants | **130,000 requests per 10 seconds** |

**DERIVED, and it is the one derivation in this document that is comfortable:**
the 100-fold run made 5,608 `$batch` requests over roughly an hour. That is
about 15.5 requests per 10 seconds against a global ceiling of 130,000. This
connector is four orders of magnitude away from the only published numeric
throttling limit that names it. Whatever produced the 429s in section 4, it was
not that.

### 2.2 The number Microsoft does not publish

**There is no published numeric ceiling on items per connection.** The limits
page has a "Item ingestion" section and it contains item *size*, not item
*count*.

What exists instead is a **licensed quota**, which is a different kind of thing
and has to be planned for differently. **PUBLISHED**, from the `connectionQuota`
resource type (`graph/api/resources/externalconnectors-connectionquota`, beta):

> `itemsRemaining` … min({*max capacity in the connection*} − {*number of items
> in the connection*}, {*tenant quota*} − {*number of items indexed in all
> connections*})

Three consequences, and each one changes a capacity plan:

1. **The ceiling is per tenant as well as per connection, and the tenant one is
   shared.** A second connector added next year eats the same allowance. A
   capacity plan for one connection that ignores the others is not a capacity
   plan.

2. **The number is not knowable from documentation — it has to be read from the
   tenant.** `GET /beta/external/connections/{id}/quota` returns
   `itemsRemaining`. That is a beta endpoint, and a capacity plan that depends
   on a beta endpoint should say so out loud, which this one is doing.

3. **You find out you were wrong by receiving an error, not by watching a
   gauge.** **PUBLISHED**, from *Monitor connector errors*: error **1008** is
   *"the total quota utilization of your tenant reached its limit"* and **1009**
   is the same for the connection. Both suggest deleting a connection or
   narrowing the ingestion filters.

**Not implemented here, and it should be.** Nothing in this repository reads
`connectionQuota`. The natural home is the health endpoint — a
`itemsRemaining` figure beside `minutesSinceLastSuccess` would turn "we are
approaching the quota" from a surprise into a trend. Until then, the operator
has to call the beta endpoint by hand, and this document is the only place that
says so.

### 2.3 The 25 concurrent operations, and an honest note about it

`PushEngine` clamps `Settings:Writers` to 16, and the reason it gives is that
*"the connectors API limits an application to 25 concurrent operations on a
connection"*. [`RUNBOOK.md`](RUNBOOK.md) section 3.2 repeats it. It is the
single most consequential number in this document, because it is what caps
per-connection throughput.

**It is not on the current published limits page.** The *Copilot connectors API
limits* article, fetched while writing this, carries schema, group, item-size
and activity limits and a prose section on throttling — and no concurrency
figure. So the honest marking for the 25 is: **cited by this repository, from
Microsoft guidance, and not verifiable against the current published limits
page.** It is treated below as a real constraint, because designing as if it
were absent would be reckless and because the clamp already exists in shipped
code — but a capacity plan that leant on it without saying this would be
passing off a secondhand number as a published one.

A nearby **PUBLISHED** figure, offered only because it is the right order of
magnitude and not because it is the same limit: the Confluence on-premises
connector setup page states that *"Copilot connectors throttle requests at a
rate of 25 requests per second."* That is a Microsoft-built connector's outbound
rate against Confluence, not Graph's inbound limit on us. Do not use it as
evidence for the 25. It is here so that nobody later finds it and thinks it was
missed.

---

## 3. The throughput model

One formula, three measured inputs, and it reproduces the observed run to within
two percent.

### 3.1 The formula

```
full-crawl write time  ~=  items_written  x  mean_in_flight_seconds  /  writers
```

**MEASURED**, all three inputs, from the 100-fold run (`crawl.Run` RunId 20 and
its `crawl.RunPhaseTiming` rows):

| Input | Value | Source |
|---|---|---|
| `items_written` | 110,590 | `crawl.Run.ItemsWritten` |
| `mean_in_flight_seconds` | 0.513 | `WriteInFlight` phase: 56,852,666,585 microseconds over 110,781 samples |
| `writers` | 16 | `Settings:Writers`, at the engine's clamp |

**DERIVED:** 110,590 x 0.513 / 16 = **3,546 seconds**.

**MEASURED:** the run took **3,611 seconds** wall clock — `StartedUtc`
2026-08-30 11:04:18.388, `CompletedUtc` 12:04:29.356. Section 5 of
[`GO-LIVE-READINESS.md`](GO-LIVE-READINESS.md) reports the same run as 60m33s at
~1,826 items/min, measured from the tool's own log, which starts earlier than
the store's run row. Both numbers are right about different clocks; this
document uses the store's, because that is the one a query can reproduce.

The model is 1.8% under the observation. That is close enough to plan with, and
the reason it is close is worth stating plainly.

### 3.2 The writers were saturated, and that is the whole finding

**DERIVED:** 56,853 writer-seconds of in-flight work, spread over 16 writers,
needs 3,553 seconds of wall clock if the writers never idle. The run took 3,611.
**The writer pool was busy 98.4% of the time.**

That single number carries most of this document's conclusions:

- **The run was Graph-round-trip-bound.** Not source-bound, not store-bound, not
  CPU-bound. The machine was largely idle. Adding cores, memory or disk to the
  push host buys nothing.
- **Throughput scales with writers, and only with writers**, right up to the
  clamp. This is why the clamp is the ceiling that matters and why section 2.3
  had to be careful about where the 25 comes from.
- **It is why the overlapped-store-reads optimisation was built, measured and
  deliberately not shipped** (section 4 of [`GO-LIVE-READINESS.md`](GO-LIVE-READINESS.md)):
  hiding store latency behind a pipeline cannot help a run in which the store is
  0.1% of per-row time.

**MEASURED**, the per-item in-flight distribution, which is what a p99 capacity
question needs:

| `WriteInFlight` | Seconds |
|---|---|
| p50 | 0.437 |
| p95 | 1.088 |
| p99 | 1.606 |
| max | 10.011 |
| mean | 0.513 |

The max is 10.011 seconds and the `Retry-After` on every 429 in that run was 10
seconds — the maximum is a throttled item's backoff, not a slow one's latency.
`WriteBackoff` totals 540 seconds across the run, 0.95% of the writer-seconds
spent.

### 3.3 The ceiling on one connection

**DERIVED**, from the model and the clamp:

| At | Items/second | Items/hour | Items/24h |
|---|---|---|---|
| 16 writers (the engine's clamp) | 31.2 | 112,300 | 2,695,000 |
| 25 writers (if the cited ceiling were usable) | 48.7 | 175,400 | 4,210,000 |

**Plan against 2.7 million items per connection per day**, and understand what
that number rests on: this corpus's 0.513-second mean round trip, on this
tenant, at this item size. It is one connection's number. It is not a Microsoft
commitment and it is not a promise about a document corpus.

The second row is deliberately included and deliberately not usable. Sixteen is
the number the engine will actually use, and raising `Settings:Writers` above it
produces a log line and no change.

### 3.4 The steady state is a completely different machine

**MEASURED.** An all-unchanged full crawl over the same 111,900 items:

| RunId | Seconds | Items read | Items written |
|---|---|---|---|
| 31 | 12.3 | 111,900 | 0 |
| 36 | 13.6 | 111,900 | 0 |
| 48 | 11.8 | 111,900 | 0 |
| 57 | 14.6 | 111,900 | 0 |

```sql
SELECT RunId, DATEDIFF(MILLISECOND, StartedUtc, CompletedUtc)/1000.0 AS Secs,
       ItemsRead, ItemsWritten
FROM   crawl.Run WHERE ItemsRead > 100000 ORDER BY RunId;
```

**DERIVED:** ~8,600 items per second read, hashed and compared — 275 times the
write rate. The two rates are that far apart because writing is a network round
trip to another continent and comparing is a hash against a row already in the
same datacentre.

**This is the fact that makes the whole design viable at scale**, and it should
be stated in one sentence: *the cost of a crawl is proportional to what
CHANGED, not to how big the corpus is.* A corpus ten times larger that changes
at the same absolute rate costs ten times more to read and exactly the same to
write.

**MEASURED**, where the 13 seconds actually goes, from run 49's phase rows:

| Phase | Total seconds over 111,900 items |
|---|---|
| `SourceRead` | 4.2 |
| `Prepare` (map + hash) | 3.3 |
| `WriteInFlight` | 0 |

Source read is a third of an unchanged crawl. At 10x it is the thing to watch,
and it is a SQL Server question rather than a Graph one — `sql/26`'s sargable
incremental view already cut base-table logical reads 42% on the changed-rows
path.

---

## 4. Throttling: one observation, treated as one observation

This is the only real evidence anybody has about what this tenant will tolerate,
and it is a single run. It is set out in full so that the next person can see
exactly how thin it is.

**MEASURED.** Across every run ever recorded on this rig, `crawl.ThrottleEvent`
holds:

| Status | Endpoint | `Retry-After` | Events |
|---|---|---|---|
| 429 | `batch` | 10 | 191 |
| 504 | `batch` | (null) | 1 |

```sql
SELECT StatusCode, Endpoint, RetryAfterSeconds, COUNT(*)
FROM   crawl.ThrottleEvent GROUP BY StatusCode, Endpoint, RetryAfterSeconds;
```

All 191 belong to run 20, the 100-fold write run. **Every run before it and
every run after it took zero.** One data point.

**MEASURED**, the distribution within that run, by whole minute from
`StartedUtc`:

| Minute of the run | 429s |
|---|---|
| 0 | 42 |
| 12 | 32 |
| 13 | 71 |
| 19 | 41 |
| 59 | 5 |

```sql
WITH e AS (SELECT DATEDIFF(MINUTE,
             (SELECT StartedUtc FROM crawl.Run WHERE RunId = 20),
             OccurredUtc) AS m
           FROM crawl.ThrottleEvent WHERE RunId = 20)
SELECT m, COUNT(*) FROM e GROUP BY m ORDER BY m;
```

Section 5 of [`GO-LIVE-READINESS.md`](GO-LIVE-READINESS.md) describes these as
arriving *"in three short clusters rather than steadily — minutes 0, 12 and 19
of sixty, the other fifty-seven clean."* The query above refines that without
contradicting it: the clusters are at minutes **0, 12–13, 19 and 59**, the
minute-13 cluster is the largest of the four at 71 events, and **fifty-five**
minutes of the sixty were clean. The shape of the finding — bursts, not a
steady rate — is unchanged and is the part that matters.

**What one observation supports:**

- Throttling here is **bursty**. Ninety-two percent of the run saw none at all.
  A capacity plan built on an average 429 rate would be describing a phenomenon
  that did not happen.
- The service asked for **10 seconds** every time, and the engine-owned backoff
  honoured every one. Backoff cost 540 seconds of 56,853 writer-seconds.
- **The tenant pushed back at ~1,838 items/minute of sustained writes, at 16
  writers, on a corpus of ~500-byte items.** That is the only rate anyone has
  ever provoked a 429 at.

**What one observation does not support, and treating it as if it did is the
mistake this section exists to prevent:**

- It is **not** a throttling threshold. Nothing here identifies which limit was
  hit, whether it was per-app, per-tenant or per-connection, or what the
  window was. Microsoft's own guidance is that a request is evaluated against
  several limits at once and *"the first limit to be reached triggers throttling
  behavior."*
- It is **not** a promise that 1,838/minute is safe. The same rate on a busier
  tenant, or with another connector running beside it, may throttle at minute 3
  instead of minute 12.
- It is **not** evidence that a lower rate is safe either. Zero 429s in
  twenty-three other runs is mostly evidence that those runs were small.

**The right response to all of that is not a better number; it is
serialisation.** If you cannot know the threshold, do not spend the budget in
parallel. That is the argument [`SCHEDULING.md`](SCHEDULING.md) is built on, and
it is why `deploy/Schedule-Connectors.ps1` runs one crawl at a time.

**The one thing to fix before the next scale test**, because it is cheap: run 20
throttled *and lost 191 items to a serialization defect in the retry*. That
defect is fixed. But the run reported `partial` and the operator found out from
a dashboard, not from a page. Route the throttling signal — Tier 1 item 8.

---

## 5. Storage: `ConnectorState` growth per item and per run

Measured directly against the live database. Reproduce with:

```sql
SELECT t.name,
       MAX(p.rows)             AS Rws,
       SUM(a.total_pages) * 8  AS TotalKB,
       SUM(a.used_pages)  * 8  AS UsedKB
FROM   sys.tables t
       JOIN sys.indexes    i ON t.object_id = i.object_id
       JOIN sys.partitions p ON i.object_id = p.object_id AND i.index_id = p.index_id
       JOIN sys.allocation_units a ON p.partition_id = a.container_id
GROUP BY t.name ORDER BY TotalKB DESC;
```

### 5.1 What is actually there

**MEASURED**, at 111,900 items and 49 runs:

| Table | Rows | Allocated KB | Used KB |
|---|---|---|---|
| `crawl.Item` | 111,900 | 53,400 | 48,608 |
| `crawl.Run` | 49 | 216 | 64 |
| `crawl.ThrottleEvent` | 192 | 144 | 64 |
| `crawl.RunPhaseTiming` | 343 | 72 | 64 |
| `crawl.RunItemType` | 112 | 72 | 32 |
| `crawl.PrincipalMap` | 0 | 144 | 32 |
| `crawl.Checkpoint` | 1 | 72 | 16 |
| `crawl.Connection` | 1 | 72 | 16 |

**`crawl.Item` is 99.7% of the used space.** Everything else, together, is under
300 KB after forty-nine runs including a 111,900-item bootstrap. That ratio is
the storage story and it does not change with scale, because the small tables
grow with *runs* and `crawl.Item` grows with the *corpus*.

### 5.2 Per item

**DERIVED** from the table above: 48,608 KB used / 111,900 items =
**445 bytes per item used, 489 bytes allocated**, across the clustered index and
both non-clustered ones.

**MEASURED**, the same figure decomposed, from
`sys.dm_db_index_physical_stats`:

| Index | Records | Avg record bytes |
|---|---|---|
| `PK_Item` (clustered) | 111,900 | 215.5 |
| `IX_Item_Sweep` | 111,900 | 101.5 |
| `IX_Item_NotLive` | 0 | — |

`IX_Item_NotLive` is filtered on `State <> 1`, so on a healthy corpus it is
empty and costs one page. **A sweep that leaves items pending is what makes it
grow, which means a growing `IX_Item_NotLive` is a health signal, not a capacity
one.**

**The caveat that will bite somebody else's corpus.** `ItemId` is
`NVARCHAR(128)` and this rig's identifiers average **10.7 characters** and top
out at 11 — `cust1`, `time5003`.

```sql
SELECT AVG(CAST(LEN(ItemId) AS FLOAT)), MAX(LEN(ItemId)) FROM crawl.Item;
```

A source with 100-character identifiers — a URL, a GUID pair, a path — adds
about 180 bytes to the clustered record and again to `IX_Item_Sweep`, taking the
per-item figure from 445 bytes to roughly 800. **Rule of thumb: 0.5 KB per item
with short identifiers, 1 KB with long ones.** Both are cheap; neither is zero.

### 5.3 Per run of history

**MEASURED**, average record sizes, and **DERIVED**, the per-run total:

| Table | Rows per run (measured) | Avg record bytes (measured) | Bytes per run |
|---|---|---|---|
| `crawl.Run` | 1 | 284.9 + 82.0 index | 367 |
| `crawl.RunPhaseTiming` | 7 | 125.4 | 878 |
| `crawl.RunItemType` | 2.3 (3 for this connector) | 84.0 | 252 |
| **Total** | | | **~1.5 KB** |

Plus **111 bytes** per throttle event (70 in the clustered index, 41 in
`IX_ThrottleEvent_Run`), which on a healthy connection is zero.

**DERIVED:** an hourly crawl retained for the 90 days `sql/27` sets is 2,160
runs, which is **3.2 MB**. A daily one is 135 KB.

**Run history is not a capacity problem and never will be.** It is worth saying
because "add per-run telemetry" is the kind of proposal that gets refused on
storage grounds by reflex, and the measurement says the reflex is wrong here by
three orders of magnitude.

### 5.4 What bounds the unbounded parts: `sql/27`

Two things in this database would otherwise grow without limit: run history, and
tombstones — `crawl.Item` rows in state 3, which are kept deliberately so that
an audit of a deletion has a row to look at.

`sql/27` ships the SQL Agent job that bounds both. **MEASURED**, from the script:

| Setting | Value |
|---|---|
| Schedule | Weekly, **Sunday 03:00** |
| `@KeepRunDays` | 90 |
| `@KeepTombstoneDays` | 180 |
| `@KeepExpiredPrincipalDays` | 30 |

It iterates every row of `crawl.Connection` and calls `crawl.uspPurgeHistory`
for each, so adding a connection needs no change to the job.

Three things about it that belong in a capacity plan rather than a deployment
one:

1. **It has to be deployed, and it cannot be deployed everywhere.** `sql/27`
   refuses distinctly when SQL Agent is not running, and **SQL Server Express
   has no Agent at all**. On Express, retention has to be scheduled by whatever
   else the estate runs — the same problem `deploy/Test-TriggerHealth.ps1`
   solves for the trigger check. Without it the database simply grows, and
   nothing throws.

2. **It does not survive a failover on its own.** SQL Agent jobs live in `msdb`,
   which does not fail over with an Availability Group. Tier 1 item 6 of
   [`GO-LIVE-READINESS.md`](GO-LIVE-READINESS.md) already records that `sql/27`
   must be deployed on every replica and gated on
   `sys.fn_hadr_is_primary_replica`. A retention job that silently stopped at
   the first failover is a capacity problem that presents, eventually, as a full
   disk.

3. **Its window collides with a nightly crawl window if you let it.** Sunday
   03:00 is inside the 01:00–06:00 default window
   [`SCHEDULING.md`](SCHEDULING.md) suggests. The purge is small — it is deleting
   rows from tables measured in hundreds of kilobytes — so this is a note rather
   than a warning, and `sql/27`'s own header makes the point that matters:
   *"Move the schedule before you move the crawl."*

### 5.5 Log and recovery model

**MEASURED**, from `sys.database_files`:

| File | Allocated KB | Used KB |
|---|---|---|
| Data | 262,144 | 60,224 |
| Log | 393,216 | 223,968 |

**The log file is larger than the data file**, and the database is in **SIMPLE**
recovery. That combination is what a bootstrap looks like: 111,900 rows inserted
in one campaign of transactions grew the log to 384 MB, and SIMPLE recovery
truncates at checkpoint but does not shrink the file back.

Two planning consequences:

- **Size the log for the bootstrap, not for the steady state.** At 10x, plan for
  a log several times this. A steady-state crawl writes almost nothing — the
  all-unchanged runs in section 3.4 write zero items and touch `LastSeenRunId`
  through one server-side statement per window.
- **SIMPLE recovery is the right model here and the DR plan already says why.**
  Tier 0 item 3 of [`GO-LIVE-READINESS.md`](GO-LIVE-READINESS.md) settles that
  losing a day of this database costs one full crawl rather than data, so a daily
  backup with RPO 24 hours is defensible. Point-in-time recovery would buy
  nothing and cost a log backup chain.

---

## 6. What scales, what does not, and what stopped scaling the way it did

### 6.1 Linear in the corpus

| Thing | Constant | At 111,900 (measured) | At 1,119,000 (derived) |
|---|---|---|---|
| `crawl.Item` used space | ~445 B/item | 47 MB | **475 MB** |
| Source read | ~4.2 s per 111,900 | 4.2 s | ~42 s |
| Hash and compare (`Prepare`) | ~3.3 s per 111,900 | 3.3 s | ~33 s |
| Store lookups (`uspGetItemState`) | `ceil(N / 200)` | 560 | 5,595 |
| Reconciliation Graph GETs | 1 per source row | 111,900 | 1,119,000 |

### 6.2 Linear in what CHANGED, not in the corpus

| Thing | Constant |
|---|---|
| Graph writes | `items_written x 0.513 s / writers` |
| Checkpoint saves | one per write chunk of 20 — so `writes / 20`, not `corpus / 20` |
| `crawl.Run` history | one row per run |

**MEASURED**, and it is the sharpest illustration of the distinction: a
bootstrap of 111,900 items makes ~5,595 checkpoint round trips; a steady-state
run over the same corpus makes about one.

### 6.3 Fixed, whatever the corpus does

| Thing | Value |
|---|---|
| Writers per connection | 16 (engine clamp), against a cited 25 |
| `$batch` sub-requests per request | 20 (Graph's hard ceiling) |
| Schema properties | 128 published; 30 used |

### 6.4 The state store stopped scaling the way it did, and here is why

This is the section Tier 2 item 13 was really asking for, because the honest
answer to "does the store scale" changed twice in one release.

**It used to be `corpus / 20`, twice over.** The write chunk and the state-store
round-trip window were one constant, `20`, chosen because that is Graph's
`$batch` ceiling. So a 111,900-row crawl made **5,595** `uspGetItemState`
lookups and **5,595** `uspRecordUnchanged` calls — 6,155 round trips in total
with the sweep and lifecycle calls. The store was paying Graph's limit for no
reason: SQL Server is perfectly willing to answer about two hundred items at
once.

**Two changes, both measured, took it to 560.**

1. **The lookup window was decoupled from the write chunk.**
   `Settings:LookupChunkSize` defaults to 200 and the write chunk stayed at
   Graph's 20. **MEASURED:** `uspGetItemState` went from 5,595 calls to **560**
   for the same 111,900 rows. The first attempt used one number for both units
   and starved the writer pool — 200 rows to one writer, fifteen idle — which
   the concurrency tests caught.

2. **`sql/41`'s `uspCompareAndSee` folded the recording into the compare.** It
   compares hashes where the data already is, returns only what must be written,
   and marks the rest seen in the same statement. **MEASURED:**
   `uspRecordUnchanged` went from 5,595 calls to **zero**.

**So the store cost per crawl fell about elevenfold at constant corpus.** It is
still O(N) — `ceil(N / 200)` lookups — but the constant is 200x smaller than the
per-item shape it started from, and the payload shrank too: a steady-state
window now returns a handful of identifiers instead of two hundred rows.

**The consequence for planning, stated as the thing to remember:** *the store
was the reason the pipeline used to be chatty, and it is not any more.* At 10x
the corpus, an all-unchanged crawl makes 5,595 store round trips, which at this
rig's rates is a few seconds. The bottleneck is Graph and it stays Graph.

That is also why the overlapped-store-reads prefetch was measured at
13/13/13 seconds without and 12/14/14 with, and shelved. There was nothing left
to hide.

---

## 7. The 10x answer

Ten times this corpus is **1,119,000 items**. Taking each question in turn.

### 7.1 Will it fit?

**Storage: yes, comfortably.** ~475 MB in `crawl.Item` (DERIVED from section
5.2), plus a log sized for the bootstrap. Nothing here is a capacity risk.

**Graph quota: unknown, and it is the first thing to check.** There is no
published item ceiling (section 2.2). Call
`GET /beta/external/connections/{id}/quota` and read `itemsRemaining` **before**
committing to a 10x source, because the failure mode is error 1009 partway
through a ten-hour bootstrap.

### 7.2 Will the bootstrap complete?

**DERIVED:** 1,119,000 items x 0.513 s / 16 writers = **35,900 seconds = 9 hours
58 minutes.**

**No nightly window holds that.** This is the single hardest fact in the
document, and there are exactly three responses:

1. **Bootstrap outside the window, once, deliberately** — a weekend, with the
   queue's other entries disabled. The run is resumable: `crawl.Checkpoint`
   holds a composite `(marker, id)` marker and a killed run resumes from it
   rather than restarting. **MEASURED**, at the ~5,595 checkpoint saves a
   bootstrap makes, the most a crash costs is one write chunk of 20.
2. **Split the source across connections.** Each connection gets its own 16
   writers, so two connections is genuinely twice the throughput — and it is
   also twice the concurrent operations against the app's budget, which is
   exactly the arithmetic `deploy/Schedule-Connectors.ps1` prints and refuses to
   exceed. This trades the one ceiling you cannot raise for the one you have to
   manage.
3. **Do not bootstrap.** If the corpus grows to 10x over a year rather than
   arriving at 10x, no bootstrap ever happens — each night writes a day's
   changes and the steady state below applies throughout. **This is the normal
   case and it is worth checking before planning for the hard one.**

### 7.3 Will the steady state fit?

**Yes, with room.** **DERIVED** from section 3.4: an all-unchanged crawl of
1,119,000 items is ~130 seconds of read-and-compare, plus
`items_changed x 0.513 / 16` seconds of writes.

**DERIVED**, what a nightly window buys at 10x:

| Nightly window | Items that can be written |
|---|---|
| 1 hour | ~112,000 |
| 4 hours | ~449,000 |
| 5 hours (the 01:00–06:00 default) | ~561,000 |

**So the question to ask the source team is not "how big is it" but "how many
items change per day".** At 10x, a five-hour window absorbs a corpus with up to
**50% daily churn**. Almost no real source moves like that. If the answer is
under ~500,000 changed items a night, the current design carries 10x with the
existing schedule and no code changes.

### 7.4 Will the safeguards still work?

**The delete sweep: yes, and it got cheaper.** **MEASURED:** 25 deletions went
in **2 round trips** (20 + 5) rather than 25 calls. The sweep's diff happens
server-side.

But **check `Settings:MaxDeletePercent` at every scale change**, because the
guard is a *percentage* and the corpus is the denominator.
[`CRAWL-STATE-DEPLOYMENT.md`](CRAWL-STATE-DEPLOYMENT.md) section 12 carries the
measurement query. At 1,118 items one deletion was already 0.09%; at 1,119,000
the default 10% guard permits **111,900 deletions in one sweep** before it
refuses. The guard did not get weaker — the number it protects got bigger, and
whoever accepted 10% accepted a different absolute number than they will be
living with.

**The reconciliation: no, not as it stands.** This is the honest bad news.
`deploy/Compare-SourceToIndex.ps1` costs **one Graph GET per source row**,
because there is no list-items API. **DERIVED:** 1,119,000 GETs at even 20 per
second is over fifteen hours, and it spends the same per-application Graph
budget the crawls need.

Three responses, in the order to try them:

1. **Sample rather than sweep.** `deploy/Invoke-Reconciliation.ps1` takes
   `-MaxItems`, and `-AllowTruncated` makes a capped pass a verdict instead of a
   blind one. A weekly 10,000-item sample of a 1,119,000-item corpus is a real
   check with a stated coverage, and the wrapper's summary records what the
   coverage was. **It will not be told to report "clean" over rows it did not
   look at** — that refusal is why `-AllowTruncated` has to be typed.
2. **Lean on the inventory instead.** The most valuable thing the comparison
   finds — an item live in `crawl.vwItemInventory` whose row the source has
   forgotten — is found by a SQL query against the source and the store, with no
   Graph call at all. That half scales fine and could be run in full weekly. It
   is not separable today; it should be.
3. **Accept that a full reconciliation is a quarterly exercise at 10x**, planned
   like a bootstrap, not a weekly job.

**And the limit that outranks all three:** the comparison reads `dbo.Tickets`
and builds identifiers as `ticket<n>`. **It does not cover the hierarchy
connector or the CDP connectors at all.** At any scale, the reconciliation
covers one connector. `deploy/Invoke-Reconciliation.ps1` refuses to run against
a connection registered under a different `ConnectorKey` precisely so this
cannot be discovered later, from a weekly alert that has been reporting every
row MISSING since the day it was scheduled.

---

## 8. What to measure on a real estate, and where the numbers come from

None of the figures above transfer. They are one rig, one tenant, one corpus of
500-byte rows. What transfers is the **method**: five queries, run once a
quarter, that produce that estate's own version of this document.

**1. Corpus size and shape.**

```sql
SELECT ConnectionId, ItemType, COUNT(*) AS Items
FROM   crawl.Item WHERE State = 1
GROUP BY ConnectionId, ItemType ORDER BY ConnectionId, Items DESC;
```

**2. Item size distribution** — the input to every throughput derivation.

```sql
SELECT r.RunId, t.P50Microseconds AS P50Bytes, t.P95Microseconds AS P95Bytes,
       t.MaxMicroseconds AS MaxBytes, t.SampleCount
FROM   crawl.RunPhaseTiming t JOIN crawl.Run r ON r.RunId = t.RunId
WHERE  t.Phase = 'ContentBytes' ORDER BY r.RunId DESC;
```

**3. Mean in-flight seconds and writer utilisation** — the model's two inputs.

```sql
SELECT r.RunId,
       t.TotalMicroseconds / 1000000.0                     AS WriterSeconds,
       t.TotalMicroseconds / NULLIF(t.SampleCount, 0) / 1000000.0 AS MeanSecondsPerItem,
       DATEDIFF(SECOND, r.StartedUtc, r.CompletedUtc)       AS WallClockSeconds
FROM   crawl.RunPhaseTiming t JOIN crawl.Run r ON r.RunId = t.RunId
WHERE  t.Phase = 'WriteInFlight' AND t.SampleCount > 0
ORDER BY r.RunId DESC;
```

Divide `WriterSeconds` by `Settings:Writers` and compare with
`WallClockSeconds`. Close together means writer-bound and the model applies.
Far apart means something else is the bottleneck, and the other phase rows say
which.

**4. Throttling, if any.**

```sql
SELECT r.RunId, COUNT(*) AS Events, MIN(e.RetryAfterSeconds) AS MinRetryAfter,
       MAX(e.RetryAfterSeconds) AS MaxRetryAfter
FROM   crawl.ThrottleEvent e JOIN crawl.Run r ON r.RunId = e.RunId
GROUP BY r.RunId ORDER BY r.RunId DESC;
```

**5. Storage per item** — section 5's query, divided by the item count from (1).

**And one that is not SQL:** `GET /beta/external/connections/{id}/quota`, for
`itemsRemaining`. It is the only one of the six that reports a ceiling rather
than a usage, and it is the one nothing in this repository automates.

---

## 9. The summary, for somebody who read only this

- **Plan against 2.7 million items per connection per day** (DERIVED from a
  measured 0.513-second mean round trip at 16 writers). It is the ceiling that
  matters and the only one you cannot buy your way out of.
- **The steady state is 275x faster than the bootstrap** (MEASURED, ~8,600
  items/second compared, 31 written). Crawl cost tracks change, not corpus.
- **Storage is ~0.5 KB per item** and run history is negligible (MEASURED).
  `sql/27` bounds the parts that would otherwise grow, and it has to actually be
  deployed, on every replica.
- **The store stopped being the bottleneck** — 6,155 round trips became 560
  (MEASURED). Do not optimise it further without measuring first; the last
  attempt made no difference and was shelved.
- **Graph's per-connection item ceiling is not published.** It is a licensed
  quota, readable only from `connectionQuota`, shared with every other
  connection in the tenant, and nothing here watches it.
- **The throttling evidence is one run.** Treat it as one run. Serialise the
  crawls instead of budgeting against a threshold nobody has measured —
  [`SCHEDULING.md`](SCHEDULING.md).
- **The reconciliation does not scale to 10x** and covers one connector. Sample
  it, and say what the coverage was.
